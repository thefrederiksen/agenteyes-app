# Issue #113 - rebase onto main, and the re-verification that goes with it

**Date:** 2026-11-15
**Machine:** devthrottle-mac-mini, macOS 26.5, arm64, ffmpeg 9.0.2 (Homebrew)
**Branch:** `issue-113-capture-watchdog`, merged with `main` at `432105b`
**Why this document exists:** this branch was passed by QA against a tree that no longer exists.
Two PRODUCT-CODE files conflicted, both in the capture path, so every acceptance criterion was
re-run on the merged tree against the shipped defaults rather than carried forward.

The re-verification found a defect that neither branch had on its own. It is fixed here, with a
test, and re-measured. Section 3.

---

## 1. The merge

Seven files conflicted. Five were list unions and both sides' entries are kept:

| File | What was unioned |
|---|---|
| `tests/AgentEyes.Tests/AgentEyes.Tests.csproj` | `<Compile Link>` of the #113 watchdog tests and of #136's `FfmpegMuxShapeTests` / `MuxLockTests` |
| `tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj` | the same |
| `tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj` | the same |
| `tests/AgentEyes.Tests.Mac/MacFfmpegArgsTests.cs` | the expected argument list: #114's `-r 30` AND #113's fragment options, in the order the builder emits them |
| `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` | the pinned file-write list: #128's and #136's new sites AND #113's `FfmpegRecorder::Abandon` pin |

The two in product code:

### `src/AgentEyes.Core/Platform/Mac/FfmpegArgs.Mac.cs`

- The `-c:a aac -b:a 128k` branch is **dropped**. #114 took the microphone out of the macOS screen
  recorder entirely, and `AvfVideoCapture` no longer takes an `avfMicIndex` parameter at all - the
  branch could not compile, let alone run.
- `FragmentedOutput()` is **kept**. It exists nowhere on main; it is #113's own remedy. All three
  screen capture builders call it in the merged tree: `Video/FfmpegArgs.cs:89` (gdigrab),
  `Platform/Mac/FfmpegArgs.Mac.cs:207` (avfoundation), `Platform/Linux/FfmpegArgs.Linux.cs:63`
  (x11grab).

### `src/AgentEyes.Core/RecordingService.cs`

Both sides kept. #114's `recorderMic` computation and its comment are passed **through** #113's
`_seams.StartScreen` seam, so macOS mic routing and the watchdog wiring tests both survive.

### Two compile breaks with no textual conflict

The round-two handoff predicted both.

- `IScreenRecorder` gains `InputStartSeconds`. The stop reads it off the screen recorder to measure
  #114's mic/video alignment, and `_video` is the seam's interface type now, not `FfmpegRecorder`.
- `MacFfmpegArgsTests`' #113 preview test dropped the `avfMicIndex` argument #114 removed.

---

## 2. The lead's three resolutions, measured

All three were right. One correction to the reasoning behind the first.

The brief said a "take main" resolution of `FfmpegArgs.Mac.cs` would delete `FragmentedOutput()` and
that **nothing in the suite would tell you**. On the merged tree that is no longer true, and the
reason is the fifth conflict in the table above. Mutant **B2** removes exactly that line and the
suite fails by name:

```
=== B2-mac-capture-drops-the-fragmented-output
    Failed!  - Failed: 2, Passed: 311, Total: 313
    FAILED MacFfmpegArgsTests.AvfVideoCapture_WholeScreen_ExactCommandLine
    FAILED MacFfmpegArgsTests.AvfVideoCapture_WithPreview_FragmentsTheRecordingNotThePreview
```

The warning still holds in the case it was written for: those two tests only bite because the
EXPECTED argument list in `MacFfmpegArgsTests.cs` was union-resolved and kept #113's fragment
entries. A seat that resolved both the product file and the test file the same way - "take main" in
each - would have had a green suite with the remedy gone. The guard is the union, not the test.

Whether the remedy is load-bearing at all was measured separately rather than assumed
(`runs/fragmented-output-counterfactual.txt`): same encoder settings, same unclean end, flags off
versus on.

| | Without the flags | With the flags |
|---|---|---|
| moov atom | **ABSENT** | **@ 28** |
| ffprobe | `moov atom not found` | duration 127.000000, 3810 frames |
| full decode | exit **183** | exit **0** |

`moov atom not found` and exit 183 are the 2026-11-12 failure in this issue verbatim.

---

## 3. The defect the re-verification found, and the fix

**A frozen capture left the service in `finalizing` for as long as the frozen process existed, and
the watchdog's kill never ran.** Found on the merged tree with the shipped defaults; present on
neither branch alone.

### What was measured (`runs/defect-finalizing-stall.txt`)

SIGSTOP the screen ffmpeg of a screen+mic recording, then touch nothing for 330 s:

```
+  0.0s State=recording  Liveness=growing     | capture pid 2175 state=T | service cpu=0.7%
+ 50.1s State=finalizing Liveness=faulted  YES| capture pid 2175 state=T | service cpu=100.1%
+180.4s State=finalizing Liveness=faulted  YES| capture pid 2175 state=T | service cpu=100.0%
+320.8s State=finalizing Liveness=faulted  YES| capture pid 2175 state=T | service cpu=98.6%
end: final state=finalizing
```

330 s was the whole observation, not a timeout. Throughout it: the frozen child stayed in process
state `T`, never killed; the recording directory still held `raw.mp4`, never finalized, with no
post-processing; and the service burned a full core. The watchdog's own verdict was correct and on
time - the fault was named on `/status` at 46 s - but the recording it was saving was not saved.

Killing that child from outside ended it **within one second**, with service CPU falling from ~100%
to 4.9%. That is the causal link, not an inference.

### The cause, sampled rather than guessed (`runs/sampled-cause-net-sighandler-spin.txt`)

`sample` of the service during the stall shows one thread at ~100% CPU for the duration: .NET's own
child reaper, `.NET SigHandler`, spinning in `SystemNative_WaitIdAnyExitedNoHangNoWait` / `wait4` on
a child that is STOPPED rather than exited. The microphone writer's `Process.Dispose` cannot get
past it - and **#114 is what put a microphone writer in front of the capture on macOS**. Before the
merge the macOS screen recorder captured the mic itself, so the capture's own `Abandon` was the
first stop step and nothing was in front of it.

The control: the same injection with `source=none`, one child, the `Abandon` first - the service
reaches idle unaided in **51 s**.

### The fix

`RecordingService.OrderStopSteps`: a stop the capture watchdog caused kills the capture **first**,
ahead of every other writer. A healthy stop keeps the order it always had - audio, loopback, screen,
camera - because the mux reads what the audio writers wrote. Ordering only; nothing retried,
nothing swallowed.

Guarded by `tests/Shared/RecordingStopOrderTests.cs` (5 facts, all three suites). **Both** orders are
asserted, so it cannot be satisfied by always putting the capture first, and a fourth test counts
the steps so an ordering that silently DROPPED a writer could not pass the index comparisons.

| | Before the fix | After the fix |
|---|---|---|
| `/status` leaves `recording` | 45.91 s / 45.97 s | **46.15 s** |
| verdict -> `idle` | never, in 330 s of observation | **0.28 s** |
| frozen child | state `T`, never killed | killed in 28 ms, `exit 137` |
| file finalized | no - `raw.mp4` only | yes - `recording.mp4` + `recording.original.mp4` |
| service CPU during | ~100% of a core, indefinitely | back to 1.1% |

---

## 4. Every acceptance criterion, re-run on the merged tree

Shipped defaults throughout: `POST /record/start {"mode":"video","screen":1,"source":"mic",
"mic":"Yealink UH36"}` - no `fps`, so the shipped 30 applies. Service installed from the branch
worktree as the launchd agent `com.agenteyes.recorder` (pid 9208, `agenteyes.dll` sha256
`0076228c...5bf9147c`), which is the only process holding the Screen Recording grant. A new pid was
required and proven after each install. The plist carries `ProcessType Interactive` (#114).

The screen was LOCKED for every run (`CGSSessionScreenIsLocked = Yes`), so the captured frames are
wallpaper. That is what the display was compositing; it changes what the frames show, not whether
frames were produced, and every criterion here is about output being produced or not.

### AC1 - a frozen capture ends the recording

**Expected:** within 60 s of a `SIGSTOP`, `/status` reports a state that is not `recording`, and
names the fault.
**Actual: PASS.** `runs/ac1-sigstop.txt`.

```
17:25:18.286Z State=recording   Liveness=growing     Bytes=10120501  Fault=None
   (SIGSTOP on capture pid 9577 at 17:25:18.264Z)
17:26:03.360Z State=recording   Liveness=not-growing Bytes=10120501  SinceGrowth=44  Fault=None
17:26:04.409Z State=finalizing  Liveness=faulted     LastStopFailed=True
   Fault='capture stalled: raw.mp4 stopped growing at 10120501 bytes and has not grown for 46s
          (limit 45s); last growth 2026-11-15T17:25:18.154Z'
17:26:04.693Z State=idle        LastStopFailed=True  (same fault still named)
```

Named **46.15 s** after the injection; idle 0.28 s later. The fault stays named through idle.

### AC2 - a dead capture ends the recording

**Expected:** same, within 60 s of a `SIGKILL`.
**Actual: PASS.** `runs/ac2-sigkill.txt`. Named **2.16 s** after the injection:

```
17:26:34.824Z State=finalizing Liveness=faulted LastStopFailed=True
   Fault='capture exited: the capture process exited (code 137) while recording;
          raw.mp4 holds 11531069 bytes'
17:26:35.115Z State=idle       LastStopFailed=True  (same fault still named)
```

### AC3 - `/status` carries a liveness signal anyone can read

**Expected:** positive evidence that output is being produced, advancing between two polls 10 s
apart and stopping afterwards. Three polls quoted: healthy, healthy, stalled.
**Actual: PASS.** `CaptureFile`, `CaptureBytes`, `CaptureLastGrowthUtc`,
`CaptureSecondsSinceGrowth`, `CaptureLiveness`:

```
17:25:00.158Z Liveness=growing     Bytes=761       LastGrowth=17:25:00.138Z SinceGrowth=0
17:25:10.223Z Liveness=growing     Bytes=5610516   LastGrowth=17:25:10.147Z SinceGrowth=0
17:25:48.415Z Liveness=not-growing Bytes=10120501  LastGrowth=17:25:18.154Z SinceGrowth=30
```

Bytes advance by 5.6 MB across the ten seconds, then stand still while `SinceGrowth` climbs.

### AC4 - no false positive on a healthy recording

**Expected:** a 10-minute screen+mic recording runs to its own completion, `recording` throughout.
**Actual: PASS.** `runs/ac4-healthy-600s.txt`. **602.4 s**, stopped by `POST /record/stop`, not by
the watchdog.

- 61 polls while recording, **all** `State=recording`; the only liveness values seen are `starting`
  (the first poll) and `growing` (the other 60). No fault at any point.
- `CaptureBytes` **strictly increasing** across all 61 polls, 0 -> 320,481,733.
- Final file `recording.mp4`: 333,115,981 bytes, duration **602.866667 s**, **18,086** video frames
  + 28,228 aac frames, full decode **exit 0**. Manifest `DurationSeconds` 602.87,
  `CapturedSeconds` 602.8, `CaptureFault` null, mux `done` in 1 attempt.
- Load average on the 10-core machine during the run: 6.8 - 8.3.

### AC5 - an unclean end still leaves a playable file

**Expected:** after AC1 and after AC2 the directory holds a file `ffprobe` reads successfully, with
a real duration and frame count. `moov atom not found` is a FAIL.
**Actual: PASS**, for both, with the moov at a named offset and a full decode of every frame.

| | AC1 (SIGSTOP) | AC2 (SIGKILL) |
|---|---|---|
| file | `recording.original.mp4` | `recording.original.mp4` |
| size | 10,120,501 bytes | 11,531,069 bytes |
| **moov** | **@ 28** (733 bytes) | **@ 28** (733 bytes) |
| box order | `ftyp moov moof mdat moof mdat ...` | the same |
| ffprobe duration | 18.666667 s | 18.666667 s |
| frames counted | 560 | 560 |
| `ffmpeg -xerror -f null -` | **exit 0** | **exit 0** |

The moov at offset 28 is `empty_moov` doing its job: the index is written up front, so every prefix
ending on a fragment is a valid MP4. The published `recording.mp4` of each is playable too (AC1's:
66.99 s, 560 video + 3141 aac frames, moov @ 12573416, decode exit 0) - the mux is a clean ffmpeg
run, so it keeps a trailing moov as normal.

### AC6 - the fault is recorded where it can be read later

**Expected:** `manifest.json` records that the capture stopped producing output, and the UTC time it
was detected.
**Actual: PASS.**

AC1's manifest:
```
CaptureFault              = "stalled"
CaptureFaultDetail        = "raw.mp4 stopped growing at 10120501 bytes and has not grown for 46s
                             (limit 45s); last growth 2026-11-15T17:25:18.154Z"
CaptureFaultDetectedUtc   = "2026-11-15T17:26:04.194709Z"
CaptureFaultElapsedSeconds= 66.1
CapturedSeconds           = 18.67      DurationSeconds = 66.84
```
AC2's: `CaptureFault = "exited"`, detail naming exit code 137 and 11531069 bytes,
`CaptureFaultDetectedUtc = "2026-11-15T17:26:34.558935Z"`, `CaptureFaultElapsedSeconds = 22`.

`CapturedSeconds` 18.67 against `DurationSeconds` 66.84 is #113's honest-duration rule: the
recording is not recorded as longer than its file.

### AC7 - the failure is reported when it happens, not at stop

**Expected:** written to the application log at detection time with the elapsed seconds, quoted
alongside the clock time.
**Actual: PASS.** Log clock is local (UTC-5); both are in the line.

```
12:26:04.194 [pid 9208] [ERROR] [RecordingService] CAPTURE FAULT at elapsed=67.3s
  (detected 2026-11-15T17:26:04.194Z) dir=.../2026-11-15_122456_video - capture stalled: raw.mp4
  stopped growing at 10120501 bytes and has not grown for 46s (limit 45s); last growth
  2026-11-15T17:25:18.154Z - ending the recording now and keeping what was captured

12:26:34.559 [pid 9208] [ERROR] [RecordingService] CAPTURE FAULT at elapsed=23.3s
  (detected 2026-11-15T17:26:34.558Z) dir=.../2026-11-15_122611_video - capture exited: the capture
  process exited (code 137) while recording; raw.mp4 holds 11531069 bytes - ending the recording now
```

Written at the same millisecond as `PublishVerdict`, which is the instant `/status` starts naming it.

### AC8 - Windows and Linux still record

**Expected:** the watchdog does not end a healthy recording on the other two platforms; the existing
capture argument tests pass and the proof names the files; the watchdog is unit-tested with a fake
clock and a fake growth source if it can be.
**Actual: PASS.**

- Linux argument tests: `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs` -
  `X11VideoCapture_ScreenAndMic_ExactCommandLine`,
  `X11VideoCapture_WithPreview_FragmentsTheRecordingNotThePreview`,
  `X11VideoCapture_NoMic_HasNoAudioInputOrCodec`, and the region/preview cases. Whole suite 320
  passed / 1 skipped.
- Windows argument guard: `tests/Shared/CaptureWatchdogTests.cs` -
  `FragmentedOutput_IsOnTheWindowsCaptureImmediatelyBeforeTheOutputPath`. The Windows suite itself
  cannot build on a Mac, so CI is the Windows evidence - section 6.
- macOS argument tests: `tests/AgentEyes.Tests.Mac/MacFfmpegArgsTests.cs` -
  `AvfVideoCapture_WholeScreen_ExactCommandLine` and the #114 topology tests.
- Faked clock and growth source: `tests/Shared/CaptureWatchdogTests.cs` (21 facts) and
  `tests/Shared/CaptureWatchdogWiringTests.cs` (8). Nothing in either launches ffmpeg or waits in
  real time.

### AC9 - the gate

**Expected:** mac and linux Core builds succeed with 0 errors, both suites report Failed: 0, and the
Windows build and suite pass on a Windows seat.
**Actual: PASS.**

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac
  -> Build succeeded.  1 Warning(s)  0 Error(s)      (the pre-existing AlwaysOnEngine CS8602)
dotnet test  tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release
  -> Passed!  Failed: 0, Passed: 313, Skipped: 0, Total: 313
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux
  -> Build succeeded.  1 Warning(s)  0 Error(s)
dotnet test  tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
  -> Passed!  Failed: 0, Passed: 320, Skipped: 1, Total: 321
```

The one skipped Linux test is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, which
only runs on a real Linux host. `AgentEyes.sln` contains the WPF app and cannot build on a Mac, so
the CLAUDE.md solution-wide gate is the Windows seat's and CI's - section 6.

### AC10 - unit tests for the watchdog itself

**Expected:** with the clock and the growth source faked - it fires when growth stops, it does not
fire while growth continues, and it does not fire during the start-up window before the first bytes.
**Actual: PASS.** In `tests/Shared/CaptureWatchdogTests.cs`:
`Stall_GrowthStops_FiresStalledInsideTheWindow`,
`Healthy_FileGrowsOnlyEveryFortySeconds_NeverFaults`,
`Startup_NoBytesYet_InsideTheWindow_DoesNotFireAndReportsStarting`,
`Startup_SlowFirstOutputThenGrowth_DoesNotFire`, `NeverStarted_AnEmptyFileIsNotOutput`,
`NeverStarted_NoOutputPastTheStartupWindow_Fires`, `Exited_ProcessEnds_FiresAtTheNextCheck`,
`Gap_LongPauseBetweenChecks_IsNotCountedAsAStall`,
`Fault_IsFinal_GrowthAfterTheDecisionDoesNotClearIt`, and
`Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds` over three offsets. Every
"does not fire" test is paired with a "fires" test through the same harness, so a watchdog that
never fired could not pass the set.

---

## 5. The guards still bite - 14 mutants on the merged tree

`runs/mutation.txt`. Every mutant is reverted afterwards and the tree ends at **313 / 0**. Round
one's A-F and round two's W-G are re-run exactly as they were defined; B2 and N are this rebase's.

| Mutant | Tests failed | Named |
|---|---|---|
| A - watchdog never fires | 14 | every `Stall_*`, `NeverStarted_*`, `Exited_*`, `Gap_*`, `Fault_IsFinal_*`, and 5 wiring tests |
| B - FragmentedOutput returns nothing | 3 | `AvfVideoCapture_WholeScreen_ExactCommandLine`, `AvfVideoCapture_WithPreview_*`, `FragmentedOutput_IsOnTheWindowsCapture*` |
| C - gaps counted as stalls | 2 | `Gap_LongPauseBetweenChecks_*`, `Gap_ThenNoGrowth_*` |
| D - no start-up window | 4 | `Startup_*` x2, `NeverStarted_NoOutputPastTheStartupWindow_Fires`, `Healthy_FileGrowsOnlyEveryFortySeconds_*` |
| E - stall ignores the timeout | 8 | `Stall_*` x4, `Gap_ThenNoGrowth_*`, 3 wiring tests |
| F - behind never recorded | 2 | `BehindAtStop_JustPastTheTolerance_IsAFault`, `BehindAtStop_TheEvidenceRecording_*` |
| W - StartVideo never starts the watchdog | 7 | all seven `CaptureWatchdogWiringTests` |
| P - verdict published only after the stop | 2 | `Status_AtDetection_NamesTheFaultBeforeTheWriterStopReturns`, `UserStop_AfterTheVerdictWasPublished_*` |
| K - Abandon drains and quits | 1 | `RecorderAbandonTests.Abandon_OnAProcessThatIgnoresTheQuit_KillsItWithinTheAbandonBound` |
| O - watchdog down after the writers | 1 | `UserStop_TakesTheWatchdogDownBeforeTheWritersStop` |
| V - verdict not in the stop report | 3 | `NextRecording_ClearsThePreviousVerdict`, `WatchdogEnded_DeadCapture_*`, `WatchdogEnded_Recording_IsAFailedStop_*` |
| G - stop after the verdict takes 16 s | 4 | `Defaults_StallPlusOneCheckPlusTheStop_FitInsideTheSixtySecondWindow`, `Stall_Wherever*` x3 |
| **B2 - macOS capture drops FragmentedOutput** | **2** | `AvfVideoCapture_WholeScreen_ExactCommandLine`, `AvfVideoCapture_WithPreview_FragmentsTheRecordingNotThePreview` |
| **N - faulted capture not killed first** | **2** | `OrderStopSteps_WatchdogFaultedTheCapture_KillsTheCaptureFirst`, `OrderStopSteps_TheCameraIsLast_*` |

No mutant survived.

---

## 6. CI

The Windows suite is the only one that cannot be built on this machine, so every Windows number
comes from CI. All three jobs, two runs, at `7b4382e` - the last commit here that touches code:

| Job | run 43742536227 (push) | run 43742541592 (pull_request) |
|---|---|---|
| windows | **2415 / 0** | **2415 / 0** |
| linux | 321 / 0 | 321 / 0 |
| macos | 313 / 0 | 313 / 0 |

CI's Linux job reports 321 passed and 0 skipped where this machine reports 320 passed / 1 skipped:
`LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome` runs only on a real Linux host.

The commit that adds this table is documentation only; its own two runs are quoted in the issue
comment.

---

## 7. What landed ahead of this branch, and whether the two watchdogs fight

They do not, and the reason is that they act on different processes, at different times, watching
different things - each verified running on the merged tree in the AC1 run's own log.

**#113's capture watchdog** judges the LIVE CAPTURE by its output FILE growing (`raw.mp4`, 45 s of
no growth), during the recording, and ends the recording by killing that one process.
**#128's post-processing watchdog** (`Ffmpeg.RunWatched`, `PostProcessStallTimeout`, used by the mux
at `Audio/AudioMix.cs:231`) judges a POST-PROCESSING ffmpeg by its reported output TIME advancing,
after the recording, and fails the stage - with attempts bounded at
`PostRecordingState.MaxMuxAttempts = 3`. **#136's two-pass mux** is the shape of that work: pass one
filters the audio to `audio_processed.wav`, pass two copies the video and encodes the audio into
`recording.mp4.muxing.tmp`, which is then renamed onto `recording.mp4`, so a filter graph is never
paired with `-c:v copy` in one command and the muxing queue cannot deadlock.

The handover between them is sequenced, not raced, and the log says so: the capture watchdog is torn
down at `12:26:04.195` (`StopWatchdog: capture watchdog stopped`) and the mux stage starts at
`12:26:04.488`, 293 ms later. No capture-watchdog timer is armed while a mux ffmpeg runs, so there
is no window in which both could rule on one process. Nor do they watch the same file: `raw.mp4` is
the capture watchdog's subject and the mux's INPUT; the mux's own output is the `.muxing.tmp`.
Each mux child is registered separately (`FfmpegChildRegistry Register: pid 9686 ... owner pid
9208`, then `9689`) under #128's orphan accounting, which the capture never enters.

The one place they genuinely meet is the file itself, and that is the merge working rather than
fighting: the fragmented MP4 #113 leaves behind after a kill is what #136's pass two copies with
`-c:v copy`. Measured on both fault runs - mux `done` in **1** attempt, 1.2 s, and the published
`recording.mp4` carries all 560 captured frames plus the full mic track.

Inherited and exercised on the merged tree, from the AC1 run: `MuxLock TryAcquire ... acquired by
pid 9208` and the re-entrant `depth 2` (#128 D1), `MuxCoordinator Reconcile` (#128 D2),
`PostRecordingState NoteStarted stage=mux` with `Attempts: 1` in the manifest (#128 bounded
retries), `FfmpegChildRegistry Register`/`Unregister` for both mux children (#128), and
`AudioMix MuxInTwoPasses` with the two command lines and the rename (#136).

---

## 8. Housekeeping

- The service is left installed from this branch, idle and healthy (`/health` ok). It is not on main.
- Recordings kept: `2026-11-15_122456_video` (AC1), `2026-11-15_122611_video` (AC2),
  `2026-11-15_122713_video` (AC4 healthy), plus the pre-fix diagnostic runs
  `2026-11-15_120112_video`, `2026-11-15_120810_video`, `2026-11-15_121304_video`,
  `2026-11-15_121934_video`, `2026-11-15_122333_video`. Nothing was deleted.
- No stray ffmpeg of this seat is running: every injected child was reaped, by the product or by
  hand. `ps -Ao pid,state,command | grep [f]fmpeg` lists no ffmpeg at all - its only two hits are
  unrelated shells a day old whose own command line contains the word, which is why `pgrep -f
  ffmpeg` is the wrong instrument here.
- All mutants reverted; `git diff` clean against the committed tree, suites green.
