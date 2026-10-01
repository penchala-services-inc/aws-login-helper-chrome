using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Owns the "dedicated Chrome instance" that stands in for Firefox containers.
    ///
    /// IMPORTANT DESIGN NOTE (see top-level README for the full rationale): this does NOT
    /// touch the user's everyday Chrome profiles or windows. It launches Chrome pointed at a
    /// completely separate --user-data-dir under
    /// %LOCALAPPDATA%\AWS-Login-Helper-Host-Chrome, where every AWS account/role gets its own
    /// --profile-directory. That is the only way to
    /// reliably keep a Chrome DevTools Protocol debugging port attached across the whole
    /// session without fighting Chrome's single-instance behavior on the user's main browser
    /// process (which almost never has remote debugging enabled, and can't be turned on for
    /// an already-running process).
    ///
    /// HISTORY: this went through THREE different mechanisms for showing which account/role a
    /// window represents, and has now come back to the first one:
    ///   1. (this one) CDP-injected banner/title, via CdpClient.SetCookiesAndNavigateAsync -
    ///      Page.addScriptToEvaluateOnNewDocument. No browser flags, works in any profile.
    ///   2. The dedicated instance's own Chrome profile display name (Local State rename,
    ///      see ApplyKnownProfileDisplayNames below) used ALONE. Reverted for being too
    ///      subtle - Chrome's toolbar shows only an avatar icon by default, no visible text.
    ///   3. A real loaded content-script extension, via --load-extension into each dedicated
    ///      profile. This worked functionally but depended on a command-line switch Google
    ///      removed ENTIRELY starting in Chrome 137 (June 2025) - not a bug on our end, a
    ///      deleted browser capability with no unprivileged replacement. Confirmed dead via
    ///      real testing: chrome-extension://<id>/... came back "blocked" in every dedicated
    ///      window, on a genuinely fresh --user-data-dir per profile too (ruling out Chrome's
    ///      command-line-forwarding quirk as the cause - see the version of this file from
    ///      immediately before this one for that now-moot investigation).
    /// Mechanism 1 (restored here) is the only one of the three with no dependency on a
    /// browser flag or a human manually toggling Developer Mode, so it's what's left. Its own
    /// known fragility (tasks doc history: banner lost on later navigations if this process's
    /// own CDP session doesn't survive long enough, and keeping the Page domain enabled for
    /// too long disabling back/forward cache and triggering AWS's own "signed out" screen) is
    /// handled in CdpClient - see its own doc comments for exactly how.
    /// </summary>
    internal sealed class ChromeManager
    {
        private static readonly string RootDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AWS-Login-Helper-Host-Chrome");

        private static readonly string ChromeUserDataDir = Path.Combine(RootDir, "ChromeProfiles");
        private static readonly string PortFile = Path.Combine(RootDir, "cdp-port.txt");
        private static readonly string LabelsFile = Path.Combine(RootDir, "profile-labels.json");

        // Program.cs's native-messaging loop dispatches every incoming request onto its own
        // Task.Run, so "Delete selected" firing N deleteProfile requests via Promise.all lands
        // here as N genuinely concurrent calls in this same process. Without a lock, concurrent
        // LoadLabels+File.WriteAllText read-modify-write cycles on profile-labels.json (from
        // RecordProfileLabel/RemoveProfileLabel) raced each other for the file's exclusive
        // handle - observed directly as "The process cannot access the file ... because it is
        // being used by another process" on most of a large batch delete. Local State (renamed
        // via ApplyKnownProfileDisplayNames/RemoveProfileDisplayName) is exposed to the exact
        // same race whenever more than one of those calls can run at once, so it gets its own
        // lock below rather than reusing this one - the two files are never touched together,
        // and a single shared lock would serialize unrelated work for no reason.
        private static readonly object LabelsLock = new object();
        private static readonly object LocalStateLock = new object();

        // Tracks the currently-running shared dedicated Chrome process (if any) so a brand-new
        // profile's first-ever open can force a cold restart - see EnsureTabForProfileAsync's
        // isBrandNewProfile branch and CloseSharedInstanceAsync below for why that's needed:
        // ApplyKnownProfileDisplayNames only works while nothing owns this user-data-dir yet,
        // but ChromeManager itself is re-created fresh per request (see Program.cs's Dispatch),
        // so this can't be an ordinary instance field - it has to survive across requests the
        // same way PortFile's on-disk state does. Set on every cold launch (both here and in
        // EnsureColdStartPlaceholderWarmAsync), cleared once that process is confirmed gone.
        private static Process _sharedInstanceProcess;

        // Serializes every COLD launch (first launch of the whole shared instance, or a forced
        // restart for a brand-new profile) - never taken for the common warm/singleton-forward
        // path, which stays exactly as concurrent as it always was. Without this, two requests
        // landing at nearly the same moment (e.g. the portal's prewarm ping racing a real first
        // click, or two different brand-new account/role links clicked back to back) could each
        // decide independently that nothing is running yet and launch two competing instances,
        // or kill the process the other one just started.
        private static readonly SemaphoreSlim LaunchLock = new SemaphoreSlim(1, 1);

        // A throwaway --profile-directory that never shows a real account/role and is never
        // navigated anywhere real - its only job is to force the shared dedicated Chrome
        // instance to actually be running (with its CDP port already known and alive) before
        // the user ever clicks a real account/role link, so that first real click can take the
        // fast "singleton-forwarded" path (a few ms) instead of paying LaunchChrome+
        // WaitUntilAliveAsync (~600ms+) plus the GetRunningCdpPortAsync -> null delay that
        // shows up specifically on a cold start. See EnsureColdStartPlaceholderWarmAsync.
        public const string ColdStartPlaceholderProfileKey = "aws-login-helper-cold-start";

        private readonly CdpClient _cdp = new CdpClient();

        public ChromeManager()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(ChromeUserDataDir);
        }

        // -----------------------------------------------------------------
        // Profile label bookkeeping (profileKey -> human readable label)
        // -----------------------------------------------------------------

        public void RecordProfileLabel(string profileKey, string displayLabel)
        {
            lock (LabelsLock)
            {
                JObject labels = LoadLabels();
                labels[profileKey] = new JObject
                {
                    ["displayLabel"] = displayLabel,
                    ["lastUsedUtc"] = DateTime.UtcNow.ToString("o"),
                };
                File.WriteAllText(LabelsFile, labels.ToString());
            }
        }

        public List<(string ProfileKey, string DisplayLabel)> ListKnownProfiles()
        {
            lock (LabelsLock)
            {
                JObject labels = LoadLabels();
                var result = new List<(string, string)>();
                foreach (var prop in labels.Properties())
                {
                    string label = (string)prop.Value["displayLabel"] ?? prop.Name;
                    result.Add((prop.Name, label));
                }
                return result;
            }
        }

        private JObject LoadLabels()
        {
            if (!File.Exists(LabelsFile)) return new JObject();
            try
            {
                return JObject.Parse(File.ReadAllText(LabelsFile));
            }
            catch
            {
                return new JObject();
            }
        }

        private void RemoveProfileLabel(string profileKey)
        {
            lock (LabelsLock)
            {
                JObject labels = LoadLabels();
                if (labels.Remove(profileKey))
                {
                    File.WriteAllText(LabelsFile, labels.ToString());
                }
            }
        }


        // -----------------------------------------------------------------
        // Chrome discovery + launching
        // -----------------------------------------------------------------

        public static string FindChromeExe()
        {
            var candidates = new List<string>
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }

            // Fall back to the "App Paths" registry entries Chrome's installer registers -
            // works for both machine-wide and per-user ("no admin") Chrome installs.
            foreach (var root in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default))
                    using (var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                    {
                        var value = key?.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(value) && File.Exists(value)) return value;
                    }
                }
                catch
                {
                    // ignore and try the next hive
                }
            }

            throw new FileNotFoundException(
                "Could not locate chrome.exe. Install Google Chrome, or set the CHROME_EXE_PATH environment variable.");
        }

        private static int FindFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private Process LaunchChrome(string profileDirectory, int? remoteDebuggingPort, string url, IEnumerable<string> extraArgs = null, bool maximized = true)
        {
            string chromeExe = FindChromeExe();
            var args = new List<string>
            {
                $"--user-data-dir=\"{ChromeUserDataDir}\"",
                $"--profile-directory=\"{profileDirectory}\"",
                "--no-first-run",
                "--no-default-browser-check",
            };

            if (remoteDebuggingPort.HasValue)
            {
                args.Add($"--remote-debugging-port={remoteDebuggingPort.Value}");
            }

            // Chrome has no per-window-size memory for a profile it's never opened before, so
            // every brand new account/role profile-directory was opening at Chrome's own
            // default (small, not maximized) window size, on both the fresh cold-start launch
            // AND the singleton-forwarded path used for every account/role after the first -
            // this flag is re-read by Chrome for each new profile-directory window even when
            // forwarded into an already-running instance, the same way --profile-directory
            // itself is. Skipped when maximized=false - only the hidden cold-start placeholder
            // (see EnsureColdStartPlaceholderWarmAsync) passes that, since maximizing a window
            // that's deliberately positioned off-screen would be pointless at best. Belt-and-
            // suspenders only: CdpClient.MaximizeWindowAsync sets this authoritatively over
            // CDP right after, since testing showed this flag doesn't always survive being
            // forwarded into an already-running instance.
            if (maximized)
            {
                args.Add("--start-maximized");
            }

            if (extraArgs != null)
            {
                args.AddRange(extraArgs);
            }

            args.Add($"\"{url}\"");

            var psi = new ProcessStartInfo
            {
                FileName = chromeExe,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,

                // IMPORTANT: this process (AWS-Login-Helper-Host-Chrome.exe) is itself talking to Chrome
                // over stdin/stdout using the native messaging protocol. Process.Start, by
                // default, makes a spawned child inherit the PARENT's standard handles when
                // they aren't explicitly redirected - so without this, the chrome.exe we launch
                // here would inherit a copy of the very pipe handles Chrome is using to talk to
                // US, keeping that pipe open (and possibly interleaving chrome.exe's own stray
                // output into it) even after this host process exits and responds. That reads
                // to Chrome as "Error when communicating with the native messaging host." Giving
                // the child its own redirected (and immediately drained/closed) handles fully
                // decouples it from our native-messaging pipes.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process == null) return null;

            // We don't care about chrome.exe's own stdout/stderr - just drain them
            // asynchronously so it never blocks trying to write to a full pipe buffer.
            process.OutputDataReceived += (_, __) => { };
            process.ErrorDataReceived += (_, __) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // chrome.exe never reads stdin; close our write end immediately so it sees a clean
            // EOF instead of an idle pipe.
            try { process.StandardInput.Close(); } catch { /* best effort */ }

            return process;
        }

        /// <summary>
        /// Writes every known profileKey -> displayLabel mapping (account_id | account_name |
        /// role_name) into this dedicated instance's own "Local State" file, so Chrome's own
        /// profile switcher shows something meaningful instead of an auto-assigned "Person N"
        /// for each isolated profile. Purely a nice-to-have - the CDP-injected in-page banner
        /// (see CdpClient.SetCookiesAndNavigateAsync) is the primary identification; this just
        /// also helps in Chrome's own profile switcher UI, which the in-page banner doesn't
        /// reach.
        ///
        /// The actual risk with editing Local State is that Chrome rewrites that file on its
        /// own whenever it's running, so a concurrent edit from here could be lost or corrupt
        /// the file. This method is only ever called from the "no Chrome process owns this
        /// user-data-dir right now" branch of EnsureTabForProfileAsync, so there is nothing
        /// else touching the file at the same time. The tradeoff: a brand new account/role
        /// opened via singleton-forwarding into an ALREADY-running dedicated instance won't get
        /// its friendly name until the next time the whole instance is cold-started (e.g. after
        /// closing all isolated windows) - acceptable, since the console banner inside the AWS
        /// page already identifies it in the meantime.
        /// </summary>
        private void ApplyKnownProfileDisplayNames()
        {
            string localStatePath = Path.Combine(ChromeUserDataDir, "Local State");
            lock (LocalStateLock)
            {
                try
                {
                    JObject root = File.Exists(localStatePath)
                        ? JObject.Parse(File.ReadAllText(localStatePath))
                        : new JObject();

                    var profile = root["profile"] as JObject;
                    if (profile == null)
                    {
                        profile = new JObject();
                        root["profile"] = profile;
                    }

                    var infoCache = profile["info_cache"] as JObject;
                    if (infoCache == null)
                    {
                        infoCache = new JObject();
                        profile["info_cache"] = infoCache;
                    }

                    foreach (var (profileKey, displayLabel) in ListKnownProfiles())
                    {
                        var entry = infoCache[profileKey] as JObject ?? new JObject();
                        entry["name"] = displayLabel;
                        entry["shortcut_name"] = displayLabel;
                        // Chrome only displays the "name" field above when it believes the name
                        // has been explicitly set. Every auto-created profile starts with
                        // is_using_default_name = true, which tells Chrome to IGNORE "name" and
                        // recompute its own "Person N" for display instead - this flag, not the
                        // name field itself, is why the rename wasn't showing up.
                        entry["is_using_default_name"] = false;
                        infoCache[profileKey] = entry;
                    }

                    File.WriteAllText(localStatePath, root.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch (Exception ex)
                {
                    // Best effort only - a naming failure must never block actually opening the
                    // profile, so swallow (but log) anything that goes wrong here.
                    Logger.LogError($"ApplyKnownProfileDisplayNames failed (non-fatal): {ex}");
                }
            }
        }

        /// <summary>
        /// Removes one profileKey's entry from Local State's info_cache - the flip side of
        /// ApplyKnownProfileDisplayNames above, used when actually deleting a profile (see
        /// DeleteProfileAsync). Same safety rule applies and matters MORE here: this is only
        /// ever called from a branch that already confirmed the shared instance isn't running,
        /// never while Chrome might be concurrently rewriting this same file.
        /// </summary>
        private void RemoveProfileDisplayName(string profileKey)
        {
            string localStatePath = Path.Combine(ChromeUserDataDir, "Local State");
            lock (LocalStateLock)
            {
                if (!File.Exists(localStatePath)) return;
                try
                {
                    JObject root = JObject.Parse(File.ReadAllText(localStatePath));
                    var infoCache = root["profile"]?["info_cache"] as JObject;
                    if (infoCache != null && infoCache.Remove(profileKey))
                    {
                        File.WriteAllText(localStatePath, root.ToString(Newtonsoft.Json.Formatting.None));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"RemoveProfileDisplayName[{profileKey}] failed (non-fatal): {ex}");
                }
            }
        }

        private async Task<int?> GetRunningCdpPortAsync()
        {
            if (!File.Exists(PortFile)) return null;
            if (!int.TryParse(File.ReadAllText(PortFile).Trim(), out int port)) return null;
            return await _cdp.IsAliveAsync(port) ? port : (int?)null;
        }

        /// <summary>
        /// True the first time this profileKey is ever opened (its --profile-directory
        /// subfolder doesn't exist on disk yet) - see EnsureTabForProfileAsync's own use of
        /// this for why that moment matters more than any other.
        /// </summary>
        private bool IsBrandNewProfile(string profileKey) =>
            profileKey != ColdStartPlaceholderProfileKey &&
            !Directory.Exists(Path.Combine(ChromeUserDataDir, profileKey));

        /// <summary>
        /// Ensures the dedicated Chrome instance is running with the given profile open on an
        /// about:blank tab, and returns that tab's CDP WebSocket URL so the caller can inject
        /// cookies before navigating it anywhere real.
        ///
        /// A brand-new profileKey (never opened before) forces a COLD RESTART of the whole
        /// shared instance even if it's already warm and running other accounts/roles, rather
        /// than the usual fast singleton-forward. This is the deliberate cost of making the OS-
        /// level profile rename (ApplyKnownProfileDisplayNames) actually stick: that method can
        /// only safely edit Local State while nothing owns this user-data-dir, which is never
        /// true for a singleton-forwarded open once the cold-start placeholder has already
        /// warmed the instance - without this, a brand-new account/role's display name would
        /// never take effect until the whole instance happened to restart on its own. The
        /// trade-off, chosen deliberately over leaving the OS name wrong: this profile's first
        /// open pays the cold-start delay again (roughly the same ~600ms+ as a genuinely cold
        /// instance), and any OTHER isolated windows already open in the shared instance get
        /// closed and have to be reopened - both real, user-visible costs, accepted because the
        /// alternative (Person 2/Person 3 forever) was worse. Every LATER open of this same
        /// profileKey goes back to the normal fast singleton-forward path, same as before.
        /// </summary>
        public async Task<(string WebSocketUrl, int Port, string TargetId)> EnsureTabForProfileAsync(string profileKey)
        {
            var sw = Stopwatch.StartNew();
            int? existingPort = await GetRunningCdpPortAsync();
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: GetRunningCdpPortAsync -> {(existingPort?.ToString() ?? "null")} ({sw.ElapsedMilliseconds}ms)");

            if (existingPort != null && !IsBrandNewProfile(profileKey))
            {
                // Common case: instance already warm, and we've opened this exact profileKey
                // before - no lock needed here at all, same as before this change.
                return await ForwardIntoRunningInstanceAsync(profileKey, existingPort.Value);
            }

            // Either nothing is running yet, or this profileKey is brand new and the instance
            // needs restarting for its rename to stick - both paths launch fresh, so both go
            // through LaunchLock to serialize against any other cold launch/restart happening
            // at nearly the same moment (see LaunchLock's own doc comment).
            await LaunchLock.WaitAsync();
            try
            {
                // Re-check under the lock: another request could have already warmed (or
                // restarted) the instance while this one was waiting its turn, in which case
                // there's nothing left to do here but the normal singleton-forward.
                existingPort = await GetRunningCdpPortAsync();
                bool brandNew = IsBrandNewProfile(profileKey);

                if (existingPort != null && brandNew)
                {
                    Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: brand-new profile while instance is warm - forcing cold restart so its display name sticks");
                    await CloseSharedInstanceAsync();
                    existingPort = null;
                }

                if (existingPort != null)
                {
                    return await ForwardIntoRunningInstanceAsync(profileKey, existingPort.Value);
                }

                return await ColdLaunchTabAsync(profileKey);
            }
            finally
            {
                LaunchLock.Release();
            }
        }

        /// <summary>
        /// Kills whatever shared dedicated Chrome process is currently tracked (if any) and
        /// clears PortFile, so the next GetRunningCdpPortAsync check correctly reports "nothing
        /// running" and a fresh cold launch can proceed. Callers must already hold LaunchLock -
        /// this only tears the instance down, it never launches a replacement.
        /// </summary>
        private async Task CloseSharedInstanceAsync()
        {
            try
            {
                if (_sharedInstanceProcess != null && !_sharedInstanceProcess.HasExited)
                {
                    // entireProcessTree=true: chrome.exe's own child renderer/GPU/utility
                    // processes would otherwise be left as orphans - killing just the main
                    // process doesn't take them down with it.
                    _sharedInstanceProcess.Kill(entireProcessTree: true);
                    _sharedInstanceProcess.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"CloseSharedInstanceAsync: failed to kill previous instance (non-fatal, still relaunching): {ex}");
            }
            finally
            {
                _sharedInstanceProcess = null;
                try { if (File.Exists(PortFile)) File.Delete(PortFile); } catch { /* best effort */ }
            }

            // Brief grace period for Windows to actually release the profile's lock files
            // (Local State, the profile directories' own lock files) before relaunching -
            // Kill() returning/WaitForExit() completing doesn't guarantee every file handle
            // the OS was holding for that process has been released yet.
            await Task.Delay(300);
        }

        /// <summary>
        /// The cold-launch path: nothing currently owns this user-data-dir, so it's safe to
        /// rewrite Local State before starting Chrome fresh. Shared by EnsureTabForProfileAsync
        /// (launching straight into a real profileKey) - EnsureColdStartPlaceholderWarmAsync
        /// has its own near-identical version since it launches the throwaway placeholder
        /// profile instead of a real one. Caller must already hold LaunchLock.
        /// </summary>
        private async Task<(string WebSocketUrl, int Port, string TargetId)> ColdLaunchTabAsync(string profileKey)
        {
            var sw = Stopwatch.StartNew();

            // Only ever touch Local State while we've just confirmed (via the caller's
            // GetRunningCdpPortAsync check, under LaunchLock) that no Chrome process owns this
            // user-data-dir yet. Chrome itself rewrites Local State whenever it's running
            // (profile switches, window close, etc.), so editing it concurrently with a live
            // process risks a lost write or a corrupt file - editing it here, in the narrow
            // window before we launch anything, is the only place this is actually safe.
            ApplyKnownProfileDisplayNames();
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: ApplyKnownProfileDisplayNames ({sw.ElapsedMilliseconds}ms)");

            sw.Restart();
            int port = FindFreeTcpPort();
            _sharedInstanceProcess = LaunchChrome(profileKey, port, "about:blank");
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: LaunchChrome (fresh, port {port}) returned ({sw.ElapsedMilliseconds}ms)");

            sw.Restart();
            await _cdp.WaitUntilAliveAsync(port, TimeSpan.FromSeconds(20));
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: WaitUntilAliveAsync ({sw.ElapsedMilliseconds}ms)");
            File.WriteAllText(PortFile, port.ToString());

            sw.Restart();
            // Fresh instance: whatever single page target exists is the one we just opened.
            var targets = await WaitForAtLeastOneTargetAsync(port);
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: WaitForAtLeastOneTargetAsync ({sw.ElapsedMilliseconds}ms)");

            var target = targets[0];
            string ws = await _cdp.GetWebSocketUrlAsync(port, target.Id);
            return (ws, port, target.Id);
        }

        /// <summary>
        /// The fast path: the shared instance is already running and this profileKey has
        /// already been opened before (its rename, if any, is already baked into Local State
        /// from an earlier cold launch) - just forward a new window into it. No lock needed;
        /// this is exactly what ran unconditionally before this change.
        /// </summary>
        private async Task<(string WebSocketUrl, int Port, string TargetId)> ForwardIntoRunningInstanceAsync(string profileKey, int port)
        {
            var sw = Stopwatch.StartNew();
            var before = new HashSet<string>((await _cdp.ListPageTargetsAsync(port)).Select(t => t.Id));
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: ListPageTargetsAsync (before snapshot, {before.Count} targets) ({sw.ElapsedMilliseconds}ms)");

            sw.Restart();
            LaunchChrome(profileKey, null, "about:blank"); // singleton-forwarded into the running instance
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: LaunchChrome (singleton-forwarded) returned ({sw.ElapsedMilliseconds}ms)");

            sw.Restart();
            string newId = await _cdp.WaitForNewTargetAsync(port, before, TimeSpan.FromSeconds(15));
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: WaitForNewTargetAsync -> {newId} ({sw.ElapsedMilliseconds}ms)");

            string ws = await _cdp.GetWebSocketUrlAsync(port, newId);
            return (ws, port, newId);
        }

        // Off-screen position + a 1x1 size, rather than --headless or --start-minimized. This
        // exact idea (a hidden prewarm window) was tried once before and reverted because it
        // showed up as a small visible "about:blank" window - --start-minimized still flashes
        // onto the screen/taskbar briefly before minimizing, and --headless can never later
        // become a normal visible window for a real account click, which is required here
        // since real clicks reuse this same running instance (singleton-forwarded). Moving the
        // window entirely off the visible desktop avoids both problems: it's a fully normal,
        // interactive Chrome window (so later real windows opened in this same instance behave
        // exactly as they always have), it just never occupies any visible screen space.
        private static readonly string[] HiddenPlacementArgs = { "--window-position=-32000,-32000", "--window-size=1,1" };

        /// <summary>
        /// Warms the shared dedicated Chrome instance ahead of time, using a throwaway hidden
        /// window/profile that no real account ever uses, so the FIRST real account/role click
        /// of a session doesn't have to pay for launching Chrome from cold. Safe to call
        /// anytime (e.g. every time the user lands on the SSO portal page) - if the instance is
        /// already running (a real account window already open, or an earlier call already
        /// warmed it), this is just the same GetRunningCdpPortAsync check EnsureTabForProfileAsync
        /// already does and returns immediately.
        /// </summary>
        public async Task EnsureColdStartPlaceholderWarmAsync()
        {
            // Same LaunchLock as EnsureTabForProfileAsync's own cold-launch path - without it,
            // this prewarm call and a genuinely concurrent first real click could each see
            // "nothing running yet" and race to launch two competing instances.
            await LaunchLock.WaitAsync();
            try
            {
                var sw = Stopwatch.StartNew();
                int? existingPort = await GetRunningCdpPortAsync();
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: GetRunningCdpPortAsync -> {(existingPort?.ToString() ?? "null")} ({sw.ElapsedMilliseconds}ms)");

                if (existingPort != null)
                {
                    Logger.Log("EnsureColdStartPlaceholderWarmAsync: instance already warm, nothing to do");
                    return;
                }

                // Same reasoning as ColdLaunchTabAsync: only safe to touch Local State because
                // we just confirmed nothing owns this user-data-dir yet.
                sw.Restart();
                ApplyKnownProfileDisplayNames();
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: ApplyKnownProfileDisplayNames ({sw.ElapsedMilliseconds}ms)");

                sw.Restart();
                int port = FindFreeTcpPort();
                _sharedInstanceProcess = LaunchChrome(ColdStartPlaceholderProfileKey, port, "about:blank", HiddenPlacementArgs, maximized: false);
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: LaunchChrome (hidden placeholder, port {port}) returned ({sw.ElapsedMilliseconds}ms)");

                sw.Restart();
                await _cdp.WaitUntilAliveAsync(port, TimeSpan.FromSeconds(20));
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: WaitUntilAliveAsync ({sw.ElapsedMilliseconds}ms)");
                File.WriteAllText(PortFile, port.ToString());

                Logger.Log("EnsureColdStartPlaceholderWarmAsync: instance is now warm and ready for a real click");
            }
            finally
            {
                LaunchLock.Release();
            }
        }

        private async Task<List<(string Id, string Url)>> WaitForAtLeastOneTargetAsync(int port)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var targets = await _cdp.ListPageTargetsAsync(port);
                if (targets.Count > 0) return targets;
                await Task.Delay(200);
            }
            throw new TimeoutException("Chrome never reported a page target for the newly launched profile.");
        }

        public CdpClient Cdp => _cdp;

        public async Task<int?> TryGetActivePortAsync() => await GetRunningCdpPortAsync();

        public List<string> ListProfileDirectories()
        {
            if (!Directory.Exists(ChromeUserDataDir)) return new List<string>();
            return Directory.GetDirectories(ChromeUserDataDir)
                .Select(Path.GetFileName)
                .Where(name => !name.StartsWith("Crashpad", StringComparison.OrdinalIgnoreCase)
                               && !name.Equals("GrShaderCache", StringComparison.OrdinalIgnoreCase)
                               && !name.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase)
                               // Never sync cookies into the hidden prewarm placeholder or close
                               // its lone tab - it has no real account/role behind it, and
                               // closing its only tab while no other window is open yet would
                               // kill the very instance it exists to keep warm.
                               && !name.Equals(ColdStartPlaceholderProfileKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Deletes one isolated account/role profile: its own --profile-directory subfolder
        /// under the shared ChromeUserDataDir, and its entry in profile-labels.json (so it
        /// stops showing up in the options page's "Isolated AWS profiles" list). This is a
        /// NEW capability - the original Firefox extension never had one; a Firefox container
        /// was always deleted through Firefox's own container-management UI, not anything this
        /// extension built itself. There's no equivalent "just delete the container" UI surface
        /// in Chrome/Edge to defer to, so this does the deletion itself instead.
        ///
        /// Fails cleanly (returns false with a message) rather than throwing when the directory
        /// can't be removed - overwhelmingly the likely cause is a window for this exact profile
        /// still being open, which means Chrome still holds its files locked. options.js surfaces
        /// that message directly, since "close the window first" is something the user can
        /// actually act on, unlike a raw IOException.
        /// </summary>
        public async Task<(bool Ok, string Error)> DeleteProfileAsync(string profileKey)
        {
            if (string.IsNullOrWhiteSpace(profileKey) ||
                profileKey.Equals(ColdStartPlaceholderProfileKey, StringComparison.OrdinalIgnoreCase) ||
                profileKey.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
                profileKey.Contains(".."))
            {
                return (false, "Refusing to delete that profile.");
            }

            string profileDir = Path.Combine(ChromeUserDataDir, profileKey);

            if (Directory.Exists(profileDir))
            {
                try
                {
                    Directory.Delete(profileDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"DeleteProfileAsync[{profileKey}]: Directory.Delete failed - {ex}");
                    return (false, "Could not delete - close any open window for this account/role first, then try again.");
                }
            }

            RemoveProfileLabel(profileKey);

            // Only safe to also prune Local State's own info_cache entry when nothing currently
            // owns this user-data-dir - same restriction ApplyKnownProfileDisplayNames's own doc
            // comment explains. Leaving a stale entry there while the instance IS running is a
            // cosmetic-only gap (an empty "ghost" name Chrome's own profile switcher might still
            // list until the whole instance next cold-starts) - acceptable, rather than risking
            // a corrupt Local State file by editing it concurrently with a live Chrome process.
            if (await TryGetActivePortAsync() == null)
            {
                RemoveProfileDisplayName(profileKey);
            }

            return (true, null);
        }
    }
}
