# QA report - issue #28, ROUND 4 (PR #32, head `8b9f096`)

**Verdict: PASS - 12/12 acceptance criteria verified.**
Handed to the Review Gate (`flow:ready-gate`). QA does NOT merge (DEVELOPMENT_METHOD.md D7,
which supersedes D5 and the qa-agent skill's step 3a).

- Issue: `thefrederiksen/agenteyes-app` #28
- PR: #32, branch `issue-28-camera-failure-boundaries`, head `8b9f096`
- Round 4 = the extra round the human personally authorised after the 3-strike limit.
- Scope of this round: the three blocking defects in
  `docs/cencon/review/pr32-issue28-gate-round2.md`, PLUS re-verification of the five in
  `docs/cencon/review/pr30-issue28-gate.md` (the same file was restructured), PLUS all 12
  acceptance criteria.
- Live camera preview is issue #29 and is out of scope.

---

## 0. How this round avoided the two traps that produced false greens

**The stale-binary trap (the one the Review Gate hit).** The tray app was running from
`D:\ReposFred\agenteyes-app\src\AgentEyes.App\bin\x64\Release\...\AgentEyesApp.exe`, which
holds locks on that output. Its timestamp was **11:17:29**, and the round-4 commit `8b9f096`
is dated **12:16:42** - the running app was a ROUND-3 binary. Every build, unit-test run,
mutation and REST/UIA interaction below was done against a **separate `git worktree`
(`--detach 8b9f096`)** built from scratch, and the app driven over `127.0.0.1:7882` was the
worktree's `AgentEyesApp.exe` (verified by its command line: it is the worktree path).

**The check-that-fails-open trap.** Two round-1 QA checks failed open (a directory check
watching the wrong path; a grep defeated by binary detection). Every absence-shaped check in
this report is paired with a POSITIVE CONTROL that was shown to fire:

| Check | Positive control that proved the instrument works |
|-------|---------------------------------------------------|
| "no new recording directory" (REST) | the AC3 run, same before/after diff at the same root, reported `NEW DIRS: 2026-08-28_122547_video` |
| "no new recording directory" (CLI) | **the round-1 mistake nearly repeated.** The CLI does NOT write to `%USERPROFILE%\Videos\AgentEyes`; `Commands.NewSessionDir` writes to `<CWD>\recordings\` (pre-existing, not in this diff). A check at the Videos root would have passed for a reason unrelated to the fix. Redone at `<CWD>\recordings` after a positive control that CREATED `2026-08-28_122911_video` there |
| "WARNING naming the camera is in the log" | first searched the same log for the recording's directory name: **81 hits**, in text mode, so the file and the encoding are right |
| "the busy-camera start fails" | the holder process was asserted alive AND asserted to have printed `Input #0, dshow` - i.e. it really held the device. The first holder attempt did NOT (a quoting bug made ffmpeg open `video=HD`), and that attempt was discarded rather than read as proof |
| every unit-test assertion | 12 independent QA mutations, each shown to turn the relevant tests RED - see `qa-mutations-round4.txt` |

**The developer's own `mutation-evidence-round4.txt` was read as context and used for none of
the conclusions below.** That is exactly the class of claim that has already failed twice.

---

## 1. The gate (AC12)

Isolated worktree, PR head `8b9f096`:

```
dotnet build AgentEyes.sln -c Release
  -> Build succeeded.   0 Error(s)   (2 pre-existing xUnit1031 warnings in PostRecordingQueueTests)

dotnet test AgentEyes.sln -c Release
  -> Passed!  - Failed: 0, Passed: 900, Skipped: 0, Total: 900, Duration: 8 s

dotnet test --filter FullyQualifiedName~CameraFailurePathTests
  -> Passed!  - Failed: 0, Passed: 33, Skipped: 0, Total: 33
     (all 33 DISCOVERED and EXECUTED - each is named in the -v n run log)
```

New coverage exists for the video-device parser (`FfmpegDevicesTests.ParseDshowVideo_*`,
including the two empty cases), the camera arg builder, and camera fragment resolution
(found / absent / ambiguous). **AC12 PASS.**

---

## 2. The three ROUND-2 gate defects (the subject of this round)

Each was re-proved by QA's own mutation, not by reading the fix. Full transcript:
`qa-mutations-round4.txt`.

### Defect 1 - a startup timeout could return failure while leaving ffmpeg alive and unreachable

- **Fix in the code.** `FfmpegCameraRecorder` is now built in two phases: `Create()`
  (`FfmpegCameraRecorder.cs:266`) constructs the recorder with NO process behind it, and
  `Open()` (`:322`) starts ffmpeg. Both callers store the recorder BEFORE the process exists -
  `RecordingService.cs:370-372` (`_camera = ...Create(...); _camera.Open();`, and `_camera` is
  what `LiveWriters()` at `:803-811` rolls back) and `Commands.cs:318-335` (`cameraRec = Create(...)`
  inside the `try` whose `finally` at `:457-463` disposes it). `FailOpen` (`:471-516`) now tells
  the two outcomes apart: CONFIRMED GONE -> `_terminated`/`_disposed` set, handle released,
  `UsageException`; STILL RUNNING -> `_terminated` stays FALSE, the handle is **KEPT**, and a
  `CameraStopFailedException` says a live ffmpeg is on the camera.
- **QA mutation M1** (restore the round-2 "terminate and dispose regardless"): 3 tests RED,
  including `Open_WhenTheStalledFfmpegSurvivesTheKill_...` (**KillEndsIt=false**) and
  `Open_WhenTheKillItselfThrows_...` (**KillThrows=true**) - the two startup arms the gate said
  had no test at all.
- **QA mutations M11 / M12** (break the caller ownership on the service side and the CLI side):
  `TheRecordingService_StoresTheCameraBeforeStartingIt` and
  `EveryCallerThatOpensACamera_ConstructsTheRecorderInTheSameMethod` RED.
- **PASS.**

### Defect 2 - Dispose abandoned a live ffmpeg after its one retry failed

- **Fix in the code.** `Dispose()` (`:738-759`) retries `Stop()`, and if `_terminated` is still
  false it LOGS and **returns without disposing** - the handle is released only after the OS
  process is confirmed gone. `Stop()` sets `_terminated = true` only at `:631`, and every path
  that reaches that line has observed `HasExited == true` (either directly at `:596`, or through
  `WaitForExit` returning true at `:611`, or through `KillOrThrow` at `:673-689`, which THROWS
  rather than returning when the process survives).
- **QA mutation M2** (release the handle regardless): 2 tests RED, one of them
  `Stop_AfterAFailedTermination_DisposeKeepsTheProcessReachableInsteadOfAbandoningIt` - **the
  exact test the gate called unfalsifiable.** It now fails against the behaviour it used to
  certify, so the rewrite is real, not a rewritten comment.
- **QA mutation M6** (round-1 defect 2 - ignore the second wait): 5 tests RED.
- **PASS.**

### Defect 3 - the header probe could turn a camera that never delivered a frame into a clean track

- **Fix in the code.** `Stop()` `:633-658`: once the process is confirmed gone it waits for
  ffmpeg's stderr to reach **end of stream** (`ICameraProcess.DrainStderr`,
  `ICameraProcess.cs:66/135` - a `ManualResetEventSlim` set by the reader's null `Data`), and
  then `if (!_wroteOutput && !_lostMidRun)` marks the track LOST with a WARNING naming the
  camera. `_wroteOutput` is set only by a **strictly positive** `time=` position
  (`:530-536`), so `time=N/A` and `time=00:00:00.00` are not evidence of a frame. An
  undrainable stderr is treated as an unfinished read, not as a clean take.
- **QA mutation M3** (disable the verdict): RED includes
  `Stop_WhenALIVECameraOpenedAndNeverDeliveredAFrame_StillReportsTheLoss` - the **LIVE**
  zero-frame camera (process alive, no progress, answers `q`) the gate said was never covered.
- **QA mutations M4** (count a zero tick as output) and **M5** (skip the drain): RED.
- **PASS.**

---

## 3. The five ROUND-1 gate defects - re-proved, not assumed

The file was restructured this round, so prior verification was not carried forward.

| Round-1 defect | Where it is fixed now | QA mutation that fires |
|---|---|---|
| 1. CLI does not own the camera through a failure boundary | `Commands.cs:307-463` - one `try`, `finally { cameraRec?.Dispose(); }` | **M10** -> `TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary` RED |
| 2. Camera stop can report success while ffmpeg is still running | `KillOrThrow` `:673-689` throws unless `WaitForExit` returns true | **M6** -> 5 tests RED |
| 3. The 400 ms probe does not establish that the camera opened | `StartAndProbe` `:381-424` waits for ffmpeg's `Input #0, dshow` AND `Output #0` headers; ANY exit (code 0 included) fails the start | **M7** -> 7 tests RED |
| 4. A real mid-run loss can be recorded as a clean track | `Stop` `:585-590` reads `HasExited` from the PROCESS before the quit and assigns `_lostMidRun` | **M8** -> `Stop_WhenTheCameraDiedWithoutItsExitCallbackDelivered_...` RED |
| 5. Devices API swallows a camera-enumeration failure | `RestServer.cs:408` - `string[] cameras = FfmpegDevices.ListVideo().ToArray();`, no catch | **M9** -> `TheDevicesEndpoint_DoesNotSwallowACameraEnumerationFailure` RED |

---

## 4. The two themes - QA's own audit of the four files

The gate asked for an audit of `FfmpegCameraRecorder.cs`, `ICameraProcess.cs`,
`RecordingService.cs` and `Commands.cs` for other instances of
**(A) "we asked the process to die" read as "it died"** and
**(B) "the device opened" read as "the device is producing video"**. QA re-did that audit
independently. Findings:

- **(A):** every liveness decision now reads `ICameraProcess.HasExited` or the boolean result of
  `WaitForExit`, and `FfmpegCameraProcess` returns the real values (`ICameraProcess.cs:123/133`).
  `Kill()` throwing and `Kill()` returning-but-not-killing are deliberately collapsed and judged
  by the wait that follows (`:673-678`). No path sets `_terminated` or `_disposed` without an
  observed `HasExited == true`. `ICameraProcess.Dispose` is documented as releasing a HANDLE and
  terminating nothing (`ICameraProcess.cs:139-149`), and no caller uses it as a stop.
- **(B):** `_opened` is a claim about the DEVICE only, and `Stop` no longer lets it stand for the
  FILE: `_wroteOutput` (strictly positive `time=`, read from drained stderr) is what decides
  `LostMidRun`. The CLI mirrors it in what it PRINTS - a lost or zero-frame camera gets
  `[warn] ... TRUNCATED`, never `[ok]` (`Commands.cs:440-446`). `RecordingService.SaveManifest`
  reads `camera.LostMidRun` AFTER the stop steps have run (`RecordingStopSequence.Run` stops the
  writers first, then saves), so the manifest sees the zero-frame verdict.
- **Sanity check that this section is not itself an absence claim:** the audit is paired with the
  12 mutations, each of which had to be shown firing. Where the audit is a source scan
  (`TheRecordingService_StoresTheCameraBeforeStartingIt`) the test itself says so and is paired
  with an IL check that sees the call wherever it is spelled.

### Observation for the Review Gate (NOT a defect against any acceptance criterion)

Stated plainly rather than left out. After a stop in which ffmpeg survived BOTH `Stop()` and
the `Dispose()` retry, `RecordingService.Stop()` has already cleared `_camera` (`:493`) and the
local `camera` goes out of scope when `RecordingStopSequence.Run` returns, so **nothing in the
process still references that recorder** - the handle it correctly kept is unreachable in
practice. The failure is not hidden: it is collected into `RecordingStopReport`, stored in
`_lastStopFailure` (`:593`) and reported on `GET /status` as `LastStopFailed` /
`LastStopError`, and a subsequent camera start fails loudly with "already in use by another
application" (proved at AC9 below). So neither theme (A) nor (B) is present - the code never
claims the camera stopped. What IS inaccurate is the comment at
`FfmpegCameraRecorder.cs:736`, which says the recorder "can be called again ... by whoever
reads the failure off `/status`": there is no API that reaches it. QA is flagging the comment
as an overclaim and the missing post-sequence retry as a design gap, both for the gate to
weigh; neither violates an acceptance criterion of #28.

---

## 5. The 12 acceptance criteria

All runtime evidence below is from the **worktree build**, real hardware
(`HD Webcam eMeet C960`, plus an `OBS Virtual Camera`), 2026-08-28.

### AC1 - Devices API lists cameras: **PASS**

```
GET http://127.0.0.1:7882/devices  -> HTTP 200
cameras count = 2
  [HD Webcam eMeet C960]
  [OBS Virtual Camera]
```
Expected: 200 with the exact DirectShow names. Actual: as above.
*Stated limit:* the "machine with no camera returns 200 and `[]`" clause cannot be produced on
this hardware. It is covered by `FfmpegDevicesTests.ParseDshowVideo_EmptyInput_ReturnsEmpty` and
`ParseDshowVideo_ListingWithNoVideoSection_ReturnsEmpty`, and - the part that MATTERS - the
empty array can now only mean "no cameras", because mutation **M9** proved there is no catch
turning a broken enumerator into `[]`.

### AC2 - CLI lists cameras: **PASS**

```
CAMERAS: DirectShow video devices (used by 'video' mode --camera)
  "HD Webcam eMeet C960"
  "OBS Virtual Camera"
```
Same names as AC1, from the same enumerator. The `(none found)` branch exists at
`Commands.cs:56` and is kept DISTINCT from the failure branch `(unavailable: ...)` at `:60`.

### AC3 - Two separate files: **PASS**

`POST /record/start {"mode":"video","screen":1,"source":"none","camera":"HD Webcam eMeet C960"}`
-> 200, then `POST /record/stop` -> 200.
Directory `C:\Users\soren\Videos\AgentEyes\2026-08-28_122547_video`:

```
camera.mp4                11,743,350
camera.mp4.ffmpeg.log          6,174
manifest.json                  1,563
recording.mp4                 83,105
recording.mp4.ffmpeg.log       6,408
thumb.jpg                      5,446
shots/

ffprobe recording.mp4 duration = 12.966667
ffprobe camera.mp4    duration = 13.199987
                        DELTA  =  0.233320 s      LIMIT 1.0 s  -> PASS

camera.mp4 streams: index=0  codec_name=h264  codec_type=video      (exactly ONE, no audio)

ffmpeg -v error -i recording.mp4 -f null -   -> exit 0   (decodes clean)
ffmpeg -v error -i camera.mp4    -f null -   -> exit 0   (decodes clean)
```
**AC3 and AC9 hold simultaneously** - see AC9. Round 2's regression on this criterion was
2.366656 s; round 3 measured 0.333 s; this round measures 0.233 s (REST), 0.267 s (CLI, AC7)
and 0.367 s (launcher, AC6).

### AC4 - Manifest records the camera track: **PASS**

```
"CameraFile": "camera.mp4",
"CameraStartOffsetSeconds": -0.547,     (numeric; negative because the camera opens first)
"CameraCapturedSeconds": 13.13,
"CameraTruncated": false,
"Files": [ "recording.mp4", "camera.mp4" ]
```

### AC5 - Status reports the camera: **PASS**

During the AC3 recording:
```
"State": "recording",  "Camera": "HD Webcam eMeet C960"
```
During a recording started with NO camera (the AC11 run):
```
"State": "recording",  "Camera": null      (asserted -eq $null -> True)
```

### AC6 - Preset round-trip: **PASS** (driven by QA over UIA, no synthesized input)

1. Preset `Demo Screen Capture With Camera` opened in the preset editor
   (`Edit active preset` -> window `Edit preset`). `CameraBox` listed exactly
   `(None)`, `HD Webcam eMeet C960`, `OBS Virtual Camera` - "None" plus the AC1 cameras.
2. `HD Webcam eMeet C960` selected via `SelectionItemPattern`; `Save` invoked. `presets.json`:
   ```
   'Demo Screen Capture With Camera' -> Camera= 'HD Webcam eMeet C960'  CameraFps= 30
   (the other five presets -> Camera= None)
   ```
3. **App killed and restarted.** `presets.json` still contains the camera name.
   Reopened editor, read back over UIA:
   ```
   CameraBox selection = [HD Webcam eMeet C960]
   preset summary      = Monitor 1 Video 15fps + camera "HD Webcam eMeet C960" 30fps - Mic only ...
   ```
4. Started from the **launcher** (`REC` -> `STOP` over UIA), directory
   `2026-08-28_123602_video`:
   ```
   camera.mp4 11,833,340   recording.mp4 305,624   (+ audio/transcript artifacts)
   recording.mp4 = 14.866667   camera.mp4 = 15.233318   delta = 0.366651   -> PASS
   camera.mp4 streams: index=0 codec_type=video
   CameraFile=camera.mp4  CameraTruncated=False  CameraCapturedSeconds=15.16
   Files=recording.mp4,camera.mp4,recording.original.mp4
   ```
`presets.json` and `config.json` were backed up before this and restored afterwards
(verified: all six presets back to `Camera = None`).

### AC7 - CLI parity: **PASS**

`agenteyes video --screen 1 --camera "eMeet" --seconds 10` -> exit 0.
```
[ok] recording.mp4 (00m12s, 82 KB), 0 marker(s)
[ok] camera.mp4 (13.1s, 10.4 MB), video only

ffprobe recording.mp4 = 12.900000    camera.mp4 = 13.166654    DELTA = 0.266654  -> PASS
camera.mp4 streams: index=0 codec_type=video
manifest: CameraFile=camera.mp4  CameraStartOffsetSeconds=-0.583
          CameraCapturedSeconds=13.09  CameraTruncated=false
          Files=[recording.mp4, camera.mp4]
```
Same two-file shape and same manifest fields as AC3/AC4.

### AC8 - Unknown camera fails the start: **PASS**

CLI (checked at the CLI's REAL root, `<CWD>\recordings`, after a positive control that created
a directory there):
```
[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.
exit = 1
dirs before = 1   dirs after = 1   NEW = []
```
REST (raw response):
```
HTTP/1.1 400 Bad Request
{ "error": "no DirectShow camera matches \"no-such-device\". Run 'agenteyes screens' to list cameras.",
  "code": "bad_request" }
GET /status -> State: idle, Camera: null, Dir: null
NEW DIRS under %USERPROFILE%\Videos\AgentEyes = []   (same instrument that reported the AC3 dir)
```

### AC9 - Busy camera fails the start: **PASS**

Holder: a separate `ffmpeg -f dshow -i "video=HD Webcam eMeet C960"`, asserted alive AND
asserted to have printed `Input #0, dshow` (i.e. it really held the device) before either
attempt.
```
REST : HTTP 400
  { "error": "the camera \"HD Webcam eMeet C960\" could not be opened (ffmpeg exited with
     code -5). Likely cause: the camera \"HD Webcam eMeet C960\" is already in use by another
     application.", "code": "bad_request" }
  GET /status -> State: idle       NEW DIRS = []       holder STILL alive after = True

CLI  : same message, exit = 1,  NEW DIRS under <CWD>\recordings = []
       ffmpeg PIDs before = [26636]   after = [26636]   -> NO orphaned ffmpeg, only the holder
```
It never silently recorded screen-only. **AC3 (0.233 s delta) and AC9 both hold on the same
build** - that pairing is what round 2 broke.

### AC10 - Camera lost mid-run does not lose the screen recording: **PASS**

Camera ffmpeg identified by command line (`dshow` + `camera.mp4`), PID 42388, killed mid-run:
```
after the kill:  /status -> State: recording, ElapsedSeconds 11.64, Camera "HD Webcam eMeet C960"
                 (the SCREEN recording kept running)
stop -> 200, DurationSeconds 15.68

recording.mp4 duration = 18.566667 ;  ffmpeg -f null - exit 0  -> valid and playable
manifest: CameraTruncated = True     CameraCapturedSeconds = 4.93     CameraFile = camera.mp4

app log (control: the same log matched the recording's dir name 81 times, text mode):
  12:30:04.675 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during
    the recording (ffmpeg exited on its own) - the screen recording continues; camera.mp4 is
    truncated at 4.9s. See ...\2026-08-28_122956_video\camera.mp4.ffmpeg.log
  12:30:15.969 [WARN] stop: the camera "HD Webcam eMeet C960" was lost during this recording -
    camera.mp4 covers 4.9s of a 15.7s session; the screen recording is unaffected
```
(`camera.mp4` itself is not playable after a hard kill - `moov atom not found`. AC10 does not
require it to be; it requires the manifest to mark the track truncated with the seconds
actually captured, which it does: 4.93 s.)

### AC11 - No regression with no camera: **PASS**

`POST /record/start {"mode":"video","screen":1,"source":"none"}` (no `camera`):
```
directory 2026-08-28_123052_video:
  manifest.json  recording.mp4  recording.mp4.ffmpeg.log  thumb.jpg  shots/
camera.mp4 exists: False
manifest raw text contains "CameraFile": False
manifest raw text contains "Camera"    : False      (no camera key of any kind)
Files: [ "recording.mp4" ]
```
Positive control for that absence: the AC3 manifest, read the same way, DID contain them.

### AC12 - Gate: **PASS** - see section 1.

---

## 6. Method checks

- CLAUDE.md standards: enterprise logging on every camera decision; no fallbacks (the round-1
  Devices fallback is gone and mutation M9 keeps it gone); try/catch at entry points only
  (`Dispose`, the CLI `finally`, the per-step stop boundary); ASCII only in all new strings,
  comments and log lines (checked across the diff).
- Privacy posture (visible / controllable) unchanged: the camera is opt-in per preset / per
  request, and a camera that cannot be opened FAILS the start rather than recording anything.
- CenCon: the developer's handoff note (`handoff-round4.md`) is present and links the work; the
  proof transport is this file on the PR branch.
- Environment left as found: `presets.json` / `config.json` restored, the QA worktree removed,
  no orphaned `ffmpeg` processes, no `%TEMP%\AgentEyes-crash.log`, `git status` clean.

## 7. What this report cannot see

- No machine without a camera was available, so AC1/AC2's "no cameras" wording was verified from
  unit tests plus the (mutation-proved) absence of a swallowing catch, not from hardware.
- The unit tests drive a fake `ICameraProcess`. They prove the RECORDER's decisions on the
  timeout / failed-kill / zero-frame paths; they prove nothing about ffmpeg itself. That half is
  what section 5 (AC3, AC7, AC9, AC10) exercises against the real webcam.
- `TheRecordingService_StoresTheCameraBeforeStartingIt` is a SOURCE scan (the method's body is
  split into lambdas, so IL ordering is not reportable). A source scan is defeated by an alias or
  a helper; it is paired with the IL caller-ownership check and with the behavioural tests.
- The AC10 kill was a `Stop-Process` on the camera ffmpeg PID, which is what the criterion asks
  for. A camera that STALLS while its process stays alive is covered only by the unit test
  `Stop_WhenALIVECameraOpenedAndNeverDeliveredAFrame_StillReportsTheLoss`; no hardware fault was
  available to reproduce it end to end.
