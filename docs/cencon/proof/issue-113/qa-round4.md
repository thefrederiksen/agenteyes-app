# Issue #113 - QA round four, on macOS, against the rebased tree

**Verdict: PASS on the acceptance criteria. NOT MERGED - see section 9.**

| | |
|---|---|
| Seat | independent QA, macOS. Did not write any of this code. |
| Machine | devthrottle-mac-mini, Mac16,10, 10 cores, macOS 26.5 arm64, ffmpeg 9.0.2 (Homebrew) |
| PR | #137, head `fe096aa` |
| Code head | `fe096aa` differs from `b74d1a7` only by issue-142 docs - `git diff --name-only b74d1a7 fe096aa` is entirely under `docs/`. The code under test is `b74d1a7`'s. |
| Screen | LOCKED throughout (`CGSessionScreenIsLocked`). A locked Mac composites the wallpaper alone. No criterion here depends on screen content. |
| Load | stated with every timing figure below. Baseline 2.2-4.3 on 10 cores; an unrelated Python process held one core for part of the session. |

## 1. Which build answered

`install.sh` publishes self-contained, so the launcher `agenteyes` is a generic apphost stub - **its
hash is identical across builds and identifies nothing**. The build identity used throughout this
report is `agenteyes.dll`. Every figure below is tied to one of these:

| Build | `agenteyes.dll` SHA-256 | service pid |
|---|---|---|
| left installed by the developer seat (NOT verified, replaced immediately) | `ebf9f5d1430debd8...` | 23007 |
| **branch head `fe096aa`** | `4cb4665c7a434d25...` | 23523, then 26649 |
| `fe096aa` with `7b4382e`'s product change reverted | `bae266f84dc438e5...` | 24368 |
| **main `90c9118`** (control) | `0446dd5696f81b19...` | 32386 |

The branch-head hash `4cb4665c` was reproduced on two independent installs, so the dll hash is a
stable build identity on this machine. Each install produced a NEW pid, confirmed with
`launchctl print` before and after.

`ProcessType Interactive` is present in both the repo template
(`scripts/mac/com.agenteyes.recorder.plist`) and the installed plist:

```
    <key>ProcessType</key>
    <string>Interactive</string>
```

## 2. The macOS gate - all four commands pass

| Command | Result |
|---|---|
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` | `Build succeeded.` `0 Error(s)` |
| `dotnet test tests/AgentEyes.Tests.Mac` | `Passed! - Failed: 0, Passed: 313, Skipped: 0, Total: 313` |
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux` | `Build succeeded.` `0 Error(s)` |
| `dotnet test tests/AgentEyes.Tests.Linux` | `Passed! - Failed: 0, Passed: 320, Skipped: 1, Total: 321` |

The one skip is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, which needs a real
Linux host. Running the Linux PROJECT on this macOS HOST is not a Linux run; the real Linux evidence
is the CI linux job (section 8), which runs 321/321 with nothing skipped.

`AgentEyes.sln` cannot build on a Mac and was not run here. The Windows gate is CI's (section 8).

## 3. The defect in `7b4382e`, reproduced from scratch

`7b4382e` is 57 lines of product code written during the rebase re-verification, after #113's last
QA round. It was treated here as new code on a fresh issue. **The defect it fixes was reproduced
independently before the fix was examined**, by reverting only its product change
(`git checkout 207594f -- src/AgentEyes.Core/RecordingService.cs`, leaving main's stop order
`audio, loopback, video, camera`) and installing that build.

### 3a. The failing state (build `bae266f8`, pid 24368, load 4.21 at start)

Screen+mic through the Control API - the shipped default - then `SIGSTOP` on the screen ffmpeg
(pid 24480). Transcript: `qa-round4/repro-reverted-sigstop.txt`.

```
T +   22.2s (inj+   0.0s) State=recording   Live=growing      child=T svc_cpu=0.1
T +   32.3s (inj+  10.1s) State=recording   Live=not-growing  child=T svc_cpu=100.0
T +   72.7s (inj+  50.6s) State=finalizing  Live=faulted      child=T svc_cpu=100.0
T +  254.7s (inj+ 232.6s) State=finalizing  Live=faulted      child=T svc_cpu=100.0
```

The watchdog's own verdict was correct and on time - the fault was named on `/status` at
**inj+46.55s**. Then the stop never finished: `finalizing` for the whole remaining **232.6 s** of
observation, the frozen child still in process state `T` and never killed, and the service holding a
full core. While hung, the recording directory held `raw.mp4` and `mic_native.wav` and no
`recording.mp4`.

Causation was then measured rather than assumed. `kill -9` on that one child (the only pid this seat
had stopped):

```
+0.04s State=finalizing svc_cpu=96.9
+1.10s State=idle       svc_cpu=3.3
```

The service reached idle **1.10 s** after the frozen child died, and the core was released. The
frozen child was the blocker and the pre-fix build had no path that killed it.

### 3b. The same injection on the branch head (build `4cb4665c`, pid 26649, load 3.62 at start)

Transcript: `qa-round4/ac1-fixed-sigstop.txt`.

```
T +   66.6s (inj+  44.5s) State=recording   Live=not-growing  child=T    svc_cpu=100.0
T +   68.7s (inj+  46.5s) State=idle        Fault=capture stalled ...    child=gone svc_cpu=28.9
### REACHED IDLE at inj+46.54s
# remaining ffmpegs: []
```

Fault named and the whole stop completed inside one 2-second poll, **idle at inj+46.54 s**, the child
gone, no ffmpeg left behind.

The ordering is visible directly in the application log, which is the presence proof that the fix is
what ran - the capture kill precedes the audio writer by 19 ms:

```
13:03:24.343 [RecordingStopSequence] Run: dir=.../2026-11-15_130215_video steps=2
13:03:24.344 [FfmpegRecorder] Abandon: .../raw.mp4 - capture stalled ... - killing the capture process now
13:03:24.362 [FfmpegRecorder] Abandon: ... ended in 18ms (killed, exit 137, last reported position 21.30s)
13:03:24.363 [AudioCapture] Stop
13:03:24.363 [AvfStream] Stop mic: asking ffmpeg to finish
```

## 4. The regression risk: the healthy path

Checked against what main actually does, not against the test's expectation.

**Main's code.** `RecordingService.cs` at `90c9118`, lines 866-880, builds the sequence in this order:

```
866:  var steps = new List<RecordingStopStep>();
867:  if (audio != null) steps.Add(new RecordingStopStep("audio", ...));
868:  if (loop != null) steps.Add(new RecordingStopStep("loopback", ...));
869:  if (video != null) steps.Add(new RecordingStopStep("video", ...));
880:  if (camera != null) steps.Add(new RecordingStopStep("camera", ...));
```

`OrderStopSteps(video, faulted: false, audio, loopback, camera)` emits
`audio, loopback, video, camera` - the same sequence, same membership. `RecordingStopOrderTests`
asserts exactly that list, so in this case the test and main agree independently of each other.

**Runtime.** A healthy 20-second screen+mic recording stopped normally on the SAME fixed build
(`4cb4665c`, pid 26649) drains audio first and the capture after:

```
13:04:32.923 [AudioCapture] Stop
13:04:33.095 [AudioCapture] Stop: wrote 1947648 bytes of PCM = 20.29s of audio over a 20.52s capture
13:04:33.162 [FfmpegRecorder] Stop: pipeline drained in 66ms ... - full take captured
13:04:33.739 [RecordingStopSequence] Run: ... completed with no failures
```

`/record/stop` returned `DurationSeconds: 21.13`, `/status` then `LastStopFailed=false`,
`LastCaptureFault=null`. Both orders are therefore demonstrated at runtime on one build, which is
what makes the ordering change safe rather than merely asserted.

`RecordingStopOrderTests` has five tests, not four, and asserts both exact lists plus a
count/uniqueness instrument check, so it cannot be satisfied by always killing the capture first nor
by an ordering that silently dropped a writer.

## 5. The mutant count - re-run, not accepted

The repo's committed `mutate.py` defines only **12** mutants and points at a Linux path
(`/home/soren/...`); the script that produced the committed 14-mutant macOS run was never committed.
A fresh harness was written for this round (`qa-round4/mutate-mac.py`), re-using rounds one and two's
anchor definitions verbatim from the repo's `mutate.py` and defining B2 and N from the rebase
report's description.

Every anchor is verified to appear **exactly once** before any mutant runs, so a silently-missing
anchor cannot masquerade as a surviving mutant:

```
[OK] all 14 mutants' anchors found exactly once
```

**14 confirmed. None survived.** Full output: `qa-round4/mutation-mac-independent.txt`. Every count
matches the developer's committed run exactly.

| Mutant | Tests failed | Mutant | Tests failed |
|---|---|---|---|
| F behind never recorded | 2 | O watchdog down after the writers | 1 |
| A watchdog never fires | 14 | V verdict not in the stop report | 3 |
| B no fragmented output | 3 | G stop after the verdict takes 16 s | 4 |
| C gaps counted as stalls | 2 | **B2 macOS capture drops FragmentedOutput** | **2** |
| D no startup window | 4 | **N faulted capture not killed first** | **2** |
| E stall ignores timeout | 8 | restored baseline | **313 / 0** |
| W StartVideo never starts the watchdog | 7 | P verdict published only after the stop | 2 |

**Mutant N - the guard on the new code is not vacuous.** It fails two tests by name:

```
FAILED AgentEyes.Tests.Shared.RecordingStopOrderTests.OrderStopSteps_WatchdogFaultedTheCapture_KillsTheCaptureFirst
FAILED AgentEyes.Tests.Shared.RecordingStopOrderTests.OrderStopSteps_TheCameraIsLast_WhicheverWayTheCaptureEnded
```

**Mutant B2 - the lead's warning was wrong in this combination, and the developer's correction is
right.** Removing `FragmentedOutput()` from `Platform/Mac/FfmpegArgs.Mac.cs:207` is NOT silent:

```
FAILED AgentEyes.Tests.Mac.MacFfmpegArgsTests.AvfVideoCapture_WholeScreen_ExactCommandLine
FAILED AgentEyes.Tests.Mac.MacFfmpegArgsTests.AvfVideoCapture_WithPreview_FragmentsTheRecordingNotThePreview
```

The developer then caveated its own correction, saying the warning still holds if a seat resolved the
product file AND the test file the same "take main" way, because "the guard is the union, not the
test". **That caveat was tested and is too pessimistic.** Dropping the fragment entries from the
product file and from the expected list in `MacFfmpegArgsTests.cs` together still does not give a
green suite:

```
Failed AgentEyes.Tests.Mac.MacFfmpegArgsTests.AvfVideoCapture_WithPreview_FragmentsTheRecordingNotThePreview
Failed! - Failed: 1, Passed: 312, Total: 313
```

`AvfVideoCapture_WithPreview_FragmentsTheRecordingNotThePreview` asserts structurally through the
shared `CaptureWatchdogTests.AssertFragmentedBefore(args, "/r/raw.mp4")` helper rather than against
the union-resolved expected list, so it survives that resolution and still bites. The remedy has a
guard that is not the union.

## 6. Acceptance criteria - expected vs actual

All on build `4cb4665c`, pid 26649.

| # | Expected | Actual | |
|---|---|---|---|
| 1 | frozen capture: not-`recording` and fault named within 60 s | `SIGSTOP`: idle at **inj+46.54 s**, `LastCaptureFault="capture stalled: raw.mp4 stopped growing at 13071834 bytes and has not grown for 46s (limit 45s)"` | PASS |
| 2 | dead capture: same within 60 s | `SIGKILL`: fault named at **inj+2.04 s**, idle at **inj+4.07 s**, `LastCaptureFault="capture exited: the capture process exited (code 137) while recording"` | PASS |
| 3 | `/status` liveness advances, then stops | poll A `+2.1s growing bytes=761 lastGrowth=18:02:18.280465Z`; poll B `+12.1s growing bytes=6503602 lastGrowth=18:02:28.288787Z`; stalled `inj+2.0s not-growing bytes=13071834 SinceGrowth=2`, rising to `SinceGrowth=46` then `faulted` | PASS |
| 4 | 10-minute healthy run never falsely ended | **60 / 60** polls over **600.8 s** all `State=recording` + `Live=growing`, `bad_polls=0`, no fault. Stop clean: `DurationSeconds: 601.95`, `LastStopFailed=false`. File 602.53 s, **18076** frames = 30.0 fps. Load 2.2-3.5 throughout | PASS |
| 5 | unclean end still leaves a playable file | `SIGSTOP` case: `recording.mp4` 69.08 s, **620** frames, full decode **exit 0**. `SIGKILL` case: 24.83 s, **620** frames, full decode **exit 0**. No `moov atom not found` in either | PASS |
| 6 | manifest records the fault and the UTC time | `CaptureFault="stalled"`, `CaptureFaultDetail="raw.mp4 stopped growing at 13071834 bytes ..."`, `CaptureFaultDetectedUtc="2026-11-15T18:03:24.341734Z"`, `CaptureFaultElapsedSeconds=68.1`, `CapturedSeconds=20.67`. Kill case: `CaptureFault="exited"` | PASS |
| 7 | written to the log at detection time with elapsed seconds | `13:03:24.342 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=69.2s (detected 2026-11-15T18:03:24.341Z) dir=... - capture stalled: ... - ending the recording now and keeping what was captured` | PASS |
| 8 | Windows and Linux still record; name the tests | CI linux **321/0** on a real Linux runner, windows **2415/0** (section 8). Argument guards: `LinuxFfmpegArgsTests`, `MacFfmpegArgsTests.AvfVideoCapture_WholeScreen_ExactCommandLine`, `CaptureWatchdogTests.FragmentedOutput_IsOnTheWindowsCaptureImmediatelyBeforeTheOutputPath`. Watchdog unit-tested with a fake clock and a fake growth source (`ICaptureClock`, `Func<long?>` size source) | PASS |
| 9 | mac and linux build + test; Windows named | section 2, all four pass; Windows via CI, section 8 | PASS |
| 10 | watchdog unit tests: fires on stopped growth, not while growing, not in start-up | `Stall_GrowthStops_FiresStalledInsideTheWindow`, `Healthy_FileGrowsOnlyEveryFortySeconds_NeverFaults`, `Startup_NoBytesYet_InsideTheWindow_DoesNotFireAndReportsStarting`, `Startup_SlowFirstOutputThenGrowth_DoesNotFire`, `Gap_LongPauseBetweenChecks_IsNotCountedAsAStall`; killed by mutants A, C, D, E | PASS |

## 7. The control on main - the instrument works, and one claim corrected

Main (`0446dd56`, pid 32386) has no watchdog (`grep -c CaptureWatchdog RecordingService.cs` = 0). The
same `SIGSTOP` injection, 150 s of observation (`qa-round4/main-control-sigstop.txt`):

```
T inj+   0.0s State=recording Elapsed=21.17  raw=11272240 child=T svc_cpu=0.0
T inj+ 145.7s State=recording Elapsed=166.86 raw=11272240 child=T svc_cpu=100.0
```

`State=recording` and `ElapsedSeconds` climbing for the whole observation while `raw.mp4` sat frozen
at 11,272,240 bytes - **issue #113's original defect, reproduced**. This is the failing case that
shows the watchdog is load-bearing rather than decorative.

What main left behind is the AC5 counterfactual, and it is this issue's 2026-11-12 failure verbatim:

```
$ ffprobe raw.mp4
[mov,mp4,m4a,3gp,3g2,mj2] moov atom not found
raw.mp4: Invalid data found when processing input      (exit 1)
```

No `recording.mp4` was produced at all, while `manifest.json` claimed `DurationSeconds: 232.45` with
`CaptureFault: None`. On the branch, the same injection yields a `recording.mp4` that decodes with
exit 0.

**One claim in the developer's commit message is narrower than stated.** It attributes the ~100% CPU
to the stop being blocked. The spin is in fact **pre-existing and independent of this PR**: main,
with no watchdog and no stop in progress, reaches `svc_cpu=100.0` at inj+5 s and holds it for the
whole 150 s. The SIGSTOPped child alone spins macOS' .NET child reaper; the watchdog's 2-second
`HasExited` poll is not the cause. This does not weaken the fix - it strengthens it, because the fix
bounds a pre-existing unbounded core burn to about 46 s. Noted so the record is right.

## 8. CI - confirmed, not taken on report

Both runs the developer named at `b74d1a7` exist and are green on all three jobs
(`43742827955`, `43742832487`). The PR head has since moved to `fe096aa`, which also has **two**
green runs on all three jobs (`43743381584`, `43743386417`). Counts read out of run
`43743386417`'s own log rather than from the report:

```
windows  Test AgentEyes.sln       Passed!  - Failed: 0, Passed: 2415, Skipped: 0, Total: 2415
linux    Test the Linux build     Passed!  - Failed: 0, Passed:  321, Skipped: 0, Total:  321
macos    Test the macOS build     Passed!  - Failed: 0, Passed:  313, Skipped: 0, Total:  313
```

Green on all three jobs across more than one run at the final head. The Windows job runs the full
`AgentEyes.sln` suite, which is criterion 9's Windows half.

## 9. Why this is not merged

The brief for this round instructed QA to squash-merge PR #137 "under CenCon method D5". **D5 was
superseded on 2026-08-12 by D7** and no longer authorizes this. `docs/cencon/DEVELOPMENT_METHOD.md`:

- Section 3.3, QA pass path: "Commits the QA proof report and re-labels the issue `flow:ready-gate`
  for the Review Gate (Section 3.4). **QA NO LONGER MERGES** (revised 2026-08-12, D7 - this
  supersedes D5)."
- Decision D7: "an independent Review Gate of a DIFFERENT AGENT VENDOR, not QA. SUPERSEDES D5.
  Rationale: on 2026-08-11/12 QA passed six PRs that the gate then rejected, including one that
  reintroduced the original recording-stranding defect."

That rationale describes this issue's own failure mode, and the `flow:ready-gate` label exists on the
repo. So this round sets **`flow:ready-gate`** and leaves #113 open, PR #137 unmerged and the branch
undeleted, for the Review Gate to decide.

**A documentation conflict to fix separately:** `CLAUDE.md` still carries the superseded rule at
line 38 ("then QA squash-merges to `main`") and line 217 ("The **QA Agent**, when it PASSES an issue
... squash-merges that issue's PR to `main`"). CLAUDE.md itself names
`docs/cencon/DEVELOPMENT_METHOD.md` as the contract, so the method document governs, but the stale
copy in CLAUDE.md is what sent this round at the merge button and should be corrected.

## 10. Observations that are not defects in this PR

1. **A pre-existing unbounded core burn**, section 7. Present on main with no watchdog. This PR bounds
   it. Possible follow-up issue, not a blocker here.
2. **The start-failure rollback keeps the old order.** `RecordingService.LiveWriters()` still emits
   `audio, loopback, video, camera` and is not routed through `OrderStopSteps`. It is only used by
   `RecordingStartSequence.Run` to unwind writers when a LATER start step throws, so reaching the hang
   through it needs a capture already frozen inside the start sequence AND a subsequent start step
   failing. Identical on main, so not a regression, and outside this issue's scope. Worth a follow-up.
3. **The mutation harness is not reproducible from the repo.** The committed `mutate.py` has 12 of the
   14 mutants and a hard-coded Linux path. `qa-round4/mutate-mac.py` is committed here as a runnable
   macOS replacement.
4. **`PostProcessing.package` fails** with "Not signed in to DevThrottle" on every recording in this
   round. Pre-existing, unrelated to #113.

## 11. Artifacts

| File | What |
|---|---|
| `qa-round4/repro-reverted-sigstop.txt` | the defect, reproduced on the reverted build |
| `qa-round4/ac1-fixed-sigstop.txt` | AC1 on the branch head |
| `qa-round4/ac2-fixed-sigkill.txt` | AC2 on the branch head |
| `qa-round4/ac4-healthy-600s.txt` | AC4, 60 polls over 600.8 s |
| `qa-round4/main-control-sigstop.txt` | the control on main: the original defect plus the CPU finding |
| `qa-round4/mutation-mac-independent.txt` | the independent 14-mutant run |
| `qa-round4/mutate-mac.py` | the mutation harness, runnable from the repo |
| `qa-round4/inject.py` | the fault-injection harness used for AC1/AC2/AC3 |
