using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #43 - that the recording schematic is actually WIRED INTO the preset editor, read as
    /// source facts out of PresetEditor.xaml and PresetEditor.xaml.cs.
    ///
    /// They are source facts for the same reason as issue #36's: the editor is a WPF window that
    /// needs a running app to render. The point of this file is the half a geometry test cannot
    /// reach - that moving "Size on screen" and changing "Corner" reach a DRAWING and not only a text
    /// label. That is exactly the defect this issue exists for, and every assertion here FAILS
    /// against the code that shipped it: v1.7.0 had no schematic, and its OverlayCorner_Changed
    /// deliberately redrew nothing.
    ///
    /// WHAT THESE CANNOT SEE, stated rather than implied:
    ///  - that the drawn inset looks right on screen, or that it is where a person would say the
    ///    corner is. That is the screenshot pair (AC1) and the four corner screenshots (AC2).
    ///  - the arithmetic itself - that is <see cref="InsetSchematicTests"/>.
    /// </summary>
    public class InsetSchematicUiTests
    {
        private const string XamlPath = @"src\AgentEyes.App\PresetEditor.xaml";
        private const string EditorCodePath = @"src\AgentEyes.App\PresetEditor.xaml.cs";

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static Dictionary<string, XElement> Named()
        {
            var doc = XDocument.Parse(RepoSource.Read(XamlPath));
            return doc.Descendants()
                      .Where(e => e.Attribute(X + "Name") != null)
                      .ToDictionary(e => e.Attribute(X + "Name")!.Value, e => e);
        }

        // ---- AC1 / AC2: there is something to draw into, and it is drawn ----

        [Fact]
        public void PresetEditor_CarriesTheRecordingSchematic()
        {
            var named = Named();

            // The instrument itself must not come back empty.
            Assert.True(named.Count >= 40,
                $"Only {named.Count} named controls found in {XamlPath} - the scan is broken, not the UI.");

            var expected = new Dictionary<string, string>
            {
                ["InsetSchematicBorder"] = "Border",
                ["InsetSchematicCanvas"] = "Canvas",
                ["InsetSchematicScreenPath"] = "Path",
                ["InsetSchematicInsetPath"] = "Path",
                ["InsetSchematicCaption"] = "TextBlock",
            };

            var missing = expected.Keys.Where(n => !named.ContainsKey(n)).ToList();
            Assert.True(missing.Count == 0,
                "The recording schematic (issue #43) is missing: " + string.Join(", ", missing));

            foreach (var (name, type) in expected)
                Assert.True(named[name].Name.LocalName == type,
                    $"{name} is a {named[name].Name.LocalName}, expected a {type}.");

            // 16:9 - it stands for a recording, so it is shaped like one (assumption F1).
            var border = named["InsetSchematicBorder"];
            double width = double.Parse(border.Attribute("Width")!.Value);
            double height = double.Parse(border.Attribute("Height")!.Value);
            Assert.Equal(16.0 / 9.0, width / height, 2);

            // At the top of the slider's range the inset is taller than the recording. The HUD clips
            // it; so must this, or the drawing spills over the panel beside it.
            Assert.Equal("True", named["InsetSchematicCanvas"].Attribute("ClipToBounds")?.Value);
        }

        [Fact]
        public void SizeOnScreen_ReachesTheDrawing_NotOnlyItsLabel()
        {
            // THE DEFECT ITSELF. The slider was always wired to Overlay_Changed and its label was
            // always updated; what was missing is that nothing drawn used the inset fraction. So the
            // assertion is on the DRAWING path: the one place that updates this panel must redraw the
            // schematic, and that redraw must place the inset from the framing the controls hold.
            string code = RepoSource.Read(EditorCodePath);

            string update = RepoSource.MethodBody(code, "private void UpdateOverlayUi()");
            Assert.Contains("RedrawInsetSchematic(circle)", update);

            string redraw = RepoSource.MethodBody(code, "private void RedrawInsetSchematic(bool circle)");
            Assert.Contains("InsetSchematic.Place(", redraw);
            Assert.Contains("ReadOverlay()", redraw);
            Assert.Contains("InsetSchematicInsetPath.Data =", redraw);

            // The slider still runs through the same handler, and that handler still redraws.
            var xaml = Named();
            Assert.Equal("Overlay_Changed", xaml["InsetSizeSlider"].Attribute("ValueChanged")?.Value);
            Assert.Contains("UpdateOverlayUi()",
                            RepoSource.MethodBody(code, "private void Overlay_Changed("));
        }

        [Fact]
        public void Corner_RedrawsTheSchematic_InsteadOfLoggingAndStopping()
        {
            // Before this issue the corner handler said "Nothing to redraw" and meant it - a real
            // choice that moved nothing on screen.
            string code = RepoSource.Read(EditorCodePath);
            string body = RepoSource.MethodBody(code, "private void OverlayCorner_Changed(");

            Assert.Contains("UpdateOverlayUi()", body);
            Assert.DoesNotContain("Nothing to redraw", body);

            // And the placement really is the corner's: the schematic asks the framing for it.
            string place = RepoSource.MethodBody(RepoSource.Read(@"src\AgentEyes.Core\Preview\InsetSchematic.cs"),
                                                 "public static OverlayRect Place(");
            Assert.Contains("overlay.CornerValue", place);
            Assert.Contains("overlay.ClampedInsetFraction", place);
        }

        [Fact]
        public void TheSchematic_IsRedrawnWhenItIsFirstLaidOut()
        {
            // A canvas has no size until WPF has laid it out, and the schematic refuses to draw to a
            // size it does not know. Without this handler the box would stay empty until the first
            // slider move - which looks exactly like the bug being fixed.
            var named = Named();
            Assert.Equal("InsetSchematic_SizeChanged", named["InsetSchematicCanvas"].Attribute("SizeChanged")?.Value);

            string body = RepoSource.MethodBody(RepoSource.Read(EditorCodePath),
                                                "private void InsetSchematic_SizeChanged(");
            Assert.Contains("UpdateOverlayUi()", body);
        }

        // ---- AC3: the two sliders are told apart ----------------------------

        [Fact]
        public void TheTwoSliders_AreGroupedByWhatTheyChange()
        {
            // "Diameter" and "Size on screen" both sound like "how big is the circle". The panel now
            // says which frame of reference each one belongs to, in a heading above it.
            var named = Named();

            string cameraHeading = named["CameraFrameGroupHeader"].Attribute("Text")!.Value;
            string recordingHeading = named["RecordingGroupHeader"].Attribute("Text")!.Value;

            Assert.Contains("CAMERA PICTURE", cameraHeading, StringComparison.Ordinal);
            Assert.Contains("RECORDING", recordingHeading, StringComparison.Ordinal);

            // The headings are on the right groups: the diameter under the camera one, the corner and
            // the inset under the recording one.
            string xaml = RepoSource.Read(XamlPath);
            int camera = xaml.IndexOf("CameraFrameGroupHeader", StringComparison.Ordinal);
            int recording = xaml.IndexOf("RecordingGroupHeader", StringComparison.Ordinal);
            int diameter = xaml.IndexOf("CircleSizeSlider", StringComparison.Ordinal);
            int inset = xaml.IndexOf("InsetSizeSlider", StringComparison.Ordinal);
            int corner = xaml.IndexOf("OverlayCornerBox", StringComparison.Ordinal);
            int schematic = xaml.IndexOf("InsetSchematicBorder", StringComparison.Ordinal);

            Assert.True(camera >= 0 && recording > camera, "The two headings are not both in the panel, in order.");
            Assert.InRange(diameter, camera, recording);
            Assert.True(inset > recording, "The inset slider is not under the recording heading.");
            Assert.True(corner > recording, "The corner picker is not under the recording heading.");
            Assert.True(schematic > recording, "The schematic is not under the recording heading.");
        }

        // ---- AC5 / AC6 / AC7: what this must not have changed ---------------

        [Fact]
        public void TheSchematic_ChangesNothingThatIsSavedOrRecorded()
        {
            string redraw = RepoSource.MethodBody(RepoSource.Read(EditorCodePath),
                                                  "private void RedrawInsetSchematic(bool circle)");

            // A drawing that writes back into the controls would be a drawing that can change what is
            // saved. It reads the framing; it never assigns to a control's Value.
            foreach (string forbidden in new[]
                     {
                         "InsetSizeSlider.Value =", "CircleSizeSlider.Value =", "CircleXSlider.Value =",
                         "CircleYSlider.Value =", "OverlayCornerBox.SelectedIndex =", "_preset",
                     })
                Assert.False(redraw.Contains(forbidden, StringComparison.Ordinal),
                    $"RedrawInsetSchematic touches {forbidden} - drawing must not change what is stored.");

            // And it starts no capture of its own: it is a schematic, not a composite (F1).
            foreach (string forbidden in new[] { "ScreenCapture", "Recorder", "StartCapture", "Screenshot" })
                Assert.False(redraw.Contains(forbidden, StringComparison.Ordinal),
                    $"RedrawInsetSchematic mentions {forbidden} - it must not capture anything.");
        }

        [Fact]
        public void TheCameraPreviewAndItsCircle_AreUntouched()
        {
            // AC7. The live preview, the circle adorner and the release path are the parts of this
            // panel that took several rounds to get right; this issue adds a second drawing beside
            // them (assumption F2) and must not have moved them.
            var named = Named();

            foreach (string name in new[]
                     {
                         "CameraPreviewPanel", "CameraPreviewImage", "CameraPreviewStatus",
                         "CameraOverlayAdorner", "OverlayMaskPath", "OverlayOutlinePath",
                         "CircleXSlider", "CircleYSlider", "CircleSizeSlider", "InsetSizeSlider",
                         "OverlayCornerBox", "OverlayResetButton", "OverlayHint", "CircleControls",
                         "OverlayControls", "OverlayShapeCircle", "OverlayShapeRectangle",
                     })
                Assert.True(named.ContainsKey(name), $"{name} is gone - UI Automation and gui-smoke drive it by name.");

            string code = RepoSource.Read(EditorCodePath);
            string update = RepoSource.MethodBody(code, "private void UpdateOverlayUi()");
            Assert.Contains("RedrawOverlayAdorner(circle)", update);

            // The camera is still handed back from the one place every close route passes through.
            Assert.Contains("_cameraPreview.Dispose()", code);
        }
    }
}
