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

        // ---- the camera overlay's framing (issue #36) -----------------------
        //
        // Flat scalars rather than a nested object, so a config.json written before this feature has
        // exactly the fields it always had and each of these simply reads as its default. The corner
        // deliberately stays in HudPreviewCorner above: one value, one home, no drift.
        //
        // These are SEEDED FROM THE PRESET when a recording starts (PresetCapture.Start) and then
        // owned by the HUD for the rest of the session - which is what lets the HUD's corner buttons
        // keep working mid-recording without writing back into the saved preset (AC7).

        // "circle" (the default, issue #36 AC1) | "rectangle" (what issue #33 shipped).
        public string HudPreviewShape { get; set; } = "circle";

        // Where the circle sits in the CAMERA FRAME, as fractions of it (assumption E2). The
        // defaults are assumption E3: horizontally centred, in the upper portion of the frame, at
        // 60% of the frame height.
        public double HudPreviewCircleCentreX { get; set; } = 0.50;
        public double HudPreviewCircleCentreY { get; set; } = 0.42;
        public double HudPreviewCircleDiameter { get; set; } = 0.60;

        // How wide the inset is on the preview, as a fraction of the preview's width. A DIFFERENT
        // thing from the circle's diameter (assumption E5): this is how big it looks, that is how
        // much of the camera is inside it.
        public double HudPreviewInsetFraction { get; set; } = 0.30;

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
