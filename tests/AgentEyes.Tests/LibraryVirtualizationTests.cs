using System;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #175 - the guard that stops the Library card grid rendering each day group's cards
    /// under the wrong header.
    ///
    /// The library list swaps its items panel at runtime: grid mode installs a plain
    /// <c>WrapPanel</c>, list mode installs a <c>VirtualizingStackPanel</c>. The virtualization
    /// attached properties (IsVirtualizing / IsVirtualizingWhenGrouping / VirtualizationMode /
    /// ScrollUnit) only take effect on a VirtualizingPanel, so pinning them on the ListBox in XAML
    /// made them honest in list mode and untrue in grid mode. With day grouping on, WPF built the
    /// group-virtualization and container-recycling machinery over a panel that cannot support it
    /// and mapped recycled containers onto the wrong GroupItem - correct data, correct headers,
    /// wrong cards underneath them.
    ///
    /// The fix is that the panel and the virtualization it supports are set TOGETHER, in
    /// ApplyLibraryMode, and nowhere else. These tests pin that invariant as a source fact - the
    /// repo's precedent for wiring inside the WPF app, which a unit test cannot otherwise reach
    /// (see StopPathTests / RepoSource).
    ///
    /// Issue #178 then deleted the day grouping entirely, so there are no group headers left for a
    /// recycled container to land under - the Library is one flat list (LibraryFlatListTests holds
    /// that). This guard survives that change on its own terms: the flags below only take effect on
    /// a VirtualizingPanel whether or not anything is grouped, and "the panel and its flags are
    /// decided in one place" is the invariant, not "grouping is safe". IsVirtualizingWhenGrouping is
    /// still set with the rest of the stack for exactly that reason - a flag left out of the single
    /// configurator is how the set drifted apart in the first place.
    /// </summary>
    public sealed class LibraryVirtualizationTests
    {
        private const string Xaml = @"src\AgentEyes.App\MainWindow.xaml";
        private const string CodeBehind = @"src\AgentEyes.App\MainWindow.xaml.cs";

        /// <summary>Every virtualization attached property that requires a VirtualizingPanel.</summary>
        private static readonly string[] VirtualizationProperties =
        {
            "VirtualizingPanel.IsVirtualizing",
            "VirtualizingPanel.IsVirtualizingWhenGrouping",
            "VirtualizingPanel.VirtualizationMode",
            "VirtualizingPanel.ScrollUnit",
        };

        // ---- criterion 5: the properties are not pinned on the ListBox ------

        [Fact]
        public void RecentList_PinsNoVirtualizationInXaml_BecauseGridModeSwapsInAPlainWrapPanel()
        {
            string element = RecentListElement();

            // Proves the extraction found the real element rather than an empty string - an
            // assertion that passes by finding nothing is not a guard.
            Assert.Contains(@"x:Name=""RecentList""", element, StringComparison.Ordinal);

            foreach (string property in VirtualizationProperties)
                Assert.False(element.Contains(property, StringComparison.Ordinal),
                    $"MainWindow.xaml pins {property} on RecentList. ApplyLibraryMode can swap in a "
                    + "plain WrapPanel, which is not a VirtualizingPanel, so this property would be "
                    + "untrue in grid mode (issue #175). Set it in ApplyLibraryMode with the panel.");
        }

        [Fact]
        public void RecentList_DoesNotPinTheModeDependentSettersEither_SoThereIsOnePlaceOnly()
        {
            // Template, panel and container style all vary by mode too. Leaving a copy in XAML is
            // how the virtualization flags drifted away from the panel in the first place.
            string element = RecentListElement();
            foreach (string property in new[] { "ItemsPanel=", "ItemTemplate=", "ItemContainerStyle=" })
                Assert.False(element.Contains(property, StringComparison.Ordinal),
                    $"MainWindow.xaml pins {property} on RecentList; ApplyLibraryMode owns it (issue #175).");
        }

        // ---- the panels this is all about are still what we think they are ---

        [Fact]
        public void GridPanel_IsAPlainWrapPanel_WhichIsWhyVirtualizationMustBeOff()
        {
            string template = ItemsPanelTemplate("LibraryWrapPanel");
            Assert.Contains("<WrapPanel", template, StringComparison.Ordinal);
            Assert.DoesNotContain("Virtualizing", template, StringComparison.Ordinal);
        }

        /// <summary>Criterion 3: list mode keeps its VirtualizingStackPanel and stays virtualized.</summary>
        [Fact]
        public void ListPanel_IsStillAVirtualizingStackPanel()
        {
            Assert.Contains("<VirtualizingStackPanel", ItemsPanelTemplate("LibraryStackPanel"),
                StringComparison.Ordinal);
        }

        // ---- the panel and its virtualization are set together ---------------

        [Fact]
        public void ApplyLibraryMode_SetsTheVirtualizationThatMatchesThePanelItInstalls()
        {
            string body = ApplyLibraryModeBody();

            // It installs the panel...
            Assert.Contains("RecentList.ItemsPanel", body, StringComparison.Ordinal);
            Assert.Contains("LibraryWrapPanel", body, StringComparison.Ordinal);
            Assert.Contains("LibraryStackPanel", body, StringComparison.Ordinal);

            // ...and, in the same method, every flag that depends on which panel that was.
            foreach (string setter in new[]
                     {
                         "VirtualizingPanel.SetIsVirtualizing(RecentList,",
                         "VirtualizingPanel.SetIsVirtualizingWhenGrouping(RecentList,",
                         "VirtualizingPanel.SetVirtualizationMode(RecentList,",
                         "VirtualizingPanel.SetScrollUnit(RecentList,",
                     })
                Assert.Contains(setter, body, StringComparison.Ordinal);
        }

        [Fact]
        public void ApplyLibraryMode_TurnsVirtualizationOffInGridMode()
        {
            string body = ApplyLibraryModeBody();

            // Grid mode (_libraryGrid == true) must be the NOT-virtualizing case.
            Assert.Contains("bool virtualizing = !_libraryGrid;", body, StringComparison.Ordinal);
            Assert.Contains("VirtualizingPanel.SetIsVirtualizing(RecentList, virtualizing);", body,
                StringComparison.Ordinal);
            Assert.Contains("VirtualizingPanel.SetIsVirtualizingWhenGrouping(RecentList, virtualizing);", body,
                StringComparison.Ordinal);

            // Recycling is the mode that mis-maps containers onto group headers; it belongs to the
            // virtualizing branch only.
            Assert.Matches(new Regex(@"SetVirtualizationMode\(RecentList,\s*virtualizing\s*\?\s*VirtualizationMode\.Recycling"),
                body);
        }

        [Fact]
        public void MainWindowConstructor_AppliesTheLibraryMode_SoTheFirstPaintIsConfiguredToo()
        {
            // Nothing pins the mode in XAML any more, so the very first paint has to go through the
            // same single configurator - otherwise the grid opens with WPF's virtualizing defaults.
            string ctor = RepoSource.MethodBody(RepoSource.Read(CodeBehind),
                "internal MainWindow(RecordingService svc, Config cfg, Action showTests, RepairService repair)");
            Assert.Contains("ApplyLibraryMode();", ctor, StringComparison.Ordinal);
        }

        // ---- extraction helpers (each throws rather than returning nothing) ---

        /// <summary>The RecentList opening tag, from "&lt;ListBox" to the closing "&gt;", quotes
        /// respected. Scoping to the tag is what keeps the surrounding comment out of the
        /// assertions.</summary>
        private static string RecentListElement()
        {
            string xaml = RepoSource.Read(Xaml);
            int start = xaml.IndexOf(@"<ListBox x:Name=""RecentList""", StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("The RecentList ListBox is not in MainWindow.xaml any more.");

            bool inQuotes = false;
            for (int i = start; i < xaml.Length; i++)
            {
                char c = xaml[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (c == '>' && !inQuotes) return xaml.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("The RecentList ListBox tag is unterminated.");
        }

        /// <summary>One named ItemsPanelTemplate resource, from its key to its closing tag.</summary>
        private static string ItemsPanelTemplate(string key)
        {
            string xaml = RepoSource.Read(Xaml);
            int start = xaml.IndexOf($@"<ItemsPanelTemplate x:Key=""{key}""", StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException($"The '{key}' ItemsPanelTemplate is not in MainWindow.xaml any more.");

            int end = xaml.IndexOf("</ItemsPanelTemplate>", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException($"The '{key}' ItemsPanelTemplate is unterminated.");
            return xaml.Substring(start, end - start);
        }

        private static string ApplyLibraryModeBody() =>
            RepoSource.MethodBody(RepoSource.Read(CodeBehind), "private void ApplyLibraryMode()");
    }
}
