# Issue #113 pass 5 - QA report, WINDOWS HALF

QA seat, Windows laptop, 2026-11-16. PR #137, branch `issue-113-capture-watchdog`.

**Scope: I am the Windows half.** The macOS and Linux re-runs of criteria 1, 2 and 5 (needed because
`+delay_moov` changes the capture command line on all three platforms) are being routed to their own
machines by the lead. Nothing in this report is evidence about macOS or Linux at runtime.

**Verdict (Windows): PASS on all seven items. No defect found.** Label left at `flow:ready-qa` -
Windows passed; macOS and Linux remain.

## What was tested

| | |
|---|---|
| Head tested | `2716e0f884084f6993ad4a1e4ac7dca3c6708d03` (code head `00c9df9`), fetched from origin, clean tree |
| Binary that ran | `agenteyes.dll` ProductVersion **`1.11.11+2716e0f884084f6993ad4a1e4ac7dca3c6708d03`**, read from the DLL in `bin\x64\Release` and again from the evidence host's own output (`host stdout: core=...`) in every head run |
| Control binary | **`1.11.11+ad21557bb177d52323a8484b3f6e9d73f16daf52`** (pre-#156, the head the gate rejected), built from a scratch worktree |
| Toolchain | .NET SDK 10.0.301 building net8.0, runtime 8.0.21; ffmpeg 8.1.1 (gyan.dev full build); Windows 11 |
| Devices | gdigrab screen 1 (1920x1080) + DirectShow `Microphone (2- Realtek(R) Audio)` |
| `origin/main` | `277a9bc` (#118), which the branch already contains |

### How the runtime evidence was driven

Shipped binaries cannot be pointed at a scratch library on Windows (#161). I used the developer's
40-line evidence host (`pass5/evidence/harness/Program.cs`) as an INSTRUMENT, after reading it: it
constructs the production `RecordingService()` (the public constructor = `RecordingServiceSeams.Production`,
`RecordingService.cs:246`) behind the production `HeadlessControlServer`, after
`AppDataPaths.RedirectForThisProcess` moves both roots to a scratch folder; it starts no `RepairService`.
I built it myself against each tree. **The scenario scripts are mine, not the developer's**
(`qa-pass5-windows/scripts/drive.py`, `verify.py`, `mut.py`, `mut2.py`).

One validity check on that instrument: the second process (`agenteyes.exe package`) runs with the REAL
roots, not the scratch ones. That is only a fair test if the claim lives in the recording folder. It does:
`capture.lock` / `capture.owner.json` are under the recording dir (`CaptureLock.cs:93`, `:584`, `:597`),
and the dir listing before the second process shows both files there. So the decline is the product's,
not a harness artefact.

Every recording folder is new and kept, outside the repository and outside the owner's
`Videos\AgentEyes` library: `<home>\AgentEyes-QA-evidence\issue113-pass5-qa\<run>\`. Committed here is
scrubbed text only (`qa-pass5-windows/`; 278 path and 46 host-name replacements; the collector refuses to
write if the user name, host name, an email or a banned string survives).

**Kill provenance, every run:** my driver finds the capture ffmpeg ONLY as a child of the evidence host it
just started (parent pid check), and logs each injection as `INJECT (MINE)`. Every other kill is the
product's, attributed from its own `Abandon` log line.

---

## (1) The gate's scenario, both arms - PASS

`drive.py <arm> gate`: start a real screen+mic recording via `/record/start` (`source=mic`, so the START
manifest carries the `PendingMux` the gate set up), two healthy polls 10 s apart (asserted advancing), then
`agenteyes.exe package <dir> --no-frames` from a second process of the same tree, then `/status` + disk
size every 10 s for 120 s, then `/record/stop` and a full verification of the files.

| | **Head `2716e0f`** (`runs/head-gate.*`) | **Control `ad21557`, pre-#156** (`runs/ctl-gate.*`) |
|---|---|---|
| second process | **exit 1 in 0.44 s**: `refusing to package 2026-11-16_145021_video: it is being captured by the capture session of pid 22568 on host <laptop> (started 2026-11-16T19:50:21.62Z). Nothing was muxed, renamed or counted as an attempt` - pid 22568 is my evidence host, the holder | **exit 3 in 2.88 s**: `The process cannot access the file because it is being used by another process.` |
| attempt counted | **no**: `PostProcessing={}` right after it | **yes**: `mux: failed, Attempts 1` written by the foreign process |
| `raw.mp4` | in place, growing | in place, growing (the rename FAILED - see item 5) |
| `recording.mp4` while capturing | **absent at all 12 polls** | **a prefix `recording.mp4` of 834,826 bytes beside the live capture, at all 12 polls** (the mux ran: `MuxInTwoPasses: process mic audio done out=...recording.mp4` at 14:53:38.911 local) |
| `/status` + disk across 120 s | `State=recording CaptureLiveness=growing` AND `raw.mp4` larger on disk at **12 of 12** polls | 12 of 12 (Windows' sharing violation spared the capture) |
| watchdog | raised nothing | raised nothing |
| capture ffmpeg | alive throughout | alive throughout |
| end | `/record/stop` -> 138.45 s; mux `done`, **`Attempts: 1`** (the owner's own) | the owner's mux later `done` with **`Attempts: 2`** - one burned by the foreign process |
| files | `recording.original.mp4` 4101 frames, `recording.mp4` 4101 frames, both full-decode exit 0 / 0 error lines / last-second frame | (verified too - see item 3, it carries the offset) |

Expected vs Actual against the gate's bar - declines, names the holder, counts no attempt, `raw.mp4` not
renamed, no `recording.mp4` during the capture, capture keeps growing, stops normally and decodes:
**all seven met on the head; the control shows the opposite on the two that matter (a published prefix,
a burned attempt)**. The absence on the head is paired with its presence on the control in the same
session, same devices, same script.

Why it holds: #156's guard (`CaptureLock.ThrowIfLive`, `MuxCoordinator.cs:135` / `Package.cs:121`),
which this branch inherits from `main`; the head's log line `[CaptureLock] ThrowIfLive DECLINED: ...` is
in `runs/head-gate.second-process-log-excerpt.txt`. Developer measured 0.5 s and a 726 KB prefix; I
measured 0.44 s and an 834 KB prefix (a slightly later prefix, same defect).

## (2) The watchdog fail-open fix - PASS

Reviewed `RecordingService.cs:1776-1858` and `CaptureWatchdog.cs:238-255`: a throwing read goes to
`CheckFailed` (counted as no growth, exit code still read, published as `check-failed`, re-armed at
`:1812`); any other throw re-arms in the tick's own catch while it is still the live session
(`:1830-1850`), publishing `watchdog-failed` instead of a stale `growing`.

**My own mutations** (`qa-pass5-windows/scripts/mut.py`, `mut2.py`; output in `mutations.txt`), each
applied to the source, test project rebuilt, filter `CaptureWatchdog|WatchdogFailOpen` (40 tests):

| Mutation | Result | Test(s) that failed |
|---|---|---|
| QA-M1: the read's catch just `return;`s (no `CheckFailed`, no publish, no re-arm) | **2 fail** | `Tick_SizeReadThrowsOnce_ReArmsAndStatusSaysCheckFailed_ThenRecovers`, `Tick_SizeReadKeepsThrowing_EndsTheRecordingInsideTheStallWindow_NamingTheError` |
| QA-M2: the outer catch keeps its log but drops the re-arm | **1 fails** | `Tick_ThrowsOutsideTheRead_StatusSaysWatchdogFailed_AndItKeepsWatching` |
| QA-M3: `CheckFailed` ignores the exit code (`exit = null`) | **1 fails** | `CheckFailed_ProcessHasExited_FiresExitedAtOnce_NotAStallLater` |
| **QA-N1 (specificity): change only the wording of `CheckFailed`'s log line** | **0 fail, 40/40 pass** | - |
| baseline after restore | 40/40 pass | - |

Each mutation fails exactly the test named for that behaviour and nothing else; the neutral edit fails
nothing, so the tests are not reacting to any edit. A measurement trap I hit and corrected: my restore
went through Python text mode and normalised CRLF, leaving a whitespace-only diff; I then restored both
files with `git checkout --`, confirmed `git status` clean and rebuilt before any further run.

## (3) `+delay_moov` and the offset - PASS (measured on real devices, both rates, before and after)

Before = `ad21557` (fragmented, no `delay_moov`); after = head. The command line that actually ran was
read from the OS (the capture ffmpeg's command line): before
`-movflags +frag_keyframe+empty_moov+default_base_moof`, after
`-movflags +frag_keyframe+empty_moov+default_base_moof+delay_moov` (`runs/*earlykill*.scenario.log`).

| Build | fps | file | video `start_time` | audio `start_time` | counted video frames | full decode |
|---|---|---|---|---|---|---|
| `ad21557` (before) | 30 | raw (`recording.original.mp4`) | **0.833333** | 0.000000 | 4193 | exit 0 |
| `ad21557` (before) | 30 | final `recording.mp4` | **0.833008** | 0.000000 | 4193 | exit 0 |
| `ad21557` (before) | 5 | raw | **0.800000** | 0.000000 | 210 | exit 0 |
| `ad21557` (before) | 5 | final | **0.800000** | 0.000000 | 210 | exit 0 |
| **head (after)** | 30 | raw | **0.000000** | 0.000000 | 4101 | exit 0 |
| **head (after)** | 30 | final | **0.000000** | 0.000000 | 4101 | exit 0 |
| **head (after)** | 5 | raw | **0.000000** | 0.000000 | 210 | exit 0 |
| **head (after)** | 5 | final | **0.000000** | 0.000000 | 210 | exit 0 |

The 30 fps pair is the two 140 s gate-scenario recordings; the 5 fps pair is two 42 s recordings
(`runs/{ctl,head}-5fps.*`). The defect is real, it is about 0.8 s at both rates (constant in TIME, not in
frames: 25 frames at 30 fps, 4 at 5 fps), it survives the mux, and the head removes it at both rates.
This matches the developer's 0.73-1.00 s, not the synthetic 0.067 s. All stall/kill files in item 5 also
show 0.000/0.000.

The regression test bites: `FragmentedOutputTimelineTests` (2 tests, real ffmpeg) runs in the DEFAULT
gate (2/2 pass with `AGENTEYES_FFMPEG_TESTS` unset); removing `+delay_moov` from `FfmpegArgs.cs:129`
fails **both** (QA-M4, `mutations.txt`); restored bytes identical, 2/2 again.

**Not measured:** which timeline is acoustically true. This laptop's microphone records silence
(-96.7 dBFS, a hardware fault), so no clap test is possible here; the claim that plain MP4's timeline is
the truthful one rests on the mechanism (the edit list), as in the developer's report.

Also reproduced, on BOTH trees, the developer's second finding: at 5 fps the mic audio is ~4.9 s shorter
than the video (before: 38.65 s audio in 42.8 s video; after: 37.86 s in 42.8 s). Pre-existing, not this
PR's; the lead is filing it.

## (4) The price of the fix - CONFIRMED, and through the product rather than raw ffmpeg

`drive.py <run> earlykill --kill-at S`: start via `/record/start`, find the capture ffmpeg (psutil, child
of my host), wait until the PROCESS is S seconds old, `TerminateProcess(pid, 137)` (MINE), then let the
product do whatever it does.

| Build | killed at (ffmpeg age) | `raw.mp4` | product's verdict | what the product published |
|---|---|---|---|---|
| head | 1.01 s | **0 bytes** | `capture exited ... (code 137) ...; raw.mp4 holds 0 bytes`, idle at the next poll | mux **`failed`** (`could not read the audio channel count of ...raw.mp4 (ffprobe said: '')`), `PendingMux` kept, no `recording.mp4` |
| head | 1.80 s | **0 bytes** | same | same |
| head (positive control) | 3.00 s | 247,594 bytes | `... holds 247594 bytes` | mux `done`; raw 14 frames, `recording.mp4` 14 frames, both full-decode exit 0, last-second frame, start 0.000/0.000 |
| `ad21557` (before) | 1.00 s | 1,241 bytes (header) | `... holds 1241 bytes` | mux **`done`** with a **262-byte `recording.mp4` that has NO streams** (`Output file does not contain any stream`); raw: 0 frames, `Cannot determine format of input 0:0 after EOF`; thumbnail `failed` |

**What is recoverable from each:** nothing, in both. Before the fix the early kill left a header with zero
frames; after it, an empty file. No frame is lost that was recoverable before. The developer's statement
("0 bytes where it previously left a zero-frame header") is confirmed.

**One thing the developer's raw-ffmpeg measurement could not show, because it did not go through the
product:** before the fix, the zero-frame header was muxed into a stream-less `recording.mp4` and the
manifest said **`mux: done`** - a false "done". After the fix, the 0-byte capture makes the mux fail and
say so. So inside the first ~2 s the new behaviour is more truthful, not less. The cost against this
issue's promise ("an unclean end leaves a playable file") is real but bounded: a crash or power loss in a
capture's first ~2 s leaves no playable file - which was equally true before, where it left an unplayable
one. The watchdog's own earliest verdict is the 45 s start-up window (`StartWatchdog: ... start-up 45s`),
so a watchdog kill is never inside the gap; only an external crash/kill is.

Minor, not a defect against any criterion: the head's failure text for an empty capture
(`could not read the audio channel count ... (ffprobe said: '')`) is accurate but does not say "the
capture is empty"; and `PendingMux` stays pending, so a repair pass may retry an empty file. Noted for the
lead, not filed.

## (5) The Windows run - PASS

All on the head binary (`runs/head-stall.*`, `runs/head-kill.*`), 30 fps.

**Stall** - `NtSuspendProcess` on ffmpeg pid 8888, child of my host 15388 (**injection MINE**):

```
19:57:32.847Z /status [healthy 1] State=recording CaptureLiveness=growing CaptureBytes=322704
19:57:42.853Z /status [healthy 2] State=recording CaptureLiveness=growing CaptureBytes=702318
19:57:43.746Z INJECT (MINE): NtSuspendProcess(ffmpeg pid 8888) -> 0
19:57:45.771Z /status State=recording CaptureLiveness=not-growing CaptureBytes=702318 CaptureSecondsSinceGrowth=2
   ... not-growing, CaptureSecondsSinceGrowth climbing every 2 s ...
19:58:28.143Z /status State=recording CaptureLiveness=not-growing CaptureSecondsSinceGrowth=44.2
19:58:30.173Z /status State=idle LastStopFailed=True LastCaptureFault=capture stalled: raw.mp4 stopped growing
              at 702318 bytes and has not grown for 46s (limit 45s); last growth 2026-11-16T19:57:42.845Z
```

- Verdict detected **19:58:29.045Z = 46.2 s after the last growth**, 45.3 s after the injection (inside 60 s).
- Log line at detection time (local clock UTC-5):
  `14:58:29.047 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=63.1s (detected 2026-11-16T19:58:29.044Z) ... capture stalled: raw.mp4 stopped growing at 702318 bytes ...`
- **The kill was the product's**: `14:58:29.146 [FfmpegRecorder] Abandon: ... ended in 91ms (killed, exit -1, last reported position 16.26s)`; my script found pid 8888 gone afterwards and killed nothing.
- Manifest: `CaptureFault=stalled`, `CaptureFaultDetectedUtc=2026-11-16T19:58:29.0447077Z`, `CapturedSeconds=15.83`; mux `done`, `Attempts: 1`.

**Kill** - `TerminateProcess(pid 14544, 137)`, child of my host 18660 (**this kill is MINE**):
fault `capture exited: the capture process exited (code 137) while recording; raw.mp4 holds 707140 bytes`,
detected 19:59:23.488Z = **1.04 s after the kill**; `Abandon ... ended in 0ms (had already exited, exit 137)`
- the product killed nothing. Manifest `CaptureFault=exited`, `CaptureFaultDetectedUtc=2026-11-16T19:59:23.4883356Z`.

**The files** (`runs/head-stall.verify.txt`, `runs/head-kill.verify.txt`):

| Run | file | counted video frames | duration | full decode | frame from the last second |
|---|---|---|---|---|---|
| stall | `recording.original.mp4` (raw) | 429 | 15.83 s | exit 0, 0 error lines | yes (`-sseof -1`, PNG written) |
| stall | `recording.mp4` | 429 | 15.17 s | exit 0, 0 error lines | yes |
| kill | `recording.original.mp4` (raw) | 426 | 16.00 s | exit 0, 0 error lines | yes |
| kill | `recording.mp4` | 426 | 15.23 s | exit 0, 0 error lines | yes |

The amended criterion (counted frames, full decode, a frame from the last second) is used throughout;
"moov present" is used nowhere.

**Size reads by path while ffmpeg holds the file work on Windows:** `CaptureBytes` advanced at every
healthy poll of every run and froze at the stall's byte count (702318) the moment the writer was suspended.

**The rename on Windows - measured, a sharing violation** (control arm, `runs/ctl-gate.second-process-log-excerpt.txt`):

```
14:53:39.115 [pid 8516] [ERROR] System.IO.IOException: The process cannot access the file because it is being used by another process.
14:53:39.115 [pid 8516] [ERROR]    at System.IO.FileSystem.MoveFile(String sourceFullPath, String destFullPath, Boolean overwrite)
14:53:39.115 [pid 8516] [ERROR]    at AgentEyes.OriginalBackup.Preserve(String dir, String mode, AudioSourceKind src) in ...\OriginalBackup.cs:line 72
```

So on Windows the gate's macOS/Linux chain (rename -> `FileSize` null -> false stall -> kill) cannot
start: the rename fails. Windows' damage on a pre-#156 binary is the burned attempt and the stray prefix
(item 1), not the kill. Consequently the head's "file left its path" verdict wording is reachable on
Windows only through unit tests, not at runtime.

## (6) D3 and the merge - PASS

- `CaptureOutputLiveness` is #113's type (`Video/CaptureWatchdog.cs:21`); every #113 use binds to it
  (`RecordingService.cs:234`, `:1790`; 10 uses in `CaptureWatchdog.cs`, 11 in `CaptureWatchdogTests.cs`).
  Every remaining whole-word `CaptureLiveness` is either #156's type in `CaptureLock.cs` / `Thumbnails.cs:160`
  (`Thumbnails.cs:160` is on `main` unchanged), or the `/status` wire property `RecordStatus.CaptureLiveness`
  (`RecordingService.cs:115`) and the tests asserting its string values. No stale reference; and the build
  succeeding is consistent with that, since a mis-bound use would not compile (the two types differ).
- `int ProcessId { get; }` is declared once on `IScreenRecorder` (`Video/IScreenRecorder.cs:30`),
  implemented once (`FfmpegRecorder.cs:78`), consumed by #156's `NoteCaptureWriter` (`RecordingService.cs:754`, `:768`).
- Merge: `origin/main` is `277a9bc` and is an ancestor of the head; `git merge-tree --write-tree origin/main HEAD`
  is clean and yields tree `be181b3f...`, **identical to the head's own tree**. GitHub: `mergeable=MERGEABLE`,
  `mergeStateStatus=CLEAN`.

## (7) Gates, on the tree that would land - PASS

**Local, this laptop, head `2716e0f`** (`qa-pass5-windows/gate-test.txt`):
- `dotnet build AgentEyes.sln -c Release` -> `0 Error(s)` (24 warnings, xUnit analyzer style).
- `dotnet test AgentEyes.sln -c Release` -> AgentEyes.Tests **2523 passed, 16 skipped, 0 failed (2539)**,
  1 m 6 s; AgentEyes.Tests.DesktopApp **23 / 0**.
- Since the merge tree equals the head tree (item 6), this local run IS a run of the tree that would land.

**CI, read from the logs, on the MERGE ref:** run **43940450779** (`pull_request`), whose checkout says
`HEAD is now at 6cf0988 Merge 2716e0f884084f6993ad4a1e4ac7dca3c6708d03 into 277a9bcf71c7f82547353177299178efc1020327`
- and `277a9bc` is still `main`'s head, so this is the merge result that would land, not a stale base:

| Job | Default | With `AGENTEYES_FFMPEG_TESTS=1` |
|---|---|---|
| windows AgentEyes.Tests | 2523 passed / 16 skipped / 0 failed | 2542 / 1 / 0 |
| windows DesktopApp | 23 / 0 / 0 | 23 / 0 / 0 |
| linux | 472 / 2 / 0 | 473 / 1 / 0 |
| macos | 431 / 1 / 0 | - |

Push-event run 43940439203 at the same head: also `success`.

---

## Observations (not defects of this PR)

1. **Stale binaries on this machine, all pre-#156 except one** (the recorded limit: a pre-#156 binary
   ignores `capture.lock`):
   - `src\AgentEyes.Core\bin\Release\net8.0-windows10.0.19041.0\agenteyes.dll` = `1.11.11+0f54fba...` - **pre-#156**
   - `src\AgentEyes.Core\bin\Release\net8.0-windows10.0.19041.0\win-x64\agenteyes.dll` = `1.11.11+93681fa...` - **pre-#156**
   - `src\AgentEyes.Core\bin\Release\net8.0\agenteyes.dll` = `1.11.11+9f45b33...` - post-#156 (an intermediate pass-5 commit)
   - **The INSTALLED app** (`%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe` and `agenteyes.exe`) is
     `1.11.11+93681fa...`, **pre-#156, and was running during this session**. Its repair pass can still do
     to a capture by a current binary what the gate rejected #113 for (on Windows: a burned attempt and a
     stray prefix). Kept my scratch library outside its library root; did not touch it.
2. 5 fps audio shortfall (item 3) - pre-existing on both trees, the lead is filing it.
3. In every run the host's post-processing also ran a `package` step that failed with `Not signed in to
   DevThrottle`, on both trees - pre-existing, nothing was uploaded.
4. Early-kill failure wording and the pending retry of an empty capture (item 4) - for the lead.

## What this report does NOT cover

- **macOS and Linux at runtime** - routed elsewhere. `+delay_moov` changes their command lines too;
  criteria 1, 2 and 5 and both streams' `start_time` must be re-run there.
- **The shipped hosts.** The evidence host runs the production service and control server, but not
  `agenteyes serve` and not the WPF app; neither ran this code here. The HUD's response to a watchdog end
  is not verified at runtime.
- **The 15-minute repair pass as the second process.** Only `agenteyes package` was driven.
- **The criterion-4 ten-minute healthy run** and criteria 3/6/7 beyond what the runs above show - not
  re-run this pass (QA round four owns them; this pass's code changes touched the tick's error paths and
  the container flags, which items 2, 3 and 5 cover).
- **Acoustic A/V sync.** The microphone records silence (hardware fault, -96.7 dBFS); every audio-bearing
  claim here is about timestamps and stream presence, not sound.
- **The watchdog's "file left its path" verdict at runtime** - unreachable on Windows (the rename fails)
  and prevented by #156 elsewhere; unit-tested only.
- **The after-verdict re-arm path** (a throw between verdict and stop) - a shape with no test reaching it,
  as the developer states; my mutation QA-M2 covers the pre-verdict re-arm, which is the same code.
- **A mixed-version machine** - a pre-#156 binary still ignores `capture.lock`; inherent, recorded above.

## Housekeeping

- The owner's recordings, presets, audio settings and running app were not touched or driven. My CLI
  invocations (`screens`, two `package` runs) wrote log lines only to the owner's `%LOCALAPPDATA%\AgentEyes\logs`.
- Every ffmpeg I suspended or terminated was a child of an evidence host I had just started (parent pid
  check); attributed above. No evidence recording was deleted.
- The scratch worktree for `ad21557` was removed after use.
