using System;
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

        // ---- the SEAM the outage actually lived in (issue #53) ------------------
        //
        // The tests above guard the SETTER. The Review Gate on PR #52 found that they do not reach
        // the path that produced the outage: nothing executes StartVideo with a preset overlay, so
        // deleting the seed - or the preset's handoff of it - leaves every one of them green while a
        // tray recording once again ends up with camera.mp4 beside an uncomposed recording.mp4.
        //
        // These are SOURCE guards rather than behavioural tests, and the reason is the suite's own
        // contract: StartVideo opens ffmpeg and a physical webcam, and this suite is required to run
        // fast and silent with neither. A source guard cannot prove the seam WORKS - the committed
        // tray-path evidence in docs/cencon/proof/issue-51/handoff.md does that - but it does prove
        // the seam is still WIRED, which is precisely the regression the gate described. Both are
        // negative-controlled: deleting either line fails the matching test.

        private static string RecordingServiceSource => RepoSource.Read("src/AgentEyes.Core/RecordingService.cs");

        [Fact]
        public void StartVideo_takes_the_framing_as_a_parameter()
        {
            // If the parameter goes, the preset has no way to hand its framing over at all.
            Assert.Contains("CameraOverlaySettings? overlay = null)", RecordingServiceSource);
        }

        [Fact]
        public void StartVideo_seeds_the_session_framing_from_that_parameter()
        {
            // THE line the gate named (RecordingService.cs:611). This is what the stop later reads,
            // and what the HUD's null must not erase. Before issue #47 it read "_previewOverlay = null".
            string src = RecordingServiceSource;

            Assert.Contains("_previewOverlay = overlay?.Canonical();", src);

            // And the clearing sites are only the three SESSION BOUNDARIES - the stop, the
            // failed-start rollback, and Reset. A fourth would mean something clears the framing
            // mid-session again, which is the shape of both this bug and issue #51's.
            int clears = src.Split(new[] { "_previewOverlay = null;" }, StringSplitOptions.None).Length - 1;
            Assert.True(clears == 3, $"expected 3 session-boundary clears, found {clears}");
        }

        [Fact]
        public void StartVideo_also_seeds_the_durable_start_manifest()
        {
            // The other half, from issue #47 round-2 defect 5: a recording killed before its stop
            // must still carry the framing on disk, or recovery finds nothing to compose.
            string src = RecordingServiceSource;

            Assert.Contains("if (dshowCamera != null && overlay != null)", src);
            Assert.Contains("_manifest.PreviewOverlayCorner = framing.Corner;", src);
            Assert.Contains("_manifest.PreviewOverlayShape = framing.Shape;", src);
            Assert.Contains("_manifest.PreviewOverlayInset = framing.InsetFraction;", src);
        }

        [Fact]
        public void The_preset_hands_its_own_framing_to_StartVideo()
        {
            // The tray path. Dropping this argument is the second way the gate showed the outage
            // could return with every test still green.
            string preset = RepoSource.Read("src/AgentEyes.App/CapturePreset.cs");

            Assert.Contains("p.Overlay", preset);
            Assert.Contains("string.IsNullOrWhiteSpace(p.Camera) ? null : p.Overlay", preset);
        }

        [Fact]
        public void A_recording_with_no_camera_still_hands_over_no_framing()
        {
            // Issue #33 AC11 and issue #36 AC10: a camera-less recording's manifest must keep the
            // shape it had before these features existed, so the handoff is conditional on a camera.
            Assert.Contains("string.IsNullOrWhiteSpace(p.Camera) ? null : p.Overlay",
                RepoSource.Read("src/AgentEyes.App/CapturePreset.cs"));
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
