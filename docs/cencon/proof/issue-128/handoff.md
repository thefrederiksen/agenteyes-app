# Issue #128 - Developer handoff (Windows)

Branch: `issue-128-mic-chain-stall`. Machine: Windows 11 (SORENLAPTOP).

# ROUND THREE (after QA round two FAIL, report `qa-report-windows-round2.md`, defects R2-1 to R2-4)

Code commit: `3a1165d`. Round two's work stands and was not redone: D1's lock, D3's framing, D5, D6
and D7 are unchanged. This round fixes the four defects QA found. Evidence for everything below is
in `dev-r3/`.

I believe this is finished. All four defects are fixed. What is still not covered, and how I
established that, is in "Round three: what is still not covered".

Every new test goes through the path the product takes. The R2-1, R2-3 and R2-4 tests call
`RepairService.ResumeUnfinishedAsync(root, epoch)`. That is the method the repair tick runs: `RunAsync`
calls `ResumeUnfinishedAsync(epoch)`, which is now `ResumeUnfinishedAsync(RecordingPaths.Root, epoch)`.
The tests pass only a different root. That covers discovery (`FindUnfinished`), then `ResumeAsync`,
then `PostRecording.Resume`, then `Execute`. Nothing calls `PostRecording.Run` or `Reconcile` directly
to set up. The one place a test reaches past the entry point is the round-two test
`Reconcile_FinalFilePresentButRawInputGone_KeepsTheFile`. It calls `Reconcile` directly, and
`RepairPass_DeathAfterTheMuxCompleted_*` covers the same case through the real entry point.

## R2-1 (BLOCKING): a hard kill on the LAST attempt is now discovered and reconciled

**Cause, confirmed:** QA was right. Once attempts = 3, `NeedsMux` is false. Round two's guards make
`NeedsThumb` and `NeedsTranscription` false while `PendingMux` is set, and `NeedsCompose` is false
without a camera. So `Outstanding()` was empty and `FindUnfinished` never listed the recording.
`Reconcile` is reachable only from `Execute`, so it never ran.

**Fix:** `PostRecordingPlan.NeedsMuxReconcile(dir)` (PostRecordingPlan.cs). It is true while a
pending mux has something for `Reconcile` to settle, even with no attempt left:
- a dead attempt: the mux record is still `running`, or
- a proven-complete final file (R2-3 below).

`Outstanding()` lists the mux stage when `NeedsMux || NeedsMuxReconcile`. Once `Reconcile` has
settled the recording (record `failed` with `interrupted: ...` and fragment removed, or mux
published), the predicate is false, so an exhausted recording does NOT come back on every repair
tick. A mux that another live process holds is `running` too, so it is listed. `Execute` then fails
`TryAcquire` and leaves it, exactly as it already did with attempts left.

**Paired proof:**
- Round-two code (89de539) plus the new tests, with only the `ResumeUnfinishedAsync(root, ...)`
  seam added (`dev-r3/prefix-seam.diff`): `RepairPass_HardKillOnTheLastAttempt_IsDiscoveredReconciledAndThenLeftAlone`
  FAILS at its first assertion. Discovery returned an empty list where it expected the recording,
  which is QA's `unfinished=0` (`dev-r3/prefix-new-tests-on-round-two-code.txt`).
- Fixed code (3a1165d): the same test PASSES. Discovery lists the recording; after the pass, the mux
  is `failed`, attempts 3, the error starts `interrupted: mux attempt 3`, the fragment is gone, no
  fourth mux ran, no later stage ran, and a second discovery returns nothing.
- Mutations N1 (predicate always false) and N2 (`Outstanding` ignores it) each fail 4 tests by name.

## R2-2 (BLOCKING as a CI gate): the D3 instrument test is deterministic

**Fix** (`MicChainStallTests.ArnndnFraming_PartialFinalFrame_ArnndnEmitsNoNonFiniteSample`): the bare
arnndn run is repeated until it reproduces the defect, up to 10 times. Nothing else changed:
- reproducing still means a non-finite sample OR ffmpeg dying;
- the framed run must still exit 0, produce samples and contain 0 non-finite samples;
- under `CI=true`, a bare run that never reproduces still fails the test.

The length check now compares the framed output with every bare run that survived. The test output
lists every bare run.

There was no conflict between determinism and the assertion, so nothing was loosened. The
probability argument: QA measured 2 of 30 bare mono runs clean on 9.0.2. Ten clean runs in a row is
about (2/30)^10 = 2e-12. Stereo had 0 clean runs in 30.

**Paired proof:**
- **Correct build, 9.0.2, `CI=true`, 20 runs** (`dev-r3/d3-instrument-CI-true-ffmpeg-9.0.2-20-runs.txt`):
  - 20 of 20 pass.
  - Of the 40 test cases, 38 reproduced on the first bare run. **2 needed a second run**: `exit 0, 0
    non-finite of 480480; exit 0, 480 non-finite of 480480`. Those 2 are exactly the cases the
    single-shot test would have failed, the same rate as QA's 1 in 20.
- **Known-bad, 8.1.1 (no defect), `CI=true`** (`dev-r3/d3-instrument-CI-true-ffmpeg-8.1.1.txt`):
  both cases FAIL by name, `did not emit arnndn's end-of-stream NaN frame in 10 bare run(s)`. The
  instrument still fires on an ffmpeg that cannot exercise D3.
- M9 (framing removed) still fails its 3 tests.
- **CI:** see "Gate (round three)". Several runs were checked at the final head, not one.

## R2-3: decided behaviour - a mux that completed before a death is PUBLISHED

**Decision:** the file at the final path is a completed mux when `OriginalBackup.Preserve` has
already renamed one of its raw inputs to its `.original` backup: the input is gone AND its backup
exists. `FinalizePending` renames only after the mux has returned, so that is proof. The intended
behaviour is to finish what the dead process was doing, not to record a failure:
- `Reconcile` calls `RecordingService.CompleteInterruptedFinalize`. It runs no ffmpeg. It completes
  the remaining renames, records every backup present, sets the duration (video probed, like the mux
  branches) and clears `PendingMux`.
- The mux is journaled `done` with no error, and its attempt count is unchanged. Publishing is not
  an attempt.
- `Execute` then continues in the same pass to compose, thumbnail and packaging, because the media
  is final.
- This path and `FinalizePending` share one tail, `PublishFinalized`, so the two cannot drift. The
  IL manifest-writer pin moved from `FinalizePending` to `PublishFinalized`.

**The other half:** an input that is gone with NO backup proves nothing. Something outside AgentEyes
removed it. The file is neither published nor deleted. `FinalizePending` now refuses a missing input
BEFORE any ffmpeg starts, naming the input (`its input raw.mp4 is missing ... left as it is`). That
closes the processing-off route by which a failed pass's `RemovePartialOutput` deleted the file. The
attempt is counted, so the ceiling still bounds it.

**Paired proof** (round-two code vs fixed code, same tests, real `FinalizePending` as the mux step):
- `RepairPass_DeathAfterTheMuxCompleted_PublishesTheFileAndRunsTheLaterStages`, attempts {1, 3} x
  processing {off, defaults}:
  - Round-two code: **all 4 FAIL**. Processing off: `the completed mux output was deleted`, which is
    QA's 235,246-byte deletion. Defaults: `PendingMux` still set. With attempts = 3, discovery did not
    even list the recording.
  - Fixed code: all 4 PASS. The file is byte-identical, `PendingMux` is null, `recording.original.mp4`
    is in `OriginalFiles`, the mux is `done` with no error, the attempt count is unchanged, and the
    thumbnail stage ran.
- `RepairPass_InputGoneWithoutABackup_KeepsTheFinalFileAndNamesTheMissingInput`:
  - Round-two code: FAILS, `a file that could not be proven a fragment was deleted`.
  - Fixed code: passes. The file is kept, attempt 2 is recorded with `raw.mp4 is missing`, and no
    later stage ran.
- Mutations: N3 (round-two Reconcile) fails 5 tests, N9 (`Execute` stops after publishing) 4, N8
  (only newly-renamed backups recorded) 4, and N4 (missing-input check removed) 1.

## R2-4: `NeedsCompose` is guarded, and the M13 declaration is corrected

**Fix:** `NeedsCompose` returns false while `PendingMux` is set, the same guard as `NeedsThumb` and
`NeedsTranscription`. That closes both of QA's observations: the burned compose attempt, and the
camera recording that discovery listed on every repair tick.

**Paired proof:**
- Round-two code: `RepairPass_CameraRecordingWithAnExhaustedMux_BurnsNoComposeAndIsNotRediscovered`,
  `RepairPass_CameraRecordingKilledOnTheLastMuxAttempt_ReconcilesAndBurnsNoCompose` and
  `NeedsCompose_PendingMux_IsFalse_AndTrueOnceTheMuxIsDone` all FAIL.
- Fixed code: all 3 pass.
- N5 (guard removed) fails all 3 by name.

**Round two's M13 declaration was wrong, and QA was right.** I named two of the three predicates
`Execute` consults after the mux and missed `NeedsCompose`. The corrected statement, and how I
established it:
- **The enumeration, from source, not memory.** This command lists every stage predicate and
  `RunStage` in `Execute`:
  `awk '/private static PostRecordingOutcome Execute\(/,/^        }$/' src/AgentEyes.Core/PostRecording.cs | grep -n "PostRecordingPlan\.\|RunStage("`
  After the mux block it prints exactly three predicates: `NeedsCompose` (line 61 of the method),
  `NeedsThumbnail` (76) and `NeedsPackage` (87). Plugins runs only inside
  `if (RunStage(... PackageStep ...))` (99-102), so it is gated by `NeedsPackage`.
- **Each of the three now refuses while a mux is pending**: `PostRecordingPlan.cs:109`,
  `Thumbnails.cs:170` and `TranscriptionBacklog.cs:89`. `NeedsThumbnail` and `NeedsPackage` delegate
  to the last two. `grep -n "PendingMux != null) return false"` over those three files prints those
  three lines.
- **Each guard is killed by its own mutation**: N5 (3 tests), M7 (1) and M8 (1).
- **So M13 alone is an equivalent mutant.** Without the early return, `Execute` reaches the three
  predicates, all false, and returns at `NeedsPackage` with a different log line. The full suite
  confirms it: M13 alone, 0 failures.
- **Its limit:** if a FOURTH later stage is ever added to `Execute` without a `PendingMux` guard, M13
  becomes observable and nothing here would catch it. The awk command above is the check to re-run.

## The mutation list, from ONE final state (3a1165d)

Method (`dev-r3/mutate.py.txt`, raw output `dev-r3/mutations-r3.txt`), one mutation at a time:
1. apply exact replacements, each checked to match once;
2. `dotnet build AgentEyes.sln -c Release`;
3. FULL `dotnet test AgentEyes.sln -c Release` with `QA_RECORD_FFMPEG_DIR` = shipped 9.0.2;
4. record failures by name, and count ffmpeg processes still running;
5. restore with `git checkout`, and prove the restore with `git diff --quiet -- src tests`.

The baseline diff was empty, every restore was empty, and the final rebuild had 0 errors.

The run was split in two. The host stopped the first invocation for low system memory
after N2 (2026-11-14). It left N3 applied, which I restored by `git checkout` and verified empty. With
the owner's go-ahead, the remaining six ran on 2026-11-15 against the same commit, `3a1165d`.
Round two's 19 were re-run here too, so the whole list is from this one state.

| # | Fix disabled | Failed (full suite) | Failed, by name | ffmpeg left |
|---|---|---|---|---|
| M1 | D1: `FinalizePending` takes no lock | 1 | `FinalizePending_LockHeldByAnotherProcess_RefusesWithoutStartingASecondWriter` | 0 |
| M2 | D1: package path does not count the attempt | 2 | `PackageRun_PendingMuxThatFails_CountsTheAttempt`, `RunForPackage_FailedAttempt_IsCountedAndJournaled` | 0 |
| M3 | D1: `Package.RunAsync` reverted to round one | 1 | `PackageRun_PendingMuxThatFails_CountsTheAttempt` | 0 |
| M4 | D1: runner ignores a held lock | 1 | `PostRecordingRun_LockHeldByAnotherProcess_LeavesTheMuxToItAndCountsNothing` | 0 |
| M16 | D1: lock file shareable | 2 | `MuxLockTests.TryAcquire_Free_HoldsTheFileExclusivelyUntilDisposed`, `MuxLockTests.TryAcquire_SameThreadTwice_IsReentrantAndReleasesOnTheOutermostDispose` | 0 |
| M5 | D2: dead attempt not recorded failed | 3 | `PostRecordingRun_HardKillOnTheLastAttempt_...`, `RepairPass_CameraRecordingKilledOnTheLastMuxAttempt_...`, `RepairPass_HardKillOnTheLastAttempt_...` | 0 |
| M6 | D2: fragment not removed | 5 | `ManifestWriterIlTests.EveryFileWriteInTheProduct_IsAPinnedCallSite`, `PostRecordingRun_HardKillOnTheLastAttempt_...`, `PostRecordingRun_HardKillWithAttemptsLeft_...`, `RepairPass_CameraRecordingKilledOnTheLastMuxAttempt_...`, `RepairPass_HardKillOnTheLastAttempt_...` | 0 |
| M14 | D2: runner does not call Reconcile | 8 | the two `PostRecordingRun_HardKill*`, `RepairPass_CameraRecordingKilled...`, `RepairPass_HardKillOnTheLastAttempt_...`, and all 4 `RepairPass_DeathAfterTheMuxCompleted_*` | 0 |
| M7 | D2: `NeedsThumb` guard removed | 1 | `NeedsThumb_PendingMuxWithAFileAtTheFinalPath_IsFalse` | 0 |
| M8 | D2: `NeedsTranscription` guard removed | 1 | `NeedsTranscription_PendingMuxWithAFileAtTheFinalPath_IsFalse` | 0 |
| M9 | D3: framing removed | 3 | `MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames`, `MixWavs_DefaultSettingsMicWithNanSample_...`, `ProcessVideoMic_DefaultSettingsFiniteS16MonoNotAWholeFrame_Completes` | 0 |
| M10 | D4: `RunStage` unwatched | 1 | `RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog` | 0 |
| M11 | D4: fixed 60 s bound | 1 | `RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog` | 0 |
| M15 | QA mutation B: no stall guard | 2 | `RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog`, `RunWatched_FilterThatStallsOnNan_FailsWithinTheBound` | 0 |
| M12 | D5: cleanup error replaces stage error | 1 | `RunStage_FailedPassWhosePartialOutputCannotBeRemoved_KeepsTheStageError` | 0 |
| M17 | D7: sanitizer not pinned | 4 | `MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm`, `MicChain_SanitizerIsPinnedToTheMeasuredMicLayout`, `MixWavs_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg`, `MuxVideoMixed_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg` | 0 |
| M18 | D6: raw `KillOnCloseJob.Assign` | 1 | `RunCore_TiesItsChildOnlyThroughTieToLifetime_InTheCompiledProduct` | 0 |
| M19 | D6: exited child treated as live | 1 | `TieToLifetime_ProcessAlreadyExited_DoesNotReplaceTheRealResult` | 0 |
| M13 | early return for an exhausted mux, alone | **0 - SURVIVES** | equivalent mutant; see R2-4 for how that was established | 0 |
| N1 | R2-1: `NeedsMuxReconcile` always false | 4 | `RepairPass_CameraRecordingKilledOnTheLastMuxAttempt_...`, `RepairPass_DeathAfterTheMuxCompleted_*`(attempts 3, both), `RepairPass_HardKillOnTheLastAttempt_...` | 0 |
| N2 | R2-1: `Outstanding` ignores it | 4 | same 4 as N1 | 0 |
| N3 | R2-3: Reconcile does not publish | 5 | `Reconcile_FinalFilePresentButRawInputGone_KeepsTheFile`, all 4 `RepairPass_DeathAfterTheMuxCompleted_*` | 0 |
| N9 | R2-3: `Execute` stops after publishing | 4 | all 4 `RepairPass_DeathAfterTheMuxCompleted_*` | 0 |
| N8 | R2-3: only newly-renamed backups recorded | 4 | all 4 `RepairPass_DeathAfterTheMuxCompleted_*` | 0 |
| N4 | R2-3: missing-input check removed | 1 | `RepairPass_InputGoneWithoutABackup_KeepsTheFinalFileAndNamesTheMissingInput` | 0 |
| N5 | R2-4: `NeedsCompose` guard removed | 3 | `NeedsCompose_PendingMux_IsFalse_AndTrueOnceTheMuxIsDone`, `RepairPass_CameraRecordingKilledOnTheLastMuxAttempt_...`, `RepairPass_CameraRecordingWithAnExhaustedMux_...` | 0 |
| N7 | R2-4: M13 and the compose guard both removed | 3 | same 3 as N5 | 0 |

Totals: 27 mutations; 26 fail by name; 1 survives (M13, equivalent); 0 ffmpeg left after any run.

N7 note: under N7 `RepairPass_CameraRecordingWithAnExhaustedMux_...` fails at its discovery
assertion (the recording is listed) before the pass runs. So N7 shows the hole is caught. It does not
by itself re-observe QA's burned compose attempt. N5 and the round-two paired run cover that test's
other assertions.

Not mutated: R2-2's retry loop. The paired evidence is the 2 of 40 cases above that needed a second
bare run, plus the 8.1.1 run that fires. A single-shot mutation would fail about 1 run in 20, so a
single mutation run could not show it reliably.

## Round three: what is still not covered, and how I know

- **M13**: an equivalent mutant, established by enumeration plus three killing mutations (R2-4
  above). It is not a belief.
- **"Proven complete" trusts the order in `FinalizePending`**: mux, then `Preserve`, then the
  manifest. It does not re-verify the media (no ffprobe of a moov atom). I checked the order by
  reading `FinalizePending` and `PublishFinalized`: `Preserve` is the first statement after every
  mux branch, and the manifest update follows it. #136 must keep that order. Its `MuxInTwoPasses`
  publishes by `File.Move` inside the mux call, so the order still holds on 47b4da1. I read that
  file; nothing ran it rebased.
- **A backup that existed BEFORE the mux** (a `.original` from somewhere else, with the raw input
  also missing) would read as proof. `grep -rn "\.original" src/AgentEyes.Core --include=*.cs` finds
  two writers of a backup:
  - `OriginalBackup.Preserve`, reached from `PublishFinalized`, which runs only after the mux;
  - the CLI's direct `audio`/`video` paths in `Commands.cs` (e.g. `Commands.cs:213`). QA's round-two
    enumeration found that these write no `PendingMux`, and the manifest only after the mux.

  So no writer leaves a backup beside a still-pending mux it did not complete. `SelfTest.cs` only
  asserts backups exist, and `CameraCompose.cs` only mentions the name in a comment. This is a code
  search, not a test.
- **macOS and Linux**: `MuxCoordinatorTests` is in the Windows suite only. Its project is
  `AgentEyes.Tests`, not `tests/Shared`; I checked the three test `.csproj` compile lists. The
  predicates and `Reconcile` are platform-neutral code. CI's mac (232) and linux (274) jobs build
  them but do not run these tests.
- **Nothing in round three was run in the real app.** QA's reproduction kit is the route. Expected
  values for each defect:
  - R2-1 (QA's launch 4 after three kills): `FindUnfinished: <dir> needs mux`, then
    `Reconcile: ... interrupted: mux attempt 3`, `RemoveFragment: removed 18350128-byte fragment`,
    and `"mux":{"State":"failed","Attempts":3,"Error":"interrupted: ..."}`. The next repair tick logs
    `unfinished=0`.
  - R2-3 (QA's probe): `Reconcile: ... is a COMPLETED mux ... publishing it`, then
    `PublishFinalized`, then `"mux":{"State":"done"}`, with `recording.mp4` kept and a thumbnail made.
  - R2-4: a camera recording with an exhausted mux is not listed, and has no `compose` record.
- **Round two's other admissions stand**: the Unix lock proven by a second handle, the probabilistic
  D7 runtime tests, and the thumbnail partial-write window. I did not re-examine them this round.

## Gate (round three)

```
dotnet build AgentEyes.sln -c Release  -> Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release  -> Passed!  - Failed: 0, Passed: 2357, Skipped: 0, Total: 2357
   QA_RECORD_FFMPEG_DIR = shipped 9.0.2-essentials: 2357/0, 0 ffmpeg left
   unpinned (PATH, 8.1.1-full - confirmed by the 8.1.1 version line in the CI=true run above): 2357/0
```
2357 = 2348 + 9 new tests: R2-1 x1, R2-3 x5 (a 4-case theory plus 1), R2-4 x3. The round-two
`Reconcile_FinalFilePresentButRawInputGone_KeepsTheFile` was changed to assert the publication. Its
old assertion described the defect.

CI at code commit 3a1165d. Both runs succeeded, all three jobs each:
```
push         43642456237  windows 2357/0  linux 274/0  macos 232/0
pull_request 43642460188  windows 2357/0  linux 274/0  macos 232/0
```
The runs at the final handoff head are reported in the issue comment. That commit adds only files
under `docs/`.

## For the #136 rebase (additions to round two's list)

- **Three test `.csproj` conflicts**: `AgentEyes.Tests`, `AgentEyes.Tests.Linux` and
  `AgentEyes.Tests.Mac`. Both branches add a `tests/Shared` compile link at the same spot: this
  branch adds `MuxLockTests.cs`, #136 adds `FfmpegMuxShapeTests.cs`. **Keep both.**
- **`ManifestWriterIlTests`**: this round renamed the pinned manifest-writer entry
  `RecordingService::FinalizePending -> ManifestStore::Update` to
  `RecordingService::PublishFinalized -> ManifestStore::Update`. Keep the rename beside #136's own
  `MuxInTwoPasses` delete pins.
- **`FinalizePending` refuses a missing input before any mux call.** #136's passes run inside
  `AudioMix.MuxVideoMixed` / `ProcessVideoMic`, after that check, so it needs no change. It is also
  why #136's `!ok` delete can no longer reach R2-3's case: a mux whose raw input was preserved is
  published by `Reconcile` and never started.
- **Known, NOT mine, NOT fixed: #136's unguarded deletes.** On 47b4da1, `AudioMix.MuxInTwoPasses`
  has **four** unguarded `File.Delete` calls. Both QA seats were counting the same four sites; one
  counted the `finally` only. The Lead confirmed this on 2026-11-14.
  - Line 137: the processed temp, in the `finally`. **Can mask a stage error** (D5's pattern).
  - Line 142: the muxing temp, in the `finally`. **Can mask a stage error.**
  - Line 152: the `!ok` delete of the final output, in the `finally`. **Can mask a stage error.**
  - Line 167: `RemoveStaleTemp`, which runs BEFORE either pass. **Cannot mask one.** It can only
    fail a mux before any work starts, with its own error.

  This branch's D5 fix covers `RunStage`, so each PASS is safe, not the mux as a whole. These four
  belong to #136 after the rebase. They are named here so the stack's merge does not leave a hole
  each seat thought the other had closed.

# ROUND TWO (after QA FAIL, report `qa-report-windows.md`, defects D1-D5, plus D6-D7)

Round one (everything below the line "ROUND ONE") stands: the head sanitizer, the progress
watchdog, the child registry and the two closed retry bypasses were not rebuilt. Round two fixes
the five defects QA found, plus two the Lead added during the round (D6, the job-object race, and
D7, the sanitizer segfault on mono mic + stereo system, both found by the #136 seat). It also
merges `main` (af0ddb1, #122) into the branch, which takes the
local count from 2164 to 2318 before the new tests.

I believe this is finished. All seven defects (D1-D7) are fixed. Nothing was left unfixed; the limits of
what the tests can see are listed under "What the tests do NOT catch".

## D1 - criteria 12 and 13: one writer per output, across processes; every attempt counted

**What QA measured.** The app's mux (ffmpeg 15332) and an `agenteyes package` mux (ffmpeg
12196) both had `...\recording.mp4` as their output at 12:41:11, and `Attempts` stayed at 2.
`RecordingWorkset` is in-memory, and `Package.RunAsync` called `FinalizePending` with no
`NoteStarted`.

**What changed.**
- New `src/AgentEyes.Core/MuxLock.cs`: a lock ON DISK. An exclusive handle (`FileShare.None`) on
  `<recording>\mux.lock`, held for the whole mux. On Windows a second open is a sharing
  violation. On macOS and Linux, .NET takes an exclusive `flock` for `FileShare.None`. The OS
  drops the handle when the holder dies, however it dies, so a killed mux cannot leave a stale
  lock. The lock is re-entrant on the holding thread only. Another thread in the same process is
  refused the same way another process is. The zero-length file is left in place after release on
  purpose: deleting it reopens the Unix flock race (a waiter locks an unlinked inode while a third
  party creates a new file).
- `RecordingService.FinalizePending` takes the lock for ANY caller. A second writer is refused
  with `the audio mux for <rec> is already running in another AgentEyes process ... refusing to
  start a second writer on the same output`. No ffmpeg is started.
- New `src/AgentEyes.Core/MuxCoordinator.cs` `RunForPackage`, which `Package.RunAsync` now calls in
  place of `RefuseExhaustedMux` + `FinalizePending`. It takes the lock (refused if held), settles
  a dead attempt (D2), refuses an exhausted mux, then **`NoteStarted` before the work**, then
  `NoteDone` or `NoteFailed`. A package-path attempt that the watchdog abandons is now counted,
  like the automatic runner's.
- `PostRecording.Execute` takes the lock with `TryAcquire` before it counts anything. If another
  process holds it, the pass leaves every stage outstanding and counts nothing. It does not join
  as a second writer, and it does not burn an attempt on someone else's mux.

**How QA re-tests it** (QA's own recipe, `qa-report-windows.md` "Reproduction kit", D1 line):
rebuild `2026-11-14_130000_qa128` with the NaN-trigger command, build mutation A into the branch
app so the app's mux stalls, and run `agenteyes.exe package <dir>` while it runs.
- Expected: the CLI exits non-zero with `... already running in another AgentEyes process ...
  refusing to start a second writer ...`. `Get-CimInstance Win32_Process` shows ONE ffmpeg with
  `<dir>\recording.mp4` as its output. `Attempts` does not move for the refused CLI call. (It was
  refused, so there was no attempt.)
- Then let the app's mux fail, and run `agenteyes package <dir>` again with attempts left.
  Expected: `"mux":{"State":"running","Attempts":N+1}` while it runs, then `failed` with the stall
  error. The package attempt now counts (criterion 13).

## D2 - criterion 11: a mux ended by process death

**What QA measured.** After `Stop-Process -Force` during attempt 3, an 18,350,128-byte moov-less
`recording.mp4` stayed at the final path. The manifest stayed at `"State":"running","Attempts":3`
with no error. The thumbnail stage then ran on the fragment twice (`moov atom not found`), and a
transcribe attempt was burned.

**Does #136's two-pass mux make this structurally unreachable? No.** No answer is posted yet on
#136 or PR #140, so I read the branch (`origin/issue-136-mux-queue-deadlock`, handoff `e01c70f`).
Pass 2 still writes directly to the FINAL path (its handoff: "RunStage already removes the final
path when it is pass 2 that fails"). So:
- a death during **pass 1** leaves nothing new at the final path. That case IS removed by
  construction;
- a death during **pass 2** still leaves a fragment at the final path. Pass 2 is short (about 1 s
  measured on the Mac), so the window is narrower, but it is not closed;
- a fragment left by an **earlier** attempt (any build, including round one or today's shipped
  app) is still at the final path when the next attempt starts;
- the **"running forever with no error"** record is untouched by two passes. No process that dies
  can write its own failure.
So round two fixes D2 in full, independently of #136. That is also why it is fixed with no change
to the mux argument construction (#136's territory).

**What changed.**
- `MuxCoordinator.Reconcile(dir)` runs under the lock before any mux decision, on both the runner
  and the package path. Holding the lock PROVES that no live process owns the mux, so:
  - a `running` mux record is recorded as `failed` with `interrupted: mux attempt N (started <utc>)
    never finished - the AgentEyes process running it ended (killed, crashed or restarted) part-way
    through`. `RefuseExhaustedMux` now names that reason instead of `(no error recorded)`;
  - a file at the final path while `PendingMux` is set is removed (size logged). Exception: a death
    in the instant after the mux completed and `OriginalBackup.Preserve` renamed the raw input away,
    but before the manifest update. Then the file IS complete, so it is kept, with a loud log. The
    rule is: delete only while every raw input the mux reads is still present.
- `PostRecording.Execute`: a pending mux that is out of attempts now returns. It no longer goes on
  to the thumbnail and package stages, because there is no final media.
- `Thumbnails.NeedsThumb` and `TranscriptionBacklog.NeedsTranscription` (QA named both) return
  false while `PendingMux` is set. They are also the predicates the repair ticks use, and those
  ticks do not go through `Execute`.

**How QA re-tests it** (reproduction kit, D2 line): with the stalling directory, `Stop-Process
-Force` the app during the mux, three times, then start it again.
- Expected after the 4th start: the log shows `[MuxCoordinator] Reconcile: ... interrupted: mux
  attempt 3 ...` and `RemoveFragment: ... removed <N>-byte fragment recording.mp4`, and the
  manifest shows `"mux":{"State":"failed","Attempts":3,"Error":"interrupted: mux attempt 3 ..."}`.
  No `recording.mp4` is in the directory. There is NO `[poster thumbnail]` ffmpeg line and no
  transcribe attempt for that recording. `agenteyes package <dir>` refuses with `failed 3 time(s)
  ... Last error: interrupted: ...`.
- After a kill on attempt 1 or 2 (attempts left): the next start logs Reconcile and RemoveFragment
  first, then starts attempt N+1. The fragment is gone before the new ffmpeg starts.

## D3 - BLOCKING: arnndn's own end-of-stream NaN frame. Option (a), pad, chosen

**Measured on this machine.** NaN counts come from the raw f32 output bytes, not `astats`. The
tool is a 300 Hz tone through `arnndn=m=bd.rnnn`.

| ffmpeg | layout | samples (% 480) | NaN after arnndn | output samples |
|---|---|---|---|---|
| 9.0.2-essentials gyan.dev (shipped) | mono s16 | 480,123 (123) | **480** (frames 480000-480479) | 480,480 |
| 9.0.2 | stereo s16 | 480,123 (123) | **960** (both channels) | 480,480 |
| 9.0.2 | **mono s16, the Mac file's exact length** | 3,623,424 (384) | **480** | 3,623,520 |
| 9.0.2 | mono / stereo s16 | 3,623,040 (0) | 0 | 3,623,040 |
| 9.0.2 | mono flt / stereo flt | 480,123 (123) | 480 / 480 | 480,480 |
| 8.1.1-full gyan.dev | all of the above | | **0** | identical lengths |
| 9.0.2 | **framed** (`asetnsamples=n=480:p=1,arnndn`), mono, stereo, and the 3,623,424 length | | **0** | identical lengths |

**On the Lead's question (which ffmpeg build and channel count): CHANNEL COUNT IS NOT THE
EXPLANATION.** The gyan.dev Windows 9.0.2 build emits the NaN frame on a MONO s16 file of exactly
the macOS file's length, 3,623,424 samples. So the macOS "no NaN" result is either the build
(Homebrew arm64 vs gyan.dev x64: different build options, different SIMD path) or the instrument.
The instrument is the less likely of the two: on Windows, `astats` DID count the frame (QA's
`Number of NaNs: 480`), and my raw-byte count agrees with it. I cannot run the Homebrew arm64 build
from here, so the build explanation is the remaining candidate, not a proven one. The one-command
check on the Mac would settle it: the same `-f f32le` output, then a byte-level NaN count.

**Why (a), and why it is not impractical in the filtergraph.** ffmpeg already has the primitive.
`asetnsamples=n=480:p=1` re-frames the stream into 480-sample frames, and `p=1` pads the LAST
frame with digital silence. It needs no knowledge of the input length. It goes immediately
before arnndn (`FfmpegArgs.ArnndnFraming`, used by `MicChain`, the one builder of every
denoising chain). Measured consequences:
- **Nothing to trim.** arnndn already rounds its output up to a whole frame on both builds (the
  output-samples column above). The framed output has exactly the same length as today's.
- **Every real sample is unchanged.** On 8.1.1 the WHOLE framed output, final frame included, is
  bit-identical to the unframed output (md5 35d896381bf6 both, 1,921,920 bytes). So padding with
  zeros is exactly what the healthy build's arnndn did internally. On 9.0.2 the first 480,000
  samples are bit-identical framed vs unframed, and identical to 8.1.1 (md5 3ca065fbad60, all
  four).
- **The full default chain on QA's 90.01 s input (4,320,500 samples), 9.0.2:** unframed timed out
  at 40 s (rc 124). Framed completed in 2 s.
- **Why not (b).** (b) would zero arnndn's output and count the zeros. Counting needs a second
  instrument in the graph (astats, parsed out of stderr) and a threshold, and it handles the
  symptom after arnndn has produced it. (a) removes the condition. And because (a) adds NO
  sanitizer after arnndn, the poisoning case stays loud: if arnndn ever emits NaN for another
  reason, speechnorm still stalls, and the watchdog still fails the stage with an error instead
  of shipping silence with a `done` status.

**How QA re-tests it** (reproduction kit, "Finite s16 trigger for D3"): build the 90.01 s finite
s16 directory and run the FIXED branch app with `QA_RECORD_FFMPEG_DIR` set to the shipped 9.0.2.
- Expected: the log shows `ffmpeg [process mic audio]: ... aeval=...,asetnsamples=n=480:p=1,arnndn=...`
  then `ffmpeg [process mic audio] ok` within seconds, and the manifest shows
  `"mux":{"State":"done","Attempts":1}`. Round one failed this with `output stopped advancing at
  89.9s ... after 63s`. QA's one-line repro with `asetnsamples=n=480:p=1,` inserted before
  `arnndn` gives `Number of NaNs: 0`.

## D4 - criterion 7: the watchdog wiring, and the fixture that dodged its own condition

- `RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog` drives the PRODUCT stage runner
  (`AudioMix.RunStage`), not `Ffmpeg.RunWatched`. Its input is read in real time (`-re`) with the
  first 28 s trimmed away, so ffmpeg writes nothing for 28 s and then finishes ON ITS OWN at about
  30 s. With the watchdog (5 s, through a new AsyncLocal override `Ffmpeg.PostProcessStallTimeoutOverride`
  that cannot leak into parallel tests), the stage fails at about 5 s. Without it, the stage
  SUCCEEDS and the test fails by name. No ffmpeg is left running in either case.
- The NaN product test (`MixWavs_DefaultSettingsMicWithNanSample_...`) now uses **4,320,500
  samples (% 480 = 20)**, not 90 s. The fixture asserts its own length is not a multiple of 480.
- New D3 tests at a non-multiple length: `ArnndnFraming_PartialFinalFrame_ArnndnEmitsNoNonFiniteSample`
  (mono and stereo, raw-byte NaN count). It prints the ffmpeg version and whether THIS ffmpeg
  reproduces the bare-arnndn NaN frame. Under `CI=true` it REQUIRES the reproduction, so if the CI
  ffmpeg stops emitting the frame, the test says the D3 tests no longer exercise the defect, rather
  than passing silently. There is also `ProcessVideoMic_DefaultSettingsFiniteS16MonoNotAWholeFrame_Completes`
  (the product path, a finite s16 mono capture) and the shape test `MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames`.
- The stall test's leak under mutation B is fixed. On a timeout it kills the ffmpeg it started,
  and `dotnet test` returns. (See M15 below for the measurement.)

## D5 - the cleanup delete no longer masks the stall error

`AudioMix.RunStage` no longer deletes in a `finally`. On failure it calls
`RemovePartialOutput`. If that delete fails, it throws a `UsageException` whose message is
**the stage's own error first**, then `(and its partial output <path> could not be removed:
<reason>)`, with the original error as the inner exception. Nothing is swallowed. The fragment
that stays behind is removed by `Reconcile` before the next attempt.
Test: `RunStage_FailedPassWhosePartialOutputCannotBeRemoved_KeepsTheStageError`. It holds the
output open with no delete sharing, which is QA's exact IOException.

## D6 (added by the Lead during round two, found by the #136 seat on Windows CI): the job-object race

`Ffmpeg.RunCore` called `KillOnCloseJob.Assign` right after `Process.Start`. Assigning an
ALREADY-EXITED process to a job object fails. So an ffmpeg that failed within milliseconds
reported `could not tie ffmpeg (pid N) to AgentEyes' lifetime` instead of ffmpeg's own error.
Now `Ffmpeg.TieToLifetime` handles it:
- if the child has already exited, there is nothing left to tie, and the caller goes on to report
  ffmpeg's exit code and stderr as usual;
- if the child is still ALIVE and cannot be tied, it is killed (it must not run untied), and the
  tie error is thrown.
Test: `TieToLifetime_ProcessAlreadyExited_DoesNotReplaceTheRealResult` is deterministic. It runs
`cmd /c exit 3` to completion, first asserts that a raw `Assign` of it DOES throw (so the fixture
reproduces the hazard), then asserts `TieToLifetime` does not throw and the exit code is still 3.
`RunStage_FailedPass_RemovesThePartialOutput` now asserts the OUTCOME (the fragment is gone), not
which exception type won. The #136 seat flagged that type assertion as the same latent flake.

## D7 (added by the Lead during round two, found by the #136 seat): the sanitizer segfaults ffmpeg on mono mic + stereo system

**Measured on this machine.** 10 runs per row, the full default mixed chain through `amix`. Exit
code 139 is a segfault.

| configuration | ffmpeg 9.0.2 result |
|---|---|
| mixed, MONO mic + STEREO system, round-one sanitizer (`aeval ... c=same`) | **139 in 8 of 10** |
| mixed, mono + stereo, NO sanitizer (main's chain) | 0 in 10 of 10 |
| mixed, stereo + stereo / stereo + mono / mono + mono, round-one sanitizer | 0 in 10 of 10 each |
| mic-only (`FilterVideoMic` shape), mono mic / stereo mic, round-one sanitizer | 0 in 10 of 10 each |
| mixed mono + stereo, sanitizer + `aformat=channel_layouts=mono` after it | **0 in 10 of 10** |
| mixed mono + stereo, `aeval ... c=mono` | 0 in 10 of 10 |

8.1.1 crashes the same way (2 of 2 spot runs on the plain `aeval` + `volume` chain), so this is not
a 9.0.2 regression.

**Is it confined to mixed recordings?** Yes. It needs the mic and the system track to have
DIFFERENT layouts in one `amix`, with the mic as the mono one. Mic-only recordings did not crash
in any of 20 runs, mono or stereo. So users who never record system audio were not exposed. Users
who record a mono mic with system audio (the shipped default for a mixed recording on a mono
device) were exposed on most runs.

**Why.** `aeval ... c=same` does not hold its OUTPUT layout to its INPUT layout during format
negotiation. `amix` pulls the mic branch to stereo to match the system track, so `aeval` ends up
configured mono-in and stereo-out, and it crashes. The evidence:
- pinning `aeval`'s output to the mic's own layout (`aformat=channel_layouts=mono` right after
  it) removes the crash in 10 of 10;
- the same `aformat` placed BEFORE `aeval` (pinning only its input) still crashed.
I did not read ffmpeg's source to the faulting line. This is the measured mechanism, not a source
proof.

**What changed.** The pin needs the mic's real layout. A mic can be mono or stereo depending on
the device, and pinning to a fixed layout changes the output: pinning to stereo gave a different
md5 from main's chain. So:
- `AudioMix.Prepare` now MEASURES the channel count with `MediaProbe.AudioChannels` (ffprobe) and
  stores it in `AudioMixOptions.MicChannels`. It is re-measured on every run, like the gate
  threshold.
- `MicChain` emits `aeval ...:c=same,aformat=channel_layouts=<mono|stereo|Nc>`.
- Building a chain that contains the sanitizer with `MicChannels` unset throws, exactly as an
  unmeasured gate does. No layout is assumed.

With the pin, the mixed output is **bit-identical to main's chain** (md5 6db48d1043cb, both).

**Note for the #136 seat:** any test that builds a sanitized mic chain from hand-made options must
now set `MicChannels`. I did that for `FfmpegArgsTests.FullOpts` and `MicChainStallTests.Defaults`.
Your `FfmpegMuxShapeTests` and the retargeted `GateCalibrationTests` call sites will need the same
one-line addition on your rebase.

Tests: `MixWavs_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg` and
`MuxVideoMixed_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg` run the PRODUCT paths with a
mono mic and a stereo system track. Each runs 5 times, because unpinned the crash hits 8 of 10, so
5 runs miss it with probability about 0.03%. Each also asserts that the product path measured
`MicChannels == 1`. The shape test is `MicChain_SanitizerIsPinnedToTheMeasuredMicLayout` (mono,
stereo, 6c, and an unmeasured mic throws).

## One theme across D5, D6 and D7: the error that reaches the manifest must describe the failure

The Lead asked for this as one theme. What is now true:
- **D5:** a cleanup failure can no longer replace a stage failure. The stage's error comes first,
  the cleanup failure is appended, and the original is the inner exception.
- **D6:** a job-object tie can no longer replace ffmpeg's own error when ffmpeg exited first.
- **D2:** a stage killed by process death no longer leaves `running` with no error. The next
  owner records `interrupted: mux attempt N ... never finished`, and `RefuseExhaustedMux` names it.
- **D7:** a crash is still reported faithfully as `ffmpeg failed during ... (exit -1073741819)`.
  It was never masked; it is now prevented.

**What could NOT be made true:**
- A process killed mid-stage cannot record WHY it was killed. The next owner can only say "the
  process ended part-way through". Whether that was a user restart, a crash or a power loss is not
  knowable from the recording directory.
- When ffmpeg segfaults, the error is an exit code plus the tail of stderr. ffmpeg prints nothing
  about the fault itself.
- The automatic runner journals a failed stage through `PostRecording.Journal`, which by design
  logs and drops a journal write that itself fails (issue #152). In that case the manifest can
  still be left `running`. D2's Reconcile then reports it as `interrupted` on the next pass: it is
  no longer silent, but it is mislabelled.

## D3 addendum: the partial-frame bug also CRASHES ffmpeg, and padding prevents that too

While chasing a test that failed in 7 of 15 suite runs, I found the failure was ffmpeg itself
exiting `0xC0000005` (access violation) on the UNFRAMED arnndn count. Measured, 40 runs each:
- unframed `arnndn` on 9.0.2: **1 of 40 segfault**;
- unframed on 8.1.1: 0 of 40;
- framed on both builds: **0 of 80**.
So the 9.0.2 partial-frame defect is an out-of-bounds read. It usually yields the NaN frame and
occasionally crashes outright, and framing removes both. That strengthens option (a): it avoids
the condition, where (b) would only have cleaned the symptom on the runs that did not crash. The
D3 instrument test now counts a crash of the bare run as "this ffmpeg reproduces the defect", and
still requires the framed run to exit 0 with no NaN.

**On the Lead's later message saying "D3 (sanitize after arnndn, assert on the sanitizer's own
count)":** the earlier message the same day said the preference was still to pad. I kept (a), for
the reasons above. If (b) is wanted after all, that is a decision for the Lead, and I have not
implemented it.

## Disable each fix in turn: the failure list, by name

Method: a script applies ONE mutation to the source, runs `dotnet build AgentEyes.sln -c
Release`, runs the FULL `dotnet test AgentEyes.sln -c Release` with `QA_RECORD_FFMPEG_DIR` set to
the shipped ffmpeg 9.0.2, records every failed test by name, counts ffmpeg processes still
running, and restores the file. Run 2 is the final code. M18 and M19 were rerun after the IL test
was added. After the run, `git diff -- src tests | md5sum` was identical to the value before it
(`1e3a144e...` both), so every mutation was restored.

| # | Fix disabled | Full suite | Failed, by name | ffmpeg left running |
|---|---|---|---|---|
| M1 | D1: `FinalizePending` takes no mux lock | 1 failed / 2347 | `MuxCoordinatorTests.FinalizePending_LockHeldByAnotherProcess_RefusesWithoutStartingASecondWriter` | 0 |
| M2 | D1: the package path does not count the attempt | 2 / 2347 | `MuxCoordinatorTests.PackageRun_PendingMuxThatFails_CountsTheAttempt`, `MuxCoordinatorTests.RunForPackage_FailedAttempt_IsCountedAndJournaled` | 0 |
| M3 | D1: `Package.RunAsync` reverted to round one's two calls | 1 / 2347 | `MuxCoordinatorTests.PackageRun_PendingMuxThatFails_CountsTheAttempt` | 0 |
| M4 | D1: the runner ignores a lock held elsewhere | 1 / 2347 | `MuxCoordinatorTests.PostRecordingRun_LockHeldByAnotherProcess_LeavesTheMuxToItAndCountsNothing` | 0 |
| M16 | D1: lock file opened shareable instead of exclusive | 2 / 2347 | `MuxLockTests.TryAcquire_Free_HoldsTheFileExclusivelyUntilDisposed`, `MuxLockTests.TryAcquire_SameThreadTwice_IsReentrantAndReleasesOnTheOutermostDispose` | 0 |
| M5 | D2: a dead `running` attempt is not recorded as failed | 2 / 2347 | `MuxCoordinatorTests.PostRecordingRun_HardKillOnTheLastAttempt_RecordsInterruptedRemovesTheFragmentAndRunsNothingOnIt`, `MuxCoordinatorTests.Reconcile_FinalFilePresentButRawInputGone_KeepsTheFile` | 0 |
| M6 | D2: the fragment at the final path is not removed | 3 / 2347 | `MuxCoordinatorTests.PostRecordingRun_HardKillOnTheLastAttempt_...`, `MuxCoordinatorTests.PostRecordingRun_HardKillWithAttemptsLeft_TheRetryNeverSeesTheFragmentAndIsCounted`, `ManifestWriterIlTests.EveryFileWriteInTheProduct_IsAPinnedCallSite` | 0 |
| M14 | D2: the runner does not call Reconcile | 2 / 2347 | `MuxCoordinatorTests.PostRecordingRun_HardKillOnTheLastAttempt_...`, `MuxCoordinatorTests.PostRecordingRun_HardKillWithAttemptsLeft_...` | 0 |
| M7 | D2: `NeedsThumb` trusts a file at the final path while the mux is pending | 1 / 2347 | `MuxCoordinatorTests.NeedsThumb_PendingMuxWithAFileAtTheFinalPath_IsFalse` | 0 |
| M8 | D2: `NeedsTranscription` does the same | 1 / 2347 | `MuxCoordinatorTests.NeedsTranscription_PendingMuxWithAFileAtTheFinalPath_IsFalse` | 0 |
| M13 | D2: the runner goes on to later stages when the mux is out of attempts | **0 / 2348 - SURVIVES** | none. It is redundant with M7/M8: the predicates already refuse, so the early return is not observable. **Declared uncovered.** | 0 |
| M9 | D3: arnndn framing removed | 3 / 2347 | `MicChainStallTests.MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames`, `MicChainStallTests.MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan`, `MicChainStallTests.ProcessVideoMic_DefaultSettingsFiniteS16MonoNotAWholeFrame_Completes` | 0 |
| M10 | D4: QA's mutation F, `RunStage` calls the unwatched `Ffmpeg.Run` | 1 / 2347 | `MicChainStallTests.RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog` | 0 |
| M11 | D4: `RunStage` ignores the per-flow bound (fixed 60 s) | 1 / 2347 | `MicChainStallTests.RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog` | 0 |
| M15 | QA's mutation B, `RunWatched` with no stall guard (checks the leak fix) | 2 / 2347 | `MicChainStallTests.RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog`, `MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound` | **0. The suite returned in 125 s; round one leaked and hung here** |
| M12 | D5: a cleanup failure replaces the stage error again | 1 / 2347 | `MicChainStallTests.RunStage_FailedPassWhosePartialOutputCannotBeRemoved_KeepsTheStageError` | 0 |
| M17 | D7: sanitizer output not pinned to the mic layout | 4 / 2347 | `MicChainStallTests.MixWavs_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg`, `MicChainStallTests.MuxVideoMixed_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg`, `MicChainStallTests.MicChain_SanitizerIsPinnedToTheMeasuredMicLayout`, `MicChainStallTests.MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm` | 0 |
| M18 | D6: `RunCore` calls a raw `KillOnCloseJob.Assign` again | 1 / 2348 (it SURVIVED run 2. Fixed by adding the IL test, then rerun) | `MicChainStallTests.RunCore_TiesItsChildOnlyThroughTieToLifetime_InTheCompiledProduct` | 0 |
| M19 | D6: an already-exited child is treated like a live one | 1 / 2348 | `MicChainStallTests.TieToLifetime_ProcessAlreadyExited_DoesNotReplaceTheRealResult` | 0 |

Run 1 (before the D6/D7 work) is what found M4 and M16 surviving. M4's test passed for the wrong
reason: the pass blew up, which also runs nothing. It now asserts the outcome had no error. M16's
probe used a strict open, which any handle refuses; it now uses the most permissive open. Run 1
also showed a D3 test failing intermittently. That turned out to be ffmpeg 9.0.2's unframed arnndn
segfaulting (the D3 addendum above), and the test now treats that as the defect reproducing.

## What the tests do NOT catch

- **M13** (above): SUPERSEDED in round three. This declaration was wrong - it missed `NeedsCompose`
  (QA R2-4). See ROUND THREE, R2-4, for the corrected statement and how it was established.
- **The cross-process lock on macOS and Linux.** `MuxLockTests` is compiled into the macOS and
  Linux suites, and it stands in for another process with a second handle. A second handle is
  refused by `flock` exactly like another process, so that run proves the flock mechanism. The
  real two-process test (`MuxLock_HeldByAnotherProcess_IsRefusedUntilThatProcessDies`) is
  Windows-only. It uses `powershell.exe`. I have no Mac or Linux machine.
- **`MuxLock.IsHeldElsewhere` on Unix** treats HResult 11 (Linux EWOULDBLOCK) and 35 (macOS) as
  "held". That comes from .NET's documented mapping, which I could not observe on those platforms.
  If .NET reported a different code, the lock would THROW rather than report "busy". That fails
  loudly, not open, and CI's mac/linux runs of `MuxLockTests.TryAcquire_LockFileHeldByAnotherHandle_ReturnsNull`
  exercise exactly that path.
- **The D3 runtime tests can only fail on an ffmpeg that has the defect.** 9.0.2 has it; 8.1.1 does
  not. On 8.1.1 only the shape test (`MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames`) guards the
  framing. Under `CI=true` the instrument test REQUIRES the bare arnndn to reproduce the defect, so
  a CI ffmpeg without it fails loudly rather than testing nothing.
- **The D7 tests are probabilistic.** Unpinned, the crash hits 8 of 10 runs, so the 5-run tests
  miss it with probability about 0.03%. The shape test is deterministic.
- **`MediaProbe.AudioChannels`** is exercised only through the product-path tests (mono, measured as
  1). There is no stereo-file assertion and no file without an audio stream.
- **Thumbnail partial output.** `Thumbnails.Ensure` writes `thumb.jpg` directly with an unwatched
  `Ffmpeg.Run`. A process death mid-write could leave a partial jpg that `PathFor` then accepts.
  That is outside D2 as QA filed it, and the window is a single-frame encode. It is noted, not
  fixed.
- **D2's generality, stated plainly.** Every caller of `AudioMix.RunStage` writes the
  `PendingMux.FinalFile` of a deferred mux, and so does `downmix loopback` (the audio-only system
  path, an unwatched `Ffmpeg.Run` to `audio.wav`). `Reconcile` keys on `PendingMux` + `FinalFile`,
  so it covers ALL of them, whichever builder wrote the file. It does not cover #136's intermediate
  `audio_processed.wav`. That seat's atomic publish makes the final path unreachable by a pass-2
  death on its own path.
- **Nothing here was run in the real app.** QA's live runs (reproduction kit) are the proof for
  criteria 11, 12 and 13 on the product path. The unit tests drive `PostRecording.Run/Resume`,
  `Package.Run` and `FinalizePending` with real files and, for the lock, a real second process.

## Gate (round two)

On the branch head, which contains `main` (`git merge-base --is-ancestor origin/main HEAD` -> yes):

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded.   0 Error(s)
dotnet test  AgentEyes.sln -c Release   -> Passed!  - Failed: 0, Passed: 2348, Skipped: 0, Total: 2348
   (run twice: QA_RECORD_FFMPEG_DIR = shipped 9.0.2-essentials, and PATH = 8.1.1-full; both 2348/0)
```
2348 = 2318 (main + round one) + 30 new tests in round two.

CI, PR #133, run 43635270037 (pull_request, head 537a1bd), all three jobs SUCCESS:
```
windows  Passed!  - Failed: 0, Passed: 2348, Skipped: 0, Total: 2348 - AgentEyes.Tests.dll
linux    Passed!  - Failed: 0, Passed:  274, Skipped: 0, Total:  274 - AgentEyes.Tests.Linux.dll
macos    Passed!  - Failed: 0, Passed:  232, Skipped: 0, Total:  232 - AgentEyes.Tests.Mac.dll
```
The run before it (43634897903, head 73dd437) FAILED on macos, 1 of 232:
`MacForeignFfmpegOptionTests.NoForeignFfmpegOptionInAnyAvfoundationArgumentList`. Its fixture builds
mix options by hand without a measured mic, and D7 now refuses that. The fixture sets
`MicChannels = 1` (537a1bd). The Windows CI job runs with `CI=true`, so the D3 instrument test
required that CI's ffmpeg reproduces arnndn's partial-frame defect, and that held: the D3 tests can
fail on CI.

## For the #136 seat, on your rebase

- `MicChain` now emits `aeval...:c=same,aformat=channel_layouts=<layout>,asetnsamples=n=480:p=1,arnndn=...`.
  Any test asserting adjacency of the sanitizer to the next stage will need the two new stages.
- Any hand-built `AudioMixOptions` that reaches a sanitized chain needs `MicChannels`, as in
  `FfmpegArgsTests.FullOpts` and the macOS guard above. `AudioMix.Prepare` measures it in the
  product, so your pass-1 path gets it as long as it goes through `Prepare`.
- `RunStage`'s body changed (D5: catch + `RemovePartialOutput` instead of a delete in `finally`;
  `Ffmpeg.CurrentPostProcessStallTimeout`). Its signature did not.
- `FinalizePending` takes `MuxLock` near its top. Your two-pass code runs inside it unchanged.
- `ManifestWriterIlTests` gained pins for `MuxLock::TryAcquire` (FileStream), `MuxCoordinator::RemoveFragment`
  (Delete), and the moved `AudioMix::RemovePartialOutput` (Delete).

# ROUND ONE

## The answer first: Windows exposure

**A Windows recording with default mic settings does NOT hang in post-processing today. The stall
is not ffmpeg-version-specific: it is INPUT-specific, and the Windows ffmpeg hangs exactly the same
way when it is given the trigger.**

| What | ffmpeg | Result |
|---|---|---|
| Shipped app v1.11.11 (commit 01ada18; MicChain / AudioMix / Ffmpeg.Run identical to main df81375 on this path), Control API `/record/start {mode:video, source:mic, mic:"Microphone (2- Realtek(R) Audio)"}`, no denoise/level fields = defaults on, **622.9 s** | bundled `9.0.2-essentials_build-www.gyan.dev` - the SAME version as the macOS measurement | **Completed.** `process mic audio` ran `[0:a]arnndn=m='...bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];[m]alimiter=limit=0.95:level=false[a]`, start 11:14:21.535, `ok` 11:14:47.903 = **26.4 s**. ffprobe: video 622.900 s, audio 622.033 s. |
| Same chain, direct, 180 s real speech | 9.0.2 | completes, 6 s |
| Same chain, 720 s real speech (48 kHz mono) | 9.0.2 | completes, 17 s |
| Same chain, 600 s digital silence / pink noise / -80 dB tone | 9.0.2 | all complete |
| `speechnorm=e=4:p=0.95` on a tone with **ONE NaN sample** at 30 s | 9.0.2 and 8.1.1 (full build) | **HANGS forever** (killed at 40 s); output frozen at the sample before the NaN; `-progress` keeps printing with `out_time_us=29930667` frozen |
| Same with +Inf instead of NaN | 9.0.2 | completes |
| arnndn / alimiter / volume alone on NaN input | 9.0.2 | complete |
| arnndn on a tone with ONE NaN sample | 9.0.2 | completes, but **every sample after the NaN is NaN** (2,880,000 of 4,320,000) - its recurrent state is poisoned |

Shipped-run manifest, final `PostProcessing` block (recording `2026-11-14_110358_video`):

```
"mux":       { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:21.5147713Z" }
"thumbnail": { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:48.3445892Z" }
"package":   { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:58.7797243Z" }
"plugins":   { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:58.7980342Z" }
```

Caveat on that run: the room was quiet (mic mean -87.3 dB raw, -79.5 dB processed), so it proves
the shipped path completes on a 10-minute Windows capture, not much about speech.

**Why the macOS bisect looks the way it does** (consistent with all of the above, NOT verified on a
Mac - I have no macOS capture): arnndn alone is "fine" because it emits NaN harmlessly; speechnorm
alone is fine because the raw capture is finite; together, one NaN reaching arnndn (or produced by
it) becomes NaN-forever, and speechnorm spins on the first one. Windows dshow captures s16 PCM,
which cannot carry a NaN - which is why Windows is not exposed. The macOS seat can confirm the
trigger in one command on the failing `raw.mp4`:

```
ffmpeg -i raw.mp4 -af "aformat=sample_fmts=flt,astats=measure_perchannel=none" -f null - 2>&1 | grep "Number of NaNs"
ffmpeg -i raw.mp4 -af "arnndn=m=<model>,astats=measure_perchannel=none" -f null - 2>&1 | grep "Number of NaNs"
```

Non-zero on the first = the capture carries NaN. Zero on the first and non-zero on the second =
arnndn produces it on that platform (then the fix below converts the hang into a reported failure,
not into silence - see "What the fix does NOT do").

Severity: **latent on Windows** (same ffmpeg, one NaN = an unbounded hang, nothing notices), live on
macOS.

**#113:** different failure (exit 183 on an unreadable `raw.mp4` with no moov atom). Nothing here
changes that diagnosis. The one bearing: the `process mic audio` stage that failed there is now run
under the stall watchdog, so it can no longer hang unbounded either.

## What changed

1. `src/AgentEyes.Core/Video/FfmpegArgs.cs` - `MicChain` prepends
   `aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same` (const `FiniteSamples`) as the
   FIRST stage on the mic input whenever denoise or level is on. It must be before arnndn, because
   arnndn turns one NaN into NaN-forever. Per channel (`ch`), layout kept (`c=same`). Applies to all
   three mic paths: `FilterVideoMic`, `MixTwoWav`, `MuxVideoMixMicSystem`.
2. `src/AgentEyes.Core/Video/FfmpegProgressWatchdog.cs` (new) - pure, clock-injected decision:
   stalled when `out_time_us` has not ADVANCED for longer than the limit (progress lines that keep
   arriving with a frozen output time are NOT liveness - that is exactly the hang).
3. `src/AgentEyes.Core/Video/Ffmpeg.cs` - new `RunWatched(args, label, stallTimeout)`: adds
   `-progress pipe:1 -stats_period 1`, feeds stdout to the watchdog, and on a stall kills the process
   tree and throws `UsageException("ffmpeg stalled during <label>: output stopped advancing at X s of
   output for N s (limit L s) after T s - process killed")`. `Run` is unchanged in behaviour.
   `PostProcessStallTimeout` = 60 s. `-stats_period` needs ffmpeg 4.4 or newer; a Linux distro ffmpeg
   older than that would reject the option and fail the stage loudly (not hang).
4. `src/AgentEyes.Core/Audio/AudioMix.cs` - the four audio post-processing stages
   (`mix mic+system`, `mux video+mic+system`, `mux video+system`, `process mic audio`) use
   `RunWatched`. Sparse-output stages (scene extract, thumbnails) deliberately keep plain `Run`: a
   frozen output time is normal for them.
5. `src/AgentEyes.Core/Video/FfmpegChildRegistry.cs` (new), wired into `Ffmpeg.RunWatched` and
   `RepairService.Start`; on Windows `RunWatched` children also join `KillOnCloseJob` - criteria 9/10.
6. `AudioMix.RunStage` deletes the partial output of a pass that did not complete;
   `Package.RefuseExhaustedMux` stops packaging from re-running a mux past its ceiling - criterion 11.
7. Tests: `MicChainStallTests` (16), `FfmpegChildRegistryTests` (5); the three new file-write call
   sites are pinned in `ManifestWriterIlTests`.

**Defaults are unchanged** (denoise and level still on). **The sound is unchanged:** on 720 s of
NaN-free speech, the pre-fix chain and the fixed chain produce bit-identical output
(`-f md5` of both = `64fd8535cdfa013d240e97de0cbdff8e`) - the sanitizer is an identity on finite
audio.

**What the fix does NOT do:** if a platform's arnndn ITSELF produces NaN from finite input, the
sanitizer at the head cannot help (it sits before arnndn by necessity). In that case the run now
FAILS after 60 s with the stall error above instead of hanging - loud, not a quiet fallback. I did
not add a second sanitizer after arnndn on purpose: it would turn that case into minutes of silence
with a "done" status, which is the quiet degradation this repo forbids.

## Acceptance criteria

| # | Criterion | What I did | Status |
|---|---|---|---|
| 1 | Windows exposure, with a number | Above: 622.9 s shipped-app recording, defaults, ffmpeg 9.0.2, completed in 26.4 s, manifest block quoted | Done (Windows) |
| 2 | Linux exposure | NOT done - no Linux machine in this seat. The mechanism above says what to look for: whether the Linux capture (Pulse) can carry NaN. Same two astats commands apply. | Open for the Linux seat |
| 3 | macOS completes | NOT done - no Mac here. The fix is shared code; QA on macOS must run it. | Open for the macOS seat |
| 4 | Same on Windows / Linux | Windows: see "Branch run" below. Linux: open. | Windows done |
| 5 | Audio still processed | See "Processing still happens" below | Done (Windows) |
| 6 | Stall now fails, within a bound | Injected the pre-fix chain (mutation A below) into the real `AudioMix.MixWavs` path with a NaN-bearing mic: failed after 61 s with `ffmpeg stalled during mix mic+system: output stopped advancing at 30.0s of output for 60s (limit 60s) after 61s - process killed`. Unit-level: a 10 s limit fails well inside the test's 60 s wait. Bound = 60 s of frozen output + up to ~1.5 s (1 s progress period + 0.5 s poll). | Done |
| 7 | Regression tests | 21 tests, both kinds, including the stall itself in ~12 s. Mutation evidence below. | Done |
| 8 | Gates | Below | Windows done; Unix via CI |
| 9 | No orphan survives the stage being abandoned | Watchdog kills the process tree; product run below shows the pid gone after the failure | Done (Windows) |
| 10 | No orphan survives a service restart | Windows: post-processing children now in the kill-on-close job, hard restart during a live stall, ps before/after below. All platforms: pid registry reaped at start (real-process test) | Done (Windows); the registry kill path is unverified on macOS/Linux hardware |
| 11 | Retries bounded and visible | Existing bound of 3, plus two bypasses found and closed; product run to the bound below | Done (Windows) |

### Branch run (criterion 4, Windows)

Branch CLI `agenteyes video --screen 1 --mic "Microphone (2- Realtek(R) Audio)" --seconds 620`
(defaults: denoise + level, gate off), ffmpeg pinned to the shipped 9.0.2 via `QA_RECORD_FFMPEG_DIR`.
The CLI runs the same `AudioMix.ProcessVideoMic` -> `Ffmpeg.RunWatched` path as the app.

- Log: `ffmpeg [process mic audio]: ... "[0:a]aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same,arnndn=m='...bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];[m]alimiter=limit=0.95:level=false[a]" ...`
  at 11:43:35.673, `ok` at 11:43:52.164 = **16.5 s** for 10m22s.
- CLI: `[ok] recording.mp4 (10m22s, 23.7 MB)`.
- ffprobe recording.mp4: video **622.100 s**, audio **621.318 s** (0.78 s apart; the bound is 3.0 s).
- Quiet room again (mean -90.3 dB raw, -80.3 dB processed), so criterion 5 is shown on speech below.

### Criteria 9, 10, 11 - product runs on Windows

Setup: a recording directory crafted in `Videos\AgentEyes` whose `raw.mp4` holds 90 s of video plus
float-PCM mic audio with ONE NaN sample at 30 s (the trigger), and a `PendingMux` with the shipped
defaults. The branch app was built with the pre-fix chain (mutation A - the "ready-made stall" the
issue names) and launched `--tray` with ffmpeg pinned to 9.0.2. The installed app was stopped for
the duration (it has no watchdog and would have hung on that directory), then relaunched; the
crafted directory was removed. Log extract: `stall-run-log.txt`; final manifest:
`stall-recording-manifest.json` (both in this folder).

**The retry policy before this change:** the mux is already bounded - `PostRecordingState.MaxMuxAttempts = 3`,
counted at START (`PostRecording.cs:363-368`), with `PostRecordingPlan.NeedsMux` stopping at 3. Every
attempt on a Mac hung forever, so a restart (or the 15-minute repair tick) is what started the next
one. That's the "Attempts 2 within seconds" the Mac QA saw. This run found two bypasses, now closed:
1. A killed/failed mux left a truncated `recording.mp4` (no moov atom) at the FINAL path. Later
   stages test for that file, so after attempt 3 the thumbnail stage ran on it (`moov atom not found`)
   and the package stage ran too.
2. `Package.RunAsync` calls `RecordingService.FinalizePending` itself - an UNCOUNTED mux - so the
   package stage started a 4th mux (`finalize: begin mux video src=mic` at 11:54:53, Attempts still 3).

**Orphans and Windows (the KillOnCloseJob question):** on main, `KillOnCloseJob` is used ONLY by
`AlwaysOn/ContinuousRecorder.cs:174`. `Ffmpeg.Run` children - every post-processing ffmpeg - were NOT
in it on Windows either, so a Windows mux child would also have survived a crashed app (Windows does
not kill children with their parent). On macOS/Linux `KillOnCloseJob` is a stub that throws
`NotOnMac`/`NotOnLinux`. So no platform tied post-processing children to the app. That is the
mechanism behind the Mac orphans (with the infinite stall supplying a long-lived child), and it
belongs on #119's gate list: the job object is Windows-only by construction, and the registry in
this change is the cross-platform replacement for post-processing.

Criterion 10 - hard restart (force-kill) during a live stall:

```
--- BEFORE restart, 11:49:13.082
 23028 ppid  19820 AgentEyesApp.exe cpu     2.2s  "...\bin\x64\Release\...\AgentEyesApp.exe" --tray
 21268 ppid  23028 ffmpeg.exe       cpu    10.5s  "...\ffmpeg.exe" -hide_banner -progress pipe:1 -stats_period 1 -y -i ...\2026-11-14_120000_st...
record: 21268.json {"Pid":21268,"StartUtc":"2026-11-14T16:49:03.9072245Z","OwnerPid":23028,"OwnerStartUtc":"2026-11-14T16:48:42.0471445Z","Label":"process mic audio"}

Stop-Process -Id 23028 -Force

--- AFTER force-kill of the app, 11:49:25.344
no ffmpeg.exe or AgentEyesApp.exe process exists
pid 21268 alive? False
(positive listing, so the query works: 7600 AggregatorHost, 19084 AnalyticsSrv, 3964 AppActions, ...)
record left behind: 21268.json
```

Next start, before any repair pass:
`11:49:37.722 [FfmpegChildRegistry] ReapOrphans: pid 21268 [process mic audio]: already exited (or pid reused) - record removed`.
On Windows the job object got there first. On macOS/Linux the same record would be found with the
pid alive and its start time matching, and killed. `ReapOrphans_RealOrphanedProcess_IsKilled` proves
that kill path with a real process, but not on a Mac or Linux box.

Criterion 9 - the stage abandoned by the watchdog:

```
11:49:57.965 [FfmpegChildRegistry] Register: pid 14140 [process mic audio] owner pid 18792
11:51:00.383 [ERROR] ffmpeg [process mic audio] ffmpeg stalled during process mic audio: output stopped advancing at 30.3s of output for 60s (limit 60s) after 62s - process killed. ...time=00:00:30.26 ... elapsed=0:00:57.89
11:51:00.407 [FfmpegChildRegistry] Unregister: pid 14140
--- after the watchdog fired, 11:51:11
 18792 ppid  14344 AgentEyesApp.exe
pid 14140 alive? False
```

Criterion 11 - run to the bound on the final code (one app launch per attempt, each force-killed
after its attempt failed, then a 4th launch):

```
launch 1 pid 4064  at 12:01:50 -> 12:03:17 mux attempts=1 state=failed; recording.mp4 exists=False; ffmpeg running=False
launch 2 pid 11648 at 12:03:18 -> 12:04:46 mux attempts=2 state=failed; recording.mp4 exists=False; ffmpeg running=False
launch 3 pid 18628 at 12:04:47 -> 12:06:14 mux attempts=3 state=failed; recording.mp4 exists=False; ffmpeg running=False
launch 4 pid 2728  at 12:06:15, waited 60 s:
  12:06:35.877 [PostRecordingPlan] FindUnfinished: root=C:\Users\<user>\Videos\AgentEyes, unfinished=0
  --- 12:07:15 process listing (ffmpeg + AgentEyesApp):
    2728 ppid  22068 AgentEyesApp.exe
  recording.mp4 exists=False
(each attempt also logged e.g. 12:03:14.250 [AudioMix] RunStage: process mic audio did not complete - removed partial output ...\recording.mp4)
```

Final `PostProcessing` block:

```
"mux": { "State": "failed", "Attempts": 3, "LastAttemptUtc": "2026-11-14T17:05:07.6125333Z",
         "Error": "ffmpeg stalled during process mic audio: output stopped advancing at 30.3s of output for 60s (limit 60s) after 62s - process killed" }
```

No thumbnail or package entry: with no fragment at the final path, nothing downstream mistakes the
recording for finished. Worst case per recording is now 3 x ~62 s of one core, then nothing more
automatically, with the reason in the manifest. `RefuseExhaustedMux` covers the explicit
`agenteyes package <dir>` path, which would otherwise still run an uncounted mux.

Not changed, flagged: the manifest is the only place this failure is visible. Nothing surfaces it in
the UI or `/status`. That reporting gap is outside this issue's criteria.

### Processing still happens (criterion 5)

On 720 s of real speech (a Windows capture) with pink noise mixed in at a known level
(`anoisesrc=color=pink:amplitude=0.01`), 0.5 s window-RMS percentiles:

| File | 5th pct (floor) | 50th | 90th (speech) |
|---|---|---|---|
| noisy input | -54.7 dB | -53.5 dB | -34.5 dB |
| sanitizer + arnndn only | -71.1 dB | -65.0 dB | -34.6 dB |
| full fixed chain | -59.0 dB | -53.5 dB | -22.6 dB |

Denoise removes ~16 dB of floor with speech untouched; leveling lifts speech +12 dB while the floor
rises only ~4 dB (speech-to-floor 20 dB -> 36 dB). On the clean speech: mean_volume -39.3 -> -27.3
dB, max_volume -15.0 -> -3.2 dB.

### Mutation evidence (the tests can fail)

Mutation A - sanitizer disabled (`string finite = "";`), run when the class had 13 tests:

```
Failed AgentEyes.Tests.MicChainStallTests.MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan [1 m 2 s]
  AgentEyes.UsageException : ffmpeg stalled during mix mic+system: output stopped advancing at 30.0s of output for 60s (limit 60s) after 61s - process killed
Failed AgentEyes.Tests.MicChainStallTests.FilterVideoMic_DefaultSettings_SanitizesNonFiniteSamplesBeforeArnndn [1 ms]
Failed AgentEyes.Tests.MicChainStallTests.MicChain_AllMixPaths_CarryTheSanitizer [3 ms]
Failed AgentEyes.Tests.MicChainStallTests.MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm [< 1 ms]
Failed!  - Failed:     4, Passed:     9, Skipped:     0, Total:    13
```

Mutation B - watchdog disabled (`RunWatched` -> `RunCore(args, label, null)`):

```
Failed AgentEyes.Tests.MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound [1 m 1 s]
  RunWatched was still running after 60 s - the stall guard did not fire
Failed!  - Failed:     1, Passed:    15, Skipped:     0, Total:    16
```

Mutations C, D, E together - partial-output delete off, exhausted-mux guard a no-op, reaper never kills:

```
Failed AgentEyes.Tests.FfmpegChildRegistryTests.ReapOrphans_OwnerGoneChildAlive_KillsTheChildAndRemovesTheRecord [150 ms]
  Assert.Equal() Failure: Collections differ
Failed AgentEyes.Tests.MicChainStallTests.RefuseExhaustedMux_AttemptsUsedUp_ThrowsWithCountAndLastError [56 ms]
  Assert.Throws() Failure: No exception was thrown
Failed AgentEyes.Tests.FfmpegChildRegistryTests.ReapOrphans_RealOrphanedProcess_IsKilled [10 s]
  the orphan was still running after the reap
Failed AgentEyes.Tests.MicChainStallTests.RunStage_FailedPass_RemovesThePartialOutput [94 ms]
  the failed pass left its partial output at the final path
Failed!  - Failed:     4, Passed:    17, Skipped:     0, Total:    21
```

(The stall test waits on a worker with a 60 s bound precisely so a dead guard fails by name instead
of hanging the suite. The watchdog's own unit tests are independent of the wiring and stay green
under B, as they should.)

### Gates

Windows, final branch head. (CI's first Windows run failed this test at a 4 s limit: on the loaded 2-core runner the guard fired before ffmpeg's output time first advanced. The limit is now 10 s, and the test checks the frozen point is 20-30.1 s, so a run that never started cannot pass as the NaN stall.)

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   -> Passed!  - Failed: 0, Passed: 2164, Skipped: 0, Total: 2164
```

(2143 on main this morning + 21 new.)

The Linux and macOS test projects BUILD on Windows (0 errors). Run on Windows they show 4 and 1
failures, all POSIX-only tests that cannot pass on NTFS and that this change does not touch
(`CredentialFileTests.*Mode600*`, `MacPathsTests.MachineLocalAppData_OnMac_*`). Their real gate is
this PR's CI `linux` and `macos` jobs. I have no Linux or macOS machine.

Review: the `/code-review` pass returned an EMPTY result after one tool call. Per 6c an empty result
is a broken instrument, not a clean review, so it is not counted; I reviewed the diff by hand.

## How QA should exercise it

1. Build + test: `dotnet build AgentEyes.sln -c Release`, `dotnet test AgentEyes.sln -c Release`
   (the new tests need ffmpeg on PATH or `QA_RECORD_FFMPEG_DIR`; Windows CI provides it).
2. Windows product run: launch the branch app from
   `src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe --tray` (NOT
   `bin\Release\`), `POST http://127.0.0.1:7882/record/start {"mode":"video","source":"mic","mic":"<name from /devices>"}`
   (no denoise/level fields = defaults), stop after >= 600 s with `/record/stop`, then read the
   recording's `manifest.json` `PostProcessing.mux` (expect `done`), the log line pair
   `ffmpeg [process mic audio]: ... aeval=...` / `ffmpeg [process mic audio] ok`, and
   `ffprobe -show_entries stream=codec_type,duration recording.mp4` (audio within 3.0 s of video).
3. Stall: the quickest honest injection is mutation A above (blank the sanitizer, rebuild, run
   `--filter MixWavs_DefaultSettingsMicWithNanSample`) - expect the quoted stall error at ~61 s.
4. Orphan / restart / retry (9-11): reproduce "Criteria 9, 10, 11" above (raw.mp4 with one NaN as
   float PCM in a mov container, PendingMux with defaults, mutation-A build). Stop the installed app
   first. On macOS/Linux the path that matters is the registry: `ps` must show the reparented ffmpeg
   killed by the next start.
5. macOS / Linux (criteria 2, 3, 4): run the two astats commands on a real default-settings capture
   first, then a 10-minute default-settings recording on the branch.

Reminders: drive the app through the REST API / UIA / PrintWindow, never force-foreground with
synthesized input without warning the human; the HUD is capture-excluded, so assert it via UIA or
`/status`, not a screenshot.
