# Issue #28 - Developer handoff to QA

**Issue:** [Capture] Record the webcam to a separate camera.mp4 alongside the screen recording
**Branch:** `issue-28-camera-track`
**Gate:** `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.` / `0 Error(s)`;
`dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 867, Total: 867`
(41 of those are new; the suite was 826 before this change).

I believe this is finished.

---

## What was built

A SECOND, independent ffmpeg process records the webcam to `camera.mp4` in the same recording
directory as `recording.mp4`. The two files are never composited - that is the whole point of the
issue - and `camera.mp4` carries no audio track.

| File | What changed |
|------|--------------|
| `src/AgentEyes.Core/Video/FfmpegDevices.cs` | `ParseDshowVideo` + `ListVideo()`. The audio and video parsers are now ONE code path (`ParseDshowKind`) with a different marker, and one ffmpeg listing feeds both - two parsers over the same output is a defect waiting to happen. |
| `src/AgentEyes.Core/Video/FfmpegArgs.cs` | `CameraCapture(name, fps, crf, outPath)` - `-f dshow -i video=<name>`, `-an`, no `-video_size` (A2), libx264/veryfast/yuv420p/CRF like the screen (A3). |
| `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs` | NEW. Owns the camera ffmpeg process: loud failure on open (decision 3), warn-and-continue on mid-run loss (decision 4), `q`-on-stdin shutdown (A6). |
| `src/AgentEyes.Core/Video/FfmpegRecorder.cs` | `StartedUtc` added (the reference point for the offset). Nothing else changed. |
| `src/AgentEyes.Core/DeviceResolver.cs` | `ResolveCameraName` - absent throws, ambiguous throws, no "take the first camera" path. |
| `src/AgentEyes.Core/Manifest.cs` | `CameraFile`, `CameraStartOffsetSeconds`, `CameraCapturedSeconds`, `CameraTruncated` - all NULLABLE, so a camera-less recording's manifest is unchanged. |
| `src/AgentEyes.Core/RecordingService.cs` | `StartVideo(..., cameraFragment, cameraFps)`; camera start/stop steps; `_cameraName` on `/status`. |
| `src/AgentEyes.Core/Commands.cs` | `screens` camera section; `video --camera` / `--camera-fps`. |
| `src/AgentEyes.App/CapturePreset.cs` | `Camera` + `CameraFps`, in `Clone()` and `Summary()`; passed through `PresetCapture.Start`. |
| `src/AgentEyes.App/PresetEditor.xaml(.cs)` | Camera picker in CAPTURE SOURCE, loaded ASYNC (enumeration launches ffmpeg). |
| `src/AgentEyes.App/RestServer.cs` | `cameras` on `/devices`; `camera` / `cameraFps` on `/record/start`; camera on `/presets` and the discovery list. |

### Two design points worth QA's attention

**1. The camera is opened BEFORE the screen recorder, on both the service and the CLI path.**
This is load-bearing for AC8/AC9, not a style choice. `RecordingStartSequence.Discard` may only
remove a directory that holds no capture bytes. Had the screen recorder started first, its
`recording.mp4` + `.ffmpeg.log` would have kept the directory alive, and a camera that failed to
open would have left an empty recording in the Library. For the same reason
`FfmpegCameraRecorder.Start` writes its ffmpeg log to the APPLICATION log on the failed-open path,
never into the recording directory.

Consequence: `CameraStartOffsetSeconds` is NEGATIVE in normal operation (the camera leads the screen
by the ~0.4s device-open probe). It is documented as such on the field. Measured live: `-0.425`.

**2. The camera is stopped AFTER the screen recorder**, so both files carry the screen recorder's
drain wait and their durations stay close. Measured live: 9.833s vs 9.333s = 0.500s apart, inside
AC3's 1.0s window.

---

## Acceptance criteria - what was implemented, and how to exercise each

| AC | Implemented by | How QA exercises it |
|----|----------------|---------------------|
| **AC1** cameras on `/devices` | `RestServer.Devices()` adds `cameras`; enumeration failure logs and yields `[]` | `GET http://127.0.0.1:7882/devices` -> `cameras` array |
| **AC2** `agenteyes screens` | `Commands.Screens()` prints a `CAMERAS:` section | Run `agenteyes screens` from the **x64** path (see below) |
| **AC3** two files, camera video-only | `FfmpegArgs.CameraCapture` (`-an`, no audio input) + separate process | `POST /record/start {"mode":"video","camera":"<name>"}`, `POST /record/stop`; `ffprobe` both files |
| **AC4** manifest camera fields | `Manifest` + `RecordingService.Stop` / `Commands.Video` | Read `manifest.json` in that directory |
| **AC5** camera on `/status` | `RecordStatus.Camera` <- `_cameraName` (null when idle) | `GET /status` during a camera recording, and during one without |
| **AC6** preset round-trip | `CapturePreset.Camera` + async picker; `_camerasLoaded` guard | Save a preset with a camera, restart the app, reopen the editor (UIA), start it from the launcher |
| **AC7** CLI parity | `agenteyes video --screen N --camera "<frag>"` | Run it; compare directory + manifest with AC3/AC4 |
| **AC8** unknown camera fails | `DeviceResolver.ResolveCameraName` runs BEFORE the directory is created | CLI + `POST /record/start` with a bogus name |
| **AC9** busy camera fails | `FfmpegCameraRecorder.Start` open probe; camera opened first so the rollback can remove the directory | Hold the camera open in another app, then start |
| **AC10** mid-run loss | `Exited` handler sets `LostMidRun` + logs WARNING; `Stop` never throws for a dead camera; manifest records `CameraTruncated` + `CameraCapturedSeconds` | Kill the camera ffmpeg by PID mid-recording, then stop |
| **AC11** no regression | All four manifest fields are NULLABLE (`WhenWritingNull`) | Record with no camera; diff the manifest shape |
| **AC12** gate | see top of this note | Re-run both commands yourself |

### Evidence I already collected (QA still verifies independently)

Run on this machine, 2026-08-28, CLI path, ffprobe from `%LOCALAPPDATA%\AgentEyes\app\ffprobe.exe`:

- **AC2 (live).** `agenteyes screens` printed:
  ```
  CAMERAS: DirectShow video devices (used by 'video' mode --camera)
    "HD Webcam eMeet C960"
    "OBS Virtual Camera"
  ```
  and the audio section separately listed `"Microphone (HD Webcam eMeet C960)"` - i.e. the
  audio/video split holds on real hardware, not just on the parser fixtures.

- **AC3 (live).** `agenteyes video --screen 2 --camera "OBS Virtual" --seconds 6` produced
  `recording.mp4` (365,317 bytes) and `camera.mp4` (152,170 bytes). ffprobe:
  ```
  camera.mp4    -> index=0  codec_type=video  codec_name=h264  1920x1080  duration=9.833324
  recording.mp4 -> index=0  codec_type=video  codec_name=h264  1920x1080  duration=9.333333
  ```
  camera.mp4 has exactly ONE stream and it is video. Duration delta 0.500s (< 1.0s).

- **AC4 (live).** That run's manifest carried
  `"CameraFile": "camera.mp4"`, `"CameraStartOffsetSeconds": -0.425`,
  `"CameraCapturedSeconds": 9.76`, `"CameraTruncated": false`, and `"camera.mp4"` in `Files`.

- **AC8 (live).** `--camera "no-such-device"` -> exit code **1**, stderr
  `[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.`,
  and **no directory** was created.

- **AC9 (live, unplanned).** The eMeet C960 was genuinely held by another application during
  testing, so this ran for real: exit code 1,
  `[error] the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5).
  Likely cause: the camera "HD Webcam eMeet C960" is already in use by another application.`,
  and **no directory** was created. It never fell back to recording screen-only.

- **AC11 (live).** A `video` run with no `--camera` produced `manifest.json`, `recording.mp4`,
  `recording.mp4.ffmpeg.log`, `shots/` - no `camera.mp4`, and `grep -ci camera manifest.json` = **0**.

**I did NOT verify AC5, AC6, AC7-over-REST, or AC10 at runtime** - those need the WPF app running
(`/status`, the preset editor, a restart) or a deliberate mid-run process kill. They are QA's to
prove, and I am not claiming them.

---

## Two things I am flagging rather than deciding

**1. AC4 quotes the manifest keys in camelCase (`"cameraFile"`, `files`); the file format is
PascalCase.** Every property AgentEyes has ever written to `manifest.json` is PascalCase - the
committed fixtures show `"VideoFile"`, `"Files"`, and AC4's own `files` reference has the same
mismatch. I implemented `CameraFile` / `CameraStartOffsetSeconds` to match the existing format,
because changing the casing convention would break every manifest on disk and the issue #155
round-trip guarantee. This is a notation difference in the issue text, not a behavioral deviation -
but it IS a literal difference from AC4 as written, so QA/the human should confirm rather than my
assuming it away.

**2. AC2 says a `Cameras:` section; I printed `CAMERAS:`** to match the existing `MONITORS` /
`MICROPHONES` uppercase headers in that command. A case-insensitive match on `cameras:` satisfies
it; a case-sensitive one does not. Say the word and I will change the header.

Everything else follows the six assumptions (A1-A6) and the four decisions exactly as written. None
of them turned out to be impossible.

---

## Tests added (41)

- `FfmpegDevicesTests` (7): the video parser against both ffmpeg listing layouts, dedupe, empty, a
  machine with no camera, and - the important one - `ParseDshow_OneListing_SplitsCamerasAndMicrophonesCleanly`,
  which asserts BOTH sides of the same listing by exact sequence. A video parser that matched every
  quoted name would pass the other six while silently offering the microphone as a camera.
- `FfmpegArgsTests` (8): the camera arg builder - device opened, no audio input/codec/`-b:a` plus an
  explicit `-an`, no `-video_size`, encoder settings identical to the screen recorder's, fps/crf
  honored, empty device name throws, name quoted on the command line.
- `DeviceResolverTests` (7): camera fragment found / case-insensitive / absent / ambiguous / no
  cameras at all / empty fragment / exact full name. The absent and ambiguous messages are asserted
  to NAME THE FRAGMENT (AC8).
- `CameraTrackTests` (19, new file): the four manifest fields round-tripping through `ManifestStore`
  on disk, the AC11 "no camera properties at all" shape, a truncated track carrying both the flag and
  the seconds, a pre-feature manifest loading as no-camera, preset `Clone`/`Summary`/JSON round-trip
  including a pre-feature `presets.json`, and the open-failure diagnosis (including the honest arm:
  an unrecognized stderr must say "see the log" and NOT invent a cause).
- `ManifestWriterIlTests`: one line added to the pinned write-site inventory for
  `FfmpegCameraRecorder::Stop -> File::WriteAllText`. **The guard caught this on its own** - it failed
  the first run and I registered the site rather than loosening the guard.

### Mutation evidence (DEVELOPMENT_METHOD 6c, rule 3)

Each new check was run against a deliberately BROKEN build to prove it FIRES. Actual output:

| Mutation | Result |
|----------|--------|
| M1: `ParseDshowVideo` matches `(?:video\|audio)` (the classic fail-open) | **5 failed** - incl. `SplitsCamerasAndMicrophonesCleanly`, `ClassicListing_ExcludesAudioDevices` |
| M2: `CameraCapture` adds `-i audio=Default -c:a aac` | **1 failed** - `CameraCapture_HasNoAudioInputAndNoAudioCodec` |
| M3: `CameraStartOffsetSeconds` made non-nullable `double` | **2 failed** - `Manifest_WithNoCamera_WritesNoCameraProperties`, `..._LoadsAsNoCameraTrack` |
| M4: ambiguity check deleted from `ResolveCameraName` | **1 failed** - `ResolveCameraName_AmbiguousFragment_ThrowsRatherThanPickingOne` |

All four mutations were reverted; the clean tree passes 867/867.

**What these tests CANNOT see, stated plainly:** they are unit tests over pure functions and data
shapes. Not one of them opens a camera, launches ffmpeg, or inspects a produced MP4. AC3, AC9 and
AC10's runtime behavior are not covered by any test in this suite - they are covered by the live
evidence above (AC3, AC9) and by QA's running-app proof (AC10). A green suite here is NOT evidence
that a webcam was recorded.

---

## One defect found and fixed during implementation

The first live run failed with `[error] No process is associated with this object.` instead of the
real cause. `FfmpegCameraRecorder.Start` read `proc.ExitCode` inside the exception message - i.e.
AFTER `proc.Dispose()` had released the process handle. The exit code is now captured before the
dispose. `DiagnoseOpenFailure` was also widened: it looked for ffmpeg's "Could not run **filter**"
and did not match the real busy-camera text, which is "Could not run **graph** (sometimes caused by
a device already in use by other application)". `CameraTrackTests.DiagnoseOpenFailure_TheRealFfmpegBusyCameraOutput_...`
pins that verbatim stderr as a regression test. This is exactly the failure a user meets first - a
webcam already open in a browser - so it mattered that the message be right.

---

## Notes for QA

- **Run from the `x64` path.** `src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe`.
  A stale `bin\Release\` may exist and has cost an agent a false failure before.
- **Smokes worth scoping:** `api-smoke.ps1` (the `/devices`, `/record/start`, `/status` surface all
  changed) and `gui-smoke.ps1` (the preset editor gained a control). The audio path is untouched -
  `camera.mp4` never joins the deferred mux (A4), and no existing ffmpeg argument was modified.
- **Focus-free layers only:** REST / UIA / PrintWindow. Do not force-foreground the app and
  synthesize input without warning the human.
- **The recording HUD is capture-excluded** - assert recording state via UIA or `/status`, never a
  full-screen grab.
- **For AC9 you need the camera genuinely busy.** Opening it in a browser tab or a camera app is
  enough; on this machine the eMeet C960 was already held and reproduced it without any setup.
- **For AC10, kill the CAMERA ffmpeg specifically.** Two ffmpeg processes are running; the camera's
  command line contains `-f dshow ... -i video=`, the screen's contains `-f gdigrab`.

## CenCon impact

No drift. The component map is unchanged (no new project; one new class inside `AgentEyes.Core/Video`).

**Privacy posture (visible / controllable) - this change STRENGTHENS it and weakens nothing:**
the camera is opt-in and off by default (`CapturePreset.Camera` defaults to null, and a preset saved
before this feature deserializes to null); the launcher summary states "no camera" or names the
camera explicitly; `/status` reports the active camera by name while recording; and a camera that
cannot be filmed fails the recording loudly rather than recording something the user did not ask
for. There is no path by which a camera records without having been named.
