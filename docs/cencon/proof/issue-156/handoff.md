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
reproduction (`GateProbeScratchTests.cs` from its scratchpad, committed unchanged below a provenance
header at `pass3/harness/GateProbeScratchTests.cs`) and ran it at the PR head before changing anything:
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
