using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Owns the "dedicated Chrome instance" that stands in for Firefox containers - the macOS
    /// build of this class. Logic is identical to the Windows build's ChromeManager.cs; the
    /// only differences are (1) where Chrome's binary is found (no Windows Registry "App
    /// Paths" equivalent exists on macOS, so this just checks the standard .app bundle
    /// locations), (2) where this host's own data lives (~/Library/Application Support instead
    /// of %LocalAppData%), and (3) building the launch command as an ArgumentList instead of a
    /// single pre-quoted string (see LaunchChrome's own comment for why).
    ///
    /// IMPORTANT DESIGN NOTE (see top-level README for the full rationale): this does NOT
    /// touch the user's everyday Chrome profiles or windows. It launches Chrome pointed at a
    /// completely separate --user-data-dir under
    /// ~/Library/Application Support/AWS-Login-Helper-Host-Chrome, where every AWS
    /// account/role gets its own --profile-directory. That is the only way to reliably keep a
    /// Chrome DevTools Protocol debugging port attached across the whole session without
    /// fighting Chrome's single-instance behavior on the user's main browser process (which
    /// almost never has remote debugging enabled, and can't be turned on for an already-
    /// running process).
    ///
    /// The account/role banner/title mechanism (CDP script injection via
    /// CdpClient.SetCookiesAndNavigateAsync) is identical to the Windows build - see
    /// CdpClient.cs, which is copied here unchanged, for that history. --load-extension is
    /// just as dead on Mac Chrome as on Windows Chrome (same Chromium removal, Chrome 137,
    /// June 2025), so there was never a reason to design this part differently per OS.
    /// </summary>
    internal sealed class ChromeManager
    {
        private static readonly string RootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "AWS-Login-Helper-Host-Chrome");

        private static readonly string ChromeUserDataDir = Path.Combine(RootDir, "ChromeProfiles");
        private static readonly string PortFile = Path.Combine(RootDir, "cdp-port.txt");
        private static readonly string LabelsFile = Path.Combine(RootDir, "profile-labels.json");

        // Same race this guards against on the Windows build: Program.cs's native-messaging
        // loop dispatches every incoming request onto its own Task.Run, so "Delete selected"
        // firing N deleteProfile requests via Promise.all lands here as N genuinely concurrent
        // calls in this same process. Without a lock, concurrent LoadLabels+File.WriteAllText
        // read-modify-write cycles on profile-labels.json race each other for the file's
        // exclusive handle. Local State gets its own lock for the same reason the Windows
        // build's does - the two files are never touched together, and a single shared lock
        // would serialize unrelated work for no reason.
        private static readonly object LabelsLock = new object();
        private static readonly object LocalStateLock = new object();

        // Serializes every COLD launch (first launch of the whole shared instance, or a forced
        // restart for a brand-new profile) - never taken for the common warm/singleton-forward
        // path, which stays exactly as concurrent as it always was.
        private static readonly SemaphoreSlim LaunchLock = new SemaphoreSlim(1, 1);

        // A throwaway --profile-directory that never shows a real account/role and is never
        // navigated anywhere real - its only job is to force the shared dedicated Chrome
        // instance to actually be running (with its CDP port already known and alive) before
        // the user ever clicks a real account/role link.
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

        /// <summary>
        /// No Windows Registry "App Paths" equivalent exists on macOS to fall back to, so this
        /// is just the standard .app bundle locations - the per-machine Applications folder
        /// (the overwhelming majority of real installs) and the per-user one (valid for a
        /// Chrome installed without admin rights, same "no admin needed" spirit as this whole
        /// project). CHROME_EXE_PATH is kept as an explicit override for anyone whose Chrome
        /// genuinely lives somewhere else, same escape hatch the Windows build offers.
        /// </summary>
        public static string FindChromeExe()
        {
            string overridePath = Environment.GetEnvironmentVariable("CHROME_EXE_PATH");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

            var candidates = new List<string>
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }

            throw new FileNotFoundException(
                "Could not locate Google Chrome.app. Install Google Chrome, or set the CHROME_EXE_PATH environment variable to the chrome binary inside it (.../Google Chrome.app/Contents/MacOS/Google Chrome).");
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

            // ArgumentList, not a single manually-quoted Arguments string: .NET's own argv
            // splitting for the string form is a Windows-quoting emulation even on macOS/Linux,
            // which is exactly backwards here - each entry below is passed to the OS as its own
            // already-separate argv element, with no quoting/escaping to get wrong at all. This
            // matters more here than it ever did on Windows, since --user-data-dir points at a
            // path under ~/Library/Application Support, which can contain spaces depending on
            // the local username.
            var psi = new ProcessStartInfo
            {
                FileName = chromeExe,
                UseShellExecute = false,

                // Same reasoning as the Windows build's own LaunchChrome: this process talks to
                // Chrome over stdin/stdout using the native messaging protocol, so the chrome.exe-
                // equivalent child we launch here must NOT inherit a copy of those same pipe
                // handles - giving it its own redirected (and immediately drained/closed) handles
                // fully decouples it.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };

            psi.ArgumentList.Add($"--user-data-dir={ChromeUserDataDir}");
            psi.ArgumentList.Add($"--profile-directory={profileDirectory}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");

            if (remoteDebuggingPort.HasValue)
            {
                psi.ArgumentList.Add($"--remote-debugging-port={remoteDebuggingPort.Value}");
            }

            // Same belt-and-suspenders note as the Windows build: CdpClient.MaximizeWindowAsync
            // sets this authoritatively over CDP right after anyway, since this flag doesn't
            // always survive being forwarded into an already-running instance. Skipped when
            // maximized=false - only the hidden cold-start placeholder passes that.
            if (maximized)
            {
                psi.ArgumentList.Add("--start-maximized");
            }

            if (extraArgs != null)
            {
                foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);
            }

            psi.ArgumentList.Add(url);

            var process = Process.Start(psi);
            if (process == null) return null;

            // We don't care about Chrome's own stdout/stderr - just drain them asynchronously
            // so it never blocks trying to write to a full pipe buffer.
            process.OutputDataReceived += (_, __) => { };
            process.ErrorDataReceived += (_, __) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Chrome never reads stdin; close our write end immediately so it sees a clean EOF
            // instead of an idle pipe.
            try { process.StandardInput.Close(); } catch { /* best effort */ }

            return process;
        }

        /// <summary>
        /// Writes every known profileKey -> displayLabel mapping into this dedicated instance's
        /// own "Local State" file, so Chrome's own profile switcher shows something meaningful
        /// instead of an auto-assigned "Person N" for each isolated profile. Identical to the
        /// Windows build's version of this method - Local State is Chromium's own file, same
        /// JSON schema on every OS it ships on, so only the path leading to it differs.
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
                        entry["is_using_default_name"] = false;
                        infoCache[profileKey] = entry;
                    }

                    File.WriteAllText(localStatePath, root.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"ApplyKnownProfileDisplayNames failed (non-fatal): {ex}");
                }
            }
        }

        /// <summary>
        /// Removes one profileKey's entry from Local State's info_cache - the flip side of
        /// ApplyKnownProfileDisplayNames above, used when actually deleting a profile.
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
        /// subfolder doesn't exist on disk yet).
        /// </summary>
        private bool IsBrandNewProfile(string profileKey) =>
            profileKey != ColdStartPlaceholderProfileKey &&
            !Directory.Exists(Path.Combine(ChromeUserDataDir, profileKey));

        /// <summary>
        /// Ensures the dedicated Chrome instance is running with the given profile open on an
        /// about:blank tab, and returns that tab's CDP WebSocket URL so the caller can inject
        /// cookies before navigating it anywhere real.
        ///
        /// Opening an account/role NEVER restarts a running instance. A restart used to run
        /// for every brand-new profileKey (so Local State could be renamed safely), which
        /// closed every other isolated window the user had open - and even when nothing else
        /// was open it stalled the click for seconds while the old instance shut down and a
        /// new one started. Instead, a brand-new profileKey gets its friendly profile name
        /// pre-seeded into its own Preferences file (SeedProfileDisplayName) and is opened
        /// with the normal fast singleton-forward, same as every already-known profile. Only
        /// when no instance is running at all does this cold-launch one - ApplyKnownProfile
        /// DisplayNames then names every known profile via Local State, which is safe because
        /// nothing owns the user-data-dir yet.
        /// </summary>
        public async Task<(string WebSocketUrl, int Port, string TargetId)> EnsureTabForProfileAsync(string profileKey)
        {
            var sw = Stopwatch.StartNew();
            int? existingPort = await GetRunningCdpPortAsync();
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: GetRunningCdpPortAsync -> {(existingPort?.ToString() ?? "null")} ({sw.ElapsedMilliseconds}ms)");

            if (existingPort != null)
            {
                // Common case: instance already warm - no lock needed here at all.
                if (IsBrandNewProfile(profileKey)) SeedProfileDisplayName(profileKey);
                return await ForwardIntoRunningInstanceAsync(profileKey, existingPort.Value);
            }

            // Nothing is running yet - launch fresh, serialized through LaunchLock against any
            // other cold launch happening at nearly the same moment (see LaunchLock's own doc).
            await LaunchLock.WaitAsync();
            try
            {
                // Re-check under the lock: another request could have already warmed the
                // instance while this one was waiting its turn, in which case there's nothing
                // left to do here but the normal singleton-forward.
                existingPort = await GetRunningCdpPortAsync();
                if (existingPort != null)
                {
                    if (IsBrandNewProfile(profileKey)) SeedProfileDisplayName(profileKey);
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
        /// Pre-seeds a brand-new profile's own Preferences file with its friendly name, so it
        /// can be opened into an already-running instance (no restart) and still come up named
        /// "account | name | role" instead of "Person N". Safe while the instance runs because
        /// that profile directory has never been loaded - Chrome isn't touching it - unlike
        /// Local State, which Chrome rewrites continuously. Never touches an existing
        /// Preferences file. EXPERIMENTAL: whether Chrome adopts profile.name from a
        /// pre-existing Preferences file for a profile it hasn't registered yet is not
        /// guaranteed. If it doesn't, the only effect is the cosmetic "Person N" name, which
        /// ApplyKnownProfileDisplayNames still corrects at the next cold launch - the banner and
        /// tab title identify the account/role either way. Best effort, never throws.
        /// </summary>
        private void SeedProfileDisplayName(string profileKey)
        {
            try
            {
                string profileDir = Path.Combine(ChromeUserDataDir, profileKey);
                string prefsPath = Path.Combine(profileDir, "Preferences");
                if (File.Exists(prefsPath)) return;

                string label = ListKnownProfiles().FirstOrDefault(p => p.ProfileKey == profileKey).DisplayLabel ?? profileKey;

                Directory.CreateDirectory(profileDir);
                var prefs = new JObject
                {
                    ["profile"] = new JObject
                    {
                        ["name"] = label,
                        ["using_default_name"] = false,
                    },
                };
                File.WriteAllText(prefsPath, prefs.ToString(Newtonsoft.Json.Formatting.None), new System.Text.UTF8Encoding(false));
                Logger.Log($"SeedProfileDisplayName[{profileKey}]: wrote Preferences with name '{label}'");
            }
            catch (Exception ex)
            {
                Logger.LogError($"SeedProfileDisplayName[{profileKey}] failed (non-fatal): {ex}");
            }
        }

        /// <summary>
        /// The cold-launch path: nothing currently owns this user-data-dir, so it's safe to
        /// rewrite Local State before starting Chrome fresh. Caller must already hold
        /// LaunchLock.
        /// </summary>
        private async Task<(string WebSocketUrl, int Port, string TargetId)> ColdLaunchTabAsync(string profileKey)
        {
            var sw = Stopwatch.StartNew();

            ApplyKnownProfileDisplayNames();
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: ApplyKnownProfileDisplayNames ({sw.ElapsedMilliseconds}ms)");

            sw.Restart();
            int port = FindFreeTcpPort();
            LaunchChrome(profileKey, port, "about:blank");
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: LaunchChrome (fresh, port {port}) returned ({sw.ElapsedMilliseconds}ms)");

            sw.Restart();
            await _cdp.WaitUntilAliveAsync(port, TimeSpan.FromSeconds(20));
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: WaitUntilAliveAsync ({sw.ElapsedMilliseconds}ms)");
            File.WriteAllText(PortFile, port.ToString());

            sw.Restart();
            var targets = await WaitForAtLeastOneTargetAsync(port);
            Logger.Log($"EnsureTabForProfileAsync[{profileKey}]: WaitForAtLeastOneTargetAsync ({sw.ElapsedMilliseconds}ms)");

            var target = targets[0];
            string ws = await _cdp.GetWebSocketUrlAsync(port, target.Id);
            return (ws, port, target.Id);
        }

        /// <summary>
        /// The fast path: the shared instance is already running - just forward a new window
        /// into it. No lock needed; this is also how a brand-new profileKey opens (after
        /// SeedProfileDisplayName), since nothing ever restarts a running instance.
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

        // Off-screen position + a 1x1 size, same trick the Windows build uses. CONFIRMED WORTH
        // NOT TRUSTING ALONE ON MAC: real Mac testing surfaced a window stuck on about:blank in
        // full screen after clicking an isolated account/role link, which lines up with this
        // off-screen placement not being honored the same way by macOS's own window manager
        // (it may clamp/ignore a negative origin back onto a visible display) - unlike the
        // Windows build, where it at least keeps the window out of the way even though it still
        // shows up in the taskbar/Alt+Tab per that build's own "known limitation" note. Kept
        // here as a first attempt/fallback, but EnsureColdStartPlaceholderWarmAsync below no
        // longer relies on it alone - it also explicitly minimizes the window via CDP
        // (CdpClient.HideWindowAsync) right after launch, which doesn't depend on display
        // geometry at all.
        private static readonly string[] HiddenPlacementArgs = { "--window-position=-32000,-32000", "--window-size=1,1" };

        /// <summary>
        /// Warms the shared dedicated Chrome instance ahead of time, using a throwaway hidden
        /// window/profile that no real account ever uses, so the FIRST real account/role click
        /// of a session doesn't have to pay for launching Chrome from cold.
        /// </summary>
        public async Task EnsureColdStartPlaceholderWarmAsync()
        {
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

                sw.Restart();
                ApplyKnownProfileDisplayNames();
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: ApplyKnownProfileDisplayNames ({sw.ElapsedMilliseconds}ms)");

                sw.Restart();
                int port = FindFreeTcpPort();
                LaunchChrome(ColdStartPlaceholderProfileKey, port, "about:blank", HiddenPlacementArgs, maximized: false);
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: LaunchChrome (hidden placeholder, port {port}) returned ({sw.ElapsedMilliseconds}ms)");

                sw.Restart();
                await _cdp.WaitUntilAliveAsync(port, TimeSpan.FromSeconds(20));
                Logger.Log($"EnsureColdStartPlaceholderWarmAsync: WaitUntilAliveAsync ({sw.ElapsedMilliseconds}ms)");
                File.WriteAllText(PortFile, port.ToString());

                // MAC-ONLY STEP - see CdpClient.HideWindowAsync's own doc comment for the full
                // reasoning. HiddenPlacementArgs (the off-screen --window-position trick above)
                // is the ONLY thing keeping this placeholder out of sight on the Windows build,
                // and real Mac testing suggests that trick may not hold there the way it does
                // on Windows. This asks Chrome's own window-state API to minimize the window as
                // a second, platform-safe guarantee that doesn't depend on display geometry.
                // Best-effort: HideWindowAsync already swallows its own failures, so this can
                // never block the placeholder from doing its one real job (keeping the shared
                // instance warm).
                try
                {
                    sw.Restart();
                    var placeholderTargets = await WaitForAtLeastOneTargetAsync(port);
                    string placeholderWs = await _cdp.GetWebSocketUrlAsync(port, placeholderTargets[0].Id);
                    await _cdp.HideWindowAsync(placeholderWs);
                    Logger.Log($"EnsureColdStartPlaceholderWarmAsync: HideWindowAsync ({sw.ElapsedMilliseconds}ms)");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"EnsureColdStartPlaceholderWarmAsync: failed to minimize placeholder window (non-fatal): {ex}");
                }

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
                               && !name.Equals(ColdStartPlaceholderProfileKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Deletes one isolated account/role profile: its own --profile-directory subfolder
        /// under the shared ChromeUserDataDir, and its entry in profile-labels.json.
        /// </summary>
        public async Task<(bool Ok, string Error)> DeleteProfileAsync(string profileKey)
        {
            if (string.IsNullOrWhiteSpace(profileKey) ||
                profileKey.Equals(ColdStartPlaceholderProfileKey, StringComparison.OrdinalIgnoreCase) ||
                profileKey.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
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

            if (await TryGetActivePortAsync() == null)
            {
                RemoveProfileDisplayName(profileKey);
            }

            return (true, null);
        }
    }
}
