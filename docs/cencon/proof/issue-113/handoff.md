# Issue #113 - handoff to QA: the capture watchdog

Branch `issue-113-capture-watchdog`, cut from `main` at `af0ddb1`. Developer seat on `devlinux`,
2026-11-14. Platform exercised at runtime: **Linux only** (x11grab + PulseAudio). macOS and Windows
are covered by the shared unit tests and the argument tests only - see "What is NOT covered".

## What was built

| Piece | Where |
|---|---|
| `CaptureWatchdog` - decides from file growth whether a capture is producing output. Pure apart from a size delegate, an exit-code delegate and an `ICaptureClock`. | `src/AgentEyes.Core/Video/CaptureWatchdog.cs` (new) |
| The service runs it every 2 s for every screen recording, publishes the reading on `/status`, and ends the recording on a fault. | `src/AgentEyes.Core/RecordingService.cs` |
| The CLI `agenteyes video` loop runs the same watchdog. | `src/AgentEyes.Core/Commands.cs` |
| Fragmented, flushed MP4 for the screen capture on all three platforms. | `FfmpegArgs.FragmentedOutput()` in `Video/FfmpegArgs.cs`, used by `VideoCapture`, `X11VideoCapture`, `AvfVideoCapture` |
| `ExitCode`, `MediaSeconds`, `Abandon(reason)` on the recorder - **additive only**. The drain, the stop and the media clock are untouched (issue #114 / PR #127 reworks them and lands first). | `src/AgentEyes.Core/Video/FfmpegRecorder.cs` |
| Manifest fields `CaptureFault`, `CaptureFaultDetail`, `CaptureFaultDetectedUtc`, `CaptureFaultElapsedSeconds`, `CapturedSeconds`. | `src/AgentEyes.Core/Manifest.cs` |
| `RecordingStopSequence.Run(..., knownFailures)` - the watchdog's verdict leads the stop report, so a watchdog-ended recording is `LastStopFailed=true`. | `src/AgentEyes.Core/RecordingStopSequence.cs` |
| 25 shared unit tests (fake clock + fake size source), linked into all three suites. | `tests/Shared/CaptureWatchdogTests.cs` |

### The thresholds, and why not exactly 60 s

The issue's 60 s is the **detection window** (fault to `/status` not-recording). To land inside it,
the watchdog declares a stall after **45 s** with no growth, checked every **2 s**: worst-case
detection is 47 s after the last write. Measured: 45.6 s. The same 45 s is the start-up window
(no output at all). Two checks more than 15 s apart are a **gap** (suspend, starvation), and a gap
is not counted as a stall - the window restarts from that check.

### Why file growth, not media time

The lead pointed at `_mediaMs`/`_mediaOriginMs` on PR #127 as a candidate signal. File growth was
chosen, on a measurement: on this machine, 2026-11-14, ffmpeg's `time=` advanced from 0 to 10 s
**while the MP4 on disk stayed at 28 bytes** (and 48 bytes for plain MP4), because ffmpeg's 32 KB
write buffer held everything a static screen produced. A SIGKILL then lost all of it. Media time
reports what the encoder did; growth reports what reached the disk, which is what the user keeps.

Growth is only a usable heartbeat because of `-flush_packets 1`: with fragmented MP4, a 2 s
fragment, and a flush per fragment, the file grows every 2 s of media on any screen, static or not.

### Why fragmented MP4 (AC5)

Measured with x11grab + pulse on `devlinux` (`fragtest.sh`):

| Output | SIGKILL at 10 s | SIGSTOP at 8 s, then SIGKILL | clean `q` |
|---|---|---|---|
| plain MP4 | `moov atom not found` | `moov atom not found` | plays |
| fragmented, no flush | `moov atom not found` (28 bytes on disk) | same | plays |
| **fragmented + flush** (shipped) | **plays: 8.07 s, 240 video frames** | **plays: 6.07 s, 180 frames** | plays: 7.93 s |

Fragmented MP4 needs no remux on stop and no new file name, so every downstream stage (mux,
thumbnail, transcription, package, the players) reads the file it read before. Capture-to-MKV +
remux was the alternative and was not needed.

## Acceptance criteria - what was implemented, how QA exercises it, what I measured

All runs below: branch build installed with `scripts/linux/install.sh` from the worktree, screen
(1920x1080 x11grab, 30 fps) + mic (PulseAudio `Family 17h ... Analog Stereo`), driven only through
`127.0.0.1:7882`. Raw `/status` bodies with their clock times are in `runs/` (account email redacted).

**Driver scripts (in this folder):**

```
MIC="Family 17h" ./inject-fault.sh STOP    # AC1, AC3, AC5, AC6, AC7
MIC="Family 17h" ./inject-fault.sh KILL    # AC2, AC5, AC6
MIC="Family 17h" ./inject-fault.sh EARLY   # the 2026-11-14 Linux recording's signature
MIC="Family 17h" ./healthy-run.sh 600      # AC4
./inspect-recording.sh <recording dir> 120 # manifest fields + ffprobe duration/frames
```

`inject-fault.sh` starts a screen+mic recording, polls `/status` at +10 s and +20 s, finds the ffmpeg
with `pgrep -f 'ffmpeg.*(raw|recording)\.mp4'`, injects the signal, polls every 0.5 s, saves the
poll at +10 s, prints the latency from injection to `State != recording`, and waits for idle
**before** resuming anything (a resumed ffmpeg would answer the quit cleanly and skip the kill path).

### AC1 - a frozen capture ends the recording - PASS (Linux)

Implemented: the watchdog's `stalled` verdict; `EndFaultedCapture` stops the recording.
Measured (`runs/final-stop`), SIGSTOP at **17:27:47.920Z**:

```
17:27:47.608Z healthy2  State=recording   CaptureLiveness=growing     CaptureBytes=475445
17:27:58.296Z stalled   State=recording   CaptureLiveness=not-growing CaptureBytes=475445 CaptureSecondsSinceGrowth=10
17:28:33.507Z left      State=finalizing  CaptureLiveness=faulted     CaptureSecondsSinceGrowth=46
17:28:48.248Z after     State=idle  LastStopFailed=true
                        LastCaptureFault="capture stalled: raw.mp4 stopped growing at 475445 bytes and has not
                        grown for 46s (limit 45s); last growth 2026-11-14T17:27:47.393Z"
```

Detection latency: **45.6 s** (two earlier runs: 45.7 s, 45.9 s). Expected <= 60 s.

### AC2 - a dead capture ends the recording - PASS (Linux)

Implemented: the watchdog's `exited` verdict, from `FfmpegRecorder.ExitCode`, decided at the next
check. Measured (`runs/final-kill`), SIGKILL at **17:29:10.280Z**:

```
17:29:09.976Z healthy2  State=recording   CaptureLiveness=growing  CaptureBytes=476805
17:29:11.940Z left      State=finalizing  CaptureLiveness=faulted
17:29:12.155Z after     State=idle  LastStopFailed=true
                        LastCaptureFault="capture exited: the capture process exited (code 137) while recording;
                        raw.mp4 holds 476805 bytes"
```

Detection latency: **1.66 s** (earlier run: 1.49 s from the log). Expected <= 60 s.

### AC3 - a liveness signal anyone can read - PASS (Linux)

`/status` gains `CaptureLiveness` (`starting` / `growing` / `not-growing` / `faulted`), `CaptureFile`,
`CaptureBytes`, `CaptureLastGrowthUtc`, `CaptureSecondsSinceGrowth`, read off the file every 2 s.
Three polls, 10 s apart, from `runs/final-stop`:

| Poll (UTC) | CaptureBytes | CaptureLastGrowthUtc | CaptureLiveness |
|---|---|---|---|
| 17:27:37.398 healthy | 203,770 | 17:27:37.393 | growing |
| 17:27:47.608 healthy | 475,445 | 17:27:47.394 | growing |
| 17:27:58.296 stalled | **475,445** | **17:27:47.394** | not-growing |

`ElapsedSeconds` went 10.02 -> 20.23 -> 30.92 across the same polls - it kept moving through the
stall, which is why it is not the signal.

### AC4 - no false positive on a healthy 10-minute recording - PASS (Linux)

`runs/healthy600/console.txt`: **59 polls over 603 s, every one `State=recording`,
`CaptureLiveness=growing`, `CaptureBytes` strictly increasing** (203,156 -> 14,183,322). Stop:
`DurationSeconds 603.19`, `LastStopFailed=false`, `LastCaptureFault=null`. Final file after the mux:
`recording.mp4` **604.73 s, 18,140 video frames, 28,344 audio frames**; manifest `CaptureFault`
absent. The machine was under load from other seats during the run (load average 9-18 on 32 cores).

### AC5 - an unclean end still leaves a playable file - PASS (Linux)

`inspect-recording.sh` after each fault, once post-processing finished:

| Fault | raw capture (`recording.original.mp4`) | final `recording.mp4` | manifest `CapturedSeconds` |
|---|---|---|---|
| SIGSTOP | 18.73 s, 560 video frames | 18.73 s, 560 frames, mux/thumbnail/package done | 18.73 |
| SIGKILL | 18.73 s, 560 video frames | 18.73 s, 560 frames, mux/thumbnail/package done | 18.73 |

Neither says `moov atom not found`. The stall was injected at ~20.2 s; the file ends at the last
2 s fragment flushed before it. For an abandoned capture `CapturedSeconds` is **probed from the
file** (ffmpeg's last progress report ran 2.1-2.4 s ahead of what reached disk: 21.12 s / 20.85 s
reported, 18.73 s on disk).

A recording the watchdog ends is **post-processed at once** when the verdict is its only failure
(every writer stopped, manifest on disk) - the same mux/thumbnail/transcript a user stop gets. Any
other failure leaves it to the recovery pass, as `RecordingStop.Keep` does for every failed stop.

### AC6 - the fault is in the manifest - PASS (Linux)

From `~/Videos/AgentEyes/2026-11-14_122726_video/manifest.json` (SIGSTOP):

```
"CaptureFault": "stalled",
"CaptureFaultDetail": "raw.mp4 stopped growing at 475445 bytes and has not grown for 46s (limit 45s); last growth 2026-11-14T17:27:47.393Z",
"CaptureFaultDetectedUtc": "2026-11-14T17:28:33.3992295Z",
"CaptureFaultElapsedSeconds": 66,
"CapturedSeconds": 18.73
```

and `2026-11-14_122848_video` (SIGKILL): `"CaptureFault": "exited"`,
`"CaptureFaultDetail": "the capture process exited (code 137) while recording; raw.mp4 holds 476805 bytes"`,
`"CaptureFaultDetectedUtc": "2026-11-14T17:29:11.7607343Z"`.

### AC7 - reported when it happens, not at stop - PASS (Linux)

`~/.local/share/AgentEyes/logs/AgentEyes-20261114.log`, written at detection, before the stop:

```
12:28:33.400 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=66.0s (detected 2026-11-14T17:28:33.399Z) dir=.../2026-11-14_122726_video - capture stalled: raw.mp4 stopped growing at 475445 bytes and has not grown for 46s (limit 45s); last growth 2026-11-14T17:27:47.393Z - ending the recording now and keeping what was captured
12:28:33.443 [WARN]  [FfmpegRecorder] Abandon: .../raw.mp4 - capture stalled: ...
12:28:47.012 [INFO]  [FfmpegRecorder] Abandon: .../raw.mp4 ended (exit 137, last reported position 21.12s)
12:29:11.760 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=22.0s (detected 2026-11-14T17:29:11.760Z) dir=.../2026-11-14_122848_video - capture exited: the capture process exited (code 137) while recording; ...
```

(Local time is EST, UTC-5.) `grep "CAPTURE FAULT"` finds them.

### AC8 - Windows and Linux still record

- Linux: AC4 above, at runtime.
- Argument tests asserting the capture command lines:
  `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs` (`X11VideoCapture_ScreenAndMic_ExactCommandLine`,
  `X11VideoCapture_WithPreview_FragmentsTheRecordingNotThePreview`),
  `tests/AgentEyes.Tests.Mac/MacFfmpegArgsTests.cs` (`AvfVideoCapture_WholeScreenAndMic_ExactCommandLine`,
  `AvfVideoCapture_WithPreview_FragmentsTheRecordingNotThePreview`),
  `tests/AgentEyes.Tests/FfmpegArgsTests.cs` (unchanged; still asserts the output path is last and
  the preview follows it), and the shared
  `CaptureWatchdogTests.FragmentedOutput_IsOnTheWindowsCaptureImmediatelyBeforeTheOutputPath`.
- The watchdog is unit-tested with a fake clock and fake growth in `tests/Shared/CaptureWatchdogTests.cs`,
  compiled into all three suites.
- **Windows was NOT run.** This SDK has no WindowsDesktop pack, so neither the Windows build nor
  the Windows suite can run here. The PR's CI is the only Windows evidence.

### AC9 - the gate

| Command (devlinux) | Result |
|---|---|
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64` | Build succeeded, 0 Error(s), 1 Warning (pre-existing, `AlwaysOn/AlwaysOnEngine.cs(901)`, not touched) |
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac` | Build succeeded, 0 Error(s), same 1 pre-existing warning |
| `dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release` | **Passed 294, Failed 0** (main: 268 / 0) |
| `dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release` (run on Linux) | Passed 251, Failed 1 - `MacPathsTests.MachineLocalAppData_OnMac_IsTheApplicationSupportFolder`, which **fails identically on main** (225 / 1) because it needs a real Mac. Must be re-run on a Mac. |
| `dotnet build AgentEyes.sln` / `dotnet test AgentEyes.sln` | **Not run** - Windows only. PR CI. |

### AC10 - unit tests for the watchdog itself - PASS

`tests/Shared/CaptureWatchdogTests.cs`, clock and growth faked: fires when growth stops
(`Stall_GrowthStops_FiresStalledInsideTheWindow`, the 3-case
`Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds`), does not fire while growth
continues (`Healthy_FileGrowsEveryCheckForTenMinutes_...`, `Healthy_FileGrowsOnlyEveryFortySeconds_...`,
`Healthy_GrowthStopsForJustUnderTheTimeout_...`), does not fire in the start-up window
(`Startup_NoBytesYet_InsideTheWindow_...`, `Startup_SlowFirstOutputThenGrowth_...`) and does fire
past it (`NeverStarted_*`), plus exit, gap, finality and the `/status` signal.

## The guard-disabled run (the tests can fail)

`mutate.py` disables the guard six ways, runs the Linux suite against each, and restores the file.
Output: `runs/mutation-guard-disabled.txt`. **Every mutant is killed.** (Where the count is larger
than the names, the rest are the three cases of the Theory
`Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds`, whose names contain a space the
script's regex stops at.)

| Mutant | Failed | Tests that failed |
|---|---|---|
| A - watchdog never fires (all three verdicts off) | 9 | `Exited_ProcessEnds_FiresAtTheNextCheck`, `Fault_IsFinal_GrowthAfterTheDecisionDoesNotClearIt`, `Gap_ThenNoGrowth_StillFiresOneWindowAfterTheResume`, `NeverStarted_AnEmptyFileIsNotOutput`, `NeverStarted_NoOutputPastTheStartupWindow_Fires`, `Stall_GrowthStops_FiresStalledInsideTheWindow`, `Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds` x3 |
| B - no fragmented output | 3 | `X11VideoCapture_ScreenAndMic_ExactCommandLine`, `X11VideoCapture_WithPreview_FragmentsTheRecordingNotThePreview`, `FragmentedOutput_IsOnTheWindowsCaptureImmediatelyBeforeTheOutputPath` |
| C - check gaps counted as stalls | 2 | `Gap_LongPauseBetweenChecks_IsNotCountedAsAStall`, `Gap_ThenNoGrowth_StillFiresOneWindowAfterTheResume` |
| D - no start-up window | 4 | `Healthy_FileGrowsOnlyEveryFortySeconds_NeverFaults`, `NeverStarted_NoOutputPastTheStartupWindow_Fires`, `Startup_NoBytesYet_InsideTheWindow_DoesNotFireAndReportsStarting`, `Startup_SlowFirstOutputThenGrowth_DoesNotFire` |
| E - stall timeout doubled (fires too late) | 5 | `Gap_ThenNoGrowth_StillFiresOneWindowAfterTheResume`, `Stall_GrowthStops_FiresStalledInsideTheWindow`, the Theory x3 |
| F - "behind" never recorded | 2 | `BehindAtStop_JustPastTheTolerance_IsAFault`, `BehindAtStop_TheEvidenceRecording_IsRecordedAsBehind` |
| restored | 0 | Passed 294 |

The first mutation run found a real test defect: `Fault_IsFinal...` looped `while (Fault == null)`
with no bound, so a disabled watchdog **hung the suite** for 23 minutes instead of failing it. It is
bounded now, and the script runs with `--blame-hang-timeout 60s`.

## The 2026-11-14 Linux recording (`2026-11-14_102532_video`, kept, untouched)

What it shows: ffmpeg printed `frame=0 time=N/A` for its whole life and, on the quit, flushed 43
frames at `fps=2.7 speed=0.0827x`. The capture was not dead; it was running at about 8% of real
time. The stop's drain gave up ("reached 0ms"), and the service then wrote `DurationSeconds 6.46`
(wall time) for a 1.43 s file. Two more today in the same log: 09:55:30 and 09:55:50.

**Reproduced on demand** (`inject-fault.sh EARLY`: freeze the capture in its first second, stop
with `-d '{}'` at ~6.5 s, resume it during the quit wait):

| Build | Log | `/record/stop` DurationSeconds | File | Fault recorded |
|---|---|---|---|---|
| **main** `af0ddb1` (control, `runs/control-main-early`) | `pipeline did not reach 7832ms within 5s (reached 720ms)` | **6.83** | 0.93 s, 28 frames | none |
| **this branch** (`runs/final-early`) | same shape | **0** | 0.17 s, 3 frames | `CaptureFault: "behind"`, "the capture wrote 0.00s of a 6.83s session; raw.mp4 is short by 6.83s"; `/status LastCaptureFault` |

A "behind" take is recorded when ffmpeg's reported seconds fall more than
`RecordingService.BehindToleranceSeconds` (3 s) short of the session. Healthy stops measured here
were 0.32 s short (pipeline lag) or ahead. It does not use the drain's internals.

**Root cause - a lead, not a finding.** I saw the same `frame=0 ... speed=0.0756x` signature live at
11:35 today, outside the service, while this machine's load average was **31.9 on 32 cores** (other
seats' test runners); minutes later, at load ~22, the identical command ran at 0.75x and 0.99x. A
CPU-starved encoder fits all three Linux occurrences. I did not reproduce it by loading the machine,
because that would have hurt the other seats on it. Not claimed.

## What these tests do NOT cover

- **The wiring in `RecordingService` has no unit test.** Starting the watchdog, the timer re-arm,
  `EndFaultedCapture`, the stop report, the manifest fields and the immediate post-processing are
  proven only by the runtime runs above. A change that stopped `StartVideo` calling
  `StartWatchdog` would keep the whole suite green. (RecordingService starts real ffmpeg; there is
  no seam for a fake capture today.)
- **macOS was not exercised at runtime**, and the issue's original stall was on macOS. The
  AVFoundation argument change is asserted only by its argument test. A Mac run of AC1/AC2/AC5 is
  needed: `inject-fault.sh` uses `date +%N` and `bc`, so on macOS it needs GNU coreutils or a port.
- **Windows was not built or run** (see AC9). WPF/Media Foundation playback of fragmented MP4 is
  untested; Media Foundation supports fragmented MP4, but nobody has played one in the app.
- **The Windows app's HUD is not told** when the watchdog ends a recording. `RecordingStopped`
  fires only on a clean stop, and the WPF app learns of stops through it, so its HUD may keep
  showing "recording" until the user presses stop (which then answers "not recording"). The Linux
  app polls `/status` and is fine. The issue rules `AgentEyes.App` out of scope; this is a
  follow-up.
- **Audio-only recordings are not watched.** Only the screen capture's ffmpeg is. The camera track
  has its own lost-mid-run handling (issue #28) and is not watched by this.
- **The original 18-hour stall was not reproduced.** Every proof is an injected fault.
- **Suspend/resume** is covered by the gap unit tests only.
- **`CapturedSeconds` after a normal stop** comes from ffmpeg's last progress line, which can be
  read before the final one arrives: the `EARLY` run recorded 0.00 s for a 0.17 s file. After an
  abandon it is probed from the file and matched exactly (18.73 / 18.73).

## Relationship to #128 and #127

- **#128** hangs a post-processing ffmpeg (`arnndn` + `speechnorm`). This guards the capture
  ffmpeg. `CaptureWatchdog` is general enough to watch a post-processing output file too (size
  source + exit code + clock), but a stage that writes its output only at the end would show
  "never-started" for its whole healthy run; #128 needs its own signal (progress lines). The proof
  for this issue touches no post-processing stage.
- **#127** (issue #114) lands first and changes `FfmpegRecorder.cs`, `RecordingService.cs`,
  `FfmpegArgs.cs` and `FfmpegArgs.Mac.cs`. This branch does not touch the drain or clock logic.
  Expected rebase conflicts: `AvfVideoCapture` (one `FragmentedOutput()` line before the output
  path; #127 removes the mic parameter), `MacFfmpegArgsTests` exact line, and additive blocks in
  `RecordingService.StartVideo` / `Stop`. Mechanical, but they need re-running the Mac suite.

## Service state on devlinux

Left running, idle, on **main `af0ddb1`** (reinstalled with `scripts/linux/install.sh` from the main
checkout at the end; `/status` shows no `Capture*` fields, which is how to tell). QA must install
this branch (`bash scripts/linux/install.sh` from a checkout of it) before running the scripts.

Test recordings made today are kept under `~/Videos/AgentEyes/` (nothing deleted). The evidence
recording `2026-11-14_102532_video` was only read.

I believe this is finished for Linux. macOS runtime proof and the Windows CI result are open.
