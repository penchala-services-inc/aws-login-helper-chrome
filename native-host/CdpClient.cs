using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Talks to a Chrome instance's remote debugging (Chrome DevTools Protocol) endpoint over
    /// plain HTTP (for the /json/* discovery endpoints) and a per-tab WebSocket (for actually
    /// issuing Network.setCookie / Page.navigate commands).
    ///
    /// This is what stands in for Firefox's browser.cookies.set({storeId: ...}) - Chrome has
    /// no concept of "set this cookie in this other profile" from an extension, but CDP lets a
    /// separate trusted process (this native host) do exactly that for any tab it can see.
    /// </summary>
    internal sealed class CdpClient
    {
        // UseProxy = false is deliberate: HttpClient otherwise respects the system's configured
        // proxy for every request by default, including these to 127.0.0.1. On a machine with
        // a corporate proxy configured, that meant every check against Chrome's local debugging
        // port was round-tripping through the proxy before failing/succeeding instead of getting
        // an immediate local answer - measured at ~2 full seconds for what should be a
        // near-instant loopback check.
        private readonly HttpClient _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        public async Task<bool> IsAliveAsync(int port)
        {
            try
            {
                var response = await _http.GetAsync($"http://127.0.0.1:{port}/json/version");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task WaitUntilAliveAsync(int port, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await IsAliveAsync(port)) return;
                await Task.Delay(250);
            }
            throw new TimeoutException($"Chrome DevTools endpoint on port {port} never became reachable.");
        }

        // Returns (id, url) for every current "page" target (i.e. tab), as reported by the
        // /json/list HTTP endpoint.
        public async Task<List<(string Id, string Url)>> ListPageTargetsAsync(int port)
        {
            var response = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/list");
            var array = JArray.Parse(response);
            var result = new List<(string Id, string Url)>();
            foreach (var item in array)
            {
                var type = (string)item["type"];
                if (type != "page") continue;
                result.Add(((string)item["id"], (string)item["url"]));
            }
            return result;
        }

        public async Task<string> GetWebSocketUrlAsync(int port, string targetId)
        {
            var response = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/list");
            var array = JArray.Parse(response);
            var match = array.FirstOrDefault(t => (string)t["id"] == targetId);
            if (match == null) throw new InvalidOperationException($"Target {targetId} not found.");
            return (string)match["webSocketDebuggerUrl"];
        }

        public async Task CloseTargetAsync(int port, string targetId)
        {
            try
            {
                await _http.GetAsync($"http://127.0.0.1:{port}/json/close/{targetId}");
            }
            catch
            {
                // best-effort cleanup only
            }
        }

        /// <summary>
        /// Waits for a "page" target to appear that was not present in <paramref name="before"/>.
        /// Used after asking an already-running dedicated Chrome instance to open a new profile
        /// window (the launch request is singleton-forwarded, so we can't get the target id
        /// directly from our own process launch - we have to notice it show up).
        /// </summary>
        public async Task<string> WaitForNewTargetAsync(int port, HashSet<string> before, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var targets = await ListPageTargetsAsync(port);
                var fresh = targets.FirstOrDefault(t => !before.Contains(t.Id));
                if (fresh.Id != null) return fresh.Id;
                await Task.Delay(200);
            }
            throw new TimeoutException("Timed out waiting for the new Chrome tab/window to appear.");
        }

        /// <summary>
        /// Connects to a tab's own WebSocket debugger URL, sets the given cookies into that
        /// tab's cookie jar (i.e. into whichever Chrome profile that tab belongs to), then
        /// navigates the tab to <paramref name="finalUrl"/>.
        ///
        /// When <paramref name="consoleBannerLabel"/> is given, also injects a script (via
        /// Page.addScriptToEvaluateOnNewDocument) that - only once the page is actually on
        /// *.console.aws.amazon.com - pins a full labeled bar to the bottom of the viewport,
        /// and prefixes just <paramref name="titleLabel"/> (falling back to the full banner
        /// label if not given) onto the tab/window title. Scoped to console pages specifically
        /// so it never runs during the awsapps.com SSO redirect hops.
        ///
        /// HISTORY / WHY THIS MECHANISM AND NOT SOMETHING ELSE: this is the third design tried
        /// for identifying a window's account/role, having come full circle back to the first.
        /// Design 2, the dedicated Chrome profile's own display name (ChromeManager.
        /// ApplyKnownProfileDisplayNames) alone, was too subtle - Chrome's toolbar shows only
        /// an avatar icon by default, no visible text. Design 3, a real loaded content-script
        /// extension via --load-extension, worked functionally but depended on a command-line
        /// switch Google removed ENTIRELY starting in Chrome 137 (June 2025) - confirmed dead
        /// via real testing (chrome-extension://<id>/... came back "blocked" even from a
        /// genuinely fresh --user-data-dir, ruling out a forwarding-timing bug). This CDP
        /// mechanism has its own well-documented fragility from BEFORE design 2/3 replaced it,
        /// both now addressed here:
        ///   - The banner/title only showing on the FIRST page load and not later navigations,
        ///     traced to two distinct causes: (a) AWS's federation link bounces through several
        ///     intermediate redirects before actually landing on console.aws.amazon.com, and
        ///     Page.addScriptToEvaluateOnNewDocument's registration lives with the debugger
        ///     SESSION - disconnecting while those hops were still in flight wiped the
        ///     registration out before the real console page ever loaded. Fixed by
        ///     WaitForConsoleNavigationAsync below, which watches Page.frameNavigated and only
        ///     disconnects once the console hostname has actually settled (no further top-level
        ///     hop for a short quiet period), not on the first (possibly intermediate) match.
        ///     (b) Chrome's MV3 service worker (and therefore this host process, launched by
        ///     it) can be recycled by Chrome mid-wait; Program.cs's native messaging loop now
        ///     stays attached and handles many requests over one long-lived connectNative port
        ///     instead of exiting after one, which keeps this process (and therefore this CDP
        ///     session) alive through that wait far more reliably than before.
        ///   - AWS's own "you've been signed out" screen, traced to keeping the Page domain
        ///     enabled (needed to receive the Page.frameNavigated events (a) requires) for
        ///     longer than this brief settle-and-detach window - Chrome disables a tab's
        ///     back/forward cache for as long as its Page domain instrumentation is active, and
        ///     AWS's federation flow doesn't tolerate that. This method calls Page.disable
        ///     (undoing (a)'s Page.enable) as soon as the console navigation has settled, ALWAYS
        ///     before closing the WebSocket - restoring bfcache eligibility before this session
        ///     goes away, rather than leaving Page enabled for however much longer the tab
        ///     happens to stay open.
        ///   - The banner/title STILL disappearing on LATER navigations, once real-world use
        ///     showed the assumption behind the fix above didn't hold: AWS's unified console is
        ///     not one single-document SPA - many in-console moves (switching services, regions,
        ///     etc.) are genuine full top-level navigations, not client-side route changes, so
        ///     the in-page MutationObservers alone were nowhere near enough. Fixed by
        ///     StartBannerWatcher below, called by the caller right after this method returns -
        ///     see its own doc comment for why that's a polling design rather than a second,
        ///     longer-lived CDP attachment (that shape already failed once, above).
        /// </summary>
        public async Task SetCookiesAndNavigateAsync(string webSocketDebuggerUrl, List<CookieDto> cookies, string finalUrl, string consoleBannerLabel = null, string titleLabel = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var ws = new ClientWebSocket())
            {
                ws.Options.Proxy = null; // same reasoning as the HttpClient handler above
                await ws.ConnectAsync(new Uri(webSocketDebuggerUrl), CancellationToken.None);
                Logger.Log($"SetCookiesAndNavigateAsync: ws.ConnectAsync ({sw.ElapsedMilliseconds}ms)");

                int nextId = 1;

                // --start-maximized on the launch command line turned out NOT to reliably
                // apply to a new profile-directory window opened by forwarding args into an
                // already-running Chrome instance (the "singleton-forwarded" path used for
                // every account/role after the very first one) - real-world testing showed
                // windows still opening at Chrome's default size. Setting the window state
                // directly through CDP, which talks to the actual window regardless of how it
                // was launched, is authoritative instead of relying on a command-line flag
                // Chrome may or may not re-read for a forwarded window.
                sw.Restart();
                await MaximizeWindowAsync(ws, nextId);
                nextId += 2;
                Logger.Log($"SetCookiesAndNavigateAsync: MaximizeWindowAsync ({sw.ElapsedMilliseconds}ms)");

                if (!string.IsNullOrEmpty(consoleBannerLabel))
                {
                    // Page.frameNavigated events (needed below to know when we've actually
                    // reached console.aws.amazon.com, past all of AWS's SSO redirect hops)
                    // only fire once the Page domain is enabled on this session. Turned back
                    // off again, further down, as soon as we no longer need those events - see
                    // this method's own doc comment for why that matters.
                    sw.Restart();
                    await SendAndAwaitAsync(ws, nextId++, "Page.enable", new JObject());
                    Logger.Log($"SetCookiesAndNavigateAsync: Page.enable ({sw.ElapsedMilliseconds}ms)");
                }

                // Show something immediately instead of leaving the window on a blank white
                // about:blank tab while cookies get set. A data: URL involves zero network
                // I/O - it renders as soon as Chrome parses the string, so this costs
                // essentially nothing but gives the user instant visual confirmation that
                // something is happening, before we swap in the real destination below.
                if (!string.IsNullOrEmpty(consoleBannerLabel))
                {
                    sw.Restart();
                    string loadingUrl = BuildLoadingPageDataUrl(consoleBannerLabel);
                    await SendAndAwaitAsync(ws, nextId++, "Page.navigate", new JObject { ["url"] = loadingUrl });
                    Logger.Log($"SetCookiesAndNavigateAsync: Page.navigate (loading placeholder) ({sw.ElapsedMilliseconds}ms)");
                }

                // Network.setCookie/getCookies do NOT require Network.enable - that call
                // only turns on the full request/response EVENT STREAM for the page, which we
                // never use here. Calling it anyway meant every open paid for Chrome starting
                // to stream Network.* events for the whole subsequent page load down this same
                // WebSocket, which SendAndAwaitAsync then had to read past while hunting for
                // each actual command response. Dropping it is a straightforward win.

                foreach (var cookie in cookies)
                {
                    var parms = new JObject
                    {
                        ["name"] = cookie.Name,
                        ["value"] = cookie.Value,
                        ["domain"] = cookie.Domain,
                        ["path"] = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                        ["secure"] = cookie.Secure,
                        ["httpOnly"] = cookie.HttpOnly,
                    };

                    if (!string.IsNullOrEmpty(cookie.SameSite))
                    {
                        // Chrome cookies API values are "no_restriction"/"lax"/"strict"; CDP wants
                        // "None"/"Lax"/"Strict".
                        parms["sameSite"] = cookie.SameSite.Equals("no_restriction", StringComparison.OrdinalIgnoreCase)
                            ? "None"
                            : char.ToUpperInvariant(cookie.SameSite[0]) + cookie.SameSite.Substring(1);
                    }

                    if (cookie.Session != true && cookie.ExpirationDate.HasValue)
                    {
                        parms["expires"] = cookie.ExpirationDate.Value;
                    }

                    sw.Restart();
                    await SendAndAwaitAsync(ws, nextId++, "Network.setCookie", parms);
                    Logger.Log($"SetCookiesAndNavigateAsync: Network.setCookie [{cookie.Name}] ({sw.ElapsedMilliseconds}ms)");
                }

                if (!string.IsNullOrEmpty(consoleBannerLabel))
                {
                    sw.Restart();
                    string script = BuildConsoleBannerScript(consoleBannerLabel, string.IsNullOrEmpty(titleLabel) ? consoleBannerLabel : titleLabel);
                    await SendAndAwaitAsync(ws, nextId++, "Page.addScriptToEvaluateOnNewDocument", new JObject { ["source"] = script });
                    Logger.Log($"SetCookiesAndNavigateAsync: Page.addScriptToEvaluateOnNewDocument ({sw.ElapsedMilliseconds}ms)");
                }

                sw.Restart();
                await SendAndAwaitAsync(ws, nextId++, "Page.navigate", new JObject { ["url"] = finalUrl });
                Logger.Log($"SetCookiesAndNavigateAsync: Page.navigate ({sw.ElapsedMilliseconds}ms)");

                if (!string.IsNullOrEmpty(consoleBannerLabel))
                {
                    // AWS's federation link bounces through several intermediate redirects
                    // before actually landing on console.aws.amazon.com. Page.
                    // addScriptToEvaluateOnNewDocument's registration lives with THIS
                    // debugger session - closing our WebSocket (which we're about to do,
                    // right before this process exits) detaches the session and wipes that
                    // registration out along with it. Detaching while those redirects are
                    // still in flight meant the script was already gone by the time the real
                    // console page loaded, which is exactly why the title/banner never
                    // showed up despite the hostname check itself being correct. So: stay
                    // attached and actually wait to observe a frame navigation that lands on
                    // a real console.aws.amazon.com hostname before disconnecting.
                    //
                    // Non-fatal, same reasoning as the session-expiry refresh and Page.disable
                    // right below: real host.log data (from the Edge build, but this code is
                    // identical here) confirmed the real Page.navigate a few lines above this
                    // ALREADY succeeded (it's just slow - AWS's federation redirect chain has
                    // been observed taking 9+ seconds) before this wait's own WebSocket gets
                    // closed out from under it. So by this point the sign-in itself already
                    // happened; losing this wait must not fail the whole request and report a
                    // false error back to the extension for a sign-in that already went
                    // through. Observed for real as "the remote party closed the WebSocket
                    // connection without completing the close handshake" propagating all the
                    // way up to "request FAILED: syncAndOpen" - this was the one call in this
                    // sequence still left unguarded.
                    sw.Restart();
                    try
                    {
                        await WaitForConsoleNavigationAsync(ws, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(1200));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"SetCookiesAndNavigateAsync: WaitForConsoleNavigationAsync failed (non-fatal): {ex}");
                    }
                    Logger.Log($"SetCookiesAndNavigateAsync: WaitForConsoleNavigationAsync ({sw.ElapsedMilliseconds}ms)");

                    // The console's own session cookie (see SessionCookieName/
                    // TryGetCookieExpiryAsync) doesn't exist until AWS's federation redirect
                    // chain actually finishes - which is exactly why this has to happen HERE,
                    // after the wait above, rather than back when the banner script was first
                    // registered via Page.addScriptToEvaluateOnNewDocument (before
                    // Page.navigate, when that cookie couldn't possibly exist yet). That
                    // registration already ran once for this exact page load, without an
                    // expiry - Runtime.evaluate below re-runs the SAME (idempotent) script
                    // directly against the document that's already loaded, purely to fill in
                    // the expiry span it left empty the first time. Skipped entirely when the
                    // cookie isn't found, leaving the already-proven "no expiry" behavior
                    // completely untouched.
                    //
                    // Deliberately done on a BRAND NEW connection, not the `ws` above - real
                    // host.log data showed WaitForConsoleNavigationAsync's own "quiet period"
                    // detection leaves that `ws` unusable: it notices "nothing more happened"
                    // by letting a ReceiveAsync call time out via a CancellationTokenSource,
                    // and cancelling a pending receive that way trips .NET's ClientWebSocket
                    // into the Aborted state - which refuses ANY further send or receive.
                    // (Page.disable below has silently been hitting this exact same failure on
                    // this same path all along, harmlessly, since disposing this `ws` when this
                    // method returns tears the CDP session down either way - that's what
                    // actually restores bfcache eligibility, not the Page.disable call itself;
                    // it just never mattered before because nothing needed a real response back
                    // afterward.) A fresh connection sidesteps the whole problem - same shape
                    // ReinjectBannerAsync already uses successfully for every LATER navigation.
                    sw.Restart();
                    double? sessionExpiry = null;
                    try
                    {
                        using (var expiryWs = new ClientWebSocket())
                        {
                            expiryWs.Options.Proxy = null;
                            await expiryWs.ConnectAsync(new Uri(webSocketDebuggerUrl), CancellationToken.None);

                            sessionExpiry = await TryGetCookieExpiryAsync(expiryWs, 1, SessionCookieName);

                            if (sessionExpiry.HasValue)
                            {
                                string refreshedScript = BuildConsoleBannerScript(consoleBannerLabel, string.IsNullOrEmpty(titleLabel) ? consoleBannerLabel : titleLabel, sessionExpiry);
                                await SendAndAwaitAsync(expiryWs, 2, "Runtime.evaluate", new JObject { ["expression"] = refreshedScript });
                            }

                            if (expiryWs.State == WebSocketState.Open)
                            {
                                await expiryWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"SetCookiesAndNavigateAsync: session-expiry refresh failed (non-fatal): {ex}");
                    }
                    Logger.Log($"SetCookiesAndNavigateAsync: session-expiry refresh ({sw.ElapsedMilliseconds}ms) -> {(sessionExpiry?.ToString() ?? "null")}");

                    // Turn Page eventing back OFF now that we're done needing
                    // Page.frameNavigated - see this method's own doc comment for exactly why:
                    // leaving the Page domain enabled disables the tab's back/forward cache,
                    // which is what previously caused AWS's own "you've been signed out"
                    // screen. Best-effort: closing the WebSocket right after this achieves the
                    // same end state even if this one call fails for some reason, so a failure
                    // here must never block the close below.
                    sw.Restart();
                    try
                    {
                        await SendAndAwaitAsync(ws, nextId++, "Page.disable", new JObject());
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"SetCookiesAndNavigateAsync: Page.disable failed (non-fatal): {ex}");
                    }
                    Logger.Log($"SetCookiesAndNavigateAsync: Page.disable ({sw.ElapsedMilliseconds}ms)");
                }

                // Nothing left to wait on - once this closes, the in-page script's own
                // MutationObservers (see BuildConsoleBannerScript) are what keep the banner
                // and title correct for the rest of this document's life; they don't depend on
                // this CDP session or this native host process still being around.
                if (ws.State == WebSocketState.Open)
                {
                    sw.Restart();
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    Logger.Log($"SetCookiesAndNavigateAsync: ws.CloseAsync ({sw.ElapsedMilliseconds}ms)");
                }
            }
        }

        // -----------------------------------------------------------------
        // Banner watcher: keeps the banner/title correct across LATER full navigations in the
        // same tab, not just the one SetCookiesAndNavigateAsync itself handles. See the doc
        // comment on StartBannerWatcher below for the full design and why it polls instead of
        // holding a second CDP session open.
        // -----------------------------------------------------------------

        // Keyed by CDP targetId (one entry per open tab this process is watching). Static, not
        // an instance field: ChromeManager/EdgeManager (and therefore their own CdpClient) are
        // recreated fresh on every single native-messaging request (see Program.cs's
        // Dispatch), so anything that needs to survive BETWEEN requests - like "is a watcher
        // already running for this tab" - has to live at the class level, not the instance
        // level. The watcher loop itself removes its own entry when the tab closes or this
        // process shuts down the CancellationTokenSource, so this dictionary never accumulates
        // entries for tabs that are no longer open.
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _bannerWatchers =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        private static readonly TimeSpan BannerWatcherPollInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan BannerWatcherPostNavigationDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Starts (if one isn't already running) a background loop that keeps re-injecting the
        /// banner/title into <paramref name="targetId"/> every time its top-level url changes,
        /// for as long as that tab stays open. Safe to call more than once for the same
        /// targetId - later calls are a no-op while an earlier watcher for that exact tab is
        /// still running.
        ///
        /// WHY THIS POLLS THE HTTP /json/list ENDPOINT INSTEAD OF JUST STAYING ATTACHED: a
        /// tempting-looking alternative is to simply not close the WebSocket in
        /// SetCookiesAndNavigateAsync at all - keep that same session open (with Page.disable
        /// already called) for the tab's whole life, so
        /// Page.addScriptToEvaluateOnNewDocument's registration keeps firing on every future
        /// navigation for free. That was deliberately NOT done here, because it is a smaller
        /// variant of the exact design that caused task #14's "you've been signed out" bug in
        /// the first place (see SetCookiesAndNavigateAsync's own doc comment) - a CDP session
        /// left attached to a tab for its whole remaining life, regardless of which individual
        /// domains happen to be enabled at any given moment. Chromium's own source/docs don't
        /// spell out whether disabling Page events while staying attached is actually enough
        /// to keep the tab bfcache-eligible, or whether the attachment itself (independent of
        /// Page.enable/disable) is what disqualifies it - and there is no way to test that
        /// without a real machine. Given task #14 already happened once from a fix in this
        /// same neighborhood, this avoids the whole question: it never holds any CDP session
        /// open between navigations at all. The only thing it keeps doing in the background is
        /// a plain HTTP GET against /json/list (Chrome's DevTools target-discovery endpoint,
        /// not a debugger attachment of any kind - it doesn't touch bfcache, full stop). Only
        /// once that poll notices the tab's url actually changed does it open a CDP session at
        /// all, and that session is exactly as brief as the one SetCookiesAndNavigateAsync
        /// already uses successfully for the very first landing: connect, run the script once
        /// (via Runtime.evaluate, since the document is already loaded by the time we notice -
        /// unlike the first landing, there's no future document to register the script
        /// against), disconnect. Same proven-safe shape, just repeated on every later
        /// navigation instead of running once.
        ///
        /// Trade-off accepted deliberately: there is a real, visible gap of up to roughly one
        /// poll interval between a navigation actually happening and the banner/title
        /// reappearing on the new page - this only notices the change on its next tick, it
        /// doesn't see it happen live the way a held-open session would. That was judged worth
        /// it for "the banner reliably shows up on every console page, every time" over
        /// re-risking task #14 on an unverified assumption.
        ///
        /// initialUrl should be whatever url the tab is ALREADY sitting on right after
        /// SetCookiesAndNavigateAsync returns - this seeds "last seen" so the very first poll
        /// tick doesn't immediately re-fire a redundant (harmless, but wasted) injection for
        /// the exact same page load that call already handled directly.
        /// </summary>
        public void StartBannerWatcher(int port, string targetId, string bannerLabel, string titleLabel, string initialUrl)
        {
            if (string.IsNullOrEmpty(bannerLabel) || string.IsNullOrEmpty(targetId)) return;

            var cts = new CancellationTokenSource();
            if (!_bannerWatchers.TryAdd(targetId, cts))
            {
                cts.Dispose();
                return; // a watcher for this exact tab is already running
            }

            _ = Task.Run(() => RunBannerWatcherLoopAsync(port, targetId, bannerLabel, titleLabel, initialUrl, cts.Token));
        }

        private async Task RunBannerWatcherLoopAsync(int port, string targetId, string bannerLabel, string titleLabel, string initialUrl, CancellationToken ct)
        {
            string lastSeenUrl = initialUrl;
            Logger.Log($"BannerWatcher[{targetId}]: starting, initial url = {initialUrl}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(BannerWatcherPollInterval, ct);

                    string currentUrl;
                    try
                    {
                        var response = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/list");
                        var array = JArray.Parse(response);
                        var match = array.FirstOrDefault(t => (string)t["id"] == targetId);
                        if (match == null)
                        {
                            Logger.Log($"BannerWatcher[{targetId}]: target gone, stopping");
                            return;
                        }
                        currentUrl = (string)match["url"];
                    }
                    catch (Exception ex)
                    {
                        // Transient - Chrome's debugging endpoint can hiccup, the whole instance
                        // could even be mid-relaunch. Never let one bad poll kill the loop.
                        Logger.LogError($"BannerWatcher[{targetId}]: poll failed (non-fatal, retrying) - {ex.Message}");
                        continue;
                    }

                    if (string.IsNullOrEmpty(currentUrl) || currentUrl == lastSeenUrl) continue;

                    Logger.Log($"BannerWatcher[{targetId}]: url changed [{lastSeenUrl}] -> [{currentUrl}]");
                    lastSeenUrl = currentUrl;

                    try
                    {
                        // Give the new document a brief moment to actually start existing
                        // before we try to run script against it - the script itself (see
                        // BuildConsoleBannerScript) also retries and sets up MutationObservers,
                        // so this is just avoiding the most common case of injecting into a
                        // not-yet-parsed document rather than depending on it.
                        await Task.Delay(BannerWatcherPostNavigationDelay, ct);
                        await ReinjectBannerAsync(port, targetId, bannerLabel, titleLabel);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"BannerWatcher[{targetId}]: reinject failed (non-fatal) - {ex}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown via the CancellationTokenSource - nothing to log as an error.
            }
            finally
            {
                _bannerWatchers.TryRemove(targetId, out _);
                Logger.Log($"BannerWatcher[{targetId}]: stopped");
            }
        }

        /// <summary>
        /// Opens a brand new, short-lived CDP session against a tab that's already sitting on
        /// its final document - unlike SetCookiesAndNavigateAsync's own injection (which
        /// registers the script to run on the NEXT document via
        /// Page.addScriptToEvaluateOnNewDocument, because at that point the real console page
        /// hasn't loaded yet), here the document already exists, so the identical script is
        /// just run directly via Runtime.evaluate. Connects, evaluates, disconnects - no Page
        /// domain involved at all, so there is nothing here that touches back/forward cache
        /// eligibility beyond the same brief window SetCookiesAndNavigateAsync's own injection
        /// already uses.
        /// </summary>
        private async Task ReinjectBannerAsync(int port, string targetId, string bannerLabel, string titleLabel)
        {
            string wsUrl = await GetWebSocketUrlAsync(port, targetId);
            using (var ws = new ClientWebSocket())
            {
                ws.Options.Proxy = null; // same reasoning as the HttpClient handler above
                await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

                // Best-effort - by this point (a LATER navigation, well after the initial
                // sign-in) AWS's own console session cookie already exists, unlike at the
                // very first landing. See TryGetCookieExpiryAsync's own doc comment for the
                // assumed cookie name and why it has to be read this way at all.
                double? sessionExpiry = await TryGetCookieExpiryAsync(ws, 1, SessionCookieName);

                string script = BuildConsoleBannerScript(bannerLabel, string.IsNullOrEmpty(titleLabel) ? bannerLabel : titleLabel, sessionExpiry);
                await SendAndAwaitAsync(ws, 2, "Runtime.evaluate", new JObject { ["expression"] = script });

                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
            }
        }

        // Ported from the Firefox extension's own SaveConsoleCookieExpiryDateTime: the AWS
        // console's own session cookie, historically named 'aws-creds' and scoped to a path
        // containing '/console'. UNVERIFIED against a current console session - AWS may have
        // renamed or restructured this since the Firefox extension was last updated; if so,
        // TryGetCookieExpiryAsync below just finds nothing and the banner quietly shows no
        // expiry, same as if this feature didn't exist. Confirm the real name via a browser's
        // own devtools (Application/Storage -> Cookies) on a live console.aws.amazon.com
        // session and update this constant if it's wrong.
        private const string SessionCookieName = "aws-creds";

        // Real, published Chrome Web Store id (lbnopneedeeomcfebahnkankbedjjlfk) - same id as
        // content.js's matching constant for the portal-page half of this same link.
        private const string ExtensionReviewUrl = "https://chromewebstore.google.com/detail/lbnopneedeeomcfebahnkankbedjjlfk/reviews";

        /// <summary>
        /// Reads <paramref name="cookieName"/>'s own expiration (CDP's "expires", a Unix
        /// timestamp in seconds, or -1/absent for a session-only cookie) via Network.getCookies
        /// on the given, already-connected session - no Network.enable needed, same as
        /// Network.setCookie elsewhere in this file. Passing no "urls" param scopes this to
        /// cookies visible to the page's OWN current url (and any subframes), which is exactly
        /// what's wanted here - no separate plumbing to pass the console url in. Best-effort:
        /// returns null (never throws) if the cookie isn't present, has no real expiry, or the
        /// call fails for any reason - the banner script already treats "no expiry" as "don't
        /// show one" rather than an error.
        /// </summary>
        private static async Task<double?> TryGetCookieExpiryAsync(ClientWebSocket ws, int id, string cookieName)
        {
            try
            {
                var response = await SendAndAwaitAsync(ws, id, "Network.getCookies", new JObject());
                var cookies = response["result"]?["cookies"] as JArray;
                if (cookies == null) return null;

                foreach (var cookie in cookies)
                {
                    if ((string)cookie["name"] != cookieName) continue;
                    double? expires = (double?)cookie["expires"];
                    if (expires.HasValue && expires.Value > 0) return expires.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"TryGetCookieExpiryAsync[{cookieName}] failed (non-fatal): {ex}");
            }
            return null;
        }

        // Same hostname rule as the injected banner script itself (matches both the bare
        // "console.aws.amazon.com" AWS lands on by default, and region-prefixed hosts like
        // "us-east-2.console.aws.amazon.com").
        private static readonly Regex ConsoleHostnameRegex =
            new Regex(@"(^|\.)console\.aws\.amazon\.com$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads raw CDP frames off this session (ignoring anything that isn't a
        /// Page.frameNavigated event), watching for a frame whose URL's hostname is a real AWS
        /// console host. Requires Page.enable to have already been called on this session.
        /// Used to know when it's actually safe to detach - see the caller for why this
        /// matters.
        ///
        /// Returning the INSTANT we see one matching hostname isn't enough: AWS's federation
        /// flow can land on the bare "console.aws.amazon.com" and then immediately redirect
        /// again to a region-prefixed host ("us-east-2.console.aws.amazon.com") - both of which
        /// match our own hostname check, but only the second one is the page that's actually
        /// going to stick. Disconnecting right after the first match meant we detached between
        /// the two hops and missed registering our script on the one that mattered. So once
        /// we've seen at least one match, this keeps listening for a short "quiet period" and
        /// only returns once nothing further has happened for that long - riding out any
        /// additional hops - rather than assuming the first hit was the last one. This all
        /// happens after the window is already visible and navigating, so a short extra wait
        /// here is invisible to the user; it only delays this background exe's own exit.
        /// </summary>
        private static async Task WaitForConsoleNavigationAsync(ClientWebSocket ws, TimeSpan overallTimeout, TimeSpan quietPeriod)
        {
            var overallDeadline = DateTime.UtcNow + overallTimeout;
            var buffer = new byte[64 * 1024];

            // Set only when a REAL top-level console navigation is seen, to (quietDeadline =
            // that moment + quietPeriod). This used to be a plain bool ("have we matched at
            // least once") with the wait window recomputed as a fresh quietPeriod on every
            // loop iteration - which meant ANY message arriving at all (including the
            // sub-frame navigations filtered out below) silently kept resetting the clock,
            // since a successful receive just loops back around to a brand new quietPeriod-
            // long wait. Real host.log data showed this: once sub-frame hops were filtered out
            // of the hop count, the actual wait time didn't improve at all, because those same
            // filtered messages were still resetting this timer on their way past. Anchoring
            // the deadline to a fixed point in time (rather than "however long the CURRENT
            // wait call happens to run for") fixes that - only a real top-level match pushes
            // this out further.
            DateTime? quietDeadlineUtc = null;

            // Per-hop logging: the aggregate total this function reports to the caller was
            // hiding whether a slow run meant "AWS bounced through many redirects" or "one hop
            // took a long time to arrive" or "we sat in the quiet-period tail for no reason".
            // This stopwatch is just for the log lines below - it doesn't affect the actual
            // wait/return logic above/below it at all.
            var hopSw = System.Diagnostics.Stopwatch.StartNew();
            int hopCount = 0;

            while (ws.State == WebSocketState.Open)
            {
                var now = DateTime.UtcNow;
                var remaining = overallDeadline - now;
                if (remaining <= TimeSpan.Zero)
                {
                    Logger.Log($"WaitForConsoleNavigationAsync: overall timeout hit after {hopCount} hop(s), {hopSw.ElapsedMilliseconds}ms elapsed");
                    return;
                }

                TimeSpan waitBudget;
                if (quietDeadlineUtc.HasValue)
                {
                    var untilQuietDeadline = quietDeadlineUtc.Value - now;
                    if (untilQuietDeadline <= TimeSpan.Zero)
                    {
                        Logger.Log($"WaitForConsoleNavigationAsync: quiet period elapsed after {hopCount} hop(s), {hopSw.ElapsedMilliseconds}ms elapsed - settled");
                        return;
                    }
                    waitBudget = untilQuietDeadline < remaining ? untilQuietDeadline : remaining;
                }
                else
                {
                    waitBudget = remaining;
                }

                using var cts = new CancellationTokenSource(waitBudget);
                string text;
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, cts.Token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    text = Encoding.UTF8.GetString(ms.ToArray());
                }
                catch (OperationCanceledException)
                {
                    // Nothing arrived within the relevant window. If we'd already seen a
                    // console hostname match, that quiet is exactly the "nothing more is
                    // redirecting" signal we were waiting for - safe to return. Otherwise this
                    // is a genuine timeout: we never saw the console load at all.
                    Logger.Log(quietDeadlineUtc.HasValue
                        ? $"WaitForConsoleNavigationAsync: quiet period elapsed after {hopCount} hop(s), {hopSw.ElapsedMilliseconds}ms elapsed - settled"
                        : $"WaitForConsoleNavigationAsync: overall timeout hit after {hopCount} hop(s), {hopSw.ElapsedMilliseconds}ms elapsed - never saw console hostname");
                    return;
                }

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(text);
                }
                catch
                {
                    continue; // not JSON we care about - does NOT touch quietDeadlineUtc
                }

                if ((string)parsed["method"] != "Page.frameNavigated") continue;

                var frame = parsed["params"]?["frame"];
                string url = (string)frame?["url"];
                if (string.IsNullOrEmpty(url)) continue;

                // Page.frameNavigated fires for EVERY frame that navigates, not just the
                // top-level page. Real host.log data showed AWS's console loading several
                // internal iframes well after the actual page was done - a region-availability
                // widget, a QR-code modal, a nav widget, a search widget - and every one of
                // them happens to live under *.console.aws.amazon.com too, so they were
                // matching our hostname check and (before the fix above) resetting the
                // quiet-period timer for no reason. A sub-frame's payload always carries a
                // parentId; only the top-level frame we actually care about has none. This does
                // NOT touch quietDeadlineUtc - a sub-frame arriving must never push the
                // deadline back out, or we're right back to the same bug.
                if (frame?["parentId"] != null)
                {
                    Logger.Log($"WaitForConsoleNavigationAsync: sub-frame navigation ignored at {hopSw.ElapsedMilliseconds}ms -> {url}");
                    continue;
                }

                hopCount++;
                bool isConsoleMatch = false;
                try
                {
                    isConsoleMatch = ConsoleHostnameRegex.IsMatch(new Uri(url).Host);
                }
                catch (UriFormatException)
                {
                    // Intermediate hops can briefly be about:blank or similar - not a real
                    // absolute URL, so just keep waiting for the next navigation.
                }

                Logger.Log($"WaitForConsoleNavigationAsync: hop #{hopCount} at {hopSw.ElapsedMilliseconds}ms -> {url} (consoleMatch={isConsoleMatch})");

                // Each further TOP-LEVEL match (matching or not - an intermediate non-matching
                // top-level hop can still be followed by another real one) legitimately pushes
                // the deadline out, same as before; sub-frames above no longer can.
                if (isConsoleMatch) quietDeadlineUtc = DateTime.UtcNow + quietPeriod;
            }

            Logger.Log($"WaitForConsoleNavigationAsync: WebSocket closed after {hopCount} hop(s), {hopSw.ElapsedMilliseconds}ms elapsed");
        }

        /// <summary>
        /// Builds a script that only acts once location.hostname is an actual
        /// *.console.aws.amazon.com page (never during the awsapps.com SSO redirect chain).
        /// Inserts the full label as a fixed bar pinned to the bottom of the viewport, and
        /// prefixes just the account name onto the tab/window title. Idempotent - checks for
        /// its own marker id before inserting - so re-running it later (e.g. if this exact
        /// script were ever re-injected) is harmless. Survives the AWS console's own SPA-style
        /// in-app navigation (which doesn't reload the document at all) via the
        /// MutationObservers set up at the bottom - those keep running for as long as this
        /// document stays loaded, entirely independent of the CDP session that injected them.
        ///
        /// <paramref name="sessionExpiryEpochSeconds"/> (ported from the Firefox extension's
        /// AppendSessionExpiryTime), when given, is baked in as a fixed Unix timestamp and
        /// rendered as a live "Session expires in Xh Ym" countdown next to the banner label,
        /// refreshed every 60s from that FIXED value - this script never re-fetches it itself
        /// (a content script could just read AWS's own session cookie; this can't, since it's
        /// almost certainly HttpOnly, so the native host has to read it via CDP's
        /// Network.getCookies and pass the result in here - see CdpClient's own callers).
        /// Stored on window.__awslhSessionExpiryEpoch, and only overwritten there when a
        /// value is actually given: this script can be re-run more than once against the SAME
        /// still-open document (the banner watcher's Runtime.evaluate re-injection can land on
        /// a same-document SPA route change, not just a real new document), and a run that
        /// couldn't fetch a fresh cookie (network hiccup) must not blank out an expiry that a
        /// previous run already established. The refresh interval itself is guarded the same
        /// way (window.__awslhExpiryIntervalId) so repeated re-runs never stack up more than
        /// one timer.
        /// </summary>
        private static string BuildConsoleBannerScript(string bannerLabel, string titleLabel, double? sessionExpiryEpochSeconds = null)
        {
            string jsonBannerLabel = Newtonsoft.Json.JsonConvert.SerializeObject(bannerLabel);
            string jsonTitleLabel = Newtonsoft.Json.JsonConvert.SerializeObject(titleLabel);
            string jsonReviewUrl = Newtonsoft.Json.JsonConvert.SerializeObject(ExtensionReviewUrl);
            string jsSessionExpiryEpoch = sessionExpiryEpochSeconds.HasValue
                ? sessionExpiryEpochSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";

            string lower = bannerLabel.ToLowerInvariant();
            // Check "nonprod" BEFORE "prod": "NonProd" contains "prod" as a plain substring,
            // so the naive version of this check flagged NonProd accounts with the same red
            // used for real Prod - the opposite of what a safety color-code is for.
            bool isNonProd = lower.Contains("nonprod") || lower.Contains("non-prod") || lower.Contains("non_prod");
            bool isProd = !isNonProd && lower.Contains("prod");
            string color = isProd ? "#d13212"
                : (isNonProd || lower.Contains("sandbox") || lower.Contains("test") || lower.Contains("dev")) ? "#1d8102"
                : "#232f3e";

            return @"
(function () {
  // Matches both the bare 'console.aws.amazon.com' AWS lands on by default, and
  // region-prefixed hosts like 'us-east-2.console.aws.amazon.com'.
  if (!/(^|\.)console\.aws\.amazon\.com$/.test(location.hostname)) return;

  var BANNER_LABEL = " + jsonBannerLabel + @";
  var TITLE_PREFIX = " + jsonTitleLabel + @" + ' :: ';
  var COLOR = '" + color + @"';
  var SESSION_EXPIRY_EPOCH = " + jsSessionExpiryEpoch + @";
  var REVIEW_URL = " + jsonReviewUrl + @";

  function ensureTitle() {
    if (document.title.indexOf(TITLE_PREFIX) !== 0) {
      document.title = TITLE_PREFIX + document.title;
    }
  }

  function insertBanner() {
    if (!document.body) return;
    if (document.getElementById('awslh-profile-banner')) return;

    // Pinning to the BOTTOM of the viewport sidesteps a fight with AWS's own fixed
    // top header/nav: nothing there to collide with, and being viewport-fixed means we
    // don't need to push any of AWS's own content down to make room.
    var banner = document.createElement('div');
    banner.id = 'awslh-profile-banner';
    banner.style.cssText = 'position:fixed;left:0;right:0;bottom:0;z-index:2147483647;' +
      'box-sizing:border-box;background:' + COLOR + ';color:#fff;text-align:center;' +
      'padding:5px 8px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;';

    // Split into two spans (label + expiry) rather than one textContent string, so the
    // expiry half can be updated on its own every 60s without touching - or re-reading -
    // the label half.
    var labelSpan = document.createElement('span');
    labelSpan.textContent = BANNER_LABEL;
    banner.appendChild(labelSpan);

    var expirySpan = document.createElement('span');
    expirySpan.id = 'awslh-session-expiry';
    banner.appendChild(expirySpan);

    // Same link as the options page and the portal page's session-expiry text - one more
    // place someone already looking at this extension's own UI might notice it and rate it.
    var rateLink = document.createElement('a');
    rateLink.id = 'awslh-rate-link';
    rateLink.href = REVIEW_URL;
    rateLink.target = '_blank';
    rateLink.rel = 'noopener noreferrer';
    rateLink.textContent = 'Rate AWS Login Helper Extension';
    rateLink.style.cssText = 'margin-left:10px;color:#fff;text-decoration:underline;opacity:0.85;';
    banner.appendChild(rateLink);

    document.body.appendChild(banner);
  }

  function formatDurationHM(totalSeconds) {
    var totalMinutes = Math.floor(totalSeconds / 60);
    var hours = Math.floor(totalMinutes / 60);
    var minutes = totalMinutes % 60;
    return hours + ' hour' + (hours !== 1 ? 's' : '') + ' ' + minutes + ' min' + (minutes !== 1 ? 's' : '');
  }

  // Ported from the Firefox extension's AppendSessionExpiryTime, adapted for a value baked
  // in at injection time rather than read live from a cookie (see this function's own doc
  // comment above BuildConsoleBannerScript for why). Reads from window.__awslhSessionExpiryEpoch
  // (not the local SESSION_EXPIRY_EPOCH constant) so a LATER re-injection into this same
  // still-open document that brings a fresher value updates what the running interval sees,
  // without needing to also replace the interval itself.
  function updateExpiryText() {
    var expirySpan = document.getElementById('awslh-session-expiry');
    if (!expirySpan) return;
    var epoch = window.__awslhSessionExpiryEpoch;
    if (!epoch) return;
    var remainingSeconds = epoch - (Date.now() / 1000);
    expirySpan.textContent = remainingSeconds > 0
      ? (' | Session expires in ' + formatDurationHM(remainingSeconds))
      : ' | Session expired';
  }

  function apply() {
    ensureTitle();
    insertBanner();
    updateExpiryText();
  }

  // Only overwrite the shared value when THIS run actually has one - see this function's own
  // doc comment for why a run with nothing new must never blank out a previously-known value.
  if (SESSION_EXPIRY_EPOCH) {
    window.__awslhSessionExpiryEpoch = SESSION_EXPIRY_EPOCH;
  }

  // Guarded the same way insertBanner's own marker id guards against a second banner div:
  // this whole script can run more than once against the same still-open document (see the
  // doc comment above), and each run must not add ANOTHER 60s timer on top of one that's
  // already ticking.
  if (!window.__awslhExpiryIntervalId) {
    window.__awslhExpiryIntervalId = setInterval(updateExpiryText, 60000);
  }

  apply();
  document.addEventListener('DOMContentLoaded', apply);
  [250, 750, 1500, 3000].forEach(function (delay) {
    setTimeout(apply, delay);
  });

  // The console's SPA occasionally wipes and rebuilds large chunks of body's own direct
  // children well after our fixed retries above have already run, which can take our
  // inserted banner element out along with whatever else got replaced. Watching body's
  // OWN child list (not the whole subtree - this fires only when top-level children are
  // added/removed, not on every one of the SPA's constant internal re-renders) lets us
  // reinsert immediately when that happens, instead of leaving a gap until the next
  // scheduled retry - without paying for a full-page observer.
  function observeBodyChildren() {
    if (!document.body) {
      setTimeout(observeBodyChildren, 50);
      return;
    }
    new MutationObserver(insertBanner).observe(document.body, { childList: true });
  }
  observeBodyChildren();

  // The title specifically needs more than fixed retries: AWS's own SPA keeps setting
  // document.title itself as it finishes loading (account name, service name, etc.), and it
  // can do that well after our last retry above, silently overwriting our prefix. Rather than
  // widen the retry window (still a race, just a slower one), watch the <title> element
  // itself - not the whole page - so we catch every future rename AWS makes, no matter when.
  // This is a world apart from the whole-subtree observer removed above: the <title> node
  // changes only a handful of times total during a page's life, so observing just it costs
  // nothing noticeable even on a page as busy as the AWS console.
  function observeTitle() {
    var titleEl = document.querySelector('head > title');
    if (!titleEl) {
      setTimeout(observeTitle, 50);
      return;
    }
    new MutationObserver(ensureTitle).observe(titleEl, { childList: true, characterData: true, subtree: true });
  }
  observeTitle();
})();
";
        }

        /// <summary>
        /// Builds a "data:" URL for an instant, zero-network-request loading placeholder page.
        /// Navigating a tab to a data: URL involves no DNS/TLS/HTTP round trip at all - Chrome
        /// just parses the string and renders it - so this shows the user something immediately
        /// while cookies are still being set, instead of a blank white about:blank tab that
        /// looks frozen.
        /// </summary>
        private static string BuildLoadingPageDataUrl(string label)
        {
            string escapedLabel = System.Net.WebUtility.HtmlEncode(label);
            string html = $@"<!doctype html><html><head><meta charset=""utf-8""><title>Signing in...</title>
<style>
  body {{ margin:0; height:100vh; display:flex; align-items:center; justify-content:center;
          background:#0f1b2a; color:#fff; font-family:Arial,sans-serif; }}
  .box {{ text-align:center; }}
  .spinner {{ width:28px; height:28px; margin:0 auto 14px; border-radius:50%;
              border:3px solid rgba(255,255,255,0.25); border-top-color:#fff;
              animation:spin 0.8s linear infinite; }}
  @keyframes spin {{ to {{ transform:rotate(360deg); }} }}
  .label {{ font-size:15px; font-weight:bold; }}
  .sub {{ font-size:12px; color:#9fb3c8; margin-top:6px; }}
</style></head>
<body><div class=""box""><div class=""spinner""></div>
<div class=""label"">{escapedLabel}</div>
<div class=""sub"">Signing in...</div>
</div></body></html>";

            return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
        }

        /// <summary>
        /// Sets the OS-level window this tab belongs to into the "maximized" state, via CDP's
        /// Browser domain rather than a launch-time command-line flag - Browser.* commands
        /// apply to the actual window regardless of which target's WebSocket session issues
        /// them, so this works the same whether the window was just freshly launched or opened
        /// by forwarding into an already-running Chrome instance. Best-effort: a failure here
        /// must never block actually opening the account/role tab.
        /// </summary>
        private static async Task MaximizeWindowAsync(ClientWebSocket ws, int firstId)
        {
            try
            {
                var windowInfo = await SendAndAwaitAsync(ws, firstId, "Browser.getWindowForTarget", new JObject());
                var windowId = windowInfo["result"]?["windowId"];
                if (windowId == null) return;

                await SendAndAwaitAsync(ws, firstId + 1, "Browser.setWindowBounds", new JObject
                {
                    ["windowId"] = windowId,
                    ["bounds"] = new JObject { ["windowState"] = "maximized" },
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"MaximizeWindowAsync failed (non-fatal): {ex}");
            }
        }

        private static async Task<JObject> SendAndAwaitAsync(ClientWebSocket ws, int id, string method, JObject parms)
        {
            var request = new JObject { ["id"] = id, ["method"] = method, ["params"] = parms };
            var bytes = Encoding.UTF8.GetBytes(request.ToString(Newtonsoft.Json.Formatting.None));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var buffer = new byte[64 * 1024];
            // One CancellationTokenSource for the whole call, not one per WebSocket frame -
            // allocating/registering a fresh 10-second timer on every single read added up
            // when there were several unrelated frames to read past before finding the one
            // response we actually wanted.
            using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (DateTime.UtcNow < deadline)
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, overallCts.Token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    JObject parsed;
                    try
                    {
                        parsed = JObject.Parse(text);
                    }
                    catch
                    {
                        continue; // not JSON we care about
                    }

                    // Ignore CDP events (they have "method" but no matching "id"); only return
                    // once we see the response to OUR request id.
                    if (parsed["id"] != null && (int)parsed["id"] == id)
                    {
                        if (parsed["error"] != null)
                        {
                            throw new InvalidOperationException($"CDP {method} failed: {parsed["error"]}");
                        }
                        return parsed;
                    }
                }
            }

            throw new TimeoutException($"CDP command {method} (id {id}) timed out waiting for a response.");
        }
    }

    internal sealed class CookieDto
    {
        public string Domain { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Path { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
        public string SameSite { get; set; }
        public bool? Session { get; set; }
        public double? ExpirationDate { get; set; }
    }
}
