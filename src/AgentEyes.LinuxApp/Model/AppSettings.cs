using System;
using System.IO;
using System.Text.Json;

namespace AgentEyes.LinuxApp.Model
{
    /// <summary>
    /// The Linux app's own settings (issue #107), in linux-app.json next to the AgentEyes config.
    /// Without presets on Linux (assumption A2) the Record view keeps ONE remembered capture setup.
    /// </summary>
    internal sealed class AppSettings
    {
        /// <summary>Monitor index (1-based) to record.</summary>
        public int Screen { get; set; } = 1;

        /// <summary>Microphone description; empty = no microphone. Null = not chosen yet.</summary>
        public string? Mic { get; set; }

        public bool SystemSound { get; set; } = true;

        /// <summary>"grid" or "list".</summary>
        public string LibraryMode { get; set; } = "grid";

        public static string FilePath => Path.Combine(AppDataPaths.Root, "linux-app.json");

        private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                Log.Info($"[AppSettings] Load: no {path} yet - defaults");
                return new AppSettings();
            }
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"{path} is empty or not a settings object");
            Log.Info($"[AppSettings] Load: screen={s.Screen} mic={s.Mic ?? "(unset)"} system={s.SystemSound} mode={s.LibraryMode}");
            return s;
        }

        public void Save()
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, path, overwrite: true);
            Log.Info($"[AppSettings] Save: screen={Screen} mic={Mic ?? "(unset)"} system={SystemSound} mode={LibraryMode}");
        }

        /// <summary>The Control API source for this setup: mixed | mic | system | none.</summary>
        public static string SourceFor(string? mic, bool system) =>
            !string.IsNullOrEmpty(mic) ? (system ? "mixed" : "mic") : (system ? "system" : "none");
    }

    /// <summary>
    /// "Run at login" (issue #107): the XDG autostart entry that starts the app in the tray. The
    /// Linux face of the Windows app's HKCU Run key (Autostart.cs).
    /// </summary>
    internal static class Autostart
    {
        public static string EntryPath => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart", "agenteyes.desktop");

        public static bool IsEnabled => File.Exists(EntryPath);

        public static void Enable(string exePath, string iconPath)
        {
            Log.Info($"[Autostart] Enable: {EntryPath} -> {exePath} --tray");
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            File.WriteAllText(EntryPath, DesktopEntry(exePath, iconPath, tray: true));
        }

        public static void Disable()
        {
            Log.Info($"[Autostart] Disable: {EntryPath}");
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
        }

        /// <summary>The .desktop text for the app grid launcher (tray=false) or the login entry (tray=true).</summary>
        internal static string DesktopEntry(string exePath, string iconPath, bool tray) =>
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=AgentEyes\n" +
            "Comment=Screen, system sound and microphone recorder\n" +
            $"Exec={exePath}{(tray ? " --tray" : "")}\n" +
            $"Icon={iconPath}\n" +
            "Terminal=false\n" +
            "Categories=AudioVideo;Recorder;\n" +
            "StartupWMClass=AgentEyesLinux\n" +
            (tray ? "X-GNOME-Autostart-enabled=true\n" : "");
    }
}
