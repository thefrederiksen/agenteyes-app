using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Post-recording plugin system v1 (issue #13). A plugin is a folder under
    /// %LOCALAPPDATA%\AgentEyes\plugins\&lt;id&gt;\ holding a plugin.json manifest.
    /// Enabled plugins run AFTER a recording is finalized and transcribed, each as
    /// its own process (failure isolation: a crashing plugin can never take the app
    /// or the recording down). The recording directory is the contract surface:
    /// plugins read the files in it (recording.mp4 / audio.wav, transcript.txt,
    /// manifest.json, shots/) and write their artifacts next to them.
    ///
    /// Spec: docs/plugins.md. Discovery/auto-update is deliberately phase 2 -
    /// see the issue for what still needs a hosting/trust decision.
    /// </summary>
    /// <summary>One configurable value a plugin declares in plugin.json (issue #32).
    /// Type "text" (default) renders a text box, "bool" a checkbox. The value
    /// reaches the plugin process as the environment variable MQS_SETTING_KEY.</summary>
    internal sealed class PluginSetting
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "text";   // text | bool
        public string Default { get; set; } = "";
        public string Description { get; set; } = "";
    }

    internal sealed class PluginInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "";
        /// <summary>Process to run: ["exe", "arg", ...]. "{dir}" in any argument is
        /// replaced with the recording directory path.</summary>
        public string[] Command { get; set; } = Array.Empty<string>();

        /// <summary>Declared configurable values (optional). Rendered on the Plugins
        /// tab; saved per machine; delivered to the process as env vars.</summary>
        public PluginSetting[] Settings { get; set; } = Array.Empty<PluginSetting>();

        /// <summary>Folder the plugin lives in (set at load; working dir at run time).</summary>
        public string PluginDir { get; set; } = "";
    }

    internal static class Plugins
    {
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentEyes", "plugins");

        private const int TimeoutMinutes = 10;

        /// <summary>All installed plugins (valid plugin.json found), sorted by name.</summary>
        public static List<PluginInfo> Load()
        {
            var found = new List<PluginInfo>();
            try
            {
                if (!Directory.Exists(Root)) return found;
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    string manifest = Path.Combine(dir, "plugin.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        var p = JsonSerializer.Deserialize<PluginInfo>(File.ReadAllText(manifest),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (p == null || p.Id.Length == 0 || p.Command.Length == 0)
                        {
                            Log.Error($"plugin {dir}: plugin.json needs at least id and command");
                            continue;
                        }
                        p.PluginDir = dir;
                        if (p.Name.Length == 0) p.Name = p.Id;
                        // Settings hygiene: drop entries without a key, default labels.
                        p.Settings = p.Settings.Where(s => s.Key.Trim().Length > 0).ToArray();
                        foreach (var s in p.Settings)
                            if (s.Label.Length == 0) s.Label = s.Key;
                        found.Add(p);
                    }
                    catch (Exception ex) { Log.Error("plugin manifest " + manifest, ex); }
                }
            }
            catch (Exception ex) { Log.Error("plugin scan", ex); }
            return found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Run every enabled plugin against a finished recording, one at a
        /// time. Reports progress through onStatus ("Running X..."); a failing plugin
        /// logs and continues to the next. Returns the count that ran successfully.</summary>
        public static async Task<int> RunEnabledAsync(string recordingDir, Config cfg, Action<string>? onStatus = null)
        {
            var enabled = cfg.EnabledPlugins;
            if (enabled.Count == 0) return 0;

            int ok = 0;
            foreach (var plugin in Load().Where(p => enabled.Contains(p.Id)))
            {
                onStatus?.Invoke($"Running {plugin.Name}...");
                try
                {
                    if (await Task.Run(() => RunOne(plugin, recordingDir))) ok++;
                }
                catch (Exception ex) { Log.Error($"plugin {plugin.Id}", ex); }
            }
            return ok;
        }

        // ---- per-plugin settings (issue #32) --------------------------------

        /// <summary>Settings live NEXT TO the plugin folder (plugins\&lt;id&gt;.settings.json),
        /// not inside it - a registry update replaces the folder and must not wipe
        /// the user's configuration.</summary>
        public static string SettingsPath(PluginInfo plugin) =>
            Path.Combine(Root, plugin.Id + ".settings.json");

        /// <summary>Saved values merged over the declared defaults.</summary>
        public static Dictionary<string, string> LoadSettings(PluginInfo plugin)
        {
            var values = plugin.Settings.ToDictionary(s => s.Key, s => s.Default, StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = SettingsPath(plugin);
                if (File.Exists(path))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (saved != null)
                        foreach (var (key, value) in saved) values[key] = value;
                }
            }
            catch (Exception ex) { Log.Error($"plugin {plugin.Id} settings load", ex); }
            return values;
        }

        public static void SaveSettings(PluginInfo plugin, Dictionary<string, string> values)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(SettingsPath(plugin),
                    JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error($"plugin {plugin.Id} settings save", ex); }
        }

        /// <summary>MQS_SETTING_API_KEY style env name for a setting key.</summary>
        public static string EnvName(string key)
        {
            var sb = new StringBuilder("MQS_SETTING_");
            foreach (char c in key)
                sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
            return sb.ToString();
        }

        /// <summary>One plugin, one process. stdout/stderr land in
        /// plugin-&lt;id&gt;.log inside the recording directory. Declared settings
        /// arrive as MQS_SETTING_* environment variables.</summary>
        private static bool RunOne(PluginInfo plugin, string recordingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = plugin.Command[0],
                WorkingDirectory = plugin.PluginDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in plugin.Command.Skip(1))
                psi.ArgumentList.Add(arg.Replace("{dir}", recordingDir));
            if (plugin.Settings.Length > 0)
                foreach (var (key, value) in LoadSettings(plugin))
                    psi.Environment[EnvName(key)] = value;

            // AgentEyes runs on DevThrottle (issue #88): hand the plugin the signed-in
            // account's dt_ key + base URL so it calls DevThrottle inference. The key is
            // decrypted from the DPAPI credential store here and passed only via the child
            // process environment - never written to disk in the recording directory.
            var dtCred = AgentEyes.DevThrottle.DevThrottleAccount.Load();
            if (dtCred?.ApiKey is { Length: > 0 })
            {
                psi.Environment["DEVTHROTTLE_API_KEY"] = dtCred.ApiKey;
                psi.Environment["DEVTHROTTLE_BASE_URL"] = AgentEyes.DevThrottle.DevThrottleAccount.ApiBaseUrl;
            }

            string logFile = Path.Combine(recordingDir, $"plugin-{plugin.Id}.log");
            var output = new StringBuilder();
            output.AppendLine($"[{DateTime.Now:HH:mm:ss}] {plugin.Id} v{plugin.Version} starting");

            Log.Info($"plugin {plugin.Id}: start ({recordingDir})");
            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine("ERR " + e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool done = proc.WaitForExit(TimeSpan.FromMinutes(TimeoutMinutes));
            if (!done)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                output.AppendLine($"[{DateTime.Now:HH:mm:ss}] TIMEOUT after {TimeoutMinutes} minutes - killed");
                Log.Error($"plugin {plugin.Id}: timeout, killed");
            }
            else
            {
                output.AppendLine($"[{DateTime.Now:HH:mm:ss}] exit code {proc.ExitCode}");
                if (proc.ExitCode == 0) Log.Info($"plugin {plugin.Id}: ok");
                else Log.Error($"plugin {plugin.Id}: exit {proc.ExitCode}");
            }

            try { File.WriteAllText(logFile, output.ToString()); }
            catch (Exception ex) { Log.Error($"plugin {plugin.Id} log write", ex); }
            return done && proc.ExitCode == 0;
        }
    }
}
