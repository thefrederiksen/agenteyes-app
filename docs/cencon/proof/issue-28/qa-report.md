# QA Proof Report - Issue #28

**[Capture] Record the webcam to a separate camera.mp4 alongside the screen recording**

| | |
|---|---|
| Issue | #28 |
| PR | #30 (`issue-28-camera-track`) |
| Commit verified | `974a5b4490e48e0359581399725d41cebf2bc0ae` |
| Verified | 2026-08-28 |
| Seat | QA Agent (independent verification) |
| Machine | 2 DirectShow cameras present: `HD Webcam eMeet C960` (physical), `OBS Virtual Camera` |
| Verdict | **VERIFIED - all 12 acceptance criteria met** (2 documented limits, neither a criterion failure) |

QA reviewed the diff and the surrounding code itself, and ran every runtime check itself. The
Developer Agent's handoff note was read as context, never as evidence.

---

## 1. Gate (AC12)

Both run by QA on the PR branch, from the `x64` output path.

```
dotnet build AgentEyes.sln -c Release
  -> Build succeeded.
     2 Warning(s)   (pre-existing xUnit1031 in PostRecordingQueueTests, untouched by this PR)
     0 Error(s)

dotnet test AgentEyes.sln -c Release
  -> Passed!  - Failed: 0, Passed: 867, Skipped: 0, Total: 867, Duration: 8 s
```

New tests present and named per the standard (`Method_Scenario_Result`): 18 in
`CameraTrackTests.cs`, 8 `CameraCapture_*` in `FfmpegArgsTests.cs`, 7 `ParseDshowVideo_*` /
`ParseDshow_OneListing_SplitsCamerasAndMicrophonesCleanly` in `FfmpegDevicesTests.cs`, and 7
`ResolveCameraName_*` (found / absent / ambiguous / empty / case-insensitive) in
`DeviceResolverTests.cs`. **AC12 PASS.**

---

## 2. Acceptance criteria

### AC1 - Devices API lists cameras. PASS

`GET http://127.0.0.1:7882/devices` (app running from the freshly built binary, timestamped
09:33:12):

```
HTTP 200
cameras key present: True
cameras = ["HD Webcam eMeet C960", "OBS Virtual Camera"]
dshow (audio) = ["Microphone (FDUCE SL40 Audio Device)", "Microphone (HD Webcam eMeet C960)"]
```

The exact DirectShow names are returned, and the parser splits cleanly: the eMeet's *microphone*
stays in `dshow` and does not leak into `cameras`. Evidence: `RestServer.cs:392-405`.

Control run: the pre-change build answering the same call had **no `cameras` key at all**, so the
check is not one that would pass either way.

> **Documented limit (stated, not silently passed):** the "machine with no camera returns `[]`"
> half of AC1 could not be exercised - this machine has two cameras. It is covered by
> `ParseDshowVideo_ListingWithNoVideoSection_ReturnsEmpty` and by the `Array.Empty` catch at
> `RestServer.cs:398-404`, but it was **not observed at runtime**.

### AC2 - CLI lists cameras. PASS

`agenteyes screens` (from `bin\x64\Release\...`):

```
CAMERAS: DirectShow video devices (used by 'video' mode --camera)
  "HD Webcam eMeet C960"
  "OBS Virtual Camera"
```

Same two names as AC1, from the same enumerator. The `(none found)` branch exists at
`Commands.cs:56`.

**Ruling on the escalated casing question ("Cameras:" vs "CAMERAS:"):** SATISFIED. Every section
header this command prints is upper case - `MONITORS (EnumDisplayMonitors)`,
`MICROPHONES - NAudio (used by 'audio' mode)`, `MICROPHONES - DirectShow (used by 'video' mode)`.
`CAMERAS:` is the house style, not a deviation. AC2's substance - a camera section listing the same
names as AC1, or `(none found)` when there are none - is met exactly. Lower-casing it to match the
issue text literally would make the output *inconsistent* with its own neighbours.

### AC3 - Two separate files. PASS

`POST /record/start {"mode":"video","screen":1,"source":"none","camera":"HD Webcam eMeet C960"}`
then `POST /record/stop`, into `C:\Users\soren\Videos\AgentEyes\2026-08-28_093920_video`:

```
camera.mp4              37,098,952 bytes
recording.mp4            1,075,740 bytes

ffprobe camera.mp4     -> index=0  codec_name=h264  codec_type=video     (EXACTLY ONE stream)
ffprobe recording.mp4  -> index=0  codec_name=h264  codec_type=video

durations: camera = 27.399973   screen = 27.400000   delta = 0.000 s  (limit 1.0 s)
```

Both files parse and play. `camera.mp4` has exactly one stream and it is `video` - no audio stream.
Two independent ffmpeg processes were observed alive during the run:

```
ProcessId 43048  ... -f dshow  ...    (camera)
ProcessId 4016   ... -f gdigrab ...   (screen)
```

**AC3 PASS.**

### AC4 - Manifest records the camera track. PASS

From the same directory's `manifest.json`:

```
  CameraFile                   'camera.mp4'
  CameraStartOffsetSeconds     -0.418        (numeric)
  CameraCapturedSeconds        27.33
  CameraTruncated              False
  Files                        ['recording.mp4', 'camera.mp4']
```

**Ruling on the escalated casing question (camelCase vs PascalCase):** SATISFIED, and the
implemented casing is the *correct* one.

This repo's manifest has always serialized PascalCase. Proof independent of this PR - a manifest
written on 2026-08-26, before this branch existed:

```json
{ "Tool": "AgentEyes", "Mode": "video", ..., "VideoFile": "recording.mp4", ... }
```

and `Manifest.JsonOptions` (`Manifest.cs:248-252`) sets `WriteIndented` and
`DefaultIgnoreCondition` but **no `PropertyNamingPolicy`**, so PascalCase is the wire format for
every field. AC4's own `files` reference carries the identical mismatch against the long-standing
`"Files"` key, which shows the AC text was describing *intent* rather than transcribing the wire
format. AC4's substance - the manifest names the camera file, carries a numeric start offset, and
lists `camera.mp4` among its files - is met on all three counts. Emitting camelCase would be a
defect: it would break every existing manifest reader.

### AC5 - Status reports the camera. PASS

```
during a camera recording:  State=recording  Camera='HD Webcam eMeet C960'
while idle:                 State=idle       Camera=None        (key present: True)
after stop:                 State=idle       Camera=(null)
during a NO-camera run:     Camera=(null)    - see AC11
```

Evidence: `RecordingService.cs:144` (`Camera = _state == "idle" ? null : _cameraName`).

### AC6 - Preset round-trip. PASS

Driven end to end through the real UI with UIA, not by editing JSON.

1. A preset `qa28 camera` was seeded with **no `Camera` key at all** (the pre-feature shape).
2. Selected it in the launcher, clicked `EditPresetButton`, and the editor opened. `CameraBox`
   loaded asynchronously and became enabled; its items were exactly:
   `(None)`, `HD Webcam eMeet C960`, `OBS Virtual Camera`.
3. Selected `HD Webcam eMeet C960` via `SelectionItemPattern`, clicked `SaveButton`. `presets.json`
   then held `Camera : HD Webcam eMeet C960`, `CameraFps : 30`.
4. **Killed and restarted the app.** `presets.json` still held the camera, and the *reopened editor*
   read back via UIA:

   ```
   REOPENED EDITOR CameraBox selection = HD Webcam eMeet C960
   ```

   The launcher summary also reflected it (`Summary()`):

   ```
   Monitor 1 Video 15fps + camera "HD Webcam eMeet C960" 30fps - Mic + System (mixed) ...
   ```

5. Started that preset **from the launcher** (`RecordButton` invoked via UIA). `/status` reported
   `State=recording Camera=HD Webcam eMeet C960`, and the directory
   `2026-08-28_094609_video` contained both files:

   ```
   camera.mp4     20,340,455
   recording.mp4     796,503
   ```

**AC6 PASS.** This run is also the strongest evidence for the video-only decision - see section 3.

### AC7 - CLI parity. PASS

`agenteyes video --screen 1 --camera "eMeet" --seconds 10` (exit 0) produced
`recordings\2026-08-28_093623_video`:

```
[ok] recording.mp4 (00m08s, 221 KB), 0 marker(s)
[ok] camera.mp4 (8.5s, 12.0 MB), video only

ffprobe camera.mp4 -> index=0 h264 codec_type=video  1920x1080   (one stream)
durations: camera 8.599991 / screen 8.300000 -> delta 0.300 s

manifest: CameraFile 'camera.mp4'  CameraStartOffsetSeconds -0.426
          CameraCapturedSeconds 8.53  CameraTruncated False
          Files ['recording.mp4', 'camera.mp4']
```

Same two-file directory and same manifest fields as AC3/AC4.

### AC8 - Unknown camera fails the start. PASS

CLI:

```
$ agenteyes video --screen 1 --camera "no-such-device" --seconds 5
[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.
EXIT CODE: 1
new recording directories: (none)
```

REST:

```
POST /record/start {"mode":"video","screen":1,"source":"none","camera":"no-such-device"}
HTTP_CODE=400
{ "error": "no DirectShow camera matches \"no-such-device\". ...", "code": "bad_request" }

/status after the failed start: State=idle  Camera=None  Dir=None
new recording directories: (none)
```

Fragment named in both messages, non-zero exit / HTTP 400, state stays `idle`, nothing left on disk.

> **Instrument note (fail-closed discipline).** The first run of this check watched
> `%USERPROFILE%\Videos\AgentEyes` while the CLI actually writes to the repo-local `recordings\`
> directory - it could never have fired. It was redone against the correct root, and the same check
> was **demonstrated to fire**: a successful recording made it report
> `> 2026-08-28_093623_video/`. An empty result was treated as a broken instrument, not a clean run.

### AC9 - Busy camera fails the start. PASS

Reproduced deliberately rather than waited for. An independent ffmpeg was started holding the
device; the Windows consent store confirmed the camera was genuinely taken:

```
IN USE: C:#Users#soren#AppData#...#ffmpeg-8.0.1-full_build#bin#ffmpeg.exe
```

Then, against that busy camera:

```
$ agenteyes video --screen 1 --camera "eMeet" --seconds 5
[error] the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5).
        Likely cause: the camera "HD Webcam eMeet C960" is already in use by another application.
EXIT CODE: 1
dirs before = 2 ; dirs after = 2 -> NO new recording directory
```

It failed loudly with an accurate diagnosis, exited non-zero, left no directory, and **did not
silently record screen-only**. `FfmpegCameraRecorder.Start` reads `ExitCode` *before* `Dispose`
(`FfmpegCameraRecorder.cs:160-176`), which is what keeps this message actionable, and it sets
`_stopped = true` before disposing so the failure path never writes an ffmpeg log into the
recording directory.

### AC10 - Camera lost mid-run does not lose the screen recording. PASS

Started a camera recording via REST, then **killed the camera ffmpeg by PID** at ~8 s:

```
camera ffmpeg pid = 44316   <- Stop-Process -Force
screen ffmpeg pid = 12360

screen ffmpeg still running: True
service state: recording | elapsed: 14.6
```

The screen recording continued. The subsequent stop succeeded (`LastStopFailed: False`) and produced
a valid, playable screen file:

```
ffprobe recording.mp4 -> h264, format mov,mp4,... , duration = 23.766667   (valid)
```

Manifest marks the track truncated with the seconds actually captured, not the session length:

```
DurationSeconds        20.62
CameraFile             camera.mp4
CameraTruncated        True
CameraCapturedSeconds  6.93      <- what ffmpeg actually wrote, not the 20.62 s session
```

Two WARNING lines naming the camera were written to
`%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-20260828.log`:

```
09:40:44.448 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during the
  recording (ffmpeg exited on its own) - the screen recording continues; camera.mp4 is truncated
  at 6.9s. See ...\camera.mp4.ffmpeg.log
09:40:58.969 [WARN] stop: the camera "HD Webcam eMeet C960" was lost during this recording -
  camera.mp4 covers 6.9s of a 20.6s session; the screen recording is unaffected
```

**AC10 PASS.**

> **Instrument note.** The first search for this warning returned nothing - not because the warning
> was missing, but because `grep` binary-detected the log and stopped. Forced text mode (`grep -a`)
> found both lines. Recorded here because a zero-hit search proves nothing on its own.

> **Documented limit.** `camera.mp4` from this hard-kill run is itself unplayable
> (`moov atom not found`), which is the expected result of SIGKILL-ing an encoder mid-write. AC10
> does not require it: it requires the *screen* recording to remain valid (it is) and the manifest to
> record the truncation (it does). The clean-stop path ("q" on stdin, assumption A6) produced a
> playable `camera.mp4` in all four normal runs.

### AC11 - No regression with no camera. PASS

`agenteyes video --screen 1 --seconds 6`, directory `2026-08-28_093812_video`:

```
manifest.json
recording.mp4              1,672,558
recording.mp4.ffmpeg.log
shots/

camera.mp4 present?        ABSENT - correct
any Camera* key in manifest? NONE  - correct
Files: ["recording.mp4"]
```

Exactly today's shape. The `Camera*` fields are `WhenWritingNull`-suppressed, so a camera-less
manifest is byte-shape-identical to a pre-feature one. The same grep demonstrably fires on the
camera runs, so its silence here is meaningful.

---

## 3. The four human decisions

| # | Decision | Verdict | Evidence |
|---|----------|---------|----------|
| 1 | `camera.mp4` is video-only, no audio stream | **HOLDS** | See below - strongest case is the AC6 launcher run |
| 2 | Works on all three surfaces (preset, REST, CLI) | **HOLDS** | AC6 (launcher preset), AC3/AC5 (REST), AC7 (CLI) |
| 3 | A camera that cannot be opened FAILS the start loudly, no orphaned directory | **HOLDS** | AC8 (absent) + AC9 (busy) - both surfaces, no directory in either |
| 4 | A camera lost mid-recording does NOT kill the screen recording | **HOLDS** | AC10 - screen ffmpeg survived a PID kill; valid 23.77 s `recording.mp4` |

**Decision 1, the decisive test.** The AC6 launcher run used a `Mic + System (mixed)` preset, so the
screen recording genuinely carried audio through the deferred mux. In that same directory:

```
ffprobe recording.mp4 -> index=0 h264 video
                         index=1 aac  audio      <- all audio is here
ffprobe camera.mp4    -> index=0 h264 video      <- ONE stream, no audio
```

So the camera track stays silent even when the screen track is not, which is the case that would
have exposed an accidental audio path. Implementation: `FfmpegArgs.CameraCapture` opens no dshow
audio input and passes `-an` (`FfmpegArgs.cs:118-158`); `camera.mp4` is written straight to its
final path and never joins `PendingMux` (`PendingMux: None` in that manifest, `AudioFile: None`).

**On the start ordering.** The camera is opened *before* the screen recorder on both the service
path (`RecordingService.cs:344-361`) and the CLI path (`Commands.cs:294-311`). This is what makes
decision 3 achievable: `RecordingStartSequence.Discard` may only remove a directory holding no
capture bytes, so had the screen recorder gone first, a failed camera open would have stranded an
empty recording in the Library. QA confirms the ordering is load-bearing and correct, and observed
zero orphaned directories across both failure modes.

---

## 4. Method and standards review

- **Coding standards.** Enterprise logging present on every new public entry point
  (`[FfmpegCameraRecorder] Start/Stop`, `[RecordingService] StartVideo`, `[PresetEditor]
  LoadCamerasAsync`). No fallback programming: `ResolveCameraName` throws on absent *and* ambiguous
  with no "take the first camera" path, and a camera that cannot open fails the start. Try-catch sits
  at entry points (`Devices()`, `LoadCamerasAsync`, `Dispose`) and at the CLI rollback, not in helper
  methods. ASCII-only throughout the diff.
- **Responsive UI.** `CameraBox` enumeration launches ffmpeg, so it runs on a background thread from
  `Loaded` with a `Loading cameras...` placeholder; QA observed the dialog open immediately and the
  picker fill in. The `_camerasLoaded` guard correctly prevents an early Save from clearing a saved
  camera - a real bug that guard exists to stop.
- **Privacy posture (visible / controllable).** Strengthened, not weakened. `Summary()` states
  `- no camera` explicitly rather than staying silent, `/status` exposes the active camera, and a
  saved-but-disconnected camera is kept and flagged in the editor rather than silently dropped. No
  stealth path was introduced.
- **Backward compatibility.** Presets written before this feature (all six on this machine) load with
  `Camera = null` / `CameraFps = 30`; manifests written before it deserialize to no camera track.
  Both verified against real on-disk files, not fixtures.

---

## 5. Verdict

**VERIFIED - all 12 acceptance criteria met.** Both escalated questions ruled SATISFIED on substance
(PascalCase is this repo's actual manifest format and the correct choice; `CAMERAS:` matches the
command's own header convention). All four human decisions hold in the running code. Two limits are
documented above rather than passed silently: AC1's no-camera-machine branch was not runtime-observed
(unit-tested only), and the hard-killed `camera.mp4` is unplayable, which AC10 does not require.

QA fixtures were cleaned up afterwards: the seeded preset was removed and `presets.json` restored
from backup, all six QA test recordings were deleted, and the app was returned to tray mode.
