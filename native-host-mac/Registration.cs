using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Handles everything needed to make Chrome aware of this native host, and to make sure
    /// the companion Chrome extension is actually installed - all without any admin/root
    /// rights, since everything here is written under the signed-in user's own home directory.
    ///
    /// THE ONE REAL DIFFERENCE FROM THE WINDOWS BUILD: there is no Windows Registry on macOS,
    /// so there's nothing here for a registry key to point at. Chrome's native-messaging-host
    /// registration on macOS IS the manifest file - Chrome looks directly for
    /// ~/Library/Application Support/Google/Chrome/NativeMessagingHosts/&lt;host_name&gt;.json,
    /// no separate pointer needed. That's actually simpler than the Windows build (one file
    /// write instead of a file write plus an HKCU registry write); everything else below
    /// (extension-id.txt bookkeeping, detecting whether the extension is installed) is the
    /// same logic as the Windows build, just reading from Chrome's macOS profile layout
    /// instead of its Windows one.
    /// </summary>
    internal static class Registration
    {
        public const string HostName = "com.penchala_services_inc.awsloginhelper";

        // Must stay IDENTICAL to the Windows build's HostName (and to the NATIVE_HOST constant
        // in extension/background.js) - this is the SAME published Chrome extension talking to
        // a platform-specific build of the same logical native host, not a different product
        // the way the Edge build is. Only the binary and its registration mechanism are
        // platform-specific; the extension's own code never changes per OS.

        private const string ExtensionIdPlaceholder = "REPLACE_WITH_CHROME_WEB_STORE_ID";

        // PublishSingleFile=true means Assembly.GetExecutingAssembly().Location returns "" at
        // runtime (there is no separate physical binary to point at - everything is bundled
        // into one executable). Environment.ProcessPath (.NET 6+) is the supported way to get
        // the real, on-disk path in that mode, same as the Windows build relies on.
        private static string ExePath => Environment.ProcessPath;
        private static string InstallDir => Path.GetDirectoryName(ExePath);
        private static string ExtensionIdFile => Path.Combine(InstallDir, "extension-id.txt");

        // Where Chrome itself looks for this host's manifest on macOS - fixed by Chrome, not
        // something this project gets to choose (unlike the Windows build's ManifestFile,
        // which lives next to the exe and is only found via the registry pointer). Per-user,
        // so no sudo/root needed - the per-machine equivalent would be
        // /Library/Google/Chrome/NativeMessagingHosts/, which this deliberately avoids for the
        // same "no admin rights" reason the Windows build stays entirely in HKCU.
        private static string NativeMessagingHostsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Google", "Chrome", "NativeMessagingHosts");

        private static string ManifestFile => Path.Combine(NativeMessagingHostsDir, $"{HostName}.json");

        /// <summary>
        /// Returns every extension id currently allowed to talk to this native host - identical
        /// logic to the Windows build. extension-id.txt holds one id per line and is additive,
        /// never overwritten wholesale.
        /// </summary>
        public static List<string> GetConfiguredExtensionIds(string overrideExtensionId = null)
        {
            var ids = new List<string>();
            if (File.Exists(ExtensionIdFile))
            {
                ids.AddRange(File.ReadAllLines(ExtensionIdFile)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrEmpty(line)));
            }

            string overrideTrimmed = overrideExtensionId?.Trim();
            if (!string.IsNullOrEmpty(overrideTrimmed) && !ids.Contains(overrideTrimmed, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(overrideTrimmed);
                try
                {
                    File.AppendAllLines(ExtensionIdFile, new[] { overrideTrimmed });
                    Logger.Log($"GetConfiguredExtensionIds: added new override id to {ExtensionIdFile}: {overrideTrimmed}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"GetConfiguredExtensionIds: failed to persist override id (non-fatal): {ex}");
                }
            }

            if (ids.Count == 0) ids.Add(ExtensionIdPlaceholder);
            return ids;
        }

        /// <summary>
        /// Writes the native-messaging-host manifest JSON straight into Chrome's own
        /// NativeMessagingHosts directory (with this executable's own absolute path baked in).
        /// That IS the registration on macOS - no registry key, no separate pointer file; see
        /// this class's own doc comment above. Safe to call repeatedly (e.g. every
        /// install/upgrade, or by hand after publishing the extension and editing
        /// extension-id.txt).
        /// </summary>
        public static void RegisterHost(string extensionIdOverride = null)
        {
            Logger.Log("RegisterHost: starting");
            try
            {
                string exePath = ExePath;
                List<string> extensionIds = GetConfiguredExtensionIds(extensionIdOverride);
                Logger.Log($"RegisterHost: exePath={exePath}, extensionIds=[{string.Join(", ", extensionIds)}], manifestFile={ManifestFile}");

                var manifest = new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = HostName,
                    ["description"] = "AWS Login Helper Native - native messaging host",
                    ["path"] = exePath,
                    ["type"] = "stdio",
                    ["allowed_origins"] = new Newtonsoft.Json.Linq.JArray(
                        extensionIds.Select(id => (object)$"chrome-extension://{id}/")),
                };

                Directory.CreateDirectory(NativeMessagingHostsDir);
                File.WriteAllText(ManifestFile, manifest.ToString());
                Logger.Log($"RegisterHost: wrote manifest file ({new FileInfo(ManifestFile).Length} bytes)");

                // The exe published by `dotnet publish` has no execute bit set by default on
                // macOS/Linux, and Chrome refuses to launch a native-messaging host it can't
                // execute. install.sh (see package-mac/) already chmods the binary right after
                // copying it into place, but this covers the "ran --register by hand without
                // re-running install.sh" case too, so a register that follows a manual file
                // copy still results in something Chrome can actually launch.
                //
                // Guarded by OperatingSystem.IsMacOS() (this project only ever runs on macOS,
                // but the compiler's platform-compatibility analyzer can't know that from a
                // plain "net8.0" TargetFramework alone) - File.SetUnixFileMode throws
                // PlatformNotSupportedException on Windows, same reason the Windows build's own
                // Registration.cs never calls it.
                if (OperatingSystem.IsMacOS())
                {
                    try { File.SetUnixFileMode(exePath, UnixFileModeRwxRxRx); }
                    catch (Exception ex) { Logger.LogError($"RegisterHost: failed to set executable bit (non-fatal): {ex}"); }
                }

                Console.WriteLine($"Registered native host manifest at {ManifestFile}");
                if (extensionIds.Count == 1 && extensionIds[0] == ExtensionIdPlaceholder)
                {
                    Console.WriteLine(
                        "WARNING: extension-id.txt is not set yet. Chrome will reject messages from the " +
                        "real extension until you put the published Chrome Web Store extension ID into " +
                        $"{ExtensionIdFile} and re-run --register.");
                    Logger.Log("RegisterHost: WARNING extension-id.txt still has the placeholder value");
                }

                CheckExtensionInstalled(openStoreIfMissing: true);
                Logger.Log("RegisterHost: completed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"RegisterHost: FAILED - {ex}");
                throw; // preserve the real exit code / error for anyone running --register by hand
            }
        }

        // rwxr-xr-x - matches what `chmod 755` would set. System.IO.UnixFileMode and
        // File.SetUnixFileMode are available since .NET 7, so both are fine to use directly on
        // this net8.0 target; on Windows this enum/API still compiles but SetUnixFileMode
        // throws PlatformNotSupportedException at runtime, which is exactly why Registration.cs
        // for the Windows build never calls it in the first place - no code here is shared
        // with that build, so there's nothing to guard.
        private const UnixFileMode UnixFileModeRwxRxRx =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        public static void UnregisterHost()
        {
            try
            {
                if (File.Exists(ManifestFile))
                {
                    File.Delete(ManifestFile);
                    Console.WriteLine($"Removed native messaging host manifest at {ManifestFile}.");
                }
                else
                {
                    Console.WriteLine("No native messaging host manifest found - nothing to remove.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not remove manifest file: " + ex.Message);
            }
        }

        /// <summary>
        /// Looks for ANY of the configured extension ids under every Chrome profile this user
        /// has on macOS - both CRX/Web-Store installs (their own folder under "Extensions")
        /// AND unpacked Developer-Mode installs (recorded only in the profile's own
        /// "Preferences" file). Identical logic to the Windows build's version of this method;
        /// only chromeUserData's root path differs.
        /// </summary>
        public static bool CheckExtensionInstalled(bool openStoreIfMissing, string extensionIdOverride = null)
        {
            List<string> extensionIds = GetConfiguredExtensionIds(extensionIdOverride);
            if (extensionIds.Count == 1 && extensionIds[0] == ExtensionIdPlaceholder)
            {
                Console.WriteLine("Extension ID not configured yet (see extension-id.txt) - skipping install check.");
                return false;
            }

            string chromeUserData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Google", "Chrome");

            bool found = false;
            if (Directory.Exists(chromeUserData))
            {
                foreach (var profileDir in Directory.GetDirectories(chromeUserData))
                {
                    if (extensionIds.Any(id =>
                        Directory.Exists(Path.Combine(profileDir, "Extensions", id)) ||
                        IsExtensionInPreferences(profileDir, id)))
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                Console.WriteLine("Extension is already installed.");
                return true;
            }

            Console.WriteLine("Extension not found in any Chrome profile.");
            if (openStoreIfMissing)
            {
                string storeUrl = $"https://chromewebstore.google.com/detail/{extensionIds[0]}";
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = ChromeManager.FindChromeExe(), Arguments = storeUrl, UseShellExecute = false });
                }
                catch
                {
                    // Fall back to whatever the OS considers the default handler for the URL -
                    // `open <url>` is macOS's equivalent of Windows's ShellExecute-with-a-URL.
                    try { Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { storeUrl }, UseShellExecute = false }); }
                    catch { /* best effort only */ }
                }
            }

            return false;
        }

        /// <summary>
        /// Chrome records every extension it has ever loaded for a profile - unpacked or not -
        /// under extensions.settings.&lt;id&gt; in that profile's "Preferences" (sometimes
        /// "Secure Preferences") JSON file. Identical to the Windows build's version - this is
        /// Chrome's own file format, same on every OS.
        /// </summary>
        private static bool IsExtensionInPreferences(string profileDir, string extensionId)
        {
            foreach (var fileName in new[] { "Preferences", "Secure Preferences" })
            {
                string path = Path.Combine(profileDir, fileName);
                if (!File.Exists(path)) continue;

                try
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                    var settings = json["extensions"]?["settings"] as Newtonsoft.Json.Linq.JObject;
                    if (settings != null && settings[extensionId] != null) return true;
                }
                catch
                {
                    // Preferences can be mid-write while Chrome is running, or not valid JSON
                    // for some other reason - skip rather than fail the whole check over it.
                }
            }
            return false;
        }
    }
}
