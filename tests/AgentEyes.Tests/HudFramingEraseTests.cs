using AgentEyes;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #51: the HUD must not erase the framing the PRESET chose.
    ///
    /// The defect this locks down shipped in v1.8.0 and broke the headline feature on the ONLY path
    /// most people use. Recording from the tray with a camera preset, the HUD's apply-at-construction
    /// landed about 1.5 seconds after the start and pushed a null overlay, because
    /// `HudPreviewState.ManifestOverlay` is null whenever the preview PANEL is not showing - the
    /// default. That null was assigned straight over the preset's framing, so the stop wrote no
    /// framing, `NeedsCompose` returned false, and camera.mp4 was left beside recording.mp4 exactly
    /// as it had been before the compose feature existed.
    ///
    /// From the owner's own recording, 2026-08-30_223040_video:
    ///   22:30:40.607  StartVideo: framing recorded at start - circle bottom-right inset 0.21
    ///   22:30:42.093  SetPreviewOverlay: overlay=(none)
    /// </summary>
    public class HudFramingEraseTests
    {
        private static CameraOverlaySettings Framing(string corner = "bottom-right") => new()
        {
            Corner = corner,
            Shape = "circle",
            InsetFraction = 0.21,
            Circle = new CameraOverlayCircle { CentreX = 0.542, CentreY = 0.489, Diameter = 0.424 },
        };

        [Fact]
        public void A_null_from_the_preview_does_not_erase_the_presets_framing()
        {
            var svc = new RecordingService();
            svc.SetPreviewOverlay(Framing());
            Assert.Equal("bottom-right", svc.PreviewOverlayCorner);

            // Exactly what the HUD does when its preview panel is not showing.
            svc.SetPreviewOverlay(null);

            Assert.Equal("bottom-right", svc.PreviewOverlayCorner);
            Assert.NotNull(svc.PreviewOverlay);
            Assert.Equal("circle", svc.PreviewOverlay!.Shape);
            Assert.Equal(0.21, svc.PreviewOverlay.InsetFraction, 6);
        }

        [Fact]
        public void Repeated_nulls_still_do_not_erase_it()
        {
            // ApplyPreview runs on EVERY apply, not just construction.
            var svc = new RecordingService();
            svc.SetPreviewOverlay(Framing());
            for (int i = 0; i < 5; i++) svc.SetPreviewOverlay(null);

            Assert.Equal("bottom-right", svc.PreviewOverlayCorner);
        }

        [Fact]
        public void A_real_framing_from_the_hud_still_refines_the_presets_one()
        {
            // The HUD is a refinement channel - when it HAS something to say it must still win.
            var svc = new RecordingService();
            svc.SetPreviewOverlay(Framing());
            svc.SetPreviewOverlay(Framing("top-left"));

            Assert.Equal("top-left", svc.PreviewOverlayCorner);
        }

        [Fact]
        public void A_null_before_any_framing_leaves_it_unset()
        {
            // A recording with no camera and no framing must still record none - issue #33 AC11 and
            // issue #36 AC10 both require the manifest of a camera-less recording to be unchanged.
            var svc = new RecordingService();
            svc.SetPreviewOverlay(null);

            Assert.Null(svc.PreviewOverlayCorner);
            Assert.Null(svc.PreviewOverlay);
        }
    }
}
