# QA report - issue #28, ROUND 2 (PR #32, branch issue-28-camera-failure-boundaries)

**Verdict: FAIL (flow:qa-failed).**

**Criteria: 9 of 12 verified PASS. 2 FAIL (AC3, AC7). 1 NOT RE-VERIFIED (AC6).**

The five Review Gate defects are ALL genuinely fixed, and the test gap the gate named is genuinely
closed - I proved both independently with my own mutations rather than accepting the developer's
mutation-evidence file. But the fix for gate defect 3 (the open probe) introduces a REGRESSION that
breaks acceptance criterion AC3, which round 1 satisfied. AC3 is a criterion in the issue; it is not
mine to relax.

Verified on branch `issue-28-camera-failure-boundaries` at commit `3129498`, against the real webcam
`HD Webcam eMeet C960` on this machine.

---

## 1. The gate (build + tests) - PASS

Run by QA, on the x64 Release output (`bin\x64\Release\`, never `bin\Release\`).

```
dotnet build AgentEyes.sln -c Release
  Build succeeded.
  2 Warning(s)     (pre-existing xUnit1031 in PostRecordingQueueTests)
  0 Error(s)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 884, Skipped: 0, Total: 884, Duration: 8 s
```

`tests/AgentEyes.Tests/CameraFailurePathTests.cs` contributes 17 tests. The whole suite was re-run
clean after every mutation below was reverted.

---

## 2. The five gate defects - independently verified FIXED

I did NOT take the developer's claim that each new test was demonstrated failing against known-bad
code. I wrote my own mutations, applied them to the working tree, ran the tests, recorded the
output, and reverted. Every mutation FIRED. Each is a different edit from the developer's, so this
is corroboration and not a replay.

| # | Gate defect | My mutation | Result |
|---|-------------|-------------|--------|
| 3 | 400ms probe did not establish the camera opened | `FfmpegCameraRecorder.cs`: replace `if (_wroteOutput) break;` with an unconditional `break` (a still-running process counts as opened) | **FIRED** - 2 failed: `Start_WhenTheCameraNeverProducesVideo_FailsTheStartInsteadOfCallingItOpen`, `Start_WhenTheCameraNeverProducesVideo_KillsTheStalledFfmpegAndReleasesIt` |
| 3 | probe accepted an exit code of 0 | `StartAndProbe`: `if (exitCode == 0) { break; }` before the throw | **FIRED** - 1 failed: `Start_WhenFfmpegExitsWithCodeZeroDuringTheProbe_FailsTheStart` |
| 2 | stop reported success while ffmpeg ran | `Stop`: neutralise the second `WaitForExit(KillTimeoutMs)` guard | **FIRED** - 3 failed: `Stop_WhenFfmpegSurvivesTheQuitAndTheKill_ThrowsInsteadOfReportingSuccess`, `Stop_WhenTheKillItselfThrows_StillReportsTheStopAsFailed`, `Stop_AfterAFailedTermination_DisposeTriesToTerminateTheProcessAgain` |
| 2 | a failed stop blocked Dispose's retry | `Stop`: `if (_stopRequested) return;` at the top (the merged conflation of "asked for" and "finished") | **FIRED** - 1 failed: `Stop_AfterAFailedTermination_DisposeTriesToTerminateTheProcessAgain` |
| 4 | observed mid-run loss never assigned | `Stop`: neutralise the `if (_opened && _proc.HasExited && !_lostMidRun)` block | **FIRED** - 1 failed: `Stop_WhenTheCameraDiedWithoutItsExitCallbackDelivered_RecordsTheTrackAsLost` |
| 1 | CLI had no failure boundary round the camera | `Commands.cs`: replace the `finally { cameraRec?.Dispose(); }` with a `catch { cameraRec?.Dispose(); throw; }` | **FIRED** - 1 failed: `TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary`, message: `the camera is opened at IL offset 1042 of AgentEyes.Commands::Video with only [Catch, Catch] protecting it` |
| 5 | forbidden fallback in the Devices API | `RestServer.cs`: restore `try { cameras = ... } catch { Log.Error(...); cameras = Array.Empty<string>(); }` | **FIRED** - 1 failed: `TheDevicesEndpoint_DoesNotSwallowACameraEnumerationFailure` |

Six independent mutations, all five defects covered, every one produced a RED test naming the
defect. Not one of the new checks is decorative.

The gate's central finding - "no test exercises `Start`, `Stop`, process ownership, termination
failure, or the exit/stop race" - is CLOSED. The `ICameraProcess` seam and `StartOver` let the tests
drive the real recorder logic over a controllable process, and the mutations above prove each of
those five surfaces is genuinely under test.

Structural confirmation of the fixes in the shipped code:

- Defect 1: `src/AgentEyes.Core/Commands.cs:297-443` - the whole video command body is inside one
  `try`, with `finally { cameraRec?.Dispose(); }` as the camera's last owner.
- Defect 2: `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:394-404` - the second `WaitForExit`
  result is read and throws `CameraStopFailedException`; `_stopRequested` / `_terminated` are now
  separate so `Dispose` retries (`FfmpegCameraRecorder.cs:95-99`, `:457-465`).
  The throw is collected by `RecordingStopSequence.StopWriters` as a `camera stop` failure, surfaces
  in `RecordingService.LastStopFailure` (`RecordingService.cs:580`) and on `/status`
  (`RecordingService.cs:135-149`, fields `LastStopFailed` / `LastStopError` / `LastStopDir`), and
  `Dispose` still runs after it (`RecordingStopSequence.cs:213-215`).
- Defect 3: `FfmpegCameraRecorder.cs:236-273` - the probe polls until ffmpeg's own first `time=`
  progress tick, rejects ANY exit (code 0 included), and kills a stalled process before failing.
- Defect 4: `FfmpegCameraRecorder.cs:363-370` - the loss is observed from `_proc.HasExited` at stop
  time and ASSIGNED to `_lostMidRun`.
- Defect 5: `src/AgentEyes.App/RestServer.cs:405` - `string[] cameras = FfmpegDevices.ListVideo().ToArray();`
  unwrapped; the throw reaches `RestServer.cs:62` which answers HTTP 500 with the real message.

**Honest limit of the above.** These tests prove the recorder's decisions over a controlled process.
They prove nothing about ffmpeg itself; the runtime section below is what covers that.

---

## 3. Acceptance criteria

Every runtime check below was run by QA against the x64 Release build. `scripts\api-smoke.ps1` was
NOT used: its `$exe` still points at `src\AgentEyes.App\bin\Release\...` (no `x64`), a path that does
not exist on this checkout, so the script cannot run the code under test (that path bug is its own
tracked work, branch `issue-9-smoke-x64-paths`). I drove the same Control API by hand against the
correct binary instead, which is a stronger check, not a weaker one.

### AC1 - Devices API lists cameras. PASS (with a stated limit)

`GET http://127.0.0.1:7882/devices` -> **HTTP 200**, and:

```
"cameras": [ "HD Webcam eMeet C960", "OBS Virtual Camera" ]
```

Expected: 200 with the exact DirectShow name. Actual: exactly that.

LIMIT, stated rather than glossed: the "no camera on the machine -> `[]` with 200" half is NOT
verifiable here, because this machine HAS cameras. I did not unplug hardware. What I can say is that
the empty array is now honest by construction - defect 5's fallback is gone (proved by mutation
above), so `[]` can no longer mean "enumeration threw".

### AC2 - CLI lists cameras. PASS (same limit)

```
$ agenteyes screens
...
CAMERAS: DirectShow video devices (used by 'video' mode --camera)
  "HD Webcam eMeet C960"
  "OBS Virtual Camera"
```

Same names as AC1. The `(none found)` branch is not verifiable on this hardware.

### AC3 - Two separate files. **FAIL**

Run exactly as the criterion specifies:
`POST /record/start {"mode":"video","screen":1,"source":"none","camera":"HD Webcam eMeet C960"}`,
6 s, then `POST /record/stop`. Directory `C:\Users\soren\Videos\AgentEyes\2026-08-28_104509_video`.

What PASSES:

```
recording.mp4            169,276 bytes
camera.mp4            13,814,595 bytes
ffprobe camera.mp4    -> index=0  codec_type=video     (EXACTLY ONE stream, no audio)
ffprobe recording.mp4 -> index=0  codec_type=video
```

What FAILS - the duration clause:

```
ffprobe recording.mp4 duration =  8.800000
ffprobe camera.mp4    duration = 11.166656
                        delta  =  2.366656 s      LIMIT: 1.0 s
```

**Expected:** the two reported durations differ by no more than 1.0 second.
**Actual:** they differ by 2.37 s.

This is a REGRESSION introduced by this PR, not a pre-existing condition. Round 1's QA report
(`docs/cencon/proof/issue-28/qa-report.md`, AC3) recorded on the merged code:

```
durations: camera = 27.399973   screen = 27.400000   delta = 0.000 s  (limit 1.0 s)
CameraStartOffsetSeconds  -0.418
```

This round's manifest for the same flow:

```
CameraStartOffsetSeconds  -2.614
CameraCapturedSeconds     11.09
```

Cause, from the application log:

```
10:43:05.961 [INFO] [FfmpegCameraRecorder] Start: camera="HD Webcam eMeet C960" ...
10:43:08.562 [INFO] [FfmpegCameraRecorder] StartAndProbe: camera="HD Webcam eMeet C960"
             reported its first output after 2593ms
```

The new open probe blocks the recording start until ffmpeg's first progress tick - 2.6 s on this
device, consistently (2593 ms and 2614 ms on two separate runs). The camera process is capturing for
that whole time, and the screen recorder does not start until the probe returns, so `camera.mp4`
carries ~2.4 s of extra head footage that `recording.mp4` does not have. The old 400 ms fixed sleep
happened to land the two starts close enough together to satisfy the 1.0 s clause; the correct probe
does not.

The developer's handoff (`handoff-round2.md`, "One behaviour change QA should look at deliberately")
reports the offset change from -0.4 to -2.652 and calls it "the honest number ... the price of
proving the camera opened". It is the honest number - but the issue's AC3 says 1.0 s, and a criterion
is changed by the human, not by the agent verifying against it (DEVELOPMENT_METHOD.md 6c item 4).
As written, AC3 is not met.

**AC3 FAIL.**

### AC4 - Manifest records the camera track. PASS

From the same directory's `manifest.json`:

```
CameraFile                'camera.mp4'
CameraStartOffsetSeconds  -2.614      (numeric)
CameraCapturedSeconds     11.09
CameraTruncated           False
Files                     ['recording.mp4', 'camera.mp4']
```

All three required elements present.

### AC5 - Status reports the camera. PASS

During the camera recording:

```
"State": "recording", "Camera": "HD Webcam eMeet C960"
```

During a recording started with NO camera (dir `2026-08-28_104756_video`):

```
"State": "recording", "Camera": null
```

Both halves observed. PASS.

### AC6 - Preset round-trip. **NOT RE-VERIFIED** (not a pass)

This PR changes no preset code - the diff touches only `RestServer.cs`, `Commands.cs`,
`RecordingService.cs`, `Video/FfmpegCameraRecorder.cs`, `Video/ICameraProcess.cs`, and tests/docs.
`CapturePreset.cs` and `PresetEditor.xaml(.cs)` are untouched, so round 1's verification of the
save/restart/reopen path still describes the shipped code. I did not re-drive the preset editor
through UIA this round.

Recording it honestly: NOT RE-VERIFIED, therefore NOT counted as passed. Note also that AC6's final
clause ("starting that preset from the launcher produces the two-file directory of AC3") inherits
the AC3 failure above.

### AC7 - CLI parity. **FAIL** (inherits AC3)

```
$ agenteyes video --screen 1 --camera "eMeet" --seconds 6
[ok] recording.mp4 (00m09s, 150 KB), 0 marker(s)
[ok] camera.mp4 (11.6s, 17.4 MB), video only
[ok] manifest.json written to ...\recordings\2026-08-28_104305_video
EXIT 0
```

Structure and manifest fields match AC3/AC4: both files present, `CameraFile: camera.mp4`,
`CameraStartOffsetSeconds: -2.606`, `CameraCapturedSeconds: 11.59`, `CameraTruncated: false`,
`Files: ['recording.mp4','camera.mp4']`.

But "the same ... as AC3" includes AC3's duration clause, and it fails identically:

```
ffprobe recording.mp4 duration =  9.333333
ffprobe camera.mp4    duration = 11.666655
                        delta  =  2.333322 s      LIMIT: 1.0 s
```

Log for the same run: `reported its first output after 2593ms`. **AC7 FAIL** on the same root cause.

### AC8 - Unknown camera fails the start. PASS

CLI (`%USERPROFILE%\Videos\AgentEyes` counted before and after, and the repo-local `recordings\`):

```
$ agenteyes video --screen 1 --camera "no-such-device" --seconds 5
[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.
EXIT CODE: 1
recording directories before = 4, after = 4   (none created)
```

REST:

```
POST /record/start {"mode":"video","screen":1,"source":"none","camera":"no-such-device"}
HTTP 400
{"error":"no DirectShow camera matches \"no-such-device\". Run 'agenteyes screens' to list cameras.",
 "code":"bad_request"}
recording directories before = 4, after = 4
GET /status -> "State": "idle"
```

Fragment named on both surfaces, non-zero exit / 400, no directory, state idle. PASS.

### AC9 - Busy camera fails the start. PASS

**This check failed open on my first attempt and I redid it.** My first run held the camera with
`Start-Process ffmpeg -ArgumentList '-f','dshow','-i','video=HD Webcam eMeet C960',...`; PowerShell's
argument splitting turned the device into `video=HD`, ffmpeg died immediately, the camera was never
busy, and the recording succeeded with exit 0 - a "pass" that would have proved nothing. The
corrected run asserts the precondition explicitly, before AND after.

CLI, holder = a separate ffmpeg holding the webcam, verified alive on both sides of the attempt:

```
PRECONDITION holder alive: True
  (last line: frame= 122 fps= 30 ... time=00:00:04.06 ...)
$ agenteyes video --screen 1 --camera "eMeet" --seconds 5
[error] the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5).
        Likely cause: the camera "HD Webcam eMeet C960" is already in use by another application.
EXIT CODE: 1        ELAPSED: 0.44 s
recording directories before = 2, after = 2   (none created)
holder still alive at end: True
```

REST, same precondition:

```
PRECONDITION holder alive: True
POST /record/start {..., "camera":"HD Webcam eMeet C960"}   -> HTTP 400
recording directories before = 4, after = 4
GET /status -> "State": "idle"
holder still alive: True
```

Never a silent screen-only recording, on either surface. PASS.

### AC10 - Camera lost mid-run does not lose the screen recording. PASS

`2026-08-28_104818_video`. Camera ffmpeg identified by command line (exactly one match, asserted
before the kill), then killed by PID:

```
camera ffmpeg PIDs: 47780            (precondition: exactly one)
camera ffmpeg alive after kill: False
screen ffmpeg alive after kill: True    <- the screen recording survived
POST /record/stop -> HTTP 200, DurationSeconds 9.87
ffprobe recording.mp4 -> codec_type=video  duration=12.933333   (valid, playable)
manifest: CameraFile=camera.mp4  CameraTruncated=True  CameraCapturedSeconds=4.93
```

Log WARNINGs naming the camera:

```
10:48:26.573 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during the
             recording (ffmpeg exited on its own) - the screen recording continues; camera.mp4 is
             truncated at 4.9s.
10:48:34.288 [WARN] stop: the camera "HD Webcam eMeet C960" was lost during this recording -
             camera.mp4 covers 4.9s of a 9.9s session; the screen recording is unaffected
```

All four required elements present. PASS.

### AC11 - No regression with no camera. PASS

`2026-08-28_104756_video`, started with no camera:

```
manifest.json  recording.mp4  recording.mp4.ffmpeg.log  thumb.jpg  shots\
NO camera.mp4 on disk
manifest.json: no cameraFile key
```

Exactly today's shape. PASS.

### AC12 - Gate. PASS

Build succeeded / 0 Error(s), and 884/884 tests pass - both re-run by QA on this branch, and re-run
clean after each mutation was reverted. New tests cover the video-device parser, the camera arg
builder, camera fragment resolution, and (new this round) all five gate-defect failure paths.

---

## 4. Method and standards review

- ASCII only - no Unicode found in the new source, tests, or docs.
- No AI-vendor attribution strings in the diff.
- No fallback programming introduced; one was REMOVED (gate defect 5). The remaining
  `catch { dshow = Array.Empty<string>(); }` on the AUDIO enumeration at `RestServer.cs:394` is
  pre-existing and outside this issue's scope - flagged, not counted against this PR.
- Enterprise logging present on every new public path in `FfmpegCameraRecorder`.
- Try/catch placement: the per-step protection in `RecordingStopSequence` and the CLI's boundary are
  entry points that report rather than hide; consistent with the standard.
- Privacy posture intact: the camera is still opt-in, still named on `/status`, and a camera that
  cannot be opened still fails loudly.
- One residual, recorded not counted: after a `CameraStopFailedException` the service still returns
  to `idle` and releases the capture claim (`RecordingService.cs:585-607`), reporting the failure via
  `LastStopFailure` / `/status`. That is the deliberate issue #153 design (a failed stop is reported,
  not hidden), and it is a real improvement on the merged behaviour the gate rejected, which reported
  a clean stop. Whether idle-after-a-failed-camera-stop is the right end state is a product question
  for the human, not a defect in this PR.

---

## 5. What the Developer Agent must fix

ONE defect. Everything else on this PR is good work and should be kept.

**Defect: the open probe's warm-up time is added to `camera.mp4` only, breaking AC3's 1.0 s duration
clause (and AC7 by parity).**

Reproduce:

```
1. Start the app: AgentEyesApp.exe --tray
2. POST http://127.0.0.1:7882/record/start
     {"mode":"video","screen":1,"source":"none","camera":"HD Webcam eMeet C960"}
3. Wait ~6 s.  POST http://127.0.0.1:7882/record/stop
4. ffprobe -v error -show_entries format=duration <dir>\recording.mp4
   ffprobe -v error -show_entries format=duration <dir>\camera.mp4
```

Expected (AC3): the two durations differ by no more than 1.0 s.
Actual: 8.80 s vs 11.17 s, delta 2.37 s. Manifest `CameraStartOffsetSeconds: -2.614`.
Log: `StartAndProbe: ... reported its first output after 2593ms`.

Root cause: `FfmpegCameraRecorder.StartAndProbe` (`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:236-273`)
now holds the recording start until ffmpeg's first progress tick, while the camera process has
already been capturing for that entire period. `Commands.cs:297` and the service start sequence both
start the screen recorder only after the probe returns.

DO NOT fix this by weakening the probe - the probe is correct and it is what closes gate defect 3 and
AC9. Fix the alignment instead. The proof of the fix must be a new ffprobe delta under 1.0 s on this
hardware, plus a regression test, plus AC9 still failing the start on a busy camera.

---

**NOT VERIFIED - 2 of 12 acceptance criteria FAIL (AC3, AC7), 1 not re-verified (AC6).**
**All five Review Gate defects independently confirmed FIXED; the named test gap is CLOSED.**
