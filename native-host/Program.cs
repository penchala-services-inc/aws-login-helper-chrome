using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Entry point. Two very different modes:
    ///
    ///   1. CLI mode (--register / --unregister / --check-extension / --version): used by the
    ///      MSI installer's custom action, and by a human troubleshooting on their own machine.
    ///
    ///   2. Native messaging mode (no arguments): this is how Chrome actually launches the
    ///      process. Chrome writes exactly one JSON message to our stdin, expects exactly one
    ///      JSON message back on stdout, and then closes the pipes. See NativeMessaging.cs for
    ///      the wire format.
    /// </summary>
    public static class Program
    {
        public const string Version = "1.0.0";

        public static int Main(string[] args)
        {
            try
            {
                // Chrome ALWAYS launches a native messaging host with extra positional
                // arguments - at minimum the calling extension's origin
                // ("chrome-extension://<id>/"), so the host can identify who's talking to it.
                // Those are never dash-prefixed. Only treat this as a CLI invocation (--register,
                // --check-extension, etc.) when args[0] actually looks like one of our own
                // flags; anything else (including Chrome's origin argument) must fall through
                // to the native messaging stdio loop, or Chrome can never talk to us at all.
                if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    return RunCli(args);
                }

                RunNativeMessagingLoop();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal error: " + ex);
                return 1;
            }
        }

        private static int RunCli(string[] args)
        {
            // CLI mode has no per-request "enableDebug" flag to read (see HandleOneRequest -
            // that only exists for the native-messaging loop below), and it's the MSI's own
            // install-time custom action and a human troubleshooting registration by hand that
            // run this path - rare, deliberate actions where the whole point is watching what
            // happened, not the high-frequency per-click noise the debug checkbox exists to
            // suppress. So CLI mode always logs fully, regardless of the extension's own
            // "Enable debug logging" setting (which it never even sees).
            Logger.IsDebugEnabled = true;

            switch (args[0])
            {
                case "--register":
                    // args[1], when present, is an extension id override (see the MSI's
                    // EXTENSIONID property in Package.wxs) - lets someone installing locally
                    // with an unpacked/dev-loaded extension point the native host at THEIR
                    // extension's id instead of the published Chrome Web Store one.
                    Registration.RegisterHost(args.Length > 1 ? args[1] : null);
                    return 0;
                case "--unregister":
                    Registration.UnregisterHost();
                    return 0;
                case "--check-extension":
                    Registration.CheckExtensionInstalled(openStoreIfMissing: true);
                    return 0;
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[0]}");
                    return 1;
            }
        }

        // -----------------------------------------------------------------
        // Native messaging: stay attached and keep handling requests until Chrome actually
        // closes the connection, instead of exiting after exactly one.
        // -----------------------------------------------------------------

        private static void RunNativeMessagingLoop()
        {
            // Historically this read exactly one request, wrote exactly one response, and
            // exited - which meant Chrome launched a brand new AWS-Login-Helper-Host-Chrome.exe process
            // for every single click. On a machine with cloud-delivered antivirus protection,
            // EVERY one of those fresh launches got scanned before being allowed to run,
            // adding several seconds to every single click, every time, with no way to fix
            // that from inside our own timing (see host.log's own numbers always looking
            // fast - that scan happens before Main() ever starts). The extension now opens
            // one long-lived connection via chrome.runtime.connectNative and keeps reusing it
            // for as long as its service worker stays alive, instead of one-shot
            // sendNativeMessage calls - so this loop keeps handling further requests over the
            // same stdin/stdout pipes until Chrome actually disconnects that port, reusing
            // this one already-launched (and already-scanned) process across many clicks
            // instead of paying that cost on every one of them.
            //
            // Each request is dispatched onto its own background Task rather than awaited
            // in-line, so a slow request (opening a new isolated window can genuinely take a
            // few seconds between cookie sync and letting redirects settle) doesn't block
            // reading - and therefore starting - the NEXT one if the user clicks a second
            // account/role link before the first has finished. NativeMessaging.WriteMessage's
            // internal lock keeps concurrent responses from corrupting each other on the
            // shared stdout stream.
            Logger.Log($"==== native messaging loop starting (pid {Environment.ProcessId}) ====");

            using (Stream stdin = Console.OpenStandardInput())
            using (Stream stdout = Console.OpenStandardOutput())
            {
                var pending = new System.Collections.Concurrent.ConcurrentBag<Task>();

                while (true)
                {
                    string requestJson;
                    try
                    {
                        requestJson = NativeMessaging.ReadMessage(stdin);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"native messaging loop: ReadMessage failed, exiting - {ex}");
                        break;
                    }

                    if (requestJson == null)
                    {
                        Logger.Log("native messaging loop: pipe closed (port disconnected), exiting");
                        break;
                    }

                    pending.Add(Task.Run(() => HandleOneRequest(requestJson, stdout)));
                }

                // Give any still-in-flight requests a chance to finish and write their
                // response before the pipes get torn down - dropping a response the
                // extension is still waiting on would just look like another silent failure.
                try
                {
                    Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(25));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"native messaging loop: error waiting for in-flight requests - {ex}");
                }
            }

            // Used to also wait here for any background CDP "keep the banner script alive"
            // sessions to finish before actually exiting - that whole mechanism (and the
            // fragility it was patching around) is gone now that account/role identification
            // is shown via a real loaded content script instead (see
            // CdpClient.SetCookiesAndNavigateAsync and extension/content.js). Nothing outlives
            // this loop any more, so it's safe to exit as soon as the pipe closes and
            // in-flight requests have been given their chance above.
            Logger.Log("==== native messaging loop exiting ====");
        }

        private static void HandleOneRequest(string requestJson, Stream stdout)
        {
            JObject response;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string action = null;
            JToken requestId = null;
            try
            {
                JObject request = JObject.Parse(requestJson);
                action = (string)request["action"];
                requestId = request["__requestId"];

                // Rides along on every request rather than a separate "set debug mode"
                // message - background.js already reads the "enable-debug" checkbox into its
                // own IsDebug for its own console.log gating (see content.js/background.js),
                // so this just forwards that same value here too. See Logger.cs's own doc
                // comment for why this is a plain static instead of anything fancier.
                Logger.IsDebugEnabled = (bool?)request["enableDebug"] ?? false;

                Logger.Log($"---- request start: {action} ----");
                response = Dispatch(request).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogError($"request FAILED: {action} - {ex}");
                response = new JObject { ["ok"] = false, ["error"] = ex.Message };
            }
            finally
            {
                Logger.Log($"---- request end: {action} total={sw.ElapsedMilliseconds}ms ----");
            }

            // Echoed back so the extension side (juggling many requests over one long-lived
            // port now, instead of one process per request) can match this response to the
            // right pending Promise.
            if (requestId != null)
            {
                response["__requestId"] = requestId;
            }

            try
            {
                NativeMessaging.WriteMessage(stdout, response.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                // The pipe may already be gone if Chrome disconnected while this request was
                // still running - nothing to do but log it; the extension side handles a
                // disconnect on its own (see background.js's onDisconnect listener).
                Logger.LogError($"request {action}: failed to write response - {ex}");
            }
        }

        private static async Task<JObject> Dispatch(JObject request)
        {
            string action = (string)request["action"];
            var chrome = new ChromeManager();

            switch (action)
            {
                case "ping":
                    return new JObject { ["ok"] = true, ["version"] = Version };

                case "prewarmChrome":
                    await chrome.EnsureColdStartPlaceholderWarmAsync();
                    return new JObject { ["ok"] = true };

                case "syncAndOpen":
                    return await HandleSyncAndOpen(chrome, request);

                case "syncAllProfiles":
                    return await HandleSyncAllProfiles(chrome, request);

                case "listProfiles":
                    return HandleListProfiles(chrome);

                case "deleteProfile":
                    return await HandleDeleteProfile(chrome, request);

                default:
                    return new JObject { ["ok"] = false, ["error"] = $"Unknown action '{action}'" };
            }
        }

        private static List<CookieDto> ParseCookies(JObject request)
        {
            var cookies = new List<CookieDto>();
            var array = request["cookies"] as JArray;
            if (array == null) return cookies;

            foreach (var item in array)
            {
                cookies.Add(new CookieDto
                {
                    Domain = (string)item["domain"],
                    Name = (string)item["name"],
                    Value = (string)item["value"],
                    Path = (string)item["path"],
                    Secure = (bool?)item["secure"] ?? false,
                    HttpOnly = (bool?)item["httpOnly"] ?? false,
                    SameSite = (string)item["sameSite"],
                    Session = (bool?)item["session"],
                    ExpirationDate = (double?)item["expirationDate"],
                });
            }
            return cookies;
        }

        private static string SanitizeProfileKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unknown-profile";
            var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '-').ToArray();
            var cleaned = new string(chars).Trim('-');
            return cleaned.Length > 0 ? cleaned : "unknown-profile";
        }

        private static async Task<JObject> HandleSyncAndOpen(ChromeManager chrome, JObject request)
        {
            string profileKey = SanitizeProfileKey((string)request["profileKey"]);
            // account_id | account_name | role_name - the full banner label. RecordProfileLabel
            // below is what ChromeManager.ApplyKnownProfileDisplayNames later reads (the Chrome
            // profile's own display name, shown in chrome://settings/manageProfile and the
            // profile switcher), but the banner/title the user actually sees on the console
            // page itself comes from the script CdpClient.SetCookiesAndNavigateAsync injects
            // directly via CDP - see that method's own doc comment for the full mechanism and
            // why it's back to this instead of a loaded extension.
            string displayLabel = (string)request["displayLabel"] ?? profileKey;
            // Just the account name, for the tab/window title - the banner still shows the
            // full displayLabel. Falls back to the full label if an older extension build
            // ever calls us without it.
            string titleLabel = (string)request["accountName"];
            string url = (string)request["url"];
            var cookies = ParseCookies(request);

            if (string.IsNullOrEmpty(url))
            {
                return new JObject { ["ok"] = false, ["error"] = "Missing url" };
            }

            chrome.RecordProfileLabel(profileKey, displayLabel);

            var (wsUrl, port, targetId) = await chrome.EnsureTabForProfileAsync(profileKey);
            await chrome.Cdp.SetCookiesAndNavigateAsync(wsUrl, cookies, url, displayLabel, titleLabel);

            // Covers every LATER full navigation within this same tab - SetCookiesAndNavigateAsync
            // above only carries the banner/title through the very first landing. See
            // CdpClient.StartBannerWatcher's own doc comment for why this is a polling design.
            // Best-effort: re-reads the tab's own current url (rather than reusing the pre-
            // redirect "url" variable above) so the watcher's "last seen" baseline matches
            // whatever host AWS's federation chain actually settled on.
            string settledUrl = url;
            try
            {
                var targets = await chrome.Cdp.ListPageTargetsAsync(port);
                var settled = targets.FirstOrDefault(t => t.Id == targetId);
                if (settled.Id != null) settledUrl = settled.Url;
            }
            catch (Exception ex)
            {
                Logger.LogError($"HandleSyncAndOpen: failed to read settled url before starting banner watcher (non-fatal) - {ex}");
            }
            chrome.Cdp.StartBannerWatcher(port, targetId, displayLabel, titleLabel, settledUrl);

            return new JObject { ["ok"] = true, ["profileKey"] = profileKey };
        }

        private static async Task<JObject> HandleSyncAllProfiles(ChromeManager chrome, JObject request)
        {
            var cookies = ParseCookies(request);
            var errors = new JArray();
            int synced = 0;

            foreach (var profileKey in chrome.ListProfileDirectories())
            {
                try
                {
                    var (wsUrl, port, targetId) = await chrome.EnsureTabForProfileAsync(profileKey);
                    await chrome.Cdp.SetCookiesAndNavigateAsync(wsUrl, cookies, "about:blank");
                    await chrome.Cdp.CloseTargetAsync(port, targetId);
                    synced++;
                }
                catch (Exception ex)
                {
                    errors.Add(new JObject { ["profileKey"] = profileKey, ["error"] = ex.Message });
                }
            }

            return new JObject { ["ok"] = true, ["synced"] = synced, ["errors"] = errors };
        }

        private static JObject HandleListProfiles(ChromeManager chrome)
        {
            var profiles = new JArray();
            foreach (var (profileKey, displayLabel) in chrome.ListKnownProfiles())
            {
                profiles.Add(new JObject { ["profileKey"] = profileKey, ["displayLabel"] = displayLabel });
            }
            return new JObject { ["ok"] = true, ["profiles"] = profiles };
        }

        private static async Task<JObject> HandleDeleteProfile(ChromeManager chrome, JObject request)
        {
            string profileKey = (string)request["profileKey"];
            if (string.IsNullOrEmpty(profileKey))
            {
                return new JObject { ["ok"] = false, ["error"] = "Missing profileKey" };
            }

            var (ok, error) = await chrome.DeleteProfileAsync(profileKey);
            return new JObject { ["ok"] = ok, ["error"] = error };
        }
    }
}
