using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #35: the preset editor was one tall 480px-wide column, so a scrollbar sat permanently
    /// down its right edge. It is now a generously sized TABBED window - Capture / Audio / Camera -
    /// with the name/note header outside the tabs and a live camera preview pane on the Camera tab.
    ///
    /// These are SOURCE facts about PresetEditor.xaml/.cs, not runtime ones, so they are read
    /// straight out of the markup and code (RepoSource). Each assertion is a PRESENCE claim - a
    /// missing control, a missing tab, a deleted ScrollViewer or an ungated preview FAILS rather
    /// than passing by finding nothing.
    ///
    /// WHAT THESE TESTS CANNOT SEE: whether the rendered content actually fits without scrolling at
    /// the default window size, and whether the preview really releases the camera. The first is a
    /// measured runtime fact (ScrollViewer extent vs viewport) proven against the real window by
    /// docs/cencon/proof/issue-35/probe; the second is behaviour and is covered by
    /// CameraPreviewTests, which exercises CameraPreviewController itself.
    /// </summary>
    public class PresetEditorLayoutTests
    {
        private const string XamlPath = @"src\AgentEyes.App\PresetEditor.xaml";
        private const string CodePath = @"src\AgentEyes.App\PresetEditor.xaml.cs";

        private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>
        /// Every x:Name the editor carried before the tabbed rewrite. The GUI smoke test and any UIA
        /// automation address these controls by name, so losing or renaming one breaks automation
        /// that has nothing to do with the layout (issue #35, AC4).
        /// </summary>
        private static readonly string[] RequiredNames =
        {
            "AspectBox", "CameraBox", "CameraHint", "CancelButton", "DenoiseCheck", "ErrorText",
            "ExactHeightBox", "ExactWidthBox", "FpsBox", "FullRadio", "GateCheck", "LandscapeButton",
            "LevelCheck", "MicBox", "MicVol", "MicVolText", "ModeAudio", "ModeShot", "ModeVideo",
            "MonitorBox", "NameBox", "NoteBox", "RegionLabel", "RegionOptions", "RegionRadio",
            "RegionWarn", "SaveAsButton", "SaveButton", "SelectAreaButton", "SetExactButton",
            "ShowAreaButton", "SquareButton", "SrcMic", "SrcMixed", "SrcSystem", "SysVol",
            "SysVolText", "VerticalButton",
            // Issue #29's live preview, which this issue moves onto the Camera tab.
            "CameraPreviewPanel", "CameraPreviewImage", "CameraPreviewStatus",
        };

        private static XDocument Xaml() => XDocument.Parse(RepoSource.Read(XamlPath));

        private static Dictionary<string, string> NamedElements(XDocument doc) =>
            doc.Descendants()
               .Where(e => e.Attribute(X + "Name") != null)
               .ToDictionary(e => e.Attribute(X + "Name")!.Value, e => e.Name.LocalName);

        private static XElement TabControl(XDocument doc)
        {
            var tabs = doc.Descendants(Wpf + "TabControl").SingleOrDefault();
            Assert.True(tabs != null, "The preset editor has no TabControl - the tabbed layout is gone.");
            return tabs!;
        }

        [Fact]
        public void PresetEditorXaml_AfterTabbedRewrite_KeepsEveryControlName()
        {
            var named = NamedElements(Xaml());

            // An empty scan is a broken instrument, never a clean run.
            Assert.True(named.Count >= RequiredNames.Length,
                $"Only {named.Count} x:Name'd controls found in {XamlPath} - expected at least {RequiredNames.Length}.");

            var missing = RequiredNames.Where(n => !named.ContainsKey(n)).ToList();
            Assert.True(missing.Count == 0,
                "PresetEditor.xaml lost or renamed control(s) UIA depends on: " + string.Join(", ", missing));
        }

        [Fact]
        public void PresetEditorXaml_NamedControls_KeepTheirOriginalControlType()
        {
            var named = NamedElements(Xaml());

            var expectedTypes = new Dictionary<string, string>
            {
                ["NameBox"] = "TextBox",
                ["NoteBox"] = "TextBox",
                ["MonitorBox"] = "ComboBox",
                ["CameraBox"] = "ComboBox",
                ["MicBox"] = "ComboBox",
                ["FpsBox"] = "ComboBox",
                ["AspectBox"] = "ComboBox",
                ["ExactWidthBox"] = "TextBox",
                ["ExactHeightBox"] = "TextBox",
                ["MicVol"] = "Slider",
                ["SysVol"] = "Slider",
                ["RegionOptions"] = "StackPanel",
                ["FullRadio"] = "RadioButton",
                ["RegionRadio"] = "RadioButton",
                ["ModeShot"] = "RadioButton",
                ["ModeAudio"] = "RadioButton",
                ["ModeVideo"] = "RadioButton",
                ["SrcMic"] = "RadioButton",
                ["SrcSystem"] = "RadioButton",
                ["SrcMixed"] = "RadioButton",
                ["DenoiseCheck"] = "CheckBox",
                ["GateCheck"] = "CheckBox",
                ["LevelCheck"] = "CheckBox",
                ["SaveButton"] = "Button",
                ["SaveAsButton"] = "Button",
                ["CancelButton"] = "Button",
                ["SelectAreaButton"] = "Button",
                ["SetExactButton"] = "Button",
                ["ShowAreaButton"] = "Button",
                ["SquareButton"] = "Button",
                ["VerticalButton"] = "Button",
                ["LandscapeButton"] = "Button",
                ["CameraPreviewImage"] = "Image",
                ["CameraPreviewStatus"] = "TextBlock",
                ["CameraPreviewPanel"] = "Border",
            };

            foreach (var (name, type) in expectedTypes)
            {
                Assert.True(named.ContainsKey(name), $"{name} is gone from {XamlPath}.");
                Assert.True(named[name] == type,
                    $"{name} changed control type: expected {type}, found {named[name]}.");
            }
        }

        [Fact]
        public void PresetEditorXaml_Settings_AreSplitAcrossCaptureAudioAndCameraTabs()
        {
            var doc = Xaml();
            var tabs = TabControl(doc).Elements(Wpf + "TabItem").ToList();
            Assert.True(tabs.Count >= 3, $"Expected at least 3 tabs, found {tabs.Count}.");

            var headers = tabs.Select(t => t.Attribute("Header")?.Value ?? "").ToList();
            Assert.Contains("Capture", headers);
            Assert.Contains("Audio", headers);
            Assert.Contains("Camera", headers);

            // The settings each tab is supposed to own, so a tab cannot become an empty shell.
            var expected = new Dictionary<string, string[]>
            {
                ["Capture"] = new[] { "MonitorBox", "FullRadio", "RegionRadio", "RegionOptions", "ModeVideo", "FpsBox" },
                ["Audio"] = new[] { "MicBox", "SrcMic", "SrcMixed", "DenoiseCheck", "MicVol", "SysVol" },
                ["Camera"] = new[] { "CameraBox", "CameraHint", "CameraPreviewPanel", "CameraPreviewImage" },
            };

            foreach (var (header, names) in expected)
            {
                var tab = tabs.Single(t => (t.Attribute("Header")?.Value ?? "") == header);
                var inTab = tab.Descendants()
                               .Where(e => e.Attribute(X + "Name") != null)
                               .Select(e => e.Attribute(X + "Name")!.Value)
                               .ToHashSet(StringComparer.Ordinal);
                var missing = names.Where(n => !inTab.Contains(n)).ToList();
                Assert.True(missing.Count == 0,
                    $"The {header} tab is missing: {string.Join(", ", missing)}");
            }

            // Name and note stay OUTSIDE the tabs so they are readable from every tab.
            var tabNames = TabControl(doc).Descendants()
                                          .Where(e => e.Attribute(X + "Name") != null)
                                          .Select(e => e.Attribute(X + "Name")!.Value)
                                          .ToHashSet(StringComparer.Ordinal);
            Assert.False(tabNames.Contains("NameBox"), "NameBox moved inside a tab - it must stay in the header.");
            Assert.False(tabNames.Contains("NoteBox"), "NoteBox moved inside a tab - it must stay in the header.");
        }

        [Fact]
        public void PresetEditorXaml_EveryTab_KeepsItsOwnScrollSafetyNet()
        {
            var tabs = TabControl(Xaml()).Elements(Wpf + "TabItem").ToList();
            Assert.NotEmpty(tabs);

            foreach (var tab in tabs)
            {
                string header = tab.Attribute("Header")?.Value ?? "(unnamed)";
                var scroller = tab.Elements(Wpf + "ScrollViewer").SingleOrDefault();
                Assert.True(scroller != null,
                    $"The {header} tab has no ScrollViewer - the small-screen safety net went with it.");
                Assert.Equal("Auto", scroller!.Attribute("VerticalScrollBarVisibility")?.Value);
            }
        }

        [Fact]
        public void PresetEditorXaml_CameraTab_CarriesAPreviewPaneBigEnoughToJudgeFraming()
        {
            var doc = Xaml();
            var panel = doc.Descendants(Wpf + "Border")
                           .Single(e => e.Attribute(X + "Name")?.Value == "CameraPreviewPanel");

            double w = double.Parse(panel.Attribute("Width")!.Value);
            double h = double.Parse(panel.Attribute("Height")!.Value);
            Assert.True(w >= 320 && h >= 240,
                $"The live preview pane is {w}x{h}; issue #35 AC8 requires at least 320x240.");

            var image = panel.Descendants(Wpf + "Image")
                             .Single(e => e.Attribute(X + "Name")?.Value == "CameraPreviewImage");
            Assert.Equal("Uniform", image.Attribute("Stretch")?.Value);
        }

        [Fact]
        public void PresetEditorXaml_Window_IsGenerouslySizedAndResizable()
        {
            var window = Xaml().Root!;
            Assert.Equal("Window", window.Name.LocalName);

            int width = int.Parse(window.Attribute("Width")!.Value);
            int height = int.Parse(window.Attribute("Height")!.Value);
            Assert.True(width >= 900, $"The preset editor is {width} wide; issue #35 asked for a generously sized window.");
            Assert.True(height >= 700, $"The preset editor is {height} tall; issue #35 asked for a generously sized window.");

            // AC10 needs the window to be resizable for its size to be worth remembering.
            Assert.Equal("CanResize", window.Attribute("ResizeMode")?.Value);
        }

        [Fact]
        public void PresetEditorCode_CameraPreview_StopsWhenTheCameraTabIsNotShowing()
        {
            // AC9: a preview nobody can see must not hold an exclusive DirectShow device. This reads
            // the ONE method that decides whether the preview runs, so the rule cannot be satisfied
            // by some unrelated mention of the tab elsewhere in the file.
            //
            // CANNOT SEE: that Stop actually releases the device - that is CameraPreviewController's
            // job and CameraPreviewTests proves it.
            string body = RepoSource.MethodBody(RepoSource.Read(CodePath), "private void UpdateCameraPreview()");
            Assert.Contains("CameraTab", body);
            Assert.Contains("_cameraPreview.Stop(", body);

            // And the tab change has to reach that method, or the rule would never fire.
            string onTab = RepoSource.MethodBody(RepoSource.Read(CodePath),
                "private void EditorTabs_Changed(object sender, SelectionChangedEventArgs e)");
            Assert.Contains("UpdateCameraPreview()", onTab);
        }

        [Fact]
        public void PresetEditorCode_RemembersItsTabAndWindowPlacement()
        {
            // AC10. Both halves must be there: writing the state on close, and reading it on open.
            string code = RepoSource.Read(CodePath);
            string remember = RepoSource.MethodBody(code, "private void RememberWindowState()");
            foreach (string key in new[] { "PresetEditorTab", "PresetEditorWidth", "PresetEditorHeight",
                                           "PresetEditorLeft", "PresetEditorTop", "_cfg.Save()" })
                Assert.Contains(key, remember);

            string restore = RepoSource.MethodBody(code, "private void RestoreWindowState()");
            foreach (string key in new[] { "PresetEditorTab", "PresetEditorWidth", "PresetEditorLeft" })
                Assert.Contains(key, restore);
        }
    }
}
