using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        ///     RegisterProfileWindowAsync below, called by the caller right after this method
        ///     returns - see its own doc comment for why that's a polling design rather than a
        ///     second, longer-lived CDP attachment (that shape already failed once, above).
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

                // MAC-SPECIFIC DEVIATION FROM THE WINDOWS BUILD (this file started as a byte-
                // for-byte copy of the Windows build's CdpClient.cs - see native-host-mac's own
                // top-of-tree notes - this is the one place it no longer is):
                //
                // On Windows, --start-maximized on the launch command line doesn't reliably
                // apply to a window opened by singleton-forwarding into an already-running
                // Chrome instance, so CDP's Browser.setWindowBounds(windowState: "maximized")
                // is used instead, right here, as the authoritative fallback.
                //
                // Real-world Mac testing surfaced a window stuck on about:blank forever, and a
                // SECOND account/role opening as a new TAB inside that same window instead of
                // its own separate window - both symptoms consistent with Chromium on macOS
                // mapping CDP's "maximized" windowState onto a true native-fullscreen Space
                // transition (an animated, OS-level transition - quite different from Windows,
                // where "maximized" is just an instant rectangle resize), with the
                // Network.setCookie/Page.navigate calls further down racing that transition,
                // and a window already inside a fullscreen Space pulling later windows in as
                // tabs rather than spawning new ones. Skipping this call on Mac and relying
                // purely on --start-maximized from the launch command line (ChromeManager.cs's
                // own LaunchChrome already passes it, same as the Windows build) removes that
                // race entirely. UNVERIFIED against real hardware beyond this one fix attempt -
                // if windows still come up at Chrome's default (non-maximized) size afterward,
                // that's the next thing to revisit, same shape as Windows's own problem, just
                // needing a Mac-safe way to set bounds (e.g. explicit left/top/width/height
                // matching the screen, which does NOT enter native fullscreen the way
                // windowState: "maximized" apparently does) instead of this windowState value.
                if (!OperatingSystem.IsMacOS())
                {
                    sw.Restart();
                    await MaximizeWindowAsync(ws, nextId);
                    nextId += 2;
                    Logger.Log($"SetCookiesAndNavigateAsync: MaximizeWindowAsync ({sw.ElapsedMilliseconds}ms)");
                }

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
        // Instance watcher: keeps the banner/title correct across LATER full navigations, AND
        // reports every AWS console service page actually visited inside a profile's isolated
        // window - including in tabs the user opened BY HAND (ctrl/middle-clicking a service
        // link in AWS's own console nav typically opens a new tab, not a same-tab navigation) -
        // back to the extension, so "Recent Logins & Services" on the portal page reflects what
        // really happened, not just the one url this host was originally asked to open.
        //
        // THIS USED TO BE PER-TARGET (StartBannerWatcher, one watcher per tab this host itself
        // opened), which was enough when the only job was "keep re-injecting the banner into
        // the one tab we know about." That falls apart the moment a SECOND tab appears in that
        // same window that this host was never told about - a watcher keyed to only the
        // original target id can never see it. Polling the WHOLE shared instance's /json/list
        // and classifying every target found there (see ResolveWindowIdAsync below) catches a
        // brand new tab the same way the old design caught an in-place navigation.
        //
        // HOW A TARGET GETS ATTRIBUTED TO AN ACCOUNT/ROLE: CDP's /json/list has no concept of
        // "--profile-directory" at all - there is no field to read it from. What IS available
        // is each target's own OS-level window (Browser.getWindowForTarget), and since every
        // profile in this shared instance gets its own separate top-level window (never shared
        // across profiles - see ChromeManager's own design notes), a window's identity stands
        // in for its profile's identity. RegisterProfileWindowAsync resolves and records that
        // mapping once, right when a profile's window is first opened or forwarded into; every
        // later poll tick looks a target's window up in that same map. The hidden cold-start
        // placeholder window is never registered here at all, so it's silently never matched -
        // no special-casing needed to exclude it.
        // KNOWN GAP: a tab opened via "open in new WINDOW" (not tab) gets a brand new windowId
        // this map was never told about, so it won't be attributed. "Open in new tab" - the
        // common case for AWS's own console nav - is what this covers.
        //
        // WHY THIS POLLS /json/list INSTEAD OF HOLDING A CDP SESSION OPEN: same reasoning as
        // the old per-target design's own doc comment (preserved in git history) - a CDP
        // session left attached to a tab for its whole remaining life risks disabling its
        // back/forward cache eligibility (see SetCookiesAndNavigateAsync's doc comment on task
        // #14's "you've been signed out" bug). Polling a plain HTTP endpoint touches no tab's
        // debugger attachment at all; only once a change is actually seen does this open any
        // CDP session, and only as briefly as ReinjectBannerAsync/ResolveWindowIdAsync already
        // do.
        // -----------------------------------------------------------------

        private sealed class ProfileWindowIdentity
        {
            public string ProfileKey;
            public string AccountId;
            public string AccountName;
            public string RoleName;
            public string DisplayLabel;
            public string TitleLabel;
        }

        // windowId -> which account/role's window this is. Static, not an instance field, for
        // the same reason the old _bannerWatchers dictionary was: ChromeManager (and therefore
        // its own CdpClient) is recreated fresh on every single native-messaging request (see
        // Program.cs's Dispatch), so anything that needs to survive BETWEEN requests has to
        // live at the class level.
        private static readonly ConcurrentDictionary<int, ProfileWindowIdentity> _profileWindows =
            new ConcurrentDictionary<int, ProfileWindowIdentity>();

        // One watcher loop per shared-instance port (not per target - see doc comment above).
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _instanceWatchers =
            new ConcurrentDictionary<int, CancellationTokenSource>();

        // Per-target bookkeeping the poll loop needs across ticks.
        private static readonly ConcurrentDictionary<string, string> _lastSeenUrlByTarget =
            new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, int> _windowIdByTarget =
            new ConcurrentDictionary<string, int>();

        private static readonly TimeSpan InstanceWatcherPollInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan InstanceWatcherPostNavigationDelay = TimeSpan.FromMilliseconds(500);
        private static readonly Regex ConsoleUrlPattern = new Regex(@"\.console\.aws\.amazon\.com/", RegexOptions.IgnoreCase);

        /// <summary>
        /// Derives the AWS service name from a console url's first path segment - the exact
        /// same rule extension/background.js's own extractServiceName uses for the main-profile
        /// case (where a loaded content script can read window.location directly). Needed here
        /// too because tabs inside the dedicated, extension-less instance have no content
        /// script to do this for themselves at all - the native host is the only thing that
        /// ever sees these urls.
        /// </summary>
        private static string ExtractServiceName(string url)
        {
            try
            {
                string path = new Uri(url).AbsolutePath;
                return path.Split('/').FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Call once, right after opening or forwarding into a profile's tab (see Program.cs's
        /// HandleSyncAndOpen), to (1) record which window that profile now owns, for every
        /// later poll tick's lookup, and (2) make sure the shared per-port instance watcher is
        /// actually running. Safe to call repeatedly for the same profile/window - both steps
        /// are idempotent. stdout is threaded all the way through from Program.cs's own
        /// RunNativeMessagingLoop - NativeMessaging.WriteMessage's internal lock already makes
        /// it safe to write to from this background loop at the same time as any in-flight
        /// request's own response.
        /// </summary>
        public async Task RegisterProfileWindowAsync(
            int port, string targetId, Stream stdout,
            string profileKey, string accountId, string accountName, string roleName,
            string displayLabel, string titleLabel, string initialUrl)
        {
            if (string.IsNullOrEmpty(targetId)) return;

            try
            {
                string wsUrl = await GetWebSocketUrlAsync(port, targetId);
                int? windowId = await ResolveWindowIdAsync(wsUrl);
                if (windowId == null)
                {
                    Logger.LogError($"RegisterProfileWindowAsync[{profileKey}]: could not resolve a windowId - service reporting/banner-on-new-tab won't work for this window until it's reopened.");
                }
                else
                {
                    _profileWindows[windowId.Value] = new ProfileWindowIdentity
                    {
                        ProfileKey = profileKey,
                        AccountId = accountId,
                        AccountName = accountName,
                        RoleName = roleName,
                        DisplayLabel = displayLabel,
                        TitleLabel = titleLabel,
                    };
                    _windowIdByTarget[targetId] = windowId.Value;
                    Logger.Log($"RegisterProfileWindowAsync[{profileKey}]: windowId={windowId.Value}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"RegisterProfileWindowAsync[{profileKey}]: failed to resolve windowId (non-fatal, same effect as above) - {ex}");
            }

            _lastSeenUrlByTarget[targetId] = initialUrl;
            StartInstanceWatcher(port, stdout);
        }

        private void StartInstanceWatcher(int port, Stream stdout)
        {
            var cts = new CancellationTokenSource();
            if (!_instanceWatchers.TryAdd(port, cts))
            {
                cts.Dispose();
                return; // a watcher for this exact shared instance is already running
            }

            _ = Task.Run(() => RunInstanceWatcherLoopAsync(port, stdout, cts.Token));
        }

        private async Task RunInstanceWatcherLoopAsync(int port, Stream stdout, CancellationToken ct)
        {
            Logger.Log($"InstanceWatcher[port {port}]: starting");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(InstanceWatcherPollInterval, ct);

                    List<(string Id, string Url)> targets;
                    try
                    {
                        targets = await ListPageTargetsAsync(port);
                    }
                    catch (Exception ex)
                    {
                        // Transient - Chrome's debugging endpoint can hiccup, the whole instance
                        // could even be mid-relaunch. Never let one bad poll kill the loop.
                        Logger.LogError($"InstanceWatcher[port {port}]: poll failed (non-fatal, retrying) - {ex.Message}");
                        continue;
                    }

                    if (targets.Count == 0)
                    {
                        Logger.Log($"InstanceWatcher[port {port}]: no targets left, stopping");
                        return;
                    }

                    foreach (var (targetId, url) in targets)
                    {
                        if (string.IsNullOrEmpty(url)) continue;

                        bool isNewTarget = !_lastSeenUrlByTarget.ContainsKey(targetId);
                        bool urlUnchanged = !isNewTarget && _lastSeenUrlByTarget[targetId] == url;
                        _lastSeenUrlByTarget[targetId] = url;

                        if (urlUnchanged)
                        {
                            // Same target, same url as last tick. Almost always this just means
                            // "still sitting on the same page" and there's nothing to do - but
                            // it's also exactly what a plain refresh (F5) looks like from here,
                            // since reloading a page never changes its URL. A refresh wipes
                            // every injected script along with the rest of the document, so
                            // without this check the banner would simply stay gone until the
                            // user next navigates somewhere else. See IsBannerPresentAsync's own
                            // doc comment for the detection side of this.
                            if (_windowIdByTarget.TryGetValue(targetId, out int knownWindowId) &&
                                _profileWindows.TryGetValue(knownWindowId, out var knownIdentity) &&
                                ConsoleUrlPattern.IsMatch(url))
                            {
                                bool present = await IsBannerPresentAsync(port, targetId);
                                if (!present)
                                {
                                    Logger.Log($"InstanceWatcher[port {port}]: [{knownIdentity.ProfileKey}] banner missing on an unchanged url (likely a refresh) -> reinjecting");
                                    try
                                    {
                                        await ReinjectBannerAsync(port, targetId, knownIdentity.DisplayLabel, knownIdentity.TitleLabel);
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.LogError($"InstanceWatcher[port {port}]: reinject-after-refresh failed (non-fatal) - {ex}");
                                    }
                                }
                            }
                            // Either way, nothing NEW was visited here - just a refresh (or a
                            // steady-state tab) - so there's no serviceVisited push to make.
                            continue;
                        }

                        if (!_windowIdByTarget.TryGetValue(targetId, out int windowId))
                        {
                            try
                            {
                                string wsUrl = await GetWebSocketUrlAsync(port, targetId);
                                int? resolved = await ResolveWindowIdAsync(wsUrl);
                                if (resolved == null) continue; // try again next tick
                                windowId = resolved.Value;
                                _windowIdByTarget[targetId] = windowId;
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError($"InstanceWatcher[port {port}]: failed to resolve windowId for new target {targetId} (will retry) - {ex.Message}");
                                continue;
                            }
                        }

                        // Not one of our own profile windows (or the hidden cold-start
                        // placeholder, which is never registered into _profileWindows at all).
                        if (!_profileWindows.TryGetValue(windowId, out var identity)) continue;
                        if (!ConsoleUrlPattern.IsMatch(url)) continue; // nothing to re-banner or report

                        Logger.Log($"InstanceWatcher[port {port}]: [{identity.ProfileKey}] {(isNewTarget ? "new tab" : "navigated")} -> {url}");

                        try
                        {
                            // Give the new document a brief moment to actually start existing
                            // before running script against it - BuildConsoleBannerScript also
                            // retries and sets up MutationObservers, so this just avoids the
                            // most common case of injecting into a not-yet-parsed document.
                            await Task.Delay(InstanceWatcherPostNavigationDelay, ct);
                            await ReinjectBannerAsync(port, targetId, identity.DisplayLabel, identity.TitleLabel);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"InstanceWatcher[port {port}]: reinject failed (non-fatal) - {ex}");
                        }

                        try
                        {
                            var push = new JObject
                            {
                                ["action"] = "serviceVisited",
                                ["accountId"] = identity.AccountId,
                                ["accountName"] = identity.AccountName,
                                ["roleName"] = identity.RoleName,
                                ["serviceName"] = ExtractServiceName(url),
                                ["url"] = url,
                            };
                            // No __requestId - this is an unsolicited push, not a response to
                            // any pending request. See background.js's nativePort.onMessage
                            // listener for the matching read side.
                            NativeMessaging.WriteMessage(stdout, push.ToString(Newtonsoft.Json.Formatting.None));
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"InstanceWatcher[port {port}]: failed to push serviceVisited (non-fatal) - {ex}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown via the CancellationTokenSource - nothing to log as an error.
            }
            finally
            {
                _instanceWatchers.TryRemove(port, out _);
                Logger.Log($"InstanceWatcher[port {port}]: stopped");
            }
        }

        /// <summary>
        /// Resolves the OS-level window a given tab's CDP target belongs to - see this
        /// section's own top-of-block doc comment for why that's the only reliable way to tell
        /// which account/role profile a tab belongs to. Brief, standalone CDP session: connect,
        /// ask, disconnect - same shape as ReinjectBannerAsync below.
        /// </summary>
        private static async Task<int?> ResolveWindowIdAsync(string webSocketDebuggerUrl)
        {
            using (var ws = new ClientWebSocket())
            {
                ws.Options.Proxy = null;
                await ws.ConnectAsync(new Uri(webSocketDebuggerUrl), CancellationToken.None);

                var windowInfo = await SendAndAwaitAsync(ws, 1, "Browser.getWindowForTarget", new JObject());
                var windowId = windowInfo["result"]?["windowId"];

                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }

                return windowId?.Value<int>();
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

        /// <summary>
        /// Answers "is our banner div still in this document right now" - the piece that was
        /// missing for a plain refresh (F5) of an already-bannered console tab. A refresh keeps
        /// the tab's URL identical, so RunInstanceWatcherLoopAsync's own "did the URL change"
        /// check alone can never notice it; this gives that loop a second, independent signal
        /// to fall back on for exactly that case. Same brief connect/evaluate/disconnect shape
        /// as ResolveWindowIdAsync/ReinjectBannerAsync above - never holds a session open.
        /// Best-effort: treats any failure (closed tab, no document yet, mid-navigation) as
        /// "present", so a transient hiccup here can never cause an unwanted reinject - only a
        /// confirmed, successfully-read "false" does.
        /// </summary>
        private async Task<bool> IsBannerPresentAsync(int port, string targetId)
        {
            try
            {
                string wsUrl = await GetWebSocketUrlAsync(port, targetId);
                using (var ws = new ClientWebSocket())
                {
                    ws.Options.Proxy = null;
                    await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

                    var response = await SendAndAwaitAsync(ws, 1, "Runtime.evaluate", new JObject
                    {
                        ["expression"] = "!!document.getElementById('awslh-profile-banner')",
                        ["returnByValue"] = true,
                    });

                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    }

                    var value = response["result"]?["result"]?["value"];
                    return value == null || value.Value<bool>();
                }
            }
            catch
            {
                return true; // see doc comment above - never let a check failure force a reinject
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

        // Real, published Chrome Web Store id (cpjgfffnippggeadnapfeggfifkmomag) - same id as
        // content.js's matching constant for the portal-page half of this same link.
        private const string ExtensionReviewUrl = "https://chromewebstore.google.com/detail/aws-login-helper/cpjgfffnippggeadnapfeggfifkmomag/reviews";

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
        /// Inserts the full label as an inline bar directly under AWS's top nav (div#h), and
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

  // Banner placement: INLINE, directly under AWS's own top nav bar (div#h), instead of
  // pinned to the bottom of the viewport. It sits in the normal page flow right below the nav
  // so it is the first thing seen, and it pushes the console content down rather than
  // overlapping it. position:unset (= static) makes left/right/bottom/z-index inert here; they
  // are kept only so the style string is identical to the fixed variant below.
  // If AWS's nav is not in the DOM yet (or its markup ever changes) the old fixed-bottom bar
  // is used so the label is never lost, and insertBanner moves it under the nav as soon as
  // the nav exists.
  var BANNER_BASE_STYLE = 'left:0;right:0;bottom:0;z-index:2147483647;' +
    'box-sizing:border-box;background:' + COLOR + ';color:#fff;text-align:center;' +
    'padding:5px 8px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;';
  var BANNER_INLINE_STYLE = 'position:unset;' + BANNER_BASE_STYLE;
  var BANNER_FALLBACK_STYLE = 'position:fixed;' + BANNER_BASE_STYLE;
  var observedNavParent = null;

  function getNav() {
    var nav = document.getElementById('h');
    return (nav && nav.parentNode) ? nav : null;
  }

  function buildBanner() {
    var banner = document.createElement('div');
    banner.id = 'awslh-profile-banner';

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
    return banner;
  }

  // The nav's parent (div#b) is a child of body, not body itself, so the body-level observer
  // below can't see the console rebuilding div#b's own children. Watch that one level too.
  function observeNavParent(parent) {
    if (observedNavParent === parent) return;
    observedNavParent = parent;
    new MutationObserver(insertBanner).observe(parent, { childList: true });
  }

  function insertBanner() {
    if (!document.body) return;
    var nav = getNav();
    var banner = document.getElementById('awslh-profile-banner') || buildBanner();

    if (nav) {
      if (banner.previousElementSibling !== nav) {
        banner.style.cssText = BANNER_INLINE_STYLE;
        nav.parentNode.insertBefore(banner, nav.nextSibling);
      }
      observeNavParent(nav.parentNode);
    } else if (!banner.parentNode) {
      banner.style.cssText = BANNER_FALLBACK_STYLE;
      document.body.appendChild(banner);
    }
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

  // Same reasoning as the __awslhExpiryIntervalId guard above, extended to these two
  // observers: the instance watcher now re-runs this whole script on a still-open,
  // never-reloaded document too (a presence check it does to detect a same-URL refresh -
  // see RunInstanceWatcherLoopAsync's own comments), so unlike before, 'ran more than once
  // against the same live document' is no longer just a theoretical case to guard against.
  // Without this guard, each extra run would stack one more MutationObserver on top of the
  // last - still harmless (every one of them calls the same idempotent insertBanner/
  // ensureTitle), but pure waste that only grows over a long-lived tab.
  if (!window.__awslhBannerObserversInstalled) {
    window.__awslhBannerObserversInstalled = true;
    observeBodyChildren();
    observeTitle();
  }
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

        /// <summary>
        /// MAC-ONLY ADDITION - nothing in the Windows build needs this, because the Windows
        /// build's own cold-start placeholder window stays off-screen via its launch-time
        /// --window-position=-32000,-32000 flag alone (see ChromeManager.HiddenPlacementArgs on
        /// both builds). That same off-screen-coordinates trick depends on the OS's window
        /// manager actually placing a window wherever the launch flag says - real Mac testing
        /// surfaced a window stuck on about:blank in full screen, consistent with the
        /// placeholder's off-screen position not being honored the same way on macOS (its
        /// window manager may clamp/ignore an invalid negative origin back onto a visible
        /// display instead). Since the placeholder is deliberately never navigated anywhere by
        /// SetCookiesAndNavigateAsync, a placeholder stuck on about:blank forever is exactly
        /// what this would look like, and a second account's window landing as a tab inside it
        /// (rather than a separate window) is consistent with the "brand-new profile forces a
        /// cold restart" logic in ChromeManager.EnsureTabForProfileAsync not actually displacing
        /// it in that state.
        ///
        /// Rather than trying to guess coordinates that macOS will respect, this asks Chrome's
        /// own window-state API directly to minimize the window - the same Browser.* CDP
        /// mechanism MaximizeWindowAsync above already uses, just a different target state, and
        /// one that doesn't depend on display geometry at all. Opens its own brief, standalone
        /// CDP session (there is no already-open one at the point ChromeManager calls this - see
        /// EnsureColdStartPlaceholderWarmAsync, which never connects via WebSocket at all today)
        /// and closes it immediately after - same connect/act/disconnect shape as
        /// ReinjectBannerAsync above. Best-effort: a failure here must never block the
        /// placeholder from warming the shared instance, which is its only real job.
        /// </summary>
        public async Task HideWindowAsync(string webSocketDebuggerUrl)
        {
            try
            {
                using (var ws = new ClientWebSocket())
                {
                    ws.Options.Proxy = null;
                    await ws.ConnectAsync(new Uri(webSocketDebuggerUrl), CancellationToken.None);

                    var windowInfo = await SendAndAwaitAsync(ws, 1, "Browser.getWindowForTarget", new JObject());
                    var windowId = windowInfo["result"]?["windowId"];
                    if (windowId != null)
                    {
                        await SendAndAwaitAsync(ws, 2, "Browser.setWindowBounds", new JObject
                        {
                            ["windowId"] = windowId,
                            ["bounds"] = new JObject { ["windowState"] = "minimized" },
                        });
                    }

                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"HideWindowAsync failed (non-fatal): {ex}");
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
