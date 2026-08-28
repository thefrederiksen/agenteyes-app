using System;
using AgentEyes.App;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33 - what the HUD preview decides: which layers are drawn, which controls may be
    /// touched, what reaches manifest.json, and when a picture has stopped being live.
    /// </summary>
    public class HudPreviewStateTests
    {
        /// <summary>The overlay framing the state is built with (issue #36). Defaults to the
        /// documented defaults, so every issue #33 assertion below still runs against the same corner
        /// it always did - the corner simply lives inside this object now.</summary>
        private static CameraOverlaySettings Framing(
            PreviewCorner corner = PreviewCorner.BottomRight,
            CameraOverlayShape shape = CameraOverlayShape.Circle) =>
            new()
            {
                Corner = PreviewNames.Text(corner),
                Shape = PreviewNames.Text(shape),
            };

        private static HudPreviewState WithCamera(
            bool visible = true, PreviewMode mode = PreviewMode.Both,
            PreviewCorner corner = PreviewCorner.BottomRight) =>
            new(visible, mode, Framing(corner), feedAvailable: true, cameraAvailable: true);

        private static HudPreviewState WithoutCamera(
            bool visible = true, PreviewMode mode = PreviewMode.Screen,
            PreviewCorner corner = PreviewCorner.BottomRight) =>
            new(visible, mode, Framing(corner), feedAvailable: true, cameraAvailable: false);

        /// <summary>A recording started with the preview switched off: its ffmpeg carries no preview
        /// output, so there is no feed and none can be added while it runs.</summary>
        private static HudPreviewState WithNoFeed(
            bool visible = true, PreviewMode mode = PreviewMode.Both,
            PreviewCorner corner = PreviewCorner.BottomRight) =>
            new(visible, mode, Framing(corner), feedAvailable: false, cameraAvailable: true);

        // ---- the toggle -----------------------------------------------------

        [Fact]
        public void Visible_FreshConfig_IsOff()
        {
            // AC1: with a fresh config the preview panel is HIDDEN on the first recording.
            var cfg = new Config();
            var state = new HudPreviewState(
                cfg.HudPreviewVisible, PreviewNames.Mode(cfg.HudPreviewMode),
                HudOverlayConfig.Read(cfg),
                feedAvailable: true, cameraAvailable: true);

            Assert.False(state.Visible);
            Assert.False(state.ShowScreenLayer);
            Assert.False(state.ShowCameraLayer);
            Assert.Equal("Show preview", state.ToggleLabel);
        }

        [Fact]
        public void ToggleVisible_ShowsThenHides()
        {
            var state = WithCamera(visible: false, mode: PreviewMode.Screen);

            Assert.True(state.ToggleVisible());
            Assert.True(state.Visible);
            Assert.Equal("Hide preview", state.ToggleLabel);
            Assert.True(state.ShowScreenLayer);

            Assert.False(state.ToggleVisible());
            Assert.False(state.Visible);
            Assert.Equal("Show preview", state.ToggleLabel);
            Assert.False(state.ShowScreenLayer);
        }

        // ---- modes ----------------------------------------------------------

        // The theories take the WIRE spellings rather than the enum values: the enums are internal
        // to the product, and xUnit needs a public signature to discover a test.
        [Theory]
        [InlineData("screen", true, false)]
        [InlineData("camera", false, true)]
        [InlineData("both", true, true)]
        public void Layers_FollowTheMode(string mode, bool screen, bool camera)
        {
            var state = WithCamera(mode: PreviewNames.Mode(mode));
            Assert.Equal(screen, state.ShowScreenLayer);
            Assert.Equal(camera, state.ShowCameraLayer);
        }

        [Fact]
        public void CameraIsInset_OnlyInBothMode()
        {
            Assert.True(WithCamera(mode: PreviewMode.Both).CameraIsInset);
            Assert.False(WithCamera(mode: PreviewMode.Camera).CameraIsInset);
            Assert.False(WithCamera(mode: PreviewMode.Screen).CameraIsInset);
        }

        [Fact]
        public void TrySetMode_CameraModeWithoutACamera_IsRefusedAndChangesNothing()
        {
            // Refused rather than silently coerced: a mode that will not be honoured must not look
            // accepted, and the person's stored choice must not be quietly rewritten.
            var state = WithoutCamera(mode: PreviewMode.Screen);

            Assert.False(state.TrySetMode(PreviewMode.Camera));
            Assert.False(state.TrySetMode(PreviewMode.Both));
            Assert.Equal(PreviewMode.Screen, state.Mode);
        }

        [Fact]
        public void TrySetMode_WithACamera_IsAccepted()
        {
            var state = WithCamera(mode: PreviewMode.Screen);

            Assert.True(state.TrySetMode(PreviewMode.Camera));
            Assert.Equal(PreviewMode.Camera, state.Mode);
            Assert.True(state.TrySetMode(PreviewMode.Both));
            Assert.Equal(PreviewMode.Both, state.Mode);
        }

        [Fact]
        public void ShowCameraLayer_WithoutACamera_IsNeverDrawn()
        {
            // A stored "camera"/"both" from a previous recording that HAD a camera.
            Assert.False(WithoutCamera(mode: PreviewMode.Camera).ShowCameraLayer);
            Assert.False(WithoutCamera(mode: PreviewMode.Both).ShowCameraLayer);
        }

        [Fact]
        public void UnavailableMessage_WithoutACamera_SaysSo()
        {
            Assert.Equal("This recording has no camera track.",
                WithoutCamera(mode: PreviewMode.Camera).UnavailableMessage);
            Assert.Equal("This recording has no camera track - showing the screen only.",
                WithoutCamera(mode: PreviewMode.Both).UnavailableMessage);
            Assert.Null(WithoutCamera(mode: PreviewMode.Screen).UnavailableMessage);
            Assert.Null(WithCamera(mode: PreviewMode.Both).UnavailableMessage);
        }

        [Fact]
        public void CameraModesEnabled_FollowsWhetherThereIsACamera()
        {
            Assert.True(WithCamera().CameraModesEnabled);
            Assert.False(WithoutCamera().CameraModesEnabled);
        }

        // ---- corners --------------------------------------------------------

        [Theory]
        [InlineData("bottom-right")]
        [InlineData("bottom-left")]
        [InlineData("top-left")]
        [InlineData("top-right")]
        public void SetCorner_IsRememberedAndSpelledOut(string wire)
        {
            var corner = PreviewNames.Corner(wire);
            var state = WithCamera();
            state.SetCorner(corner);

            Assert.Equal(corner, state.Corner);
            Assert.Equal(wire, PreviewNames.Text(corner));
            Assert.Equal(wire, state.ManifestCorner);
        }

        [Fact]
        public void CornerControlsEnabled_OnlyWhenAnOverlayIsActuallyBeingShown()
        {
            Assert.True(WithCamera(mode: PreviewMode.Both).CornerControlsEnabled);
            Assert.False(WithCamera(mode: PreviewMode.Screen).CornerControlsEnabled);
            Assert.False(WithCamera(visible: false, mode: PreviewMode.Both).CornerControlsEnabled);
            Assert.False(WithoutCamera(mode: PreviewMode.Both).CornerControlsEnabled);
        }

        // ---- what reaches manifest.json (AC5 / AC11) -------------------------

        [Fact]
        public void ManifestCorner_OverlayShownWithACamera_IsTheChosenCorner()
        {
            var state = WithCamera(mode: PreviewMode.Both, corner: PreviewCorner.TopLeft);
            Assert.Equal("top-left", state.ManifestCorner);
        }

        [Fact]
        public void ManifestCorner_NoOverlayFramed_IsNull()
        {
            // Three separate ways to have framed nothing. Each must reach the manifest as an ABSENT
            // field, so a recording made without the overlay keeps the manifest it always had (AC11).
            Assert.Null(WithCamera(mode: PreviewMode.Screen).ManifestCorner);
            Assert.Null(WithCamera(mode: PreviewMode.Camera).ManifestCorner);
            Assert.Null(WithCamera(visible: false, mode: PreviewMode.Both).ManifestCorner);
            Assert.Null(WithoutCamera(mode: PreviewMode.Both).ManifestCorner);
        }

        // ---- a recording started with the preview switched off (AC11) --------

        [Fact]
        public void WithNoFeed_NothingIsDrawnAndThePanelSaysWhy()
        {
            // AC11 is bought with this case: a recording whose ffmpeg carries no preview output is
            // byte-for-byte the recording it was before the feature existed. The panel must then SAY
            // there is no feed - an empty rectangle would read as a broken preview.
            var state = WithNoFeed();

            Assert.False(state.ShowScreenLayer);
            Assert.False(state.ShowCameraLayer);
            Assert.NotNull(state.UnavailableMessage);
            Assert.Contains("NEXT recording", state.UnavailableMessage);
        }

        [Fact]
        public void WithNoFeed_NoCornerReachesTheManifest()
        {
            // Nothing was ever framed, because nothing was ever shown.
            Assert.Null(WithNoFeed(mode: PreviewMode.Both).ManifestCorner);
        }

        [Fact]
        public void WithNoFeed_TheCameraControlsAreNotOffered()
        {
            var state = WithNoFeed();
            Assert.False(state.CameraAvailable);
            Assert.False(state.CameraModesEnabled);
            Assert.False(state.CornerControlsEnabled);
        }

        [Fact]
        public void ArmNextRecording_FollowsTheVisibleChoice()
        {
            // Turning the preview on is what asks for a feed; the feed itself is created when the
            // next recording starts.
            Assert.True(WithCamera(visible: true).ArmNextRecording);
            Assert.False(WithCamera(visible: false).ArmNextRecording);
            Assert.True(WithNoFeed(visible: true).ArmNextRecording);
        }

        // ---- staleness (AC10) ------------------------------------------------

        [Fact]
        public void IsStale_NoFrameHasEverArrived_IsStale()
        {
            // The fail-closed arm. "We have never seen a frame" must read as a broken preview, never
            // as a preview that is merely showing something very still.
            Assert.True(HudPreviewState.IsStale(null, DateTime.UtcNow));
        }

        [Fact]
        public void IsStale_FreshFrame_IsNotStale()
        {
            var now = DateTime.UtcNow;
            Assert.False(HudPreviewState.IsStale(now.AddMilliseconds(-100), now));
        }

        [Fact]
        public void IsStale_FrameOlderThanTheWindow_IsStale()
        {
            var now = DateTime.UtcNow;
            Assert.True(HudPreviewState.IsStale(
                now.AddSeconds(-HudPreviewState.StaleAfterSeconds - 0.1), now));
        }

        // ---- the wire spellings ---------------------------------------------

        [Theory]
        [InlineData("screen", "screen")]
        [InlineData("camera", "camera")]
        [InlineData("both", "both")]
        [InlineData("nonsense", "screen")]
        [InlineData(null, "screen")]
        public void PreviewNames_Mode_ParsesOrFallsBackToScreen(string? text, string expected) =>
            Assert.Equal(expected, PreviewNames.Text(PreviewNames.Mode(text)));

        [Theory]
        [InlineData("bottom-right", "bottom-right")]
        [InlineData("bottom-left", "bottom-left")]
        [InlineData("top-left", "top-left")]
        [InlineData("top-right", "top-right")]
        [InlineData("nonsense", "bottom-right")]
        [InlineData(null, "bottom-right")]
        public void PreviewNames_Corner_ParsesOrFallsBackToTheDocumentedDefault(
            string? text, string expected) =>
            Assert.Equal(expected, PreviewNames.Text(PreviewNames.Corner(text)));

        [Fact]
        public void PreviewNames_UnknownEnumValue_Throws()
        {
            // No default that guesses: a mode or corner nobody spelled out here must break this test
            // rather than quietly become one of the existing ones.
            Assert.Throws<ArgumentOutOfRangeException>(() => PreviewNames.Text((PreviewMode)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => PreviewNames.Text((PreviewCorner)99));
        }

        [Fact]
        public void Config_Defaults_AreAHiddenScreenPreviewInTheBottomRight()
        {
            var cfg = new Config();
            Assert.False(cfg.HudPreviewVisible);
            Assert.Equal("screen", cfg.HudPreviewMode);
            Assert.Equal("bottom-right", cfg.HudPreviewCorner);
            Assert.Null(cfg.HudWidth);
            Assert.Null(cfg.HudHeight);
        }
    }
}
