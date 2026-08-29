REJECT

# PR #40 / issue #35 - Review Gate round 1

## Basis of review

Reviewed PR #40 at head `3b0f21c6f2fe195e7281d45e51c9fb1730c6b733` against
`origin/main` at `e57d828578f499df95a9d66d19523436a222b590`. I read issue #35 and
all comments, including the scope change and the later ruling. This verdict applies AC3 as the
winner and applies AC2 as reworded: controls must be present, reachable and fully visible within
their own tabs. It does not require all settings to be visible simultaneously.

## Blocking defects

### 1. Closing during camera enumeration can start a new preview after the window is gone

`PresetEditor` starts `LoadCamerasAsync` from its `Loaded` handler without cancellation
(`src/AgentEyes.App/PresetEditor.xaml.cs:114-117`). Every close route disposes the controller in
`Window.Closed` (`src/AgentEyes.App/PresetEditor.xaml.cs:104-112`), but the asynchronous continuation
later selects the saved camera and unconditionally calls `UpdateCameraPreview`
(`src/AgentEyes.App/PresetEditor.xaml.cs:177-232`). `CameraPreviewController.Dispose` unregisters the
controller but does not mark it disposed (`src/AgentEyes.App/CameraPreviewController.cs:268-275`),
and `Select` accepts calls after disposal and starts another session
(`src/AgentEyes.App/CameraPreviewController.cs:114-145`).

Concrete failure scenario: open a Video preset with a saved camera on the remembered Camera tab,
then press Save, Save as, Cancel, Esc, or the window X while the picker still says "Loading
cameras...". The window closes and unregisters its holder. When DirectShow enumeration completes,
the closed window's continuation starts the preview anyway. There is no later close event and the
controller is no longer registered with the arbiter, so ffmpeg can hold the exclusive camera with
no visible window and no recording-start release callback. The next recording can fail to open the
camera, directly violating AC9.

The compiled-head probe demonstrates the missing disposed-state guard rather than inferring it
from source:

```text
POST_DISPOSE_HOLDER_COUNT=0
POST_DISPOSE_EXPECTED_BASE_COUNT=0
POST_DISPOSE_SESSION_HELD=True
POST_DISPOSE_CONTROLLER_HOLDS=True
```

### 2. Closing unregisters the holder before it releases the camera

`CameraPreviewController.Dispose` calls `CameraDeviceArbiter.Unregister` before `StopSession`
(`src/AgentEyes.App/CameraPreviewController.cs:268-272`). Recording start snapshots only the holders
that are still registered (`src/AgentEyes.Core/Video/CameraDeviceArbiter.cs:78-90`). A real preview
stop can take up to three seconds, so this ordering creates a window in which the camera is still
held but the arbiter has no callback capable of releasing it.

Concrete failure scenario: `POST /record/start` arrives while `Window.Closed` is waiting for the
preview ffmpeg to stop. The recording thread snapshots zero holders and opens the same exclusive
DirectShow device before the close path has freed it. The recording start then fails as camera in
use. The compiled-head probe blocks the session stop at exactly this point and records:

```text
DISPOSE_GAP_RELEASED_COUNT=0
DISPOSE_GAP_HOLDER_COUNT=0
DISPOSE_GAP_SESSION_HELD=True
```

### 3. An in-flight open times out by claiming release while the camera is still held

`StopSession` retires and clears `_opening`, waits at most 5000 ms, and only logs when that task has
not finished (`src/AgentEyes.App/CameraPreviewController.cs:227-242`). With no `_session` published
yet it then returns (`src/AgentEyes.App/CameraPreviewController.cs:244-250`). `Stop` announces
`Stopped`, and the arbiter callback returns `true` to the recorder
(`src/AgentEyes.App/CameraPreviewController.cs:152-170`). This contradicts the method's promise that
it does not return until the device is free.

Concrete failure scenario: Windows or the device graph stalls while a preview open is acquiring the
camera, and a recording starts during that open. The release call already exceeds AC9's two-second
budget, then reports success and lets the recorder open while the first operation still owns the
device. The compiled-head probe supplies a blocking session factory and records the exact result:

```text
BLOCKED_OPEN_RELEASED_COUNT=1
BLOCKED_OPEN_RELEASE_MS=5009
BLOCKED_OPEN_SESSION_HELD_AFTER_RETURN=True
BLOCKED_OPEN_CONTROLLER_HOLDS_AFTER_RETURN=False
```

The normal control for the same probe released one session and observed both the session and the
controller no longer holding it, so the bad result is not an empty or constant instrument.

### 4. The real ffmpeg stop path returns normally after a failed kill and discards the handle

The session contract requires `Stop` not to return until the camera is actually free
(`src/AgentEyes.Core/Video/CameraPreviewSession.cs:13-20`). The real implementation catches and logs
a `Process.Kill` failure, and also only logs when the process is still alive after 3000 ms
(`src/AgentEyes.Core/Video/FfmpegCameraPreview.cs:209-224`). It then unconditionally logs that the
camera was released, and `Dispose` disposes the `Process` wrapper
(`src/AgentEyes.Core/Video/FfmpegCameraPreview.cs:231-238`). The controller has already removed the
session from `_session` before making those calls
(`src/AgentEyes.App/CameraPreviewController.cs:244-255`). Disposing a `Process` wrapper does not
terminate a surviving operating-system process.

Concrete failure scenario: ffmpeg ignores the kill for more than three seconds, or `Kill` throws.
The preview process remains alive and holds the webcam, but the controller has forgotten it and the
arbiter continues into `FfmpegCameraRecorder.Create`. DirectShow rejects the recorder's open and
`POST /record/start` fails. On a close route, the surviving preview also becomes an orphan with no
remaining process handle in AgentEyes. This is another independent AC9 failure even after the first
three defects are fixed.

## Checks that did not find a blocking defect

- Reorder: the pre-reorder feature tree changes 43 files. Derived blob comparison against the
  QA-passed tree found 40 byte-identical files and exactly three differences: `Config.cs`,
  `FfmpegArgs.cs`, and `FfmpegCameraRecorder.cs`. For each file, the ordered feature-added lines are
  identical to the old tree (9/9, 56/56, and 7/7). `Config.cs` differs by omitting #33 HUD state;
  `FfmpegArgs.cs` differs by omitting #33's in-recording MJPEG output; and the recorder is current
  main plus only the seven-line arbiter release at `FfmpegCameraRecorder.cs:556`. Those differences
  are legitimate consequences of dropping #33.
- Issue #28 preservation: `CameraTerminationRecord.cs`, `CameraObservation.cs`, `Manifest.cs`,
  `CameraTrackRecord.cs`, `RecordingService.cs`, `CameraFailurePathTests.cs`, and
  `CameraTrackTests.cs` have identical main/head blobs. The recorder has only the release hunk named
  above. The explicit termination history, single stop-kind derivation, and three-state
  `CameraComplete` design are intact.
- AC4: a parsed enumeration derived from main found 38 pre-existing `x:Name` controls; head has 48.
  Every one of the 38 is present with the same XML control type. Removing `NameBox` in memory made
  the same check report exactly one missing control, so the zero-missing result is not an empty
  instrument.
- AC1/AC2/AC3/AC6/AC8: a separate compiled WPF probe at the default 1000x760 measured Capture
  `285.7/527.9`, Audio `311.8/527.9`, and Camera `515.6/527.9`, all with
  `ComputedVerticalScrollBarVisibility=Collapsed`. Every ruled named control was inside its tab
  viewport. The Camera pane measured 480x360. At height 600 the Camera tab reported
  `Visible; ScrollableHeight=147.8`; Capture and Audio still fit without scrolling. The screenshots
  were also inspected and show the intended three-tab layout and live image.
- AC5: all 38 old named controls retain their behavioral XAML attributes; the only new behavioral
  binding is `CameraBox.SelectionChanged=Camera_Changed`. Save, Save as, Cancel, the region handlers,
  `ReadInto`, and the presets store are unchanged from main; `Mode_Changed` retains its prior mode UI
  update and adds only preview release/restart behavior.
- AC10: the compiled probe restored `tab=2`, `size=1120x700`, and `position=140,90` exactly.
- Gate: from a detached worktree at the reviewed head, `dotnet restore` completed first. The x64
  Release build reported `Build succeeded`, 4 warnings, 0 errors. The full x64 test run reported
  `Failed: 0, Passed: 983, Skipped: 0, Total: 983`; the two issue-specific test classes separately
  reported 32/32. The four blocking paths above are absent from those tests. No ffmpeg process was
  left after the review probes.
