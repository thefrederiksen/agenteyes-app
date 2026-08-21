# Issue #2 - Developer handoff

Library verification follow-ups from the round-3 APPROVE-WITH-FOLLOWUPS on the archived
repository's PR #179 (issue #178). Branch: `issue-2-library-verification-followups`.

Machine gotcha (known, documented in `docs/cencon/proof/issue-1/handoff.md`): this machine has
no `Microsoft.WindowsDesktop.App` 8.x runtime, so run the tests with
`DOTNET_ROLL_FORWARD=LatestMajor` set. This affects main too and is not a defect of this branch.

## ROUND 3 (fix pass 2) - the round-2 gate's REJECT, answered systematically

The round-2 gate rejected PR #26 with one blocking defect: `DispatchEdges`
(tests/AgentEyes.Tests/CompiledCode.cs) read interface methods only from the interface a
class's InterfaceImpl row names. With `IChild : IBase`, `IBase` declaring
`Configure(ICollectionView)`, a class implementing `IChild`, and a handler calling through
`IBase.Configure`, the callee token is `IBase::Configure` and - the gate's finding - the map
would have no edge. The gate also required a SYSTEMATIC sweep: this was the third dispatch
shape found seriatim, so every remaining shape had to be either covered with a red-first
regression or listed verbatim in the documented limits.

### What changed in round 3

| File | Change |
|------|--------|
| `tests/AgentEyes.Tests/CompiledCode.cs` | (1) Each InterfaceImpl row is now expanded to the interface's FULL in-assembly inheritance closure (new `InterfaceClosure` helper): a class implementing any interface that transitively inherits the declaring interface implements its methods, so the edge comes from the interface the method is DECLARED on, however many inheritance levels sit between it and the row. (2) `jmp` targets are now collected - the one other IL instruction that transfers control to a method token (ECMA-335 III.3.37), found by the round-3 sweep; C# never emits it, which is exactly why it had gone unnoticed. |
| `tests/AgentEyes.Tests/LibraryDefectDecoys.cs` | One decoy handler + implementation pair per remaining dispatch shape (inventory below), including the gate's exact `IChild : IBase` construction (`ApplyLibraryModeThroughAnInheritedInterfaceDeclaration`). |
| `tests/AgentEyes.Tests/HandWrittenDispatchAssembly.cs` | NEW: a tiny assembly emitted by hand (MetadataBuilder/ManagedPEBuilder, both in the .NET 8 shared framework) whose `Impl` type lists ONLY `IChild` in its InterfaceImpl rows - metadata Roslyn will not produce (see the honesty note below) - plus a `jmp` body. Written to a temp file and READ, never executed, like every assembly CompiledCode scans. |
| `tests/AgentEyes.Tests/LibraryFlatListTests.cs` | Eleven new walk-level regressions (one per shape, `TheReachabilityWalk_*` / `TheDispatchMap_*`), each also asserting the unrelated `PanelGroupConfigurer` is NOT dragged in (narrowness per shape); the ten new grouping implementations added to `TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses`'s required-reported list; the documented limits on `LibraryGroupingIn` rewritten as the complete claims-vs-limits accounting below. |

No product code changed in round 3; the fix is entirely in the verification instrument.

### Honesty note - what is empirically true about the gate's C# construction

Roslyn FLATTENS a class's InterfaceImpl rows: `class Impl : IChild` is emitted with rows for
both `IChild` and `IBase` (verified empirically on this machine, .NET 8 SDK, via a
System.Reflection.Metadata probe). So on any Roslyn-compiled assembly - the product included -
the pre-fix map found the edge through the direct `IBase` row, and the gate's exact C#
construction was in fact already reported: the new decoy regression
`TheReachabilityWalk_FollowsAnInheritedInterfaceDeclaration` PASSED against the unchanged
round-2b walk (it is kept as the permanent pin of the gate's exact C# shape, with this note in
its doc comment).

The gate's underlying point stands regardless, and was real: the traversal was ABSENT, and
ECMA-335 does not require flattening - an ilasm-authored, non-Roslyn or rewritten assembly may
legally carry only the direct row. The fix therefore implements the full
interface-inheritance-graph traversal, and the regression that actually exercises it - and
that was RED before it - runs on hand-written metadata where no flattened row can rescue the
map. The instrument no longer depends on one compiler's habit.

### RED demonstrated, both layers (run on this branch, 2026-08-21)

Layer 1 - against the UNCHANGED round-2b walk (commit 7d5cdfb, tests committed first): the two
hand-emitted-probe regressions fail, everything else passes:

```
Failed AgentEyes.Tests.LibraryFlatListTests.TheDispatchMap_TraversesTheInterfaceInheritanceGraph_WithoutCompilerFlattening [38 ms]
   Assert.Contains() Failure: Item not found in collection
Collection: ["Probe.Handler::Run"]
Not found:  "Probe.Impl::Configure"
Failed AgentEyes.Tests.LibraryFlatListTests.TheReachabilityWalk_FollowsAJmpInstruction [81 ms]
   Assert.Contains() Failure: Item not found in collection
Collection: ["Probe.Handler::RunJmp"]
Not found:  "Probe.Impl::Configure"
Failed!  - Failed:     2, Passed:    55, Skipped:     0, Total:    57 (LibraryFlatListTests)
```

The gate's inherited-declaration edge really was missing from the map; only compiler
flattening had been hiding it.

Layer 2 - against the round-1 DISPATCH-BLIND walk (`git checkout 09f3cae --
tests/AgentEyes.Tests/CompiledCode.cs`, run, restored): all 15 dispatch-shaped regressions
fail - the 11 new ones, the 3 from rounds 2/2b, and the strengthened grouping-scan control -
proving each new regression is a real detector of a walk that cannot follow dispatch:

```
Failed!  - Failed:    15, Passed:     0, Skipped:     0, Total:    15
```

(filter: `FullyQualifiedName~TheReachabilityWalk|FullyQualifiedName~TheDispatchMap|FullyQualifiedName~TheGroupingScan_ReportsLibraryGrouping`)

### The dispatch-shape inventory - every shape in exactly one bucket

The IL instructions that can name a method token are `call`, `callvirt`, `newobj`, `jmp`,
`ldftn`, `ldvirtftn` and `ldtoken` - all collected - plus `calli`, which names no target and
is pinned to zero. Over those, the shapes:

COVERED - each with a walk-level regression (tests/AgentEyes.Tests/LibraryFlatListTests.cs):

| # | Shape | Regression |
|---|-------|------------|
| 1 | Direct call/callvirt into an in-assembly body (helpers included, transitively) | `TheTransitiveDateScan_ReportsAFallbackHiddenBehindAHelper` (round 1 of #2's parent) |
| 2 | Interface dispatch, direct implementation | `TheReachabilityWalk_FollowsInterfaceDispatch_ToTheInAssemblyImplementation` (round 2) |
| 3 | Inherited IMPLEMENTATION (InterfaceImpl row on derived type, body on a base class) | `TheReachabilityWalk_FollowsInterfaceDispatch_ToAnInheritedImplementation` (round 2b) |
| 4 | Inherited interface DECLARATION (IChild : IBase - the round-2 gate's shape) | `TheReachabilityWalk_FollowsAnInheritedInterfaceDeclaration` (C# pin) + `TheDispatchMap_TraversesTheInterfaceInheritanceGraph_WithoutCompilerFlattening` (red-first, non-flattened metadata; contains its own instrument check that the fixture is still non-flattened) |
| 5 | EXPLICIT interface implementation (MethodImpl row), of an inherited declaration at that | `TheReachabilityWalk_FollowsAnExplicitInterfaceImplementation` |
| 6 | Generic interface INSTANTIATION (TypeSpec at both ends, folded to the open type) | `TheReachabilityWalk_FollowsAGenericInterfaceInstantiation` |
| 7 | Constructed generic METHOD (MethodSpec resolved to the open declaration) | `TheReachabilityWalk_FollowsAConstructedGenericMethod` |
| 8 | DEFAULT interface method (the DIM body directly) AND a class override of it (by dispatch) | `TheReachabilityWalk_FollowsADefaultInterfaceMethod_AndItsOverride` |
| 9 | Virtual call through a BASE-CLASS reference (benign base body, grouping override) | `TheReachabilityWalk_FollowsAVirtualCallThroughABaseReference` |
| 10 | Virtual call through a GENERIC base-class reference (TypeSpec BaseType) | `TheReachabilityWalk_FollowsAVirtualCallThroughAGenericBaseReference` |
| 11 | DELEGATE built from an interface method group (ldvirtftn; the Invoke is external) | `TheReachabilityWalk_FollowsADelegateBuiltFromAnInterfaceMethod` |
| 12 | STATIC ABSTRACT interface member (constrained call in a generic method) | `TheReachabilityWalk_FollowsAStaticAbstractInterfaceMember` |
| 13 | Static constructors and finalizers (implicit runtime invocation, per touched type) | `TheReachabilityWalk_ReachesTheStaticConstructor_OfATouchedType` (round 2b) |
| 14 | `jmp` (the one other method-token control transfer; C# never emits it) | `TheReachabilityWalk_FollowsAJmpInstruction` (red-first, hand-emitted) |

Covered by MECHANISM, no dedicated regression (both only ADD edges - the fail-closed
direction): `ldtoken` method handles are collected by the same token collector the call
regressions exercise; method HIDING (`new`/`new virtual`) resolves to whichever declaration
the reference is typed as, which is a direct edge or the name-matched fan-out, over-reporting
at worst. Compiler-split bodies (lambdas, local functions, async/iterator state machines) fold
back onto their declaring method - pinned since round 1 by the existing folding tests.

LIMITS - stated verbatim in `LibraryGroupingIn`'s doc comment, none silently unhandled:

| Limit | Why it is beyond this static walk |
|-------|-----------------------------------|
| Assembly boundary, both forms | a callee's body in another assembly is not walked into; a dispatch seam DECLARED in another assembly (BCL/WPF interface or base class, e.g. `IObserver<T>.OnNext`) is not a key in this assembly's metadata tables |
| String-based reflection, `dynamic`, `FindName` | a method reached only via a name in a string is no call-graph edge; a route reaching the Library's list only through `FindName("RecentList")` touches no field, so it is not a seed either |
| Delegate invoked by code that did not build it | ldftn/ldvirtftn edges run from BUILDER to target; `Invoke` on the delegate type connects to nothing |
| Runtime-generated code (Reflection.Emit, expression compilation) | no IL in the assembly to walk; the product contains none, and `calli` is pinned to zero by `ManifestWriterIlTests.TheProductMakesNoIndirectCalls` |
| Markup-declared behaviour (XAML/BAML) | grouping declared in markup never appears as IL; that is the markup guard's territory, scoped to the RecentList element |

### Mutation drill on the REAL product (run on this branch, 2026-08-21, then reverted)

The gate's exact construction planted in `src/AgentEyes.App/MainWindow.xaml.cs`: a nested
`IRound3BaseConfigurer` declaring `Configure(ICollectionView)`, `IRound3ChildConfigurer :
IRound3BaseConfigurer` declaring nothing, `Round3DrillConfigurer : IRound3ChildConfigurer`
with the only grouping body, and `ApplyLibraryMode` calling through the BASE interface:

```csharp
IRound3BaseConfigurer round3 = new Round3DrillConfigurer();
round3.Configure(System.Windows.Data.CollectionViewSource.GetDefaultView(RecentList.ItemsSource));
```

Result - the guard FIRES, naming the implementation only the inherited-declaration edge reaches:

```
Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [66 ms]
   A method that handles the Library groups its collection view. The Library is one flat list (issue #178):
AgentEyesApp.dll!AgentEyes.App.MainWindow/Round3DrillConfigurer::Configure -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
AgentEyesApp.dll!AgentEyes.App.MainWindow/Round3DrillConfigurer::Configure -> System.Windows.Data.PropertyGroupDescription::.ctor x1
```

Attack reverted (`git checkout -- src/AgentEyes.App/MainWindow.xaml.cs`); full gate green
afterwards (below).

### Gate (round 3, branch tip, 2026-08-21)

```
Build succeeded.
    0 Error(s)

Passed!  - Failed:     0, Passed:   842, Skipped:     0, Total:   842, Duration: 14 s
```

(831 from round 2b + the 11 round-3 regressions. `DOTNET_ROLL_FORWARD=LatestMajor` as always
on this machine.)

### How QA drills round 3

In an ISOLATED WORKTREE (round-2b instructions below apply unchanged - never mutate the shared
checkout):

1. Re-run the product drill above: plant the three nested types + two lines in
   `ApplyLibraryMode` (src/AgentEyes.App/MainWindow.xaml.cs:1504), build, run the
   LibraryFlatListTests filter. Expected: `NoMethodThatHandlesTheLibrary_GroupsIt` FAILS naming
   `MainWindow/Round3DrillConfigurer::Configure`. An empty or aborted run is a broken
   instrument, never a pass. Revert.
2. Instrument-side RED both ways: `git checkout 09f3cae -- tests/AgentEyes.Tests/CompiledCode.cs`
   -> the 15-test filter above fails 15/15; `git checkout 8fefd79 -- tests/AgentEyes.Tests/CompiledCode.cs`
   (round-2b walk) -> exactly the two hand-emitted-probe regressions fail, 2/57 in
   LibraryFlatListTests. Restore with `git checkout HEAD -- tests/AgentEyes.Tests/CompiledCode.cs`.
3. Read the inventory against the instrument: every shape above is either a listed regression
   or a listed limit - the gate's specific demand. The probe fixture's own honesty is asserted
   inside `TheDispatchMap_...` (DirectInterfaceRowsOf must return exactly `IChild`).
4. Full gate: build + `dotnet test` (842/842 expected).

No product code changed in round 3, so no smoke area is newly touched; the smoke scoping
statement of round 1 stands.

### CenCon impact (round 3)

No drift: no component map change, no privacy-posture change; instrument-only.

## ROUND 2 (fix pass) - the review gate's REJECT, answered

The round-1 gate rejected PR #26 with one blocking defect: the grouping guard's reachability
walk (`CompiledCode.Reachable`, tests/AgentEyes.Tests/CompiledCode.cs) followed only calls into
method BODIES, so it never followed virtual/interface dispatch. Concrete attack: a Library
handler instantiates an in-assembly implementation through an interface and calls
`Configure(view)` through that interface; the IL call site names the abstract interface method
(no body, not a graph node), so the concrete implementation that adds a
`PropertyGroupDescription` stays unreached and every guard test stays green. And the documented
limits named only the assembly boundary, reflection and stored delegates - this ordinary
dispatch blind spot was not stated.

### What changed in round 2

| File | Change |
|------|--------|
| `tests/AgentEyes.Tests/CompiledCode.cs` | `Reachable` now follows virtual/interface dispatch conservatively, via a new `DispatchEdges` map built from the assembly's own metadata tables: (1) MethodImpl rows (explicit implementations/overrides - exact edges), (2) InterfaceImpl rows (implicit implementations of in-assembly interfaces, generic instantiations included, matched by name), (3) the in-assembly base-type chain (virtual overrides, by name). When a reached method calls a method DECLARED in this assembly, EVERY in-assembly implementation/override of it becomes reachable - fail closed, over-report direction. `Callee` also resolves member references whose parent is a generic instantiation of an in-assembly type onto the open type, so an `IConfigure<T>`-shaped seam is an edge too. |
| `tests/AgentEyes.Tests/LibraryDefectDecoys.cs` | The gate's exact construction, compiled as a permanent decoy: `LibraryWindow.ApplyLibraryModeThroughAnInterface` instantiates `DayGroupConfigurer` through `ILibraryViewConfigurer` and calls `Configure(view)` through the interface; only the implementation groups. Plus the narrowness half: `IPanelConfigurer`/`PanelGroupConfigurer` (same method name, same signature, unrelated interface) called only from `Grouping.ThroughAnInterfaceOfItsOwn`, which no Library handler reaches. |
| `tests/AgentEyes.Tests/LibraryFlatListTests.cs` | `TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses` now REQUIRES `DayGroupConfigurer::Configure` reported and FORBIDS `PanelGroupConfigurer::*`; new dedicated regression `TheReachabilityWalk_FollowsInterfaceDispatch_ToTheInAssemblyImplementation` (red under the old walk by construction); the documented limits on `NoMethodThatHandlesTheLibrary_GroupsIt` and `LibraryGroupingIn` restated to the walk's honest remaining limits - assembly boundary in BOTH forms (external callee bodies, and dispatch seams DECLARED outside the assembly such as `IObserver<T>.OnNext`), reflection, a delegate invoked by code that did not build it, runtime-generated code (with `calli` pinned by `IndirectCalls`). |

No product code changed in round 2; the fix is entirely in the verification instrument.

### RED demonstrated under the OLD walk (the defect, reproduced on this branch, 2026-08-21)

The decoy attack and the strengthened assertions were committed FIRST and run against the
UNCHANGED round-1 walk:

```
Failed AgentEyes.Tests.LibraryFlatListTests.TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses [79 ms]
   The grouping scan does not report the compiled grouping in 'AgentEyes.Tests.LibraryDefects.DayGroupConfigurer::Configure':
Failed!  - Failed:     1, Passed:    42, Skipped:     0, Total:    43
```

Exactly the gate's finding: the interface-dispatched grouping is invisible to the body-only
walk. With the new walk in place the same suite is green (Criterion 5 below: 829/829).

### Mutation drill on the REAL product (run on this branch, 2026-08-21, then reverted)

The gate's attack applied to `src/AgentEyes.App/MainWindow.xaml.cs` - `ApplyLibraryMode` given a
private interface + implementation pair, the handler calling `Configure` through the interface,
only the implementation grouping:

```csharp
// at the end of ApplyLibraryMode():
IMutationDrillConfigurer configurer = new MutationDrillConfigurer();
configurer.Configure(System.Windows.Data.CollectionViewSource.GetDefaultView(RecentList.ItemsSource));
// nested in MainWindow:
private interface IMutationDrillConfigurer { void Configure(System.ComponentModel.ICollectionView view); }
private sealed class MutationDrillConfigurer : IMutationDrillConfigurer
{
    public void Configure(System.ComponentModel.ICollectionView view) =>
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("DayGroup"));
}
```

Result - the guard FIRES, naming the implementation only dispatch can reach:

```
Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [60 ms]
   A method that handles the Library groups its collection view. The Library is one flat list (issue #178):
AgentEyesApp.dll!AgentEyes.App.MainWindow/MutationDrillConfigurer::Configure -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
AgentEyesApp.dll!AgentEyes.App.MainWindow/MutationDrillConfigurer::Configure -> System.Windows.Data.PropertyGroupDescription::.ctor x1
```

(`TheDayGroupMachineryIsGone` also failed on that run - the planted string "DayGroup" trips it;
incidental, and further proof the plant was really in the product.) Attack reverted with
`git checkout -- src/AgentEyes.App/MainWindow.xaml.cs`; full gate green afterwards.

### Round 2b - independent-review findings on the fix pass, fixed before handing to QA

An independent review of the round-2 diff (commit f89dd69) found two fail-open gaps in the new
dispatch walk, both demonstrated empirically. Both are FIXED in the follow-up commit on this
branch, each with its own decoy, RED demonstration and walk-level regression:

1. INHERITED IMPLEMENTATIONS (blocking-grade): the dispatch map matched an interface's methods
   only against the implementing type's OWN methods. `class Base { public void Configure(view)
   { groups } } class Derived : Base, IConfigurer {}` - the InterfaceImpl row is on Derived, the
   body on Base, no MethodImpl row exists for a same-assembly implicit inherited implementation,
   so `Base::Configure` was never reached. Fixed: `DispatchEdges` now collects bodies up the
   implementing type's in-assembly base chain (nearest declaration wins). Decoy:
   `IInheritedViewConfigurer` / `DayGroupConfigurerBase` / `InheritedDayGroupConfigurer`;
   regression: `TheReachabilityWalk_FollowsInterfaceDispatch_ToAnInheritedImplementation`.
   RED under the f89dd69 walk (run on this branch before the fix):
   `Failed AgentEyes.Tests.LibraryFlatListTests.TheReachabilityWalk_FollowsInterfaceDispatch_ToAnInheritedImplementation`
   plus the grouping-scan control failing to report `DayGroupConfigurerBase::Configure`.
2. IMPLICIT RUNTIME INVOCATIONS (unstated limit): a static constructor (or finalizer) is invoked
   by the runtime, never by a call instruction, so grouping hidden in a `.cctor` of a type a
   Library handler touches passed every guard silently. Fixed by adding implicit edges rather
   than stating a limit: touching any member of a type - a call, a construction, or a static
   field read/write - now reaches its `.cctor` and its `Finalize`. Per touched type, not a
   blanket sweep (the walk-level regression also asserts an untouched type stays out). Decoy:
   `CctorDayGroupConfigurer` (the only grouping is in its `.cctor`); regression:
   `TheReachabilityWalk_ReachesTheStaticConstructor_OfATouchedType`. RED under the f89dd69 walk:
   both that regression and the grouping-scan control (route `CctorDayGroupConfigurer::.cctor`).

Two further review observations, recorded and deliberately not taken:

* PERF: `Reachable` + `LibraryGroupingIn` re-open and re-parse the PE metadata several times per
  guard run. Real cost, no breakage - the full suite still runs in ~14s - and threading one
  MetadataReader through the scans is a refactor of a settled instrument that this fix pass has
  no spec for. Left as-is.
* The round-2 gate numbers below supersede round 2's: 831/831 (the 2 new walk-level regressions).

### How QA verifies round 2

IMPORTANT (review finding on the drills themselves): run every mutation drill in an ISOLATED
`git worktree` (`git worktree add ..\ae-drill issue-2-library-verification-followups`), not in
the shared checkout - a drill observed mid-flight in the shared tree makes concurrent test runs
report spurious failures that look exactly like real regressions, and a drill interrupted before
its revert step leaves the planted attack in the product source. Clean up with
`git worktree remove ..\ae-drill` when done.

1. Both directions of the drill:
   - RED: apply the mutation above to `MainWindow.xaml.cs` (end of `ApplyLibraryMode`, ~line
     1520), build, run `DOTNET_ROLL_FORWARD=LatestMajor dotnet test AgentEyes.sln -c Release
     --filter "FullyQualifiedName~LibraryFlatListTests"`. Expected:
     `NoMethodThatHandlesTheLibrary_GroupsIt` FAILS naming
     `MainWindow/MutationDrillConfigurer::Configure`. An empty or aborted run is a broken
     instrument, never a pass. Revert (`git checkout -- src/AgentEyes.App/MainWindow.xaml.cs`).
   - RED the other way (the instrument, not the product): revert ONLY the walk - restore the
     round-1 `Reachable`/`Callee` from commit 63e86cc
     (`git checkout 63e86cc -- tests/AgentEyes.Tests/CompiledCode.cs`), run the same filter.
     Expected: `TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses` and
     `TheReachabilityWalk_FollowsInterfaceDispatch_ToTheInAssemblyImplementation` FAIL naming
     `DayGroupConfigurer::Configure`. Restore with `git checkout HEAD -- tests/AgentEyes.Tests/CompiledCode.cs`.
2. Narrowness stays green: the same suite run reports `PanelGroupConfigurer` and `Grouping::*`
   in NO failure output, and the full suite passes 829/829 - the conservative fan-out follows
   the CALLED declaration only, it does not drag in same-named implementations of unrelated
   interfaces.
3. Read the restated limits (`LibraryGroupingIn` doc comment and
   `NoMethodThatHandlesTheLibrary_GroupsIt` doc comment, tests/AgentEyes.Tests/LibraryFlatListTests.cs)
   against `CompiledCode.Reachable`/`DispatchEdges` and confirm each stated limit is real and no
   unstated static-reach blind spot remains in the dispatch dimension.

## What changed

| File | Change |
|------|--------|
| `tests/AgentEyes.Tests/LibraryFlatListTests.cs` | `LibraryGroupingIn` now follows the call graph out of Library-handling methods (`CompiledCode.Reachable`); doc comments restate the guard's reach and its remaining limits; the negative control requires the delegated-helper route reported; two new tests pin the once-per-apply total. |
| `tests/AgentEyes.Tests/LibraryDefectDecoys.cs` | New decoys `LibraryWindow.ApplyLibraryModeThroughAHelper` + `LibraryWindow.ConfigureLibraryView` - the gate's exact delegation attack, compiled into the test assembly. |
| `src/AgentEyes.App/MainWindow.xaml.cs` | `LoadRecent` re-totals the Library only when the apply raised no collection event, so the total is computed exactly once per apply. |

## Criterion 1 - the grouping guard CATCHES grouping delegated to a helper (DEMONSTRATED)

Implemented: `LibraryGroupingIn` (tests/AgentEyes.Tests/LibraryFlatListTests.cs) no longer looks
only inside methods that touch `_recent`/`RecentList`. It seeds `CompiledCode.Reachable` with
every such handler and reports any grouping call made by anything in that transitive closure.
Remaining limits are stated in the helper's doc comment (assembly boundary; routes reached only
through reflection or a stored delegate; reached-from, not proved dataflow - that direction errs
toward a false alarm, never a silent pass).

Demonstration, run on this branch on 2026-08-21. The gate's exact attack was applied to the
REAL product - `ApplyLibraryMode` handed the Library's default view to a new private static
helper, and only the helper grouped it:

```csharp
// in ApplyLibraryMode():
ConfigureLibraryView(System.Windows.Data.CollectionViewSource.GetDefaultView(_library.Rows));
// the helper - never names a Library field:
private static void ConfigureLibraryView(System.ComponentModel.ICollectionView view) =>
    view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Title"));
```

Step 1 - attack in place, guard UNCHANGED (the defect, reproduced): full suite

```
Passed!  - Failed:     0, Passed:   826, Skipped:     0, Total:   826, Duration: 14 s
```

and the Library guard suite alone: `Passed! - Failed: 0, Passed: 41, Total: 41`. Real grouping
on the real Library, every guard green - exactly what the issue describes.

Step 2 - same attack, guard FIXED (the suite goes RED, naming the helper):

```
Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [38 ms]
  Error Message:
AgentEyesApp.dll!AgentEyes.App.MainWindow::ConfigureLibraryView -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
AgentEyesApp.dll!AgentEyes.App.MainWindow::ConfigureLibraryView -> System.Windows.Data.PropertyGroupDescription::.ctor x1
Failed!  - Failed:     1, Passed:    40, Skipped:     0, Total:    41
```

Step 3 - attack reverted: full gate green (see Criterion 5).

The attack also lives permanently in the suite: `LibraryDefectDecoys.cs` compiles the identical
shape (`ApplyLibraryModeThroughAHelper` -> `ConfigureLibraryView`), and
`TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses` requires the scan, pointed at
the test assembly, to report `LibraryDefects.LibraryWindow::ConfigureLibraryView`.

How QA verifies:
1. Re-run the demonstration itself: apply the two-line mutation above to
   `src/AgentEyes.App/MainWindow.xaml.cs` (`ApplyLibraryMode` is at line 1504), build, and run
   `DOTNET_ROLL_FORWARD=LatestMajor dotnet test AgentEyes.sln -c Release --filter "FullyQualifiedName~LibraryFlatListTests"`.
   Expected: `NoMethodThatHandlesTheLibrary_GroupsIt` FAILS naming
   `AgentEyes.App.MainWindow::ConfigureLibraryView`. Empty/aborted run = broken instrument, not a pass.
   Revert the mutation afterwards (`git checkout src/AgentEyes.App/MainWindow.xaml.cs`).
2. Confirm the permanent in-suite control: `TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses`
   passes and its route list includes `LibraryWindow::ConfigureLibraryView`
   (tests/AgentEyes.Tests/LibraryFlatListTests.cs, route list at line 724).

## Criterion 2 - the guard still ignores an unrelated grouped view

Implemented: no change needed to the narrowness control itself; the transitive closure starts
only from methods that touch the Library's own fields, so the `LibraryDefects.Grouping` decoys
(grouping through ListCollectionView, ICollectionView and ItemsControl with no Library
involvement) stay unreachable and unreported.
`TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses` keeps its
`Assert.DoesNotContain(... Grouping::...)` arm and passes - targeted run on this branch:

```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4
```

(the four targeted tests: the grouping guard, its control, and the two new loader tests).

How QA verifies: run the same filter
(`--filter "FullyQualifiedName~TheGroupingScan_ReportsLibraryGrouping"`) and read the test body:
the narrowness arm is the `Assert.DoesNotContain` over `AgentEyes.Tests.LibraryDefects.Grouping::`.

## Criterion 3 - compare-178.ps1 DST fix: NO TARGET ON THIS REPOSITORY (already satisfied by absence)

`docs/cencon/proof/issue-178/uia/compare-178.ps1` does not exist on this repository's main and
never did: the proof tree carried over from the archived repository contains only `issue-1` and
`issue-3`, and `git log --all --oneline -- "docs/cencon/proof/issue-178*"` returns nothing. The
instrument the criterion targets stayed behind in the archived repository, so the defect (a
strictly-descending check over rendered LOCAL labels) cannot reproduce here. Recreating the
script from the issue's description just to fix it would be fabricating an artifact this
repository never had, so per the carry-over instructions no fix was implemented.

The concern the item protects is already held on this repository's main, with file:line:

* The product comparator orders by the UTC INSTANT, never the local reading -
  `src/AgentEyes.App/MainWindow.xaml.cs:2509` (`int byStart = y.StartedUtc.Value.CompareTo(x.StartedUtc.Value);`),
  with the DST rationale in the comparator's doc comment (lines 2492-2495).
* The issue's exact fixture is a permanent regression test -
  `NewestFirst_DoesNotInvertAcrossTheAutumnDstTransition`
  (`tests/AgentEyes.Tests/LibraryFlatListTests.cs:153`): 2026-11-01 05:30Z vs 06:15Z in Eastern
  time, with an instrument check proving the fixture straddles the fall-back.
* The one rendered-order comparator that DID carry over,
  `docs/cencon/proof/issue-3/library-proof.ps1`, compares identity sets (`Sort-Object -Unique`
  on both sides) and parses no wall-clock labels - the defect shape is not present in it. A
  repo-wide sweep of `*.ps1` finds no strictly-descending label check anywhere.

How QA verifies: `ls docs/cencon/proof/` (no issue-178),
`git log --all --oneline -- "docs/cencon/proof/issue-178*"` (empty - and here the empty result IS
the fact being claimed, an absence of history, not a check passing on absence), read
`library-proof.ps1` for the comparison logic, and run
`--filter "FullyQualifiedName~NewestFirst_DoesNotInvertAcrossTheAutumnDstTransition"`.

## Criterion 4 - the Library total is computed once per apply

Implemented, the "make it actually once" arm: `MainWindow.LoadRecent`
(`src/AgentEyes.App/MainWindow.xaml.cs`, the apply block) now subscribes a one-shot
`CollectionChanged` probe around `ApplySnapshot`. An apply that changed the rows settles as ONE
coalesced collection event (RecentItemCollection's scope), and the constructor's
`CollectionChanged` handler re-totals on it - so `LoadRecent` skips its own pass. Only an apply
that raised NO event still calls `UpdateEmptyState()`, because a reload can adopt fresh values
into existing rows (a repaired recording's AI cost) and that changes the total with no
collection event. Either way: exactly one total per apply. The probe is detached in `finally`.

Pinned by two new tests (both in `tests/AgentEyes.Tests/LibraryFlatListTests.cs`):

* `TheLoader_RetotalsTheLibrary_OnlyWhenTheApplyRaisedNoEvent` - fails on any
  `UpdateEmptyState` call in `LoadRecent` outside the `if (!notified)` guard; the extraction
  throws (rather than passing) if `LoadRecent` stops calling it entirely.
* `TheOnceGuard_ReportsAnUnconditionalRetotal` - the negative control: the same scan, run on the
  loader's body with the guard stripped, must report the unconditional call.

How QA verifies: read the `LoadRecent` diff; run
`--filter "FullyQualifiedName~TheLoader_RetotalsTheLibrary|FullyQualifiedName~TheOnceGuard"`;
confirm the comment in `src/AgentEyes.App/RecentItemCollection.cs` (~line 43, "settled ONCE ...
the handler on CollectionChanged re-walks the collection to total the AI spend") now matches
reality instead of contradicting it.

## Criterion 5 - the gate

Round 2b (current tip), run on this branch, 2026-08-21:

```
Build succeeded.
    0 Error(s)

Passed!  - Failed:     0, Passed:   831, Skipped:     0, Total:   831, Duration: 14 s
```

(826 on main + the 2 loader tests from round 1 + the round-2 dispatch regression + the 2
round-2b regressions: inherited implementation, static constructor.)

For the record: round 2 gated at 829/829 (commit f89dd69), round 1 at 828/828.

## Smoke scoping for QA

The only product-code change is the `LoadRecent` totaling condition - the reload path of the
Library. `dotnet test` covers it structurally; if QA wants a running-app check, the targeted
instrument is `docs/cencon/proof/issue-3/library-proof.ps1` (launches the branch build, compares
rendered Library rows against disk; read its BEFORE/AFTER notes - the installed tray app must be
idle and stopped first, and restarted after). No audio recording, api-smoke or gui-smoke area is
touched; the heavy audible smokes are not warranted by this change.

Standing reminders: drive the app via the REST Control API (`http://127.0.0.1:7882`) / UIA /
PrintWindow - never force-foreground plus synthesized input without warning the human; the
recording HUD is capture-excluded (`WDA_EXCLUDEFROMCAPTURE`), so HUD/recording state is asserted
via UIA or `/status`, never a screen grab.

## Code-review findings considered (self-review pass, recorded for QA)

A review of this diff surfaced three non-blocking observations; none changed the code, and each
is recorded here so QA does not have to rediscover them:

1. OUT OF SCOPE, follow-up candidate for the Product Agent: the DELETE path carries the same
   double-retotal shape this issue removes from the loader - `Delete_Click`
   (`src/AgentEyes.App/MainWindow.xaml.cs:1812`) calls `UpdateEmptyState()` unconditionally after
   `_library.Delete(items)` has already raised the coalesced event that triggers the
   constructor's handler. Pre-existing, harmless at current sizes, and NOT one of this issue's
   three items (Scope: "IN: the three items above"), so it was left alone rather than fixed
   without a spec.
2. Design alternative, considered and not taken: `LibraryCoherence.ApplySnapshot` could RETURN
   whether it changed anything, replacing the loader's event probe. Strictly more capable (an
   unchanged reload could skip its one walk too), but it widens a settled public surface and its
   guard tests for a LOW follow-up; the probe is localized to the one call site the issue names,
   and the review verified it sound on the exception path (the scope's Dispose settles during
   unwind; the finally still unsubscribes).
3. Stated limit of the transitive guard: seeding reachability from every Library-handling method
   makes the closure broad, so a FUTURE legitimate grouping of a non-Library view reachable from
   one of them would false-alarm. That direction is noisy, never silent, and is stated in
   `LibraryGroupingIn`'s doc comment; the issue itself prescribed this instrument.

## CenCon impact

No drift: no component map change, no privacy-posture change. The changes narrow two
verification instruments and remove duplicate UI-thread work.

I believe this is finished.
