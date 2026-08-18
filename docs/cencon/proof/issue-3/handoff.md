# Developer handoff - issue #3

**[App] Library asynchronous refresh has no coherence model - reloads, inserts, renames and deletes race**

Repo: `thefrederiksen/agenteyes-app`. Branch: `issue-3-library-coherence`. Base: `8d46403` (v1.4.8).

I believe this is finished. Build clean, `dotnet test` 804/804, every guard demonstrated failing, and
the running app verified against the owner's real 44-recording library.

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
| `src/AgentEyes.App/LibraryCoherence.cs` | NEW - the model |
| `src/AgentEyes.App/RecentItemCollection.cs` | the gate + notification coalescing; `ReplaceAll` removed |
| `src/AgentEyes.App/MainWindow.xaml.cs` | `_recent` -> `_library`; every route goes through the model; `RecentItem.AdoptFrom` + notification on every bound property |
| `tests/AgentEyes.Tests/LibraryCoherenceTests.cs` | NEW - 30 tests |
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
| 6 | A reload starting after a UI delete but before the directory is removed does not resurrect the row | `AReloadThatStartedBeforeTheDirectoryWasRemoved_DoesNotResurrectTheDeletedRow`, `AReloadThatStartedAfterTheDelete_StillShowsARecordingWhoseDeletionFailed` | same |
| 7 | A structural guard proves every mutation route participates, and FAILS on a direct `RemoveAt`/`Move`/indexer mutation - demonstrated, not asserted | `EveryDirectMutationOfTheLibrarysRows_IsRefused` (11 spellings, each observed to throw), `TheGate_RefusesMutationsOnly_AndLeavesEveryReadWorking`, `TheModelAndItsRows_RefuseEveryCallFromAnotherThread`, `NoMethodOutsideTheCoherenceModel_TouchesTheLibrarysRows` + `TheRowsScan_ReportsEveryBypassOfTheModel` | same. Section 5 below has the mutation evidence |
| 8 | Enumerated proof that no route bypasses the model, including all three RepairService triggers | `EveryLibraryRoute_GoesThroughTheCoherenceModel` (12 routes read from the compiled assembly), `EveryRepairServiceTrigger_ReachesTheLibraryOnlyThroughLibraryChanged`, `TheWindowSubscribesToLibraryChanged_InExactlyOnePlace` | same |
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
| `MainWindow::DeleteRecordings` | `Delete` |
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
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 804, Skipped: 0, Total: 804
```

(801 before this change; +30 new, -1 replaced, 2 retargeted.)

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

## 8. Suggested QA scope

- **Re-run the suite yourself** (`dotnet build` + `dotnet test`, ~25s, silent). Then re-run at least
  two of the mutations in section 5 - M2 is the most valuable, because a design that passes
  everything except the criterion-2 tests is the design the gate already rejected twice.
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
