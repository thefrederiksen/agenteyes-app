# Issue #2 - Developer handoff

Library verification follow-ups from the round-3 APPROVE-WITH-FOLLOWUPS on the archived
repository's PR #179 (issue #178). Branch: `issue-2-library-verification-followups`.

Machine gotcha (known, documented in `docs/cencon/proof/issue-1/handoff.md`): this machine has
no `Microsoft.WindowsDesktop.App` 8.x runtime, so run the tests with
`DOTNET_ROLL_FORWARD=LatestMajor` set. This affects main too and is not a defect of this branch.

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

Run on this branch after the attack was reverted, 2026-08-21:

```
Build succeeded.
    0 Error(s)

Passed!  - Failed:     0, Passed:   828, Skipped:     0, Total:   828, Duration: 14 s
```

(826 on main + the 2 new loader tests.)

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
