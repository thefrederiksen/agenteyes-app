# Issue #156 - Developer handoff to QA

Branch `issue-156-capture-ownership`, based on `main` `497e9d4`. Implementation context for QA, not
proof: QA produces its own running-app proof.

I believe this is finished, with the platform limits stated in "What this does NOT cover". Pass 2 (after the Linux QA failure) is at the end of this note.

## The defect, in one paragraph

The capture's claim on its recording directory was in-memory (`RecordingWorkset`), so a second
AgentEyes process saw "no claim" on a directory still being captured into. Its repair pass
(`PostRecording.Resume`) or `agenteyes package` then ran the deferred mux against the live `raw.mp4`.
On today's plain MP4 that fails and burns one of #128's three attempts; three foreign passes burn all
three, and the healthy recording is then refused forever. With #113's fragmented container the same
pass SUCCEEDS on the prefix: it publishes a truncated `recording.mp4`, clears `PendingMux`, and
renames `raw.mp4` out from under the live ffmpeg. All of these were reproduced on `main` here (below).

## What was implemented

| Change | File |
|---|---|
| `CaptureLock`: the capture's cross-process claim - an exclusive OS lock on `<recording>/capture.lock` held for the whole capture, plus `capture.owner.json` naming host, pid, process start time and every ffmpeg writer the capture started | `src/AgentEyes.Core/CaptureLock.cs` (new) |
| `BeginSession` takes it straight after the in-process capture claim, before any writer starts; a refusal gives the in-process claim back and fails the start without touching the directory | `RecordingService.cs` |
| The lock is ATTACHED to the in-process claim (`RecordingWorkset.Attach`) and released by the same `Release`, so every path that ends a capture (stop, start rollback, a stranded camera finally letting go) frees both, never one without the other | `RecordingWorkset.cs` |
| The video and camera ffmpeg writers are recorded in the owner record (`NoteCaptureWriter`) and registered with `FfmpegChildRegistry`, so the next service start reaps an ffmpeg whose owner is gone | `RecordingService.cs`, `Video/FfmpegRecorder.cs` (`ProcessId`), `Video/FfmpegChildRegistry.cs` (pid-based `Register`) |
| The finalizing paths decline a live capture BEFORE counting an attempt: `PostRecording.Execute` (the repair pass's resume AND the post-stop pipeline), `MuxCoordinator.RunForPackage`, the whole `agenteyes package` command (`Package.RunAsync`), and `RecordingService.FinalizePending` itself as the backstop every path reaches | `PostRecording.cs`, `MuxCoordinator.cs`, `Package.cs`, `RecordingService.cs` |
| Criterion 5: each counted attempt records who consumed it - `PostProcessing.<stage>.AttemptedBy`: `attempt N at <utc> by <process> pid <pid> on <host>` | `Manifest.cs`, `PostRecordingState.cs` |
| Regression found in self-review: `capture.lock` / `capture.owner.json` are not "capture bytes", or a start that failed before any writer ran would keep its empty directory (the #155 litter) | `RecordingStopSequence.cs` |
| IL write inventory: the new file-write call sites are pinned (nine lines; two are read-only opens through the FileStream ctor, labelled as such) | `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` |
| The repair scans skip a recording another process is capturing: `PostRecordingPlan.FindUnfinished` (so the repair pass never admits it) and the thumbnail repair (`Thumbnails.NeedsThumb`, which would otherwise run ffmpeg on a mic-only video's growing `recording.mp4`) | `PostRecordingPlan.cs`, `Thumbnails.cs` |
| `RecoveryManifest` does not list the claim files as capture media | `RecoveryManifest.cs` |
| 29 tests, compiled into all three suites | `tests/Shared/CaptureOwnershipTests.cs` |
| Reproduction harness and recorded evidence | `docs/cencon/proof/issue-156/` |

## The two hard parts, answered

### Why it holds across processes, and across hosts

- **Two processes, one machine.** `capture.lock` is opened `FileShare.None` - the primitive `mux.lock`
  already uses. On Windows a second open is a sharing violation; on macOS and Linux .NET takes an
  exclusive `flock`, which every AgentEyes process honours. The finalizing side probes it (open and
  close at once) and never takes the claim. Proven with a separate OS process holding the lock
  (`TryAcquire_HolderProcessKilled_ClaimIsFreedByTheOs`: powershell on Windows, python3 `flock` on
  Unix) and with the real binary in the harness.
- **Two hosts, one shared library.** The lock holds where the file system carries it: SMB enforces the
  Windows share mode on the server for every client; on Linux, `flock` over NFS is mapped to NFS
  byte-range locks. A file system that does not carry locks between clients reduces this to the
  per-machine guarantee, and that cannot be detected from here. **Not measured** - there is no
  shared-library rig on this machine.
- **A writer recorded by another host** cannot be probed from this host. A free lock with a foreign
  writer is reported LIVE, with the exact fix (`Finalize it from <host>, or - once you have checked
  nothing is writing into <dir> - delete capture.owner.json`), rather than guessed dead. That host's
  own repair pass, which CAN probe its writer, takes it over once the writer is gone.

### What happens when the holder dies, and what makes a claim stale rather than old

**Measured first, because it decides the design:** on this Mac a capture-style ffmpeg (stdin, stdout
and stderr piped, as `FfmpegRecorder` starts it) whose parent is SIGKILLed is reparented to pid 1 and
**keeps writing** - 3.6 MB to 13.1 MB in the 8 s after the parent died. So a lock held only by the
AgentEyes process is not enough: the OS would free it while the orphan still writes `raw.mp4`, and
the next pass would mux the prefix and rename the live file - the gate's defect by another route.

So a claim is **stale exactly when both hold** (no timeout, no age - a capture can run for hours):
1. `capture.lock` can be taken - the OS allows that only once the holder's handle is closed, i.e. it
   released it or died; AND
2. no writer recorded for this host is still the same running process (pid AND start time - the test
   `FfmpegChildRegistry.ReapOrphans` already uses, so a reused pid is never mistaken for the writer).

When the owner dies with its writer still running, the recording is declined and the decline names
the writer's pid. **That is not a lock nobody can break:** the writer is registered with
`FfmpegChildRegistry`, and the next service start (launchd/systemd restart a crashed agent) reaps an
ffmpeg whose owner is provably gone; the next repair pass then repairs the recording. Posture note:
an orphaned capture ffmpeg is a screen/camera recorder with no indicator and no owner, so reaping it
is in line with the "visible, controllable" stance, not against it.

An unreadable `capture.owner.json` with a free lock fails loudly (`UsageException` with the fix)
instead of guessing - it can only come from damage outside AgentEyes, because the record is written
atomically (temp file + rename).

### mux.lock or the workset? (the issue's question)

Neither alone. `mux.lock` is held for one mux, and "nobody holds mux.lock" is what proves a dead mux
attempt to `MuxCoordinator.Reconcile`; overloading it with a capture lifetime would blur that proof
and give a misleading refusal ("another process is muxing"). So the capture got its OWN lock using the
same primitive, and it is owned BY the workset claim (`RecordingWorkset.Attach`) so the two lifetimes
cannot drift apart. On the "single cross-process primitive" question: this change does NOT refactor
#6, #10 or #143. `CaptureLock` is a reusable shape (OS lock + atomic owner record + writer liveness)
that #10/#143 could adopt, but that is their decision, not built here.

## Acceptance criteria -> how QA verifies each

All harness runs: `docs/cencon/proof/issue-156/both-arms.sh <agenteyes> <scratch> <plain|frag>`
runs the live arm and both dead-owner arms **concurrently in one run**. Use a scratch dir; it never
touches a real recording. **Read the script's header**: the stand-in for a capture is a lavfi ffmpeg
plus a python `flock` owner (this Mac's shell has no Screen Recording grant).

| # | Criterion | Implemented by | How QA verifies | My evidence |
|---|---|---|---|---|
| 1 | Race reproduced deterministically before the fix | - | Run the harness against a `main` binary. Plain: each foreign `package` exits 1 `could not read the audio channel count`, Attempts 1 -> 2 -> 3, then the healthy recording is refused forever ("failed 3 time(s)"). Frag: the prefix is published, `raw.mp4` renamed while still growing | `evidence-main-497e9d4-plain.txt`, `evidence-main-497e9d4-frag.txt` |
| 2 | Same scenario: attempt count stays 1, the decline names the holder | `CaptureLock.Inspect` / `ThrowIfLive`, guards in Execute / RunForPackage / Package / FinalizePending | Harness on the branch binary, `live-package` arm: three declines `refusing to package ... being captured by the capture session of pid <P> on host <H>`, no attempt recorded; after the owner stops, finalized with `Attempts 1`. For the repair-pass route: unit test `Resume_LiveCaptureInAnotherProcess_ThreeTimes_...` and, on Linux/Windows, the harness `live-repair` arm (log line `[PostRecording] Execute: ... DECLINED by pid ... - it is being captured by ...`) | `evidence-fix-plain.txt`, `evidence-fix-frag.txt` |
| 3 | A foreign process cannot consume the budget | declines happen before `NoteStarted` | Same `live-package` arm: three foreign passes, then the owning recording completes at `Attempts 1`. Unit tests `Resume_LiveCapture..._ThreeTimes_...`, `RunForPackage_LiveCapture..._WithoutCounting`, `FinalizePending_LiveCapture..._BeforeTouchingAFile` | as above |
| 4 | A stale claim does not strand a recording | stale = lock free AND no live recorded writer; writers reaped at service start | `dead-owner` arm: owner SIGKILLed after its writer ended, stale `capture.owner.json` left behind -> repaired first time. `orphan-writer` arm: owner SIGKILLed, writer still running -> declined naming the writer pid; writer ends -> repaired. Unit tests `Resume_CaptureOwnerDiedAndWritersExited_RepairsTheRecording`, `Resume_OwnerDeadButWriterStillRunning_DeclinesUntilTheWriterEnds`, `Inspect_PidReusedByAnotherProcess_IsNotLive` | both fix evidence files |
| 5 | The manifest says who consumed an attempt | `PostStageRecord.AttemptedBy` | Any finalized recording's `manifest.json`: `PostProcessing.mux.AttemptedBy` = `attempt 1 at <utc> by agenteyes pid <pid> on <host>`. Unit test `NoteStarted_RecordsWhoConsumedEachAttempt` | fix evidence, `mux=` lines |
| 6 | Both hosts still work side by side | guards only fire on a live claim | `package` on a recording no live capture holds succeeds (the post-release and dead-owner runs). `translate` and rename are not touched by this change (no guard added, same code). QA: on Windows, record with the app, stop, then `agenteyes package <dir>` from the CLI -> works; and package/rename a recording while the app records a DIFFERENT one -> works | dead-owner / post-release runs |
| 7 | Gates green on all three platforms, CI across more than one run | - | Windows: `dotnet build AgentEyes.sln -c Release` + `dotnet test AgentEyes.sln -c Release` (I cannot build the .sln on a Mac). macOS/Linux suites below. CI on the PR | counts below |

### The positive control, explicitly (the lead's requirement)

Both arms run in the SAME harness invocation (`both-arms.sh`, three arms concurrently):
- **Arm 1 - live capture touched by a second process:** left alone. No `recording.mp4`, `raw.mp4`
  never renamed and still growing, no attempt recorded, and the declining process prints why, naming
  the holder's pid and host.
- **Arm 2 - genuinely orphaned recording, owner dead:** repaired exactly as on `main` - mux done,
  `PendingMux` cleared, `Attempts 1`. Shown twice: writer already gone (`dead-owner`), and writer
  outliving its owner then ending (`orphan-writer`).

Mutation checks I ran against the unit tests (each made the named tests FAIL, then was reverted):
the Execute guard removed (2 tests failed), a "refuse everything" guard (4 failed, including both
repair-the-orphan tests), the release not disposing the lock (1), the orphan-writer check removed (2),
`capture.lock` counted as capture bytes (1), the probe made exclusive again (1), the host id ignored
(1), release ignoring a still-running writer (1), the lock handle leaked on a throw (1).

## Self-review round (`/code-review` on the diff), and what changed because of it

| Finding | Outcome |
|---|---|
| Two finalizers probing the same recording at the same instant read each other as a holder (the probe opened `FileShare.None`) - the second could fail inside a counted attempt | **Fixed**: the probe is a SHARED open, refused only by the capture's exclusive handle. Test `Inspect_AnotherProbeOpenAtTheSameInstant_IsNotLive` |
| `capture.owner.json` deleted between the existence check and the read threw `FileNotFoundException` | **Fixed**: read as "released while being read", not live |
| A throw in `TryAcquire` after the exclusive open leaked the handle | **Fixed** (try/finally). Test `TryAcquire_OwnerRecordUnreadable_ThrowsAndDoesNotLeakTheLock` |
| A writer that died instantly gave the framework's "process is not running" message | **Fixed**: `UsageException` saying the ffmpeg exited immediately, pointing at its log. Test `NoteWriter_WriterAlreadyExited_ThrowsSayingSo` |
| Release deleted the owner record even if the stop could not end a writer, publishing a file still being written | **Fixed**: a still-running writer keeps the record and its reap entry; the claim stays live through the dead-owner check. Test `Dispose_AWriterIsStillRunning_KeepsTheClaimLiveForOtherProcesses` |
| macOS renames the host when the network changes, so a machine's own record would look foreign and strand the recording | **Fixed**: the record carries a per-machine host id (`<data>/host-id`); same host = same id OR same name. Test `Inspect_HostRenamedButSameHostId_IsCheckedLocally` |
| `RecoveryManifest` listed the claim files as media | **Fixed** |
| `FindUnfinished` and the thumbnail repair still checked only the in-process claim | **Fixed** for both repair scans. Test `FindUnfinished_RecordingCapturedByAnotherProcess_IsSkippedUntilTheCaptureEnds`. The user-initiated Library/REST **delete** is NOT guarded: deleting a recording is the user's explicit act, outside this issue's repair/resume scope - flagged for the Product Agent rather than changed here |
| `package` checks three times (Package, RunForPackage, FinalizePending) | **Kept**: each is a different boundary - the whole command, the counted attempt (it must decline BEFORE `NoteStarted`), and the backstop. A probe is one open and one small JSON read |
| `RecordingWorkset.DisposeAttached` catches and logs | **Kept**: it runs inside every release, which is called from finally blocks; a release must not fail half-done. Same isolation this class already applies to its `Released` subscribers |

## Gates (run by me, on this Mac)

This Mac has no .NET 8 SDK or runtime: everything below is the .NET 10.0.403 SDK, net8.0 targets run
with `DOTNET_ROLL_FORWARD=LatestMajor`.

| Gate | Result |
|---|---|
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` | Build succeeded, 0 errors (1 pre-existing warning, `AlwaysOnEngine.cs:901`) |
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux` | Build succeeded, 0 errors (same warning) |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | **307 passed, 0 failed** (278 on `main` before this change) |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | **314 passed, 1 skipped, 0 failed** (285 + 1 skipped on `main`) - built and run ON THIS MAC, so it is **not a Linux run**; the skip is the existing `MachineLocalAppData_OnLinux_IsTheXdgDataHome` |
| CI on PR #162 at `755ba01` (two runs: 43863426513, 43863464370), counts read from the logs | each run: **windows 2410 passed / 4 skipped / 0 failed**, **linux 315 / 0** (a real Linux runner), **macos 307 / 0**. The 4 Windows skips are the existing `MicChainSampleRateTests` (#146), none of this change's. The Windows-only `ManifestWriterIlTests` inventory passed with the new pinned entries |
| Windows `AgentEyes.sln` build + test on a developer machine | **NOT RUN by me - cannot build on macOS** (CI above is the Windows evidence I have). QA must run it on Windows. The shared tests are linked into the Windows suite too, and `ManifestWriterIlTests` (Windows only) gained five pinned entries |

## What this does NOT cover - please read

- **No real screen capture was started here** (no Screen Recording grant for a shell on this Mac). The
  real `BeginSession` -> `CaptureLock` -> `NoteCaptureWriter` wiring is exercised by no test I could
  run. QA please verify on Windows with the real app: start a recording (`POST /record/start`), then
  confirm `<recording>\capture.lock` exists and `capture.owner.json` names the app's pid and the
  ffmpeg's pid (and a `HostId`); from a CLI run `agenteyes package <that dir>` -> it must exit 1 with `refusing to
  package ... being captured by the capture session of pid <app pid>`; `/status` still recording;
  stop -> `capture.owner.json` gone, the recording finalizes with `Attempts 1` and `AttemptedBy`
  naming the APP. On Windows the gate expected the old foreign mux to fail on a sharing violation
  rather than rename - not measured by me either way.
- **The repair-pass arm (`live-repair`) was NOT run here, and must not be on macOS.** During
  development I ran `agenteyes serve` from `main` under an isolated HOME and found that .NET on macOS
  ignores HOME for the Movies and Application Support folders: that service's startup pass scanned
  the owner's REAL recordings folder. It was read-only - its log shows 62 recordings all "needs
  package", skipped because the service is not signed in, 0 missing thumbnails, housekeeping
  `reportOnly=True`, one orphan record left alone; no attempt counted, no manifest written, no file
  under `~/Movies/AgentEyes` modified - and the lead was told at once. The harness now REFUSES that arm
  on macOS and, on Linux, checks the service's recordings root before its first pass. The repair
  route is covered here by unit tests driving the real `PostRecording.Resume` - the Implementation
  Lead's ruling, after independently confirming the pass was read-only. The missing supported way to
  point a real `agenteyes` process at a scratch library is now issue #161; it is deliberately not
  part of this change.
- The package runs append their log lines to the machine's real AgentEyes log on macOS (same cause);
  they touch no recording outside the scratch directory.
- **Shared library across two machines: not measured** (see above).
- **The CLI's own `agenteyes video` capture** writes its manifest only at the end, so a live CLI capture
  is not visible to any pass mid-capture today; it takes no `CaptureLock`. Unchanged by this issue.
- **Order with #113:** this should land FIRST; #113's fragmented container is what makes the foreign
  mux destructive, and with this change a live capture is never muxed by anyone but its owner.
  #113's own watchdog-by-path concern is then unreachable through this route, but #113 still owns it.

## Notes for QA (standing reminders)

- The focus-free layers are REST (`http://127.0.0.1:7882`), UIA and PrintWindow. Never force-foreground
  the app and synthesize input without warning the human first.
- The recording HUD is capture-excluded: assert recording state via `/status` or UIA, not a screen grab.
- Smoke scope: this touches the capture start and post-processing, so `api-smoke.ps1` (record/stop/
  package round trip) is the relevant one; `gui-smoke.ps1` is not needed for this change.

## CenCon impact

No component-map drift: no `docs/cencon/` document describes the claim or lock machinery. Privacy
posture: strengthened, not weakened - an ownerless capture ffmpeg (an indicator-less recorder) is now
reaped at the next service start. No new stealth path, no new data leaving the machine.

---

## Pass 2 - after the Linux QA failure (QA comment 6916902446, lead's rulings 6916944268)

Developer pass on `devlinux`, in the clone `/home/soren/ReposFred/agenteyes-app`, branch
`issue-156-capture-ownership` (PR #162). This pass started from QA's report commit `760e6a7` (code head
`d245037`). It covers D1, D2 and G1, and names D3 for #113's next pass. I believe it is finished; the one
open platform gap (Windows, under D1) is stated below and went to the lead as a decision.

**Not touched, as ruled:** the lock and the owner file (`TryAcquire`, `NoteWriter`'s record, `Inspect`,
`FreeLockLiveness`, `Dispose`), the four decline sites, and the attempt-counting order. D2's new call is a
separate line AFTER each decline site has already passed. It runs only on a recording that site has
just admitted, so no decline and no count changes. Only one line of `NoteWriter` changed: it now
registers the writer as a capture writer (D1).

### D1 - the reaper ends an orphaned capture writer with SIGTERM, waits a measured bound, and only then SIGKILLs

| Change | Where |
|---|---|
| Registry records carry `CaptureWriter`; `CaptureLock.NoteWriter` registers through `RegisterCaptureWriter`. A post-processing child stays `CaptureWriter=false` and is still killed at once, because its output is disposable and the mux re-runs | `Video/FfmpegChildRegistry.cs`, `CaptureLock.cs` (`NoteWriter`, one line) |
| `ReapOrphans` sends SIGTERM to every orphaned capture writer first, then waits for all of them against ONE shared deadline (`CaptureWriterGrace`). It SIGKILLs (`KillTree`) only a writer that is still the same process when the deadline passes. Every outcome is its own report and log line: `SIGTERM, exited after N s (graceful: never killed)` / `SIGTERM, did NOT exit within 10s - SIGKILLED` / `this platform has no graceful stop for it, so it was KILLED at once` | `Video/FfmpegChildRegistry.cs` |
| SIGTERM is `kill(2)` through `libc` (`RequestGracefulExit`): ESRCH counts as delivered (the wait then sees the process gone), and any other errno throws with the exact `kill -TERM <pid>` fix | `Video/FfmpegChildRegistry.cs` |
| The reap now runs in the BACKGROUND (`Task.Run` in `RepairService.Start`), because it can wait up to the bound and the tray app calls `Start` on its UI thread. Every pass `await`s it first (`RunAsync`), and so does housekeeping (`HousekeepAsync`), which the Control API can start without a pass. So nothing that `Start` used to precede runs before the reap ends | `RepairService.cs` |

**The bound and where it comes from.** I measured real orphaned captures on `devlinux`: the recorder's
own `X11VideoCapture` arguments (x11grab 1920x1080 at 30 fps, pulse mic, libx264 veryfast crf 23, AAC
128k, plain MP4). Each one was orphaned exactly the way the reaper finds them: the parent exited at once,
stdin at EOF, stderr a broken pipe, SIGPIPE ignored (`SigIgn=...1001000`, as inherited from .NET). Then
SIGTERM, and the clock runs until the process is gone. Harness:
`pass2/harness/sigterm_bound.py`; raw results: `pass2/sigterm-measurements.jsonl`.

| Capture length before SIGTERM | Runs | SIGTERM -> exit (s) | File after |
|---|---|---|---|
| 10 s | 5 | 0.084-0.145 | complete moov, video+audio, `ffprobe` duration 9.73-9.87 s |
| 60 s | 3 | 0.121-0.127 | complete, 59.87 s |
| 300 s | 2 | 0.113-0.117 | complete, 299.87 s, 51 MB |
| 1800 s (30 min) | 1 | 0.117 | complete, 1799.77 s, 298 MB, video+audio |
| 60 s, all 32 cores busy (32 busy loops) | 2 | 0.252-0.269 | complete, 59.8 s |
| **In the product's own reap** (the control arm below: a real serve SIGKILLed mid-capture, the next serve's start-up reap) | 1 | **0.13** | complete, recovered, decodes (below) |

**Bound: 10 s** (`FfmpegChildRegistry.CaptureWriterGrace`). Headroom: about **37x** the worst measured exit (0.269 s, full CPU load) and about **69x** the worst idle exit (0.145 s), drawn from 13 standalone runs plus the in-product reap. The load arm stayed in the same order of magnitude as idle (0.25 s against 0.12 s), and the 30-minute arm matched the 10-second one, so neither gave a reason to move the bound. Exit time does not grow with
capture length: a plain MP4's moov is written in one pass at the end, and 30 minutes of samples is a small
index. The cost is flushing the x264 lookahead and the AAC tail, which is fixed. 10 s matches the
recorder's own clean stop, which already gives ffmpeg 8 s after `q` (`FfmpegRecorder.Stop`). The bound is
SHARED: all orphans are signalled first and waited for together, so a reap of several writers still ends
within one bound. A test pins the value (`CaptureWriterGrace_IsTheMeasuredBound`), so changing it has to
be deliberate.

**Measured on Linux only.** macOS is reachable by its CI suite, not by me. The real-process tests below
run there and prove that SIGTERM is delivered and handled through `libc` on macOS. They do not prove the
macOS finalize TIME of an avfoundation capture. That is a measurement for a Mac seat.

**Tests that pin the order** (`tests/Shared/CaptureWriterReapTests.cs`, compiled into all three suites):
- `ReapOrphans_CaptureWriterIgnoresSigterm_SigtermThenWaitThenSigkill` - THE pin. The call order must be
  exactly `TERM 500`, `KILL 500`, and the kill must come after the bound.
- `ReapOrphans_CaptureWriterExitsOnSigterm_IsNeverKilled`, `..._TwoStubbornCaptureWriters_BothSignalledFirstAndOneSharedDeadline`,
  `..._PostProcessingChild_KilledAtOnceWithoutAGracefulRequest` (control), `..._NoGracefulRequestOnThisPlatform_KillsAtOnceAndSaysSo`,
  `..._CaptureWriterOwnerAlive_NoSignalSent` (control).
- Real processes and the real signal and kill: `ReapOrphansIn_RealWriterThatHandlesSigterm_ExitsGracefully`
  (exit code 0, so its own trap ran: SIGTERM, not SIGKILL) and
  `ReapOrphansIn_RealWriterThatIgnoresSigterm_IsSigkilledAfterTheBound`. On Windows these assert that
  platform's contract instead of being skipped.
- Mutation check, run here: with the reaper changed to kill before SIGTERM, 6 of these failed, the pin
  among them.

**OPEN GAP - Windows, sent to the lead as a decision, not built.** Windows has no SIGTERM. A capture ffmpeg
orphaned there (capture writers are not in `KillOnCloseJob`) can only be asked to finish through
`AttachConsole` + `GenerateConsoleCtrlEvent`. I cannot build or measure that from Linux. This pass makes
Windows EXPLICIT rather than silent: `RequestGracefulExit` returns false, the writer is killed at once, and
the reap says `this platform has no graceful stop for it, so it was KILLED at once; its recording may not be
recoverable`. So on Windows a crashed plain-MP4 recording is still lost, as before this issue. The lead
ACCEPTED this as it stands (fleet message, 2026-11-16 13:14 UTC). The follow-up is **issue #166**, which
records the candidate routes and why the reaper, a different process with no handle, stdin or console,
cannot use the normal stop path. Not widened here, by that ruling.

### D2 - a recovered crash no longer keeps `capture.owner.json`; other hosts accept the recording

`CaptureLock.ClearStaleClaim(dir, purpose)` (new) removes the owner record of a claim that is PROVABLY
stale. It holds `capture.lock` SHARED (the finalizers' own probe, `OpenShared`) while it computes the
liveness again (the same `FreeLockLiveness` rule as before), and deletes the record only if it still
reads stale. The shared hold stops a capture taking the lock in between, and other finalizers' probes
never see it as a holder. My first version held the lock EXCLUSIVELY. The self-review found that for its
few milliseconds, another process's `FinalizePending` would read it as a live capture by the dead owner.
That check runs after the attempt is counted, so it would fail the attempt. The shared hold is the fix,
pinned by `ClearStaleClaim_AnotherFinalizerProbingAtTheSameInstant_StillClearsAndNeitherSeesAHolder`.
The record is KEPT, with the reason logged, when any of these holds: a capture holds the lock, this
process is capturing, a recorded writer is still running, or a writer was recorded by ANOTHER host (this
host cannot prove that one dead).

It is called after each takeover's own decline check: `PostRecording.Execute` (`PostRecording.cs:381`, the
repair and post-recording passes), `RecordingService.FinalizePending` (`:1093`) and `agenteyes package`
(`Package.cs:125`). A clean release already deleted the record (`Dispose`, unchanged).

**The control that matters, on a REAL capture.** The quotes below are from `pass2/run-c4-fix-worktree.txt`; the re-run on the committed binary, `pass2/run-c4-fix.txt`, shows the same sequence: after this host recovers the
crash, ANOTHER HOST (see "the other host" below) accepts the finished recording:
- other host, BEFORE recovery: `refusing to package ...: ... video ffmpeg pid 1216853 ran on host devlinux
  and cannot be checked from otherhost`. That is correct: the orphan was still writing.
- this host's next serve recovers it: `ClearStaleClaim: ... (post-recording) - CLEARED capture.owner.json
  (lock held shared): stale capture claim taken over ...`, mux `done`, `Attempts=1`,
  `capture.owner.json=absent`.
- other host, AFTER recovery: `agenteyes package` gets past the claim. It stops only at `transcription
  FAILED: Not signed in to DevThrottle`, because the scratch root has no account. The other host's own
  service pass logs `Execute: 2026-11-16_081047_video - capture claim: no capture holds it`, then runs its
  stages: nothing to mux, the thumbnail already present, packaging left for sign-in. No decline anywhere.
- Unit control: `Resume_CrashedCaptureRecovered_AnotherHostThenAcceptsTheRecording`. Its precondition
  asserts that the other host DOES refuse before the recovery, so the test can fail. With the call in
  `Execute` removed, it fails (mutation check, run here). Keep-side tests: `ClearStaleClaim_*` (lock held
  elsewhere, writer still running, writer on another host, this process capturing, provably stale, no
  record).

**Known limit.** A record that a `d245037` binary left on an already FINISHED recording, with nothing
outstanding, is cleared only when a same-host `package` or finalize touches that recording. Only QA's
`d245037` runs wrote such records; that commit never reached `main`.

### G1 - the manifest names the capture's owner

`manifest.json` gains `CaptureOwner: { Host, Pid, ProcessStartUtc }`. `BeginSession` writes it into the
START manifest straight after `CaptureLock.TryAcquire`. The values come from the same owner record the
lock publishes (`CaptureLock.OwnerForManifest`), so the two cannot differ. `RecoveryManifest` carries it,
because it identifies the recording. No API or `/status` change (#159's territory). On the real run:
`CaptureOwner={"Host": "devlinux", "Pid": 1216746, "ProcessStartUtc": "2026-11-16T13:10:46.6224249Z"}`
is in the manifest from the first seconds of the capture. It names S1, the pid that was then SIGKILLed,
and it is still there after another process (pid 1218641, `AttemptedBy`) recovered the recording. Tests:
`OwnerForManifest_MatchesThePublishedOwnerRecord`,
`CaptureOwner_SurvivesTheManifestRoundTripAndTheRecoveryRecord`.

### Criterion 4's control arm, re-run with REAL captures (plain MP4, the arm that failed)

Harness `pass2/harness/c4-control.sh`, one script with two binaries. The FIX arm ran twice. First on
the working tree (`pass2/run-c4-fix-worktree.txt`, binary `+760e6a7` with the uncommitted change): 0.13 s
to exit, 50.67 s recovered. Then on the FINAL committed code (`pass2/run-c4-fix.txt`, binary
`1.11.11+31ed72191e6b76a3be197fb4539daa103ea06094`, after the self-review and CI fixes): SIGTERM, exited
after **0.13 s**. An intermediate run on `a2f8103` measured 0.15 s with the same outcome. The final run
was recovered at Attempts 1, `recording.mp4` **50.53 s**, full decode exit 0, `video,audio`,
`ClearStaleClaim ... CLEARED capture.owner.json (lock held shared)`. The other host's package got past the
claim, and its service pass logged `capture claim: no capture holds it`. The pre-fix negative control is
`pass2/run-c4-prefix.txt`, binary `+d245037`. The table quotes the first FIX run:

| Step | PRE-FIX `d245037` (`pass2/run-c4-prefix.txt`) | FIX (`pass2/run-c4-fix.txt`) |
|---|---|---|
| S1 `serve`, `POST /record/start` (x11grab + pulse), 45 s | capturing | capturing |
| `SIGKILL` S1 mid-capture | orphan writer keeps writing (7.6 -> 8.1 MB in 4 s, ppid 2221) | same (7.6 -> 8.1 MB in 4 s) |
| other host `package` | refused, names the writer it cannot check | refused, names the writer it cannot check |
| S2 `serve` start-up reap | `ORPHAN ... - killed` (SIGKILL) | `SIGTERM -> pid 1216853` ... `exited after 0.13s (graceful: its file was finished)`; `raw.mp4` probes at 50.67 s |
| S2's first repair pass | mux **failed**, `could not read the audio channel count`, Attempts 1, recording lost | mux **done**, Attempts 1 by S2, `recording.mp4` 50.67 s, full decode exit 0, `video,audio` |
| `capture.owner.json` afterwards | present | **absent** (`ClearStaleClaim ... CLEARED`) |
| other host `package` / its service pass | still refused / `FindUnfinished: skipping ... cannot be checked from otherhost` | gets past the claim / `capture claim: no capture holds it` |
| manifest `CaptureOwner` | (field does not exist) | names S1 |

**The other host, honestly stated:** these are not two machines. "Another host" is a process on the SAME
library whose `gethostname()`/`uname()` report `otherhost` (an `LD_PRELOAD` shim,
`pass2/harness/fakehost.c`). It also gets its own AgentEyes data root, so its own `host-id`. That is
exactly the pair `IsThisHost` compares, so the claim logic sees another host. File-system lock semantics
across real machines are not exercised (unchanged from pass 1's stated limit). The shim provably took
effect: every decline names `from otherhost`.

**Isolation (#161), checked BEFORE any service ran.** Each run first resolves both roots with a
report-only `agenteyes housekeep`, which starts no service, for this host and for the other host. It
asserts the recordings root and both data roots are inside the scratch directory. Every `serve` is then
SIGKILLed at once if its own `recordings ->` line is outside scratch. No run aborted. The owner's real
library, service, tray app and audio settings were not touched. The foreign go2rtc ffmpeg was
re-identified by its command line at the start and end of each run (pid 72110 both times), alive, and
never signalled. Only pids the scripts started were signalled.

### D3 - NOT fixed here, by ruling; #113's next pass owns it. The two conflicts, exactly

#156 and #113 (`ad21557`) do not compile together:

1. **Two types named `CaptureLiveness`.** #156: `AgentEyes.CaptureLiveness` (`src/AgentEyes.Core/CaptureLock.cs:10`,
   `Live` + `Reason`). #113: `AgentEyes.Video.CaptureLiveness` (`src/AgentEyes.Core/Video/CaptureWatchdog.cs:14`
   on `ad21557`, `State` / `Bytes` / `LastGrowthUtc`). In #113's `RecordingService.cs` (namespace
   `AgentEyes`), `private volatile CaptureLiveness? _liveness;` (`:225` on `ad21557`) binds to the
   ENCLOSING namespace's type, #156's, ahead of the `using AgentEyes.Video` one. So `live?.State` (`:430`) and
   `live?.LastGrowthUtc` (`:433`) fail with CS1061. Resolution belongs to #113: rename one of the two
   types. Renaming #113's watchdog type is the smaller change; #156's type is used by the four decline
   sites, which QA verified.
2. **`IScreenRecorder` has no `ProcessId`.** #156 records the screen writer with
   `NoteCaptureWriter("video", _video.ProcessId)` (`RecordingService.cs:693` on this branch, where
   `_video` is `FfmpegRecorder`, and #156 added `FfmpegRecorder.ProcessId` at `Video/FfmpegRecorder.cs:78`).
   On `ad21557`, `_video` is `IScreenRecorder?` (`RecordingService.cs:162`), declared at
   `Video/IScreenRecorder.cs:15` with no `ProcessId`, so the call fails with CS1061. Resolution belongs to
   #113: add `int ProcessId { get; }` to `IScreenRecorder` and implement it on every recorder. Do not
   drop the call: an unrecorded writer is exactly the gap #156 closed. `_camera`
   (`FfmpegCameraRecorder`, `:679`) is a concrete type on both branches and does not conflict.

QA's scratch patch that resolves both is `qa-linux/harness/merged-qa-edits.patch`. With it, every
fragmented arm passed. I did not pull any #113 code into this branch.

### Self-review (`/code-review` on the pass-2 diff): what changed, and what was dismissed

Fixed:
- The exclusive hold in `ClearStaleClaim` could fail a concurrent counted finalize (above). It is now a
  shared hold, with a regression test.
- Housekeeping could run before the now-background reap had finished. It awaits the reap now.
- The report claimed "its file was finished" for a process whose exit status cannot be read. It now
  says what is known: `graceful: never killed`. The recovery's mux is what proves the file.
- I had put a try/catch in the `KillTree` helper (CLAUDE.md rule 4). It is removed. Instead, the
  post-deadline loop probes again before it kills, and a writer that exited at the deadline is reported
  graceful.

Dismissed, with the reason:
- "A registry record from before `CaptureWriter` existed deserializes false and is SIGKILLed." Capture
  writers were never registered at all before #156 (`main` has no such records). The only binaries that
  wrote one without the field are `d245037` QA builds, which never reached `main`. Guessing from the
  label would be a heuristic for a case that cannot occur on upgrade.
- "Add `HostId` to `CaptureOwner`." The ruling names host, pid and process start time. `AttemptedBy`,
  the thing it is compared against, carries the host NAME, not an id, so the id would compare against
  nothing.
- "`ClearStaleClaim` is called up to three times on one path." After the first call removes the record,
  every later call is one `File.Exists`.
- "An errno other than ESRCH aborts the reap" and "SIGKILLs past the deadline are serial, 5 s each".
  EPERM cannot happen when signalling a process of the same user that this product started. A SIGKILLed
  process exits at once, so the 5 s waits are an upper bound that is not reached.

### Gates (pass 2, run by me on `devlinux`)

| Gate | Result |
|---|---|
| `dotnet build tests/AgentEyes.Tests.Linux -c Release` | **0 Error(s)** |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` (at `31ed721`) | **334 passed / 0 failed / 0 skipped** (315 before this pass; 19 new) |
| CI at `31ed721`, TWO runs (push 43877189016, pull_request 43877202060) | windows **2429 passed / 0 failed / 4 skipped** (2433), linux **334 / 0 / 0**, macos **326 / 0 / 0** - the same counts in both runs. The macOS suite includes the real-process SIGTERM tests, so the `libc` `kill` path is proven on macOS |
| CI at `a2f8103` (the first pass-2 push) - **FAILED on Windows, 2 tests**, both Windows-only and both fixed in `31ed721` | `RepairServiceTests.RecordingCompleted_RunsARepairPass_WhicheverStopPathAnnouncedIt`: `RunAsync` awaited the background reap BEFORE its cheap recording-in-progress check, so the check no longer answered at once. The reap is now awaited after the check and the gate, before any work. `ManifestWriterTests.OnlyPinnedFiles_NameManifestJsonInCode`: a new `ClearStaleClaim` log line named the manifest file; it now says "the manifest". The Windows suite cannot be built on this machine (no Windows Desktop SDK), which is why CI found these and I did not |
| IL pins for the Windows suite, CONFIRMED by the green Windows CI above | `ManifestWriterIlTests`: `ReapOrphans` deletes x2 -> x7, `Register` -> `WriteRecord`, `+ CaptureLock::ClearStaleClaim -> File::Delete x1`, native import `+ libc!kill` (no file access). The lines were taken from the repo's own `CompiledCode` scanner run over the Linux `agenteyes.dll`, not counted by hand |

### What QA should re-verify (the delta)

1. Criterion 4's plain-MP4 control arm with real captures: SIGKILL the owning serve mid-capture, then
   start the next serve. Expected: the reap logs `SIGTERM ... exited after <1 s (graceful)`, the first
   pass recovers at Attempts 1, the file decodes with audio, `capture.owner.json` is gone, and the
   manifest's `CaptureOwner` names the killed pid.
2. D1's other branch: a writer that ignores SIGTERM is SIGKILLed after 10 s and the log says so. A
   real ffmpeg rarely refuses SIGTERM, so the real-process test with a TERM-ignoring child is the proof.
   A `kill -STOP` on the orphan before the next start would reproduce it for real.
3. D2's control: another host (a second machine, or the shim) accepts the recovered recording.
4. G1: `CaptureOwner` in a fresh recording's START manifest.
5. The unchanged arms (live declines, no attempt counted) should still pass. Nothing in their path
   changed except the new line after each decline.

### Still NOT covered after pass 2

Windows graceful reap (open gap above, issue #166); the macOS finalize time; two real machines on one library; the
tray app as the capturing host; the CLI `agenteyes video` path (it takes no `CaptureLock`, unchanged); a
systemd unit with `KillMode=mixed`, where systemd itself SIGKILLs the orphan in 2-4 s, before any reaper
runs (issue #165, by the lead's ruling not this issue's).

---

## Pass 3 - after the Review Gate's rejection (gate comment 6918963862, lead's ruling 6919009792)

Seated on `devthrottle-mac-mini` (macOS 26, Apple silicon) on purpose: the defect is invisible on Linux.
Every result below was run on this Mac on .NET SDK 10.0.403 with the net8.0 build ROLLED FORWARD to
runtime 10.0.x (`DOTNET_ROLL_FORWARD=Major`), because there is no .NET 8 here.

### The defect, reproduced first

`FfmpegChildRegistry.IsSameProcess` threw `System.ComponentModel.Win32Exception` instead of answering
when a recorded pid is now held by ANOTHER USER's process. I started from the gate's scratch
reproduction (`GateProbeScratchTests.cs` from its scratchpad, committed at
`pass3/harness/GateProbeScratchTests.cs` - [pass 4 correction: body unchanged, header replaced. The
gate's line 1 was replaced by a four-line provenance header; lines 2-100 are byte-identical]) and ran it at the PR head before changing anything:
every row of the gate's table reproduced (`Inspect`, `ClearStaleClaim`, `ThrowIfLive`, `FindMissing` and
`ReapOrphansIn` threw; `FindUnfinished` dropped the crashed recording), and both controls passed.

Then I measured where it throws, on this Mac, before writing code:

| pid | `kill(pid, 0)` | `HasExited` | `StartTime` |
|---|---|---|---|
| 1 (root) | -1, errno 1 (EPERM) | False | **throws** `Win32Exception` (NativeErrorCode 2 in one run, 1 in another) |
| this test process | 0 | False | readable |
| 999999 | -1, errno 3 (ESRCH) | - | `GetProcessById` throws `ArgumentException` (already mapped) |
| a ZOMBIE of this user (`sh -c 'sleep 0.01 & exec sleep 5'`) | 0 | **False** | **throws** `Win32Exception` (NativeErrorCode 3) |

Two findings shaped the fix. The exception's `NativeErrorCode` does not identify the cause on macOS
(2 or 1 for another user's process, 3 for a zombie), so it is not used for the answer. And there is a
second route into the same throw that nobody had named: this user's own zombie.

### The fix: the predicate is total (`src/AgentEyes.Core/Video/FfmpegChildRegistry.cs`)

One `catch (Win32Exception)` in `IsSameProcess` itself, answering FALSE, with the reasoning in the
method's summary. No call site changed. Every outcome and why:

| Outcome | Answer | Why |
|---|---|---|
| No such process (`ArgumentException`) | FALSE | unchanged |
| Exited (`HasExited`, or `InvalidOperationException` between two reads) | FALSE | unchanged |
| Start time read, different | FALSE | unchanged: the pid was reused |
| Start time read, equal within 1 s | TRUE | unchanged |
| **Start time unreadable (`Win32Exception`)** | **FALSE** | every recorded start time was read by an AgentEyes process of THIS account, from a process this account started. While that process runs it stays readable to this account, so a pid whose start time this account cannot read is not it |

The two measured ways into the new row, both named in the decision's log line by
`DescribeUnreadable(pid)`, which asks `kill(pid, 0)`. On macOS/Linux that call fails only with ESRCH or
EPERM, because EINVAL is for a bad signal number and 0 is valid:

- **Another user's process** (EPERM). The lead's ruling, as a fact. Our writer ran as us, so a process
  we may not even signal cannot be it.
- **This user's zombie** (kill succeeds, start time refused). It has exited and writes nothing. The log
  line says "the only cause measured (macOS) is a zombie". It does not claim more, because a successful
  `kill(pid, 0)` proves only that the process exists and is ours.

The decision is logged at the point it is made, with the pid. A real line, from the fixed CLI on this Mac:

```
[FfmpegChildRegistry] IsSameProcess: pid 1 - start time cannot be read (1: Unable to retrieve the specified
information about the process or thread. It may have exited or may be privileged.); it belongs to ANOTHER USER
(kill(pid, 0) errno 1); the recorded process ran as this user, so this is not it - answered NOT the recorded process
```

`DescribeUnreadable` never calls `kill` for a pid of 0 or below: kill reads those as a process GROUP
(0 is the caller's own group, -1 is every process). A record holding such a pid is reported as corrupt.

**The ambiguous case, named and resolved.** The premise "the writer ran as this account" fails in one
case: a SECOND user account on this machine probing a record written by the first account, whose
writer is still running. That reads as FALSE, so it would be treated as stale. I resolved it FALSE
because the product cannot put one account's record in front of another account's probe. Every capture
claim is made by `RecordingService` (`RecordingService.cs:1320`) in a directory from `RecordingPaths.NewDir`
(`:420`, `:548`), under the account's own recordings root. The CLI's `--out` captures take no claim at
all. That root is in the account's own home (`~/Movies/AgentEyes`, and `~/Movies` is `drwx------` here,
measured), and the reap registry is under per-user application data. This is the case the gate raised in its last
paragraph. A shared library across accounts would reopen it, and nothing in the product offers one.

**Windows: NOT measured.** I have no Windows machine from this seat. The analogue is `OpenProcess`
(`PROCESS_QUERY_LIMITED_INFORMATION`) refusing another user's process with ERROR_ACCESS_DENIED (5). The
same `catch` answers FALSE there. The premise is weakest on Windows: if a NON-elevated probe is also
refused for an ELEVATED process of the SAME user, a writer started by an elevated AgentEyes would read
as not running to a non-elevated one, and a live capture could be called stale. **QA must measure
this.** As a non-admin user: `IsSameProcess` (or the shared test below) against another user's process
and against an elevated process of the same user. On Windows the shared tests use pid 4 (System) as
the foreign process. Windows CI passing them shows the predicate answers without throwing for pid 4.
It does NOT measure the other-user or elevation cases, because a CI runner is an administrator.

**Linux: unchanged in answer.** Linux reads any process's start time from `/proc`, so another user's
pid answers FALSE by the start-time comparison, as before. One platform difference, named in the
code: Linux reads a ZOMBIE's start time, so there a zombie with a matching start time answers TRUE until
its parent reaps it. An orphaned writer's parent is init or a subreaper, which reaps it at once. Not
changed here.

### (1) The gate's reproduction, failing before and passing after (this Mac)

- **Suite level, real probe:** `pass3/run-gate-probe-and-sweep.txt`. BEFORE is the predicate with the new
  `catch` removed, which is exactly the code at `d7b7cc7`: the gate's rows all throw `Win32Exception`. AFTER:
  `Inspect(pid 1) -> not live, "stale capture claim taken over"`, `ClearStaleClaim -> True`,
  `ThrowIfLive -> passed`, `FindUnfinished -> healthy,rootpid` (the crashed recording is admitted),
  `FindMissing -> OK`, `ReapOrphansIn(owner pid 1) -> record removed`.
- **Real binary, real files** (both binaries checked to be the macOS build of the engine; see the trap below):
  `pass3/foreign-pid.sh <agenteyes> <scratch>` builds the crashed recording (a
  complete 4 s lavfi `raw.mp4`, pending mux, FREE `capture.lock`, owner pid 99999 gone, writer pid 1) and
  runs `agenteyes package` on it.
  - PRE-FIX binary (built from `d7b7cc7` in a scratch worktree), `pass3/run-foreign-pid-prefix.txt`:
    exit 3, `[error] Unable to retrieve the specified information about the process or thread`. Mux
    still pending, no attempt, `capture.owner.json` PRESENT.
  - FIX, `pass3/run-foreign-pid-fix.txt`: mux `done`, Attempts 1, `recording.mp4` 4.0 s with `video` and
    `audio`, full decode exit 0, and `capture.owner.json` absent. **Recovered, not skipped.** Its exit code
    is 1 because the hosted transcription that runs after the mux needs a sign-in this seat does not
    have. The same holds in every `package` run below.

### (2) The mapping pinned in the shared suite, with one mutation per mapping

New `tests/Shared/ProcessIdentityTests.cs`, compiled into all three suites (`AgentEyes.Tests`,
`.Mac`, `.Linux`). It uses the REAL probe: no seam on `IsSameProcess` anywhere in the class. It has 14
tests: one per outcome (another user's pid, its precondition, no such process, own process with a
different start time, own process with the recorded start time as the positive control, own zombie on
macOS, pid 0/-1 never signalled as a group), plus the blast radius (`Inspect` stale for a foreign
writer and LIVE for a running one, `FindUnfinished` admits it, `FindMissing` completes, `Resume`
recovers it, `ReapOrphansIn` removes the record).

Each mutation was applied to the source, the class run on this Mac, then reverted (`cmp` against a copy).
The final run, on the final tests, is `pass3/mutations.txt`:

| Mutation | Tests that FAILED |
|---|---|
| M1: the new `catch` removed (the pre-fix code) | 7 - the foreign-pid predicate test, the zombie, `Inspect`, `FindUnfinished`, `FindMissing`, `Resume`, `ReapOrphansIn`, all with `Win32Exception` |
| M2: unreadable start time answered TRUE | 6 - foreign pid, zombie, `Inspect`, `FindUnfinished`, `Resume`, `ReapOrphansIn` |
| M3: no such process answered TRUE | 3 - `NoSuchProcess`, `FindUnfinished`, `ReapOrphansIn` |
| M4: start-time mismatch answered TRUE | 7 - `OwnProcessDifferentStartTime`, foreign pid, zombie, and four blast-radius tests |
| M5: a matching start time answered FALSE (the predicate becomes a wall) | 2 - `OwnProcessRecordedStartTime`, `Inspect_WriterStillRunning_IsLive` |
| M6: the pid <= 0 guard removed from the classifier | 2 - both `DescribeUnreadable_PidZeroOrNegative` cases |

Not mutation-tested: the `InvalidOperationException` mapping. It is a race (the process exits between
two reads) that cannot be produced on demand, and it is unchanged from `main`.

### (3) The thumbnail and housekeeping sweep, unblocked (this Mac)

`pass3/harness/SweepProofScratch.cs` runs ONE real `RepairService.RunAsync` with the real probe over a
library holding the foreign-pid crash plus two healthy recordings, one of them queued for the drain. The
per-recording steps are stubbed so they only record that they were called. It first asserts that
`RecordingPaths.Root` is inside the suite's redirected temp root (#161). It also turns off the queue's
release-triggered dispatch, so the only drain that can show is the pass's own. Without that, my first
run counted a drain that came from the enqueue itself and would have "passed" BEFORE too. Results, in
`pass3/run-gate-probe-and-sweep.txt`:

| Stage | BEFORE (code at `d7b7cc7`) | AFTER |
|---|---|---|
| unfinished admitted | the two healthy ones; the crash skipped | all three, the crash included |
| thumbnail backfill | **none** | both healthy recordings |
| housekeeping | **0 runs** | 1 run |
| queue drain | **not run** | ran the queued recording |
| log | `RunAsync FAILED: trigger=pass3-proof` | `RunAsync: trigger=pass3-proof done` |

The SCOPE line holds. `Thumbnails.NeedsThumb` still catches only `UsageException`, so ANY other exception
from one recording still stops the pass. That is #170 and it is not touched here. This pass closes the
route by which the predicate threw.

### (4) The predicate on all three platforms

| Platform | Measured? | Result |
|---|---|---|
| macOS (this Mac) | yes | pre-fix throws for another user's pid; fixed answers FALSE and logs EPERM; a zombie answers FALSE |
| Linux | NOT re-measured in pass 3 | `/proc` makes every start time readable. That was measured by both Linux QA passes and by the gate's statement of why they missed this. The `.Linux` suite run on this Mac is NOT a Linux run. QA on `devlinux`: run `tests/AgentEyes.Tests.Linux` there, where `ProcessIdentityTests` should pass with pid 1 answered by the start-time comparison |
| Windows | **NO** | see above. QA must measure another user's process and an elevated same-user process as a non-admin |

### (5) Criterion 4's control arm, re-run on this Mac

`both-arms.sh` (pass 1's harness: a lavfi ffmpeg writer plus a python `flock` owner, three arms
concurrently, plain MP4) against the FIXED binary: `pass3/run-c4-control-fix.txt`.

- **live-package:** declined three times, naming the holder, with no attempt recorded. Then finalized at Attempts 1.
- **orphan-writer:** owner SIGKILLed with the writer still running. Declined with `its video ffmpeg pid
  ... is STILL RUNNING`. Once the writer ended: recovered at Attempts 1. This arm matters for the new
  mapping: a LIVE writer of this user still reads as live, so the fix did not turn the predicate into
  "everything is stale".
- **dead-owner:** recovered at Attempts 1.

Not re-run in pass 3: pass 2's REAL-capture control arm (x11grab + pulse, SIGKILLed serve, start-up reap).
It is Linux-only: this Mac's shell has no Screen Recording grant. Nothing in that path changed except
that the predicate it calls can no longer throw.

### Gates (pass 3, run by me on this Mac, roll-forward to .NET 10)

| Command | Result |
|---|---|
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` | Build succeeded, 0 errors (1 existing warning) |
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux` | Build succeeded, 0 errors (1 existing warning) |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | **340 passed, 0 failed, 0 skipped** (326 before; 14 new) |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | **347 passed, 1 skipped, 0 failed** - ON THIS MAC, **not a Linux run** |
| `AgentEyes.sln` | not built by me: it cannot build on a Mac |
| CI on `d99830c` (push run 43887778843, pull-request run 43887796620) | Windows: **2437 passed, 15 skipped, 0 failed** (default gate) and **2456 passed, 0 skipped** (`AGENTEYES_FFMPEG_TESTS=1`). The baseline is `d7b7cc7`'s pull-request run 43882106917, which ran on the same `main` `17c244d`: 2423 + 15 and 2442. **+14 in both, exactly `ProcessIdentityTests`**, so they ran and passed on Windows with pid 4 as the foreign process, which answered FALSE without throwing. That is NOT the other-user or elevated measurement: the runner is an administrator. All six jobs green (windows, linux and macos, on both runs) |
| merge onto `main` `17c244d` | **CONFLICTED once pass 3 was committed.** My checks before the commit were clean, but pass 3's predicate summary sits on the lines next to `main`'s #151 change (`IsSameProcess` and `KillTree` made `internal`). I merged `main` into the PR branch (`9a1accb`): this branch's code, plus `main`'s `internal KillTree`, which `main`'s `tests/Shared/OwnedFfmpeg.cs` calls. Against the branch, the resolution changes `FfmpegChildRegistry.cs` only by that visibility. `main` carries no #113 code |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` on the MERGED branch (`9a1accb`) | **345 passed, 1 skipped, 0 failed**. The gate measured 331 + 1 on its merged tree; the +14 is `ProcessIdentityTests` |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` on the MERGED branch, ON THIS MAC | **352 passed, 2 skipped, 0 failed**. The gate measured 338 + 2; the +14 again. Not a Linux run |

### Unchanged, by scope

The lock, the owner file, the four decline sites, the attempt counting, the shared hold, the SIGTERM
bound, the awaited reap and G1's manifest field are untouched: the code diff is
`FfmpegChildRegistry.IsSameProcess` plus the new `DescribeUnreadable`. No sweep was hardened (#170).
**D3 stays #113's**, and both conflicts named above still stand: the two `CaptureLiveness` types, and
`IScreenRecorder` lacking `ProcessId`. No #113 code was pulled in.

### Hazards, as handled

No screen capture was started. No ffmpeg I did not start was signalled: `pgrep ffmpeg` was empty
before and after the harness. No recording was deleted. The launchd agent `com.agenteyes.recorder` was
not touched. No service was started. **#161 footprint, reported:** the `agenteyes package` runs above
used an isolated HOME, which on macOS does NOT redirect the data root. So their log lines were appended
to the owner's real `~/Library/Application Support/AgentEyes/logs/AgentEyes-20261116.log` (verified
read-only: the `IsSameProcess: pid 1` lines are there). No recording outside the scratch directory was
read or written. The suite runs use the test projects' in-process redirection.

### Self-review (`/code-review` on the pass-3 diff): what changed, and what was dismissed

Changed because of it:
- The zombie test ran on Linux too, where `/proc` gives a zombie's start time, so it would have FAILED
  on a real Linux run. It passed here only because this is a Mac. It is now macOS-only, and the
  Linux zombie behaviour is named in the code.
- `FindMissing_...` asserted only "no exception". It now asserts that the exact backlog is returned:
  finished recordings sorted on both sides of the crash, so neither an early nor a late stop passes.
- The pid-1 precondition assumed pid 1 is foreign to any non-root runner. It now returns where pid 1
  runs as the test's own user (a container).
- Log and doc wording: the zombie line no longer asserts more than kill(2) proves; the Windows line
  points at the native error and names the unmeasured elevated case; the ambiguity paragraph cites
  where claims are created.

Dismissed, with reasons:
- *"A catch in a helper is fallback programming (CLAUDE.md 3 and 4)."* The lead's ruling is explicit:
  fix the predicate, never the call sites. Each answer is a defined mapping with its justification,
  pinned by a test and a mutation, and logged at the decision. It is not a swallowed unknown. The
  helper already mapped `ArgumentException` and `InvalidOperationException` the same way.
- *"`--out DIR` makes the second-account case reachable."* Refuted: `--out` captures take no
  `CaptureLock`, so they have no owner record and never reach the writer probe.
- *"Info lines and an extra kill(2) per unreadable probe flood the log."* The ruling asks for the
  decision to be logged with its pid. A foreign-pid claim is cleared on the first pass that recovers it
  (`ClearStaleClaim`), and its reap record is removed, so the probe and its line stop.
- *"On Windows, unreadable means stale can fail open for an elevated writer."* **Kept, NOT dismissed.**
  It is the Windows risk named above. It is unmeasured, so QA has to measure it.

### A trap for QA: one output folder for both Unix builds

`dotnet build src/AgentEyes.Core -p:AgentEyesUnixPlatform=linux` and `=mac` write to the SAME
`src/AgentEyes.Core/bin/Release/net8.0/`, and so do the two test projects. Whichever ran last owns
`agenteyes`. My first real-binary runs used the engine left there by the `.Linux` suite. The predicate
is the same code in both flavours, but I re-ran every real-binary arm on an explicit
`-p:AgentEyesUnixPlatform=mac` build. Check the flavour before using a binary:
`strings agenteyes.dll | grep -c '^AvfStream$'` gives 1 on a macOS build and 0 on Linux, and
`^PulseStream$` gives the reverse. Each evidence file starts with that check.

### What QA should verify (pass 3 delta)

1. On macOS: `pass3/foreign-pid.sh` against the branch binary shows mux done, Attempts 1, a decodable
   file and no `capture.owner.json`. Against a `d7b7cc7` binary it shows exit 3 with the Win32Exception message.
2. The six mutations, or any of them, against `ProcessIdentityTests`.
3. **Windows (unmeasured by me):** as a non-admin, the answer for another user's process and for an
   ELEVATED process of the same user. The second is the one that could call a live capture stale.
4. Linux on `devlinux`: the `.Linux` suite, with `ProcessIdentityTests` included.
5. The criterion 4 arms (`both-arms.sh ... plain`). The orphan-writer arm is the live-writer control for
   the new mapping.

I believe this is finished.

## Pass 4 - prove liveness or report Unknown (gate comment 6921080636, lead's ruling 6921153923)

Seated on `devthrottle-mac-mini` (macOS 26, Apple silicon). Every number below was run on this Mac on
.NET SDK 10.0.403 with the net8.0 build ROLLED FORWARD to runtime 10.0.15
(`DOTNET_ROLL_FORWARD=LatestMajor`), because there is no .NET 8 here.

### The defect, and where I started

Pass 3 answered "not the recorded process" for a pid that `kill(pid, 0)` had just shown EXISTS and is
THIS USER's, whenever its start time was refused. The reason given was "the only cause measured (macOS)
is a zombie". The Review Gate measured a second cause. A prober inside a Seatbelt sandbox that allows
process-info only within its own sandbox is refused the start time of a LIVE same-user process. That
prober admitted a live capture, spent an attempt on it and deleted `capture.owner.json` while the
writer ran.

**I started from the gate's own reproduction, not a new one.** Its probe and run logs were at
`/private/tmp/agenteyes-gate-156-pass3/` on this machine (`probe/live-orphan.sh`, `probe/rusage.py`,
`run-lo-*.txt`). `pass4/repro-sandbox.sh` is `live-orphan.sh` with its live arm unchanged in shape: the
same lavfi ffmpeg writer, the same manifest and owner record, the same two-rule profile. I added the
STALE arm the ruling asks for as the positive control, so both run in one invocation.

Before writing code I extended the gate's system-call table with the call the fix relies on. Its
`rusage.py`, plus `sysctl KERN_PROC_PID`, against a live same-user `sleep`, a zombie of this user,
pid 1 and a missing pid:

| Target | prober | `sysctl KERN_PROC_PID` (p_stat) | `proc_pid_rusage` | `kill(pid, 0)` |
|---|---|---|---|---|
| live same-user `sleep` | plain | rc 0, **2 (SRUN)** | rc 0 | rc 0 |
| live same-user `sleep` | **sandboxed** | rc 0, **2 (SRUN)** | **rc -1, EPERM** | rc 0 |
| zombie of this user | plain | rc 0, **5 (SZOMB)** | rc 0 | rc 0 |
| zombie of this user | **sandboxed** | rc 0, **5 (SZOMB)** | rc -1, EPERM | rc 0 |
| pid 1 (root) | both | rc 0, 2 | rc -1, EPERM | rc -1, EPERM |
| 999999 (none) | both | rc 0, **empty reply** | rc -1, ESRCH | rc -1, ESRCH |

So the kernel state can still be read where the start time is refused. A zombie can be PROVED, and a
live process in the same position can be told apart from it.

### What changed

**The predicate has three answers.** `FfmpegChildRegistry.IsSameProcess` (bool) is replaced by
`IdentifyProcess`, which returns a `ProcessAnswer`: `Mine`, `NotMine` or `Unknown`, each with the fact
behind it (`Video/ProcessIdentity.cs`). The bool is gone, so no caller can drop the third state by
accident. `Unknown` is enum value 0, so an answer nobody filled in reads as the safe one (pinned).

**The unreadable-start-time decision proves or reports** (`ClassifyUnreadable`; the old
`DescribeUnreadable` is gone). The probes are injected, so every row has a test:

| Row | Answer | The fact |
|---|---|---|
| Windows, any error other than 5 | Unknown | not a refusal, so not the measured fact (added after self-review) |
| Windows, open refused (error 5) | NotMine | MEASURED (Windows QA, pass 3): .NET opens with `PROCESS_QUERY_LIMITED_INFORMATION`, which Windows grants the same user across integrity levels, so a refusal means another user's or a protected process. The "elevated case is unmeasured" sentence is gone from the code and the log line. #171 keeps the KillTree-across-elevation measurement and the two unpinned mappings. |
| `kill` ESRCH | NotMine | gone |
| `kill` EPERM, Linux | NotMine | another user's; our writer ran as us (as ruled; same premise unmeasured - see round 2) |
| `kill` EPERM, macOS, target uids not ours | NotMine | **PROVED** another user from the target's `p_ruid`/`p_svuid` (round 2) |
| `kill` EPERM, macOS, target uid IS ours | **Unknown** | **measured in round 2: a signal-denying sandbox refuses kill for a live process of this user** |
| `kill` other errno | Unknown | the probe failed |
| `kill` 0, Linux | Unknown | no second probe exists. Linux reads a zombie's start time anyway. |
| `kill` 0, macOS, p_stat SZOMB | NotMine | **PROVED zombie** |
| `kill` 0, macOS, empty reply | NotMine | exited between the calls |
| `kill` 0, macOS, any other state | **Unknown** | **the gate's row: alive, this user's, unidentifiable from here** |
| `kill` 0, macOS, sysctl refused or odd size | Unknown | cannot know. `Unreadable` is enum value 0, so an unread state is never a proof (pinned) |

The tradeoff is written in the predicate's summary. Unknown delays a recovery, which can be recovered
from. Guessing NotMine finalizes a live file and destroys its claim, which cannot.

**The callers learned the third state.** Each one, with the test that pins it:

| Caller | On Unknown | Test |
|---|---|---|
| `CaptureLock.FreeLockLiveness` / `Inspect` | live AND `Undetermined`, reason starts `UNDETERMINED:` and names the pid, the why and the fix. A writer proved running wins over an Unknown one. | `Inspect_WriterIdentityUnknown_LiveUndeterminedAndNothingCleared`, `Inspect_OneWriterUnknownOneRunning_ReportsStillRunningNotUndetermined` |
| `ThrowIfLive` | refuses; logged `DECLINED (undetermined)` | same, first test |
| `ClearStaleClaim` | keeps the record | same, first test |
| `CaptureLock.Dispose` (the capture's own release) | the writer counts as still running, so the owner record is kept | `Release_WriterIdentityUnknown_OwnerRecordKept` |
| `PostRecording.Execute` | declined, no stage, no attempt; WARN `DECLINED (undetermined)` | `Resume_WriterUnknownBesideAStaleClaim_DeclinesOneRecoversTheOther` |
| The sweep, `PostRecordingPlan.FindUnfinished` | not admitted; WARN `NOT REPAIRED ...` on every pass, and the pass summary counts `UNDETERMINED=n` | `FindUnfinished_WriterUnknownBesideAStaleClaim_AdmitsOnlyTheStaleOne` |
| `Thumbnails.NeedsThumb` | skipped; WARN `NOT GENERATED` | (same decline as before; the log level is the change) |
| The reaper, owner Unknown | left alone, record kept, no signal | `ReapOrphans_OwnerIdentityUnknown_LeftAloneRecordKept` |
| The reaper, child Unknown | not signalled, record kept (NOT removed as "exited") | `ReapOrphans_ChildIdentityUnknown_NotSignalledRecordKept` |
| The reaper, Unknown at the SIGTERM deadline | NOT killed, record kept | `ReapOrphans_WriterUnknownAfterSigterm_NotKilledAtTheDeadline` |
| Test helper `OwnedFfmpeg.KillOwned` | left alone | `KillOwned_IdentityUnknown_LeavesItAlone` |

### (1) + (2) The gate's reproduction failing before, passing after, with the positive control in the same run

`pass4/repro-sandbox.sh <binary> <scratch>`, four arms per run. Before = the PR head `384e3fe`, built
`-p:AgentEyesUnixPlatform=mac` in a separate worktree (flavour checked: `AvfStream` 1, `PulseStream` 0).
After = this pass, built `-p:AgentEyesUnixPlatform=mac` immediately before its run. Full output:
`pass4/run-before-384e3fe.txt` and `pass4/run-after-pass4.txt`. The product's own decision lines for the
sandboxed arm, copied read-only from the log: `pass4/product-log-sandbox-arm.txt`.

| Arm | BEFORE `384e3fe` | AFTER (pass 4) |
|---|---|---|
| live writer, plain prober (control) | exit 1 STILL RUNNING; pending, no attempt; claim PRESENT | exit 1 STILL RUNNING; pending, no attempt; claim PRESENT |
| **live writer, sandboxed prober** | **ADMITTED: `mux.State=failed`, `Attempts=1` by the foreign pid; claim ABSENT** while the writer ran | **exit 1 UNDETERMINED; pending, no attempt; claim PRESENT; writer untouched** |
| **stale claim, plain prober (positive control)** | recovered: mux done, `recording.mp4` 471649 bytes, claim cleared | **recovered: mux done, `recording.mp4` 471649 bytes, claim cleared** |
| stale claim, sandboxed prober | recovered, claim cleared | **recovered, claim cleared** |

The stale arms exit 1 because of the transcription stage, AFTER the mux: "Not signed in to DevThrottle"
on this Mac. The recovery itself is the `PendingMux=cleared  mux.State=done` line and the file.
The sandboxed stale arm still recovers because a writer that has exited gives ESRCH from `kill`
(`GetProcessById` throws). That is a fact the sandbox does not hide, so the guard is not a wall.

`run-after-pass4.txt` is the run on the FINAL code (re-run after the self-review changes; same result as
the first after-run). The AFTER decision line, verbatim:
`IdentifyProcess: pid 64172 - start time cannot be read (2: ...) - answered Unknown: pid 64172 is ALIVE (p_stat 2) and this user's, but this process is refused its start time (...) - typically a sandboxed prober - so whether it is the recorded process cannot be proved from here`

The same arm is also an automated test: `PackageCli_SandboxedProberLiveWriter_DeclinesUndeterminedAndKeepsTheClaim`
runs the real `agenteyes package` under `sandbox-exec` with the gate's profile against a live `sleep`
writer, with the unsandboxed control in the same test. It is **opt-in** (`AGENTEYES_REAL_CLI_TESTS=1`),
because its first run on this Mac appended four lines to the owner's real AgentEyes log: the CLI child's
log is not moved by HOME (#161). The macOS CI job now sets the variable (`.github/workflows/ci.yml`);
a hosted runner has no user log to protect. The row it proves end to end runs in the default gate too,
as `ClassifyUnreadable_OursOnMacAlive_Unknown`.

### (3) The zombie, proved

`IdentifyProcess_ThisUsersZombie_NotMineProvedBySzomb` makes a real zombie of this user. It asserts
three things: `ps` says Z (the precondition), `ReadMacProcessState` reads p_stat 5 (SZOMB), and the
answer is NotMine with the Why naming the ZOMBIE. `ReadMacProcessState_RunningAndMissingPids_AliveAndGone`
pins the other two kernel reads on real processes (this process reads 2, a missing pid reads Gone). The
`kinfo_proc` size (648) and the p_stat offset (36) are checked on every read: any other reply size is
Unknown, never a guess.

### (4) A test per mapping, each with a mutation shown failing

`pass4/mutate.py` applies one mutation, builds and runs the identity, ownership, reap and owned-ffmpeg
tests, then restores the file. All 18 mutations fail at least one test, on the final code
(`pass4/mutations.txt`, run with `AGENTEYES_REAL_CLI_TESTS=1`):

| Mutation | Fails |
|---|---|
| M1 Alive -> NotMine (**the pass 3 behaviour**) | `ClassifyUnreadable_OursOnMacAlive_Unknown`, `PackageCli_SandboxedProberLiveWriter_...` |
| M2 Zombie -> Unknown | `ClassifyUnreadable_OursOnMacZombie_NotMine`, `IdentifyProcess_ThisUsersZombie_NotMineProvedBySzomb` |
| M3 Gone -> Unknown | `ClassifyUnreadable_OursOnMacGoneBetweenCalls_NotMine` |
| M4 state unreadable -> NotMine | `ClassifyUnreadable_OursOnMacStateRefused_Unknown`, `MacProcessState_Default_IsUnreadableNotAProof` |
| M5 EPERM -> Unknown | 7, incl. `ClassifyUnreadable_KillEperm_NotMineAnotherUser`, the real pid-1 probe, `Resume_WriterPidHeldByAnotherUser_RecoversTheRecording` |
| M6 ESRCH -> Unknown | `ClassifyUnreadable_KillEsrch_NotMineGone` |
| M7 other errno -> NotMine | `ClassifyUnreadable_KillOtherErrno_Unknown` |
| M8 ours on Linux -> NotMine | `ClassifyUnreadable_OursOnLinux_Unknown` |
| M9 Windows refusal -> Unknown | `ClassifyUnreadable_Windows_NotMineWithoutAskingPosix` |
| M10 SZOMB constant 5 -> 4 | `IdentifyProcess_ThisUsersZombie_NotMineProvedBySzomb` |
| M11 equal start time -> Unknown | 7, incl. `IdentifyProcess_OwnProcessRecordedStartTime_Mine`, `Inspect_WriterStillRunning_IsLive` |
| M12 FreeLockLiveness ignores Unknown | 4: `Inspect_WriterIdentityUnknown_...`, `Resume_WriterUnknownBeside...`, `FindUnfinished_WriterUnknownBeside...`, the CLI test |
| M13 Release treats Unknown as exited | `Release_WriterIdentityUnknown_OwnerRecordKept` |
| M14 reaper: Unknown owner treated as gone | `ReapOrphans_OwnerIdentityUnknown_LeftAloneRecordKept` |
| M15 reaper: Unknown child treated as exited | `ReapOrphans_ChildIdentityUnknown_NotSignalledRecordKept` |
| M16 reaper: Unknown at the deadline killed | `ReapOrphans_WriterUnknownAfterSigterm_NotKilledAtTheDeadline` |
| M17 Windows: any error -> NotMine | `ClassifyUnreadable_WindowsOtherError_Unknown` |
| M18 `MacProcessStateKind`: Zombie becomes value 0 | `MacProcessState_Default_IsUnreadableNotAProof` |

### (5) The two same-premise cases the gate read but did not measure

**Linux, a prober in its own pid namespace: NOT MEASURED, and NOT HANDLED by this change.** This seat is
a Mac. A Linux suite run here is not a Linux run, and I did not take a Linux machine from another seat.
By reading the code: the namespaced prober asks for a host pid that does not exist in its namespace.
`GetProcessById` throws `ArgumentException` and the answer is NotMine ("no such process"). That is the
pass 1 mapping, and it is a fact only inside one pid namespace. So I expect the defect class to be
**present** there: a live host writer would be read as gone. The predicate's summary now states this
premise in a paragraph headed "PREMISE NOT HANDLED", so nobody assumes it away. Fixing it needs a design
decision I have not taken. One option: record the writer's pid-namespace identity (`/proc/self/ns/pid`)
in `capture.owner.json`, and answer Unknown when the prober's differs. That changes the owner file, so it
is the lead's call, and I have asked. For QA on Linux:
`unshare --user --map-root-user --pid --fork --mount-proc agenteyes package <dir> --no-frames` against
a crash fixture whose writer is a live host ffmpeg (shape: `pass4/repro-sandbox.sh`, live arm). Expected
on this code: ADMITTED (the defect). The control: the same command without `unshare` declines STILL RUNNING.

**macOS, a writer under `sudo`: NOT MEASURED.** `sudo -n true` here asks for a password, and a seat
does not type the owner's password (that is a user-input step). By reading: the writer is root's, so the
unprivileged prober gets EPERM, and EPERM answers NotMine ("another user's"). The kept mapping is a fact
for a writer this product started as this user. A root writer breaks that premise in the same way as the
Windows elevation question. Two things I could not establish, and QA should: (a) whether a CLI under
sudo puts its recordings in the user's library at all. On macOS .NET resolves the data roots without
HOME (#161), which suggests root's home, `/var/root/Movies/AgentEyes`, where the user's prober never
looks. (b) If it does write into the user's library, whether the unprivileged prober can delete the
root-owned `capture.owner.json`. Deleting it needs write permission on the directory, which root created.
If either fails, the case cannot reach the defect. If both hold, it is the EPERM row answering NotMine
for a live writer.

### (6) The Windows sentence

Rewritten from the measurement (row 1 of the table above). The log line no longer says "possibly an
elevated process; the elevated case is unmeasured". `ClassifyUnreadable_Windows_NotMineWithoutAskingPosix`
asserts that the word "unmeasured" is gone.

### Gates (run by me, on this Mac; SDK 10.0.403, net8.0 rolled forward to runtime 10.0.15)

| Suite | Result |
|---|---|
| `tests/AgentEyes.Tests.Mac` (default gate) | **367 passed, 2 skipped, 0 failed** (the gate measured 345/1 at `384e3fe`: +23 tests; the new skip is the opt-in CLI test) |
| `tests/AgentEyes.Tests.Mac` with `AGENTEYES_REAL_CLI_TESTS=1` | **368 passed, 1 skipped, 0 failed** |
| `tests/AgentEyes.Tests.Linux` built `-p:AgentEyesUnixPlatform=linux`, RUN ON THIS MAC | 374 passed, 3 skipped, 0 failed. **NOT a Linux run.** On Linux the opt-in CLI test skips as macOS-only. |
| `tests/AgentEyes.Tests` (Windows) | CI run on `3090641`: 1 failure, `ManifestWriterIlTests.EveryNativeImport_IsPinned` - the new `libc!sysctl` import was not in the pinned list (a review moment for file-writing natives; `sysctl` with a NULL `newp` is a read-only kernel query). Pinned, with that reason. Every other Windows test passed (2458). Locally: **compile only**, with `-p:EnableWindowsTargeting=true`: 0 errors. It caught three call sites in `FfmpegChildRegistryTests.cs` that the Unix suites cannot see. NOT run; CI runs it. |
| `AgentEyes.sln` | cannot build on a Mac (expected) |

### CI on the branch (run 43905403115, head `0e57590`, all three jobs green)

| Job | Default gate | With the opt-in variables |
|---|---|---|
| macOS (hosted runner) | - | **368 passed, 1 skipped** with `AGENTEYES_REAL_CLI_TESTS=1`: the sandboxed real-CLI test RAN on the runner and passed |
| Linux (a real Linux runner) | 375 passed, 2 skipped | 376 passed, 1 skipped (`AGENTEYES_FFMPEG_TESTS=1`) |
| Windows | 2459 passed, 16 skipped | 2478 passed, 1 skipped (`AGENTEYES_FFMPEG_TESTS=1`) |

The Linux row is the only Linux evidence for this pass, and it is CI's, not mine. It runs the classifier
rows and the caller tests. It does NOT reach the pid-namespace case.

### Scope kept

Untouched, and not regressed (their tests are in the runs above): the lock, the owner file and its
format, the four decline sites (their decline condition is unchanged; only Unknown now reaches it), the
attempt counting, the shared hold in `ClearStaleClaim`, the SIGTERM bound (10 s,
`CaptureWriterGrace_IsTheMeasuredBound`), the awaited reap, G1's `CaptureOwner` manifest field, and the
`main` merge. **D3 stays #113's**, and both conflicts still stand: the two `CaptureLiveness` types (this
pass ADDS `Undetermined` to ours, which #113's merge will now also have to carry), and `IScreenRecorder`
lacking `ProcessId`. No #113 code was pulled in. #170 is not touched. One CI change, inside the tests'
scope: the macOS job sets `AGENTEYES_REAL_CLI_TESTS=1`.

### Hazards, as handled - and my footprint

No screen capture was started. Every ffmpeg was started and ended by my scripts (`pgrep ffmpeg` was
empty before and after each run). I signalled nothing I did not start. No recording outside my scratch
directory was read or written, and none was deleted. The launchd agent and the owner's serve (pid 50642)
were not touched. `/status` read idle before the suites ran. **#161 footprint, reported:** every real
`agenteyes package` run appended lines to the owner's real
`~/Library/Application Support/AgentEyes/logs/AgentEyes-20261116.log`. That is the two
`repro-sandbox.sh` invocations, the first run of the CLI test before I made it opt-in, and the mutation
runs with the opt-in set. Under M1, M11 and M12 the mutated CLI admitted my temp fixture and ran its mux
on it, inside the test's temp root. Only the log was touched. I read it read-only to copy the decision
lines.

### Self-review (`/code-review` on the pass-4 diff): what changed, and what was dismissed

Changed because of it (all re-run above):
- **Windows: only error 5 is the measured fact.** Every `Win32Exception` used to be NotMine. Any other
  code is now Unknown (`ClassifyUnreadable_WindowsOtherError_Unknown`, M17).
- **`MacProcessStateKind.Zombie` was enum value 0**, so `default(MacProcessState)` read as a PROVED
  zombie. `Unreadable` is now 0 (`MacProcessState_Default_IsUnreadableNotAProof`, M18).
- `CaptureLock.Dispose` logged an Unknown writer at Error level as "STILL RUNNING ... the next service
  start reaps it". Neither was true. That writer now gets only its own warning, which says what is known.
- The opt-in CLI test returned (a silent pass) off macOS. It now SKIPS there, with the reason.

Raised, NOT changed - a decision for the lead (sent):
- **Windows AppContainer or restricted-token prober.** It may be refused (error 5) for a LIVE process of
  the same user, which is the Windows counterpart of the macOS sandbox row. The pass 3 measurement
  covered integrity levels only. The ruling keeps 5 as NotMine, so the mapping is unchanged, and the
  code now names this as unmeasured. A possible discriminator is a positive fact about the PROBER:
  `GetTokenInformation(TokenIsAppContainer)` or `IsTokenRestricted` on its own token, which answers Unknown
  when the prober is sandboxed. I cannot write or measure that on a Mac. **QA on Windows:** run the
  pass-3 harness from a restricted-token or AppContainer prober (a launcher of QA's choice; I have not
  tried any) against a live process of the same user.
- **The EPERM row and a root writer** (sudo). This is the same point as (5) above, and the mapping is
  kept as ruled.

Dismissed, with reasons:
- *"Stop waiting for a writer that turns Unknown after SIGTERM."* Kept as is. The wait loop and its
  bound are pass-2 behaviour, which this pass must not move. A writer can only turn Unknown mid-wait in
  a prober whose readability changes under it (not observed). The wait is bounded at 10 s either way.
- *"The undetermined WARN lines repeat on every pass."* That is the reporting the ruling asks for ("the
  sweep must report it rather than silently skipping"). The lines are bounded by how often a pass runs,
  and each one names the fix.
- *"Use one tri-state instead of `Live` plus `Undetermined`."* `Live` keeps every existing caller's
  decline unchanged, which is the point. The constructor rejects the one invalid combination.
- *"ProcessIdentity.cs is untracked."* Committed with the rest.

### What QA should verify (pass 4 delta)

1. macOS: `pass4/repro-sandbox.sh <binary> <scratch>` against the branch binary. Expect the table in
   (1)+(2): the sandboxed live arm declines UNDETERMINED with no attempt and the claim PRESENT, and both
   stale arms recover. Against a `384e3fe` binary the sandboxed live arm is ADMITTED.
2. macOS: `AGENTEYES_REAL_CLI_TESTS=1 dotnet test tests/AgentEyes.Tests.Mac -c Release` (writes your
   real AgentEyes log; see #161). Any of the 18 mutations in `pass4/mutate.py`.
3. Linux on `devlinux`: the `.Linux` suite, and the pid-namespace case in (5). I expect it to show the
   defect, and that case is not fixed here.
4. Windows: the suite (only compiled here), and the AppContainer or restricted-token prober above.
5. macOS with a password-holder present: the `sudo` writer case in (5).

I believe this is finished, with the Linux pid-namespace case and the Windows AppContainer case named
as open and sent to the lead for a decision.

## Pass 4, round 2 - after the lead's decisions (fleet message, 2026-11-16 16:04Z)

**The lead's rulings.** (2) Windows error 5 stays NotMine. Error 5 is the ordinary signal for a pid
reused by a SYSTEM service (nine measured), and making it Unknown would strand those crashes, which is
D2. The prober-token discriminator goes to **#171** and is not written blind from a Mac. The code now
says the case is unmeasured and names #171. (1) Pid namespaces go to a follow-up, provided pids are only
compared when the claim's host matches.

**Answer to (1): the host guard IS there, so the namespace case is the exotic one.**
`CaptureLock.FreeLockLiveness` calls `IsThisHost(owner)` before any writer pid is probed. That check
passes on the same host id OR the same machine name. A writer of another host is reported LIVE with the
exact fix ("Finalize it from <host>, or ... delete capture.owner.json"), never NotMine-by-pid. It is
pinned by `Inspect_WriterOnAnotherHost_IsLiveWithTheExactFix`, whose probe THROWS if it is ever called.
The reaper's registry has no host field, but it lives in per-user local app data, not in the shared
library. What remains for the follow-up is "passes the host check, other pid namespace". That is a Linux
container given the host's name, or one that mounts the user's home and so reads the same `host-id`
file. The expected defect, by reading and NOT measured: the host writer's pid gives "no such process" in
that namespace (`ArgumentException`), the answer is NotMine, the claim is cleared and an attempt spent.
The predicate's PREMISE paragraph now says exactly this, for the follow-up to inherit.

**The case I was tempted to assume away, written down and then measured: it was a defect.** My first cut
kept "kill EPERM = another user" as the lead had ruled. But the gate's sandbox was only one sandbox. One
that ALSO denies signals outside itself makes `kill(pid, 0)` fail with EPERM for a LIVE process of this
same user. The EPERM row then answers "ANOTHER USER, NotMine", which is the gate's defect through the
next door. Measured with `sandbox-exec` and the gate's profile plus
`(deny signal)(allow signal (target same-sandbox))`:

| Target | prober | `kill(pid, 0)` | `sysctl` p_stat | uid offsets 392 / 396 / 420 |
|---|---|---|---|---|
| live same-user `sleep` | plain | rc 0 | 2 | (this process: 501 at all three) |
| live same-user `sleep` | **signal-denying sandbox** | **rc -1, EPERM** | 2 | readable |
| pid 1 (root) | both | rc -1, EPERM | 2 | 0 |

The fix reads a positive fact about the TARGET. The same `sysctl` reply carries the uids that kill(2)
checks: `p_ruid` at 392 and `p_svuid` at 396, with this process's euid at 420. On macOS, EPERM is
NotMine only when neither target uid is ours. If one is ours, the uids permit the signal, so something
else refused it, and the answer is Unknown. If the process is gone the answer is NotMine; if the uids are
unreadable it is Unknown. This needs no new native import: our own uids come from the same read of our
own pid. Linux keeps the ruled row. Its same premise (a seccomp or LSM policy refusing kill(2) for a
same-user process) is named in the code and NOT measured; Linux normally never reaches this row, because
it reads any start time.

**Measured end to end** (`pass4/repro-sandbox.sh` now has a `live-sigdeny` arm; `run-before-384e3fe.txt`,
`run-after-pass4.txt` and `product-log-sandbox-arm.txt` are re-run on the final code):

| Arm | BEFORE `384e3fe` | AFTER (final) |
|---|---|---|
| live, plain | declined STILL RUNNING, claim PRESENT | declined STILL RUNNING, claim PRESENT |
| live, gate sandbox | ADMITTED, Attempts=1, claim ABSENT | declined UNDETERMINED, no attempt, claim PRESENT |
| **live, signal-denying sandbox** | **ADMITTED, Attempts=1, claim ABSENT** (log: "pid 71983 belongs to ANOTHER USER" - a uid-501 process) | **declined UNDETERMINED, no attempt, claim PRESENT** (log: "pid 72330 is THIS USER's (uid 501) but kill(pid, 0) was refused") |
| stale, plain (positive control) | recovered, claim cleared | recovered, claim cleared |
| stale, gate sandbox | recovered, claim cleared | recovered, claim cleared |

The opt-in CLI test now runs the signal-denying arm too. New default-gate tests: the four macOS EPERM
rows (the "this user's" row as a three-case theory: both uids ours, only the real uid, only the saved
uid), and `ReadMacProcessState_Uids_MatchThisUserAndRoot`, which reads this process and pid 1 for real,
so a layout change fails on the first macOS run. Mutations M5b-M5e join the set. **All 22 fail at least
one test** (`pass4/mutations.txt`). M5b is this pass's own first cut (EPERM -> NotMine without the uid
check). It fails the theory AND the real-CLI test.

**Gates on the final code** (SDK 10.0.403, roll-forward to 10.0.15): macOS **374 passed / 2 skipped**
(default) and **375 / 1** (opt-in). Linux configuration on this Mac (NOT a Linux run): 381 / 3. Windows
test project: compiles; CI runs it.

**What else I am still tempted to assume, written down so the next reader is not:**
- **The `kinfo_proc` layout off arm64 macOS 26.** Measured only here. The size is checked (any other
  size is Unreadable, so Unknown) and the offsets are pinned by real reads on every macOS run. But an
  Intel Mac, or a future macOS that kept the size and moved a field, would first show up as a failing
  test, not in the field. A layout change that kept the size is caught by the uid test and the zombie
  test, not by the size check.
- **A sandbox that ALSO denies `sysctl` (sysctl-read).** Then the state and the uids are Unreadable and
  every unreadable-start-time row is Unknown. That is safe. It also means such a prober never recovers a
  crash whose writer pid was reused, but the service recovers it.
- **A same-user process that is not ours in the gate's sandbox.** It is answered Unknown even when its
  start time would have differed. That is a delayed recovery, which is acceptable by the ruling.
- **Windows AppContainer or restricted token:** #171, as ruled.
- **Linux EPERM under seccomp or an LSM:** unmeasured, named in the code.
- **Pid namespaces:** the follow-up, as above.
- **The reaper registry on a shared home** (two hosts with one roaming LocalAppData). Its records have no
  host. A foreign owner pid would read gone, and the child is killed only on a start-time match within
  1 s, which another host's process essentially never has. The worst case is a deleted reap record, not a
  kill. Read, not measured.

**CI on round 2** (run 43908206886, head `57259ea`, all three jobs green): macOS 375 passed / 1 skipped
with the opt-in set, so both sandbox arms of the real-CLI test ran on the hosted runner. Linux 382 / 2
and 383 / 1. Windows 2466 / 16 and 2485 / 1.

I believe this is finished. Open and owned elsewhere: pid namespaces (the follow-up), Windows prober
tokens (#171), Linux EPERM under seccomp or an LSM (named, unmeasured), the macOS sudo writer (QA, needs
a password).
