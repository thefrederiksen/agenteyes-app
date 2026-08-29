# Runtime evidence - issue #28 round 3 (developer's own, QA still verifies independently)

Everything below was run by the Developer Agent against the x64 Release build
(`src\...\bin\x64\Release\net8.0-windows10.0.19041.0\`) and the real webcam
`HD Webcam eMeet C960`, on 2026-08-28. It is implementation evidence, NOT the QA proof.

---

## 0. The measurement that chose the fix

`ffmpeg` run with the exact `FfmpegArgs.CameraCapture` arguments, every stderr line timestamped
from the moment the process started, quit with `q` after 10.0 s:

```
   0.635  Input #0, dshow, from 'video=HD Webcam eMeet C960':
   0.635    Duration: N/A, start: 33764.902015, bitrate: N/A
   0.635    Stream #0:0: Video: mjpeg (Baseline) (MJPG), yuvj422p, 1920x1080, 30 fps
   0.636  Stream mapping:  Stream #0:0 -> #0:0 (mjpeg (native) -> h264 (libx264))
   0.659  [libx264 ...] options: ... threads=34 lookahead_threads=8 ... rc_lookahead=10 ...
   0.660  Output #0, mp4, to '...\measure_camera.mp4':
   1.149  frame=    0 fps=0.0 q=0.0 size=       0KiB time=N/A     <- no output yet
   1.665  frame=    0 ...                                          time=N/A
   2.181  frame=    0 ...                                          time=N/A
   2.696  frame=   13 fps=6.3 q=29.0 size=  512KiB time=00:00:00.36  <- FIRST real tick
  10.006  quit sent
  10.915  process exit

camera.mp4 duration = 9.633
implied capture START (quit time - duration) = 0.373
```

Reading: the device was filming from **0.373 s**; ffmpeg reported it open at **0.635 s**; the
first ENCODED frame did not surface until **2.696 s**, because libx264 buffers ~34 frames
(`threads=34`) before emitting one. Round 2 blocked the recording start on that last number.

## 1. The failure paths still fail, and fail before either header

```
holder alive: True
BUSY attempt exit code=-5 after 0.232s
   0.223  [in#0 @ ...] Could not run graph (sometimes caused by a device already in use by other application)
   0.228  [in#0 @ ...] Error opening input: I/O error
   0.228  Error opening input file video=HD Webcam eMeet C960.
contains 'Input #0': False
contains 'Output #0': False
busy.mp4 exists: False
holder still alive: True

UNKNOWN exit=-5 after 0.032s
[in#0 @ ...] Could not find video device with name [NoSuchCameraXYZ] among source devices of type video.
contains 'Input #0': False
```

## 2. AC3 - two separate files, REST Control API

`POST /record/start {"mode":"video","screen":1,"source":"none","camera":"HD Webcam eMeet C960"}`,
6 s, `POST /record/stop`. Directory `C:\Users\soren\Videos\AgentEyes\2026-08-28_110743_video`.

```
POST /record/start -> 200   "State": "recording",  "Camera": "HD Webcam eMeet C960"
POST /record/stop  -> 200   "DurationSeconds": 6.01
FILES: camera.mp4, camera.mp4.ffmpeg.log, manifest.json, recording.mp4,
       recording.mp4.ffmpeg.log, shots, thumb.jpg

ffprobe recording.mp4 duration = 8.800000
ffprobe camera.mp4    duration = 9.133324
                        delta  = 0.333324 s      LIMIT: 1.0 s     -> PASS
camera.mp4 streams: index=0 codec_type=video      (exactly one stream, no audio)

manifest CameraFile               = 'camera.mp4'
manifest CameraStartOffsetSeconds = -0.6
manifest CameraCapturedSeconds    = 9.06
manifest CameraTruncated          = False
manifest Files                    = ['recording.mp4', 'camera.mp4']
```

Round 2's number for the identical flow was 8.800000 vs 11.166656, delta **2.366656 s**.

## 3. AC7 - CLI parity

`agenteyes video --screen 1 --camera "eMeet" --seconds 6`, exit 0:

```
[ok] recording monitor 1 (1920x1080) + video only
     camera: "HD Webcam eMeet C960" -> camera.mp4 (30 fps, video only)
[ok] recording.mp4 (00m09s, 297 KB), 0 marker(s)
[ok] camera.mp4 (9.6s, 8.5 MB), video only
[ok] manifest.json written to ...\recordings\2026-08-28_110612_video

ffprobe recording.mp4 duration = 9.333333
ffprobe camera.mp4    duration = 9.666657
                        delta  = 0.333324 s      LIMIT: 1.0 s     -> PASS
camera.mp4 streams: index=0 codec_type=video

manifest CameraFile = camera.mp4   CameraStartOffsetSeconds = -0.54
         CameraCapturedSeconds = 9.59   CameraTruncated = False
         Files = ['recording.mp4', 'camera.mp4']
```

Round 2: 9.333333 vs 11.666655, delta **2.333322 s**.

## 4. The probe's own log line, every run this round

```
11:06:13.073 [INFO] [FfmpegCameraRecorder] StartAndProbe: camera="HD Webcam eMeet C960" reported the camera and camera.mp4 open after 528ms
11:07:16.539 [INFO] [FfmpegCameraRecorder] StartAndProbe: ... open after 534ms
11:07:43.901 [INFO] [FfmpegCameraRecorder] StartAndProbe: ... open after 588ms
11:08:32.857 [INFO] [FfmpegCameraRecorder] StartAndProbe: ... open after 597ms
```

Round 2 logged `reported its first output after 2593ms` / `2614ms` for the same device.

## 5. AC9 - busy camera STILL fails the start

Holder = a separate ffmpeg holding the webcam, asserted alive before and after:

```
PRECONDITION holder alive: True
POST /record/start (busy) -> 400 after 0.40s
{"error":"the camera \"HD Webcam eMeet C960\" could not be opened (ffmpeg exited with code -5).
  Likely cause: the camera \"HD Webcam eMeet C960\" is already in use by another application.",
 "code":"bad_request"}
recording directories before = 8  after = 8      (none created)
GET /status -> "State": "idle"
holder still alive: True
AC9: PASS
```

Application log for the same attempt:

```
11:08:01.178 [ERROR] [FfmpegCameraRecorder] Start FAILED: camera="HD Webcam eMeet C960"
             ffmpeg exited with code -5 cmd=... -f dshow ... -i "video=HD Webcam eMeet C960" ...
```

## 6. AC10 - camera lost mid-run (re-checked: the fix moves when `_opened` flips)

```
camera ffmpeg PIDs: [38768]   screen ffmpeg PIDs: [13072]     (precondition: exactly one camera)
camera ffmpeg alive after kill: False
screen ffmpeg alive after kill: True          <- the screen recording survived
POST /record/stop -> 200, DurationSeconds 14.47
ffprobe recording.mp4: index=0 codec_type=video duration=17.566667      (valid, playable)
manifest CameraFile = 'camera.mp4'   CameraStartOffsetSeconds = -0.608
         CameraCapturedSeconds = 6.93   CameraTruncated = True

11:08:42.367 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during the
             recording (ffmpeg exited on its own) - the screen recording continues; camera.mp4 is
             truncated at 6.9s.
11:08:50.620 [WARN] stop: the camera "HD Webcam eMeet C960" was lost during this recording -
             camera.mp4 covers 6.9s of a 14.5s session; the screen recording is unaffected
```

## 7. AC11 - no camera, unchanged shape

```
FILES: manifest.json, recording.mp4, recording.mp4.ffmpeg.log, shots, thumb.jpg
camera keys in manifest: []
camera.mp4 on disk: False
```

## 8. Gate

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded.  2 Warning(s)  0 Error(s)
dotnet test  AgentEyes.sln -c Release   -> Passed!  Failed: 0, Passed: 889, Total: 889, 8 s
```
