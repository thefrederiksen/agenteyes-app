# Issue #113 - QA report: the capture watchdog (PR #137)

**Verdict: FAIL - criterion 1. Returned to the Developer as `flow:qa-failed`.**

The watchdog works. It detects a frozen capture and a dead one, the liveness signal is real, the
10-minute healthy run was not falsely ended, and every file left by a fault plays. But criterion 1
requires that, within 60 seconds of a SIGSTOP, `/status` reports not-recording **and names the
fault**. On this build the fault is named only after the stop has finished, which is a fixed
~13.5 s after the verdict. That puts it at 59.4-60.2 s after the freeze. I measured it **over 60 s**
(60.17 s) on one of three SIGSTOP runs. The developer's own AC1 evidence is over 60 s too (60.33 s).
By construction the worst case is about 61 s. See "The defect".

QA seat on `devlinux`, 2026-11-14, Linux only (x11grab 1920x1080 at 30 fps + PulseAudio mic
`Family 17h ... Analog Stereo`), driven only through `127.0.0.1:7882`.

## Which build answered

| | |
|---|---|
| Branch | `issue-113-capture-watchdog` at `c3eabd821288b1b1e4293a392bc9ddb382ada12a` (= `origin`, clean worktree `/home/soren/ReposFred/agenteyes-app-113`) |
| Before | service pid 2624298, `~/.local/lib/agenteyes/agenteyes.dll` sha256 `79979fd7...319b03c` (main `af0ddb1`; `/status` had no `Capture*` fields) |
| Installed by QA | `bash scripts/linux/install.sh` from the branch worktree, 12:38 EST |
| Under test | service **pid 2657099**, started Sat Nov 14 12:38:19 2026, `/proc/2657099/maps` maps `~/.local/lib/agenteyes/agenteyes.dll`, sha256 **`4087240e290af3c6dcc88f1b85f614e30f0745adf50aa60a4e349b58a548ef50`**, which contains the `CaptureWatchdog` type |
| Every log line quoted below | carries `[pid 2657099]` |

Local log times are EST (UTC-5). `/status` times are UTC.

## Criteria: Expected vs Actual

| # | Expected | Actual | Result |
|---|---|---|---|
| 1 | SIGSTOP: within 60 s `/status` is not `recording` **and names the fault** | Leaves `recording` at 46.05-46.54 s, but at that moment says only `State=finalizing CaptureLiveness=faulted LastCaptureFault=null`. The fault is first named at **59.39 / 59.49 / 60.17 s**. One of three runs is over 60 s | **FAIL** |
| 2 | SIGKILL: within 60 s not `recording`, fault named | Not `recording` at 1.82 s; `LastCaptureFault="capture exited: ... (code 137) ..."` at 2.13 s | PASS |
| 3 | A readable liveness signal that advances while healthy and stops after the stall | `CaptureBytes` 204,407 -> 476,495 -> **476,495**; `CaptureLastGrowthUtc` 17:39:11.419 -> 17:39:21.422 -> **17:39:21.422**; `CaptureLiveness` growing, growing, not-growing. Read from the output file's size every 2 s | PASS |
| 4 | 10-minute healthy run ends on its own, `recording` throughout | 59 / 59 polls over 601 s all `State=recording CaptureLiveness=growing`, bytes strictly increasing (203,051 -> 14,135,602); stop 601.23 s, `LastStopFailed=false`, `LastCaptureFault=null`; final file 602.87 s, 18,084 frames, no `CaptureFault` | PASS |
| 5 | After both faults, ffprobe reads a real duration and frame count | SIGSTOP: 18.73 s / 560 video frames (x2), 20.73 s / 620. SIGKILL: 18.73 s / 560. No `moov atom not found`. Paired case: plain MP4 killed the same way gives `moov atom not found` | PASS |
| 6 | Manifest records the fault and its UTC detection time | `CaptureFault`, `CaptureFaultDetail`, `CaptureFaultDetectedUtc`, `CaptureFaultElapsedSeconds`, `CapturedSeconds` present in all four (quoted below) | PASS |
| 7 | Detection logged at detection time, with elapsed seconds | `12:40:07.433 [pid 2657099] [ERROR] [RecordingService] CAPTURE FAULT at elapsed=66.0s (detected 2026-11-14T17:40:07.431Z) ...`, before the stop starts at 12:40:07.435 and 13.6 s before the stop ends | PASS |
| 8 | Healthy recordings on Windows and Linux are not ended; argument tests pass and are named | Linux: AC4 at runtime. Windows: CI `windows` job `dotnet test AgentEyes.sln` 2322 / 0 on `c3eabd8`. Files: `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs`, `tests/AgentEyes.Tests.Mac/MacFfmpegArgsTests.cs`, `tests/AgentEyes.Tests/FfmpegArgsTests.cs`, `tests/Shared/CaptureWatchdogTests.cs` (fake clock + fake size source) | PASS (Windows at CI level only, not at runtime) |
| 9 | mac + linux builds 0 errors; both suites `Failed: 0`; Windows sln build + test | linux build and mac build: `Build succeeded.` `0 Error(s)` (1 pre-existing warning, `AlwaysOnEngine.cs(901)`). Linux suite **294 / 0**. Mac suite on Linux 251 / 1 - the one failure is `MacPathsTests.MachineLocalAppData_OnMac_IsTheApplicationSupportFolder`, which fails **identically on main** (225 / 1, same `Expected "/home/soren/Library/Application Support"` / `Actual "/home/soren/.local/share"`) and **passes on the real Mac CI runner (252 / 0)**. Windows: CI 2322 / 0 | PASS |
| 10 | Watchdog unit tests with fake clock and growth: fires on stop, not while growing, not in start-up | Present in `tests/Shared/CaptureWatchdogTests.cs`; the mutation run below shows they fail when the guard is disabled | PASS |

## The defect (criterion 1)

**`/status` names a stalled capture's fault only after the stop has finished. The stop waits a fixed
~13.5 s on a process the watchdog has already declared dead, so detection (45-47 s) plus that wait
straddles the 60-second window.**

Where:

- `src/AgentEyes.Core/Video/FfmpegRecorder.cs:292` - `Abandon(reason)` just calls `Stop()`.
- `src/AgentEyes.Core/Video/FfmpegRecorder.cs:32,196` `Stop()`: waits up to `DrainTimeout` (5 s) for the
  pipeline to reach the stop position, which a SIGSTOPped process never does, then writes `q` and
  waits `WaitForExit(8000)`, which a frozen process never answers, then kills it.
- `src/AgentEyes.Core/RecordingService.cs:935` - `_lastCaptureFault = recorded;` inside `SaveManifest()`: this is the only place
  `/status`'s `LastCaptureFault` is set, and it runs after the writer steps have finished.
- Between the verdict and that line, `/status` shows `State=finalizing`,
  `CaptureLiveness=faulted`, `LastStopFailed=false`, `LastCaptureFault=null`. It says something
  ended, but not that the capture stalled, and `LastStopFailed=false` reads like a normal stop.

Timeline of QA run 1 (log, pid 2657099; SIGSTOP at 12:39:21.674):

```
12:40:07.433 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=66.0s (detected 2026-11-14T17:40:07.431Z) ... capture stalled ...
12:40:07.485 [WARN]  [FfmpegRecorder] Abandon: .../raw.mp4 - capture stalled: ...
12:40:12.498 [WARN]  [FfmpegRecorder] Stop: pipeline did not reach 66972ms within 5s (reached 20310ms) ...   <- 5.0 s drain wait
12:40:20.946 [INFO]  [FfmpegRecorder] Abandon: .../raw.mp4 ended (exit 137, last reported position 20.31s) <- 8.4 s quit wait, then kill
12:40:21.050 [INFO]  stop: abandoned capture - raw.mp4 probes at 18.73s (ffmpeg last reported 20.31s)
12:40:21.061 [ERROR] [RecordingStopSequence] Run FAILED ... capture watchdog: capture stalled ...         <- LastCaptureFault now set
```

Measured, three SIGSTOP runs (latency from injection, polled every 0.2-0.25 s):

| Run | Injected (UTC) | Left `recording` | Fault first named on `/status` | Note |
|---|---|---|---|---|
| QA stop | 17:39:21.674 | 46.06 s | idle poll at 59.57 s (log: set at 59.39 s) | injected just after the watchdog saw growth |
| QA stop2 | 17:41:26.717 | 46.05 s | **59.49 s** | same |
| QA stop3 | 17:43:19.952 | 46.54 s | **60.17 s** | injected the instant `raw.mp4` grew (475,471 -> 513,873), before the watchdog saw it. The watchdog logged last growth at 17:43:20.333, 0.38 s **after** the freeze |
| Developer `runs/final-stop` | 17:27:47.920 | 45.6 s | `after` body at 17:28:48.248 = **60.33 s** | the body the handoff quotes as AC1's proof |

Worst case by construction: the last write lands just before the freeze and is first seen at the next
check (up to 2 s later), the verdict comes 45 s plus up to one 2 s interval after that, and then
the stop takes ~13.5 s. That is about 60-62 s. The tests that guard the window
(`Defaults_StallPlusOneCheck_FitInsideTheSixtySecondWindow`, `Stall_WhereverInTheIntervalItBegins_...`)
budget `StallTimeout + Interval <= 50 s` and stop at the verdict. They never count the stop that has
to finish before `/status` says what happened, and that is where the window is lost.

What would fix it. These are suggestions; the Developer chooses:

1. Publish the verdict on `/status` **at detection**. For example, set `_lastCaptureFault` (and the
   directory) in `EndFaultedCapture` alongside `_pendingFault` (`RecordingService.cs:1509`), or have `Status()` report
   `_pendingFault` while finalizing. With that change, AC1 is satisfied at ~46-47 s whatever the stop costs.
2. And/or make `Abandon` kill at once (or after a short grace) instead of running the full
   drain-and-quit `Stop()`. A capture the watchdog judged dead has no pipeline to drain. This also
   removes `Abandon`'s dependence on `Stop()`'s timeouts, which PR #127 is rewriting (see "Rebase").
3. Add a test that asserts `/status` names the fault before the stop finishes (see check 6).

The re-test is the same as `qa-fault.sh STOP` with `ONGROWTH=1`: the fault must be named on
`/status` well inside 60 s, not at 59-60 s.

## Raw evidence

### AC1 - SIGSTOP (QA run 1, `2026-11-14_123900_video`, capture pid 2660660, ppid 2657099)

```
[17:39:00.146Z] before:   State=idle ...
[17:39:11.575Z] healthy1: State=recording ElapsedSeconds=10.17 CaptureLiveness=growing CaptureBytes=204407 CaptureLastGrowthUtc=17:39:11.419
[17:39:21.627Z] healthy2: State=recording ElapsedSeconds=20.22 CaptureLiveness=growing CaptureBytes=476495 CaptureLastGrowthUtc=17:39:21.422
[17:39:21.674Z] injected SIGSTOP into 2660660
[17:39:31.749Z] stalled:  State=recording ElapsedSeconds=30.35 CaptureLiveness=not-growing CaptureBytes=476495 CaptureLastGrowthUtc=17:39:21.422 CaptureSecondsSinceGrowth=10
[17:40:07.735Z] State left recording after 46.06s (now finalizing)
[17:40:07.738Z] left:     State=finalizing CaptureLiveness=faulted CaptureSecondsSinceGrowth=46 LastStopFailed=False LastCaptureFault=None
[17:40:21.245Z] after:    State=idle LastStopFailed=True LastCaptureFault="capture stalled: raw.mp4 stopped growing at 476495 bytes and has not grown for 46s (limit 45s); last growth 2026-11-14T17:39:21.421Z"
capture ffmpeg 2660660 is gone
```

`ElapsedSeconds` went 10.17 -> 20.22 -> 30.35 through the stall, and `Level` was 0 in every poll
(a quiet room), so neither one could have told a live capture from a dead one. Only the `Capture*`
fields did.

### AC2 - SIGKILL (`2026-11-14_124427_video`, capture pid 2684793)

```
[17:44:49.171Z] healthy2: State=recording CaptureLiveness=growing CaptureBytes=475812
[17:44:49.214Z] injected SIGKILL into 2684793
[17:44:51.031Z] State left recording after 1.82s (now finalizing)
[17:44:51.034Z] left:  State=finalizing CaptureLiveness=faulted LastCaptureFault=None
[17:44:51.347Z] /status first NAMED the fault after 2.13s
[17:44:51.349Z] named: State=idle LastStopFailed=True LastCaptureFault="capture exited: the capture process exited (code 137) while recording; raw.mp4 holds 475812 bytes"
```

### AC3 - the signal, and what it is made of

`CaptureBytes` is `FileInfo.Length` of `raw.mp4`, read by `CaptureWatchdog.Check()` every 2 s
(`RecordingService.StartWatchdog`, `CaptureWatchdog.FileSize`). `CaptureLastGrowthUtc` is the wall
time of the last check that saw that size increase. It tracks output because the capture flushes
every 2 s fragment to disk (`-flush_packets 1`). Without the flush, the developer measured the file
standing at 28 bytes while ffmpeg's `time=` reached 10 s. Bytes on disk are what the user keeps,
which makes this a stricter signal than the encoder's clock. The three polls are in the AC1 block
above: 10.05 s and 10.12 s apart.

### AC5 - the leftover files (ffprobe `-count_frames`)

| Recording | Fault | `recording.original.mp4` (the raw capture) | `recording.mp4` (after mux) |
|---|---|---|---|
| `2026-11-14_123900_video` | stalled | 18.733 s, video 560, audio 872 | 18.733 s, video 560, audio 873 |
| `2026-11-14_124105_video` | stalled | 18.733 s, video 560, audio 872 | 18.733 s, video 560 |
| `2026-11-14_124257_video` | stalled | 20.733 s, video 620, audio 966 | 20.733 s, video 620 |
| `2026-11-14_124427_video` | exited | 18.733 s, video 560, audio 872 | 18.733 s, video 560 |

The freeze or kill came at ~20.2-20.3 s; the file ends at the last 2 s fragment flushed before it.
All four post-processed fully (`mux`, `thumbnail`, `package` = done).

The choice is fragmented MP4. The capture command the service ran (from `/proc/<pid>/cmdline` of
pid 2660660 and 2684793) ends with:

```
-movflags +frag_keyframe+empty_moov+default_base_moof -frag_duration 2000000 -flush_packets 1 .../raw.mp4
```

Paired case (the instrument can say FAIL): a plain-MP4 x11grab capture of mine, SIGSTOPped at 10 s
and SIGKILLed 2 s later, gives `moov atom not found ... Invalid data found when processing input`
(48 bytes on disk). The same command with an audio track and the shipped flags, killed at 24 s,
probes at 20.73 s / 620 frames (`qa-runs/paired/`).

An observation, not a defect: one video-only control run of mine sat at `frame=0` for its whole
24 s and left a 762-byte header-only file. A re-run of the identical command grew every 2 s, and
the service's own video-only capture (`source=system`, below) grew normally. That is the start-up
starvation signature the developer described, which `never-started` is there to catch.

A normal recording still has its index. In the 10-minute healthy run, `recording.original.mp4`
(the raw capture) has top-level boxes `ftyp 1, moov 1, moof 362, mdat 362, mfra 1`, and the muxed
`recording.mp4` has `ftyp, free, mdat, moov`, a conventional MP4. Both probe at 602.87 s,
18,084 video frames.

### AC6 - manifest fields

```
2026-11-14_123900_video (SIGSTOP):
  "CaptureFault": "stalled",
  "CaptureFaultDetail": "raw.mp4 stopped growing at 476495 bytes and has not grown for 46s (limit 45s); last growth 2026-11-14T17:39:21.421Z",
  "CaptureFaultDetectedUtc": "2026-11-14T17:40:07.4316253Z",
  "CaptureFaultElapsedSeconds": 66,
  "CapturedSeconds": 18.73, "DurationSeconds": 18.73
2026-11-14_124427_video (SIGKILL):
  "CaptureFault": "exited",
  "CaptureFaultDetail": "the capture process exited (code 137) while recording; raw.mp4 holds 475812 bytes",
  "CaptureFaultDetectedUtc": "2026-11-14T17:44:50.9666052Z",
  "CaptureFaultElapsedSeconds": 22,
  "CapturedSeconds": 18.73, "DurationSeconds": 18.73
```

### AC4 - the 10-minute healthy run

`qa-runs/healthy/console.txt`, using the developer's `healthy-run.sh 600`, re-run by QA on pid 2657099,
17:45:25Z -> 17:55:27Z. Load average stayed between 3.2 and 3.9 on 32 cores (`load.txt`), so this
run was cleaner than the developer's (load 9-18, plus its stray ffmpeg).

```
[17:45:36.094Z] poll 1:  State=recording CaptureLiveness=growing CaptureBytes=203051   ElapsedSeconds=10.05
[17:45:46.288Z] poll 2:  State=recording CaptureLiveness=growing CaptureBytes=475489   ElapsedSeconds=20.25
...
[17:55:27.066Z] poll 59: State=recording CaptureLiveness=growing CaptureBytes=14135602 ElapsedSeconds=601.03
[17:55:27.148Z] stop: {"Dir": ".../2026-11-14_124524_video", "File": ".../recording.mp4", "DurationSeconds": 601.23, "Shots": 0}
polls=59 all-recording-and-advancing=yes LastCaptureFault=None LastStopFailed=False
12:55:28.026 [pid 2657099] [INFO] [FfmpegRecorder] Stop: pipeline drained in 765ms (take 602222ms) - full take captured
```

Final: `recording.mp4` 602.866 s, 18,084 video / 28,253 audio frames; manifest `DurationSeconds
602.87`, `CapturedSeconds 602.71`, `CaptureFault` null. Over 12:45-12:55, a grep of pid 2657099's log for
`CAPTURE FAULT|Abandon|behind|wrote .* of a` finds **0** lines. That absence counts because the same grep
on the same build found the fault lines in all four fault runs.

Also, outside the criteria, a screen + **system audio** recording (`qa-runs/system/`). Its screen capture
has no audio track (`RecordingService.cs`: `src == AudioSourceKind.System ? null : dshowMic`), and
that is the case where I expected a fragmented file might not grow. Over 78 s, 15 polls were all `growing`
(55,201 -> 587,546 bytes). It stopped normally with `LastStopFailed=false` and no fault. Not falsely ended.

## The six checks

**1. Criteria 1 and 2.** See above. SIGKILL passes with a lot of margin (2.1 s). SIGSTOP is detected
at 46 s, but the fault is only named at 59.4-60.2 s. **FAIL.**

**2. Criterion 3.** The signal is the output file's byte count and the time it last grew, read off
the disk. It does not come from `elapsed`, `level` or ffmpeg's progress line. It advanced between the two
healthy polls (+272,088 bytes in 10 s) and froze after the stall (same byte count, same
`CaptureLastGrowthUtc`, `CaptureLiveness=not-growing`, `CaptureSecondsSinceGrowth=10`). **PASS.**

**3. Criterion 4.** My run was the cleaner one (load 3-4) and it did not false-positive: 59 / 59 polls `recording`
and `growing`, and the file played for 602.87 s. Together with the developer's run under load 9-18 plus
its stray ffmpeg, that is two clean 10-minute runs at very different loads. The paired presence: the
same build, in the same session, ended four recordings on injected faults. **PASS.**

**4. Criterion 5.** Fragmented MP4 with a flush per fragment. The flags are in the command the service
ran, all four fault files play, and a plain MP4 killed the same way does not (paired case). A normal
recording still carries a `moov` atom (see AC5). **PASS.**

**5. Can the tests fail?** **Yes for the watchdog. No for its wiring.**

- Mutant A (all three verdicts disabled in `CaptureWatchdog.Check`): **Failed 9, Passed 285**, which is the
  same nine tests as the developer's list: `NeverStarted_AnEmptyFileIsNotOutput`,
  `Stall_GrowthStops_FiresStalledInsideTheWindow`, `Fault_IsFinal_GrowthAfterTheDecisionDoesNotClearIt`,
  `Exited_ProcessEnds_FiresAtTheNextCheck`,
  `Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds` (offset 0, 0.5, 1.9),
  `Gap_ThenNoGrowth_StillFiresOneWindowAfterTheResume`, `NeverStarted_NoOutputPastTheStartupWindow_Fires`
  (`qa-runs/mutant-A.txt`).
- Mutant W, QA's own (the `StartWatchdog(_video!);` call deleted from `RecordingService.StartVideo`, so the
  service never watches any capture): **Passed 294, Failed 0. It survives** (`qa-runs/mutant-wiring.txt`).
  The whole guard can be disconnected from the product with the suite green. See check 6.
- Restored with `git checkout --`: `git diff` empty, HEAD `c3eabd8`, suite **Passed 294, Failed 0**
  (`qa-runs/restored.txt`).

**6. The developer's stated gap: no unit test for the RecordingService wiring.** **Not acceptable for
this defect, and this QA pass shows why.** The one criterion that fails, fails in the wiring:
the time between the watchdog's verdict and `/status` saying so. The watchdog's own tests all
pass, because the verdict really is on time. 311 lines of service code that decide what a user
sees during the worst failure this product has are covered only by live runs, and the live run the
developer quoted for AC1 was itself 0.33 s over the limit without anyone noticing. A test should:

- assert that, at the moment the watchdog returns a fault, `Status()` reports `State != "recording"` **and a
  field naming the fault kind and message**, before any writer's stop has returned. To test this,
  give it a fake capture whose stop blocks, so the test catches the current 13.5 s gap;
- assert that `StartVideo` starts the watchdog. The handoff itself says deleting that call keeps the suite green;
- assert that a user `Stop()` takes the watchdog down before the writers stop, so a deliberate stop
  is never recorded as `stalled`;
- assert that a watchdog-ended recording gives `LastStopFailed=true` and writes the manifest's `CaptureFault*` fields.

That needs a seam: an interface over `FfmpegRecorder` (`OutputPath`, `ExitCode`, `Stop`/`Abandon`)
plus an injectable `ICaptureClock`, which the watchdog already has. That is a moderate refactor, but
without it the next change to `RecordingService` (and #127 is about to make a large one) can break
this guard silently.

## CI

PR #137, head `c3eabd8`, both the `push` run (43621649985) and the `pull_request` run (43621674479)
are green. Counts from the `pull_request` run's job logs: **linux 294 / 0, macos 252 / 0,
windows 2322 / 0** (`dotnet test AgentEyes.sln`). `CaptureWatchdogTests.cs` is linked into all
three test projects. This CI ran on the branch head, not on a rebase onto `main`. The owner's
review-gate rule still needs a re-run after the rebase.

## Rebase onto #127 - what will obviously matter

- `FfmpegRecorder.Abandon` is `Stop()` plus two log lines. #127 rewrites `Stop()`'s drain and clock,
  so whatever #127 does to the drain timeout changes how long a faulted recording takes to be named
  on `/status`. This is the defect above, and fixing it by making `Abandon` independent of `Stop()`
  also removes the coupling.
- `MediaSeconds` reads `_mediaMs`. If #127 renames or re-bases that clock (the lead mentioned
  `_mediaMs`/`_mediaOriginMs`), `CapturedSeconds` and the `behind` check move with it, and the
  `behind` verdict decides `DurationSeconds` on every normal stop. That needs re-verifying after the
  rebase, and the EARLY case re-run.
- The AVFoundation argument order changes (#127 removes the mic parameter), so `FragmentedOutput()`
  must stay immediately before the output path. `MacFfmpegArgsTests` exact-line tests will catch it.

## Not covered by this QA pass

- macOS and Windows at runtime. The original 18-hour stall was on macOS.
- The original stall itself, which the issue explicitly does not require.
- The 2026-11-14 Linux "behind" case (`inject-fault.sh EARLY`). It is not one of the ten criteria. I did
  not re-run it, so the developer's result for it is a claim only.
- `never-started` at runtime (the watchdog unit tests cover it).

## Machine state at the end

- The service is back on **main `af0ddb1`**: pid **2826484**, started 13:03:52 EST, `agenteyes.dll` sha256
  `79979fd73e0999b01ff59c23c97330befbc50f21f1f0bfc3e22fee9c8319b03c` (byte-identical to the pre-QA
  build), `/status` idle with no `Capture*` fields.
- No ffmpeg of QA's left running (`pgrep -a ffmpeg` is empty). Every control ffmpeg was killed in the
  same command that started it.
- Recordings made by QA, kept: `2026-11-14_123900_video`, `_124105_`, `_124257_`, `_124427_` (faults),
  `_124524_` (10-minute healthy), `_130001_` (system audio). Nothing under `~/Videos/AgentEyes` was deleted.
  `2026-11-14_102532_video` and `2026-11-14_093032_video` were not touched.
- Raw evidence: `docs/cencon/proof/issue-113/qa-runs/` (account email redacted). Driver: `qa-runs/qa-fault.sh`.
