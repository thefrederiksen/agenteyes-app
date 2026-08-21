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
    internal static partial class RecentItem
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

        /// <summary>
        /// The round-3 gate's attack, in its exact shape (issue #2, item 1): the mode application
        /// groups NOTHING itself - it hands the Library's view to a helper, and only the helper
        /// groups it. The one-body scan this repository shipped reported no offender against this,
        /// while real day groups rendered on the real Library.
        /// </summary>
        public void ApplyLibraryModeThroughAHelper() =>
            ConfigureLibraryView(CollectionViewSource.GetDefaultView(_recent));

        /// <summary>The helper nobody named: it takes the view as an argument and never touches a
        /// Library field, so only following the call from its caller finds it.</summary>
        private static void ConfigureLibraryView(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("Title"));

        /// <summary>
        /// The round-1 gate's attack on the FIX PASS (issue #2): the handler groups nothing and
        /// calls no grouping helper directly - it instantiates an in-assembly implementation
        /// through an interface and calls Configure(view) through that interface. The IL call
        /// site targets the abstract interface method, so a walk that only follows calls into
        /// method BODIES never reaches <see cref="DayGroupConfigurer.Configure"/>, and real day
        /// groups return with every guard green. Only conservative dispatch-following finds it.
        /// </summary>
        public void ApplyLibraryModeThroughAnInterface()
        {
            ILibraryViewConfigurer configurer = new DayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>
        /// The round-2 review's refinement of the same attack (issue #2 fix pass, finding 1): the
        /// implementation the interface hides is INHERITED. The derived type carries the
        /// InterfaceImpl row but declares no Configure of its own; the body lives on its base
        /// class, which mentions no interface at all. A dispatch map that matches interface
        /// methods only against the implementing type's OWN methods has no edge here - the exact
        /// fail-open the review demonstrated - so the base-class body must be found by walking the
        /// implementing type's in-assembly base chain.
        /// </summary>
        public void ApplyLibraryModeThroughAnInheritedImplementation()
        {
            IInheritedViewConfigurer configurer = new InheritedDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>
        /// The round-2 review's second gap (issue #2 fix pass, finding 2): work hidden in a STATIC
        /// CONSTRUCTOR. No IL instruction anywhere calls a .cctor - the runtime invokes it when the
        /// type is first touched - so a call-graph walk with no implicit-invocation edges never
        /// reaches it, and grouping planted there passes every guard silently.
        /// </summary>
        public void ApplyLibraryModeThroughAStaticConstructor()
        {
            _ = _recent.Count;              // what makes this a Library handler
            CctorDayGroupConfigurer.Touch();
        }

        // ---- issue #2, round 3: one handler per remaining dispatch shape -----
        // The round-2 gate found dispatch shapes seriatim (direct interface, inherited
        // implementation, inherited interface declaration), so round 3 enumerates the rest
        // systematically. Each handler below compiles ONE dispatch shape the walk can meet,
        // and a walk-level regression in LibraryFlatListTests pins each one.

        /// <summary>
        /// The round-2 GATE's construction (issue #2, round 3): the interface method is INHERITED -
        /// IChildViewConfigurer : IBaseViewConfigurer, the declaration on the base interface, the
        /// implementing class names only the child, and the call goes through the BASE interface.
        /// The callee token is IBaseViewConfigurer::Configure, so a dispatch map that never
        /// traverses the interface inheritance graph has no edge from it.
        /// </summary>
        public void ApplyLibraryModeThroughAnInheritedInterfaceDeclaration()
        {
            IBaseViewConfigurer configurer = new InheritedDeclarationDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>EXPLICIT interface implementation, of an INHERITED declaration at that: the
        /// body is a private method whose MethodImpl row is the only thing connecting it to
        /// IExplicitBaseConfigurer::Configure.</summary>
        public void ApplyLibraryModeThroughAnExplicitImplementation()
        {
            IExplicitBaseConfigurer configurer = new ExplicitDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>GENERIC INTERFACE INSTANTIATION: the call site's token parent is a TypeSpec
        /// (IGenericViewConfigurer&lt;ICollectionView&gt;), and the implementing class's
        /// InterfaceImpl row is a TypeSpec too - both must fold onto the open generic type or the
        /// edge's two ends never meet.</summary>
        public void ApplyLibraryModeThroughAGenericInterface()
        {
            IGenericViewConfigurer<ICollectionView> configurer = new GenericDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>CONSTRUCTED GENERIC METHOD: the call site's token is a MethodSpec
        /// (Configure&lt;ICollectionView&gt;), which must resolve onto the open generic method
        /// declaration before the dispatch edge can be found.</summary>
        public void ApplyLibraryModeThroughAGenericMethod()
        {
            IGenericMethodConfigurer configurer = new GenericMethodDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>DEFAULT INTERFACE METHOD: the interface method has a BODY of its own, and the
        /// implementing class overrides it. The walk must reach both - the default body directly
        /// (the callee has IL), and the override through the dispatch edge.</summary>
        public void ApplyLibraryModeThroughADefaultInterfaceMethod()
        {
            IDefaultViewConfigurer configurer = new DimOverrideDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>VIRTUAL CALL THROUGH A BASE-CLASS REFERENCE: the callee token names the base's
        /// virtual method (whose body is benign); only the override groups.</summary>
        public void ApplyLibraryModeThroughAVirtualBaseReference()
        {
            ViewConfigurerBase configurer = new OverrideDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>VIRTUAL CALL THROUGH A GENERIC BASE-CLASS REFERENCE: the callee token's parent
        /// and the derived type's BaseType are both TypeSpecs (GenericConfigurerBase&lt;
        /// ICollectionView&gt;), so both must fold onto the open generic base.</summary>
        public void ApplyLibraryModeThroughAGenericBaseReference()
        {
            GenericConfigurerBase<ICollectionView> configurer = new GenericOverrideDayGroupConfigurer();
            configurer.Configure(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>DELEGATE CREATED FROM AN INTERFACE METHOD GROUP: the IL is dup + ldvirtftn
        /// IDelegateViewConfigurer::Configure + newobj Action - no call instruction ever targets
        /// the implementation, and the later Invoke is an external call that connects to nothing.
        /// The ldvirtftn token is the only route in, and it needs the dispatch fan-out.</summary>
        public void ApplyLibraryModeThroughADelegate()
        {
            IDelegateViewConfigurer configurer = new DelegateDayGroupConfigurer();
            Action<ICollectionView> apply = configurer.Configure;
            apply(CollectionViewSource.GetDefaultView(_recent));
        }

        /// <summary>STATIC ABSTRACT INTERFACE MEMBER: the call is `constrained. !!T` +
        /// call IStaticViewConfigurer::Configure inside a generic method - the token names the
        /// interface declaration, and only the dispatch edge reaches the implementing static
        /// method.</summary>
        public void ApplyLibraryModeThroughAStaticAbstract()
        {
            _ = _recent.Count;              // what makes this a Library handler
            ConfigureStatically<StaticDayGroupConfigurer>(
                CollectionViewSource.GetDefaultView(_recent));
        }

        private static void ConfigureStatically<T>(ICollectionView view)
            where T : IStaticViewConfigurer => T.Configure(view);
    }

    /// <summary>The dispatch seam of the round-1 gate's attack. Nothing about this interface names
    /// the Library or grouping - it is an ordinary in-assembly abstraction.</summary>
    internal interface ILibraryViewConfigurer
    {
        void Configure(ICollectionView view);
    }

    /// <summary>The implementation the interface hides. Reached ONLY through
    /// <see cref="ILibraryViewConfigurer"/> dispatch - no body anywhere calls this method by its
    /// concrete type - so a walk blind to dispatch reports the Library clean while this groups it.</summary>
    internal sealed class DayGroupConfigurer : ILibraryViewConfigurer
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The dispatch seam of the inherited-implementation attack.</summary>
    internal interface IInheritedViewConfigurer
    {
        void Configure(ICollectionView view);
    }

    /// <summary>The base class that actually groups. It mentions no interface; nothing in ITS
    /// metadata connects it to <see cref="IInheritedViewConfigurer"/>.</summary>
    internal class DayGroupConfigurerBase
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The derived type that carries the InterfaceImpl row and NOTHING else - the
    /// implementation it contributes is the inherited one.</summary>
    internal sealed class InheritedDayGroupConfigurer : DayGroupConfigurerBase, IInheritedViewConfigurer
    {
    }

    /// <summary>The static-constructor attack: the only grouping is in the .cctor, which no call
    /// instruction anywhere targets - the runtime runs it when <see cref="Touch"/> first touches
    /// the type. Never executed by the tests (decoys are READ as IL, not run).</summary>
    internal static class CctorDayGroupConfigurer
    {
        private static readonly object[] Rows = new object[0];

        static CctorDayGroupConfigurer() =>
            CollectionViewSource.GetDefaultView(Rows).GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));

        public static void Touch() => _ = Rows.Length;
    }

    // ---- issue #2, round 3: the dispatch-shape decoys ------------------------

    /// <summary>The round-2 gate's IBase: the interface that DECLARES Configure. No class names it
    /// in an implements list - implementers name only the child interface.</summary>
    internal interface IBaseViewConfigurer
    {
        void Configure(ICollectionView view);
    }

    /// <summary>The round-2 gate's IChild: declares nothing itself, inherits the declaration.</summary>
    internal interface IChildViewConfigurer : IBaseViewConfigurer
    {
    }

    /// <summary>The implementation behind the inherited declaration. Its implements list names
    /// only <see cref="IChildViewConfigurer"/>; the call that reaches it is through
    /// <see cref="IBaseViewConfigurer"/>.</summary>
    internal sealed class InheritedDeclarationDayGroupConfigurer : IChildViewConfigurer
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The seam of the explicit-implementation shape, inherited-declaration variant.</summary>
    internal interface IExplicitBaseConfigurer
    {
        void Configure(ICollectionView view);
    }

    internal interface IExplicitChildConfigurer : IExplicitBaseConfigurer
    {
    }

    /// <summary>EXPLICIT implementation: the body is a private method whose compiled NAME is the
    /// dotted interface name, and the MethodImpl row (declaration IExplicitBaseConfigurer::
    /// Configure -> this body) is the only metadata connecting the two.</summary>
    internal sealed class ExplicitDayGroupConfigurer : IExplicitChildConfigurer
    {
        void IExplicitBaseConfigurer.Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The generic-interface seam. Instantiations of it are TypeSpecs, not TypeDefs.</summary>
    internal interface IGenericViewConfigurer<T>
    {
        void Configure(T view);
    }

    internal sealed class GenericDayGroupConfigurer : IGenericViewConfigurer<ICollectionView>
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The generic-METHOD seam: the call site's token is a MethodSpec.</summary>
    internal interface IGenericMethodConfigurer
    {
        void Configure<T>(T view) where T : ICollectionView;
    }

    internal sealed class GenericMethodDayGroupConfigurer : IGenericMethodConfigurer
    {
        public void Configure<T>(T view) where T : ICollectionView =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The default-interface-method seam: the interface method has a BODY, and it groups.
    /// A class can implement this interface without contributing any method at all.</summary>
    internal interface IDefaultViewConfigurer
    {
        void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>...and this class OVERRIDES the default body with a grouping body of its own, so
    /// the walk has to reach both ends: the default body directly, the override by dispatch.</summary>
    internal sealed class DimOverrideDayGroupConfigurer : IDefaultViewConfigurer
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The virtual seam: a base class whose virtual method is BENIGN.</summary>
    internal class ViewConfigurerBase
    {
        public virtual void Configure(ICollectionView view)
        {
        }
    }

    /// <summary>...and the override that groups. A call through a ViewConfigurerBase reference
    /// names the base's benign method; only the override edge reaches this.</summary>
    internal sealed class OverrideDayGroupConfigurer : ViewConfigurerBase
    {
        public override void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The generic-base seam: the derived type's BaseType handle is a TypeSpec.</summary>
    internal class GenericConfigurerBase<T>
    {
        public virtual void Configure(T view)
        {
        }
    }

    internal sealed class GenericOverrideDayGroupConfigurer : GenericConfigurerBase<ICollectionView>
    {
        public override void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The delegate seam: nothing ever CALLS Configure - a delegate is built over it
    /// (ldvirtftn) and invoked through Action, which is external code.</summary>
    internal interface IDelegateViewConfigurer
    {
        void Configure(ICollectionView view);
    }

    internal sealed class DelegateDayGroupConfigurer : IDelegateViewConfigurer
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The static-abstract seam (.NET 8 static interface members).</summary>
    internal interface IStaticViewConfigurer
    {
        static abstract void Configure(ICollectionView view);
    }

    internal sealed class StaticDayGroupConfigurer : IStaticViewConfigurer
    {
        public static void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("DayGroup"));
    }

    /// <summary>The NARROWNESS half of the same seam: an unrelated feature configuring ITS OWN view
    /// through an interface of its own. No Library handler ever calls through
    /// <see cref="IPanelConfigurer"/>, so conservative dispatch-following must NOT drag this in -
    /// reporting it would fail legitimate future work and cost the guard its life.</summary>
    internal interface IPanelConfigurer
    {
        void Configure(ICollectionView view);
    }

    /// <summary>The unrelated implementation the guard must stay silent about.</summary>
    internal sealed class PanelGroupConfigurer : IPanelConfigurer
    {
        public void Configure(ICollectionView view) =>
            view.GroupDescriptions.Add(new PropertyGroupDescription("Kind"));
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

        /// <summary>The unrelated feature actually CALLING through its own interface - the compiled
        /// call site whose dispatch edge leads to <see cref="PanelGroupConfigurer.Configure"/>. The
        /// guard must not report either end: no Library handler reaches this method.</summary>
        public static void ThroughAnInterfaceOfItsOwn(ICollectionView view)
        {
            IPanelConfigurer configurer = new PanelGroupConfigurer();
            configurer.Configure(view);
        }
    }

    // ---- issue #3: bypasses of the library's coherence model ------------------

    /// <summary>
    /// The type that OWNS the library's rows, named so the rows scan's
    /// "LibraryCoherence::" exclusion matches it here exactly as it does in the product.
    ///
    /// It is the NARROWNESS control: the scan must stay silent about the model touching its own
    /// field, or it would report a defect on every correct route and be deleted within a week.
    /// </summary>
    internal sealed class LibraryCoherence
    {
        internal readonly ObservableCollection<object> _rows = new();

        /// <summary>The model changing its own rows - never an offence.</summary>
        public void ApplySnapshot(object row) => _rows.Add(row);
    }

    /// <summary>
    /// The bypasses. Each one reaches the library's rows from OUTSIDE the model, in a spelling the
    /// previous guard could not see: it recognized only Insert/Remove/Clear/Add, so a direct
    /// <c>RemoveAt(0)</c> produced zero matcher hits and a move or an indexer assignment produced
    /// none either. The last one is hidden behind a wrapper, which is how a text scan is usually
    /// defeated.
    ///
    /// Nothing here is ever called. These methods exist to be READ, as IL, by CompiledCode.
    /// </summary>
    internal static class LibraryBypass
    {
        public static void RemoveAtDirectly(LibraryCoherence library) => library._rows.RemoveAt(0);

        public static void MoveDirectly(LibraryCoherence library) => library._rows.Move(0, 1);

        public static void AssignThroughTheIndexer(LibraryCoherence library, object row) =>
            library._rows[0] = row;

        public static void ThroughAWrapper(LibraryCoherence library, object row) =>
            Wrapper(library._rows, row);

        private static void Wrapper(ObservableCollection<object> rows, object row) => rows.Insert(0, row);
    }

    /// <summary>
    /// A card whose VALUE can be written - the decoy for the row-write scan. It is the same
    /// <see cref="RecentItem"/> the date scans already match on, so the two guards share one decoy
    /// type exactly as the product shares one real one.
    /// </summary>
    internal static partial class RecentItem
    {
        /// <summary>The value a rename writes. The scan matches its SETTER.</summary>
        public static string Title { get; set; } = "";

        /// <summary>The card writing its own value - never an offence.</summary>
        public static void AdoptFrom(string title) => Title = title;
    }

    /// <summary>
    /// Writes to a library row from OUTSIDE the card and the model - the shape QA found in
    /// RecordingDetailWindow.CommitRename, plus the version hidden behind a helper that never names
    /// a row itself, which is how a source scan is usually defeated.
    ///
    /// Nothing here is ever called. These methods exist to be READ, as IL, by CompiledCode.
    /// </summary>
    internal static class RowBypass
    {
        public static void RenameDirectly(string name) => RecentItem.Title = name;

        public static void ThroughAHelper(string name) => Helper(name);

        private static void Helper(string name) => RecentItem.Title = name;
    }
}
