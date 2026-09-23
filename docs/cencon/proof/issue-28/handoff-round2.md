# Issue #28 - Developer handoff, round 2 (Review Gate REJECT)

The Review Gate rejected the ALREADY-MERGED implementation of issue #28 with five blocking defects.
The verdict is committed on this branch at
[`docs/cencon/review/pr30-issue28-gate.md`](../../review/pr30-issue28-gate.md) - it is the
authoritative defect list and the thing to review this change against.

**This is a fix FORWARD.** The defective code is on `main` (commit `eee17b4`, merged before the gate
had run, through an orchestrator error). There is no PR branch to repair, so this branch starts from
`main` and fixes the five defects there.

Nothing in the FEATURE changed. The two-file recording, the manifest fields, the preset fields, the
CLI flag and the Devices/status surfaces are all as merged. What changed is what happens when the
camera does not behave - which is the entire content of the gate's verdict.

---

## The five defects and what was done about each

### Defect 1 - the CLI did not own the camera through a failure boundary

`Commands.Video` opened the webcam into a nullable local and then ran ~100 lines that can throw -
gdigrab opening the screen, `sysCap.Start`, `recorder.Stop`, the audio mux, the duration probe, the
manifest save - with no `finally` and no `using` anywhere on the path. `Program.Main` reported the
error and the command exited, and that ffmpeg kept writing `camera.mp4` with the webcam held for the
life of the process.

**Fix.** Everything from the camera open to the command's return now runs inside one `try` with a
`finally { cameraRec?.Dispose(); }`. The inner `catch` that removes the empty directory when the OPEN
fails is unchanged. `src/AgentEyes.Core/Commands.cs`.

Two consequences worth reviewing deliberately:

- A camera stop that fails (defect 2) is caught in `Video`, printed, logged, and turned into exit
  code 1 - it does NOT abandon the manifest save. This is failure ISOLATION, the same shape as
  `RecordingStopSequence`, not a swallowed error: the failure is printed, logged and returned.
  Letting it propagate would strand a recording with media on disk and no manifest, which is worse
  than the defect being fixed.
- The `RecordingService` path already had a real failure boundary (`RecordingStartSequence`), which
  is why the gate flagged only the CLI. It is unchanged.

### Defect 2 - a camera stop could report success while ffmpeg was still running

The 8-second timeout was a warning, the failed `Kill` was swallowed, and the second wait's result was
ignored - so `Stop` returned normally with ffmpeg alive. `_stopped` was already set at the top of
`Stop`, so `Dispose` would not retry, and disposing a `Process` does not terminate the OS process.
The service then went idle and released the capture claim with the webcam still held.

**Fix.** `Stop` throws `CameraStopFailedException` when the process survives both the quit and the
kill. The single `_stopped` flag is split into `_stopRequested` (a stop was ASKED for - what tells a
deliberate quit from a mid-run loss) and `_terminated` (the OS process is CONFIRMED gone - the only
state that makes `Stop` a no-op), so `Dispose` still gets its retry.
`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs`.

In the service, the throw lands in `RecordingStopSequence`, which collects it, still saves the
manifest, and raises `RecordingStopFailedException` - so `/status` reports a failed stop instead of a
clean idle. No change was needed there; the comment was updated to say so.

### Defect 3 - the 400 ms probe did not establish that the camera opened

The probe slept 400 ms and rejected only a process that had ALREADY exited with a NON-ZERO code.
Anything still running - and even a process that exited with code 0 - was marked opened. A device
that took longer than 400 ms to fail therefore passed, and its later exit was filed as a harmless
mid-run loss, silently turning a camera that never recorded a frame into a screen-only recording.
That is exactly what AC9 (one of the four decisions the human made explicitly) forbids.

**Fix.** The probe now waits for a PRESENCE: ffmpeg's own first `time=` progress tick, which it only
prints once the input is open and the muxer is writing output. Anything else fails the start - the
process ending with any exit code, or the deadline (8 s) passing with no output at all. A timeout
kills the stalled ffmpeg before it throws, so the fix cannot trade one leak for another.

**This is not a theoretical margin.** On the real device here the first tick arrived **2638 ms**
after start (application log, 10:34:46) - the old probe declared that camera "opened" 2.2 seconds
before it had produced anything.

### Defect 4 - a real mid-run loss could be recorded as a clean camera track

`Stop` set `_stopped` before observing the process, which suppressed the `Exited` handler, and the
local `lost` it then computed was used only in a log line and never assigned to `_lostMidRun`. A
camera that died just before the user stopped - exit callback not yet delivered - produced
`LostMidRun == false`, so the manifest wrote `CameraTruncated: false` over a camera file that ends
early and the required warning was omitted.

**Fix.** `Stop` reads `HasExited` from the process itself, before anything suppresses the callback,
and assigns `_lostMidRun`. The callback is now explicitly the convenience and `HasExited` the fact.

### Defect 5 - a forbidden fallback in the Devices API

`GET /devices` caught every camera-enumeration exception and answered `cameras = []` with HTTP 200,
making a broken enumerator indistinguishable from a machine with no webcam - and making AC1's
"an empty array means no camera" false.

**Fix.** The `try/catch` is gone. The failure reaches the request handler, which answers 500 with the
real message. `src/AgentEyes.App/RestServer.cs`. AC1's empty-array case is unaffected: an empty
enumeration still returns `[]` with 200; only a THROW now propagates.

---

## The tests the gate said did not exist

The gate: *"a derived search of every test reference to `FfmpegCameraRecorder` found only five calls
to `DiagnoseOpenFailure`; no test exercises `Start`, `Stop`, process ownership, termination failure,
or the exit/stop race."* That was true, and it is the reason five defects survived a 12/12 QA pass.

`tests/AgentEyes.Tests/CameraFailurePathTests.cs` - 17 tests - now covers all five.

**The seam that made it possible.** `FfmpegCameraRecorder` now owns an `ICameraProcess`
(`src/AgentEyes.Core/Video/ICameraProcess.cs`) instead of a `System.Diagnostics.Process`. The real
implementation `FfmpegCameraProcess` holds NO policy - it only talks to the OS. Every decision the
gate rejected stayed in the recorder, where a test can reach it, and `StartOver(...)` runs the
identical logic over a supplied process. A test can now make a process that ignores `q`, survives a
kill, exits without delivering its callback, or never produces a frame - none of which a real ffmpeg
performs on request.

Defects 1 and 5 are STRUCTURAL (a boundary that is present / absent), so they are read from compiled
IL via the new `CompiledCode.GuardedCalls`, which reports the exception-handler regions covering a
given call and the calls a `finally` handler makes. Source text cannot answer this: a `using`
declaration writes no "finally" in the source, and a grep cannot say WHICH call a boundary covers.
`GuardedCalls` is fail-closed - an unknown method or an absent call throws rather than passing.

### Mutation evidence - every check was run against KNOWN-BAD code

`docs/cencon/proof/issue-28/mutation-evidence.txt`. Six mutations; each fires exactly the tests
written for it. The first two are not paraphrases - they are the LITERAL merged files restored from
`eee17b4`, i.e. the code the gate rejected:

| Mutation | Fires |
|----------|-------|
| `cli-boundary` (Commands.cs = eee17b4) | 1 test: *"the camera is opened at IL offset 1039 of AgentEyes.Commands::Video with only [Catch] protecting it"* |
| `devices-fallback` (RestServer.cs = eee17b4) | 1 test: *"the camera enumeration at IL offset 107 of RestServer::Devices is wrapped in [Catch]"* |
| `probe-timeout` | 2 tests |
| `probe-exitcode` | 1 test |
| `terminate` | 3 tests |
| `lost` | 1 test |

`Stop_WhenTheExitCallbackWasDelivered_StillRecordsTheTrackAsLost` deliberately keeps passing under
the `lost` mutation - it guards the OTHER route to the same fact, which the merged code did handle.

---

## How QA should verify this

### The gate

    dotnet build AgentEyes.sln -c Release      -> Build succeeded. 0 Error(s)
    dotnet test  AgentEyes.sln -c Release      -> Passed! Failed: 0, Passed: 884, Total: 884

(884, up from 867: 17 new tests. Both were run here, on this branch, after the last code change.)

### Re-run the mutation evidence yourself

Do not take the table above on trust. Restore either file from the merged commit and watch the guard
fire:

    git show eee17b4:src/AgentEyes.Core/Commands.cs > src/AgentEyes.Core/Commands.cs
    dotnet test AgentEyes.sln -c Release --filter FullyQualifiedName~CameraFailurePathTests
    git checkout src/AgentEyes.Core/Commands.cs

Expected: `TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary` FAILS naming `[Catch]`.
An all-passing run there means the guard is decorative - that is the finding, not a pass.

### The runtime checks (already run here - repeat any you want to see yourself)

The camera on this machine is `HD Webcam eMeet C960`.

1. **Happy path still records, and the new probe does not break it** (AC3/AC7 unchanged):

       agenteyes.exe video --screen 1 --camera "eMeet" --seconds 6

   Observed: exit 0; `[ok] camera.mp4 (11.6s, 9.6 MB), video only`; manifest
   `CameraFile: camera.mp4`, `CameraCapturedSeconds: 11.59`, `CameraTruncated: false`; log line
   `StartAndProbe: ... reported its first output after 2638ms`.

2. **AC9 against the real device.** Hold the webcam with another process
   (`ffmpeg -f dshow -i video="HD Webcam eMeet C960" -t 25 -f null -`), then run the same command.
   Observed: exit code **1** in **0.4 s**, `[error] the camera "HD Webcam eMeet C960" could not be
   opened (ffmpeg exited with code -5). Likely cause: the camera ... is already in use by another
   application.`, and **no new recording directory** (counted before and after).

3. **`GET /devices` unchanged on a healthy machine** - `cameras` still lists the exact name with
   HTTP 200. The 500 arm is by construction (no catch), and its shape is the RestServer handler's
   existing 500, not new code. Worth an `api-smoke.ps1` run since this endpoint changed.

4. **AC10 (mid-run kill)** is unchanged in behaviour but now also correct when the exit callback is
   late; the runtime kill-by-PID check from round 1 still applies.

### One behaviour change QA should look at deliberately

`CameraStartOffsetSeconds` is now larger in magnitude - it was `-0.4` (the old fixed sleep) and is
now the camera's true warm-up, `-2.652` on this machine. That is the honest number: the camera really
does start that far ahead of the screen recorder now, and `camera.mp4` really does contain that much
extra footage at the head. The cost is that a camera recording takes ~2.6 s to start instead of
~0.4 s. That is the price of proving the camera opened, and it is what AC9 asks for.

## CenCon impact

No drift. No change to the component map and no change to the privacy posture - the camera is still
opt-in, still named on `/status`, and a camera that cannot be opened still fails loudly rather than
recording silently. `docs/cencon/review/pr30-issue28-gate.md` (the gate's own verdict) is committed
here because the gate never commits; that is the orchestrator's or developer's job.

## Scope boundary honoured

Issue #29 (live camera preview, PR #31, branch `issue-29-camera-preview`) touches
`FfmpegCameraRecorder.Start` with a `CameraDeviceArbiter.ReleaseForRecording(...)` call at the top of
the method. Nothing here touched that branch. The call site it needs is still the first statement of
`Start`, before the `ProcessStartInfo` is built, so the rebase is one line landing in the same place.

## Statement

I believe this is finished. Build clean, 884/884 tests green, every one of the five gate defects has
a regression test that was demonstrated FAILING against the defective code first, and both the happy
path and AC9 were exercised against the real webcam on this machine.
