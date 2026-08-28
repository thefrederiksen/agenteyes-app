using System;
using System.IO;
using System.Text.Json;

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

        // Preset editor window (issue #35, AC10): the tab it was last closed on, and the size and
        // position it was left at. Null size/position = never moved, so the editor opens at its XAML
        // default centred on its owner.
        public int PresetEditorTab { get; set; }
        public double? PresetEditorWidth { get; set; }
        public double? PresetEditorHeight { get; set; }
        public double? PresetEditorLeft { get; set; }
        public double? PresetEditorTop { get; set; }

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

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
            }
            catch { }
            return new Config();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
