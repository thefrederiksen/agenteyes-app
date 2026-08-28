using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #35: the preset editor was one tall 480px-wide column, so a scrollbar sat permanently
    /// down its right edge. It is now two columns in a wider window.
    ///
    /// These are SOURCE facts about PresetEditor.xaml, not runtime ones, so they are read straight
    /// out of the markup (RepoSource). Each assertion is a PRESENCE claim - a missing control, a
    /// collapsed column, or a deleted ScrollViewer FAILS rather than passing by finding nothing.
    ///
    /// What these tests CANNOT see: whether the rendered content actually fits without scrolling at
    /// the default window size. That is a measured runtime fact (ScrollViewer extent vs viewport) and
    /// is verified against the running app, not here.
    /// </summary>
    public class PresetEditorLayoutTests
    {
        private const string XamlPath = @"src\AgentEyes.App\PresetEditor.xaml";

        private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>
        /// Every x:Name the editor carried before the two-column rewrite. The GUI smoke test and any
        /// UIA automation address these controls by name, so losing or renaming one breaks automation
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
        };

        private static XDocument Xaml() => XDocument.Parse(RepoSource.Read(XamlPath));

        private static Dictionary<string, string> NamedElements(XDocument doc) =>
            doc.Descendants()
               .Where(e => e.Attribute(X + "Name") != null)
               .ToDictionary(e => e.Attribute(X + "Name")!.Value, e => e.Name.LocalName);

        [Fact]
        public void PresetEditorXaml_AfterTwoColumnLayout_KeepsEveryControlName()
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
            };

            foreach (var (name, type) in expectedTypes)
            {
                Assert.True(named.ContainsKey(name), $"{name} is gone from {XamlPath}.");
                Assert.True(named[name] == type,
                    $"{name} changed control type: expected {type}, found {named[name]}.");
            }
        }

        [Fact]
        public void PresetEditorXaml_Content_IsLaidOutInTwoColumns()
        {
            var doc = Xaml();
            var scroller = doc.Descendants(Wpf + "ScrollViewer").SingleOrDefault();
            Assert.True(scroller != null, "The preset editor's ScrollViewer is gone - the small-screen safety net went with it.");

            var grid = scroller!.Elements(Wpf + "Grid").SingleOrDefault();
            Assert.True(grid != null, "The ScrollViewer's content is not a Grid, so it cannot be two columns.");

            var columns = grid!.Element(Wpf + "Grid.ColumnDefinitions")?.Elements(Wpf + "ColumnDefinition").ToList();
            Assert.True(columns != null && columns.Count == 2,
                $"Expected exactly 2 columns inside the ScrollViewer, found {(columns?.Count ?? 0)}.");

            var placed = grid.Elements()
                             .Where(e => e.Name.LocalName != "Grid.ColumnDefinitions")
                             .Select(e => e.Attribute("Grid.Column")?.Value ?? "0")
                             .ToList();
            Assert.Contains("0", placed);
            Assert.Contains("1", placed);
        }

        [Fact]
        public void PresetEditorXaml_EverySetting_LivesInsideOneOfTheTwoColumns()
        {
            var doc = Xaml();
            var grid = doc.Descendants(Wpf + "ScrollViewer").Single().Elements(Wpf + "Grid").Single();

            // The settings named in the acceptance criteria must all sit inside the two-column grid -
            // a control left outside it would not be part of the column layout at all.
            string[] mustBeInColumns =
            {
                "NameBox", "NoteBox", "MonitorBox", "FullRadio", "RegionRadio", "RegionOptions",
                "CameraBox", "MicBox", "MicVol", "SysVol", "FpsBox", "ModeShot", "ModeAudio", "ModeVideo",
            };

            var inColumns = grid.Descendants()
                                .Where(e => e.Attribute(X + "Name") != null)
                                .Select(e => e.Attribute(X + "Name")!.Value)
                                .ToHashSet(StringComparer.Ordinal);

            var stray = mustBeInColumns.Where(n => !inColumns.Contains(n)).ToList();
            Assert.True(stray.Count == 0,
                "These settings are not inside the two-column grid: " + string.Join(", ", stray));
        }

        [Fact]
        public void PresetEditorXaml_Window_IsWideEnoughForTwoColumnsAndKeepsTheScrollSafetyNet()
        {
            var doc = Xaml();
            var window = doc.Root!;
            Assert.Equal("Window", window.Name.LocalName);

            int width = int.Parse(window.Attribute("Width")!.Value);
            Assert.True(width >= 860,
                $"The preset editor is {width} wide - too narrow for two columns (issue #35 widened it).");

            int height = int.Parse(window.Attribute("Height")!.Value);
            Assert.True(height >= 600, $"The preset editor default height is {height}; expected at least 600.");

            var scroller = doc.Descendants(Wpf + "ScrollViewer").Single();
            Assert.Equal("Auto", scroller.Attribute("VerticalScrollBarVisibility")?.Value);
        }
    }
}
