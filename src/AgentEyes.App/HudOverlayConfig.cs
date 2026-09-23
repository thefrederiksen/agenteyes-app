using System;
using AgentEyes;
using AgentEyes.Preview;

namespace AgentEyes.App
{
    /// <summary>
    /// The one bridge between the overlay framing as it is CHOSEN (on a preset, before recording)
    /// and as it is USED (by the HUD, during a recording) - issue #36, AC7.
    ///
    /// THE DIRECTION OF TRAVEL IS THE WHOLE DESIGN, and it is one-way:
    ///
    ///   preset.Overlay --Seed()--> config --Read()--> HudPreviewState --Write()--> config
    ///
    /// The preset SEEDS the config when a recording starts, so the shape, circle, corner and inset
    /// chosen in the editor are what the HUD shows the moment it appears. From then on the HUD owns
    /// the values and writes only to the config. Nothing here ever writes back into a preset, which
    /// is exactly why moving the camera to another corner mid-recording CANNOT corrupt the saved
    /// preset (AC7) - there is no code path from the HUD to presets.json at all.
    ///
    /// It is deliberately free of WPF so the rule is testable without a window.
    /// </summary>
    internal static class HudOverlayConfig
    {
        /// <summary>
        /// The overlay framing currently in the config, as one object. Unrecognised spellings and
        /// out-of-range numbers are read as their documented defaults (see
        /// <see cref="CameraOverlaySettings.Canonical"/>), so a hand-edited config.json cannot put
        /// the HUD into a state it has no rendering for.
        /// </summary>
        public static CameraOverlaySettings Read(Config cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            return new CameraOverlaySettings
            {
                Shape = cfg.HudPreviewShape,
                Corner = cfg.HudPreviewCorner,
                InsetFraction = cfg.HudPreviewInsetFraction,
                Circle = new CameraOverlayCircle
                {
                    CentreX = cfg.HudPreviewCircleCentreX,
                    CentreY = cfg.HudPreviewCircleCentreY,
                    Diameter = cfg.HudPreviewCircleDiameter,
                },
            }.Canonical();
        }

        /// <summary>
        /// Write the framing into the config object. Does NOT save the file - the caller decides when
        /// disk I/O happens, because this runs on paths (the HUD's own click handlers) where an
        /// unnecessary write would be file I/O on the UI thread.
        /// </summary>
        public static void Write(Config cfg, CameraOverlaySettings overlay)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (overlay == null) throw new ArgumentNullException(nameof(overlay));

            var c = overlay.Canonical();
            cfg.HudPreviewShape = c.Shape;
            cfg.HudPreviewCorner = c.Corner;
            cfg.HudPreviewInsetFraction = c.InsetFraction;
            cfg.HudPreviewCircleCentreX = c.Circle.CentreX;
            cfg.HudPreviewCircleCentreY = c.Circle.CentreY;
            cfg.HudPreviewCircleDiameter = c.Circle.Diameter;
        }

        /// <summary>
        /// A recording is starting from <paramref name="preset"/>: make its overlay framing the one
        /// the HUD will show (AC3, AC7). Called from <see cref="PresetCapture.Start"/>, which is the
        /// single funnel every recording start goes through, so the launcher, the tray and the REST
        /// API cannot each seed it differently - or forget to.
        /// </summary>
        public static void Seed(Config cfg, CapturePreset preset)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (preset == null) throw new ArgumentNullException(nameof(preset));

            var overlay = (preset.Overlay ?? new CameraOverlaySettings()).Canonical();
            Write(cfg, overlay);
            Log.Info($"[HudOverlayConfig] Seed: preset \"{preset.Name}\" -> overlay {overlay}");
        }
    }
}
