using System;
using System.IO;
using System.Text.Json;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>App settings persisted to %LOCALAPPDATA%\AgentEyes\config.json.</summary>
    internal sealed class Config
    {
        public int Port { get; set; } = 7882;   // 7879/7880 are used by cc-director/tailscale
        public bool ApiEnabled { get; set; } = true;
        public bool RunAtLogin { get; set; } = false;
        // Check for a new release on startup and offer it. Sends no user data - it just asks the
        // public releases repo "what is the latest version" - so it fits the privacy stance.
        public bool AutoUpdate { get; set; } = true;
        public string? LastUsedPresetId { get; set; }   // launcher startup selection + tray quick-record

        // Recording HUD (issue #20): last dragged position; null = top-right default.
        public double? HudLeft { get; set; }
        public double? HudTop { get; set; }

        // Recording HUD live preview (issue #33). Size is remembered ONLY for the preview state -
        // with the preview hidden the HUD sizes itself to its content exactly as it always has, so a
        // null here is "never resized" and not "zero".
        public double? HudWidth { get; set; }
        public double? HudHeight { get; set; }

        // Whether the preview panel is showing. FALSE BY DEFAULT, and that default is the feature's
        // first acceptance criterion: a fresh config records with no preview panel at all.
        public bool HudPreviewVisible { get; set; }

        // What the preview shows: "screen" | "camera" | "both". Parsed by PreviewNames.Mode, which
        // reads anything unrecognised as "screen" - the one mode every recording can show.
        public string HudPreviewMode { get; set; } = "screen";

        // Where the camera sits in "both" mode: "bottom-right" | "bottom-left" | "top-left" |
        // "top-right". Parsed by PreviewNames.Corner; the documented default is bottom-right.
        public string HudPreviewCorner { get; set; } = "bottom-right";

        // Capture feature (issue #64): global snip shortcuts, parsed with TriggerSpec.
        // Defaults: region = PrintScreen (drag a rectangle),
        // full-screen = Ctrl+PrintScreen (whole monitor). Rebinding persists across restart.
        public string CaptureRegionTrigger { get; set; } = "hotkey:printscreen";
        public string CaptureFullTrigger { get; set; } = "hotkey:ctrl+printscreen";
        // Save folder for snips (issue #64, AC9/AC10). Null/blank = the Windows Screenshots known
        // folder (SHGetKnownFolderPath(FOLDERID_Screenshots), honoring OneDrive redirection). A
        // non-blank value overrides it to any writable path and persists across restart.
        public string? CaptureSaveFolder { get; set; }

        // Transcription runs 100% through the signed-in DevThrottle account (issue #87):
        // no engine choice, no provider key. The dt_ credential lives in the DPAPI-encrypted
        // credential store (AgentEyes.DevThrottle.DevThrottleAccount), never in this config.

        // Post-recording plugins (issue #13): ids the user opted into. Plugins run
        // after transcription, each as its own process. See docs/plugins.md.
        public System.Collections.Generic.List<string> EnabledPlugins { get; set; } = new();
        // Plugin registry (issue #32): null = PluginRegistry.DefaultUrl, the registry file on the
        // main branch of the one consolidated public repo (issue #186).
        public string? PluginRegistryUrl { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEyes", "config.json");

        /// <summary>The ONE thing that ever writes config.json, so a synchronous save and a
        /// background one cannot land on the file at the same moment and lose each other.</summary>
        private static readonly object WriteGate = new();

        /// <summary>The background writer behind <see cref="SaveWithoutBlockingTheUiThread"/>. Its
        /// thread is started by <see cref="Load"/> - at application startup, before any window
        /// exists - and never lazily from a UI path, so the write loop is not reachable from the
        /// HUD's click handlers even through the call graph.</summary>
        private static readonly BackgroundFileWriter Writer = new(FilePath, WriteJson);

        public static Config Load()
        {
            // Loading the config is what brings its writer to life: every save in the process goes
            // through that one writer, and it must exist before anything can ask for one.
            Writer.Start();
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
            }
            catch { }
            return new Config();
        }

        /// <summary>
        /// Write config.json now, on the calling thread. For the launcher's dialogs, where a
        /// blocking write has always been what happens and the window is modal anyway.
        ///
        /// NOT for the recording HUD: it is the window a person uses to STOP a recording, and a
        /// dispatcher blocked inside this call cannot serve the Stop button (repo coding standard 1;
        /// Review Gate round 1 on PR #34). That path uses
        /// <see cref="SaveWithoutBlockingTheUiThread"/>.
        /// </summary>
        public void Save()
        {
            try { WriteJson(FilePath, Serialize()); }
            catch (Exception ex)
            {
                Log.Warn($"[Config] Save FAILED: {FilePath} - {ex.Message}. "
                         + "The change is held in memory for this session but is not on disk.");
            }
        }

        /// <summary>
        /// Persist the config WITHOUT waiting for the disk (issue #33). The JSON is produced here, on
        /// the caller's thread - microseconds of in-memory work, and it is what stops the writer ever
        /// seeing a half-changed object - and the write itself is handed to a background thread.
        ///
        /// The caller returns immediately whatever the filesystem is doing, which is the whole point:
        /// this is called from the recording HUD's click handlers, and the same dispatcher serves the
        /// Stop button.
        /// </summary>
        public void SaveWithoutBlockingTheUiThread() => Writer.Queue(Serialize());

        /// <summary>Wait, bounded, for a queued background save to reach the disk. Called at
        /// application exit. Returns false when it did not land, which is reported rather than waited
        /// out - the writer is allowed to be stuck in a filesystem call, and exit is not.</summary>
        public static bool FlushPendingSave(int milliseconds) => Writer.Flush(milliseconds);

        private string Serialize() =>
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        private static void WriteJson(string path, string json)
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
        }
    }
}
