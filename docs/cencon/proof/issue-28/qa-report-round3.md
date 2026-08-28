# QA report - issue #28, ROUND 3 (PR #32, branch issue-28-camera-failure-boundaries)

**Verdict: PASS. 12/12 acceptance criteria verified.** Handed to the Review Gate
(`flow:ready-gate`). QA does not merge (DEVELOPMENT_METHOD.md D7, superseding D5).

Verified at commit `06c8596` ("Open the camera on ffmpeg's open report, not its first encoded
frame"), local tree == `origin/issue-28-camera-failure-boundaries`, working tree clean.

Hardware: `HD Webcam eMeet C960` (a second DirectShow camera, `OBS Virtual Camera`, is also
present). Every runtime number below is from a run I made in this context; nothing in the
developer's handoff or `runtime-proof-round3.md` was accepted as proof.

---

## 0. What this round had to prove, and how each check fails closed

Round 2 failed this issue 9/12: the round-2 open probe waited for ffmpeg's first PROGRESS TICK
(~2.6 s), so `camera.mp4` carried head footage `recording.mp4` never had and the two durations
came out 2.37 s apart against AC3's hard 1.0 s limit. Round 3 changes the probe's presence to
ffmpeg's own two open headers.

The crux is therefore that **AC3 and AC9 hold at the same time, on one build**. A probe that
returns instantly passes AC3 and silently records screen-only; a probe that waits for encoded
output passes AC9 and breaks AC3. Both were measured here, back to back, on the build I made.

Per Section 6c, each check below names its PRESENCE, and two instruments that could have failed
open were demonstrated FIRING before they were believed (Section 4.3 and Section 6).

---

## 1. Gate: build and tests (AC12)

The build first failed because a leftover `AgentEyesApp` (PID 38408, started 11:07 from this
repo's `bin\x64\Release`, `/status` = idle - the developer's round-3 runtime-proof instance) held
`agenteyes.dll` and `AgentEyes.Setup.Engine.dll`. Stopped it, rebuilt.

```
dotnet build AgentEyes.sln -c Release
  Build succeeded.
  2 Warning(s)   (pre-existing xUnit1031 in PostRecordingQueueTests)
  0 Error(s)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 889, Skipped: 0, Total: 889, Duration: 8 s
```

Binaries confirmed rebuilt in `bin\x64\Release\...` (timestamps 11:14). There is NO stale
`src\AgentEyes.App\bin\Release\` or `src\AgentEyes.Core\bin\Release\` directory in this checkout -
checked explicitly, both absent - so the known false-test-result trap could not apply. Every
runtime check below drove the `bin\x64\Release` binaries.

**AC12: PASS.**

---

## 2. Test inventory - nothing weakened, nothing deleted

`CameraFailurePathTests`: round 2 = 17 `[Fact]`s, round 3 = 22. Suite 884 -> 889.

Compared `git show 3129498:tests/AgentEyes.Tests/CameraFailurePathTests.cs` against the branch tip.
All 17 round-2 tests survive. Exactly one is RENAMED, and its assertions are byte-identical:

| round 2 | round 3 |
|---------|---------|
| `Start_WhenFfmpegReportsItsFirstOutput_OpensTheCamera` | `Start_WhenFfmpegReportsTheCameraAndTheFileOpen_OpensTheCamera` |

The only other edits to existing tests are plumbing: the fake's single `ReportsOutputOnStart` flag
is split into `ReportsInputOpenOnStart` / `ReportsOutputOpenOnStart` / `ReportsProgressOnStart`, so
a fake can emit the headers WITHOUT a tick (which is what a real ffmpeg does for the first ~2 s).
No assertion was removed, relaxed, or `[Skip]`ped. Five tests added.

---

## 3. Mutation evidence - I reverted each fix myself and watched the test fire

Every claim below is MY OWN run, not the developer's `mutation-evidence-round3.txt`. Full transcript:
`docs/cencon/proof/issue-28/qa-mutations-round3.txt`.

| # | Mutation (fix reverted) | Tests that FIRED |
|---|--------------------------|------------------|
| M1 | probe presence `_inputOpenReported && _outputOpenReported` -> `_wroteOutput` (the round-2 code) | 3 failed: `Start_DoesNotHoldTheRecordingStartWaitingForTheFirstEncodedFrame` (the AC3 regression test - blocked 10 s and then threw), `Start_WhenOnlyAProgressTickArrives_DoesNotCountThatAsAnOpenCamera`, `Stop_WhenTheCameraOpenedAndThenNeverDeliveredAFrame_StillReportsTheLoss` |
| M2 | `&&` -> `||` in the same predicate | 1 failed: `Start_WhenFfmpegOpensTheCameraButNeverOpensTheOutputFile_FailsTheStart` |
| M3 | gate defect 5: re-wrap `FfmpegDevices.ListVideo()` in `try/catch -> Array.Empty` in `RestServer.Devices` | 1 failed: `TheDevicesEndpoint_DoesNotSwallowACameraEnumerationFailure` |
| M4 | gate defect 1: outer `finally { cameraRec?.Dispose(); }` in `Commands.Video` -> `catch { throw; }` | 1 failed: `TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary` |
| M5 | gate defect 2: drop `throw new CameraStopFailedException(...)` from `Stop` | 3 failed: `Stop_WhenFfmpegSurvivesTheQuitAndTheKill_ThrowsInsteadOfReportingSuccess`, `Stop_WhenTheKillItselfThrows_StillReportsTheStopAsFailed`, `Stop_AfterAFailedTermination_DisposeTriesToTerminateTheProcessAgain` |
| M6 | gate defect 4: delete the stop-time `if (_opened && _proc.HasExited && !_lostMidRun)` block | 1 failed: `Stop_WhenTheCameraDiedWithoutItsExitCallbackDelivered_RecordsTheTrackAsLost` |

All five Review Gate defects therefore still have live regression tests that genuinely fail when
their fix is removed - re-verified in round 3, not carried over from round 2. Sources were restored
from byte-copies afterwards; `git status` is clean and the 889/889 run in Section 1 is the restored
tree.

Note on M1: `Start_WhenFfmpegOpensTheCameraButNeverOpensTheOutputFile_FailsTheStart` does NOT fire
under M1 (the round-2 predicate also rejects that fake). That is why M2 exists - each new test was
mutated against the fix it actually pins, not against one convenient mutation.

---

## 4. The crux: AC3 and AC9 together, on one build

### 4.1 AC3 - two separate files, durations within 1.0 s (REST)

`POST /record/start {"mode":"video","screen":1,"source":"none","camera":"HD Webcam eMeet C960"}`,
then `POST /record/stop`. Directory `2026-08-28_111811_video`:

```
camera.mp4              28,233,949 bytes
recording.mp4              404,798 bytes
manifest.json, thumb.jpg, shots\, *.ffmpeg.log

ffprobe format=duration  recording.mp4 -> 21.166667
ffprobe format=duration  camera.mp4    -> 21.299979
                                 DELTA -> 0.133 s      (AC3 limit 1.0 s)

ffprobe stream=index,codec_type camera.mp4
  index=0
  codec_name=h264
  codec_type=video          (exactly ONE stream, video - no audio stream)
```

Both play: `ffmpeg -v error -i <file> -f null -` decoded BOTH files end to end with **no error
output and exit 0**.

Probe cost, from `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-20260828.log`:

```
11:18:12.452 [INFO] [FfmpegCameraRecorder] StartAndProbe: camera="HD Webcam eMeet C960"
             reported the camera and camera.mp4 open after 595ms
```

Three digits, as the handoff predicted. Round 2's number in this same log line was 2593 ms.

**Expected:** both files present, both play, camera video-only, durations within 1.0 s.
**Actual:** exactly that; delta 0.133 s. **AC3: PASS.**

### 4.2 AC3 by parity over the CLI (AC7)

`agenteyes video --screen 1 --camera "HD Webcam eMeet C960" --seconds 8`, run from a scratch working
directory (the CLI writes to `recordings\` relative to cwd, not to `%USERPROFILE%\Videos\AgentEyes`).

```
[ok] recording.mp4 (00m11s, 232 KB), 0 marker(s)
[ok] camera.mp4 (11.6s, 11.2 MB), video only
exit 0

ffprobe recording.mp4 -> 11.366667
ffprobe camera.mp4    -> 11.666655
                DELTA -> 0.300 s

camera.mp4 streams: index=0 codec_type=video      (single video stream)
manifest: "CameraFile": "camera.mp4", "CameraStartOffsetSeconds": -0.586,
          "CameraCapturedSeconds": 11.59, "CameraTruncated": false,
          "Files": [ "recording.mp4", "camera.mp4" ]
```

**AC7: PASS.**

A third and fourth measurement, from the AC6 launcher run (a preset with mic audio, so
`recording.mp4` goes through the mic post-processing path): 12.866667 vs 13.199987 =
**0.333 s**, `recording.mp4` = video+audio, `camera.mp4` = video only.

Probe timings observed across all four camera recordings I made: **571 / 595 / 651 / 781 ms**.
Duration deltas: **0.133 / 0.300 / 0.333 / 0.333 s**. Recorded honestly: the delta is essentially
the probe cost minus the webcam's own ~0.4 s warm-up, so the headroom under AC3's 1.0 s is real but
not unlimited. Every measurement I took passes with margin; a probe that took over ~1.4 s would not.
That is an observation about the margin, not a violation - AC3 as written is met on every run.

### 4.3 AC9 - busy camera still fails the start (REST)

**The instrument was demonstrated firing before it was trusted.** My first attempt to hold the
camera passed the device name through PowerShell's `Start-Process -ArgumentList` array, which split
it: ffmpeg reported `Could not find video device with name [HD]` and exited. That run printed
`HOLDER_ALIVE: False`, `STDERR_HAS_INPUT0_DSHOW: False` - i.e. the guard REFUSED to proceed. This is
the exact fail-open QA round 2 recorded. Re-run with a properly quoted single command-line string:

```
HOLDER_PID: 45084
HOLDER_ALIVE_BEFORE:   True
HOLDER_OPENED_CAMERA:  True      (its stderr contains "Input #0, dshow")
HOLDER_BYTES_BEFORE:   4,194,352 (it is really encoding frames off the device)
```

With the camera held, `POST /record/start` with the same body as AC3:

```
HTTP_STATUS=400
{
  "error": "the camera \"HD Webcam eMeet C960\" could not be opened (ffmpeg exited with
            code -5). Likely cause: the camera \"HD Webcam eMeet C960\" is already in use by
            another application.",
  "code": "bad_request"
}

GET /status  ->  "State": "idle",  "Dir": null,  "Camera": null
%USERPROFILE%\Videos\AgentEyes:  11 directories before, 11 after; diff of the two
                                 full listings is EMPTY (no new directory)
holder after the attempt:        ALIVE
```

The directory check is a listing-vs-listing comparison, not a count, and it is proven to detect a
new directory: the AC3 run in 4.1 moved that same listing from 10 entries to 11.

### 4.4 AC9 over the CLI

The first CLI attempt at this **failed open and I caught it**: the holder process had exited on its
own between two tool calls, so the camera was free and the CLI recorded successfully (exit 0, a new
directory). That is not an AC9 result. Re-run with the holder start, the liveness assertions, the
CLI invocation and the after-checks all inside ONE process lifetime:

```
HOLDER_PID: 45596
HOLDER_ALIVE_BEFORE:   True
HOLDER_OPENED_CAMERA:  True
HOLDER_BYTES_BEFORE:   8,912,944

CLI stdout: [ok] recording monitor 1 (1920x1080) + video only
CLI stderr: [error] the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with
            code -5). Likely cause: the camera "HD Webcam eMeet C960" is already in use by
            another application.
CLI_EXIT_CODE: 1

CLI recordings dirs:  2 before, 2 after,  NEW_CLI_DIRS: (empty)
Videos\AgentEyes:    11 before, 11 after, NEW_VIDEO_DIRS: (empty)
HOLDER_ALIVE_AFTER:  True
```

**Expected:** non-zero exit / HTTP 400 naming the camera, no directory, state stays idle, never a
silent screen-only recording. **Actual:** exactly that on both surfaces, with the holder proven to
own the device before and after. **AC9: PASS.**

**AC3 and AC9 both hold on the same build.** Neither was bought with the other.

---

## 5. The remaining acceptance criteria

### AC1 - Devices API lists cameras. PASS

`GET /devices` -> 200, `"cameras": [ "HD Webcam eMeet C960", "OBS Virtual Camera" ]`, matching AC2.

The clause "on a machine with no camera the call still returns 200 and `cameras` is `[]`" is
**NOT runtime-verified - this machine has two cameras and I have no camera-less machine.** Judged
from code instead, and stated as such: `RestServer.Devices()` (src/AgentEyes.App/RestServer.cs:396-405)
now calls `FfmpegDevices.ListVideo().ToArray()` UNWRAPPED, so an empty array can only mean "the
enumerator ran and found none"; an enumeration failure propagates to the request loop
(src/AgentEyes.App/RestServer.cs:61-62) which answers **500** with the real message, not 200 with
`[]`. That is what makes AC1's empty-array meaning true, and it is the gate-defect-5 fix. Pinned by
`TheDevicesEndpoint_DoesNotSwallowACameraEnumerationFailure` (IL: no handler region covers the call)
plus `TheDevicesEndpoint_StillEnumeratesCameras` (the presence half), and I fired the first of those
with mutation M3.

### AC2 - CLI lists cameras. PASS

```
CAMERAS: DirectShow video devices (used by 'video' mode --camera)
  "HD Webcam eMeet C960"
  "OBS Virtual Camera"
```

Same names as AC1. The header reads `CAMERAS:` rather than the issue's `Cameras:` - same section,
ASCII, upper-case to match the neighbouring `MONITORS` / `MICROPHONES` headings; I do not read a
capitalisation difference as a criterion miss.

The `(none found)` branch is **not runtime-verified** (no camera-less machine); it exists at
src/AgentEyes.Core/Commands.cs:56 and matches the two sibling device sections at lines 33 and 41.

### AC4 - Manifest records the camera track. PASS

`2026-08-28_111811_video\manifest.json`:

```
"CameraFile": "camera.mp4",
"CameraStartOffsetSeconds": -0.609,
"CameraCapturedSeconds": 21.23,
"CameraTruncated": false,
"Files": [ "recording.mp4", "camera.mp4" ]
```

Keys are PascalCase (`CameraFile`), the issue writes them lower-camel. The file's existing keys are
PascalCase throughout (`VideoFile`, `DurationSeconds`), so this is the manifest's own convention,
not a deviation introduced here.

### AC5 - Status reports the camera. PASS

During the camera recording: `"Camera": "HD Webcam eMeet C960"`. During a recording started with no
camera (the AC11 run): `Camera` is **null** (asserted as `$null -eq $st.Camera` -> True, not as an
empty string). Idle: null.

### AC6 - Preset round-trip. PASS

Driven through UIA against the running app (never force-foregrounded; the editor window was reached
by HWND -> `AutomationElement.FromHandle` because `RootElement.FindAll('Children')` did not surface
the owned modal).

1. Selected preset `Demo Screen Capture With Camera`, opened the editor (`EditPresetButton`).
2. `CameraBox` items: `(None) | HD Webcam eMeet C960 | OBS Virtual Camera` - the "None" plus
   enumerated cameras the scope asks for.
3. Selected `HD Webcam eMeet C960`, clicked `SaveButton`. `presets.json`:
   `Camera = HD Webcam eMeet C960`, `CameraFps = 30`, `Mode = video`.
4. **Killed the app process (0 remaining), restarted it**, reopened the editor:
   `REOPENED_CAMERABOX_SELECTION: [HD Webcam eMeet C960]`.
   The launcher summary line also carries it:
   `Monitor 1 Video 15fps + camera "HD Webcam eMeet C960" 30fps - Mic only ...` (Summary()).
5. Started that preset from the launcher's `RecordButton`, stopped it with the same button.
   New directory `2026-08-28_113013_video` containing BOTH `recording.mp4` (366,771 B, video+audio)
   and `camera.mp4` (15,775,104 B, single `codec_type=video` stream), manifest with
   `"CameraFile": "camera.mp4"`, `"CameraStartOffsetSeconds": -0.807`,
   `"CameraCapturedSeconds": 13.13`, `"CameraTruncated": false`. Durations 12.866667 vs 13.199987 =
   **0.333 s**.

`presets.json` was backed up before this and restored afterwards, so the user's configuration is as
it was found.

### AC8 - Unknown camera fails the start. PASS

CLI, in a scratch working directory:

```
agenteyes video --screen 1 --camera "no-such-device" --seconds 5
[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.
EXITCODE=1
cli recordings dirs: 1 before / 1 after (diff empty)
Videos\AgentEyes:   11 before / 11 after (diff empty)
```

REST:

```
HTTP_STATUS=400
{"error":"no DirectShow camera matches \"no-such-device\". Run 'agenteyes screens' to list
 cameras.","code":"bad_request"}
GET /status -> "State": "idle", "Camera": null
Videos\AgentEyes: diff of full listings before/after is EMPTY
```

### AC10 - Camera lost mid-run does not lose the screen recording. PASS

Started a camera recording over REST, waited 7 s, then enumerated `ffmpeg.exe` via
`Win32_Process` and matched on the command line:

```
FFMPEG PID 14368 :: ... -f dshow ... -i "video=HD Webcam eMeet C960" ... camera.mp4
FFMPEG PID 19132 :: ... -f gdigrab ... -i desktop ... recording.mp4
CAMERA_FFMPEG_MATCHES: 1      (the script THROWS if this is not exactly 1 - broken instrument,
                               never a silent pass)
KILLING_CAMERA_PID: 14368
CAMERA_PROC_GONE: True
STATUS_AFTER_KILL: recording elapsed=11.9 camera=HD Webcam eMeet C960
SCREEN_FFMPEG_STILL_RUNNING: True
```

Then stopped normally:

```
STOP: DurationSeconds 17.29,  STATUS_AFTER_STOP: idle  LastStopFailed=False
manifest: "CameraCapturedSeconds": 6.43,  "CameraTruncated": true
log 11:25:06.365 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during
     the recording (ffmpeg exited on its own) - the screen recording continues; camera.mp4 is
     truncated at 6.4s. See ...\2026-08-28_112457_video\camera.mp4.ffmpeg.log
recording.mp4: ffprobe duration 20.100000; ffmpeg -f null full decode -> no error output, exit 0
```

Screen recording survived, `recording.mp4` is valid and playable, the manifest marks the camera
track truncated with the seconds actually captured, and the WARNING names the camera.

### AC11 - No regression with no camera. PASS

`POST /record/start {"mode":"video","screen":1,"source":"none"}` (no camera):

```
DIR_LISTING: shots, manifest.json, recording.mp4, recording.mp4.ffmpeg.log
CAMERA_MP4_EXISTS: False
MANIFEST_HAS_CameraFile:   False
MANIFEST_HAS_AnyCameraKey: False        (regex '"Camera' - no CameraFile, CameraFps,
                                         CameraStartOffsetSeconds, CameraCapturedSeconds
                                         or CameraTruncated key at all)
manifest "Files": [ "recording.mp4" ]
STATUS_DURING: State=recording  CameraIsNull=True
```

The same two manifest predicates answer **True** on every camera recording above, so they are not
absence-checks that pass by doing nothing.

---

## 6. Method checks

- **Fail-closed (Section 6c).** Two of my instruments were caught failing open and re-run against
  the correct target before being believed: the AC9 camera holder (device name split by PowerShell -
  the guard refused, Section 4.3) and the AC9 CLI run (holder had died - a new directory appeared
  and exit was 0, which I rejected rather than recorded, Section 4.4). The AC10 process-matching
  script throws on any match count other than 1. Directory checks are listing-vs-listing diffs whose
  detection was demonstrated by a run that DID create a directory.
- **Honest limits.** Two clauses could not be exercised on this hardware and are marked as such,
  not silently passed: AC1's empty-array-on-a-camera-less-machine and AC2's `(none found)`. Both are
  judged from code with file:line, and both are stated in this report as code-verified only.
- **The round-3 presence's own limit, restated so it is on the record.** ffmpeg's two open headers
  prove the DirectShow device opened and `camera.mp4` opened for writing. They do NOT prove a frame
  was ENCODED. A device that opens and then stops delivering becomes a MID-RUN loss (decision 4):
  the screen recording survives, a WARNING names the camera, and the manifest marks the track
  truncated with 0.0 s - loud, not silent. AC9 as written is about a BUSY camera, and I observed a
  busy camera abort inside ffmpeg's input open (exit code -5, before either header) on both
  surfaces, so this limit does not touch AC9. It is pinned by
  `Stop_WhenTheCameraOpenedAndThenNeverDeliveredAFrame_StillReportsTheLoss`.
- **CLAUDE.md standards.** ASCII only throughout the new code and log strings. Enterprise logging on
  every path (`StartAndProbe` success line, `FailOpen`, `OnExited`, both `Stop` warnings, the stop
  failure). No fallback programming - the gate-defect-5 catch is gone and the probe fails loudly.
  Try-catch confined to entry points (`Dispose`, the CLI command boundary, the REST request loop);
  the two catches inside `Stop`/`FailOpen` wrap external process calls and re-report rather than
  hide. No UI-thread blocking added; the preset editor still loads cameras via
  `LoadCamerasAsync` off the Loaded handler.
- **Privacy posture.** Unchanged and intact: the camera is opt-in per preset, named on `/status`
  while recording, fails the start loudly when it cannot be opened, and cannot record without the
  always-on indicator. No stealth path added. `docs/cencon/` needs no edit.
- **Housekeeping.** No `nul` files. No stray `ffmpeg.exe` processes left behind after any failure
  path I exercised (checked: 0). `presets.json` restored from backup. Working tree clean; the
  889/889 run is the restored tree.

---

## 7. Criterion summary

| AC | Verdict | Evidence |
|----|---------|----------|
| AC1 devices API lists cameras | PASS (empty-array clause code-verified only) | Section 5 |
| AC2 CLI lists cameras | PASS (`(none found)` clause code-verified only) | Section 5 |
| AC3 two separate files, delta <= 1.0 s | **PASS** - 0.133 s | Section 4.1 |
| AC4 manifest records the camera track | PASS | Section 5 |
| AC5 status reports the camera | PASS | Section 5 |
| AC6 preset round-trip | PASS | Section 5 |
| AC7 CLI parity | PASS - 0.300 s | Section 4.2 |
| AC8 unknown camera fails the start | PASS | Section 5 |
| AC9 busy camera fails the start | **PASS** | Sections 4.3, 4.4 |
| AC10 camera lost mid-run | PASS | Section 5 |
| AC11 no regression with no camera | PASS | Section 5 |
| AC12 gate | PASS - 889/889 | Section 1 |

**12/12 verified. VERIFIED - all acceptance criteria met.**

The five Review Gate defects in `docs/cencon/review/pr30-issue28-gate.md` are all still fixed, each
re-proved by a mutation I ran in this context (Section 3). The AC3 regression that failed round 2 is
closed and pinned by a test that blocks and throws when the fix is reverted.

Handed to the Review Gate: `flow:ready-gate`. **QA did not merge and did not close the issue** (D7).
