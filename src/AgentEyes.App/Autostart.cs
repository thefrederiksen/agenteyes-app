using System.Diagnostics;
using Microsoft.Win32;

namespace AgentEyes.App
{
    /// <summary>Run-at-login via the per-user Run key. Launches minimized to the tray.</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AgentEyes";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled)
            {
                string exe = Process.GetCurrentProcess().MainModule!.FileName;
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
