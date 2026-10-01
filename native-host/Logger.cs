using System;
using System.IO;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Minimal best-effort file logger. Chrome launches this process directly with no console
    /// attached, so there's no terminal to print diagnostics to - a log file is the only
    /// practical way to see what actually happened (and how long each step took) after the
    /// fact. Never lets a logging failure affect the real native-messaging flow.
    ///
    /// <see cref="Log"/> is gated by <see cref="IsDebugEnabled"/> - it's what every step/timing
    /// narration line in CdpClient/ChromeManager/Program calls, and with the options page's
    /// "Enable debug logging" checkbox off (the default), host.log would otherwise grow
    /// forever with a full per-step trace of every single request. <see cref="LogError"/> is
    /// the escape hatch for the much smaller set of call sites that report an actual failure
    /// (a caught exception, a request that couldn't complete) - those write unconditionally,
    /// same as the extension side still calls console.error() unconditionally while gating its
    /// own verbose log() behind the identical checkbox (see content.js/background.js's own
    /// IsDebug). <see cref="IsDebugEnabled"/> is set per request from the "enableDebug" field
    /// background.js now attaches to every native-messaging call (see Program.HandleOneRequest)
    /// - there's no separate "turn debug on" message, it just rides along.
    /// </summary>
    internal static class Logger
    {
        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AWS-Login-Helper-Host-Chrome", "host.log");

        // Deliberately a plain static bool, not per-connection state: this process only ever
        // serves one signed-in user's one extension instance at a time, and requests are
        // dispatched onto their own Task.Run (see Program.RunNativeMessagingLoop) - so two
        // requests racing to set this from slightly different checkbox states is a harmless,
        // momentary imprecision (worst case, one nearby log line is written or skipped when it
        // "shouldn't" have been), never a correctness issue.
        public static bool IsDebugEnabled = false;

        public static void Log(string message)
        {
            if (!IsDebugEnabled) return;
            WriteLine(message);
        }

        public static void LogError(string message)
        {
            WriteLine(message);
        }

        private static void WriteLine(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // best effort only
            }
        }
    }
}
