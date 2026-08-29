# Issue #29 - [Presets] Live camera preview in the preset editor

**Developer Agent handoff to QA.** Branch `issue-29-camera-preview`.
I believe this is finished.

---

## What was built, and the one thing it is really about

A 320x240 / 10 fps live preview pane sits under the camera picker in the preset editor.
That part is easy. The reason #29 exists separately from #28 is the other half: on Windows a
DirectShow camera is **exclusive**, so a preview that is still holding the device when a recording
starts would make that recording fail outright (issue #28, decision 3 - a camera that cannot be
opened fails the whole start). Releasing is therefore the feature; the picture is the by-product.

Every way out of the preview lands on **one** release path:

| Way out | Route in the code |
|---------|-------------------|
| a different camera picked | `CameraPreviewController.Select` stops the previous session first |
| "(None)" picked | `Select(null)` |
| the preset leaves Video mode | `PresetEditor.UpdateCameraPreview` -> `Stop(...)` |
| Save / Save as / Cancel / the window X / Esc | all five reach `Window.Closed` -> `CameraPreviewController.Dispose` |
| a recording opens a camera | `CameraDeviceArbiter.ReleaseForRecording` -> the controller |

The last row is the interesting one. Rather than teaching the launcher, the tray and
`POST /record/start` each to remember to stop a preview, the release is hooked at the ONE place a
camera is opened for recording - `FfmpegCameraRecorder.Start`. Every recording path reaches the
device through there, so none of them can forget. The call is synchronous and never touches the UI
thread, so a recording start cannot be blocked behind a busy dialog.

### Files

| File | What changed |
|------|--------------|
| `src/AgentEyes.Core/Video/CameraDeviceArbiter.cs` | NEW. "A recording wants this camera - let go." |
| `src/AgentEyes.Core/Video/CameraPreviewSession.cs` | NEW. `ICameraPreviewSession` + factory delegate (this is what makes the lifecycle testable with no camera). |
| `src/AgentEyes.Core/Video/FfmpegCameraPreview.cs` | NEW. ffmpeg -> raw BGR24 frames on stdout; `Stop()` KILLS (no file to finalize, so speed of release is all that matters). |
| `src/AgentEyes.Core/Video/FfmpegArgs.cs` | `CameraPreview(...)` args builder (pure). |
| `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs` | `Start` now calls `CameraDeviceArbiter.ReleaseForRecording` before opening the device. |
| `src/AgentEyes.App/CameraPreviewController.cs` | NEW. The lifecycle state machine: Stopped / Starting / Running / Failed. |
| `src/AgentEyes.App/PresetEditor.xaml(.cs)` | The pane, the picker wiring, `Window.Closed` release, frame rendering. |
| `tests/AgentEyes.Tests/CameraPreviewTests.cs` | NEW. 24 tests. |

### Two implementation decisions worth knowing before you review

1. **Raw BGR24 frames of a fixed size, not an encoded stream.** The reader finds frame boundaries by
   COUNTING bytes (`320*240*3 = 230400`), which is why the args force an exact, aspect-preserved,
   padded box. A variable-size or containered stream would let the reader chop the pipe into
   plausible-looking garbage.
2. **The frame rate is limited on the OUTPUT (`-r`), never requested from the device
   (`-framerate`).** A dshow input refuses to open when the camera does not offer the requested
   mode, which would turn a working camera into a preview that never starts.

### Deviations / additions beyond the literal AC list

- **Leaving Video mode also stops the preview.** Not in the issue's stop list, but #28 assumption A1
  makes the camera a Video-mode setting: a preset that will not record the camera has no business
  holding the exclusive device open to show it. Flagging it because it is behaviour the AC list does
  not mention.
- **A recording on a DIFFERENT camera also releases the preview.** Deliberate, and the reason is in
  the code comment: the two mistakes are not symmetric. Releasing a preview that did not need
  releasing costs a preview the user was losing anyway; keeping one because two device names were
  compared and judged different costs them the recording.

### Stated limit (please check this rather than take it)

`CameraDeviceArbiter` coordinates holders **inside the app process**. A recording started from the
CLI (`agenteyes video --camera ...`, a separate process) while a preview is running in the tray app
CANNOT be preempted, and fails loudly with `the camera "X" is already in use by another
application`. That is the honest outcome - a named failure, never a silent screen-only recording -
but it is a limit, not full coverage. AC7's proof path (`POST /record/start` + `GET /status`) is
in-process and is covered.

---

## CenCon impact

No drift. No component-map or privacy-posture change: the preview is local-only, shows the user
their own camera in a dialog they opened, holds the device for as long as that pane is on screen and
no longer, and records nothing to disk. `docs/cencon/` needs no update.

---

## The gate I ran (I ran it - not the human)

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  0 Warning(s)  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 891, Total: 891, Duration: 8 s
```

(891, not the 288 CLAUDE.md still quotes - the suite has grown. 24 of them are new here.)

### Every new check was run against KNOWN-BAD code and shown to FIRE

Four mutations, each applied to the product, tested, and reverted. Full transcript:
`mutation-evidence.txt`.

| Mutation applied to the product | What fired |
|---------------------------------|------------|
| 1. `FfmpegCameraRecorder.Start` no longer asks holders to release | `OpeningACameraForRecording_AsksEveryHolderToReleaseIt` |
| 2. the closing editor no longer disposes the preview | `TheClosingPresetEditor_ReleasesTheCamera` |
| 3. changing the selection no longer stops the previous session | `Select_ADifferentCamera_ReleasesThePreviousOne`, `Select_None_ReleasesTheCameraBeforeItReturns`, `FramesAndFailuresFromAReleasedSession_AreIgnored` |
| 4. the preview ignores a recording that wants the camera | `ARecordingOpeningTheCamera_ReleasesTheRunningPreviewFirst`, `ARecordingOnADifferentCamera_StillReleasesThePreview` |

All four ran the full 24 tests, so nothing quietly disappeared from the run.

### What the unit tests CANNOT see

They drive the lifecycle with a FAKE session, so they prove that every exit path reaches a released
session on a machine with no camera. They do NOT prove that ffmpeg produces frames, that WPF paints
them, or that Windows hands the physical device back. Two of the tests are IL reads over the
compiled product (`CameraDeviceArbiter::ReleaseForRecording` inside `FfmpegCameraRecorder::Start`;
`CameraPreviewController::Dispose` inside `PresetEditor`) - those prove the wiring EXISTS, not that
the release happens BEFORE the open (an ordered IL read across a body carrying lambdas has no
meaning). The running-app checks below are what close those gaps.

---

## Headless probe I already ran against the real camera

Not the running-app proof (that is yours) - but the riskiest class, `FfmpegCameraPreview`, has no
unit coverage, so I exercised it directly with no app, no window and no audio. Source:
`preview-probe.cs.txt` (drop it in a `net8.0-windows10.0.19041.0` / x64 console project as
`Program.cs` and `dotnet run`; it reflects into the built `agenteyes.dll` because the class is
internal). Machine has two cameras: `HD Webcam eMeet C960`, `OBS Virtual Camera`.

**Good state:**

```
ffmpeg processes before: 0
[open 1] frames=24 frameBytes=230400 firstFrameMs=721 stopMs=384 failures=0
[open 1] 163 of 232 sampled bytes differ between the first and last frame
[open 2, immediately after open 1 was stopped] frames=15 frameBytes=230400 firstFrameMs=630 stopMs=374 failures=0
[open 2, immediately after open 1 was stopped] 167 of 232 sampled bytes differ between the first and last frame
ffmpeg processes after: 0
RESULT: PASS - frames arrive, frames change, the device is released, nothing left behind
```

First frame at 721 ms; frames genuinely CHANGE (163/232 sampled bytes differ - a frozen pane would
show 0); the device was free for a second open the instant the first was stopped; nothing left over.

**Known-bad state (the same probe, camera held by another ffmpeg - i.e. AC6's condition):**

```
ffmpeg processes before: 1
[open 1] frames=0 ... failures=1 The camera "HD Webcam eMeet C960" could not be opened: the camera "HD Webcam eMeet C960" is already in use by another application.
RESULT: FAIL
```

So the probe can fail, and the AC6 message names the device.

---

## How to verify each acceptance criterion

**Setup.** Build and run the tray app from the branch:
`src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe` (the `x64` segment
is not optional). Open the editor from the launcher: "Edit preset", or Manage presets -> Edit.
Cameras on this machine: `HD Webcam eMeet C960`, `OBS Virtual Camera`.

Reminders that apply throughout: the focus-free layers are the **REST Control API**
(`http://127.0.0.1:7882`), **UIA**, and **PrintWindow**. Do NOT force-foreground the app and
synthesize input without warning the human. The recording HUD is capture-excluded, so recording
state is asserted via `/status` or UIA, never a screen grab.

UIA names added by this issue: `CameraPreviewPanel` (the Border), `CameraPreviewImage` (the Image),
`CameraPreviewStatus` (the TextBlock). Existing ones you will need: `NameBox`, `CameraBox`,
`ModeShot`, `ModeVideo`, `SaveButton`, `SaveAsButton`, `CancelButton`.

| AC | How to exercise it |
|----|--------------------|
| **AC1 live frames** | Open the editor, pick `HD Webcam eMeet C960`. PrintWindow the editor twice, at least 2 s apart, MOVING something in front of the camera in between. Compare the pixels inside the `CameraPreviewImage` rect - they must DIFFER. Identical or blank = FAIL. (`gui-smoke.ps1` has the PrintWindow pattern.) |
| **AC2 dialog never blocked** | Save a preset with a camera. Reopen the editor and, immediately, UIA-read `NameBox.Value` and toggle `ModeVideo` - both must work before any frame renders, and `CameraPreviewStatus` must read `Starting camera...` at that moment. The status is set in the constructor for exactly this reason, before the (slow) camera enumeration. |
| **AC3 "(None)" releases** | With the preview running, UIA-select `(None)` in `CameraBox`. Within 2 s: `POST /record/start {"mode":"video","camera":"HD Webcam eMeet C960"}` -> 200, then `GET /status` -> `"State":"recording"`. Stop it. |
| **AC4 all five close paths** | For EACH of Save, Save as, Cancel, the window X, and Esc: open the editor with the camera selected, wait for frames, close by that route, then within 2 s `POST /record/start` with that camera and read `/status`. Five results, all must be 200 + `recording`. (Esc reaches `Cancel_Click` through `IsCancel`; the X does not - both end at `Window.Closed`, which is where the release lives, so please confirm the X specifically.) |
| **AC5 no orphan** | `Get-Process ffmpeg` before opening the editor and 5 s after closing it. The counts must match. My probe showed 0 -> 0. |
| **AC6 busy camera** | Hold the camera from another process first: `ffmpeg -f dshow -i "video=HD Webcam eMeet C960" -t 60 -f null -`. Then select it in the editor. Within 5 s `CameraPreviewStatus` must read a message containing the device NAME (expected: `The camera "HD Webcam eMeet C960" could not be opened: the camera "HD Webcam eMeet C960" is already in use by another application.`), the text must be red, UIA must still be able to toggle `ModeShot`/`ModeVideo` and click `SaveButton`, and `%LOCALAPPDATA%\AgentEyes\` log must carry `[FfmpegCameraPreview] the camera ... could not be opened for preview`. |
| **AC7 recording preempts** | With the preview RUNNING and the editor still open, `POST /record/start` with that same camera. Expect 200 and `/status` -> `recording`; `CameraPreviewStatus` should read `Preview stopped - the camera is in use by a recording.` and the image must be cleared. The log records `[CameraDeviceArbiter] ReleaseForRecording: ... 1 released`. |
| **AC8 UI stays responsive** | While the preview runs, UIA `ModeShot` -> read back -> `ModeVideo` -> read back, timing it: under 1 s. Frames are coalesced (at most one queued for the UI thread at a time, `DispatcherPriority.Background`), which is what this is testing. |
| **AC9 no regression on "(None)"** | Open + close the editor with the camera on `(None)`: `Get-Process ffmpeg` delta must be zero, and no `[FfmpegCameraPreview] Start` line in the log. Then round-trip every preset field (name, note, monitor, region, mic, source, denoise/gate/level, volumes, mode, fps, camera, cameraFps) through Save and reopen - unchanged. |
| **AC10 gate** | `dotnet build AgentEyes.sln -c Release` and `dotnet test AgentEyes.sln -c Release`. Run them yourself; do not trust the numbers above. |

### Smokes worth scoping

- **`gui-smoke.ps1`** - yes. This change is entirely in the preset editor's UI surface, and AC1,
  AC2, AC8 and AC9 are UIA/PrintWindow questions.
- **`api-smoke.ps1`** - yes, lightly. AC3, AC4 and AC7 all turn on `POST /record/start` +
  `GET /status` behaving normally while the editor is open, and nothing in this change touches the
  recording engine other than the one arbiter call at the top of `FfmpegCameraRecorder.Start` -
  which is worth one camera recording to confirm still works end to end.
- `agenteyes selftest` / `run-all.ps1` - not obviously needed; nothing here touches audio,
  transcription, the manifest or the packaging pipeline.

### Where to look first if something is wrong

`%LOCALAPPDATA%\AgentEyes\` log, tags `[CameraPreviewController]`, `[FfmpegCameraPreview]`,
`[CameraDeviceArbiter]`, `[PresetEditor]`. Preview start, first frame (with its latency), stop (with
the release time in ms and the frame count), and every failure are all logged.
