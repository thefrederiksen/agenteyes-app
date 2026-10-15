using System;
using System.Diagnostics;
using System.IO;

namespace AgentEyes.LinuxApp.Services
{
    /// <summary>Hands a folder, file or URL to the desktop (xdg-open) - the Linux face of the Windows
    /// app's explorer / ShellExecute calls (issue #107).</summary>
    internal static class Shell
    {
        public static void Open(string target)
        {
            Log.Info($"[Shell] Open: {target}");
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(target) && !Directory.Exists(target))
                throw new FileNotFoundException("nothing to open at " + target, target);
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("xdg-open could not be started");
        }
    }
}
