# Issue #113 - QA round two (targeted re-verification) - FAIL on check 6 (Windows CI red)

QA seat on devlinux, 2026-11-14. Branch `issue-113-capture-watchdog` at `89e1593` (code `41b8d24`; the
diff `41b8d24..89e1593` is docs only - `git diff --name-only 41b8d24 89e1593 | grep -v ^docs/` is
empty). Round one's report (`qa-report.md`) and its `qa-runs/` evidence are untouched. Round-two raw
evidence: `qa-runs/round2/`.

**Verdict: FAIL.** The defect round one failed is FIXED and verified at runtime (checks 1-5 pass). But the
PR's **Windows CI job is red on the head commit**: 2 of 2,330 tests fail, both guard tests that round
two's own changes broke and that were never run (the developer did not run Windows and the round-two
comment does not report the CI result). A red gate cannot go to the review gate.

## Build under test (established, not trusted)

- On arrival the service was pid 2910064, `agenteyes.dll` sha256 `5116697b...2e167a3c3542` - matching
  the developer's claim.
- QA then **reinstalled from the branch itself**: `scripts/linux/install.sh` from the clean worktree at
  `89e1593` (`git status --short` empty), 13:38 EST. **Service pid 3033290, `agenteyes.dll` sha256
  `77586071b7e2921fb131419b34743ae0d305b4d2469f9dabbd6a99865298284e`.** The hash differs from the
  developer's because the build is not byte-reproducible; the code commit is the same.
- The binary contains the round-two `PublishVerdict` log string (`strings -el agenteyes.dll`), and
  every service log line quoted below carries `[pid 3033290]`.

## Machine load (stated, because it matters here)

1-minute load average on 32 cores: **27.6-45.6** during the SIGSTOP run (`round2/stop/load.txt`),
17-47 around it, other seats' work on the box. That is heavy. It made the SIGSTOP run harsher than
intended (see "An observation" below) - and the window was still clean.

## Check 1 - THE WINDOW IS CLEAN: PASS

`ONGROWTH=1 qa-runs/qa-fault.sh STOP round2/stop` (round one's driver, unchanged), with
`round2/densepoll.py` polling `/status` every ~24 ms from before the start to 13 s after idle.

**What counts as misleading** (`round2/classify.py`): any poll after the recording started in which
`State` is not `recording` (so a client sees the recording as ended) AND (`LastStopFailed` is not
`true` OR `LastCaptureFault` is empty). That is exactly the round-one state
(`finalizing / faulted / LastStopFailed=false / LastCaptureFault=null`), and any idle-and-clean
state after a watchdog stop.

**Instrument check (known-bad input):** fed round one's own `qa-runs/stop3/left.json` body, the same
classifier reports **`MISLEADING polls: 1`** (`round2/classify-knownbad.txt`). It fires.

**Result** (`round2/stop/classify.txt`):

```
polls total=3591 errors=0 first=2026-11-14T18:39:29.995Z last=2026-11-14T18:41:05.909Z
  ('idle', None, False, False): 41            first 18:39:29.995Z   (before the start)
  ('recording', 'starting', False, False): 344 first 18:39:31.051Z
  ('recording', 'growing', False, False): 168  first 18:39:42.557Z
  ('recording', 'not-growing', False, False): 2464 first 18:39:44.578Z
  ('finalizing', 'faulted', True, True): 50    first 18:40:52.581Z
  ('idle', None, True, True): 524              first 18:40:53.746Z
polls after the recording ended: 574; of them finalizing=50 idle=524
MISLEADING polls after the recording started: 0
```

**0 misleading of 574 post-recording polls, 50 of them inside the `finalizing` window.** The presence
is the 50 `finalizing` polls: the instrument was inside the window it is judging, 24 ms apart. (The
developer's 0.29 s timeline landed 2 there.) No other class of post-recording state exists in the run.

The sequence:

```
healthy   18:39:42.557Z recording growing      LastStopFailed=False LastCaptureFault=None
injected  18:40:05.287Z SIGSTOP into 3047499
stalled   18:40:15.532Z recording not-growing  SinceGrowth=8  LastStopFailed=False LastCaptureFault=None
last rec  18:40:52.557Z recording not-growing  SinceGrowth=44 LastStopFailed=False LastCaptureFault=None
VERDICT   18:40:52.567Z (service log 13:40:52.568 [pid 3033290] PublishVerdict)
first     18:40:52.581Z finalizing faulted SinceGrowth=46 LastStopFailed=True
                        LastCaptureFault="capture stalled: raw.mp4 stopped growing at 81303 bytes and has not
                        grown for 46s (limit 45s); last growth 2026-11-14T18:40:06.559Z"
                        LastStopError="capture watchdog: capture stalled: raw.mp4 stopped growing at 81303 by..."
mid       18:40:53.162Z finalizing faulted LastStopFailed=True LastCaptureFault="capture stalled: ..."
idle      18:40:53.746Z idle LastStopFailed=True LastCaptureFault="capture stalled: ..." (same)
end       18:41:05.980Z idle LastStopFailed=True
```

Service log (`round2/stop/service-log.txt`, EST):

```
13:40:52.568 [pid 3033290] [INFO] [RecordingService] PublishVerdict: /status now reports LastStopFailed=true, LastCaptureFault="capture stalled: raw.mp4 stopped growing at 81303 bytes ..."
13:40:52.569 [pid 3033290] [ERROR] [RecordingService] CAPTURE FAULT at elapsed=80.0s (detected 2026-11-14T18:40:52.567Z) ...
13:40:52.638 [pid 3033290] [WARN] [FfmpegRecorder] Abandon: .../raw.mp4 - capture stalled: ... - killing the capture process now
13:40:53.275 [pid 3033290] [INFO] [FfmpegRecorder] Abandon: .../raw.mp4 ended in 636ms (killed, exit 137, last reported position 2.64s)
13:40:53.740 [pid 3033290] [ERROR] stop: FAILED dir=.../2026-11-14_133931_video dur=80.0s manifest=on disk - capture watchdog: capture stalled: ...
```

Timings: State left `recording` and the fault was named **at the same poll, 47.29 s after the SIGSTOP**
(the verdict at 47.28 s; the driver's own coarser loop saw it at 47.34 s / named 47.43 s). Verdict to
first post-verdict poll: 14 ms. Verdict to idle: **1.18 s** at load ~28, of which the kill took 636 ms
(round one: ~13.5 s).

## Check 2 - LastStopFailed=True FROM THE FIRST POST-VERDICT POLL: PASS

First post-verdict poll **18:40:52.581Z**, 14 ms after the verdict: `State=finalizing
CaptureLiveness=faulted LastStopFailed=True LastCaptureFault="capture stalled: ..."`. The last poll
before it (18:40:52.557Z) is still `recording`. Every one of the 574 post-recording polls has
`LastStopFailed=True` (SIGKILL run: all 823, below).

## Check 3 - THE WIRING MUTANT NOW FAILS: PASS

`round2/mutation.txt`. `StartWatchdog(_video!);` replaced by a comment in `RecordingService.StartVideo`:

```
=== W-StartWatchdog-deleted
-                StartWatchdog(_video!);
+                // QA-MUTANT-W
  Failed ...CaptureWatchdogWiringTests.WatchdogEnded_DeadCapture_IsNamedAtTheNextCheck
  Failed ...CaptureWatchdogWiringTests.Status_AtDetection_NamesTheFaultBeforeTheWriterStopReturns
  Failed ...CaptureWatchdogWiringTests.WatchdogEnded_Recording_IsAFailedStop_AbandonedNotDrained_WithTheManifestFields
  Failed ...CaptureWatchdogWiringTests.UserStop_AfterTheVerdictWasPublished_CarriesTheVerdict
  Failed ...CaptureWatchdogWiringTests.UserStop_TakesTheWatchdogDownBeforeTheWritersStop
  Failed ...CaptureWatchdogWiringTests.StartVideo_StartsTheWatchdog_OnTheCaptureFile
  Failed ...CaptureWatchdogWiringTests.NextRecording_ClearsThePreviousVerdict
Failed!  - Failed: 7, Passed: 295, Skipped: 0, Total: 302
```

Round one: 294 / 0 green on the same mutation. Now 7 named failures.

**Restored** (same file): `git status --short -- src tests` empty, `git diff --stat` empty,
`Passed! - Failed: 0, Passed: 302, Skipped: 0, Total: 302`.

## Check 4 - THE WINDOW TEST MODELS THE STOP: PASS

`tests/Shared/CaptureWatchdogTests.cs:75` `Defaults_StallPlusOneCheckPlusTheStop_FitInsideTheSixtySecondWindow`
now asserts `DefaultStallTimeout (45) + Interval (2) + FfmpegRecorder.AbandonExitWait (3) = 50 <= 60`.
The stop term is read from the product constant, not a test copy.

Would it fail if the stop cost came back? QA tried both ways the cost can return:

- **G - the bound raised to round one's 16 s** (`AbandonExitWait = 16`): **4 failed** - the window
  test itself plus the three `Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds` cases.
- **K - `Abandon` calls `Stop()` again** (round one's behaviour, constant unchanged): the window test
  alone would NOT see this - it reads a constant. It is caught by
  `RecorderAbandonTests.Abandon_OnAProcessThatIgnoresTheQuit_KillsItWithinTheAbandonBound`, which runs
  the real `Abandon` on a process that ignores the quit: **1 failed, 13 s run**. The two tests together
  hold the window; neither alone does.

Non-blocking note: the test's comment says the service is idle "once the abandon returns, at most
VerdictToIdle later". Measured verdict-to-idle was 1.18 s against a 636 ms kill - the probe and
manifest after the kill are not in the 3 s budget. With 10 s of slack under 60 s this does not matter
today, but the comment overstates what the constant bounds.

## Check 5 - NOTHING THAT PASSED HAS REGRESSED: PASS (spot checks)

- **SIGKILL** (`round2/kill/`, load 13-17): injected 18:44:05.002Z; first `finalizing` dense poll
  18:44:06.666Z (**1.66 s**) `finalizing faulted LastStopFailed=True LastCaptureFault="capture exited:
  the capture process exited (code 137) while recording; raw.mp4 holds 476035 bytes"`. 1,756 polls,
  **14 in `finalizing`, 0 misleading** (`kill/classify.txt`).
- **Fault files still playable** (`round2/inspect-*.txt`):
  SIGSTOP `2026-11-14_133931_video/recording.mp4` - ffprobe 2.07 s, 60 video / 90 audio frames, manifest
  `CaptureFault "stalled"`, `CapturedSeconds 2.07`. SIGKILL `2026-11-14_134343_video/recording.mp4` -
  18.73 s, 560 video / 873 audio frames, manifest `CaptureFault "exited"`. Post-processing `done` on both.
- **Healthy 10-minute run**: `round2/healthy600/` - round one's `healthy-run.sh 600`, unchanged,
  screen + mic, 18:44:38Z-18:54:42Z. **59 polls, every one `State=recording CaptureLiveness=growing`,
  `CaptureBytes` strictly increasing 202,299 -> 14,181,101**; `polls=59 all-recording-and-advancing=yes`.
  After stop: `State=idle LastStopFailed=False LastCaptureFault=None`. `recording.mp4` 605.13 s, 18,152
  video frames; manifest `CaptureFault null`. **Machine load: 1-minute average 14.57-47.77 on 32 cores**
  (`healthy600/load.txt`, 21 samples every 30 s) - heavier than round one (3-4) or the developer (5.9-9.3),
  so this no-false-positive result is the strongest of the three.
- **A normal recording still keeps its moov**: the healthy `recording.mp4` top-level boxes are
  `ftyp free mdat moov`, `moov` present, 0 `moof` (`inspect-2026-11-14_134437_video.txt`).

## Check 6 - THE GATE AND CI: FAIL

Local (QA ran these, `round2/gate-*.txt`):

| Command | Result |
|---|---|
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64` | Build succeeded, 0 Error(s), 1 Warning (pre-existing) |
| `dotnet test tests/AgentEyes.Tests.Linux/...` | **Passed 302, Failed 0** |
| mac Core build | Build succeeded, 0 Error(s) |
| `dotnet test tests/AgentEyes.Tests.Mac/...` (on Linux) | 259 / 1 - the pre-existing `MacPathsTests.MachineLocalAppData_OnMac_IsTheApplicationSupportFolder`, not chased |

PR #137 CI on head `89e1593` (runs 43627540056 and 43627545011, both identical):

| Job | Result |
|---|---|
| linux | pass - `Passed! Failed: 0, Passed: 302, Total: 302` |
| macos | pass - `Passed! Failed: 0, Passed: 260, Total: 260` |
| **windows** | **FAIL - `Failed! Failed: 2, Passed: 2328, Total: 2330`** |

Round one's head `a65a4fc` was green on all three (windows 2322 / 0). The two failures are both
introduced by round two:

1. **`AgentEyes.Tests.ManifestWriterIlTests.EveryFileWriteInTheProduct_IsAPinnedCallSite`**
   (`tests/AgentEyes.Tests/ManifestWriterIlTests.cs:328`). The IL inventory of file writes (#155) finds
   a new, unpinned call site: `agenteyes.dll!AgentEyes.Video.FfmpegRecorder::Abandon ->
   System.IO.File::WriteAllText x1`. It is the new `Abandon`'s
   `File.WriteAllText(_logPath, _stderr.ToString())` (`src/AgentEyes.Core/Video/FfmpegRecorder.cs:334`), the
   ffmpeg stderr log, not a manifest. Round one's `Abandon` called `Stop()` and so added no write site.
   The pinned list is at `ManifestWriterIlTests.cs:249-250` next to the `Start`/`Stop` entries for the same
   log. The guard is doing its job: a new write needs a review decision.
2. **`AgentEyes.Tests.SessionManifestTests.StartVideo_StartsEveryWriterInsideTheSequence`**
   (`tests/AgentEyes.Tests/SessionManifestTests.cs:247`): "StartVideo calls FfmpegRecorder.Start( outside a
   RecordingStartStep". The guard searches `StartVideo`'s source for `FfmpegRecorder.Start(` (line 245).
   Round two replaced that call with `_seams.StartScreen(` (`RecordingService.cs`, inside the "video" step);
   the real call moved to `RecordingServiceSeams.cs:57`. With zero occurrences, `EveryOccurrenceIsAStartStep`
   returns false by design ("an occurrence that vanished entirely is a finding, not a pass", line 324). The
   product ordering is still right - `_seams.StartScreen(` IS inside `new RecordingStartStep("video", ...)` -
   but the guard can no longer see the screen writer, and it says so.

Both are fail-CLOSED guards working as designed. They are in the Windows-only suite, so the Linux and
mac suites (302/0, 260/0) cannot see them, and the developer did not run Windows.

## What round three owes

- Pin `FfmpegRecorder::Abandon -> System.IO.File::WriteAllText x1` in `ManifestWriterIlTests` with the file it
  writes (the ffmpeg stderr log).
- Point `StartVideo_StartsEveryWriterInsideTheSequence` at the call that `StartVideo` actually makes
  (`_seams.StartScreen(`) so that it keeps guarding the screen writer, and do not just delete the needle.
  Optionally guard that `RecordingServiceSeams.Production.StartScreen` is the only path to `FfmpegRecorder.Start`.
- Windows CI green on all three jobs, with the three counts quoted.
- No runtime re-run is needed for these two fixes unless product code changes. Checks 1-5 above stand
  on `41b8d24`'s product code.

## An observation, not a defect of this issue

Under the load of this run (27-45 on 32 cores) the HEALTHY capture starved before the fault was
injected: `raw.mp4` reached 1,241 bytes at 18:39:42.557Z and did not grow again for ~24 s (`healthy2`
at 18:39:53Z: `not-growing`, `SinceGrowth=10`). The session ran 80 s and the file holds **2.07 s** of
video. The watchdog correctly did NOT fire on that 24 s gap (inside 45 s), and it did not report the
lost time either. This is the partial starvation the Implementation Lead's cross-issue comment
describes, reproduced live on this branch build, and it is out of scope for #113.

## Housekeeping

- Service left running on **this branch build `89e1593` (code `41b8d24`), pid 3033290, dll sha256
  `77586071...298284e`**, idle, `/health` ok. It is NOT on main.
- New recordings, kept: `2026-11-14_133931_video` (SIGSTOP), `2026-11-14_134343_video` (SIGKILL),
  `2026-11-14_134437_video` (healthy 600 s). Nothing under `~/Videos/AgentEyes` was deleted; `2026-11-14_102532_video`
  was not touched.
- No QA ffmpeg is left running: `pgrep -c ffmpeg` = 0 at the end. The dense pollers and load samplers have exited.
