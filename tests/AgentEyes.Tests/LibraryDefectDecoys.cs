using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;

namespace AgentEyes.Tests.LibraryDefects
{
    /// <summary>
    /// The Library defects of issue #178, COMPILED FOR REAL - one decoy per claim the structural
    /// guards make.
    ///
    /// Why this file exists. A guard that passes by finding nothing proves nothing: it would report
    /// exactly the same "no offenders" against a renamed method, a filter that matches no route, or
    /// an assembly it never read. The independent review of PR #179 made that concrete twice - first
    /// the date scan's filter EXCLUDED MainWindow.Record_Click, StopAsync and PackageDirAsync, then
    /// it turned out the scan could not follow a call at all, so a helper reached FROM the card's
    /// date path could read the filesystem with both date guards still green.
    ///
    /// So each defect is written out here, in the test assembly, under the very type and method names
    /// the guards filter on. The guard is then pointed at THIS assembly and must REPORT every one of
    /// them. That is the negative control: proof the guard fails when the defect is present, taken on
    /// the same instrument that certifies the product.
    ///
    /// Some decoys exist to prove the opposite - that a guard is NARROW enough. <see cref="Grouping"/>
    /// groups a collection view without touching anything the Library owns, and the grouping guard
    /// must NOT report it: a grouped view built for some other feature later is not a Library defect,
    /// and a guard that punishes unrelated work is a guard someone eventually deletes.
    ///
    /// Nothing here is ever called. These methods exist to be READ - as IL by CompiledCode.
    /// </summary>
    internal static class DecoyNote
    {
        public const string Why =
            "Negative controls for LibraryFlatListTests. Never called; read as IL.";
    }

    /// <summary>The card. Named RecentItem so the date-path scans' ".RecentItem::" matches it.</summary>
    internal static class RecentItem
    {
        /// <summary>
        /// The TRANSITIVE defect, written exactly as the round-2 review wrote it: the card's date
        /// path does not read the filesystem itself, it calls a helper that does. Every scan that
        /// inventories a LIST of method names reports this as clean, because
        /// <see cref="LibraryDateFallback"/> is not on any list. Only following the call finds it.
        /// </summary>
        public static DateTime From(string dir) => LibraryDateFallback.For(dir);

        /// <summary>The direct half: "now" as the default when the manifest says nothing.</summary>
        public static DateTime StartUtc(string? createdUtc) =>
            string.IsNullOrWhiteSpace(createdUtc) ? DateTime.UtcNow : DateTime.Parse(createdUtc);

        // The remaining seeds of the card's date path, so the scan's fail-closed "every seed must
        // exist" check is satisfied on this assembly too. These are deliberately BENIGN: the control
        // has to show the guard catching the defect in the helper, not drown in decoy noise.
        public static DateTime RefreshNaming(string dir) => From(dir);
        public static string DateLabel(DateTime? startedLocal) =>
            startedLocal.HasValue ? startedLocal.Value.ToString("MMM d, yyyy") : "Undated";
        public static DateTime? StartedLocal => null;
        public static string DateText => "Undated";
    }

    /// <summary>The helper nobody named. Reached only from <see cref="RecentItem.From"/>, which is
    /// the whole point - a guard that cannot follow that one call cannot see this.</summary>
    internal static class LibraryDateFallback
    {
        public static DateTime For(string dir) => Directory.GetCreationTime(dir);
    }

    /// <summary>The ordering rule. Named so the scans' ".NewestFirstComparer::" matches it.</summary>
    internal static class NewestFirstComparer
    {
        /// <summary>An ordering rule that asks what time it is - a card can then be "newer" or
        /// "older" depending on when the list was drawn.</summary>
        public static int Compare(DateTime x) => DateTime.Now.CompareTo(x);
    }

    /// <summary>The loader's snapshot, dating a recording by the folder it happens to sit in.</summary>
    internal static class LibrarySnapshot
    {
        public static DateTime NewestFirst(string root) => Directory.GetLastWriteTimeUtc(root);
    }

    /// <summary>
    /// The window. Every method here is named after a REAL current library route, so the direct
    /// date scan has to match it: the two live insert routes (a screenshot from Record_Click, a
    /// saved recording from StopAsync), the refresh route (PackageDirAsync), the loader, and the
    /// re-sort helper.
    /// </summary>
    internal static class MainWindow
    {
        public static DateTime Record_Click(string dir) => Directory.GetCreationTime(dir);
        public static DateTime StopAsync() => DateTime.Now;
        public static DateTime PackageDirAsync(string dir) => File.GetLastWriteTime(dir);
        public static DateTime LoadRecent(string dir) => Directory.GetLastWriteTimeUtc(dir);
        public static DateTime ResortLibrary() => DateTimeOffset.Now.LocalDateTime;
    }

    /// <summary>
    /// Day grouping put back on the LIBRARY, outside the constructor and outside the XAML - the two
    /// places round 1 showed a narrow guard would miss. Each method here handles the Library by its
    /// own fields, which is what makes it a Library defect rather than someone else's grouped view.
    /// </summary>
    internal sealed class LibraryWindow
    {
        private readonly ObservableCollection<object> _recent = new();
        private readonly ListBox RecentList = new();

        /// <summary>Grouping added where the mode is applied, not where the view is built.</summary>
        public void ApplyLibraryMode() => RecentList.GroupStyle.Add(new GroupStyle());

        /// <summary>Grouping added from a Loaded handler, through the interface.</summary>
        public void OnLoaded()
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(_recent);
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
        }

        /// <summary>Handles the Library and does NOT group it - the guard must stay quiet about a
        /// method merely because it touches the collection.</summary>
        public int Count() => _recent.Count;
    }

    /// <summary>
    /// Grouping that has nothing to do with the Library: no Library collection, no Library list.
    /// The NARROWNESS control - the grouping guard must NOT report any of these. A future grouped
    /// view for some other feature is exactly this shape, and failing it would be a false alarm that
    /// costs the guard its life.
    /// </summary>
    internal static class Grouping
    {
        public static void ThroughTheConcreteView(ListCollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("Kind"));

        public static void ThroughTheInterface(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("Kind"));

        public static void ThroughTheItemsControl(ItemsControl list) =>
            list.GroupStyle.Add(new GroupStyle());
    }
}
