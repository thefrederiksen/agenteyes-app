# IMPLEMENTATION PLAN - Issue #3 (agenteyes-app)

## UNDERSTANDING

The Library has no coherence model. `LoadRecent` is `async void`, several invocations overlap, and
each one CLEARS the collection and reinstalls its own snapshot wholesale. Live mutations (screenshot
insert, saved-recording insert, import, rename, RefreshNaming, delete) touch the same collection with
no ordering against those snapshots. Six independent failure modes follow, and the previous attempt
("newest generation wins, drop the rest") was rejected twice because DROPPING a snapshot loses
whatever only that snapshot contained.

## THE MODEL: per-recording evidence epochs, merged - never a wholesale replace

One monotonic counter (`_clock`) on the UI thread. Every snapshot takes a START epoch BEFORE its
worker reads the disk; every live mutation takes its own epoch when it happens. For each recording
DIRECTORY the model keeps one fact: `(epoch, Present|Removed)` - the newest evidence about that one
recording.

A snapshot that lands does NOT replace the collection. It is merged row by row:

* a directory the snapshot HAS, whose fact is NEWER than the snapshot's start -> the live row wins
  (Present: leave the row exactly as it is; Removed: do not re-add it).
* a directory the snapshot HAS with no newer fact -> adopt the fresh values INTO the existing row
  object (never a new object), or add the row when it is genuinely new.
* a row the snapshot LACKS whose fact is NEWER than the snapshot's start -> keep it. The snapshot
  simply read the disk before that recording existed.
* a row the snapshot LACKS with no newer fact -> remove it and tombstone it at the snapshot's epoch.

Nothing is ever dropped, so there is nothing to merge back or retry. "Newest wins" is applied PER
RECORDING, which is the granularity at which the evidence actually differs.

## CHANGES

1. `src/AgentEyes.App/LibraryCoherence.cs` (new) - the model above. Owns the rows collection, the
   fact table and the clock; thread-affine to its creating thread (throws otherwise). Public routes:
   `BeginSnapshot`, `ApplySnapshot`, `AbandonSnapshot`, `Insert`, `Delete`, `Rename`, `Refresh`,
   `SetStatus`, `Find`, `Rows`.
2. `src/AgentEyes.App/RecentItemCollection.cs` - the rows collection becomes GATED. All five
   `Collection<T>` mutation virtuals (`InsertItem`, `RemoveItem`, `SetItem`, `ClearItems`,
   `MoveItem`) throw unless a coherent-update scope is open, and off-thread mutation throws. Every
   spelling - `Add`, `Insert`, `Remove`, `RemoveAt`, `Move`, `this[i] =`, `Clear`, an `IList` cast,
   any wrapper - funnels through those five, so the gate cannot be spelled around. `ReplaceAll` is
   replaced by the scope, which still coalesces a whole reload into ONE notification (issue #178's
   O(n squared) fix is preserved, and an unchanged reload now raises NOTHING at all).
3. `src/AgentEyes.App/MainWindow.xaml.cs` - `_recent` becomes `_library` (a `LibraryCoherence`).
   Every route that reads or mutates the Library goes through it; `LoadRecent` brackets its worker
   with `BeginSnapshot`/`ApplySnapshot`, and a THROWN worker calls `AbandonSnapshot` and applies
   nothing (it no longer installs an empty list over a good library).
   `RecentItem` gains `AdoptFrom` (in-place update) and notification on the bound properties that
   previously only ever changed by replacing the whole object.
4. `tests/AgentEyes.Tests/LibraryCoherenceTests.cs` (new) - one deterministic interleaving test per
   failure mode, the gate demonstrations, and the enumerated route proof.
5. `tests/AgentEyes.Tests/LibraryDefectDecoys.cs` - decoys for the new structural guard.
6. `tests/AgentEyes.Tests/LibraryFlatListTests.cs` - the RefreshNaming re-sort guard is retargeted to
   the file that now owns the call, and the one-notification test drives the new scope.

## ACCEPTANCE CRITERIA -> HOW EACH IS MET

| # | How the code satisfies it | How QA verifies |
|---|---------------------------|-----------------|
| 1 | A landing snapshot may only overwrite a row whose evidence is OLDER than its start epoch | `AnOlderReload_LandingLast_DoesNotInstallItsStaleSnapshot` |
| 2 | Snapshots are merged per recording, never dropped; the live row and the snapshot-only row both survive | `AnInsertDuringAnInFlightReload_LosesNeitherTheInsertedRowNorTheSnapshotOnlyRecording` |
| 3 | `AbandonSnapshot` applies nothing; there is no generation gate, so nothing blocks | `AReloadWhoseWorkerThrows_...` + `...DoesNotBlockAConcurrentSuccessfulReload` |
| 4 | `Rename` stamps a fact newer than the in-flight snapshot's start | `ARenameDuringAnInFlightReload_IsNotRevertedByThatReload` |
| 5 | Rows are updated IN PLACE, and every held-row route re-resolves by directory | `ARowHeldAcrossAnAwait_...` (both halves) |
| 6 | `Delete` tombstones the directory at a newer epoch than the in-flight snapshot's start | `AReloadThatStartedBeforeTheDirectoryWasRemoved_DoesNotResurrectTheDeletedRow` |
| 7 | The gate on the five mutation virtuals; demonstrated failing for RemoveAt/Move/indexer/Clear/Add/Insert/Remove and an IList cast, plus an IL guard with decoys | the gate tests + `NoMethodOutsideTheCoherenceModel_TouchesTheLibrarysRows` |
| 8 | Enumerated route proof over the compiled app assembly, fail-closed on a renamed route | `EveryLibraryRoute_GoesThroughTheCoherenceModel` |
| 9 | build + test | `dotnet build` / `dotnet test` |

## CENCON IMPACT

No drift. No component-map change (one new internal type inside `AgentEyes.App`), no privacy-posture
change - the Library is local UI state; nothing is recorded, sent or newly exposed.

## RISK

Medium. It touches every Library mutation route. Mitigated by the runtime gate (any route that
forgets the model throws immediately rather than corrupting silently) and by in-place row updates
(bindings, thumbnails and selection survive a reload, which they did not before).
