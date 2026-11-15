# Issue #128 - QA report, ROUND THREE (Windows)

PR #133, branch `issue-128-mic-chain-stall`, head `43aa5bb` (code `3a1165d`). Machine: SORENLAPTOP,
Windows 11. Rounds one and two (`qa-report-windows.md`, `qa-report-windows-round2.md`) are unchanged.
Raw evidence for everything below is in `qa-windows-r3/`.

## Verdict: PASS -> flow:ready-gate

R2-1, R2-2, R2-3 and R2-4 are fixed. Each is shown against a measured pre-fix state of the same
instrument. R2-1 and R2-3 were also run in the real app, through the real restart path. Round two's
verified fixes still hold. CI is green on every run at the head that ran the tests. One CI hazard is
written down below. It is outside this issue and does not block it.

One limit, stated up front: my own mutation batch was stopped by the host for low memory after 4 of 9.
M7, M8, M13 and N5 completed. My extra mutations Q1 to Q5 did not, and are not counted. Section 5 says
what covers each instead. The one point that rests on review rather than on a fired mutation is that a
regression in the instrument's FRAMED run would still fail it.

| # | Claim | Verdict | Pre-fix measured | Fixed measured |
|---|---|---|---|---|
| R2-1 | A kill on the LAST mux attempt is discovered and reconciled. A settled recording drops out. | VERIFIED, real app | round-two app: launch 4 and launch 5 both `unfinished=0`; manifest `running`, Attempts 3, no Error; 18,350,128-byte fragment stays | head app: launch 4 `needs mux`, `interrupted: mux attempt 3`, fragment removed; launch 5 `unfinished=0`, manifest unchanged |
| R2-2 | The D3 instrument retries the bare run. The assertion is unchanged. | VERIFIED | single-shot: 5 of 100 runs would have failed (the 5 cases that needed a retry) | 100 of 100 runs pass under `CI=true`, 200 of 200 cases. The framed assertions are unchanged and outside the retry (review; the mutation was stopped by the host, section 5). |
| R2-3 | A mux that completed before a death is published. A missing input with no backup is refused and the file kept. | VERIFIED, real app | round-two app: both cases stranded with `could not read the audio channel count`, `PendingMux` set, no thumbnail | head app: published (md5 unchanged, mux `done` at Attempts 1, thumbnail made); no-backup case names `raw.mp4 is missing`, keeps the file, stops at 3, drops out |
| R2-4 | `NeedsCompose` is guarded. M13 is an equivalent mutant. | VERIFIED | round two: M13 burned a compose attempt (round-two QA probe) | enumeration re-derived and complete; each guard killed by its mutation; M13 survives |

## Gate

| What | Result |
|---|---|
| `dotnet build AgentEyes.sln -c Release` | `0 Error(s)` |
| `dotnet test AgentEyes.sln -c Release --no-build`, `QA_RECORD_FFMPEG_DIR` = shipped 9.0.2-essentials | `Passed!  - Failed: 0, Passed: 2357, Skipped: 0, Total: 2357`. 0 ffmpeg left. |
| Same, `QA_RECORD_FFMPEG_DIR` = 8.1.1-full | `Passed!  - Failed: 0, Passed: 2357, Skipped: 0, Total: 2357`. 0 ffmpeg left. |

Both pinned explicitly. In the default shell this machine's `QA_RECORD_FFMPEG_DIR` points at a folder
that does not exist, as noted in round two.

A trap I hit, recorded so the next seat does not: `dotnet test tests/AgentEyes.Tests/AgentEyes.Tests.csproj
--no-build` ran the STALE `bin\Release` dll from September (`No test matches the given testcase filter`
for a test that exists). All single-test runs below target
`tests\AgentEyes.Tests\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyes.Tests.dll` directly.

## 1. R2-1 - a kill on the last attempt: VERIFIED in the real app

### The tests go through the real entry point

`git diff 89de539 3a1165d -- tests/AgentEyes.Tests/MuxCoordinatorTests.cs`: the new repair-pass tests
call `new RepairService(() => false).ResumeUnfinishedAsync(_root, CaptureSignal.Epoch)`. That is the
overload the product's `ResumeUnfinishedAsync(int)` forwards to with `RecordingPaths.Root`
(RepairService.cs:359), and it runs `PostRecordingPlan.FindUnfinished(root)` before the resume loop
(RepairService.cs:371). `RepairPass_HardKillOnTheLastAttempt_IsDiscoveredReconciledAndThenLeftAlone`
asserts discovery BEFORE the pass (`Assert.Equal(new[] { dir }, FindUnfinished(_root))`) and again
AFTER it (`Assert.Empty(FindUnfinished(_root))`). So the test can see a recording discovery misses, and
a recording that never settles.

### The behaviour, measured the way round two found it

Instrument: `r21-kill.ps1.txt`, round two's `d2-restart.ps1` kit with the CLI step removed and a fifth
launch added. Same fixture as round two (90 s, one NaN at 30 s, float PCM, `raw.mp4` 72,476,673 bytes,
the crafted manifest template). Both builds carry QA mutation A (sanitizer blanked) ONLY so the mux
stalls long enough to be killed. The mutation lives in throwaway worktrees and does not touch discovery
or reconciliation. Three `Stop-Process -Force` on the app while its mux runs, then two more launches,
75 s each.

**Pre-fix, round-two code `537a1bd`** (`r21-PREFIX.txt`):
```
launch 3, 2 s after Stop-Process -Force: fragment at final path: 18350128 bytes;
  PostProcessing: {"mux":{"State":"running","Attempts":3,...}}
launch 4: 10:04:44.386 [pid 14960] FindUnfinished: root=...\Videos\AgentEyes, unfinished=0
  PostProcessing: {"mux":{"State":"running","Attempts":3,...}}   files: ..., recording.mp4(18350128)
launch 5: 10:06:02.008 [pid 23100] FindUnfinished: root=...\Videos\AgentEyes, unfinished=0
  PostProcessing: {"mux":{"State":"running","Attempts":3,...}}   files: ..., recording.mp4(18350128)
```
Round two's R2-1, reproduced by me on two consecutive restarts.

**Fixed, head code `3a1165d`** (`r21-FIXED.txt`), same fixture:
```
launch 3, 2 s after Stop-Process -Force: fragment at final path: 18350128 bytes;
  PostProcessing: {"mux":{"State":"running","Attempts":3,...}}
launch 4 (pid 21424):
  10:09:11.689 FindUnfinished: 2026-11-15_100100_qa128r3fix needs mux
  10:09:11.689 FindUnfinished: root=...\Videos\AgentEyes, unfinished=1
  10:09:11.704 [MuxLock] TryAcquire: ... (post-recording) - acquired by pid 21424
  10:09:11.708 [MuxCoordinator] Reconcile: ... - interrupted: mux attempt 3 (started 2026-11-15T15:08:44Z) never finished ...
  10:09:11.739 [MuxCoordinator] RemoveFragment: ... - removed 18350128-byte fragment recording.mp4 ...
  10:09:11.745 [PostRecording] Execute: ... - the mux is out of attempts; there is no final media, so no later stage can run
  PostProcessing: {"mux":{"State":"failed","Attempts":3,...,"Error":"interrupted: mux attempt 3 ..."}}
  ThumbAttempts=0 TranscribeAttempts=0; files: manifest.json(1277), mux.lock(0), raw.mp4(72476673)
  ffmpeg targeting this recording: 0
launch 5 (pid 12972):
  10:10:29.113 FindUnfinished: root=...\Videos\AgentEyes, unfinished=0
  PostProcessing: identical to after launch 4; files identical
```
- **Expected** (handoff): `needs mux`, `interrupted: mux attempt 3`, the fragment removed, mux `failed`
  Attempts 3 with the error, and the next pass `unfinished=0`. **Actual**: all of them, in that order.
- **No fourth mux and no later stage**: Attempts stays 3, no `ffmpeg [` line for this recording, no
  thumbnail or transcribe attempt.
- **Settled recordings drop out**: launch 5 does not list it. The other half of the claim is a
  presence too. Launch 4's `unfinished=1` listed it, and launch 5's `unfinished=0` did not.

### Code review of the predicate

`PostRecordingPlan.NeedsMuxReconcile` (PostRecordingPlan.cs:66-77) is true only while `PendingMux` is
set AND (the mux record is `running` OR `MuxCoordinator.IsProvenComplete`). `Reconcile` turns a
`running` record into `failed` before it touches the fragment, and the publish clears `PendingMux`. So
both settled outcomes make the predicate false. The developer's N1 and N2 kill the predicate. My Q3 and
Q5 did not run (section 5). The real-app launch 5 above is the direct evidence for the settled arm.

Two edge cases, read from the code and not run. Neither blocks:
- If `RemoveFragment`'s `File.Delete` throws (a viewer holds `recording.mp4` open), the record is already
  `failed`. The recording drops out with the fragment still there, and `Execute`'s catch logs the error.
  The thumbnail and transcription guards still refuse it, so nothing runs on the fragment.
- If `CompleteInterruptedFinalize` throws, `PendingMux` stays set and the proof stays true. The publish
  is then retried on every repair tick, with no bound. No ffmpeg runs, apart from one ffprobe for the
  duration.

## 2. R2-2 - the D3 instrument: VERIFIED, 100 runs

### The assertion is unchanged

`git diff 89de539 3a1165d -- tests/AgentEyes.Tests/MicChainStallTests.cs`:
- Only the BARE run is in the loop: `do { bare = NonFiniteAfter("arnndn=...") } while (bare.nonFinite == 0
  && bare.exit == 0 && bareRuns.Count < 10)`.
- "Reproduced" has the same predicate as before: `bare.nonFinite > 0 || bare.exit != 0`. A crash counts.
- The FRAMED run happens ONCE, after the loop, with no retry. Its three assertions are byte-identical to
  round two: `framed.exit == 0`, `framed.total > 0`, `Assert.Equal(0, framed.nonFinite)`.
- The length check got stricter: it now compares the framed output with EVERY surviving bare run, not
  just the last one. When every bare run crashed it logs that, and does not skip silently.
- Under `CI=true` a bare series that never reproduces still fails the test.
- No retry exists anywhere else: no retry attribute in the test project, and no rerun step in `ci.yml`.

### Would a regression in the framed run still fail it? Yes by review; the mutation did not complete

- The retry cannot mask it, because the framed run is not in the loop. A framed run that emits a NaN or
  exits non-zero fails on its single attempt.
- M9 (framing removed from the product chain) does NOT touch this test, because the test builds its own
  framed command from the `FfmpegArgs.ArnndnFraming` constant. I queued Q1 (test side) and Q2 (the
  constant) to fire this, but the host stopped the batch for low memory before either reported (section
  5). So this point rests on review, plus the 195-of-200 first-bare-run NaN rate below. It does not rest
  on a fired mutation.

### 100 runs under CI=true on the shipped 9.0.2

`d3-instrument-CI-true-ffmpeg-9.0.2-100-runs.txt`. 100 invocations of the theory, 200 test cases.
Each case line prints every bare run.
```
200 x Passed, 0 x Failed
bare runs needed:  195 cases x 1,  4 cases x 2,  1 case x 3
framed:            200 x "exit 0, 0 of <total>"
bare crashed (0xC0000005, counted as reproducing): 4 cases
```
- **The pre-fix instrument would have failed 5 of these 100 runs.** Those are the 5 runs with a case
  that needed more than one bare run. That matches round two's 1 in 20, and the developer's 2 of 40
  cases.
- One case needed THREE bare runs (`0`, `0`, `480 non-finite`). Round two's "retry once" estimate
  would not have been enough. The limit of 10 is.
- I ran 100 rather than 40 because 20 runs cannot tell a 1-in-20 flake from a fix. At 5 failures per
  100 before, 0 in 100 after is about a 0.6% chance of coming from an unfixed test.

### What this test cannot see (unchanged from round two, still true)

On an ffmpeg without the defect (8.1.1), the instrument passes locally and fails by design under
`CI=true`. The CI job downloads `ffmpeg-release-essentials.zip`, which is a moving target. When gyan.dev
ships a release without the arnndn defect, the Windows CI job will go red on this test by design, and
that will not be a regression.

## 3. R2-3 - publish a completed mux: VERIFIED in the real app

Instrument: `r23.ps1.txt`. A 20 s finite fixture (`raw.mp4`, s16 mic) and a real completed mux of it
(`recording.mp4`, 12,515,040 bytes, md5 `D0C06F54C19E9BB55A011961754D1924`). Two recordings, each with
the crafted manifest (defaults on), `PendingMux` set, and a dead attempt (`mux running, Attempts 1`):
- `pub`: `raw.mp4` already renamed to `recording.original.mp4` (death after `Preserve`, before the
  manifest write);
- `gone`: `raw.mp4` missing and NO backup.

Three launches of 60 s each. Pre-fix used the round-two worktree app. Fixed used the UNMUTATED head build
(`src\AgentEyes.App\bin\x64\Release\...`).

**Pre-fix, `537a1bd`** (`r23-PREFIX.txt`), both recordings, every launch:
```
RemoveFragment: ... its input raw.mp4 is gone (already preserved as the original): ... The file is KEPT.
MediaProbe AudioChannels FAILED: could not read the audio channel count of ...\raw.mp4 (ffprobe said: '')
after launch 2: mux failed Attempts 3; PendingMux set: True; OriginalFiles: (empty); no thumb.jpg
launch 3: FindUnfinished unfinished=0 - stranded for good
```
Round two's "defaults on" row, reproduced in the real app, for both recordings.

**Fixed, `3a1165d`** (`r23-FIXED.txt`):
```
pub, launch 1:
  Reconcile: ... recording.mp4 is a COMPLETED mux whose manifest update was lost ...; publishing it
  [RecordingService] PublishFinalized: ... dur=20.0s originals=1
  Execute: ... reconciled a completed mux; continuing with the later stages
  ffmpeg [poster thumbnail] ... -> thumb.jpg; stage thumbnail: done
  PostProcessing: mux done Attempts 1 (unchanged), thumbnail done, package done, plugins done
  PendingMux set: False; OriginalFiles: recording.original.mp4; recording.mp4 md5 D0C06F54... (unchanged)
  launch 2 and 3: not listed again
gone, launches 1 and 2:
  RemoveFragment: ... its input raw.mp4 is gone with no .original backup: ... cannot be proven a fragment, so it is KEPT.
  FileNotFoundException: the deferred mux for ... cannot run: its input raw.mp4 is missing from ... Nothing was written and recording.mp4 was left as it is.
  mux failed Attempts 2, then 3; no ffmpeg started for it; recording.mp4 md5 unchanged
gone, launch 3: FindUnfinished unfinished=0 (bounded at 3, then dropped out)
```
- **Expected** (handoff): `COMPLETED mux ... publishing it`, then `PublishFinalized`, mux `done`, the file
  kept and a thumbnail made. **Actual**: all of them, and no attempt was spent publishing.
- **The no-backup half**: the file is kept byte-identical. The error names the input and says nothing
  was written. Each attempt is counted, so the ceiling bounds it.
- Note: the `pub` recording also went through packaging, because this machine is signed in. That ran on a
  20 s sine fixture (the title step reported `transcript is empty`). No user recording was involved.

### Review

- `IsProvenComplete` (MuxCoordinator.cs:77-83) requires the final file AND `OriginalBackup.AnyPreserved`,
  meaning a raw input is gone AND its backup exists (OriginalBackup.cs:98-106). A missing input with no
  backup returns false. The real-app `gone` recording above exercises that arm, and the developer's N4
  kills the missing-input refusal.
- `PublishFinalized` (RecordingService.cs:1074-1106) is the one tail for both paths: `Preserve`, then
  `Present`, then the manifest write. A death between the two leaves exactly what `IsProvenComplete`
  reads.
- `FinalizePending` refuses a missing input before any ffmpeg (RecordingService.cs:1013-1019), for each of
  `raw`/`sys`/`mic` that the plan names.
- The developer's stated limits stand, and I agree with them: "proven complete" trusts the order and
  does not re-probe the media, and a `.original` that existed before the mux would read as proof. On the
  second point I re-ran the developer's grep: `Preserve` and the CLI's direct paths are the only backup
  writers, and neither leaves a backup beside a pending mux it did not complete.

## 4. R2-4 - the M13 equivalent-mutant argument: VERIFIED complete

The Lead asked for this to be checked hardest, so I re-derived the enumeration myself rather than
re-running the developer's awk.

`Execute` (PostRecording.cs:351-466 at 3a1165d), read in full. M13 deletes the `return outcome;` in the
`else` branch, where `PendingMux != null` after `Reconcile` and `NeedsMux` is false. Without it, control
falls out of the `if (PendingMux != null)` block (the `using var muxLock` is disposed there either way)
and reaches, in order:

1. `PostRecordingPlan.NeedsCompose(dir)`: if true, `NoteStarted(Compose)` + `RunStage(Compose)`.
2. `PostRecordingPlan.NeedsThumbnail(dir)`: if true, `RunStage(Thumbnail)`.
3. `if (!PostRecordingPlan.NeedsPackage(dir)) return outcome;`
4. Only past 3: the `hostedWorkAllowed` check, `RunStage(Package)`, and `RunStage(Plugins)`. Plugins is
   reachable only inside `if (RunStage(... PackageStep ...) && AfterPackaging != null)`.
5. The `catch`, which only handles exceptions.

There is nothing else between the mux block and the end of the method. So the observable surface under
M13 is exactly three predicates, and the developer's list (`NeedsCompose`, `NeedsThumbnail`,
`NeedsPackage`) is complete.

Each predicate refuses while a mux is pending, BEFORE any branch that can return true:
- `NeedsCompose` (PostRecordingPlan.cs:109): `if (m.PendingMux != null) return false;`. Only
  `ComposedCamera == true` is checked before it, and that also returns false.
- `NeedsThumbnail` delegates to `Thumbnails.NeedsThumb(dir, respectClaim: false)` (PostRecordingPlan.cs:85).
  The guard is at Thumbnails.cs:170. Before it, every branch returns false.
- `NeedsPackage` delegates to `TranscriptionBacklog.NeedsTranscription` (PostRecordingPlan.cs:124). The guard
  is at TranscriptionBacklog.cs:89. Before it, every branch returns false.
- None of the three writes anything. They are reads and log lines only. So under M13 all three are false,
  the method returns at step 3, and the only difference is three extra log lines.

**Each guard is killed by the mutation named** (my runs, full suite, ffmpeg 9.0.2, section 5): N5 (compose
guard), M7 (thumbnail guard), M8 (transcription guard). M13 alone survives.

**What the argument does NOT cover**, which I add to the developer's own limit (a fourth unguarded stage):
the predicates re-read the manifest after the lock is released. So under M13 a DIFFERENT process that
published the mux in that window could make a predicate true. That is a cross-process interleaving, not
a sequential behaviour, and if it happened, running the later stages on a freshly published file would be
correct anyway. It does not make M13 observable in any way the suite could pin, and I do not count it
against the claim.

## 5. Mutations (QA's own runs)

Method: the developer's harness (`dev-r3/mutate.py.txt`) with the same anchors, plus five QA mutations,
saved as `qa-windows-r3/mutate-qa.py.txt`. One mutation at a time: apply (each anchor must match once),
full `dotnet build`, FULL `dotnet test` on shipped 9.0.2, failures by name, ffmpeg count, restore with
`git checkout`, and prove the restore with `git diff --quiet -- src tests`. Raw output:
`qa-windows-r3/mutations-qa-r3.txt`.

| # | Fix disabled | Full suite | Failed, by name | ffmpeg left | Restore |
|---|---|---|---|---|---|
| M7 | `NeedsThumb` guard removed | Failed! | `NeedsThumb_PendingMuxWithAFileAtTheFinalPath_IsFalse` | 0 | diff empty |
| M8 | `NeedsTranscription` guard removed | Failed! | `NeedsTranscription_PendingMuxWithAFileAtTheFinalPath_IsFalse` | 0 | diff empty |
| M13 | early return for an exhausted mux, alone | **Passed! - survives** | none: an equivalent mutant (section 4) | 0 | diff empty |
| N5 | `NeedsCompose` guard removed | Failed! | `NeedsCompose_PendingMux_IsFalse_AndTrueOnceTheMuxIsDone`, `RepairPass_CameraRecordingKilledOnTheLastMuxAttempt_ReconcilesAndBurnsNoCompose`, `RepairPass_CameraRecordingWithAnExhaustedMux_BurnsNoComposeAndIsNotRediscovered` | 0 | diff empty |

All four match the developer's table by name. The baseline diff was empty before the run.

**The batch did not finish.** The host stopped the run while Q1 was in progress, because the machine was
low on memory, the same thing that happened to the developer's sweep. So Q1 to Q5 have NO result, and I
do not count them:
- Q1: the instrument test's FRAMED command loses its framing (test side)
- Q2: `FfmpegArgs.ArnndnFraming` becomes `anull` (product side)
- Q3: a `failed` mux record still counts as needing reconcile
- Q4: a missing input counts as proof of completion with no backup
- Q5: `NeedsMuxReconcile` loses its proven-complete arm

The stop left Q1 applied to `tests/AgentEyes.Tests/MicChainStallTests.cs`. I restored it with
`git checkout`, and `git diff --quiet -- src tests` was then empty. I rebuilt the solution from the
committed code (0 errors), so the binaries match the source, and shut down the MSBuild nodes. The host's
note asked that the batch not be restarted without being asked, so I did not restart it.

What covers each, in place of the missing runs:
- Q3, Q4 and Q5 are covered by the developer's N1, N3 and N4 (26 of 27 fail by name, from one state), and
  by my real-app runs. Launch 5 in section 1 shows the settled-record arm. The `gone` recording in
  section 3 shows the no-backup arm. The `pub` recording in section 3 shows the proven-complete arm,
  through discovery at Attempts 1.
- Q1 and Q2, "would a framed regression still fail the instrument?", are answered by REVIEW only (section
  2). The framed run is outside the retry loop and runs once. `Assert.Equal(0, framed.nonFinite)` is
  unchanged. An unframed run emitted NaN in 195 of the 200 first bare runs above, so an unframed
  "framed" run would fail almost every time. That is an argument, not a fired mutation, and I am saying
  so.

## 6. Round two's fixes still hold (spot-check, not re-run in full)

- **Unchanged code**: `git diff --stat 537a1bd 3a1165d` over `MuxLock.cs`, `Package.cs`, `Audio/AudioMix.cs`,
  `Video/FfmpegArgs.cs`, `Video/Ffmpeg.cs`, `Thumbnails.cs` and `TranscriptionBacklog.cs` is EMPTY. D1's lock,
  D3's framing, D5, D6 and D7 live there. `MuxCoordinator.Reconcile` only gained the R2-3 branch in front
  of round two's code.
- **D1 on the product path**: the R2-1 fixed run logged `[MuxLock] TryAcquire: ... (post-recording) -
  acquired by pid 17620`, then `(finalize) - re-entered, depth 2`, then `Release` in each pass.
- **D3 in the real command**: the same run's service command was `[0:a]asetnsamples=n=480:p=1,arnndn=...`
  (the sanitizer is missing only because of QA mutation A).
- **D2's guards**: in every R2-1 and R2-3 run, no thumbnail or transcribe attempt was ever made on a
  fragment (`ThumbAttempts=0 TranscribeAttempts=0`).
- **Their tests** are in the 2357 that pass on both ffmpeg builds, and M7/M8 re-ran in section 5.

## 7. CI

All counts were read from the job logs (`gh api .../actions/jobs/<id>/logs`), not from the tick.

| Run | Commit | Event / attempt | windows | linux | macos |
|---|---|---|---|---|---|
| 43642456237 | 3a1165d | push | **2357/0** | **274/0** | **232/0** |
| 43642460188 | 3a1165d | pull_request | **2357/0** | **274/0** | **232/0** |
| 43725937052 | 43aa5bb | push, attempt 1 | **2357/0** | **274/0** | **232/0** |
| 43725937052 | 43aa5bb | push, attempt 2 | **FAILURE in `Download ffmpeg`**: Build and Test `skipped` | 274/0 | 232/0 |
| 43725937052 | 43aa5bb | push, attempt 3 | **2357/0** | **274/0** | **232/0** |
| 43725941015 | 43aa5bb | pull_request | **2357/0** | **274/0** | **232/0** |

So at the head, Windows ran the suite 3 times, 2357/0 each time, with no D3 flake. Linux and macOS are
green on every run. Every count in bold was read from the job log by me. With the code commit, Windows
ran this code 5 times, 2357/0 every time.

### The CI hazard the developer flagged (item 4 of my brief)

- **The download failure itself FAILS CLOSED.** In attempt 2 the step `Download ffmpeg (essentials build)`
  is `failure`, `Build AgentEyes.sln` and `Test AgentEyes.sln` are `skipped`, the job is `failure`, and the
  attempt's run conclusion is `failure` (red). A run whose tests never ran because of the download is
  therefore NOT green. Missing ffmpeg also cannot make tests pass quietly: `FfmpegLocator.Find` has no
  skip path, and no test in `tests/AgentEyes.Tests` skips.
- **But at a glance, the run list shows only the LATEST attempt.** `gh run list` and the PR's checks show
  43725937052 as `success` (attempt 3). Attempt 2's red is visible only per attempt. Here that is harmless,
  because attempt 3 really ran the tests. It does mean "the run is green" says nothing about how many
  attempts it took.
- **The real fail-open gate is different, and I am writing it down as asked.** CI asserts no test COUNT.
  A `dotnet test` invocation that runs ZERO tests exits 0. Measured here: `dotnet test <dll> --filter
  "FullyQualifiedName=No.Such.Test"` prints `No test matches the given testcase filter` and exits 0. So
  any change that makes discovery find nothing would show a green job with the tests never run. Examples:
  a filter, a broken adapter reference, or a project the solution stops building. The only way to tell is
  to read the `Passed!  - ... Total: N` line in the log, which is what I did above. Suggested fix, not mine
  to make: fail the job unless the log shows `Total:` at or above a floor, or unless a TRX result file
  lists at least N tests. This belongs on #132's list, not on #128.

## 8. Criteria on this issue's Windows scope

| # | Status after round three |
|---|---|
| 1, 5 | Carried from round one. Nothing in round three touches the chain (FfmpegArgs.cs is unchanged since round two). |
| 4 | Not re-run at 10 minutes this round. Round two's bit-identity to main on whole frames still applies, and FfmpegArgs.cs is unchanged since then. |
| 6, 9, 10 | Watchdog and kill paths unchanged. Every listing after a kill in section 1 shows no ffmpeg left. |
| 7 | MET. R2-2's flake is gone (0 of 100), and R2-4's uncovered line is now a demonstrated equivalent mutant. |
| 8 | MET. Local gate green on both ffmpeg builds. CI green on every run at the head that ran the tests. |
| 11 | **MET: R2-1.** A kill on the last attempt is reconciled on restart: `failed`, Attempts 3, the error says why, the fragment is removed, and it is not revisited. |
| 12, 13 | MET in round two. The lock code is unchanged. |

## Machine state left behind

- The installed app is running again (pid 16936, `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray`).
  It was stopped (pid 14864) for the app-based runs, as in rounds one and two.
- No ffmpeg, `agenteyes.exe` or MSBuild node is running (281 processes visible to the query).
- `Videos\AgentEyes` has 0 `*qa128*` directories. The crafted recordings were moved to QA scratch.
- Both throwaway worktrees (round-two code + mutation A, head + mutation A) are removed. `git worktree
  list` shows only the main checkout.
- The product tree is clean (`git diff -- src tests` empty). Only this report and `qa-windows-r3/` are
  added.
- Transcripts in `qa-windows-r3/` have the local QA scratch path replaced with `<qa-scratch>`.
