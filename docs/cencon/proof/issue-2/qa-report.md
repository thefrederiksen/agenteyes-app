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

---

# QA ROUND 2 (2026-08-21) - the gate's REJECT, independently re-verified

Round 1 above passed 5/5; the Review Gate then REJECTED PR #26 with one blocking defect: the
grouping guard's reachability walk (`CompiledCode.Reachable`) followed only calls into method
bodies, so an in-assembly implementation invoked through an interface was unreachable - a
handler could group the Library through `IConfigurer.Configure(view)` with every guard green -
and the documented limits omitted that dispatch blind spot. The fix pass is commits f89dd69 +
8fefd79 (tip verified: `8fefd79`, PR head OID matches). No product code changed in the fix pass
(`git diff --stat 2e48bfe..8fefd79 -- src/` is empty); the change is entirely in the
verification instrument and its decoys/regressions.

QA round 2 was performed fresh: PR tip checked out, built, tested, diff reviewed, and every
drill below run by QA itself in an ISOLATED detached worktree at 8fefd79 (per the handoff's
drill-isolation instruction), then the worktree removed. The shared checkout was never mutated.

## Gate check (run by QA on the PR tip)

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded. 0 Error(s)
DOTNET_ROLL_FORWARD=LatestMajor dotnet test AgentEyes.sln -c Release
Passed!  - Failed:     0, Passed:   831, Skipped:     0, Total:   831, Duration: 14 s
```

(Same known machine gotcha as round 1: no x64 `Microsoft.WindowsDesktop.App` 8.x on this
machine; without the roll-forward the run ABORTS - an aborted run is a broken instrument, never
a pass, so the roll-forward run above is the evidence.)

## Fix review - what the walk now does (file:line)

* `Reachable` (tests/AgentEyes.Tests/CompiledCode.cs:282-350) now follows dispatch: every
  callee that is an in-assembly interface/abstract/virtual DECLARATION fans out to every
  in-assembly implementation/override via `DispatchEdges` (CompiledCode.cs:389-460) - MethodImpl
  rows (explicit impls/overrides, exact), InterfaceImpl rows (implicit impls by name, generic
  instantiations folded onto the open type via `DefinedType`, CompiledCode.cs:487-505), and the
  in-assembly base chain (virtual overrides). Conservative by design: over-reports, fail closed.
* Round-2b gap 1 (inherited implementations): `CollectBodies` walks the implementing type's
  in-assembly base chain, nearest declaration first (CompiledCode.cs:424-437), so a derived type
  carrying the InterfaceImpl row with the body on a base that never names the interface is now
  an edge.
* Round-2b gap 2 (implicit runtime invocations): `Reach` adds `Type::.cctor` and
  `Type::Finalize` for every type whose member is touched (CompiledCode.cs:293-304), and static
  FIELD accesses trigger the owner's `.cctor` via a dedicated edge set built from
  `FieldAccesses` (CompiledCode.cs:306-318). Per touched type, not a blanket sweep - the
  regression asserts an untouched type stays out.

## Mutation drills (all run by QA, isolated worktree, each reverted afterwards)

1. **The gate's exact attack on the REAL product** (Expected: guard FIRES): appended to
   `ApplyLibraryMode` (src/AgentEyes.App/MainWindow.xaml.cs) a private
   `IMutationDrillConfigurer` + `MutationDrillConfigurer` pair, `Configure(view)` called only
   through the interface, only the implementation grouping. Result:

   ```
   Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [73 ms]
   AgentEyesApp.dll!AgentEyes.App.MainWindow/MutationDrillConfigurer::Configure -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
   AgentEyesApp.dll!AgentEyes.App.MainWindow/MutationDrillConfigurer::Configure -> System.Windows.Data.PropertyGroupDescription::.ctor x1
   Failed!  - Failed:     2, Passed:    44, Total:    46
   ```

   The guard names the implementation only dispatch can reach. (`TheDayGroupMachineryIsGone`
   also fired on the planted "DayGroup" string - incidental corroboration.) Reverted.

2. **Self-found gap 1 (inherited implementation) on the REAL product** (Expected: guard FIRES
   naming the BASE body): `IDrillInheritedConfigurer` + `DrillBaseConfigurer` (groups) +
   `DrillDerivedConfigurer : DrillBaseConfigurer, IDrillInheritedConfigurer` (empty), handler
   calls through the interface. Result:

   ```
   Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [134 ms]
   AgentEyesApp.dll!AgentEyes.App.MainWindow/DrillBaseConfigurer::Configure -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
   ```

   The base-class body, reachable only via the derived type's InterfaceImpl row, is named.
   Reverted.

3. **Instrument RED, round-1 walk** (Expected: the new regressions FAIL under the pre-fix
   walk): `git checkout 63e86cc -- tests/AgentEyes.Tests/CompiledCode.cs`, guard suite run:

   ```
   Failed ...TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses
   Failed ...TheReachabilityWalk_FollowsInterfaceDispatch_ToTheInAssemblyImplementation
   Failed ...TheReachabilityWalk_FollowsInterfaceDispatch_ToAnInheritedImplementation
   Failed ...TheReachabilityWalk_ReachesTheStaticConstructor_OfATouchedType
   Failed!  - Failed:     4, Passed:    42, Total:    46
   ```

4. **Instrument RED, round-2a walk (f89dd69, before the 2b fixes)** (Expected: exactly the two
   self-found-gap regressions FAIL, proving both gaps were real fail-opens in the first fix):

   ```
   Failed ...TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses
   Failed ...TheReachabilityWalk_FollowsInterfaceDispatch_ToAnInheritedImplementation
   Failed ...TheReachabilityWalk_ReachesTheStaticConstructor_OfATouchedType
   Failed!  - Failed:     3, Passed:    43, Total:    46
   ```

   (The plain interface-dispatch regression passes here, as it should - f89dd69 fixed that.)

5. **Restore tip walk, GREEN**: `git checkout 8fefd79 -- tests/AgentEyes.Tests/CompiledCode.cs`,
   full suite in the worktree: `Passed!  - Failed: 0, Passed: 831, Total: 831`. Worktree
   removed; shared checkout tree clean throughout.

## Narrowness and false-alarm check

The conservative fan-out did not start flagging legitimate code: the full 831 run is green
against the REAL `AgentEyesApp.dll` (the guards scan it on every run), and the strengthened
control `TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses`
(LibraryFlatListTests.cs:721-749) simultaneously REQUIRES six positive routes (including
`DayGroupConfigurer::Configure`, `DayGroupConfigurerBase::Configure`,
`CctorDayGroupConfigurer::.cctor`) and FORBIDS `PanelGroupConfigurer::*` (the same-name,
same-signature implementation of an unrelated interface) and `Grouping::*` - it cannot pass
vacuously. All three walk-level regressions also carry a `DoesNotContain` narrowness arm.

## Documented limits - honesty check

The restated limits on `LibraryGroupingIn` (LibraryFlatListTests.cs:1029-1060) and on
`Reachable`/`DispatchEdges` (CompiledCode.cs:239-256, 389-412) were checked against the
instrument, each one real and none overclaimed:

* Assembly boundary, BOTH forms: external callee bodies are not walked, and a dispatch seam
  DECLARED outside the assembly has no edge - `InAssemblyDeclaration` and `DefinedType` return
  null for TypeReference parents (CompiledCode.cs:465-505), exactly as stated.
* Reflection: no call token, no edge - stated.
* A delegate invoked by code that did not build it: `ldftn`/`ldvirtftn` ARE edges from the
  builder (CompiledCode.cs:861-863, 914), `Invoke` connects to nothing - stated precisely.
* Runtime-generated code: no IL to walk; `calli` (the one call shape naming no target) is
  counted and pinned at zero in the product by `IndirectCalls` (CompiledCode.cs:659) - stated.
* The reached-from (not dataflow) over-report direction is stated twice over, including the
  dispatch fan-out's every-implementation breadth.

No unstated static-reach blind spot was found in the dispatch dimension: explicit interface
implementations ride MethodImpl rows, abstract methods carry the Virtual flag so the base-chain
case covers them, default interface methods have bodies and are ordinary graph nodes, generic
instantiations fold onto the open type, and `.cctor`/`Finalize` ride the implicit edges.

## Round-1 items, lightly re-verified

* No product code changed since 2e48bfe, so criteria 1-5 of round 1 stand on the same code QA
  verified then. Spot-checked at tip: the once-per-apply probe in `LoadRecent`
  (src/AgentEyes.App/MainWindow.xaml.cs:1391-1409) is intact, and the criterion-3 no-target
  disposition (comparator DST concern held by `NewestFirstComparer` + the DST regression test)
  is untouched.

## Verdict (round 2)

**VERIFIED - the gate's defect is fixed and demonstrated, both self-found gaps closed and
demonstrated, limits honest, narrowness intact, 831/831 green.** Handing to the Review Gate
(`flow:ready-gate`) per D7 - QA does not merge.

---

# QA ROUND 3 (2026-08-21) - the round-2 gate's REJECT, independently re-verified: FAIL

Verified at PR #26 tip `02150db`, fresh QA context, nothing taken from the handoff on trust.
All drills below were run by QA in an isolated git worktree at the tip; every mutation was
reverted and the worktree deleted afterwards.

Verdict: **FAIL - two defects in the round-3 inventory's honesty, both reproducible below.**
The walk fix itself is sound (the gate's construction is caught, all claimed red-first evidence
reproduced), but the dispatch-shape inventory - the round-2 gate's specific demand that every
shape be either regression-covered or a verbatim documented limit - overclaims in two places.
Recommended next state: `flow:qa-failed`.

## Gate check (run by QA on the tip)

```
dotnet build AgentEyes.sln -c Release   ->  2 Warning(s), 0 Error(s)
dotnet test  AgentEyes.sln -c Release   (DOTNET_ROLL_FORWARD=LatestMajor, known machine gotcha)
Passed!  -  Failed:     0, Passed:   844, Skipped:     0, Total:   844, Duration: 14 s
```

PASS. (Note: the handoff's "How QA drills round 3" step 4 says "842/842 expected" - a typo for
844; the handoff's own gate section says 844 and 844 is what runs.)

## Drill 1 - the round-2 gate's construction, planted in the REAL product

Expected: planting IRound3BaseConfigurer / IRound3ChildConfigurer : IRound3BaseConfigurer /
Round3DrillConfigurer : IRound3ChildConfigurer in MainWindow with ApplyLibraryMode calling
Configure through the BASE interface makes NoMethodThatHandlesTheLibrary_GroupsIt FAIL,
naming the implementation. Actual:

```
Failed AgentEyes.Tests.LibraryFlatListTests.NoMethodThatHandlesTheLibrary_GroupsIt [57 ms]
AgentEyesApp.dll!AgentEyes.App.MainWindow/Round3DrillConfigurer::Configure -> System.ComponentModel.ICollectionView::get_GroupDescriptions x1
AgentEyesApp.dll!AgentEyes.App.MainWindow/Round3DrillConfigurer::Configure -> System.Windows.Data.PropertyGroupDescription::.ctor x1
Failed!  - Failed:     2, Passed:    57, Skipped:     0, Total:    59
```

The guard FIRES on the inherited-declaration route. (The second failure is
TheDayGroupMachineryIsGone, the source-side belt catching the planted PropertyGroupDescription
text - consistent, not spurious.) Reverted; 59/59 green afterwards. PASS.

## Drill 2 - the R2 gap is pinned at WALK level (instrument-side RED, both layers)

Expected per handoff: round-2b walk (git checkout 8fefd79 -- tests/AgentEyes.Tests/CompiledCode.cs)
fails exactly 5 of 59 LibraryFlatListTests; round-1 walk (09f3cae) fails the 17-test dispatch
filter 17/17. Actual, both reproduced exactly:

```
8fefd79 walk:  Failed!  - Failed: 5, Passed: 54, Total: 59
  TheDispatchMap_TraversesTheInterfaceInheritanceGraph_WithoutCompilerFlattening   <- the R2 gap, non-flattened metadata
  TheReachabilityWalk_FollowsAJmpInstruction
  TheReachabilityWalk_ChasesDispatchTransitively_ToADerivedOverride
  TheReachabilityWalk_ChasesDispatchTransitively_ThroughABodilessRelay
  TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses
09f3cae walk:  Failed!  - Failed: 17, Passed: 0, Total: 17
```

The R2 construction is pinned red-first at walk level by the hand-emitted-metadata regression
(the C# pin TheReachabilityWalk_FollowsAnInheritedInterfaceDeclaration stays green under the
round-2b walk exactly as the honesty note says - Roslyn flattening - which QA confirms is stated
truthfully, tests/AgentEyes.Tests/LibraryFlatListTests.cs:851-857). Walks restored to tip. PASS.

## Drill 3 (QA's chosen covered row) - inventory row 13, and DEFECT 1

Chosen row: 13, "Static constructors and finalizers (implicit runtime invocation, per touched
type)", claimed covered by TheReachabilityWalk_ReachesTheStaticConstructor_OfATouchedType
(tests/AgentEyes.Tests/LibraryFlatListTests.cs:817).

Ablation: deleted ONLY the implicit-member block in Reach()
(tests/AgentEyes.Tests/CompiledCode.cs:302-307 - the "{type}::.cctor" / "{type}::Finalize"
enqueue on touching a method). Expected if the row is pinned: at least that regression goes red.
Actual:

```
Passed!  -  Failed:     0, Passed:   844, Skipped:     0, Total:   844
```

The ENTIRE SUITE stays green. The decoy's Touch() reads the static field Rows
(tests/AgentEyes.Tests/LibraryDefectDecoys.cs:346), so the .cctor is still reached through the
separate static-field route (CompiledCode.cs:350-352) and the regression pins only that one
route. What the ablated walk can no longer see - both demonstrated with QA probes compiled into
the worktree test assembly, red under the ablation, green under the intact tip walk:

* a grouping .cctor of a type touched ONLY by a method call ("static void Poke() { }",
  no field access): Not found: ...CallOnlyTouchCctor::.cctor
* a grouping FINALIZER ("~FinalizerGrouper() => ...GroupDescriptions.Add(...)", handler
  constructs the type): Not found: ...FinalizerGrouper::Finalize

No committed test anywhere pins Finalize (grep over the guard tests and decoys: zero hits),
and the field-access route cannot reach it - fieldOwners adds only "::.cctor"
(CompiledCode.cs:350-352). So half of row 13's covered claim is unproven: the row names
finalizers and the mechanism comment (CompiledCode.cs:293-296) claims all three touch forms,
but the committed regression can only ever fire on the loss of the field-touch/.cctor form.
Per method 6c this is a check that fails open on the unpinned half - a future edit deleting
those six lines ships with 844/844 green and a live .cctor/finalizer blind spot.

## Inventory audit - completeness and honesty (scope item 3), and DEFECT 2

Presence check, all 16 rows: every covered shape names a regression that EXISTS at the tip -
rows 1-16 map to LibraryFlatListTests.cs lines 554, 783, 801, 860+872, 924, 931, 938, 945, 956,
963, 971, 979, 817, 902, 993, 1001 respectively. The verbatim limits block exists on
LibraryGroupingIn (LibraryFlatListTests.cs:1255-1275) and matches the handoff's LIMITS table
line for line. Red-first evidence for the rows reproduced collectively in Drill 2 (17/17 red
under the dispatch-blind walk covers rows 2-16; row 1's scan has its own negative control,
TheHelperTheTransitiveScanCatches_IsInvisibleToADirectScan, line 576). The narrowness controls
hold: every AssertShapeReached also asserts PanelGroupConfigurer::Configure NOT reached
(line 844), the grouping scan forbids Grouping::* and PanelGroupConfigurer::* (lines 766-769),
and the real product passes the guard 844/844 - no false positive on legitimate code.

DEFECT 2 - one call-site shape is absent from BOTH buckets: a VARARG call site. The inventory's
completeness claim (LibraryFlatListTests.cs:1251-1253, "the IL instructions that can name a
method token are call, callvirt, newobj, jmp, ldftn, ldvirtftn and ldtoken - all collected -
and calli, which names none") holds at the COLLECTION layer but not at the RESOLUTION layer:
a vararg call site names its in-assembly target through a MemberReference whose parent is a
METHODDEF (ECMA-335 II.22.25), and Callee resolves that parent to null
(tests/AgentEyes.Tests/CompiledCode.cs:869, "_ => null,   // external TypeSpec, ModuleRef,
MethodDef (vararg)"), so the edge is silently dropped. QA proof, compiled with this repo's own
toolchain in the worktree test assembly against the INTACT tip walk:

```csharp
public static void Handler() => Grouper(__arglist(1));
public static void Grouper(__arglist) =>
    CollectionViewSource.GetDefaultView(new object[0]).GroupDescriptions
        .Add(new PropertyGroupDescription("DayGroup"));
```
```
Failed  TheWalk_FollowsAVarargCallSite_ToTheInAssemblyBody
Not found:  "AgentEyes.Tests.QaProbe.VarargProbe::Grouper"
```

The grouping body one ordinary call instruction away from the handler is unreachable to the
walk, the suite stays green, and varargs appear nowhere in the verbatim limits - only in a code
comment inside Callee. That is the round-2 gate's REJECT pattern exactly: an undocumented
construction, "not among the documented limits". C# compiles __arglist today, so this is a
real, buildable bypass, not a hypothetical ilasm one - and the hand-emitted-probe precedent in
this very round shows the bar the fix pass itself set.

## Round-1 / round-2 items

git diff --stat 09c78a8..02150db touches only tests/ and docs/cencon/ - no product code, so
every round-1 criterion (helper-delegation guard, DST comparator no-target disposition,
once-per-apply total) and the round-2 product drills stand as verified. PASS.

## Verdict (round 3)

FAIL - two defects, both in the verification instrument's coverage honesty, neither in product
code:

1. Inventory row 13's covered claim is half unproven: no committed test pins the finalizer
   edge or the method-call/construction touch routes; deleting CompiledCode.cs:302-307 leaves
   844/844 green while both claimed forms go blind. Fix direction: extend the decoy set with a
   call-only-touch .cctor pair and a finalizer pair (both QA probe shapes above), red-first
   against the ablation, or narrow the row and the Reach() comment to what is actually pinned.
2. Vararg call sites (MemberRef with MethodDef parent) are silently unresolved
   (CompiledCode.cs:869) - an in-assembly edge shape in NEITHER bucket. Fix direction: resolve
   the MethodDef-parent MemberReference onto its MethodDef (the parent IS the target), with a
   red-first regression, or pin vararg call sites to zero the way calli is pinned and add the
   limit verbatim to the documented limits.

Everything else verified: build clean, 844/844, the round-2 gate's construction caught in the
real product (Drill 1), the R2 gap pinned red-first at walk level on non-flattened metadata
(Drill 2), honesty note about Roslyn flattening truthful, narrowness intact, no product drift.
