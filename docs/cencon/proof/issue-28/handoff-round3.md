# Developer handoff - issue #28, ROUND 3 (PR #32, branch issue-28-camera-failure-boundaries)

Fixes the ONE defect QA round 2 found: the open probe's warm-up time landed on `camera.mp4`
only, so the two files' durations differed by 2.37 s against AC3's hard 1.0 s limit.

**Nothing else on this PR changed.** The five Review Gate defects stay fixed, and none of the
17 tests from round 2 was weakened or deleted - 5 new ones were added on top (17 -> 22 in
`CameraFailurePathTests`, 884 -> 889 in the suite).

I believe this is finished.

---

## 1. The defect, and what the fix had to satisfy at the same time

QA's finding (`qa-report-round2.md`, section 5): round 2 replaced the unreliable 400 ms sleep
with a probe that waits for ffmpeg's first PROGRESS TICK. That takes ~2.6 s on this webcam, and
the screen recorder is started only after the probe returns - so `camera.mp4` carried ~2.4 s of
head footage `recording.mp4` never had.

Two criteria had to hold together, and neither may be relaxed by an agent:

- **AC9 / decision 3** - a camera that cannot be opened FAILS THE START. Round 2's probe is what
  closed gate defect 3, and weakening it back was explicitly off the table.
- **AC3** - the two reported durations differ by no more than 1.0 s.

`CameraStartOffsetSeconds` (assumption A5) is an alignment HINT for an editor. It does not
license a 2.37 s duration divergence when AC3 states a hard limit, so it was not used as the
answer.

## 2. What changed

One file of product code: `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs`.

**The probe now waits for ffmpeg's OWN OPEN REPORT instead of its first encoded frame** - both
of these lines, on the process's stderr:

```
Input #0, dshow, from 'video=HD Webcam eMeet C960':
Output #0, mp4, to '...\camera.mp4':
```

ffmpeg prints the first only after the DirectShow capture graph ran and the stream parameters
were read off the device, and the second only once `camera.mp4` is open for writing. There is
exactly ONE input and ONE output on the camera command line (`FfmpegArgs.CameraCapture`), so
neither line can be about anything else. Everything else in the probe is unchanged: any exit
(code 0 included) fails the start, the deadline still fails it, and a stalled process is still
killed before the failure is reported.

Why this is the right presence, measured rather than assumed. Timings against the real
`HD Webcam eMeet C960` with the shipped ffmpeg, relative to the moment the process started
(1920x1080 mjpeg in, x264 veryfast, `threads=34`):

| t | event |
|---|-------|
| 0.373s | first frame actually captured (quit time minus the resulting file's duration) |
| 0.635s | `Input #0, dshow, from 'video=HD Webcam eMeet C960'` |
| 0.660s | `Output #0, mp4, to '...camera.mp4'` |
| 2.696s | first progress tick carrying a real `time=` (`frame=13 time=00:00:00.36`) |

The 2.3 s gap is **libx264 frame-threading**, not the device: x264 buffers ~34 frames before it
emits the first encoded one, while the camera has been filming since 0.373 s. So the tick is a
true statement arriving 2.3 s late, and every millisecond of that lateness is head footage on
`camera.mp4`. The open report costs ~0.26 s instead.

And it still rejects every failure decision 3 names, verified against the shipped ffmpeg:

| failure | what ffmpeg does | `Input #0` printed? |
|---------|------------------|---------------------|
| camera held by another app | `Could not run graph ...` / `Error opening input: I/O error`, exit -5 in **0.23 s** | NO |
| camera absent / unplugged | `Could not find video device with name [...]`, exit -5 in **0.03 s** | NO |
| framerate refused | `Could not set video options`, exits | NO |

All three abort inside ffmpeg's input open, before either header. No fixed sleep is involved, so
"a device that takes 500 ms to fail" - the gate's own example - still fails the start.

**What the new presence deliberately does NOT claim** is that a frame was ENCODED. A device that
opens and then stops delivering is a MID-RUN loss, which decision 4 governs: the screen recording
survives, a WARNING naming the camera goes to the log, and the manifest records the track as
truncated with the seconds actually captured. That is a reported failure, not a silent one, and
it is pinned by a new test (`Stop_WhenTheCameraOpenedAndThenNeverDeliveredAFrame_StillReportsTheLoss`).

### What was considered and rejected

- **Post-trimming `camera.mp4`** - papers over the divergence rather than fixing it, and throws
  away real footage.
- **Starting the screen recorder concurrently with the probe** (so both files start together).
  It fixes AC3, but a camera-confirm failure would then have to delete a directory that already
  holds `recording.mp4`. `RecordingStartSequence.Discard` deliberately KEEPS a directory holding
  capture bytes (issue #155: bytes plus the start manifest are a recoverable recording), so this
  would either regress AC8/AC9's "creates NO new recording directory" clause or require
  overturning another issue's decision. Not this issue's call.
- **Relaxing AC3 or AC9** - not an agent's call either.

## 3. Tests

5 added, 0 removed, 0 weakened. `CameraFailurePathTests` 17 -> 22; suite 884 -> 889.

| Test | What it pins |
|------|--------------|
| `Start_DoesNotHoldTheRecordingStartWaitingForTheFirstEncodedFrame` | **THE AC3 REGRESSION TEST.** The start must complete on the open report and must not wait for encoded output. Budget asserted is AC3's own 1.0 s. |
| `Start_WhenOnlyAProgressTickArrives_DoesNotCountThatAsAnOpenCamera` | The other side: a tick alone is not the presence, so the regression cannot be "fixed" by accepting either signal. |
| `Start_WhenFfmpegOpensTheCameraButNeverOpensTheOutputFile_FailsTheStart` | Both headers are required - half a presence is not one. |
| `TheOpenReport_IsFfmpegsTwoHeadersAndNothingElse` | The predicates, both directions, on verbatim ffmpeg output including the busy-camera failure lines and a non-dshow input header. |
| `Stop_WhenTheCameraOpenedAndThenNeverDeliveredAFrame_StillReportsTheLoss` | The one case the new presence hands to decision 4 is LOUD: `LostMidRun` true, 0.0 s captured, manifest truncated. |

The `FakeCameraProcess` open flag was split into `ReportsInputOpenOnStart` /
`ReportsOutputOpenOnStart` / `ReportsProgressOnStart`, so a fake can emit the headers WITHOUT a
tick - which is what a real ffmpeg does for the first 2 s of every camera recording.

**Demonstrated failing against the round-2 code first.** `docs/cencon/proof/issue-28/mutation-evidence-round3.txt`
holds the verbatim run: revert the single line `if (_inputOpenReported && _outputOpenReported)`
back to `if (_wroteOutput)` and 3 of the 5 fire, including the AC3 regression test.

## 4. Gate (run by me, not by the human)

```
dotnet build AgentEyes.sln -c Release
  Build succeeded.   2 Warning(s) (pre-existing xUnit1031 in PostRecordingQueueTests)   0 Error(s)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 889, Skipped: 0, Total: 889, Duration: 8 s
```

Run from the x64 Release output (`bin\x64\Release\`), never `bin\Release\`.

## 5. Runtime evidence I already collected (QA still verifies independently)

Against the real `HD Webcam eMeet C960`, x64 Release build. Full numbers in
`runtime-proof-round3.md` in this directory.

| Check | Round 2 | Round 3 |
|-------|---------|---------|
| AC3 delta over REST | 8.800 vs 11.167 = **2.367 s FAIL** | 8.800 vs 9.133 = **0.333 s PASS** |
| AC7 delta over the CLI | 9.333 vs 11.667 = **2.333 s FAIL** | 9.333 vs 9.667 = **0.333 s PASS** |
| probe duration in the log | 2593 ms | **528 / 534 / 588 / 597 ms** |
| `CameraStartOffsetSeconds` | -2.614 | **-0.54 / -0.60 / -0.61** |
| AC9 busy camera | PASS | **PASS** - HTTP 400 in 0.40 s, no directory, `/status` idle, holder still alive |
| AC10 camera killed mid-run | PASS | **PASS** - screen ffmpeg survived, `recording.mp4` valid, `CameraTruncated: true`, `CameraCapturedSeconds: 6.93`, WARN naming the camera |
| AC11 no camera | PASS | **PASS** - no `camera.mp4`, no camera keys in the manifest |

## 6. How QA should re-verify AC3 and AC9 TOGETHER

They are one check, not two - the point of this round is that neither may be bought with the
other. Do them back to back, in this order, on the same build.

**AC3 (and AC7 by parity).**

1. `AgentEyesApp.exe --tray` from `src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\`.
2. `POST http://127.0.0.1:7882/record/start`
   `{"mode":"video","screen":1,"source":"none","camera":"<your camera>"}` (note `/record/stop`
   is a **POST**; a GET answers 404).
3. Wait ~6 s, then `POST http://127.0.0.1:7882/record/stop`.
4. `ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 <dir>\recording.mp4`
   and the same for `camera.mp4`. Expected: the two differ by **<= 1.0 s**.
5. `ffprobe -v error -show_entries stream=index,codec_type -of default=nw=1 <dir>\camera.mp4`
   Expected: exactly one stream, `codec_type=video`.
6. `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-<date>.log` - expected
   `StartAndProbe: camera="..." reported the camera and camera.mp4 open after <~600>ms`.
   A four-digit number there is the regression coming back.
7. CLI parity: `agenteyes video --screen 1 --camera "<fragment>" --seconds 6` and the same two
   ffprobes. NOTE: the CLI writes to a `recordings\` directory **relative to the working
   directory**, not to `%USERPROFILE%\Videos\AgentEyes`.

**AC9, immediately after, on the same build.**

1. Hold the camera with a separate process, e.g.
   `ffmpeg -f dshow -framerate 30 -i video=<exact camera name> -c:v libx264 -an hold.mp4`.
   Build the argument list so the device name survives the shell - QA round 2 recorded this
   check failing OPEN when PowerShell split `video=HD Webcam ...` into `video=HD`. Assert the
   holder is alive BEFORE and AFTER.
2. `POST /record/start` with the camera. Expected: **HTTP 400** naming the camera, in well under
   a second; NO new directory under `%USERPROFILE%\Videos\AgentEyes\`; `GET /status` still
   `"State": "idle"`; the holder still alive.
3. Same over the CLI: non-zero exit, error naming the camera, no directory created.

**Do not accept an AC3 pass that came with an AC9 fail, or the reverse** - a probe that returns
instantly would pass AC3 and silently record screen-only, and a probe that waits for encoded
output passes AC9 and breaks AC3. Both, on one build, is the criterion.

**Also worth re-running, because the fix moves when `_opened` flips (0.6 s instead of 2.6 s):**
AC10 (kill the camera ffmpeg by PID mid-run - the screen recording must survive and the manifest
must say truncated) and AC11 (no camera selected - no `camera.mp4`, no camera key). Both were
re-run here and pass; QA's own run is the one that counts.

Reminders carried from the method: the focus-free layers are REST / UIA / PrintWindow; never
force-foreground the app and synthesize input without warning the human; the recording HUD is
capture-excluded, so HUD/recording state is asserted via UIA or `/status`, never a screen grab.
`scripts\api-smoke.ps1` still points at a non-existent `bin\Release\...` path (tracked separately
on `issue-9-smoke-x64-paths`), so drive the API by hand against the `bin\x64\Release\` binary.

## 7. CenCon impact

No drift. No change to the component map and none to the privacy posture: the camera is still
opt-in, still named on `/status`, still fails the start loudly when it cannot be opened, and
still cannot be recorded without the always-on indicator. `docs/cencon/` needs no edit.

## 8. Risk

Low, and confined to `FfmpegCameraRecorder.StartAndProbe`. The one thing to know: the probe reads
ffmpeg's stderr headers, so it depends on ffmpeg's wording - the same dependency the existing
progress parser (`FfmpegRecorder.ParseProgressMs`) and `DiagnoseOpenFailure` already carry. It is
matched on header SHAPE (`Input #0` + `, dshow,` / `Output #0`) rather than on the device name, so
a differently-quoted name cannot fail a working camera, and both predicates are pure and unit
tested in both directions. If a future ffmpeg renamed those headers the symptom would be a LOUD
failed start after 8 s, never a silent screen-only recording.
