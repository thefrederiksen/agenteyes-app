# Issue #156 - Developer handoff to QA

Branch `issue-156-capture-ownership`, based on `main` `497e9d4`. Implementation context for QA, not
proof: QA produces its own running-app proof.

I believe this is finished, with the platform limits stated in "What this does NOT cover".

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
