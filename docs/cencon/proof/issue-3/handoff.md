# Developer handoff - issue #3

**[App] Library asynchronous refresh has no coherence model - reloads, inserts, renames and deletes race**

Repo: `thefrederiksen/agenteyes-app`. Branch: `issue-3-library-coherence`. Base: `8d46403` (v1.4.8).

**ROUND 2** - QA failed round 1 on criterion 6 and raised two guard-strength findings. All three are
addressed; section 0 is the round-2 record and is the part to read first. I believe this is finished.
Build clean, `dotnet test` 812/812, every round-2 guard demonstrated failing before it was trusted,
and the running app re-verified against the owner's real 44-recording library.

---

## 0. Round 2 - what QA found and what changed

QA's report: `docs/cencon/proof/issue-3/qa-report.html`. It was right on every point.

### BLOCKING 1 - criterion 6 failed on the exact scenario it names

QA constructed the reload that starts AFTER the delete while the folder is still being removed, and
the row came back. Worse, the two tests round 1 shipped for criterion 6 covered the OPPOSITE ordering
and then asserted the criterion-6 case as INTENDED behaviour - the defect was written into the suite,
which is why 804 green tests certified it. That is the failure this fix is judged on.

**The design point.** A newer epoch is not better evidence about a directory whose deletion is still
running. For the whole span between "the rows go" and "the folder is gone" the manifest is still on
disk, so a reload begun after the delete honestly reports the recording AND carries the higher epoch.
No epoch can express "the disk has not caught up yet" - only the deletion's own outcome can.

A deletion is therefore bounded by its OUTCOME:

* `Delete` marks each directory `Removing` and returns a `LibraryDeletion` handle. `Removing` beats
  every snapshot at ANY epoch, and is never pruned.
* `MainWindow.DeleteRecordings` runs the recursive delete on its worker and then reports back through
  `CompleteDelete(deletion, failed)` - unconditionally, in the continuation, whether or not the
  delete succeeded.
* `CompleteDelete` moves each directory to `Removed` at the COMPLETION epoch, and ordinary epoch
  ordering resumes from there. That gives both halves for free: a deletion that SUCCEEDED is not
  listed by any later snapshot so the row stays gone, and one that FAILED is listed, so the row comes
  back. A snapshot that began BEFORE completion still loses, because its epoch is lower.

Both of QA's causes are fixed by that one mechanism. `PruneTombstones` never touches a `Removing`
fact, so the horizon can no longer drop a tombstone in the same call that created it, and a snapshot
landing correctly can no longer be the event that expires the deletion (QA's N3).

**The cost, stated rather than discovered later:** a deletion the caller never settles hides its
recordings for the rest of the session. `ADeletionThatIsNeverSettled_KeepsHidingTheRecording` pins
that, the settle is unconditional in the window, and the route enumeration now requires
`DeleteRecordings` to reach BOTH `Delete` and `CompleteDelete`.

**The tests.** The two round-1 criterion-6 tests are replaced. Written FIRST and confirmed RED on the
unmodified round-1 code before any product change:

```
AReloadStartingAfterTheDelete_WhileTheDirectoryIsStillBeingRemoved_DoesNotResurrectTheRow  [FAIL]
ADeleteWithOneReloadAlreadyInFlight_SurvivesBothThatReloadAndALaterOne                     [FAIL]
AnUnrelatedSnapshotSettling_DoesNotExpireADeletionThatIsStillRunning                       [FAIL]
   Expected: ["one_video"]   Actual: ["doomed_video", "one_video"]
```

The inverted test that asserted the resurrection is gone. The property it was protecting - a FAILED
deletion must reappear - is kept by `AReloadAfterADeletionThatFAILED_ShowsTheRecordingAgain`, which
settles the deletion as a failure first, plus
`AReloadInFlightWhenTheDeletionSettled_DoesNotResurrectTheDeletedRow` for the succeeded arm.

### BLOCKING 2 - the gate was castable-around, and the bypass crashed

* **The cast is closed.** The gated collection is now a PRIVATE NESTED type of `LibraryCoherence`
  (`internal sealed partial class LibraryCoherence { private sealed class RecentItemCollection ... }`),
  so no code in `AgentEyes.App` can name it, and `(RecentItemCollection)library.Rows` cannot be
  written. `TheGatedRowsCollection_CannotBeNamedOutsideTheModel` asserts that as a PRESENCE -
  nested, private, declared by the model, and still carrying `BeginCoherentUpdate`.
  Its honest limit: reflection still reaches it. What is closed is the cast, which is the form a
  bypass takes when nobody is trying to break in.
* **The divergence can no longer take the process down.** `ApplySnapshot` used to THROW when the fact
  table and the rows disagreed, on the path of `async void LoadRecent` where nothing catches it.
  `ReconcileFactsWithRows` now re-derives the fact table from the rows and logs an ERROR naming every
  corrected directory. That is a repair, not a fallback: the rows are what the user is looking at and
  the fact table is bookkeeping ABOUT those rows, so rebuilding the bookkeeping from the thing it
  describes hides nothing. `RepairedDivergences` makes it observable, because a silent self-heal is
  indistinguishable from one that never runs. `LoadRecent` also got an entry-point try/catch, so no
  future throw on that path can be fatal either.
  `ADivergenceForcedIntoTheRows_IsRepairedRatherThanThrownOntoTheUiThread` performs QA's bypass for
  real - by reflection, since the cast is gone - and requires no throw, the right rows, and
  `RepairedDivergences == 1`.

### MEDIUM - a guard distincted by method, not call site

`TheWindowSubscribesToLibraryChanged_InExactlyOnePlace` collapsed the call sites by declaring method,
so QA's second subscription inside `MainWindow::.ctor` left it green. It now counts CALL SITES and is
renamed for what it measures:
`TheWholeApp_WritesLibraryChanged_AtExactlyTwoCallSites_TheSubscriptionAndTheTeardown`.

Two, not one, because the constructor subscribes and the `Closed` handler - which the compiler folds
back onto the constructor - clears it. So that "two" is not a magic number, both roles are pinned by
a LITERAL STRING MATCH on the source, named as such. Together they close the gap a bare count leaves:
adding a rogue subscription makes it three, and deleting the teardown to keep the count at two fails
the source half. Both attacks are demonstrated in section 5.

### One test of my own that could not fail, and how it was found

`CompletingADeletion_DoesNotTombstoneARecordingReCreatedInTheSameFolder` was written asserting only
the rows. The mutation that removes the identity check from `CompleteDelete` did NOT turn it red -
because tombstoning a directory whose row is present is exactly the state `ReconcileFactsWithRows`
repairs, so the rows came out right either way. The test was asserting something the model would
produce with or without the code it claimed to cover.

What the identity check actually buys is that a legitimate re-import does not trip the corruption
alarm, so that is what it now measures (`RepairedDivergences == 0`) - and the mutation turns it red.
This is recorded because it is the same class of defect QA found in criterion 6, caught here by
insisting every guard be demonstrated failing rather than assumed to work.

### Noted, not actioned

Epoch overflow (QA's N11) needs ~9.2e18 operations on a `long` incremented once per snapshot or live
change. Agreed: theoretical, not actionable.

---

## 1. The model, in one page

Reading the library is slow, so it happens on a worker, and several reads overlap by design. Before
this change each read CLEARED the collection and reinstalled its own answer, while the live routes
(screenshot insert, saved-recording insert, rename, RefreshNaming, delete) mutated the same
collection with no ordering against them.

The previous attempt - "latest generation wins, drop the rest" - was rejected twice by the
independent gate, because a snapshot that is DROPPED takes with it everything only it knew about.
That is not fixed by choosing the other winner; a whole-snapshot decision is the wrong granularity.

`src/AgentEyes.App/LibraryCoherence.cs` keeps ONE monotonic counter on the owning thread. A snapshot
claims its START epoch before its worker touches the disk; every live change claims its own epoch as
it happens. For each recording DIRECTORY the model keeps exactly one fact: the epoch of the newest
evidence about that recording, and whether it said Present or Removed. A landing snapshot is then
MERGED, one recording at a time:

| the snapshot... | the fact is NEWER than its start | no newer fact |
|---|---|---|
| HAS the recording | live wins: Present leaves the row untouched, Removed refuses to resurrect | adopt the fresh values into the existing row, or add it |
| LACKS the recording | the row stays - the snapshot read the disk before it existed | remove the row, tombstoned at the snapshot's epoch |

Nothing is ever dropped, so there is nothing to merge back and nothing to retry. "Newest wins" still
holds - **per recording**, which is the granularity at which the evidence actually differs.

Two consequences worth naming:

- **A failed read is not an empty library.** A worker that throws goes to `AbandonSnapshot` and
  changes nothing. Nothing waits on a generation either, so a failing or hanging read blocks nothing.
- **Rows are updated, not replaced.** A reload reuses the existing `RecentItem` for a directory it
  already has, which is what keeps a row held across an await attached (and keeps thumbnails, live
  status and the user's selection alive across a reload). `Refresh` and `SetStatus` re-resolve by
  directory anyway, so a row that WAS detached still updates what is visible.

`src/AgentEyes.App/RecentItemCollection.cs` is now a GATE. All five `Collection<T>` mutation
virtuals refuse a change made outside an open coherence scope, or from another thread. Every
spelling - `Add`, `Insert`, `Remove`, `RemoveAt`, `Move`, `this[i] =`, `Clear`, an `IList` cast, a
wrapper - funnels through those five, so the spelling cannot be what decides. The scope also keeps
issue #178's fix: a whole reload settles as ONE notification, a single-row change re-raises itself
precisely (so a saved screenshot no longer resets the list), and a reload that changed nothing
raises nothing at all.

## 2. Files

| File | What changed |
|---|---|
| `src/AgentEyes.App/LibraryCoherence.cs` | NEW - the model. Round 2: the `Removing`/`Removed` deletion lifecycle, `CompleteDelete`, `ReconcileFactsWithRows`, `RepairedDivergences`, `LibraryDeletion` |
| `src/AgentEyes.App/RecentItemCollection.cs` | the gate + notification coalescing; `ReplaceAll` removed. Round 2: now a PRIVATE NESTED type of `LibraryCoherence` |
| `src/AgentEyes.App/MainWindow.xaml.cs` | `_recent` -> `_library`; every route goes through the model; `RecentItem.AdoptFrom` + notification on every bound property. Round 2: `DeleteRecordings` settles the deletion, `LoadRecent` has an entry-point catch |
| `tests/AgentEyes.Tests/LibraryCoherenceTests.cs` | NEW - 38 tests (30 in round 1, +8 in round 2) |
| `tests/AgentEyes.Tests/LibraryDefectDecoys.cs` | decoys for the new structural guard |
| `tests/AgentEyes.Tests/LibraryFlatListTests.cs` | 3 tests retargeted onto the new owner of the behaviour |

## 3. Acceptance criteria

Every interleaving is FORCED on one thread in the model's own vocabulary (begin a snapshot, do
something live, land the snapshot). Nothing races and hopes.

| # | Criterion | Where | How QA exercises it |
|---|---|---|---|
| 1 | An older reload completing after a newer one does not install its stale snapshot | `AnOlderReload_LandingLast_DoesNotInstallItsStaleSnapshot`, `...DoesNotRemoveARecordingOnlyTheNewerReloadSaw` | `dotnet test --filter LibraryCoherenceTests` |
| 2 | A live insert during an in-flight reload loses NEITHER the inserted row NOR the recording only the snapshot had | `AnInsertDuringAnInFlightReload_LosesNeitherTheInsertedRowNorTheSnapshotOnlyRecording`, `...DoesNotLoseTheRepairedTitleThatOnlyTheSnapshotCarried` | same. Note both tests assert BOTH halves, so either single-winner design fails them |
| 3 | A reload whose worker throws does not blank or truncate the Library, and does not prevent a concurrent successful reload from landing | `AReloadWhoseWorkerThrows_DoesNotBlankOrTruncateTheLibrary`, `AFailedReload_DoesNotBlockAnOlderSuccessfulReloadFromLanding`, `AHungReload_NeverSettled_DoesNotStopLaterReloadsFromLanding`, `ASuccessfulReloadThatFoundNothing_EmptiesTheLibrary` | same |
| 4 | A rename completing during an in-flight reload is not reverted by it | `ARenameDuringAnInFlightReload_IsNotRevertedByThatReload`, `ARefreshDuringAnInFlightReload_...`, `ALaterReload_StillUpdatesARenamedRow` | same |
| 5 | A row captured before an await and refreshed after a reload replaced it leaves no stale visible row | `ARowHeldAcrossAnAwait_IsStillTheLibrarysRowAfterAReloadCompleted`, `...WhenTheHeldOneIsDetached`, `StatusOnAHeldRow_...`, `RefreshingARowForADeletedRecording_...`, `AReloadLandingOnARow_DoesNotWipeItsLiveStatus` | same |
| 6 | A reload starting after a UI delete but before the directory is removed does not resurrect the row | **Round 2, see section 0.** `AReloadStartingAfterTheDelete_WhileTheDirectoryIsStillBeingRemoved_DoesNotResurrectTheRow`, `ADeleteWithOneReloadAlreadyInFlight_...`, `AnUnrelatedSnapshotSettling_...`, `AReloadInFlightWhenTheDeletionSettled_...`, `AReloadAfterADeletionThatFAILED_ShowsTheRecordingAgain`, `ADeletionThatIsNeverSettled_KeepsHidingTheRecording`, `CompletingADeletion_DoesNotTombstoneARecordingReCreatedInTheSameFolder`, plus `AReloadThatStartedBeforeTheDirectoryWasRemoved_...` (the round-1 ordering, still covered) | same |
| 7 | A structural guard proves every mutation route participates, and FAILS on a direct `RemoveAt`/`Move`/indexer mutation - demonstrated, not asserted | `EveryDirectMutationOfTheLibrarysRows_IsRefused` (11 spellings, each observed to throw), `TheGate_RefusesMutationsOnly_AndLeavesEveryReadWorking`, `TheModelAndItsRows_RefuseEveryCallFromAnotherThread`, `NoMethodOutsideTheCoherenceModel_TouchesTheLibrarysRows` + `TheRowsScan_ReportsEveryBypassOfTheModel`; **round 2** `TheGatedRowsCollection_CannotBeNamedOutsideTheModel` + `ADivergenceForcedIntoTheRows_IsRepairedRatherThanThrownOntoTheUiThread` | same. Section 5 below has the mutation evidence |
| 8 | Enumerated proof that no route bypasses the model, including all three RepairService triggers | `EveryLibraryRoute_GoesThroughTheCoherenceModel` (12 routes read from the compiled assembly; `DeleteRecordings` must now reach `Delete` AND `CompleteDelete`), `EveryRepairServiceTrigger_ReachesTheLibraryOnlyThroughLibraryChanged`, `TheWholeApp_WritesLibraryChanged_AtExactlyTwoCallSites_TheSubscriptionAndTheTeardown` | same |
| 9 | `dotnet build -c Release` clean, `dotnet test -c Release` `Failed: 0` | - | section 6 |

### The route enumeration (criterion 8), in full

Read from `AgentEyesApp.dll`, and fail-closed: each route must EXIST in the binary before its calls
are checked, so a rename fails the test rather than silently checking nothing.

| Route | Must reach |
|---|---|
| `MainWindow::.ctor` | `LibraryCoherence::get_Rows`, `set_SortKeyChanged`, `MainWindow::LoadRecent` |
| `MainWindow::LoadRecent` | `BeginSnapshot`, `ApplySnapshot`, `AbandonSnapshot`, `LibrarySnapshot::NewestFirst` |
| `MainWindow::Record_Click` | `Insert` |
| `MainWindow::StopAsync` | `Insert`, `SetStatus`, `Refresh` |
| `MainWindow::PackageDirAsync` | `Find`, `SetStatus`, `Refresh` |
| `MainWindow::RenameRecording_Click` | `Rename` |
| `MainWindow::DeleteRecordings` | `Delete`, `CompleteDelete` |
| `MainWindow::ImportVideo_Click` | `MainWindow::LoadRecent` |
| `MainWindow::Search_TextChanged`, `ResortLibrary`, `UpdateLibraryTotal`, `UpdateEmptyState` | `get_Rows` |

The **three RepairService triggers** are the last link of a chain, not an assumption:
`RepairService::ResumeAsync`, `TitleAsync` and `ThumbsAsync` are each required to raise
`LibraryChanged` (read from `agenteyes.dll`'s IL); the app's ONLY subscription to it is
`MainWindow::.ctor`; and `.ctor` is required to call `LoadRecent`, which is itself in the table.

## 4. What each guard can and cannot see

Stated rather than glossed, because an overclaiming guard has been rejected here repeatedly.

- **The runtime gate** is spelling-independent and is the load-bearing one. It sees any mutation of
  the rows however it is written or wherever from, because `Collection<T>` has only five mutation
  virtuals. It does NOT see a mutation of some OTHER collection someone later binds to the list.
- **The IL rows scan** sees which methods NAME the rows field. It does NOT see a method handed the
  collection as an argument or fetched through the public `Rows` property - that is exactly the hole
  the runtime gate closes, and `EveryDirectMutationOfTheLibrarysRows_IsRefused` demonstrates it
  closing on a caller doing exactly that, through the property, in eleven spellings.
- **The route enumeration** is an enumeration, so it is only as complete as its list - but it is
  fail-closed on each entry (a renamed route fails) and it reads the COMPILED assembly, so an alias
  or a helper does not evade it.
- **The RepairService chain** proves those three stages signal through `LibraryChanged` and that the
  callback lands in the model. It cannot prove that no future code in Core finds another way to a UI
  it cannot see.
- **The gated collection being private and nested** is a fact about VISIBILITY, checked by
  reflection over the running type. It stops the type being NAMED, so it stops a cast. It does not
  stop reflection - and the test that proves the divergence is repaired uses reflection itself to
  create the divergence, so that limit is not theoretical, it is exercised.
- **The reconciliation** repairs the fact table from the rows and counts itself in
  `RepairedDivergences`. It can see that the two disagree; it cannot see WHO made them disagree, and
  it does not try to say. The log names the directories, not a culprit.
- **The LibraryChanged call-site count** measures writes to that property in the compiled app, which
  is exact, plus two LITERAL STRING MATCHES on the source for the two roles. It cannot see a
  subscription made through reflection or a delegate stored elsewhere.
- **The retargeted re-sort guard** (`EveryRefreshNamingCallSite_ReSortsTheLibraryWhenTheStartTimeMoved`)
  is a LITERAL STRING MATCH over `LibraryCoherence.cs`, and says so in its own comment. It cannot see
  a second route added by reflection or a delegate; it throws rather than passing if nothing calls
  `RefreshNaming` at all.

## 5. Guards demonstrated FAILING

Every one of these was constructed in the product code, the test run was watched go RED, and the
mutation was reverted. `grep -rn MUTATION src/ tests/` is empty on the branch and the full suite is
green (section 6).

| # | The defect constructed | Tests that went RED |
|---|---|---|
| M1 | `ApplySnapshot` clears and reinstalls wholesale (the pre-issue-3 behaviour) | 10 red, incl. both failure-mode-1 tests, failure mode 2, both failure-mode-4 tests, failure mode 5, failure mode 6 |
| M2 | **The design the gate rejected twice**: a snapshot invalidated by anything newer is DROPPED | 7 red, incl. `AnInsertDuringAnInFlightReload_LosesNeither...` and `...DoesNotLoseTheRepairedTitle...`. Failure mode 1 stayed GREEN - which is the point: the criterion-2 tests are what separate the two wrong answers |
| M3 | `AbandonSnapshot` installs the failed worker's empty list | `AReloadWhoseWorkerThrows_DoesNotBlankOrTruncateTheLibrary` |
| M4 | A blocking generation gate - only the newest read may land | `AFailedReload_DoesNotBlockAnOlderSuccessfulReloadFromLanding` (+2) |
| M5 | The collection's gate made advisory (never throws) | `EveryDirectMutationOfTheLibrarysRows_IsRefused` |
| M6 | Both thread-affinity checks removed | `TheModelAndItsRows_RefuseEveryCallFromAnotherThread` |
| M7 | A type in `AgentEyes.App` outside the model holding and mutating a `_rows` collection | `NoMethodOutsideTheCoherenceModel_TouchesTheLibrarysRows` |
| M8 | `DeleteRecordings` stops calling `LibraryCoherence::Delete` | `EveryLibraryRoute_GoesThroughTheCoherenceModel` ("does not call 'LibraryCoherence::Delete'") |
| M8b | `DeleteRecordings` renamed away (the FAIL-CLOSED arm) | same test ("is not in AgentEyesApp.dll ... would pass by finding nothing") |
| M9 | `RepairService.ThumbsAsync` stops raising `LibraryChanged` | `EveryRepairServiceTrigger_ReachesTheLibraryOnlyThroughLibraryChanged` |
| M10 | A second subscriber to `RepairService.LibraryChanged` | `TheWindowSubscribesToLibraryChanged_InExactlyOnePlace` |
| M11 | Notification coalescing removed (one event per row) | `LoadingEveryRow_RaisesOneResetRatherThanOneEventPerRow` |
| M12 | Every change raises a Reset | `ASingleLiveInsert_RaisesAnAddRatherThanAReset` |
| M13 | `Refresh`/`SetStatus` write on the caller's row instead of re-resolving it | `ARowHeldAcrossAnAwait_UpdatesTheLibrarysRow_WhenTheHeldOneIsDetached`, `StatusOnAHeldRow_...` |

### Round 2

Eight more, same discipline: constructed in the product code, watched go RED, reverted.

| # | The defect constructed | Tests that went RED |
|---|---|---|
| (pre-fix) | NOTHING - the three new criterion-6 tests were run against the UNMODIFIED round-1 code, before any product change | all three, with `Expected: ["one_video"] Actual: ["doomed_video", "one_video"]` |
| R1a | The deletion tombstoned as SETTLED at delete time (the round-1 epoch bound) | 5 red: all three criterion-6 interleavings, `ADeletionThatIsNeverSettled_...`, `AReloadInFlightWhenTheDeletionSettled_...` |
| R1b | `PruneTombstones` prunes a deletion that is still RUNNING (QA's eager-prune cause) | the same 5 |
| R2 | `CompleteDelete` never records the outcome | `AReloadAfterADeletionThatFAILED_ShowsTheRecordingAgain` |
| R3 | `CompleteDelete` FORGETS the directory instead of tombstoning it at the completion epoch | `AReloadInFlightWhenTheDeletionSettled_DoesNotResurrectTheDeletedRow` |
| R4 | `CompleteDelete` tombstones whatever has happened to the directory since (no identity check) | `CompletingADeletion_DoesNotTombstoneARecordingReCreatedInTheSameFolder` - and see section 0: this mutation is what exposed that the test could not fail as first written |
| R5 | Divergence throws from inside the merge again instead of being reconciled | `ADivergenceForcedIntoTheRows_IsRepairedRatherThanThrownOntoTheUiThread` |
| R6 | The gated collection moved back out of the model as an assembly-internal type | `TheGatedRowsCollection_CannotBeNamedOutsideTheModel` |
| R7 | **QA's exact attack**: a second `_repair.LibraryChanged +=` inside `MainWindow::.ctor` | `TheWholeApp_WritesLibraryChanged_AtExactlyTwoCallSites_...` ("writes ... at 3 call site(s)") |
| R7b | The teardown DELETED and a rogue subscription added, so the call-site count stays at two | the same test, on the source half |
| R8 | `DeleteRecordings` stops calling `CompleteDelete` | `EveryLibraryRoute_GoesThroughTheCoherenceModel` ("does not call 'LibraryCoherence::CompleteDelete'") |

**The discriminator was re-run after the round-2 changes**, because they touch `ApplySnapshot`. The
rejected "latest generation wins, drop the rest" design was re-applied to the new merge: 12 red / 800
green, with BOTH criterion-2 tests red and BOTH failure-mode-1 tests still green. The separation QA
independently reproduced in round 1 survives the fix, and the criterion-6 tests now fail under it too.

An unplanned fourteenth demonstration is in the record too: while the branch was being written, the
gate fired on a genuine pre-existing direct `Add` in `LibraryFlatListTests` and failed that test with
the "outside its coherence model" message. That is the guard catching a real mutation nobody planted.

The two IL guards additionally carry compiled negative controls in `LibraryDefectDecoys.cs`
(`LibraryBypass::RemoveAtDirectly`, `MoveDirectly`, `AssignThroughTheIndexer`, `ThroughAWrapper`) and
a NARROWNESS control (`LibraryDefects.LibraryCoherence`, which touches its own rows and must NOT be
reported).

## 6. The dev gate

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 812, Skipped: 0, Total: 812
```

(801 on `main`; 804 after round 1; 812 after round 2 - +38 new for this issue in total, 1 round-1
test replaced because it encoded the criterion-6 defect, 2 retargeted.)

## 7. Running-app verification I already did

I ran this myself - it is not a task for QA to repeat blindly, but QA should re-run it independently.
It is READ ONLY with respect to the owner's recordings: it records nothing, renames nothing, deletes
nothing. Script: `library-proof.ps1` (reproduced below).

The owner's installed v1.4.8 was idle (`/status` -> `State: idle`, `PendingTranscriptions: 0`) and was
stopped, the branch build was launched, the Library rail was selected by **UI Automation** (no
force-foreground, no synthesized input), and each rendered row's AutomationId - the recording folder
name, per issue #178 - was compared against the folders on disk.

```
ON DISK: 44 recording folder(s) with a manifest.json
main window found
HEALTH: ok=True app=AgentEyes
RENDERED: 44 library row(s)
MISSING FROM THE LIBRARY: 0
IN THE LIBRARY BUT NOT ON DISK: 0
17:01:39.772 [INFO] [LibraryCoherence] BeginSnapshot: epoch=1, in flight=1, rows=0
17:01:40.485 [INFO] [LibraryCoherence] ApplySnapshot: epoch=1, snapshot=44, added=44, updated=0,
                    removed=0, kept live=0, refused resurrection=0, rows=44
no crash log (good)
```

Expected vs actual: expected every recording on disk to appear exactly once and the gate never to
fire in the real WPF binding path; actual 44/44 with zero missing, zero extra, no
"outside its coherence model" exception anywhere in the log, and no crash log. (The
`ffmpeg would not start` entries in the same log file are at 16:57 and 16:59, from the unit suite's
deliberate start-failure fixtures in `%TEMP%\agenteyes-guard-*`, not from this run.)

The owner's installed v1.4.8 was then restarted and confirmed back up
(`/version` -> `AgentEyes 1.4.8`, `/status` -> `idle`, signed in). **It was restarted with `--tray`**;
if the owner had a window open before, it is reachable from the tray icon.

**Round 2 re-run** (same script, same read-only guarantees, after the criterion-6 and gate changes):

```
ON DISK: 44 recording folder(s) with a manifest.json
HEALTH: ok=True app=AgentEyes
RENDERED: 44 library row(s)
MISSING FROM THE LIBRARY: 0
IN THE LIBRARY BUT NOT ON DISK: 0
17:42:14.778 [INFO] [LibraryCoherence] BeginSnapshot: epoch=1, in flight=1, rows=0
17:42:15.399 [INFO] [LibraryCoherence] ApplySnapshot: epoch=1, snapshot=44, added=44, updated=0,
                    removed=0, kept live=0, refused resurrection=0, rows=44
no crash log (good)
```

No divergence line and no gate exception in the log, so `RepairedDivergences` stayed 0 on the real
startup path. The owner's v1.4.8 was stopped only from idle and restarted afterwards, verified up.

**The delete path was NOT exercised against the owner's recordings, deliberately.** Criterion 6 is
about deleting a recording, and everything under `%USERPROFILE%\Videos\AgentEyes` is the owner's
irreplaceable real data - the Library reads that root and there is no runtime override for it. The
criterion-6 interleavings are proved on temporary fixtures instead, deterministically, and each one
was seen failing on the pre-fix code. QA should not delete a real recording to check this either.

## 8. Suggested QA scope

- **Start with criterion 6.** That is what failed last time. The three interleavings QA constructed
  (N1, N2, N3) are now shipped as tests; re-run them, and re-run mutation R1a from section 5, which
  restores the round-1 epoch-bound tombstone and should turn all three red. If it does not, this fix
  is not doing what this note claims.
- **Re-run the suite yourself** (`dotnet build` + `dotnet test`, ~12s, silent). Then re-run at least
  two of the mutations in section 5 - M2 is the most valuable, because a design that passes
  everything except the criterion-2 tests is the design the gate already rejected twice. It was
  re-run after the round-2 changes and still separates them.
- **Re-attack the two guards you broke last round**: the cast around the gate (finding 6a) and the
  second `LibraryChanged` subscription inside the constructor (finding 6b). Both should now fail;
  R7b in section 5 is the subtler version of 6b, where the count is kept at two.
- **A gui smoke is worth it here** - this is the Library UI. `scripts/gui-smoke.ps1 -Confirm`. **Read
  it before running it**: it points at `bin\Release\` rather than `bin\x64\Release\`, so it will
  either fail "app not built" or drive a stale binary, and it backs up and rewrites the owner's
  `presets.json`/`config.json` and records audio. The read-only proof in section 7 is the safer
  instrument and it is scripted; prefer it, or fix the path first.
- `scripts/api-smoke.ps1` is not indicated: the REST surface is unchanged.
- **Worth an independent eye**: `AdoptFrom` now copies every bound value onto an existing row. I made
  the previously non-notifying bound properties (`Badge`, `BadgeBrush`, `Duration`,
  `WalkthroughVisibility`, `CostTip`, `MediaPath`, `MediaKind`, `IconGeometry`, `PreviewTip`,
  `PreviewVisibility`) raise `PropertyChanged`, because in-place updates would otherwise change them
  silently and leave the card rendering the old value. Two values are deliberately NOT adopted:
  `Status` (live progress the running app owns, which no manifest knows about) and `Thumb` when the
  fresh card has none (a snapshot taken before the poster frame existed must not blank a decoded
  thumbnail). Both are covered by tests, but they are judgement calls worth confirming.

## 9. CenCon impact

No drift. One new internal type inside `AgentEyes.App`; no component-map change; no change to the
privacy posture - the Library is local UI state and nothing is recorded, sent or newly exposed. No
change to the REST surface, the installer, or `docs/cencon/`.
