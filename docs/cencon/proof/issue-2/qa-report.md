# Issue #2 - QA verification report

Issue: #2 "[App] Library verification follow-ups: grouping guard misses helper delegation,
comparator fails a correct DST order, duplicate total"
PR: #26, branch `issue-2-library-verification-followups`, verified at commit `63e86cc`.
Date: 2026-08-21. Verified independently: every check below was run by QA on this machine;
nothing was taken from the developer's handoff on trust.

Verdict: **VERIFIED - all acceptance criteria met.** Recommended next state: `flow:ready-gate`.

---

## The gate (criterion 5) - run by QA

```
dotnet build AgentEyes.sln -c Release
    2 Warning(s)
    0 Error(s)

dotnet test AgentEyes.sln -c Release   (DOTNET_ROLL_FORWARD=LatestMajor, known machine gotcha)
Passed!  - Failed:     0, Passed:   828, Skipped:     0, Total:   828, Duration: 14 s
```

PASS. (828 = 826 on main + the 2 new loader-guard tests.)

---

## Criterion 1 - the grouping guard CATCHES grouping delegated to a helper

Expected: reproduce the gate's exact attack (ApplyLibraryMode calls a helper taking the view;
only the helper calls GroupDescriptions.Add) and the suite goes RED - demonstrated, not asserted.

What the code does: `LibraryGroupingIn` (tests/AgentEyes.Tests/LibraryFlatListTests.cs:964-982)
seeds `CompiledCode.Reachable` with every method that touches `_recent`/`RecentList`
(via `CompiledCode.FieldAccesses`) and reports any grouping call made by anything in that
transitive closure. `Reachable` (tests/AgentEyes.Tests/CompiledCode.cs:242) walks the in-assembly
call graph from IL and fails closed twice: zero handlers throws (LibraryFlatListTests.cs:970-973),
and a seed that is not a method definition throws inside Reachable itself (CompiledCode.cs:264-267).
Limits (assembly boundary, reflection/delegate routes, reached-from not dataflow) are stated in
the doc comment - an honestly documented limit, per method 6c.6.

QA mutation drill (run by QA, both directions):

1. Planted the gate's exact attack in the REAL product - `ApplyLibraryMode`
   (src/AgentEyes.App/MainWindow.xaml.cs) handed
   `CollectionViewSource.GetDefaultView(_library.Rows)` to a new private static
   `ConfigureLibraryView(ICollectionView view)` whose body did
   `view.GroupDescriptions.Add(new PropertyGroupDescription("Title"))`. Built clean (0 errors),
   ran `--filter "FullyQualifiedName~LibraryFlatListTests"`:

   ```
   Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [59 ms]
     Error Message:
   AgentEyesApp.dll!AgentEyes.App.MainWindow::ConfigureLibraryView -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
   AgentEyesApp.dll!AgentEyes.App.MainWindow::ConfigureLibraryView -> System.Windows.Data.PropertyGroupDescription::.ctor x1
   Failed!  - Failed:     1, Passed:    42, Skipped:     0, Total:    43
   ```

   The guard FIRES and NAMES the delegated helper. This is the exact scenario that stayed green
   in the archived repo's round 3.

2. Reverted the attack (`git checkout src/AgentEyes.App/MainWindow.xaml.cs`), rebuilt, re-ran:
   `Passed!  - Failed: 0, Passed: 43, Total: 43`.

The attack is also compiled permanently into the suite: `LibraryDefectDecoys.cs:127-135`
(`ApplyLibraryModeThroughAHelper` -> `ConfigureLibraryView`), and
`TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses`
(LibraryFlatListTests.cs:717-736) requires the scan to report
`LibraryDefects.LibraryWindow::ConfigureLibraryView` (route list at line 724).

PASS.

## Criterion 2 - the guard still does NOT fire on a legitimate unrelated grouped view

Expected: the round-2 over-broadness must not come back.

What the code does: the closure is seeded ONLY from methods touching the Library's own fields
(`IsALibraryField`, LibraryFlatListTests.cs:943-945), so the `LibraryDefects.Grouping` decoys
(tests/AgentEyes.Tests/LibraryDefectDecoys.cs:145-156: grouping through ListCollectionView,
ICollectionView, and ItemsControl with no Library involvement) are unreachable and unreported.
The narrowness arm is the `Assert.DoesNotContain(... "AgentEyes.Tests.LibraryDefects.Grouping::" ...)`
at LibraryFlatListTests.cs:734-735, in the SAME test that requires the three positive routes -
it cannot pass vacuously.

QA targeted run:

```
--filter "FullyQualifiedName~TheGroupingScan_ReportsLibraryGrouping"
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1
```

Also green inside the full 828 run and inside both mutation-drill runs above (42 and 43 passed
respectively - the narrowness control never fired). PASS.

## Criterion 3 - compare-178.ps1 DST fix: verified NO TARGET in this repository

Expected: `compare-178.ps1` reports MATCH for the DST pair and MISMATCH for a swap - OR the
developer's claim that the script was never carried into this repository holds up under an
independent search.

QA presence check (what was searched, what was found):

1. `git log --all --oneline -- "docs/cencon/proof/issue-178*" "*compare-178*" "**/compare-178.ps1"`
   -> EMPTY. Here the empty result IS the fact claimed (an absence of history), and it is
   cross-checked by the positive inventory in item 2 - the instrument is not broken.
2. Full-history file inventory: enumerated every path in every commit reachable from every ref
   (`git rev-list --all` x `git ls-tree -r --name-only`) -> 309 unique paths ever. Grep for
   `compare-178` and `issue-178` (case-insensitive) -> ZERO hits. The proof tree carried over
   contains only `issue-1` and `issue-3`.
3. The comparator LOGIC under another name: listed all 17 `.ps1` files ever in history; swept
   the tree's `.ps1` files for descending/label-parsing shapes (`desc`, `ParseExact`,
   `[datetime]`, AM/PM). The only rendered-row comparator is
   `docs/cencon/proof/issue-3/library-proof.ps1`, which compares identity SETS
   (`Sort-Object` / `Sort-Object -Unique` on folder names, lines 74 and 111) and parses no
   wall-clock labels. The defect shape (a strictly-descending check over rendered LOCAL labels)
   exists nowhere in this repository, under any name.
4. The concern the item protects is held on main: the product comparator orders by the UTC
   INSTANT - `int byStart = y.StartedUtc.Value.CompareTo(x.StartedUtc.Value);`
   (src/AgentEyes.App/MainWindow.xaml.cs, NewestFirstComparer) with the DST rationale in its doc
   comment - and the issue's exact fixture (2026-11-01 05:30Z vs 06:15Z, Eastern) is a permanent
   regression test with an instrument check proving it straddles the fall-back
   (tests/AgentEyes.Tests/LibraryFlatListTests.cs:153,
   `NewestFirst_DoesNotInvertAcrossTheAutumnDstTransition`). QA targeted run:
   `Passed!  - Failed: 0, Passed: 1, Total: 1`.

The criterion's target artifact stayed behind in the archived repository; recreating a defective
script here in order to fix it would fabricate history. Disposition (documented no-target, with
the protected concern shown held) is correct. VERIFIED.

## Criterion 4 - the Library total is computed once per apply

Expected: once per apply, or the comment states what actually happens. The developer took the
"make it actually once" arm.

What the code does (src/AgentEyes.App/MainWindow.xaml.cs:1391-1409): `LoadRecent` subscribes a
one-shot `CollectionChanged` probe on `_library.Rows` around `ApplySnapshot`, detaches it in
`finally`, and calls `UpdateEmptyState()` only `if (!notified)`.

QA traced the original double-count path and why the new code cannot hit it:

* On main, `LoadRecent` called `UpdateEmptyState()` unconditionally after `ApplySnapshot`; the
  constructor handler (`MainWindow.xaml.cs:74`,
  `_library.Rows.CollectionChanged += (_, _) => UpdateEmptyState();`) ALSO re-totals on the
  apply's coalesced event -> two full walks (`UpdateLibraryTotal` iterates every row) per
  changing reload. That is the issue's item 3.
* The settle is SYNCHRONOUS inside the apply: `ApplySnapshot` mutates only inside
  `using (_rows.BeginCoherentUpdate())` (src/AgentEyes.App/LibraryCoherence.cs:172), and the
  scope's `Dispose` -> `Settle` -> `base.OnCollectionChanged` runs on the UI thread before
  `ApplySnapshot` returns (src/AgentEyes.App/RecentItemCollection.cs:143-181), so the probe
  observes the event reliably - `notified` cannot miss an apply that changed rows.
* Changing apply: one coalesced event -> handler re-totals once; the explicit call is skipped.
  Non-changing apply that adopted values into existing rows (AdoptFrom raises no collection
  event): zero events -> the explicit call runs once. Either way exactly one total per apply.
* `ReconcileFactsWithRows` (LibraryCoherence.cs:527) mutates only `_facts`, never `_rows` - no
  extra event before the apply's own. Exception path: the `using` settles during unwind (event
  observed if anything changed), the `finally` detaches the probe, the guarded call still runs
  if nothing changed. No path totals twice, no path totals zero times after a value change.
* The `RecentItemCollection` scope comment ("settled ONCE ... the handler on CollectionChanged
  re-walks the collection to total the AI spend", RecentItemCollection.cs:44-48) now matches
  reality instead of contradicting it.

QA drilled the fail-closed guard itself, both directions (source-scan tests; no rebuild needed):

1. Reintroduced the defect - replaced `if (!notified) UpdateEmptyState();` with an unconditional
   `UpdateEmptyState();`:

   ```
   Failed AgentEyes.Tests.LibraryFlatListTests.TheLoader_RetotalsTheLibrary_OnlyWhenTheApplyRaisedNoEvent [3 ms]
    LoadRecent re-totals the Library unconditionally, ... (issue #2):
   UpdateEmptyState at offset 2554: UpdateEmptyState();
   ```

   The guard FIRES on the exact original defect - it does not silently correct.

2. Removed the call ENTIRELY (the absence a fail-open check would certify):

   ```
   Failed ... TheLoader_RetotalsTheLibrary_OnlyWhenTheApplyRaisedNoEvent [30 ms]
    System.InvalidOperationException : LoadRecent no longer calls UpdateEmptyState, so this
    guard would pass by finding nothing - the loader has to settle the empty state and the
    total somewhere.
   ```

   It THROWS on absence rather than passing (`UnguardedRetotalsIn`,
   LibraryFlatListTests.cs:1004-1020). Restored the source afterwards
   (`git checkout src/AgentEyes.App/MainWindow.xaml.cs`; tree clean, re-verified).

3. The in-suite negative control `TheOnceGuard_ReportsAnUnconditionalRetotal`
   (LibraryFlatListTests.cs:800-808) pins direction 1 permanently.

PASS.

---

## Runtime-check judgment and smoke scoping

The only product-code change is the `LoadRecent` totaling condition. Behaviorally it is main's
handler path minus the duplicate walk: when the apply raises the event, the SAME pre-existing
handler (MainWindow.xaml.cs:74) performs the SAME `UpdateEmptyState` main performed; when it
raises none, the explicit call is unchanged. Synchronicity of the settle was verified from the
code above, and both directions of the guard were mutation-drilled. A running-app pass
(`issue-3/library-proof.ps1`) would require stopping the user's installed tray app for a check
already pinned structurally; not warranted. No audio, API, or GUI surface is touched - the heavy
audible smokes (`api-smoke.ps1` / `gui-smoke.ps1` / `run-all.ps1`) are not warranted either.

## Method and standards review

* Handoff note present and linked repo-relative on the issue (6a). ASCII-only diff. No new
  public product methods (logging standard unaffected); the probe/guard is a designed condition,
  not a fallback; no privacy-posture or component-map drift.
* Known machine gotcha honored (no `Microsoft.WindowsDesktop.App` 8.x on this machine;
  `DOTNET_ROLL_FORWARD=LatestMajor` for test runs) - affects main equally, not a branch defect.
* Noted, not blocking (developer disclosed it, and it is outside the issue's scope as written):
  `Delete_Click` (MainWindow.xaml.cs:1812) still carries the same double-retotal shape on the
  DELETE path - a follow-up candidate for the Product Agent.

## Verdict

**VERIFIED - all acceptance criteria met.** 5/5 PASS. Handing to the Review Gate
(`flow:ready-gate`) per D7 - QA does not merge.
