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

        // WHICH LAYOUT THAT REMEMBERED SIZE BELONGS TO (issue #43). A size is only "the size I left
        // it at" for the panel it was left on. Issue #43 re-laid the Camera tab out and widened the
        // editor's default, so a 1000x760 remembered under the old two-column panel would re-open
        // the dialog too small for the new one - putting back the very scrollbar #35 removed, for
        // every existing installation, while a fresh one looked correct. A remembered size whose
        // stamp is not PresetEditor.LayoutVersion is a size from a panel that no longer exists and
        // is discarded; the editor opens at its XAML default and stamps the next size it is given.
        public int PresetEditorLayout { get; set; }

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

        // ---- Housekeeping (issues #55, #56) ---------------------------------------------------
        // AgentEyes never reduced its own footprint: one machine's recordings root held 52.9 GiB
        // across 43 recordings, of which the durable readable record was 11 MB. These are the knobs
        // on the pass that fixes that. HousekeepingReportOnly starts TRUE, so a fresh install reports
        // and changes nothing until the owner has read one report and turned it off.
        public bool HousekeepingEnabled { get; set; } = true;
        public bool HousekeepingReportOnly { get; set; } = true;

        /// <summary>Days before a recording's preserved originals (issue #83) are deleted.</summary>
        public int HousekeepingPreservedOriginalDays { get; set; } = 30;

        /// <summary>
        /// Days before a video recording's composed VIDEO expires and the recording decays to its
        /// source of truth - transcript, walkthrough text, manifest, thumbnail (issue #59). Zero
        /// disables it. The owner's ruling, 2026-09-19: thirty days, one-way, by design.
        /// </summary>
        public int HousekeepingKeepVideoDays { get; set; } = 30;

        /// <summary>
        /// True extracts walkthrough frames to disk at packaging (the behaviour before issue #59).
        /// False - the owner's default since 2026-09-19 - writes no frame files: the walkthrough page
        /// asks the running app for each frame at its offset and the control endpoint extracts it
        /// from the video on demand. Core reads this through LocalAppConfig, whose defaults MIRROR
        /// these - a test pins the mirror.
        /// </summary>
        public bool WalkthroughExtractFrames { get; set; } = false;

        /// <summary>
        /// True keeps the preserved-audio transcode BIT-EXACT (WavPack, 40.2% of the WAV). False
        /// accepts FLAC, which reaches 20.8% but writes 24 bits and so loses the low 8 bits of this
        /// 32-bit float audio - smaller, inaudible, and not something to do to an owner's archive
        /// without being asked. See HousekeepingSettings for the measurements.
        /// </summary>
        public bool HousekeepingBitExactAudio { get; set; } = true;

        /// <summary>A footprint ceiling in gigabytes, or 0 for none.</summary>
        public double HousekeepingCeilingGb { get; set; }

        // ---- Always-on recording (issue #66) ---------------------------------------------------
        // Records the chosen setup all day in one-minute pieces and keeps only the stretches with
        // sound. A 5 GB cap is the owner's decision of 2026-09-23; the keep settings are issue #79's
        // (10 s before, 10 s after, a clip closes after 5 min of silence).

        /// <summary>True while always-on is switched on, so it comes back on when AgentEyes starts.</summary>
        public bool AlwaysOnEnabled { get; set; }

        /// <summary>True while always-on is paused BY HAND. A restart brings it back paused, never
        /// recording: the user's last word was "do not record" (review of PR 68, finding 1).</summary>
        public bool AlwaysOnHandPaused { get; set; }

        /// <summary>The recording setup (preset id) always-on records; null = the last used video setup.</summary>
        public string? AlwaysOnPresetId { get; set; }

        /// <summary>Which sound decides what is kept: "mic" | "system" | "both".</summary>
        public string AlwaysOnCounts { get; set; } = "mic";

        /// <summary>The sound line in dBFS, or null for Auto (measured noise floor plus the gate margin).</summary>
        public double? AlwaysOnThresholdDb { get; set; }

        // ---- the keep settings (issue #79) ----
        // Seconds. Ranges and defaults live in AlwaysOnKeepSettings; BuildOptions refuses a config.json
        // edited out of range, with the reason, rather than guessing a value.

        /// <summary>How much a clip keeps before the first speech: 0 - 120 s, default 10 s.</summary>
        public double AlwaysOnKeepBeforeSeconds { get; set; } = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.DefaultKeepBefore.TotalSeconds;

        /// <summary>How much a clip keeps after the last speech: 0 s up to the silence gap, default 10 s.</summary>
        public double AlwaysOnKeepAfterSeconds { get; set; } = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.DefaultKeepAfter.TotalSeconds;

        /// <summary>How long a silence closes a clip: 30 s - 30 min, default 5 min.</summary>
        public double AlwaysOnSilenceGapSeconds { get; set; } = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.DefaultSilenceGap.TotalSeconds;

        /// <summary>
        /// v1.11.x's "keep before" in minutes (issue #66). Read only to migrate it (issue #79) and never
        /// written again: null once migrated, and a null is left out of config.json.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? AlwaysOnBeforeMinutes { get; set; }

        /// <summary>v1.11.x's "keep after" in minutes - what closed a clip then. Migrated to the silence gap (issue #79).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? AlwaysOnAfterMinutes { get; set; }

        public double AlwaysOnCapGb { get; set; } = 5;

        /// <summary>Where clips are saved; null = Videos\AgentEyes\AlwaysOn.</summary>
        public string? AlwaysOnClipsFolder { get; set; }

        /// <summary>The config as the Core housekeeping pass wants it.</summary>
        public AgentEyes.Housekeeping.HousekeepingSettings HousekeepingSettings() => new()
        {
            Enabled = HousekeepingEnabled,
            ReportOnly = HousekeepingReportOnly,
            PreservedOriginalDays = HousekeepingPreservedOriginalDays,
            // Structural, not by value: an expiry sooner than the preserved-original window would
            // delete the composed video while the RAW copies it was cleaned from are still
            // protected - the recording would lose its regenerable form before its raw one. The
            // clamp makes the nonsense config impossible rather than merely untested - but ZERO is
            // the documented off switch for a one-way tier, and a clamp must never turn "off" back
            // into "on".
            KeepVideoDays = AgentEyes.Housekeeping.HousekeepingSettings.ClampKeepVideoDays(
                HousekeepingKeepVideoDays, HousekeepingPreservedOriginalDays),
            PreservedAudioMustBeBitExact = HousekeepingBitExactAudio,
            CeilingBytes = HousekeepingCeilingGb <= 0 ? 0 : (long)(HousekeepingCeilingGb * 1024 * 1024 * 1024),
        };

        private static string FilePath => Path.Combine(AgentEyes.AppDataPaths.Root, "config.json");

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
                    return FromJson(File.ReadAllText(FilePath));
            }
            catch { }
            return new Config();
        }

        /// <summary>Read a config.json's text, bringing settings from an older version forward.</summary>
        internal static Config FromJson(string json)
        {
            var cfg = JsonSerializer.Deserialize<Config>(json) ?? new Config();
            cfg.MigrateAlwaysOnKeepSettings();
            return cfg;
        }

        /// <summary>
        /// Issue #79: v1.11.x kept whole minutes either side of the sound ("keep before" and "keep after",
        /// 5 min each by default), and "keep after" was also what closed a clip. Now the lead-in and the
        /// tail are seconds and the silence that closes a clip is its own setting. So:
        ///  - the old "keep after" becomes the SILENCE GAP (it was the silence that closed a clip);
        ///  - "keep before" and "keep after" take the new 10 s defaults - the old minute values meant
        ///    "a piece either side", which the 2 s keyframes replace.
        /// Runs once: the old fields are cleared, and a cleared field is not written back. A no-op for a
        /// config that has no old fields.
        /// </summary>
        internal void MigrateAlwaysOnKeepSettings()
        {
            if (AlwaysOnBeforeMinutes == null && AlwaysOnAfterMinutes == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string was = $"before={AlwaysOnBeforeMinutes?.ToString("0.##", inv) ?? "(none)"} min, after={AlwaysOnAfterMinutes?.ToString("0.##", inv) ?? "(none)"} min";
            if (AlwaysOnAfterMinutes is double after)
            {
                var gap = TimeSpan.FromMinutes(after);
                // The old choices were 1 - 30 minutes, all inside the gap's range; a hand-edited value
                // outside it is brought to the nearest end, and the log says so.
                if (gap < AgentEyes.AlwaysOn.AlwaysOnKeepSettings.SilenceGapMin) gap = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.SilenceGapMin;
                if (gap > AgentEyes.AlwaysOn.AlwaysOnKeepSettings.SilenceGapMax) gap = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.SilenceGapMax;
                AlwaysOnSilenceGapSeconds = gap.TotalSeconds;
            }
            AlwaysOnKeepBeforeSeconds = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.DefaultKeepBefore.TotalSeconds;
            AlwaysOnKeepAfterSeconds = AgentEyes.AlwaysOn.AlwaysOnKeepSettings.DefaultKeepAfter.TotalSeconds;
            AlwaysOnBeforeMinutes = null;
            AlwaysOnAfterMinutes = null;
            Log.Info($"[Config] MigrateAlwaysOnKeepSettings: v1.11 keep settings ({was}) -> keep before "
                     + $"{AlwaysOnKeepBeforeSeconds:0}s, keep after {AlwaysOnKeepAfterSeconds:0}s, silence gap {AlwaysOnSilenceGapSeconds:0}s");
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
