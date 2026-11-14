using System;
using System.Diagnostics;
using System.IO;

namespace AgentEyes.DesktopApp.Services
{
    /// <summary>Hands a folder, file or URL to the desktop (issue #107; per platform since #116):
    /// xdg-open on Linux, open on macOS, explorer on Windows - the same thing the WPF app's
    /// explorer / ShellExecute calls do.</summary>
    internal static class Shell
    {
        /// <summary>The platform this process runs on, as the app names it in its log: windows, mac or linux.</summary>
        public static string PlatformName =>
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

        /// <summary>The program that opens a path or URL with the user's default handler on a platform. Pure.</summary>
        internal static string OpenerFor(string platform) => platform switch
        {
            "windows" => "explorer.exe",
            "mac" => "open",
            "linux" => "xdg-open",
            _ => throw new ArgumentException($"unknown platform '{platform}' (expected windows, mac or linux)", nameof(platform)),
        };

        public static void Open(string target)
        {
            string opener = OpenerFor(PlatformName);
            Log.Info($"[Shell] Open: {target} (with {opener})");
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(target) && !Directory.Exists(target))
                throw new FileNotFoundException("nothing to open at " + target, target);
            var psi = new ProcessStartInfo(opener) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException(opener + " could not be started");
        }
    }
}
