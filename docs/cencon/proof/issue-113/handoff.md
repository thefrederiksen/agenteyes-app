# Issue #113 - handoff to QA: the capture watchdog

## PASS 5 (2026-11-16) - after the Review Gate rejected `ad21557`. READ THIS FIRST.

Full report: `pass5/report.md`. Head `00c9df9`; `main` `d0d0a27` (#156) and `277a9bc` (#118) are
merged in. What changed, and what QA should check:

1. **D3 resolved** (`c7e2339`, the merge commit holds only the resolution): `ProcessId` on
   `IScreenRecorder` (one definition, `FfmpegRecorder`'s); #113's liveness type renamed
   `CaptureOutputLiveness` because #156's `CaptureLiveness` shadowed it. Check: the merge diff
   against `d0d0a27` touches no #156 logic.
2. **The gate's scenario now declines** - measured on a real Windows capture with its pre-#156
   control (`pass5/report.md`, item 2). QA on macOS and Linux: re-run the gate's own scenario on a
   real capture; expected the same decline, no rename, no `recording.mp4`, `/status` growing past 60 s.
3. **The watchdog's fail-open catch is gone** (`9f45b33`, `00c9df9`). Check: `WatchdogFailOpenTests`
   and the four mutations in the report.
4. **The fragmented container started the video 0.7-1.0 s late on Windows** (0.067 s at 30 fps on a
   synthetic source). Fixed with `+delay_moov` (`3debd3d`). **This changes the capture command line
   on all three platforms**, so on macOS and Linux QA must re-run criteria 1, 2 and 5 (frozen and
   killed captures, and the file they leave) and check `start_time` of both streams in raw.mp4 and
   recording.mp4 (expected 0.000 and 0.000).
5. **Windows recorded, stalled and killed** with this code for the first time; the rename on Windows
   fails with a sharing violation, measured.
6. Every file cited is decoded in full to exit 0 with a frame read from the last second.

I believe this pass is finished. What it does not cover is listed in the report.

---

Branch `issue-113-capture-watchdog`, cut from `main` at `af0ddb1`. Developer seat on `devlinux`,
2026-11-14. Platform exercised at runtime: **Linux only** (x11grab + PulseAudio). macOS and Windows
are covered by the shared unit tests and the argument tests only - see "What is NOT covered".

## ROUND THREE (2026-11-14) - the two Windows guard tests, test-only

QA round two (`qa-report-round2.md`) passed checks 1-5 at runtime and failed check 6: Windows CI on
`89e1593` was 2 failed / 2328 passed / 2330. Both failures were guard tests that worked. Each one is
brought up to date below, and is still exactly as strict as it was or stricter. **No product code
changed** in this pass: `git diff 9fb65d9 843605f --name-only` lists only the two test files.
Commit `843605f`. Evidence: `runs/round3/`.

### Guard 1 - `ManifestWriterIlTests.EveryFileWriteInTheProduct_IsAPinnedCallSite`

- **What it protects against:** a file-write call site in the compiled product that nobody
  reviewed. In particular it catches a direct `manifest.json` writer that bypasses `ManifestStore`.
- **Is the new write intended? Yes, but it is not a manifest write.** The brief for this pass said
  `Abandon` writes the manifest at detection. The code does not do that.
  `FfmpegRecorder.cs:334` is `File.WriteAllText(_logPath, _stderr.ToString())`. It writes the ffmpeg
  stderr log, which is the same file `Stop` writes at line 202 and failed starts write at line 138.
  Round two's `Abandon` no longer calls `Stop()`, so it writes that log itself. The fault reaches
  `/status` in memory, through `PublishVerdict`. It goes to disk through `RecordingService::Stop ->
  ManifestStore::Update`, which is already pinned. The second IL guard
  (`EveryCallOfTheCanonicalManifestPath_IsPinned`) did not move, and it is green.
- **The update:** one exact entry with an exact count, next to `Start` and `Stop`:
  `agenteyes.dll!AgentEyes.Video.FfmpegRecorder::Abandon -> System.IO.File::WriteAllText x1`.
  The comment says which file it writes. Nothing else in the pin changed. A second write in `Abandon`
  (x2) or a write anywhere else still fails.
- **It still bites (mutant M1, `runs/round3/mutant-m1.diff`):** this adds a new method that writes
  `manifest.json` directly with `File.WriteAllText`. On Windows CI run 43629840734 it **FAILED by
  name**:
  `Failed AgentEyes.Tests.ManifestWriterIlTests.EveryFileWriteInTheProduct_IsAPinnedCallSite`,
  with `FOUND: agenteyes.dll!AgentEyes.Video.FfmpegRecorder::MutantWriteManifest -> System.IO.File::WriteAllText x1`.
  The two source-scan guards fired as well (`ManifestWriterTests.NoStatementAnywhere_WritesTheManifestFileDirectly`
  and `OnlyPinnedFiles_NameManifestJsonInCode`). Result: 3 failed / 2327 passed / 2330
  (`runs/round3/ci-mutant-m1-3b0f48e.txt`).

### Guard 2 - `SessionManifestTests.StartVideo_StartsEveryWriterInsideTheSequence`

- **What it protects against:** a capture writer in `StartVideo` that starts outside a
  `RecordingStartStep`. Such a writer runs before the manifest is on disk, and outside the rollback
  that stops writers already started (issue #155).
- **Is the new call path intended? Yes.** Round two starts the screen writer with
  `_seams.StartScreen(` (`RecordingService.cs:732`), inside the `"video"` `RecordingStartStep`
  (line 730). The production seam is `FfmpegRecorder.Start(...)` (`RecordingServiceSeams.cs:55-57`).
  The order is unchanged. Only the spelling the needle looked for changed.
- **The update:** an exact needle replaced by another exact needle, with two checks added:
  1. Needle `FfmpegRecorder.Start(` -> `_seams.StartScreen(`. It is still checked by
     `EveryOccurrenceIsAStartStep`, so it **must be found**, and every occurrence must be inside a step.
  2. The old spelling is still watched. A `FfmpegRecorder.Start(` in `StartVideo` that is outside a
     step fails (`NoOccurrenceOutsideAStartStep`). That check allows zero occurrences, so it only
     runs next to check 1, which requires a presence.
  3. The seam's production default must still be
     `FfmpegRecorder.Start(capture, mic, fps, crf, outPath, preview, screenNumber)`. This keeps the
     new needle tied to the real screen writer.
- **It still bites (mutant M2a, `runs/round3/mutant-m2a.diff`):** this moves the
  `_video = _seams.StartScreen(...)` call out of the `"video"` step to just before it. Windows CI run
  43629843142 **FAILED by name**:
  `Failed AgentEyes.Tests.SessionManifestTests.StartVideo_StartsEveryWriterInsideTheSequence`, with
  `StartVideo calls _seams.StartScreen( outside a RecordingStartStep`. Result: 1 failed / 2329 passed
  / 2330 (`runs/round3/ci-mutant-m2a-7db814f.txt`).
- **The added check bites too (mutant M2b, `runs/round3/mutant-m2b.diff`):** this leaves the seam
  call in its step and adds a direct `FfmpegRecorder.Start(...)` in `StartVideo` outside any step.
  Windows CI run 43629845209 **FAILED by name**:
  `Failed AgentEyes.Tests.SessionManifestTests.StartVideo_StartsEveryWriterInsideTheSequence`, with
  `StartVideo calls FfmpegRecorder.Start( outside a RecordingStartStep`. Two side effects of this
  mutant, both expected: it starts a real ffmpeg inside the wiring tests, so the Linux and macOS jobs
  failed the `CaptureWatchdogWiringTests`, and on Windows `PreviewTapTests.Dispose_WhileThePublisherIsWedged_...`
  failed with `Access to the path 'screen.jpg' is denied`. Windows: 2 failed / 2328 passed / 2330
  (`runs/round3/ci-mutant-m2b-6707a72.txt`). Neither failure shows up on the fixed commit.

The mutants ran on throwaway branches (`qa-mutant-113-m1`, `-m2a`, `-m2b`) cut from `843605f`. Each
branch held one commit, and all three were deleted after their runs. The diffs are committed in
`runs/round3/` so the mutations can be rebuilt.

### CI on the fixed commit `843605f` - green on all three jobs

| Job | Run 43629772758 (pull_request) | Run 43629765564 (push) |
|---|---|---|
| windows | 2330 passed / 0 failed / 2330 | 2330 / 0 / 2330 |
| linux | 302 / 0 / 302 | 302 / 0 / 302 |
| macos | 260 / 0 / 260 | 260 / 0 / 260 |

Before, on `89e1593`, Windows was 2328 / 2 / 2330. The total is the same 2330 because no test was
added or removed (the counts are in `runs/round3/ci-fix-843605f-*.txt`). Local gate on devlinux:
Core `net8.0` build 0 errors, Linux suite 302 / 0, macOS-configuration build 0 errors.

### What is NOT covered by this pass

- No runtime re-run. Product code did not change, as QA's round-two report allows.
- The Windows suite cannot build on Linux (the WindowsDesktop SDK is missing), so every Windows
  result above comes from CI, not from a local run.
- Guard 2 is still a source-text guard. It sees spellings, not behaviour. The behavioural proof
  of the start order is `RecordingStartSequenceTests`, and this pass did not touch it.

## ROUND TWO (2026-11-14) - what changed after QA failed criterion 1

QA round one (`qa-report.md`, gate ruling on the issue): a frozen capture was **detected at 46 s but
named on `/status` only at 59.4-60.2 s**. For the ~13.5 s between, `/status` read
`State=finalizing CaptureLiveness=faulted LastStopFailed=false LastCaptureFault=null` - a clean stop,
stated after the service had decided otherwise. And deleting `StartWatchdog(_video!);` from
`StartVideo` left the suite 294 / 0 green. Both are fixed; the watchdog itself is unchanged.

Code: commit `41b8d24` on this branch. Round-two raw evidence: `runs/round2/`.

### What changed

| Change | Where |
|---|---|
| **The verdict is published at detection.** `PublishVerdict` runs inside the same `_lock` that sees the fault: it sets `LastCaptureFault` and a provisional stop report that leads with the verdict, so `LastStopFailed=true` and `LastStopError="capture watchdog: capture stalled: ..."` from that instant. The final stop report replaces it, with the same verdict leading. | `RecordingService.OnWatchdogTick`, `PublishVerdict` |
| **`Abandon` kills at once.** It no longer calls `Stop()` (5 s drain + 8 s quit wait on a process already judged dead). It kills the process tree and waits at most `AbandonExitWait` (3 s) for it to be reaped, and **throws** if it is not - a capture that may still be writing is a failed stop. It shares nothing with `Stop()` any more. | `FfmpegRecorder.Abandon`, `AbandonExitWait` |
| **A seam for a fake capture.** `IScreenRecorder` (what the service needs from the capture) and `RecordingServiceSeams` (monitor, folder, capture start, watchdog clock, watchdog timer, duration probe, post-processing). The public constructor uses `RecordingServiceSeams.Production`, which is exactly what the service called before; only tests pass anything else. `FfmpegRecorder.ForStartedProcess` lets a test put a non-ffmpeg process under the recorder. | `Video/IScreenRecorder.cs`, `RecordingServiceSeams.cs` (new) |
| **Wiring tests**, platform-neutral, linked into all three suites. | `tests/Shared/CaptureWatchdogWiringTests.cs` (new) |
| **The window test counts the stop.** `Defaults_StallPlusOneCheckPlusTheStop_FitInsideTheSixtySecondWindow` budgets `StallTimeout + Interval + AbandonExitWait` (45 + 2 + 3 = 50 s) against 60; the Theory `Stall_WhereverInTheIntervalItBegins_...` adds the same stop to each measured detection. Round one's stop (16 s worst case) gives 63 s, which both reject - see mutant G. | `tests/Shared/CaptureWatchdogTests.cs` |

**I took both of QA's fixes.** (1) alone would have fixed what `/status` says, but left the service
`finalizing` for ~13.5 s on a dead process and left `Abandon` coupled to the `Stop()` that #127
rewrites. (2) alone would have shortened the lie to ~1 s, not removed it.

### The new tests (`CaptureWatchdogWiringTests`, `RecorderAbandonTests`)

They drive the **real** `RecordingService` - `StartVideo`, the watchdog it starts, the verdict, `Stop`,
the manifest on disk - with a capture the test controls: a real file the test grows (the watchdog
stats it through the production `FileSize`), a fake clock, a timer the test fires.

| Test | Asserts (presence, not absence) |
|---|---|
| `StartVideo_StartsTheWatchdog_OnTheCaptureFile` | a watchdog timer exists, armed for 2 s; `/status` `CaptureLiveness=starting`, `CaptureFile=recording.mp4`; after growth `growing`, `CaptureBytes=12345`, re-armed |
| `Status_AtDetection_NamesTheFaultBeforeTheWriterStopReturns` | with the capture's abandon held open (and asserted not returned): `State=finalizing`, `CaptureLiveness=faulted`, `LastStopFailed=true`, `LastCaptureFault` starts `capture stalled: recording.mp4 stopped growing at 200000 bytes`, `LastStopError` contains `capture watchdog: capture stalled`, fault dir and time |
| `WatchdogEnded_Recording_IsAFailedStop_AbandonedNotDrained_WithTheManifestFields` | idle with `LastStopFailed=true`; the final report has a manifest; `Abandon` ran and `Stop` did not; manifest `CaptureFault=stalled`, `CaptureFaultDetail`, `CapturedSeconds=18.73` (probe), `CaptureFaultElapsedSeconds`, `CaptureFaultDetectedUtc`; post-processing queued for that directory |
| `WatchdogEnded_DeadCapture_IsNamedAtTheNextCheck` | exit code 137 -> `capture exited ... (code 137)`, `LastStopFailed=true`, manifest `exited` |
| `UserStop_TakesTheWatchdogDownBeforeTheWritersStop` | event order `watchdog disposed` before `video stop`; no abandon; clean stop; a check fired after the stop acts on nothing |
| `UserStop_AfterTheVerdictWasPublished_CarriesTheVerdict` | a user stop during the watchdog's stop is refused and `/status` still says failed; manifest `stalled` |
| `NextRecording_ClearsThePreviousVerdict` | the next start clears `LastStopFailed` / `LastCaptureFault` |
| `RecorderAbandonTests.Abandon_OnAProcessThatIgnoresTheQuit_KillsItWithinTheAbandonBound` | the REAL `FfmpegRecorder.Abandon` on `sleep 600` (`ping -n 600` on Windows) with stdin redirected: process exited, exit code present, under `AbandonExitWait` (3 s). Round one's Abandon takes ~13 s on it (mutant K: 13 s run) |

### Re-run proof (Linux, devlinux) - re-run, not reasoned

Service: branch commit `41b8d24`, installed with `scripts/linux/install.sh` from this worktree at
18:20:59Z. **Service pid 2910064, `~/.local/lib/agenteyes/agenteyes.dll` sha256
`5116697b919da56424c53cccd40309ff823b2458f4d99e1523142e167a3c3542`** (`runs/round2/service.txt`; the
log lines below carry `[pid 2910064]` and the new `PublishVerdict` line, which main does not have).
Driver: QA's own `qa-runs/qa-fault.sh`, unchanged, plus `runs/round2/timeline.sh` polling `/status`
every ~0.29 s for the whole run.

#### SIGSTOP, `ONGROWTH=1` (`runs/round2/stop/`) - named 46.6 s after the freeze (round one: 59.4-60.2 s)

```
18:21:45.313Z healthy2  State=recording  CaptureLiveness=growing     CaptureBytes=475563 LastStopFailed=False LastCaptureFault=None
                        raw.mp4 grew 475563 -> 513996; injecting now
18:21:46.746Z           injected SIGSTOP into 2911602
18:21:56.815Z stalled   State=recording  CaptureLiveness=not-growing CaptureBytes=513996 SinceGrowth=8  LastStopFailed=False LastCaptureFault=None
18:22:33.319Z           State left recording after 46.571010231s (now finalizing)
18:22:33.322Z left      State=finalizing CaptureLiveness=faulted SinceGrowth=46 LastStopFailed=True
                        LastCaptureFault=capture stalled: raw.mp4 stopped growing at 513996 bytes and has not grown
                        for 46s (limit 45s); last growth 2026-11-14T18:21:47.106Z
18:22:33.397Z           /status first NAMED the fault after 46.649312801s
18:22:34.519Z after     State=idle LastStopFailed=True LastCaptureFault=capture stalled: raw.mp4 ... (same)
                        capture ffmpeg 2911602 is gone
```

The 0.29 s timeline (`stop/timeline.txt`, 263 polls) through the verdict:

```
18:22:33.019Z State=recording  Liveness=not-growing SinceGrowth=44 LastStopFailed=False LastCaptureFault=None
18:22:33.310Z State=finalizing Liveness=faulted     SinceGrowth=46 LastStopFailed=True  LastCaptureFault='capture stalled: raw.mp4 stopped growing at 513996 bytes and'
18:22:33.594Z State=finalizing Liveness=faulted     SinceGrowth=46 LastStopFailed=True  LastCaptureFault='capture stalled: ...'
18:22:33.886Z State=idle                                           LastStopFailed=True  LastCaptureFault='capture stalled: ...'
```

**Polls showing `faulted` or `finalizing` with `LastStopFailed=False` or no fault name: 0 of 263**
(`grep -E "faulted|finalizing" timeline.txt | grep -cE "LastStopFailed=False|LastCaptureFault=None"`).
The check is a count over polls that DID see the post-verdict state - there are two such polls in
`finalizing` and the rest idle, so an empty instrument would show as zero `finalizing` lines, which it
does not.

Service log (local time, EST):
```
13:22:33.115 [pid 2910064] [INFO]  [RecordingService] PublishVerdict: /status now reports LastStopFailed=true, LastCaptureFault="capture stalled: ..."
13:22:33.116 [pid 2910064] [ERROR] [RecordingService] CAPTURE FAULT at elapsed=68.0s (detected 2026-11-14T18:22:33.113Z) ...
13:22:33.212 [pid 2910064] [WARN]  [FfmpegRecorder] Abandon: .../raw.mp4 - capture stalled: ... - killing the capture process now
13:22:33.734 [pid 2910064] [INFO]  [FfmpegRecorder] Abandon: .../raw.mp4 ended in 521ms (killed, exit 137, last reported position 21.74s)
13:22:33.873 [pid 2910064] [ERROR] stop: FAILED dir=.../2026-11-14_132123_video dur=68.0s manifest=on disk - capture watchdog: capture stalled: ...
```

Verdict to idle: **0.77 s** (33.113 -> 33.886), of which the kill took 521 ms. Round one: ~13.5 s.

File (`stop/inspect.txt`): `recording.mp4` 20.73 s, 620 video frames, mux/thumbnail/package done;
manifest `CaptureFault "stalled"`, `CapturedSeconds 20.73`, `CaptureFaultDetectedUtc
2026-11-14T18:22:33.1139502Z`.

#### SIGKILL (`runs/round2/kill/`) - named 1.81 s after the kill (QA round one: 2.13 s)

```
18:23:36.360Z healthy2  State=recording  CaptureLiveness=growing CaptureBytes=475529 LastStopFailed=False
18:23:36.414Z           injected SIGKILL into 2920073
18:23:38.223Z           State left recording after 1.807620476s (now finalizing)
18:23:38.225Z left      State=finalizing CaptureLiveness=faulted LastStopFailed=True
                        LastCaptureFault=capture exited: the capture process exited (code 137) while recording; raw.mp4 holds 475529 bytes
18:23:39.410Z after     State=idle LastStopFailed=True LastCaptureFault=capture exited: ... (same)
```

The first post-verdict poll (`left`, 18:23:38.225Z) already names it, as does `named`. The 0.29 s
timeline in `kill/timeline.txt` did NOT land a poll inside the short `finalizing` window (0
`finalizing` lines), so it is no evidence either way for this run - an empty result, not a clean one.
The two script polls above are the evidence. File: `recording.mp4` 18.73 s, 560 frames; manifest `CaptureFault "exited"`.

#### `LastStopFailed=true` on a watchdog-ended recording

Both runs above: `LastStopFailed=True` from the first post-verdict poll through idle (`after.json`),
and in the unit tests `Status_AtDetection_...` (during the stop) and `WatchdogEnded_Recording_...`
(after it).

#### Healthy 10-minute run (`runs/round2/healthy600/`)

QA's `healthy-run.sh 600`, unchanged, screen + mic, 18:23:48Z-18:33:49Z: **59 polls, every one
`State=recording` and `CaptureLiveness=growing`, `CaptureBytes` strictly increasing** (203,366 ->
14,137,346). Stop: `DurationSeconds 601.12`, then `/status` `State=idle LastStopFailed=False
LastCaptureFault=None`. Final `recording.mp4` **602.73 s, 18,080 video frames**; manifest `CaptureFault`
null. **Machine load during the run: 1-minute load average 5.91-9.34 on 32 cores** (`load.txt`, sampled
every 30 s; other seats were running on the box).

### Service state on devlinux after round two

Left running and healthy on **this branch, commit `41b8d24`, pid 2910064, agenteyes.dll sha256
`5116697b...2e167a3c3542`**, idle, `/health` ok. It is NOT on main. Recordings made in round two are
kept: `2026-11-14_132123_video` (SIGSTOP), `2026-11-14_132315_video` (SIGKILL),
`2026-11-14_132347_video` (healthy 600 s). Nothing under `~/Videos/AgentEyes` was deleted;
`2026-11-14_102532_video` was not touched. No ffmpeg of mine is running (`pgrep -a ffmpeg` empty after
the last run); the `sleep 600` stand-in from the unit test is killed by the test itself.
`AccountEmail` is redacted in the committed `/status` bodies.

### The mutation run (`runs/round2/mutation.txt`, produced by `mutate.py`)

`mutate.py` applies each mutant to the source, runs the Linux suite, lists every failing test by full
name, and restores the file. The name regex now matches `[< 1 ms]` and Theory cases (round one's
missed both), so **the names listed equal the failure counts: 51 names for 51 failures.**

| Mutant | Failed (of 302) |
|---|---|
| **W - `StartWatchdog(_video!);` deleted from `StartVideo`** (QA's wiring mutant; round one: 0 failed) | **7**: every `CaptureWatchdogWiringTests` test that starts a recording - `StartVideo_StartsTheWatchdog_OnTheCaptureFile`, `Status_AtDetection_NamesTheFaultBeforeTheWriterStopReturns`, `WatchdogEnded_Recording_IsAFailedStop_AbandonedNotDrained_WithTheManifestFields`, `WatchdogEnded_DeadCapture_IsNamedAtTheNextCheck`, `UserStop_TakesTheWatchdogDownBeforeTheWritersStop`, `UserStop_AfterTheVerdictWasPublished_CarriesTheVerdict`, `NextRecording_ClearsThePreviousVerdict` |
| P - verdict reaches `/status` only after the stop (round one's behaviour) | 2: `Status_AtDetection_NamesTheFaultBeforeTheWriterStopReturns`, `UserStop_AfterTheVerdictWasPublished_CarriesTheVerdict` |
| K - `Abandon` runs the drain-and-quit `Stop()` (round one's behaviour) | 1: `RecorderAbandonTests.Abandon_OnAProcessThatIgnoresTheQuit_KillsItWithinTheAbandonBound` (the run took 13 s) |
| O - watchdog taken down AFTER the writers stop | 1: `UserStop_TakesTheWatchdogDownBeforeTheWritersStop` |
| V - the verdict left out of the stop report | 3: `WatchdogEnded_Recording_...`, `WatchdogEnded_DeadCapture_...`, `NextRecording_ClearsThePreviousVerdict` |
| G - the stop after the verdict budgeted at round one's 16 s | 4: `Defaults_StallPlusOneCheckPlusTheStop_FitInsideTheSixtySecondWindow` + the 3 Theory cases of `Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds` |
| A - watchdog never fires (round one) | 14 (round one: 9) - the round-one nine plus 5 wiring tests |
| B - no fragmented output (round one) | 3, same names as round one |
| C - gaps counted as stalls (round one) | 2, same |
| D - no start-up window (round one) | 4, same |
| E - stall timeout doubled (round one) | 8 (round one: 5) - the round-one five plus 3 wiring tests |
| F - "behind" never recorded (round one) | 2, same |
| restored | **Passed 302, Failed 0** |

### The gate (round two, devlinux)

| Command | Result |
|---|---|
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64` | Build succeeded, 0 Error(s), 1 Warning (pre-existing `AlwaysOnEngine.cs(901)`) |
| `dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release` | **Passed 302, Failed 0** (round one 294 / 0) |
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac` | Build succeeded, 0 Error(s), same 1 warning |
| `dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release` (on Linux) | Passed 259, Failed 1 - `MacPathsTests.MachineLocalAppData_OnMac_IsTheApplicationSupportFolder`, the pre-existing one that fails identically on main |
| Windows suite | **Not run** here (no WindowsDesktop pack). The new shared file is compiled into it; `RecorderAbandonTests` uses `ping -n 600 127.0.0.1` there. PR CI is the only Windows evidence. |

### Still NOT covered after round two

- **The Windows suite and the Windows app at runtime**, as round one. The Windows `ping` stand-in in
  `RecorderAbandonTests` has not been run anywhere; if CI shows it misbehaving, that is the test, not
  the product.
- **macOS at runtime**, as round one.
- **The kill itself on a frozen capture is measured on Linux only** (521 ms). `Kill(entireProcessTree)`
  on Windows/macOS is .NET's, not ours, but no frozen Windows or Mac capture has been killed by it.
- **The real System.Threading.Timer** is not under test: the wiring tests use a fired-by-hand timer.
  `ThreadingWatchdogTimer` is three lines and the runtime runs above exercise it.
- **The window test still budgets constants.** It is held to the code by `RecorderAbandonTests`
  (Abandon really returns inside `AbandonExitWait`) and by `Status_AtDetection_...` (the name is
  published before the stop), not by measuring a 60 s run. The runtime runs above are the measurement.
- **The CLI `agenteyes video` loop** calls the same `Abandon`, so it now kills too; it was not re-run
  at runtime in round two.
- **`EARLY` / `behind` and `never-started`** were not re-run at runtime in round two (not touched).
- `TranscriptionBacklog.FindPending(RecordingPaths.Root)` runs inside `Status()` in the wiring tests and
  reads the real recordings folder read-only; harmless, but the tests are not hermetic about it.

### PR #127 merges first - what I expect to conflict

`Abandon` no longer calls `Stop()`, so #127's rewrite of the drain and quit does not change what a
watchdog-ended recording does. Expected on rebase:

1. **`RecordingService.StartVideo`, the capture start.** #127 changes the `FfmpegRecorder.Start(...)`
   call to pass `recorderMic`; this branch changed the same call to `_seams.StartScreen(...)`. Resolve
   as `_video = _seams.StartScreen(capture, recorderMic, fps, 23, ffOut, _screenTap, _monitor!.Index);`.
2. **`RecordingService.StartVideo`, the end.** #127 moves `_sw.Restart()` above `StartSession(steps)`;
   this branch has `StartWatchdog(_video!);` right after `_state = "recording";`. Keep #127's order and
   keep `StartWatchdog` after `_state = "recording"`. **If the resolution drops it, mutant W's seven
   tests fail** - which is the point of them.
3. **A compile break with no textual conflict:** #127's `Stop()` reads `video?.InputStartSeconds`, and
   `video` is now an `IScreenRecorder`. Add `double? InputStartSeconds { get; }` to `IScreenRecorder`
   (and a `null` property to `FakeScreenRecorder` in the wiring tests).
4. `FfmpegRecorder.cs`: round two's edits are the class line (`: IScreenRecorder`) and the block after
   `MediaSeconds` (`AbandonExitWait`, `Abandon`, `ForStartedProcess`); #127's hunks are in `Start` and
   `Stop`. I expect it to merge cleanly. Round one's `Start` call-site extraction was considered and
   rejected for exactly this reason.
5. Round one's expected conflicts still apply: `AvfVideoCapture` (one `FragmentedOutput()` line) and
   the `MacFfmpegArgsTests` exact line. Re-run the Mac suite after the rebase.

---

# Round one (unchanged below this line)

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

- **[Round one; closed in round two - see `CaptureWatchdogWiringTests` and mutant W.]** **The wiring in `RecordingService` has no unit test.** Starting the watchdog, the timer re-arm,
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

## Service state on devlinux (round one - superseded by the round-two section at the top)

Left running, idle, on **main `af0ddb1`** (reinstalled with `scripts/linux/install.sh` from the main
checkout at the end; `/status` shows no `Capture*` fields, which is how to tell). QA must install
this branch (`bash scripts/linux/install.sh` from a checkout of it) before running the scripts.

Test recordings made today are kept under `~/Videos/AgentEyes/` (nothing deleted). The evidence
recording `2026-11-14_102532_video` was only read.

I believe this is finished for Linux. macOS runtime proof and the Windows CI result are open.
