using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #36 - the SETUP UI (AC2) and the HUD's two overlay shapes (AC1, AC6), read as source
    /// facts out of PresetEditor.xaml, PresetEditor.xaml.cs and HudWindow.cs.
    ///
    /// They are source facts because both surfaces are WPF windows that need a running app to
    /// render. Each assertion is a PRESENCE - a missing control, a deleted slider, a circle host that
    /// has grown a background again, or an adorner that guesses the camera's size all FAIL rather
    /// than passing by finding nothing.
    ///
    /// WHAT THESE CANNOT SEE, stated rather than implied:
    ///  - that the drawn circle is actually round on screen, or that moving a slider changes what is
    ///    inside it. Those are the three editor screenshots in the running-app proof (AC2).
    ///  - that the screen preview really shows through the corners of the inset's bounding box. That
    ///    is the HUD screenshot (AC1). This file can only prove the code asks for it.
    ///  - anything about camera.mp4. That is ffprobe's answer (AC5).
    /// </summary>
    public class CameraOverlayUiTests
    {
        private const string XamlPath = @"src\AgentEyes.App\PresetEditor.xaml";
        private const string EditorCodePath = @"src\AgentEyes.App\PresetEditor.xaml.cs";
        private const string HudPath = @"src\AgentEyes.App\HudWindow.cs";

        private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Xaml() => XDocument.Parse(RepoSource.Read(XamlPath));

        private static Dictionary<string, XElement> Named(XDocument doc) =>
            doc.Descendants()
               .Where(e => e.Attribute(X + "Name") != null)
               .ToDictionary(e => e.Attribute(X + "Name")!.Value, e => e);

        // ---- AC2: the circle can be positioned and sized, against a live image ----

        [Fact]
        public void PresetEditor_CameraTab_CarriesCentreAndDiameterControls()
        {
            var named = Named(Xaml());

            // The instrument itself must not come back empty.
            Assert.True(named.Count >= 40,
                $"Only {named.Count} named controls found in {XamlPath} - the scan is broken, not the UI.");

            var expected = new Dictionary<string, string>
            {
                ["OverlayShapeCircle"] = "RadioButton",
                ["OverlayShapeRectangle"] = "RadioButton",
                ["CircleXSlider"] = "Slider",
                ["CircleYSlider"] = "Slider",
                ["CircleSizeSlider"] = "Slider",
                ["CircleXText"] = "TextBlock",
                ["CircleYText"] = "TextBlock",
                ["CircleSizeText"] = "TextBlock",
                ["OverlayCornerBox"] = "ComboBox",
                ["InsetSizeSlider"] = "Slider",
                ["InsetSizeText"] = "TextBlock",
                ["OverlayResetButton"] = "Button",
                ["OverlayHint"] = "TextBlock",
                ["CircleControls"] = "StackPanel",
                ["OverlayControls"] = "StackPanel",
                // The circle drawn over the live picture.
                ["CameraOverlayAdorner"] = "Canvas",
                ["OverlayMaskPath"] = "Path",
                ["OverlayOutlinePath"] = "Path",
            };

            var missing = expected.Keys.Where(n => !named.ContainsKey(n)).ToList();
            Assert.True(missing.Count == 0,
                "The circle setup UI (issue #36, AC2) is missing: " + string.Join(", ", missing));

            foreach (var (name, type) in expected)
                Assert.True(named[name].Name.LocalName == type,
                    $"{name} is a {named[name].Name.LocalName}; AC2 needs a {type}.");
        }

        [Fact]
        public void PresetEditor_CircleControls_CoverTheWholeFrameAndAreDrivableByAutomation()
        {
            var named = Named(Xaml());

            foreach (string slider in new[] { "CircleXSlider", "CircleYSlider" })
            {
                Assert.Equal("0", named[slider].Attribute("Minimum")?.Value);
                Assert.Equal("1", named[slider].Attribute("Maximum")?.Value);
            }

            // The diameter's range is the model's range, so a value set through UI Automation cannot
            // ask for a circle the model would silently clamp somewhere else.
            Assert.Equal("0.1", named["CircleSizeSlider"].Attribute("Minimum")?.Value);
            Assert.Equal("1", named["CircleSizeSlider"].Attribute("Maximum")?.Value);
            Assert.Equal("0.15", named["InsetSizeSlider"].Attribute("Minimum")?.Value);
            Assert.Equal("0.6", named["InsetSizeSlider"].Attribute("Maximum")?.Value);

            foreach (string slider in new[] { "CircleXSlider", "CircleYSlider", "CircleSizeSlider", "InsetSizeSlider" })
                Assert.True(named[slider].Attribute("ValueChanged") != null,
                    $"{slider} moves nothing - it has no ValueChanged handler.");
        }

        [Fact]
        public void PresetEditor_TheCircleIsDrawnOverTheLiveCameraPane_NotBesideIt()
        {
            // AC2 says "against a LIVE camera image". The adorner has to be inside the same pane as
            // the preview Image, or it is drawn over nothing.
            var doc = Xaml();
            var pane = doc.Descendants(Wpf + "Border")
                          .Single(e => e.Attribute(X + "Name")?.Value == "CameraPreviewPanel");

            var inside = pane.Descendants()
                             .Where(e => e.Attribute(X + "Name") != null)
                             .Select(e => e.Attribute(X + "Name")!.Value)
                             .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("CameraPreviewImage", inside);
            Assert.Contains("CameraOverlayAdorner", inside);
            Assert.Contains("OverlayMaskPath", inside);
            Assert.Contains("OverlayOutlinePath", inside);
        }

        [Fact]
        public void PresetEditor_TheAdorner_NeverGuessesWhereTheCameraPictureIs()
        {
            // The pane shows a PADDED 320x240 buffer. Without the camera's own reported size the
            // adorner cannot know where the black bars are - and an assumed size draws a perfectly
            // convincing circle over the wrong part of the face. The method must therefore return
            // "not known" rather than assuming the picture fills the pane.
            string body = RepoSource.MethodBody(RepoSource.Read(EditorCodePath),
                                                "private OverlayRect? PreviewContentRect()");

            Assert.Contains("SourceSize is not { } camera", body);
            Assert.Contains("return null", body);
            // Both fits: the buffer into the pane, and the camera's picture into the buffer.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, @"OverlayFit\.Contain").Count);
            Assert.Contains("camera.Width", body);
        }

        [Fact]
        public void PresetEditor_WhenTheCameraSizeIsUnknown_TheHintSaysSoInsteadOfShowingACircle()
        {
            string body = RepoSource.MethodBody(RepoSource.Read(EditorCodePath),
                                                "private void RedrawOverlayAdorner(bool circle)");

            // Both halves: the drawing is cleared AND the reason is written where a person reads it.
            Assert.Contains("OverlayMaskPath.Data = null", body);
            Assert.Contains("OverlayOutlinePath.Data = null", body);
            Assert.Contains("OverlayHint.Text", body);
            Assert.Contains("Waiting for the camera picture", body);
        }

        [Fact]
        public void PresetEditor_SavesAndLoadsTheOverlayWithThePreset()
        {
            string code = RepoSource.Read(EditorCodePath);

            Assert.Contains("p.Overlay = ReadOverlay();",
                            RepoSource.MethodBody(code, "private void ReadInto(CapturePreset p)"));
            Assert.Contains("LoadOverlayFrom(p.Overlay);",
                            RepoSource.MethodBody(code, "private void LoadFrom(CapturePreset p)"));
        }

        [Fact]
        public void PresetEditor_DraggingOnThePicture_SetsTheSlidersRatherThanASecondHiddenValue()
        {
            // Two ways to say the same thing is two things to keep in step. The drag writes the
            // sliders; the sliders are what Save reads.
            string body = RepoSource.MethodBody(RepoSource.Read(EditorCodePath),
                                                "private void MoveCircleTo(Point pointInPane)");

            Assert.Contains("CircleXSlider.Value", body);
            Assert.Contains("CircleYSlider.Value", body);
            Assert.Contains("PreviewContentRect()", body);
        }

        // ---- AC1 / AC6: the two shapes in the HUD ---------------------------

        [Fact]
        public void Hud_CircleOverlay_HasNoBackgroundAndNoBorder()
        {
            // AC1: the area outside the circle is fully transparent to the screen preview beneath.
            // A background or a border on the host would box the circle back in - which is exactly
            // what the screenshot of the bounding-box corners is meant to detect.
            string body = RepoSource.MethodBody(RepoSource.Read(HudPath),
                                                "private void LayOutInset(double surfaceWidth)");

            Assert.Contains("circle ? Brushes.Transparent : Brushes.Black", body);
            Assert.Contains("new Thickness(circle ? 0 : 1)", body);
            // A circle needs a square host, or it renders as an oval.
            Assert.Contains("circle ? inset : double.NaN", body);
            // AC6: the rectangle keeps the bordered box on black it always had.
            Assert.Contains("Brushes.Black", body);
        }

        [Fact]
        public void Hud_CircleOverlay_ShowsTheChosenCropRatherThanTheWholeShrunkenFrame()
        {
            // The circle is a CROP of the camera frame, mapped through an ImageBrush viewbox. A mask
            // over the whole frame would be a round hole showing the middle of the picture, which is
            // not what was framed in the editor.
            string body = RepoSource.MethodBody(RepoSource.Read(HudPath),
                                                "private void PaintCameraFrame(BitmapSource? frame)");

            Assert.Contains("_preview.Circle.Viewbox(frame.PixelWidth, frame.PixelHeight)", body);
            Assert.Contains("ViewboxUnits = BrushMappingMode.RelativeToBoundingBox", body);
            Assert.Contains("_cameraCircle.Fill = brush", body);
            // The two shapes are mutually exclusive: whichever is drawn, the other holds nothing.
            Assert.Contains("_cameraImage.Source = null", body);
        }

        [Fact]
        public void Hud_InsetSize_ComesFromTheChosenFramingAndNotAHardCodedConstant()
        {
            // Issue #33 had a fixed 0.30. Promoting the inset size into the preset (AC7, E5) means
            // the layout has to read it - a leftover constant would ignore the control silently.
            string hud = RepoSource.Read(HudPath);
            Assert.DoesNotContain("InsetWidthFraction", hud);

            string body = RepoSource.MethodBody(hud, "private void LayOutInset(double surfaceWidth)");
            Assert.Contains("_preview.InsetFraction", body);
        }

        [Fact]
        public void Hud_WritesItsFramingToTheConfigOnly_NeverToAPreset()
        {
            // AC7's absence claim, checked where it can actually be checked: the HUD's whole save
            // path. presets.json is reached through PresetStore, and this window must never touch it.
            string hud = RepoSource.Read(HudPath);
            Assert.DoesNotContain("PresetStore", hud);
            Assert.DoesNotContain("CapturePreset", hud);

            string body = RepoSource.MethodBody(hud, "private void SavePreviewChoices()");
            Assert.Contains("HudOverlayConfig.Write(_cfg", body);
            Assert.Contains("_cfg.Save()", body);
        }

        [Fact]
        public void Hud_StatusLine_NamesTheShape_SinceTheHudCannotBeScreenshotted()
        {
            // The HUD sets WDA_EXCLUDEFROMCAPTURE, so no screen grab can prove the overlay is round.
            // The UI Automation status text is the surface that can, and it must carry the shape.
            string body = RepoSource.MethodBody(RepoSource.Read(HudPath),
                                                "private void UpdatePreviewStatus(bool screenStale, bool cameraStale)");

            Assert.Contains("PreviewNames.Text(_preview.Shape)", body);
            Assert.Contains("PreviewNames.Text(_preview.Corner)", body);
        }

        // ---- the recording is never touched ---------------------------------

        [Fact]
        public void TheOverlay_NeverReachesTheCameraRecorder()
        {
            // Assumption E1 / AC5, as far as source can carry it: nothing in the camera recording
            // path knows the overlay exists, so there is no code that could crop camera.mp4. The
            // recorded file's dimensions are settled by ffmpeg's own camera arguments, which this
            // issue does not touch.
            string args = RepoSource.Read(@"src\AgentEyes.Core\Video\FfmpegArgs.cs");
            string recorder = RepoSource.Read(@"src\AgentEyes.Core\Video\FfmpegCameraRecorder.cs");

            foreach (string forbidden in new[] { "Overlay", "circle", "Circle" })
            {
                Assert.False(args.Contains(forbidden, StringComparison.Ordinal),
                    $"FfmpegArgs mentions \"{forbidden}\" - the overlay must not be able to change what is recorded.");
                Assert.False(recorder.Contains(forbidden, StringComparison.Ordinal),
                    $"FfmpegCameraRecorder mentions \"{forbidden}\" - the overlay must not reach the recorded file.");
            }

            // And the scan is not vacuous: these files really are the camera recording path.
            Assert.Contains("CameraPreview", args);
            Assert.Contains("camera", recorder, StringComparison.OrdinalIgnoreCase);
        }
    }
}
