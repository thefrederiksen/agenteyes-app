# Issue #113 - pass 5 (after the Review Gate rejection of `ad21557`): Developer report

Developer seat on the Windows laptop, 2026-11-16. Branch `issue-113-capture-watchdog` (PR #137).
Spec for this pass: the issue, the gate's rejection (comment 6915218061), the lead's ruling
(6915262658) and the lead's follow-up (6916961462).

| Commit | What |
|---|---|
| `c7e2339` | merge `main` `d0d0a27` (#156) - item 1, the D3 resolution. No behaviour change |
| `9f45b33` | the watchdog's fail-open hole - item 3 |
| `3debd3d` | the fragmented capture no longer starts the video late - item 4 |
| `14b2fa6` | merge `main` `277a9bc` (#118) - no conflicts, touches no Windows recording code |
| `00c9df9` | self-review fixes to item 3 (see "Self-review") - the head this report is for |

Toolchain everywhere below: **.NET SDK 10.0.301 building net8.0, runtime 8.0.21**, Windows 11,
ffmpeg 8.1.1 (gyan.dev full build). Every binary quotes its embedded commit
(`1.11.11+<40 hex>`), read from the DLL that ran, and every run used the `bin\x64\Release` output.

## The gate on the final head

| Gate | Where | Result |
|---|---|---|
| `dotnet build AgentEyes.sln -c Release` | this laptop, `00c9df9` | `Build succeeded.`, `0 Error(s)` |
| `dotnet test AgentEyes.sln -c Release` | this laptop, `00c9df9` | AgentEyes.Tests **2523 passed, 16 skipped, 0 failed** (2539); DesktopApp 23/23 |
| CI, push event | run 43935526549, `3debd3d` | windows 2522/16 skipped and 2541/1 (with `AGENTEYES_FFMPEG_TESTS`), linux 464/1, macos 430/1, DesktopApp 15/15 |
| CI, pull_request (merge with `main` `277a9bc`) | run 43935540270 | windows 2522/16 and 2541/1, linux 472/1, macos 430/1, DesktopApp 23/23 |

The Linux suite does not restore on Windows (the desktop project is Windows-targeted here). The
macOS suite on a Windows host fails exactly one test,
`MacPathsTests.MachineLocalAppData_OnMac_IsTheApplicationSupportFolder`, and fails it identically on
unmodified `main` `d0d0a27` (382/1): a macOS path assertion cannot hold on Windows. CI runs both
suites on their own platforms, green above.

---

## Item 1 - the rebase onto `d0d0a27`, and D3

Merged (`c7e2339`), not rebased: the branch already carries merge commits, so a rebase would
rewrite every hash the earlier proof cites. The review-gate rule (comment 6881245021) accepts
either: "the branch must contain main's head". The merge commit holds ONLY the resolution; the two
fixes are separate commits on top, so the gate can read each alone.

| Conflict | Kind | Resolution | Why it is right |
|---|---|---|---|
| `RecordingService.cs` StartVideo | textual | kept #113's `_seams.StartScreen(...)`; added #156's `NoteCaptureWriter("video", _video.ProcessId)` after it | both halves are load-bearing: the seam is how the watchdog wiring is tested; the note is how a second process sees the capture's writer |
| `AgentEyes.Tests.Linux.csproj` | textual | union of both sides' shared test links | each side added its own tests; dropping either side's line silently drops its tests from the Linux suite |
| `IScreenRecorder` lacks `ProcessId` | compile | `int ProcessId { get; }` added to the interface | `FfmpegRecorder` already implements it (from #156), so it is defined ONCE and the interface states it. Duplicating it (a second property, or a cast to the concrete type) would be two sources of truth for the pid that #156's claim depends on |
| two `CaptureLiveness` types | compile | #113's renamed `CaptureOutputLiveness`; #156's untouched | they are different questions: #156's asks "is a capture's CLAIM live" (another process asking); #113's asks "is the capture PRODUCING OUTPUT" (the owner asking). Inside `namespace AgentEyes` the short name binds to #156's type ahead of `AgentEyes.Video`'s, so with one name every unqualified #113 use resolved to the WRONG type - the build failed only because the members differ. #156's type is merged and verified; mine is not. The `/status` field stays `CaptureLiveness`: that is a wire name QA has quoted, not a type |

One test-only change the merge forced: the watchdog wiring tests' fake recorder reports the test
host's own pid (`NoteWriter` reads the start time of a LIVE process, and it is the one process that
provably runs for the whole test and that no reap can kill on its behalf). #156 names the child
registry's record file by pid, so two harness tests running in parallel opened the same file and
failed on a sharing violation (measured). They now run in one xUnit collection. A real writer's pid
is unique, so this is a property of the fake, not of the product.

Gate on the merge commit alone: 2513 passed, 16 skipped, 0 failed.

## Item 2 - the gate's scenario, on a real Windows capture, with its control

**How it was driven.** There is no supported way to point a shipped binary at a scratch library on
Windows (#161: `AppDataPaths` reads the known folders, not environment variables). So the capture
ran in a scratch-only evidence host (`evidence/harness/Program.cs`, NOT committed product code):
the **production** `RecordingService` with production seams (real `FfmpegRecorder`, real
`CaptureWatchdog`, real `CaptureLock`) behind the **production** `HeadlessControlServer` on port
7990, after `AppDataPaths.RedirectForThisProcess` - the test host's own fail-closed mechanism -
moved both roots into a scratch folder. No `RepairService` is started, so nothing swept any library.
The second process was the real `agenteyes.exe package <dir> --no-frames` built from the same
commit - the command the gate used, which reaches `RecordingService.FinalizePending`.

Scenario (`evidence/harness/gate_scenario.py`): screen 1 + the laptop mic through
`/record/start` (`source=mic`, so the capture writes `raw.mp4` with a `PendingMux`, as the gate's
setup), 12 s healthy, then the second process, then `/status` every 10 s for 120 s, then
`/record/stop` and a full decode.

| | control: pre-#156 `ad21557` | this branch: `9f45b33`, and again on `3debd3d` |
|---|---|---|
| binary | `1.11.11+ad21557bb177...` | `1.11.11+9f45b336...` / `1.11.11+3debd3d6...` |
| second process | ran the mux; **exit 3**: `The process cannot access the file because it is being used by another process.` | **exit 1 in 0.4 s / 0.5 s**: `refusing to package 2026-11-16_140834_video: it is being captured by the capture session of pid 22440 on host <laptop> (started ...). Nothing was muxed, renamed or counted as an attempt` |
| attempt counted | yes - final `Attempts: 2` | **no** - `PostProcessing: {}` after the decline; final `Attempts: 1` (the owner's own) |
| `raw.mp4` | stayed (the rename FAILED) and kept growing | stayed and kept growing |
| `recording.mp4` during the capture | **a 726,204-byte prefix written beside the live capture**, there for two minutes until the real stop overwrote it | **none** |
| `/status` after the second process | `growing` throughout | `growing` at **18 of 18** recording-time polls, to 120 s past the decline |
| watchdog | nothing raised | nothing raised |
| end | stop -> 138.27 s | stop -> **134.27 s** `recording.mp4`, decodes (item 6) |

Expected (the gate's bar): declines, names the holder, counts no attempt, `raw.mp4` not renamed, no
`recording.mp4` while the capture runs, the watchdog raises nothing, completes and decodes.
**Actual: all seven, on the final head.** Evidence: `evidence/runs/final-gate-scenario.*`,
`evidence/runs/head-gate-scenario.*`, `evidence/runs/second-process-package-*.log`.

**Why it holds: #156's fix, not a change here.** `MuxCoordinator.RunForPackage` calls
`CaptureLock.ThrowIfLive` before reconcile and before the attempt is counted; this branch adds
nothing to that path. What this branch adds to the chain is item 3's truthful verdict for a file
that leaves its path (below), which is a second line, not the guard.

**The control on Windows is not the macOS/Linux chain.** The rename fails with a sharing violation
(item 5), so Windows never reached the kill. It still did damage: one burned attempt, a wasted mux
of the prefix, and a prefix `recording.mp4` sitting beside a live capture for two minutes, where any
reader of the folder would take a 10-second file for the recording.

## Item 3 - the watchdog's own fail-open hole

`OnWatchdogTick`'s catch logged and returned without re-arming. Now (`9f45b33`):

- **A check whose READ throws** is counted as a check that saw **no growth**
  (`CaptureWatchdog.CheckFailed`) - the rule the watchdog already applies to a file that does not
  exist. `/status` says `CaptureLiveness=check-failed` (not a stale `growing`), the timer is
  re-armed, and a read that keeps failing ends the recording with the ordinary stall verdict inside
  the same 45 s window, saying what was observed: `recording.mp4 could not be read for the last 23
  checks (UnauthorizedAccessException: ...); last seen at 120000 bytes` - never "stopped growing",
  which an unreadable file may not have done. The exit code is still read on a failed check, so a
  capture that has ENDED is decided at once with its real code, not 45 s later as a stall.
- **Anything else that throws in the tick** re-arms while the session is still the live one. Before
  a verdict, `/status` says `CaptureLiveness=watchdog-failed` (the last good numbers, no longer
  called growing). After a verdict but before the stop began (the stop flips the state first, so this
  is the only window), the verdict is already on `/status` and the next tick ends the recording
  again. The re-arm lives in the tick's own catch - the entry point - with its own boundary, because
  a throw there would escape onto the timer thread.

Chosen over "fail the recording loudly on any throw" because a transient read error (a slow disk, an
antivirus scan holding the file) would then end a healthy recording; counting it as no growth keeps
the 45 s tolerance and still cannot go silent. A seam (`RecordingServiceSeams.OutputFileSize`) lets a
test make the REAL read throw through the real service.

Tests: `WatchdogFailOpenTests` (3, through the real service) and 4 `CaptureWatchdogTests`.
**Mutations** (`evidence/harness/mutate.py`), each restored and rebuilt afterwards:

| Mutation | Result |
|---|---|
| M1: the read's catch logs and returns (the silent shape at the read) | 2 tests fail |
| M2: the tick's outer catch only logs | 1 test fails |
| M3: exactly the code the gate reviewed | 3 tests fail |
| M4: a failed check ignores the exit code (`exit: null`) | 1 test fails |

Re-run on `00c9df9` with the same counts. The after-verdict re-arm is a shape, like the gate's own
finding: nothing between the verdict and the stop's first line throws in practice
(`PostRecording.TrackWork` is an increment and a log line), so no test reaches it without a seam I
did not think worth adding; the pre-verdict re-arm, which is the same code path, is tested.

A measurement trap worth recording: the restore copied a file whose timestamp was OLDER than the
mutated DLL, so the next incremental build skipped it and the full gate ran the mutant (3 failures).
Every restore is now followed by a touch and a rebuild, and the source was grepped for the fix
before each counted run.

## Item 4 - the video start offset, on real captures

**It is real, and on Windows it is not two frames.** Same laptop, same devices, `/record/start`
with `source=mic`, about 45 s each (`evidence/verify-offset-runs.txt`):

| Build | Container | fps | video `start_time` | audio `start_time` |
|---|---|---|---|---|
| `main` `d0d0a27` | plain MP4 | 30 | 0.000 | 0.000 |
| `main` `d0d0a27` | plain MP4 | 5 | 0.000 | 0.000 |
| `9f45b33` | fragmented | 30 | **0.867** (raw and final) | 0.000 |
| `9f45b33` | fragmented | 5 | **0.800** (raw and final) | 0.000 |
| `9f45b33`, gate run | fragmented | 30 | **1.000** | 0.000 |
| `ad21557`, control run | fragmented | 30 | **0.833** | 0.000 |
| **`3debd3d`** | fragmented + `delay_moov` | 30 | **0.000** | 0.000 |
| **`3debd3d`** | fragmented + `delay_moov` | 5 | **0.000** | 0.000 |

Roughly constant in TIME (0.8-1.0 s), not in frames (4 frames at 5 fps, 25-30 at 30 fps). It
survived the mux into `recording.mp4` every time.

**Mechanism** (`evidence/debug-ts-*.txt`, the product's exact command run for 8 s with
`-debug_ts`): both inputs are rebased to 0. gdigrab delivers its first frame and then nothing for
~0.69 s. libx264's B-frames make the first video DTS negative by two frames measured in TIME -
here 0.733 s, because of that gap (the gate's lavfi source had no gap: 0.067 s). Plain MP4 cancels
it with an edit list (`elst media_time 11264 / timescale 15360 = 0.733 s`). With `+empty_moov` the
index is written before any sample, so there is **no edit list**, and the video is presented 0.73 s
after the audio. Plain MP4 is therefore the truthful timeline, and **the branch as the gate reviewed
it put a ~0.7-1.0 s lip-sync error on every Windows screen+mic recording.**

**Cost if left:** at 0.7-1.0 s, every Windows recording's speech visibly trails or leads the screen.
That is not a separate issue's worth of risk to merge knowingly, and the cause is this PR's own
container choice, so it is fixed here (`3debd3d`) and the lead was told when I started it.

**Candidates, measured on the real devices** (`evidence/debug-ts-candidates.txt`):

| Flags | video start | B-frames | verdict |
|---|---|---|---|
| fragmented (as reviewed) | 0.733 | yes | the defect |
| + `negative_cts_offsets` | 0.833 | yes | does not fix it |
| + `-bf 0` | 0.000 | **no** | fixes it by spending B-frames |
| **+ `delay_moov`** | **0.000** | yes | **chosen**: the index is written when the first fragment is cut, so it carries the edit list |
| + `delay_moov`, TerminateProcess at ~12 s | 0.000 | yes | **decodes to exit 0**, 250 frames, a frame from the last second |

**Why not the alternatives, one line each:** `-bf 0` fixes the offset by spending B-frames on every
recording. `+negative_cts_offsets` does not fix it (measured 0.833 s).

**The price, measured: a kill in the first ~2 s** (`evidence/debug-ts-early-kill*.txt`, real
devices, TerminateProcess on an ffmpeg this script started, final flags):

| Killed at | With `+delay_moov` | As reviewed (no `delay_moov`) |
|---|---|---|
| 1.0 s | **0 bytes** - no ftyp, no moov, nothing to recover | 1,241 bytes - header only, **zero frames** |
| 1.8 s | **0 bytes** - nothing to recover | 1,241 bytes - header only, **zero frames** |
| 2.6 s | 245,008 bytes, decodes to exit 0, 20 frames, both streams at 0.000 | - |
| 3.5 s | 254,075 bytes, decodes to exit 0, both streams at 0.000 | - |

So the early window loses nothing that was recoverable before: as reviewed, a kill there left a
playable-looking header with no frames; now it leaves an empty file. The difference a reader sees is
"0 bytes" instead of "0 frames". Exposure is a crash or a power loss in a capture's first two seconds.
**A watchdog kill is never inside it**: the watchdog's earliest verdict is the 45 s start-up window.
`raw.mp4` stays at 0 bytes until the first fragment (measured `[0, 0, 247352, ...]` per second),
and the watchdog reads that as "starting", as it always has.

**One lesson, from the lead, and it is right.** The gate's lavfi measurement (0.067 s, "two frames")
was correct for lavfi and under-reported the defect ten-fold: "two frames" is a measure of TIME, and
a synthetic source has no gap between its first frames, while gdigrab's second frame arrives ~0.7 s
after its first. **A synthetic source systematically under-reports this class of defect**; a
timestamp defect has to be measured on the real capture device.

> **QA, macOS and Linux: `+delay_moov` changes the capture command line on ALL THREE platforms.
> Criteria 1 (frozen capture), 2 (killed capture) and 5 (the file an unclean end leaves) must be
> re-run there on a real capture, because criterion 5's playable file now depends on when the index
> is written; and check both streams' `start_time` in raw.mp4 and recording.mp4 (expected 0.000).**

Every criteria 1/2/5 run in this pass was taken at 30 fps. The 5 fps runs are used only for the
offset measurement, because at 5 fps the pre-existing audio shortfall below would contaminate them.

Regression test: `FragmentedOutputTimelineTests` runs a real ffmpeg through `FragmentedOutput()`;
removing `+delay_moov` fails it with the video at **0.066667 s** - the gate's own number.

**A second finding, not this PR's:** at 5 fps on Windows the mic audio is ~4.5 s shorter than the
video on BOTH trees (`main`: 39.4 s of audio in 44.0 s of video; final: 39.06 in 44.0). #113's
`behind` check is what surfaces it (`the capture wrote 39.04s of a 42.86s session`), because
ffmpeg's progress time follows the shortest stream. Pre-existing audio truncation, #114's territory;
the lead is filing it as its own issue with these numbers.

## Item 5 - the Windows run nobody had done

All on the final head `3debd3d` (`evidence/runs/final-*`). Kill provenance is stated for each.

**Recorded.** The 134.27 s gate-scenario recording above and two 45 s recordings: healthy throughout,
`CaptureLiveness` advancing every poll. **The size read by path while ffmpeg holds the file open
works on Windows**: `CaptureBytes` climbed at every healthy poll and froze the moment the writer did.

**Stalled** (`NtSuspendProcess`, the Windows SIGSTOP, applied by MY script to the capture ffmpeg
after checking its parent was my evidence host):

```
19:06:05.997Z /status [healthy 1] State=recording CaptureLiveness=growing CaptureBytes=366865
19:06:16.002Z /status [healthy 2] State=recording CaptureLiveness=growing CaptureBytes=716246
19:06:17.467Z INJECT: NtSuspendProcess(pid 23232)
19:06:21.490Z /status State=recording CaptureLiveness=not-growing CaptureBytes=994385 CaptureSecondsSinceGrowth=2
19:07:05.886Z /status State=idle LastStopFailed=true LastCaptureFault="capture stalled: raw.mp4 stopped growing at
              994385 bytes and has not grown for 46s (limit 45s); last growth 2026-11-16T19:06:18.006Z"
```

Detected at 19:07:04.164Z: 46.2 s after the last growth, 46.7 s after the injection; the state was
idle at the poll 48.4 s after it. Logged at detection time:
`14:07:04.166 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=67.6s (detected 2026-11-16T19:07:04.164Z) ...`.
**The kill came from the product**: `[FfmpegRecorder] Abandon: ... ended in 75ms (killed, exit -1)`;
the suspended pid was gone afterwards and my script killed nothing. Manifest:
`CaptureFault=stalled`, `CaptureFaultDetectedUtc=2026-11-16T19:07:04.1645335Z`, `CapturedSeconds=20.47`.

**Killed** (`TerminateProcess(pid, 137)` - **this kill is MINE**, on my evidence host's child):
fault `capture exited: the capture process exited (code 137) while recording; raw.mp4 holds 776585 bytes`
detected 1.2 s later (19:08:00.187Z); `Abandon ... ended in 0ms (had already exited, exit 137)` - the
product killed nothing.

**The resulting files** (`evidence/verify-final-runs.txt`): stall - `raw.mp4` 559 frames, 20.47 s;
kill - 500 frames, 18.87 s. Both decode to exit 0 with a frame from the last second, and both were
muxed to a `recording.mp4` that does the same.

**Repeated on the head, `00c9df9`** (`1.11.11+00c9df94c307...`; `evidence/runs/00c9df9-*`,
`evidence/verify-00c9df9-fault-runs.txt`), because the self-review changed watchdog code:

| Injection (who) | Verdict | Latency | Who killed the writer | Files left |
|---|---|---|---|---|
| `NtSuspendProcess` (my script) | `capture stalled: raw.mp4 stopped growing at 760981 bytes and has not grown for 46s (limit 45s)` | detected 19:28:20.706Z, 46.2 s after the last growth; idle at the poll 46.5 s after the injection | **the product** (`Abandon ... ended in 92ms (killed, exit -1)`) | `raw.mp4` 500 frames, `recording.mp4` 500 frames, both decode to exit 0 with a last-second frame |
| `TerminateProcess` (my script) | `capture exited: the capture process exited (code 137) while recording; raw.mp4 holds 755597 bytes` | detected 19:29:07.329Z, 1.1 s after the kill | **my script** (`Abandon ... had already exited, exit 137`) | `raw.mp4` 500 frames, `recording.mp4` 500 frames, both decode to exit 0 with a last-second frame |

**The rename on Windows: measured, a sharing violation.** On the pre-#156 control the second
process's log shows it, at the call the gate named:

```
13:46:37.812 [pid 8060] [ERROR] System.IO.IOException: The process cannot access the file because it is being used by another process.
13:46:37.812 [pid 8060] [ERROR]    at System.IO.FileSystem.MoveFile(String sourceFullPath, String destFullPath, Boolean overwrite)
13:46:37.812 [pid 8060] [ERROR]    at AgentEyes.OriginalBackup.Preserve(String dir, String mode, AudioSourceKind src)
```

The mux had already written the prefix `recording.mp4` before that throw (`MuxInTwoPasses: process
mic audio done out=...recording.mp4` at 13:46:37.435).

## Item 6 - the amended criterion, met by this pass's own evidence

Every file this pass cites was checked by `evidence/harness/verify_file.py`: a COUNTED frame total
(`-count_frames`), a **full decode to exit 0** with the error lines counted (zero, every file), and
**a frame read from the last second** (`-sseof -1`, PNG written). "moov present" is not used
anywhere. 26 files, `evidence/verify-*.txt` and `evidence/runs/head-gate-scenario.verify.txt`.

## Self-review

A code review of this pass's diff (`ad21557..14b2fa6`, the eight files this pass touched) returned
ten findings. Fixed in `00c9df9`, each with a test that fails when the fix is reverted where a test
can reach it:

| Finding | Fix |
|---|---|
| a throw after a verdict but before the stop began stopped re-arming - a recording nobody would end | re-arm while still the live session, verdict or not (item 3) |
| a size read that throws hid an exit that had already happened, reported 45 s later as a stall | `CheckFailed` reads the exit code (mutation M4) |
| a stall reached through failed reads said "stopped growing" - the false-reason problem again | "could not be read for the last N checks (...)" |
| `FileInfo.Exists` is false when the stat FAILS too, so "no longer at its path" could be false | "could not be found at its path (moved, deleted or not readable)" |
| my test collection did not cover #156's ownership tests, which record the same test-host pid | the wiring tests joined #156's `CaptureOwnershipCollection` |
| the try/catch sat in a helper, against CLAUDE.md rule 4 | the re-arm moved into the tick's own catch |
| a field held state used only inside one call | a local |
| the ffprobe helper read stdout to the end before stderr (a pipe deadlock) | both pipes drained concurrently |

Not changed: `_captureLock` duplicating the claim's lock is #156's merged code, outside this issue.
And the harness's fake writer is the test host, which never exits, so the wiring tests never see a
faulted capture's claim RELEASED and then finalized. That is a coverage gap, not a defect: the
runtime stall above shows the real path - verdict, kill, claim released, the owner's own mux
(`Attempts: 1`), a decodable `recording.mp4`.

---

## What this evidence does NOT cover

- **macOS and Linux at runtime.** `delay_moov` changes their capture command lines too. Their argument
  tests pin the new line and pass on CI, and the muxer flag is platform-neutral, but no macOS or
  Linux capture has been recorded, stalled or killed with it. QA owes criteria 1, 2 and 5 there.
- **The shipped hosts.** The evidence host is the production service and control server, not
  `agenteyes serve` and not the WPF app. Neither ran this code here; the HUD's response to a
  watchdog end is still from reading (`HudWindow.cs`, `MainWindow.xaml.cs` poll `IsRecording`).
- **The repair pass as the second process.** Only `package` was driven. The 15-minute pass reaches
  the same guard through #156's `PostRecording.Execute` decline, which #156's QA owns.
- **Which timeline is the true A/V sync**, by an acoustic event. This laptop's mic records silence
  (-96.7 dBFS, a hardware fault), so the plain timeline is shown to be truthful by mechanism (the
  edit list cancels an encoder artefact), not by a clap.

## The cases I was tempted to assume away

1. **A mixed-version machine is still exposed.** #156's guard works only if BOTH processes honour
   `capture.lock`. The installed AgentEyes app on THIS laptop is a pre-#156 build. Any pre-#156
   binary - an un-upgraded second host, or an old service beside a new CLI - still runs the gate's
   chain against a capture by a new one: on macOS and Linux, the published prefix, the rename and
   (with this PR) the 45 s kill; on Windows, the burned attempt and the stray prefix. Both hosts of a
   two-host machine have to be upgraded together. That is a rollout rule, not something this branch
   can enforce.
2. **The watchdog still ends a capture whose file leaves its path.** If anything moves `raw.mp4` again,
   the capture writes on into a file nobody will publish, so ending it is defensible - but the reason
   used to be false ("stopped growing"). It now says what was seen: `raw.mp4 could not be found at
   its path (moved, deleted or not readable; last seen at N bytes)`. Unit-tested; not reproduced at
   runtime, because #156 now prevents it.
3. **`check-failed` keeps the last byte count on `/status`.** That number is the last one READ, not a
   current one; the state word says so, and a client must read the state, not just the bytes.
4. **`delay_moov` leaves the first ~2 s empty.** Measured in item 4: a capture killed at 1.0 s or
   1.8 s leaves a 0-byte file, where the reviewed flags left a header with zero frames. Nothing that
   was recoverable is lost; the watchdog never kills inside that window.

## Housekeeping

- Evidence recordings are kept, outside the repository (they hold the owner's screen):
  `<evidence>` on this laptop, one folder per run, each with its scenario log, its own data folder
  and the recording. Nothing was deleted. Only scrubbed text evidence is committed: paths, the host
  name and the host id are replaced, and the collector refuses to write if an identifying or banned
  string survives (`<home>` 68 replacements, `<laptop>` 88, `<host-id>` 9).
- The owner's real data folder (#161): my six CLI invocations (`--version`, `devices`, `screens` and
  three `package` runs) wrote **log lines only** to `%LOCALAPPDATA%\AgentEyes\logs`. The control
  `package` registered and unregistered two ffmpeg children in `run\ffmpeg`, which is empty again.
  The owner's recordings, presets and audio settings were not touched; the owner's running app was
  never driven.
- Every ffmpeg I ended was one my own evidence host had started, checked by parent pid first; the
  kills are attributed above. The test suite's own ffmpeg handling is #151's.
- Two scratch git worktrees (`ad21557`, `d0d0a27`) were used to build the control hosts and are removed.
