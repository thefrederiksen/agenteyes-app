using System;
using AgentEyes.App;
using AgentEyes.Preview;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #36, AC7 - the preset and the HUD stay in sync, and the HUD cannot corrupt the preset.
    ///
    /// The claim has TWO HALVES and both are easy to get wrong in opposite ways. The preset must
    /// REACH the HUD (or the framing chosen in the editor is decoration), and the HUD must NOT REACH
    /// BACK (or moving the camera to another corner mid-recording silently rewrites a saved preset).
    /// The second half is an absence claim, so it is not tested by "nothing happened" - it is tested
    /// by mutating the HUD's copy and then asserting the PRESENCE of the preset's original values.
    ///
    /// WHAT THESE CANNOT SEE: that the HUD window really draws the corner it holds. That is a
    /// rendering fact; the HUD's UI Automation status line ("both circle top-left") is what carries
    /// it into the running-app proof, because the HUD is excluded from screen capture.
    /// </summary>
    public class CameraOverlaySyncTests
    {
        private static CapturePreset PresetFramedAs(CameraOverlayShape shape, PreviewCorner corner,
                                                    double centreX, double centreY, double diameter,
                                                    double inset)
        {
            var p = new CapturePreset { Name = "Framed", Mode = "video", Camera = "Some Cam" };
            p.Overlay.Shape = PreviewNames.Text(shape);
            p.Overlay.Corner = PreviewNames.Text(corner);
            p.Overlay.InsetFraction = inset;
            p.Overlay.Circle.CentreX = centreX;
            p.Overlay.Circle.CentreY = centreY;
            p.Overlay.Circle.Diameter = diameter;
            return p;
        }

        // ---- preset -> config -> HUD ---------------------------------------

        [Fact]
        public void Seed_PutsThePresetsFramingWhereTheHudReadsIt()
        {
            var cfg = new Config();
            var preset = PresetFramedAs(CameraOverlayShape.Circle, PreviewCorner.TopLeft,
                                        centreX: 0.7, centreY: 0.25, diameter: 0.45, inset: 0.5);

            HudOverlayConfig.Seed(cfg, preset);

            var read = HudOverlayConfig.Read(cfg);
            Assert.Equal(CameraOverlayShape.Circle, read.ShapeValue);
            Assert.Equal(PreviewCorner.TopLeft, read.CornerValue);
            Assert.Equal(0.7, read.Circle.CentreX, 6);
            Assert.Equal(0.25, read.Circle.CentreY, 6);
            Assert.Equal(0.45, read.Circle.Diameter, 6);
            Assert.Equal(0.5, read.InsetFraction, 6);
        }

        [Fact]
        public void HudPreviewState_BuiltFromASeededConfig_ShowsThePresetsFraming()
        {
            // AC3: the framing chosen in the editor is the framing the HUD shows when recording
            // starts. This is the whole chain minus the window: preset -> config -> HUD state.
            var cfg = new Config();
            HudOverlayConfig.Seed(cfg, PresetFramedAs(CameraOverlayShape.Circle, PreviewCorner.BottomLeft,
                                                      0.33, 0.4, 0.5, 0.25));
            cfg.HudPreviewVisible = true;
            cfg.HudPreviewMode = PreviewNames.Both;

            var state = new HudPreviewState(
                cfg.HudPreviewVisible, PreviewNames.Mode(cfg.HudPreviewMode), HudOverlayConfig.Read(cfg),
                feedAvailable: true, cameraAvailable: true);

            Assert.Equal(CameraOverlayShape.Circle, state.Shape);
            Assert.Equal(PreviewCorner.BottomLeft, state.Corner);
            Assert.Equal(0.33, state.Circle.CentreX, 6);
            Assert.Equal(0.25, state.InsetFraction, 6);
            Assert.True(state.CameraIsInset);
        }

        [Fact]
        public void Seed_ARectanglePreset_ReachesTheHudAsARectangle()
        {
            // AC6: choosing rectangle really does reproduce today's inset rather than being ignored
            // in favour of the new default.
            var cfg = new Config();
            HudOverlayConfig.Seed(cfg, PresetFramedAs(CameraOverlayShape.Rectangle, PreviewCorner.BottomRight,
                                                      0.5, 0.42, 0.6, 0.3));

            Assert.Equal(CameraOverlayShape.Rectangle, HudOverlayConfig.Read(cfg).ShapeValue);
        }

        [Fact]
        public void Read_FreshConfig_IsACircleInTheBottomRight()
        {
            // AC1 as a user experiences it on an untouched machine.
            var read = HudOverlayConfig.Read(new Config());

            Assert.Equal(CameraOverlayShape.Circle, read.ShapeValue);
            Assert.Equal(PreviewCorner.BottomRight, read.CornerValue);
            Assert.Equal(0.60, read.Circle.Diameter, 3);
        }

        [Fact]
        public void Read_HandEditedNonsenseInConfig_IsReadAsTheDocumentedDefaults()
        {
            var cfg = new Config
            {
                HudPreviewShape = "trapezoid",
                HudPreviewCorner = "somewhere",
                HudPreviewCircleCentreX = 12,
                HudPreviewCircleCentreY = -3,
                HudPreviewCircleDiameter = 88,
                HudPreviewInsetFraction = -1,
            };

            var read = HudOverlayConfig.Read(cfg);

            Assert.Equal(CameraOverlayShape.Circle, read.ShapeValue);
            Assert.Equal(PreviewCorner.BottomRight, read.CornerValue);
            Assert.Equal(1.0, read.Circle.CentreX, 6);
            Assert.Equal(0.0, read.Circle.CentreY, 6);
            Assert.Equal(1.0, read.Circle.Diameter, 6);
            Assert.Equal(CameraOverlaySettings.MinInsetFraction, read.InsetFraction, 6);
        }

        // ---- the HUD writes to the config, never to the preset ---------------

        [Fact]
        public void ChangingTheCornerOnTheHud_LeavesTheSavedPresetAlone()
        {
            // AC7, the half that is an absence claim - so it is proved by a PRESENCE: the preset
            // still holds its own corner after the HUD has been moved somewhere else entirely.
            var preset = PresetFramedAs(CameraOverlayShape.Circle, PreviewCorner.TopRight,
                                        0.6, 0.3, 0.5, 0.35);
            var cfg = new Config();
            HudOverlayConfig.Seed(cfg, preset);

            var state = new HudPreviewState(
                visible: true, PreviewMode.Both, HudOverlayConfig.Read(cfg),
                feedAvailable: true, cameraAvailable: true);
            state.SetCorner(PreviewCorner.BottomLeft);
            HudOverlayConfig.Write(cfg, state.Framing);

            // The HUD moved...
            Assert.Equal(PreviewCorner.BottomLeft, state.Corner);
            Assert.Equal("bottom-left", cfg.HudPreviewCorner);
            // ...and the preset did not.
            Assert.Equal("top-right", preset.Overlay.Corner);
            Assert.Equal(0.6, preset.Overlay.Circle.CentreX, 6);
            Assert.Equal(0.35, preset.Overlay.InsetFraction, 6);
        }

        [Fact]
        public void HudPreviewState_HoldsItsOwnCopy_NotTheConfigsObject()
        {
            var cfg = new Config();
            var framing = HudOverlayConfig.Read(cfg);

            var state = new HudPreviewState(
                visible: true, PreviewMode.Both, framing,
                feedAvailable: true, cameraAvailable: true);
            framing.Corner = PreviewNames.TopLeft;
            framing.Circle.CentreX = 0.01;

            Assert.Equal(PreviewCorner.BottomRight, state.Corner);
            Assert.Equal(0.5, state.Circle.CentreX, 6);
        }

        // ---- what reaches manifest.json -------------------------------------

        [Fact]
        public void ManifestOverlay_WhenTheOverlayIsBeingShown_IsTheWholeFraming()
        {
            var cfg = new Config();
            HudOverlayConfig.Seed(cfg, PresetFramedAs(CameraOverlayShape.Circle, PreviewCorner.TopLeft,
                                                      0.55, 0.31, 0.48, 0.4));
            var state = new HudPreviewState(
                visible: true, PreviewMode.Both, HudOverlayConfig.Read(cfg),
                feedAvailable: true, cameraAvailable: true);

            var overlay = state.ManifestOverlay;

            Assert.NotNull(overlay);
            Assert.Equal("circle", overlay!.Shape);
            Assert.Equal("top-left", overlay.Corner);
            Assert.Equal(0.4, overlay.InsetFraction, 6);
            Assert.Equal(0.55, overlay.Circle.CentreX, 6);
            // The corner surface issue #33 records is now derived from the same object, so the two
            // can never disagree.
            Assert.Equal(overlay.Corner, state.ManifestCorner);
        }

        // The mode arrives as its WIRE spelling: the enum is internal to the product and xUnit needs
        // a public signature to discover a test.
        [Theory]
        [InlineData(false, "both", true)]     // panel hidden
        [InlineData(true, "screen", true)]    // screen only - nothing framed
        [InlineData(true, "camera", true)]    // camera only - nothing framed
        [InlineData(true, "both", false)]     // no camera track to inset
        public void ManifestOverlay_WhenNothingWasFramed_IsNull(bool visible, string mode, bool camera)
        {
            // AC10: no overlay framing means no overlay geometry in the manifest at all.
            var state = new HudPreviewState(
                visible, PreviewNames.Mode(mode), new CameraOverlaySettings(),
                feedAvailable: true, cameraAvailable: camera);

            Assert.Null(state.ManifestOverlay);
            Assert.Null(state.ManifestCorner);
        }

        [Fact]
        public void ManifestOverlay_IsACopy_SoALaterHudClickCannotRewriteTheRecording()
        {
            var state = new HudPreviewState(
                visible: true, PreviewMode.Both, new CameraOverlaySettings(),
                feedAvailable: true, cameraAvailable: true);

            var recorded = state.ManifestOverlay!;
            state.SetCorner(PreviewCorner.TopLeft);

            Assert.Equal("bottom-right", recorded.Corner);
            Assert.Equal("top-left", state.ManifestOverlay!.Corner);
        }

        // ---- the camera's own frame size, which the editor cannot guess -------

        [Fact]
        public void CameraFrameSize_ReadsTheINPUTStreamSizeAndNotTheOutputBuffer()
        {
            // The real shape of ffmpeg's report for the preset editor's preview pipeline. The output
            // block says 320x240 - the padded buffer - and reading THAT is exactly the mistake that
            // would put the circle over the black bars.
            const string log = """
            Input #0, dshow, from 'video=Integrated Camera':
              Duration: N/A, start: 224062.628000, bitrate: N/A
              Stream #0:0: Video: rawvideo (YUY2 / 0x32595559), yuyv422, 1280x720, 30 fps, 30 tbr, 10000k tbn
            Stream mapping:
              Stream #0:0 -> #0:0 (rawvideo (native) -> rawvideo (native))
            Output #0, rawvideo, to 'pipe:1':
              Stream #0:0: Video: rawvideo (BGR[24] / 0x18524742), bgr24, 320x240, q=2-31, 10 fps
            """;

            var size = CameraFrameSize.FromFfmpegLog(log);

            Assert.NotNull(size);
            Assert.Equal(1280, size!.Value.Width);
            Assert.Equal(720, size.Value.Height);
            Assert.Equal(1280.0 / 720.0, size.Value.Aspect, 6);
        }

        [Fact]
        public void CameraFrameSize_MjpegCamera_IsReadPastTheColourSpaceParentheses()
        {
            const string log = """
            Input #0, dshow, from 'video=HD Pro Webcam C920':
              Stream #0:0: Video: mjpeg (Baseline) (MJPG / 0x47504A4D), yuvj422p(pc, bt470bg/unknown/unknown), 640x480, 30 fps, 30 tbr
            """;

            var size = CameraFrameSize.FromFfmpegLog(log);

            Assert.Equal(new CameraFrameSize(640, 480), size);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ffmpeg version 6.0 Copyright (c) 2000-2023")]
        public void CameraFrameSize_NotReportedYet_IsNullRatherThanAGuess(string? log)
        {
            // The whole point: "not observed" must be distinguishable from a size. A guess here draws
            // a convincing circle over the wrong part of the picture.
            Assert.Null(CameraFrameSize.FromFfmpegLog(log));
        }

        [Fact]
        public void CameraFrameSize_OutputBlockOnly_IsNull()
        {
            // ffmpeg has described its own output but has not yet described the camera. Reading the
            // output would answer 320x240 - the padded buffer - and be wrong.
            const string log = """
            Output #0, rawvideo, to 'pipe:1':
              Stream #0:0: Video: rawvideo (BGR[24] / 0x18524742), bgr24, 320x240, q=2-31, 10 fps
            """;

            Assert.Null(CameraFrameSize.FromFfmpegLog(log));
        }

        [Fact]
        public void CameraFrameSize_TooSmallToBeAFrame_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFrameSize(8, 480));
        }
    }
}
