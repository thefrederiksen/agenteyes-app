# Issue #128 - QA report, ROUND TWO (Windows)

PR #133, branch `issue-128-mic-chain-stall`, head `743eb45` (code `537a1bd`). Machine: SORENLAPTOP,
Windows 11. Round one's report (`qa-report-windows.md`) is unchanged. Raw evidence for everything below
is in `qa-windows-r2/`.

## Verdict: FAIL -> flow:qa-failed

D1, D3 (the fix), D5, D6 and D7 are verified, each with a pre-fix case showing the instrument fires.
Two defects block the merge. Two more must be fixed or re-declared in the same round.

| # | Severity | Defect | Where |
|---|---|---|---|
| R2-1 | BLOCKING | D2 is not fixed on the automatic path when the LAST attempt dies. The recording stays `"State":"running","Attempts":3` with no Error, and the 18,350,128-byte fragment stays at the final path. Nothing ever reconciles it. | `PostRecordingPlan.cs:119` FindUnfinished, `Thumbnails.cs:170`, `TranscriptionBacklog.cs:89`, `PostRecording.cs:377` |
| R2-2 | BLOCKING (CI gate) | The D3 instrument test fails on a correct build in about 1 of 20 runs under `CI=true`. CI failed on it at the current head. | `MicChainStallTests.cs:494-496` |
| R2-3 | must fix | D2's "keep the completed file" branch keeps the file, then the next pass DELETES it (processing off) or strands it forever with a misleading error (defaults on). | `MuxCoordinator.cs:49, 69-82` |
| R2-4 | must fix or re-declare | M13's "no observable effect" is wrong. Without the early return, a camera recording with an exhausted mux burns a compose attempt and records a compose error that misdescribes the failure. | `PostRecording.cs:393-397`, `PostRecordingPlan.cs:65-86` |

## Gate

| What | Result |
|---|---|
| `dotnet build AgentEyes.sln -c Release` | `0 Error(s)` |
| `dotnet test AgentEyes.sln -c Release`, ffmpeg 9.0.2 (`QA_RECORD_FFMPEG_DIR` = shipped `9.0.2-essentials`) | `Passed!  - Failed: 0, Passed: 2348, Skipped: 0, Total: 2348`. 0 ffmpeg left. |
| Same, ffmpeg 8.1.1 (PATH) | `Passed!  - Failed: 0, Passed: 2348, Skipped: 0, Total: 2348` |
| CI run 43635553491 (pull_request, head 743eb45) | windows 2348/0, macos 232/0, linux 274/0. All SUCCESS. |
| CI run 43635548525 (push, SAME head 743eb45) | **windows FAILURE**, 1 of 2348: `ArnndnFraming_PartialFinalFrame_ArnndnEmitsNoNonFiniteSample(layout: "mono")`, which reported `arnndn alone: exit 0, 0 non-finite of 480480`. macos and linux SUCCESS. See R2-2. |

So CI is NOT green on every run at the current head. The developer's handoff cites run 43635270037
(537a1bd). That run is green, but the failure above came two minutes after the handoff was posted.

Note on this machine: `QA_RECORD_FFMPEG_DIR` in the default shell points at a folder that does not
exist (`...\MyQuietShadow\app`), so an unpinned run silently uses 8.1.1 from PATH. Every 9.0.2 run here
pinned the variable explicitly.

## 1. D7 - the segfault (merge blocker): VERIFIED

Instrument: a QA harness (`argdump-Program.cs.txt`) loads EACH commit's own `agenteyes.dll` and prints
the exact `MixTwoWav` argument list that commit emits. So the three graphs below are real builder
output, not hand-copied strings (`d7-builder-args-per-commit.txt`):
- round one, 1b6176f: `aeval...:c=same,arnndn,...`
- main, af0ddb1: no sanitizer
- fixed, 537a1bd: `aeval...:c=same,aformat=channel_layouts=mono,asetnsamples=n=480:p=1,arnndn,...`

Fixture: a mono s16 mic and a stereo s16 system track, 1,440,000 samples each (a whole number of
480-sample frames, so D3 does not confound the result). Shipped ffmpeg 9.0.2.

| Graph | Runs | Result |
|---|---|---|
| round one (pre-fix) | 20 | **18 x exit -1073741819 (0xC0000005, access violation)**, 2 x exit 0 |
| main | 10 | 10 x exit 0, md5 `285E4D4804FF` |
| fixed | 40 | **40 x exit 0, md5 `285E4D4804FF`** |
| round one on 8.1.1 | 10 | 8 x 0xC0000005. So it is not a 9.0.2 regression, as claimed. |

- **The crash is reproduced and the fix holds.** At a 90% pre-fix crash rate, 40 clean runs by chance is
  about 1e-40.
- **"Bit-identical to main" is TRUE on whole-frame input**, on 9.0.2 (40/40 fixed = 10/10 main) and on
  8.1.1 (5/5 = 5/5, md5 `D575381BCB2B`).
- **On a partial final frame** (1,440,123 samples) main is not even deterministic on 8.1.1: 4 of 5 runs
  give `C2F782A0C26A` and 1 of 5 gives `207C04D40A3F`. The fixed graph gives `C2F782A0C26A` 5 of 5.
  On 9.0.2 at that length the fixed graph exits 0 in 20 of 20. Main cannot be compared there, because main
  carries the D3 NaN frame.

**The tests use the shipped layout.** `MixWavs_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg`
builds `S16Wav("mic-mono.wav", 1, ...)` and a 2-channel system track. `MuxVideoMixed_...` builds
`raw-mono.mp4` with `-ac 1`. Both assert the product measured `MicChannels == 1`.

**Mutation M17** (pin removed) fails 4 tests by name, identical to the handoff:
`MixWavs_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg`,
`MuxVideoMixed_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg`,
`MicChain_SanitizerIsPinnedToTheMeasuredMicLayout`, `MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm`.

The real service run (section 4) logged `[MediaProbe] AudioChannels: ...raw.mp4 -> 1` before building the
chain, so the measurement is on the product path.

## 2. D1 - one writer across processes: VERIFIED with real processes

Both builds were run through the same instrument (`d1-race.ps1.txt`): `agenteyes package <dir>` (CLI A),
then a second `agenteyes package <dir>` (CLI B) while A's mux is live, with `Win32_Process` listings.
Each listing also prints the total process count, as proof the query sees processes at all. Both builds
had mutation A (sanitizer blanked, so the mux stalls for 60 s and the window is wide). The mutation is
applied in throwaway worktrees only.

**Pre-fix (round one, 1b6176f)**, `d1-PREFIX-r1.txt`:
```
21072 ppid  4000 ffmpeg.exe ... | output-arg: ...\2026-11-14_210000_qa128r2pre\recording.mp4
15588 ppid 18096 ffmpeg.exe ... | output-arg: ...\2026-11-14_210000_qa128r2pre\recording.mp4
manifest mux: (empty - neither package attempt counted)
CLI A: [error] The process cannot access the file '...recording.mp4' because it is being used by another process.
```
Two writers on one output, uncounted, and the real error replaced. Round one's D1 and D5, reproduced.

**Fixed (743eb45)**, `d1-FIXED-head.txt`:
```
1408 ppid 7184 ffmpeg.exe ... | output-arg: ...\2026-11-14_210100_qa128r2fix\recording.mp4   (the only one)
CLI B: [error] the audio mux for 2026-11-14_210100_qa128r2fix is already running in another AgentEyes
       process (or another task of this one) - refusing to start a second writer on the same output. ...
manifest mux while running: {"State":"running","Attempts":1,...}   <- the package attempt IS counted
manifest mux after: {"State":"failed","Attempts":1,"Error":"ffmpeg stalled during process mic audio: ..."}
```
**App plus CLI, the topology round one measured** (`d2-FIXED-head.txt`, launch 1): app 1876 muxing via
ffmpeg 23192. `agenteyes package` (pid 22356) exits with the same refusal, and `ffmpeg targeting this
recording: 1`.

**Holder death: no deadlock** (`d1-holder-death.txt`, `d1-holder-death-log.txt`):
```
holder CLI 17172 muxing (ffmpeg 17816), fragment 18350128 bytes, mux {"State":"running","Attempts":1}
taskkill /F 17172 -> 2 s later neither 17172 nor 17816 exists (job object); no "Release" line from 17172
next CLI 19756 started 15:44:45.866:
15:44:46.084 [pid 19756] [MuxLock] TryAcquire: ... (package) - acquired by pid 19756
15:44:46.086 [pid 19756] [MuxCoordinator] Reconcile: ... - interrupted: mux attempt 1 (...) never finished ...
15:44:46.120 [pid 19756] [MuxCoordinator] RemoveFragment: ... - removed 18350128-byte fragment recording.mp4 ...
mux {"State":"running","Attempts":2}, exactly one ffmpeg (17588)
```
The OS freed the lock when its holder was killed, and the next process proceeded 0.2 s after starting.

(An earlier holder-death run had a bug in my wait loop, so the holder finished before the kill. It is
kept as `...INVALID...` in the scratch area and is NOT counted.)

## 3. D2 - process death: PARTIAL. R2-1 and R2-3 are defects

### Generality, enumerated from source

`RecordingService.FinalizePending` (RecordingService.cs:990) is the ONLY writer of a
`PendingMux.FinalFile`, through five branches: `downmix loopback` (unwatched `Ffmpeg.Run`), `MixWavs`,
`MuxVideoMixed`, `MuxVideoSystemOnly` and `ProcessVideoMic`. `BuildPendingMux` (RecordingService.cs:1228)
is the only builder of a `PendingMuxInfo`. The other callers of those builders do not set `PendingMux`:
- `Commands.cs:441-449` (CLI `video`) writes its manifest only AFTER its direct mux (`Commands.cs:473`)
- `SelfTest.cs` works in a temp directory

So **keying `Reconcile` on `PendingMux.FinalFile` does cover every deferred-mux writer. The generality
claim is TRUE.** What it does not cover is when `Reconcile` runs, which is R2-1.

### The guards work

Fixed build, launch 4 after three kills (`d2-FIXED-head.txt`): `ThumbAttempts=0 TranscribeAttempts=0`,
no `[poster thumbnail]` line, no `[Package]` line. The paired pre-fix run (`d2-PREFIX-r1.txt`, same
fixture, round-one app) ran `ffmpeg [poster thumbnail]` on the fragment TWICE (`moov atom not found`),
`TranscribeAttempts=1`, and the package error read `Last error: (no error recorded)`.

Attempts-left kills reconcile correctly (`d2-FIXED-reconcile-log.txt`):
```
15:50:33.093 [pid 21516] Reconcile: ... interrupted: mux attempt 1 ...; RemoveFragment: removed 18350128-byte fragment
15:51:00.264 [pid 3196]  Reconcile: ... interrupted: mux attempt 2 ...; RemoveFragment: removed 18350128-byte fragment
```

### R2-1 (BLOCKING): the LAST attempt's death is never reconciled automatically

After the kill during attempt 3, launch 4 (pid 21624), observed for 90 s:
```
15:51:27.330 [pid 21624] [PostRecordingPlan] FindUnfinished: root=...\Videos\AgentEyes, unfinished=0
PostProcessing: {"mux":{"State":"running","Attempts":3,"LastAttemptUtc":"2026-11-14T20:51:00.3098001Z"}}
files: manifest.json(1089), mux.lock(0), raw.mp4(72476673), recording.mp4(18350128)
(no Reconcile line, no RemoveFragment line)
```
- **Expected** (handoff, "How QA re-tests it", D2): `Reconcile: ... interrupted: mux attempt 3`,
  `RemoveFragment ...`, `"mux":{"State":"failed","Attempts":3,"Error":"interrupted: ..."}`, and no
  `recording.mp4`.
- **Actual**: none of the three. This is round one's D2 verbatim ("frozen at running, Attempts 3, no
  Error"), and the fragment remains.
- **Mechanism**:
  - `Reconcile` is reached automatically only from `PostRecording.Execute` (PostRecording.cs:377), and
    `Execute` runs only for recordings that `FindUnfinished` (PostRecordingPlan.cs:119) lists.
  - Once attempts = 3, `NeedsMux` is false. Round two made `NeedsThumb` (Thumbnails.cs:170) and
    `NeedsTranscription` (TranscriptionBacklog.cs:89) false while `PendingMux` is set. `NeedsCompose` is
    false without a camera.
  - So `Outstanding()` is empty, the recording is never resumed, and the same holds on every 15-minute
    repair tick. The guard that stops the thumbnail from burning attempts also hides the recording from
    the only automatic caller of its cleanup.
- **Paired**: `agenteyes package <dir>` on the same directory DOES reconcile (`RunForPackage`): it
  recorded `"State":"failed","Attempts":3,"Error":"interrupted: mux attempt 3 ..."` and removed the
  fragment. So the fix exists but is reachable only by a person running the CLI.
- **Why the suite is green**: `PostRecordingRun_HardKillOnTheLastAttempt_...` calls
  `PostRecording.Run(dir)` directly, which skips `FindUnfinished`, the entry point a restart actually
  uses. The proof covers the wrong caller.

### R2-3: the "keep the completed file" branch does not keep it

The scenario is the death the handoff names: after `OriginalBackup.Preserve` (RecordingService.cs:1029)
renamed `raw.mp4` away, but before `m.PendingMux = null` (RecordingService.cs:1042) was written.
`Reconcile` keeps `recording.mp4` (MuxCoordinator.cs:69-75). But it also marks the attempt failed
(MuxCoordinator.cs:49) and leaves `PendingMux` set, so the next pass re-muxes from a raw input that is
gone. Measured with a temporary QA probe through `PostRecording.Run` and the REAL `FinalizePending`
(`QaR2ProbeTests.cs.txt`, output `probe-at-HEAD.txt`), on a real 235,246-byte mp4:

| Options | After the next pass | After 3 passes |
|---|---|---|
| processing off | **recording.mp4 ABSENT**. `ffmpeg failed during process mic audio (exit -2)`. `RunStage`'s `RemovePartialOutput` (AudioMix.cs:71) deleted the completed file. | ABSENT, mux failed 3/3 |
| defaults on | kept, but mux failed with `could not read the audio channel count of ...raw.mp4 (ffprobe said: '')` | kept; `PendingMux` set, `NeedsThumbnail=False`, `NeedsPackage=False` forever |

The window is narrow: a death between a rename and a manifest write. But the branch exists precisely for
it, and it either destroys the file it set out to keep or strands it with an error that describes the
wrong failure. The system-only video path has no `Prepare`, so it takes the "deleted" row whatever the
options. #136's `MuxInTwoPasses` `finally` (`if (!ok && File.Exists(outMp4)) File.Delete(outMp4)`) would
delete it the same way after the rebase.

## 4. D3 - framing: the FIX is VERIFIED; the instrument test is flaky (R2-2)

**In the real command the service ran** (fixed app pid 21592, `d3-service-log.txt`), on QA's 90.01 s
finite s16 mono input (4,320,500 samples, 4,320,500 % 480 = 20):
```
15:53:50.085 [pid 21592] ffmpeg [process mic audio]: "...ffmpeg.exe" -y -i ...\raw.mp4 -filter_complex "[0:a]aeval=exprs='...':c=same,aformat=channel_layouts=mono,asetnsamples=n=480:p=1,arnndn=m='.../bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];[m]alimiter=limit=0.95:level=false[a]" ...
15:53:52.536 [pid 21592] ffmpeg [process mic audio] ok
PostProcessing: {"mux":{"State":"done","Attempts":1}}   ffprobe: video 90.000000, audio 90.020000
```
**Paired pre-fix**, same input, round-one app (`d3-PREFIX-service-log.txt`): `[0:a]arnndn=...` then
`ffmpeg stalled during process mic audio: output stopped advancing at 89.9s of output for 60s (limit 60s)
after 63s - process killed`. The fix turns a 63 s failure into a 2.45 s success.

Direct measurement with the test's own command, 30 runs each on 9.0.2 (`d3-bare-vs-framed-30-runs.txt`):

| Run | Result |
|---|---|
| bare mono | 28 x 480 NaN, **2 x 0 NaN** |
| bare stereo | 22 x 960, 7 x 480, 1 x 0xC0000005 |
| framed mono | **30 x 0 NaN** |

**The instrument test matches the Lead's ruling in its logic.** A crash of the bare run counts as a
reproduction (`bare.nonFinite > 0 || bare.exit != 0`). The framed run must exit 0, produce samples and
contain 0 NaN. Under `CI=true` on 8.1.1 it fails as designed, so it does fire on a build without the
defect.

### R2-2 (BLOCKING as a CI gate): the reproduction requirement is single-shot

- The defect is per-run nondeterministic (an out-of-bounds read). `MicChainStallTests.cs:494-496` requires
  ONE bare run to reproduce it, so the test fails on a correct build whenever the bare run comes out clean.
- Measured: the real test under `CI=true` on 9.0.2 failed **1 of 20** (`d3-instrument-test-CI-true-20-runs.txt`,
  run 9). That is consistent with 2 of 30 clean bare mono runs.
- CI hit it at the current head: push run 43635548525, windows (`ci-43635548525-windows-push-failure.txt`).
- The Lead's ruling required that the test "passes for the right reason on a build with the bug". This
  one fails on a build with the bug about 5% of the time, and every merge in the stack needs a green CI
  run.
- Suggested shape, not mine to implement: retry the bare run until it reproduces, up to N times. At the
  measured 7% clean rate, 10 attempts all clean is about 3e-12. Keep the framed assertions as they are.

## 5. The "real error survives" theme

| Path | Status, with evidence |
|---|---|
| D5: cleanup delete in `RunStage` | COVERED. Pre-fix race: CLI A's error was the IOException. Fixed race: the stall error reached the manifest. M12 fails `RunStage_FailedPassWhosePartialOutputCannotBeRemoved_KeepsTheStageError`. |
| D6: job-object tie on an exited child | COVERED. M19 fails `TieToLifetime_ProcessAlreadyExited_DoesNotReplaceTheRealResult`. Code reviewed: the `when (p.HasExited)` filter logs the real exit; a live untied child is killed and the tie error thrown. |
| D2: a process death leaves `running` with no error | PARTIAL. Covered while attempts remain (logged `interrupted: mux attempt N`). NOT covered after the last attempt on the automatic path (R2-1). |
| D2: kept-file branch | NOT covered. Errors recorded: `exit -2` and `could not read the audio channel count`. Neither says the mux had already completed (R2-3). |
| M13 path | NOT covered. Compose records `the screen recording is missing` for what is a mux failure (R2-4). |
| `TieToLifetime` throws while ALIVE | The real tie error is thrown, and it is the right one. |
| #136's `MuxInTwoPasses` deletes (on the stacked branch, not this developer's) | Read on `origin/issue-136-mux-queue-deadlock` (47b4da1): the `finally` holds three unguarded `File.Delete` calls (processed temp, muxing temp, `!ok` final) plus one in `RemoveStaleTemp`. A throw from any `finally` delete replaces the stage error, which is D5's pattern one level up. `RunStage` makes each PASS safe, not the mux as a whole. This belongs to the Lead and #136 after the rebase. |

## 6. Mutations, and the M13 declaration

8 of the 19 were re-run (`mutations-summary.txt`, `mutations-applied.diff`). Method: one mutation, full
build, full suite on 9.0.2, failures by name, ffmpeg count, restore with `git checkout`, then prove the
restore by the md5 of `git diff -- src tests`. That md5 was `d41d8cd9...` (empty) before, after every
mutation, and at the end. The final rebuild had 0 errors.

| # | Disabled | Full suite | Failed by name (all identical to the handoff) | ffmpeg left |
|---|---|---|---|---|
| M1 | `FinalizePending` takes no lock | 1/2348 | `FinalizePending_LockHeldByAnotherProcess_RefusesWithoutStartingASecondWriter` | 0 |
| M4 | runner ignores a held lock | 1/2348 | `PostRecordingRun_LockHeldByAnotherProcess_LeavesTheMuxToItAndCountsNothing` | 0 |
| M6 | fragment not removed | 3/2348 | `EveryFileWriteInTheProduct_IsAPinnedCallSite`, `PostRecordingRun_HardKillOnTheLastAttempt_...`, `PostRecordingRun_HardKillWithAttemptsLeft_...` | 0 |
| M9 | framing removed | 3/2348 | `MixWavs_DefaultSettingsMicWithNanSample_...`, `ProcessVideoMic_DefaultSettingsFiniteS16MonoNotAWholeFrame_Completes`, `MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames` | 0 |
| M12 | cleanup error replaces stage error | 1/2348 | `RunStage_FailedPassWhosePartialOutputCannotBeRemoved_KeepsTheStageError` | 0 |
| M13 | early return for an exhausted mux | **0/2348, survives** | none | 0 |
| M17 | D7 pin removed | 4/2348 | the four in section 1 | 0 |
| M19 | exited child treated as live | 1/2348 | `TieToLifetime_ProcessAlreadyExited_DoesNotReplaceTheRealResult` | 0 |

M2, M3, M5, M7, M8, M10, M11, M14, M15, M16 and M18 were not re-run by me.

### R2-4: the M13 declaration is inaccurate

- **The handoff says** the early return "is redundant with the predicate guards, which already refuse, so
  it has no observable effect". But `Execute` consults THREE predicates after the mux. The handoff names
  two. The third, `NeedsCompose` (PostRecordingPlan.cs:65-86), has no `PendingMux` guard.
- **Measured** with a probe (`QaR2ProbeTests.cs.txt`): a camera recording (`CameraFile`, overlay
  framing) whose mux is exhausted, run through `PostRecording.Run` with the PRODUCT compose step.
- **At HEAD** (`probe-at-HEAD.txt`): `compose: (none)`.
- **Under M13** (`probe-under-M13.txt`): `compose: failed attempts=1 error=the screen recording is missing:
  ...\recording.mp4`. A compose attempt is burned, and the manifest records an error that misdescribes a
  mux failure.
- So the line is load-bearing for every camera recording, and untested. Same class as #136's stale
  admission: an honest-looking declaration that under-claims. Fix: the probe as a real test, or a
  `PendingMux` guard in `NeedsCompose` so the predicates really are sufficient. Either way, correct the
  declaration.
- Related, observed and not filed: with a camera, `NeedsCompose` keeps such a recording in
  `FindUnfinished` forever, so it is resumed on every repair tick and returns at once.

## 7. Criteria on this issue's Windows scope

| # | Status in round two |
|---|---|
| 1 | Carried from round one. No re-measurement needed: the exposure question is answered. |
| 4 | NOT re-run at 10 minutes. The chain changed (pin plus framing). The bit-identity to main on whole frames (section 1) is strong indirect evidence. A 10-minute default-settings run belongs to the next QA round. |
| 5 | Indirect, by the same bit-identity: the processed output equals main's. |
| 6, 9, 10 | Watchdog fired on the product path in every stalled run here (`output stopped advancing at 30.3s ... after 61s - process killed`). Every listing after a kill or a stall shows no ffmpeg left. |
| 7 | Mostly met. Gaps: R2-2 (a flaky gate) and R2-4 (an uncovered load-bearing line). |
| 8 | Local gate green on both ffmpeg builds. CI is NOT green on every run at the head (R2-2). |
| 11 | **FAIL: R2-1.** |
| 12, 13 | **PASS**: one writer across processes, with app plus CLI and CLI plus CLI; package attempts are counted. |

## 8. The #136 rebase list: one omission

A trial merge (`git merge-tree`, HEAD vs `origin/issue-136-mux-queue-deadlock` 47b4da1, read-only):
- `AudioMix.cs` and `FfmpegArgs.cs` auto-merge.
- Conflicts in 5 files: `ManifestWriterIlTests.cs` and `MicChainStallTests.cs`, which the handoff
  anticipates, and **the three test `.csproj` files**, which it does not. Both branches add a
  `tests/Shared` compile link at the same spot: `MuxLockTests.cs` here, `FfmpegMuxShapeTests.cs` there.
  Keep both.

Otherwise the list holds:
- #136's passes go through `Prepare` (`MuxVideoMixed` and `ProcessVideoMic` call it before
  `MuxInTwoPasses`), so `MicChannels` is measured on the product path.
- Hand-built options in `FfmpegMuxShapeTests` will need `MicChannels`, as the handoff says.

Two additions for the stack:
- `MuxLock` re-entrancy is per THREAD, so a mux step must not hop threads inside `FinalizePending`.
- `RemoveFragment`'s "inputs present means fragment" rule assumes `OriginalBackup.Preserve` runs AFTER the
  final file is published. #136 keeps that order and must keep it.

## Machine state left behind

- The installed app is running again (pid 14864, `...\AgentEyes\app\AgentEyesApp.exe --tray`). It was
  stopped for the app-based runs, as in round one.
- No ffmpeg is running (`final-machine-state.txt`).
- Every crafted recording was moved out of `Videos\AgentEyes`, which has 0 `*qa128*` directories.
- The product tree is clean (`git status`: only this report and `qa-windows-r2/` added).
- The probe test was removed from `tests/` and is kept as `qa-windows-r2/QaR2ProbeTests.cs.txt`, not
  compiled.
- The mutation-A builds lived in throwaway worktrees, which are removed.
