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

        // Preset editor window (issue #35, AC10): the tab it was last closed on, and the size and
        // position it was left at. Null size/position = never moved, so the editor opens at its XAML
        // default centred on its owner.
        public int PresetEditorTab { get; set; }
        public double? PresetEditorWidth { get; set; }
        public double? PresetEditorHeight { get; set; }
        public double? PresetEditorLeft { get; set; }
        public double? PresetEditorTop { get; set; }

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

        /// <summary>How long a blocking save waits for its snapshot to reach the disk before it
        /// reports that it has not. Bounded because the writer is allowed to be stuck in a filesystem
        /// call and a modal dialog is not.</summary>
        private const int BlockingSaveBudgetMs = 2000;

        /// <summary>The ONE thing that ever writes config.json. It is still here, and it is no longer
        /// what ORDERS the writes - see <see cref="Save"/>: a mutex says who goes first, not who goes
        /// last, and this file's whole content is rewritten by every save.</summary>
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
        /// Save config.json and WAIT for it, bounded. For the launcher's dialogs, the settings
        /// window, the tray and the preset and plugin managers, where a blocking save has always
        /// been what happens and the window is modal anyway.
        ///
        /// NOT for the recording HUD: it is the window a person uses to STOP a recording, and a
        /// dispatcher waiting inside this call cannot serve the Stop button (repo coding standard 1;
        /// Review Gate round 1 on PR #34). That path uses
        /// <see cref="SaveWithoutBlockingTheUiThread"/>.
        ///
        /// IT NO LONGER WRITES THE FILE ITSELF (Review Gate round 2 on PR #39, defect 3). Both kinds
        /// of save serialise the WHOLE document, so the file is only ever correct if the LAST save
        /// made is the last one written. While this method wrote directly, the two kinds were ordered
        /// by nothing but a mutex - and a mutex decides who goes first, not who goes last. A HUD
        /// preview change queued snapshot A; before its writer got the lock, the person changed the
        /// capture folder, a shortcut, a plugin, run-at-login or the last preset, and THIS method
        /// wrote the newer snapshot B; the background writer then wrote A on top of it, and the
        /// person's newer choice was silently reverted on disk. That race widened under exactly the
        /// disk stalls the background writer exists to tolerate.
        ///
        /// So there is ONE writer and therefore ONE ORDER. This queues its snapshot like every other
        /// save and then waits for it. The wait is what makes it "blocking"; it is not what makes it
        /// write.
        /// </summary>
        public void Save()
        {
            if (!Writer.WriteNow(Serialize(), BlockingSaveBudgetMs))
                Log.Warn($"[Config] Save: {FilePath} had not reached the disk within "
                         + $"{BlockingSaveBudgetMs}ms. The change is held by the writer and is "
                         + "retried at application exit; it is not on disk yet.");
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
