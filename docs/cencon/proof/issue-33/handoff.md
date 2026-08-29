# Issue #33 - Developer Agent handoff

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

Branch: `issue-33-hud-live-preview`, branched from `issue-28-camera-failure-boundaries` at `e485561`
(NOT from `main` - `main` still carries the rejected `eee17b4`, and the camera pipeline this builds on
lives on #28's branch). When #28 merges, this rebases onto `main`.

I believe this is finished.

---

## 1. The one decision everything else follows from

While a recording runs, ffmpeg holds the DirectShow camera EXCLUSIVELY. A preview cannot open the
device a second time (assumption C1). So preview frames come **from the recording's own ffmpeg**, as a
second, small output.

The obvious way to do that is wrong, and it was MEASURED wrong rather than argued wrong. Giving ffmpeg
the preview as an `image2` **file** output and then removing the preview directory mid-run
(2026-08-28):

```
[out#1/image2] Error muxing a packet
[out#1/image2] Task finished with error code: -2 (No such file or directory)
-> recording.mp4 ffprobe duration 5.133333   (the run was -t 15)
```

**A preview failure truncated the recording by ten seconds.** That is exactly the failure AC10
forbids, so the file output was abandoned.

What shipped instead: the preview output writes an **MJPEG stream to ffmpeg's stdout**, and AgentEyes
drains that pipe **unconditionally**. The drain is the one thing that is never allowed to stop - an
anonymous pipe nobody reads fills, and a full pipe blocks the ffmpeg writing the recording. Everything
downstream of the drain (parsing frames, publishing them to a file, the HUD reading that file) is
allowed to fail, and its failure costs a picture.

```
ffmpeg (recording)  --stdout MJPEG-->  PreviewTap.Drain (always)  --> newest whole frame
      |                                        |                          |
      v                                        |                    (only while the panel is showing)
 recording.mp4 / camera.mp4                    |                          v
                                               |     %LOCALAPPDATA%\AgentEyes\preview\{screen|camera}.jpg
                                               |                          |
                                               +--------------------------+--> HUD panel
```

---

## 2. What changed, file by file

### New - `src/AgentEyes.Core/Preview/`

| File | What it is |
|------|-----------|
| `JpegFrame.cs` | Is this buffer a WHOLE JPEG? Asked as a PRESENCE (both markers), never as "decoding did not throw" - a truncated JPEG decodes to a half-drawn picture without complaining. |
| `MjpegFramer.cs` | Cuts the byte STREAM into whole frames. Pure, no threads/files/ffmpeg, so every boundary case is a unit test rather than a race. Memory is bounded and overruns are COUNTED. |
| `PreviewTap.cs` | The frame source for one track: drains ffmpeg's stdout unconditionally, publishes the newest whole frame by writing a temp file and renaming it over the target. |
| `PreviewFrameFile.cs` | Reads a published frame. Opens with `FileShare.ReadWrite \| Delete` so the reader cannot break the writer's rename. |
| `PreviewOverlay.cs` | `PreviewMode` / `PreviewCorner` and their wire spellings. |
| `PreviewPaths.cs` | `%LOCALAPPDATA%\AgentEyes\preview` - deliberately OUTSIDE the recording directory. |

### New - `src/AgentEyes.App/`

| File | What it is |
|------|-----------|
| `HudPreviewState.cs` | Every decision the panel makes, with no WPF in it: layers, control availability, what reaches the manifest, staleness. This is what the tests drive. |
| `PreviewFrameFeed.cs` | Reads and DECODES frames on its own thread and hands the window frozen bitmaps. No file I/O or JPEG decode ever touches the UI thread (coding standard 1). |

### Changed

- `Video/FfmpegArgs.cs` - `PreviewOutput()` plus an opt-in `previewStream` parameter on
  `VideoCapture` and `CameraCapture`. Default `false` produces **byte-for-byte the command line these
  built before**.
- `Video/FfmpegRecorder.cs`, `Video/ICameraProcess.cs`, `Video/FfmpegCameraRecorder.cs` - an optional
  tap; when present it takes stdout instead of `BeginOutputReadLine()`. **This is the only #28 code I
  touched** - see section 5.
- `RecordingService.cs` - `PreviewArmed` (opt-in), the taps' lifetime, `SetPreviewPublishing`,
  `SetPreviewOverlayCorner`, and eight new `/status` fields.
- `Manifest.cs` - `PreviewOverlayCorner`.
- `App/HudWindow.cs` - the panel, the toggle, mode and corner controls, resize, persistence.
- `App/Config.cs` - `HudWidth`, `HudHeight`, `HudPreviewVisible`, `HudPreviewMode`, `HudPreviewCorner`.
- `App/App.xaml.cs` - carries the persisted preview choice into the session.
- `tests/ManifestWriterIlTests.cs` - the five new file-write call sites are PINNED with their
  justification. That guard caught them immediately; it works.

---

## 3. The one design consequence QA must know before testing

**A preview feed is created when a recording STARTS, or not at all.** ffmpeg's outputs are fixed when
the process starts, and restarting ffmpeg to add a monitor would interrupt the thing being monitored.

So `RecordingService.PreviewArmed` (fed from the persisted "show preview" choice) decides whether this
recording carries a feed:

- **Armed** - the command line gains the preview output; showing, hiding, re-moding and re-cornering
  the panel are pure UI, instant, and never touch ffmpeg (AC8).
- **Not armed** - there is no second output at all, so the recording is byte-for-byte the recording it
  was before this feature (AC11). Turning the preview on during such a recording shows the panel with
  an honest message: *"Live preview starts with your NEXT recording..."*, and persists the choice.

This is a deliberate trade and it is what buys AC11 and a genuine AC9 control run. Consequence for the
human: on the very FIRST recording after installing, clicking "Show preview" shows that message rather
than a picture; from the next recording on it is live immediately. **If the human would rather pay the
cost on every recording to make that first click live, that is a Product decision, not a bug - flag it
rather than treat it as a defect.**

---

## 4. Acceptance criteria - what was built and how to test it

The focus-free layers are **REST** (`http://127.0.0.1:7882`), **UIA**, and **PrintWindow**. Never
force-foreground the app and synthesise input. The HUD is `WDA_EXCLUDEFROMCAPTURE`, so a full-screen
grab CANNOT see it - assert HUD state via UIA or `/status`, and capture the HUD itself with
PrintWindow.

New `/status` fields, all of which exist so the preview is verifiable without a screenshot:

| Field | Meaning |
|-------|---------|
| `PreviewArmed` | the NEXT recording will carry a feed |
| `PreviewAvailable` | THIS recording carries one |
| `PreviewPublishing` | frames are being written out right now (the panel is showing) |
| `PreviewScreenFrame` / `PreviewCameraFrame` | the file the HUD reads |
| `PreviewScreenFramesRead` / `PreviewCameraFramesRead` | whole frames taken off each pipe - a count that CLIMBS is a live tap; a count stuck at zero is a tap that has never seen a frame |
| `PreviewFailed` | a tap is currently failing to publish |
| `PreviewOverlayCorner` | the corner framed so far this recording |

**Setup for every runtime check below:** set `"HudPreviewVisible": true` in
`%LOCALAPPDATA%\AgentEyes\config.json` and restart the app (or toggle the preview on once during a
throwaway recording and stop it) so the next recording is armed. Confirm with `/status`
-> `PreviewArmed: true`.

### AC1 - toggle exists and defaults off

Built: a `Button` in the HUD row whose **UI Automation name is exactly `Show preview`** in both states
(the visible label flips between "Show preview" / "Hide preview"; `AutomationProperties.HelpText` is
`hidden` / `showing`). `Config.HudPreviewVisible` defaults `false`.

Test: with a fresh `config.json`, start a recording; find the HUD by title `Recording HUD`, find the
button by name `Show preview`, read its HelpText (`hidden`) - and confirm `/status`
-> `PreviewPublishing: false`. Invoke it: HelpText `showing`, `PreviewPublishing: true`, HUD bounding
rectangle grows. Invoke again: back to `hidden` / `false`.
Unit tests: `HudPreviewStateTests.Visible_FreshConfig_IsOff`, `ToggleVisible_ShowsThenHides`,
`Config_Defaults_AreAHiddenScreenPreviewInTheBottomRight`.

### AC2 - screen preview is live

Built: the tap publishes `%LOCALAPPDATA%\AgentEyes\preview\screen.jpg` at 10 fps while the panel shows.

Test, two independent ways (the second is stronger and needs no screenshot):
1. Two PrintWindow captures of the HUD at least 2s apart while the screen changes - the preview region
   must DIFFER.
2. Read `preview\screen.jpg` twice, 2s apart, while the screen changes - the bytes must DIFFER, and
   `/status`'s `PreviewScreenFramesRead` must CLIMB by roughly 20. A static file or a frozen count
   FAILS; an absent file is a broken preview, not a clean run.

### AC3 - camera preview is live AND the device is opened once

Built: the camera preview is a second output on the SAME `FfmpegCameraRecorder` process
(`FfmpegArgs.CameraCapture(..., previewStream: true)`), never a second device open.

Test: record with a camera and `both` (or `camera`) mode showing. Then:
- liveness as in AC2, on `preview\camera.jpg` and `PreviewCameraFramesRead`;
- **one process holds the camera**: `Get-Process ffmpeg` while recording, and confirm the camera
  ffmpeg's command line (from `manifest.json`'s `FfmpegCommand` for screen, and the process command
  line for camera) contains exactly ONE `-i video=...`. Unit test
  `FfmpegArgsTests.CameraCapture_WithAPreview_StillOpensTheDeviceExactlyOnce` pins the argument side
  (one `-i`, one `dshow`, one `video=`);
- **camera.mp4 is unharmed**: ffprobe `recording.mp4` and `camera.mp4`, durations within 1.0s (#28
  AC3), and `manifest.json` records `CameraComplete: "yes"`.

### AC4 - both mode composites in the selected corner

Built: WPF layering - the screen `Image` fills the panel, the camera sits in a `Border` sized to 30% of
the panel width and aligned per corner. Corner buttons carry UIA names `Preview corner top-left`,
`...top-right`, `...bottom-left`, `...bottom-right`; the mode buttons are `Preview mode screen`,
`...camera`, `...both`.

Test: in `both` mode, invoke each corner button and PrintWindow the HUD - four captures, four distinct
inset positions. The `HUD preview status` TextBlock also reads e.g. `both top-left | live`, which is a
focus-free assertion of the same thing.

### AC5 - the corner reaches the manifest

Built: `HudPreviewState.ManifestCorner` is non-null only when the panel is showing AND the mode is
`both` AND there is a camera. It is pushed to `RecordingService.SetPreviewOverlayCorner` on every
change, and written at the stop into `Manifest.PreviewOverlayCorner`.

Test: record with `both` + a chosen corner, stop, read `manifest.json` -> `"PreviewOverlayCorner":
"top-left"` (or whichever). Then record with the preview hidden, or in `screen` mode, and confirm the
property is **absent from the file entirely** (not present-and-null).
Unit tests: `PreviewManifestTests` (4 corners round-trip, absence, an old manifest, and an
`ManifestStore.Update` that does not disturb #28's camera fields), `HudPreviewStateTests.ManifestCorner_*`.

### AC6 - no mirror tunnel, no HUD in the output

Built: nothing relaxed. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` is untouched in
`ApplyWindowStyles` (assumption C5).

Test: record with the preview ON in `screen` or `both` mode over changing content, stop, then extract
frames from `recording.mp4` and inspect the region where the HUD sat - neither the HUD nor its preview
may appear in any frame. (Note `MQS_HUD_CAPTURABLE=1` deliberately opts OUT of the exclusion for demos;
it must be unset for this check.)

### AC7 - HUD is resizable and persists

Built: `ResizeMode.CanResize`; `SizeToContent` becomes `Manual` when the panel opens (auto-sizing
overrides any width/height, so it is what made the old HUD unresizable). A visible grip `Thumb` with
UIA name `HUD resize` drags the size, and `Config.HudWidth/HudHeight` are saved on close - only while
manually sized, so the auto-sized pill's dimensions never come back as a preview panel.

Test: with the preview showing, use the UIA **TransformPattern** on the HUD window (`ResizeMode` is
what enables it) to resize to >= 3x the default width; read the bounding rectangle back and confirm the
preview scaled to fit. Stop, start a new recording, and confirm the HUD returns at that size and screen
position.

### AC8 - toggling mid-recording is safe

Built: showing/hiding/re-moding/re-cornering only flips flags and visibilities. No ffmpeg is started,
stopped or reconfigured; `PreviewTap.Publishing` is a bool.

Test: during one armed recording, turn the preview on, change mode, change all four corners, turn it
off, turn it on again, then stop. `recording.mp4` and `camera.mp4` both play, durations within #28's
1.0s limit, `CameraComplete: "yes"`.

### AC9 - bounded cost

Built and MEASURED. Committed: `docs/cencon/proof/issue-33/preview-cost-check.py` and its recorded run
`preview-cost-check.txt`. 5 rounds of 30s at 1920x1080/30fps, control vs preview, 2026-08-28:

```
CPU seconds  control [28.88, 27.09, 28.98, 26.50, 20.03]  median 27.09
             preview [28.48, 26.36, 31.23, 25.42, 24.11]  median 26.36
drops        control [40, 23, 2, 5, 53]  median 23
             preview [23, 11, 20, 8, 38]  median 20
duration     every run 30.000s (one 29.967s); preview frames 300/300/300/300/300 (10 fps x 30s)
```

**Honest reading of that table.** gdigrab drop counts on a live desktop are dominated by machine load -
identical CONTROL runs varied from 2 to 53 - so drop count alone cannot resolve an effect this small in
either direction, and the script reports it rather than judging on it. CPU time can, and it shows the
preview inside the control's own spread. The filter chain is why: `fps=10` FIRST so ten frames a second
are scaled instead of thirty, `flags=neighbor` point sampling for the quarter-size downscale, and
`yuvj420p` into the JPEG encoder. The first shape (scale then decimate, 4:4:4) measured **19/27/37
drops against a control's 4/1/5** and was rejected; the unit test
`FfmpegArgsTests.PreviewOutput_GoesToStdoutAndNeverToAFile` pins the ordering so it cannot drift back.

QA should re-run this on its own machine (`python docs/cencon/proof/issue-33/preview-cost-check.py 5`)
and additionally do the 60-second recording AC9 asks for, through the app, reporting both numbers.

### AC10 - preview failure never harms the recording

Built, in three layers:
1. The drain is unconditional (`PreviewTap.Drain`), including after a framing or publishing failure -
   it drops to read-and-discard rather than stopping.
2. Publishing failures are caught, logged as `WARNING`, surfaced on `/status` as `PreviewFailed`, and
   never rethrown into the drain.
3. The panel judges FRAME AGE, not "did something throw". No frame for 2 seconds - **including no frame
   ever** - replaces the picture with *"Preview unavailable - no frames from the recorder. The
   recording is unaffected."* The last frame is never left on screen.

Test (this is the deliberate, inducible failure): start an armed recording with the preview showing,
confirm frames are flowing, then **delete `%LOCALAPPDATA%\AgentEyes\preview\` (the whole directory)**.
Within ~2s the panel must show the message; the app log must carry
`[PreviewTap] Publish FAILED: ...`; `/status` must show `PreviewFailed: true` while
`PreviewScreenFramesRead` **keeps climbing** (that climbing count is the proof the pipe is still being
drained, which is what protects the recording). Then stop: both files valid, durations within #28's
limit, `CameraComplete` unaffected.
Unit tests: `PreviewTapTests.Pump_WhenPublishingFails_KeepsDrainingAndReportsTheFailure`,
`Pump_WhileNotPublishing_STILL_DRAINS_ANDWritesNothing`, `Pump_WhenPublishingRecovers_*`,
`HudPreviewStateTests.IsStale_NoFrameHasEverArrived_IsStale`.

### AC11 - no regression

Built: with `PreviewArmed` false there is no tap, so no second output, so the same arguments, the same
files and the same manifest. The **CLI never arms one**, so `agenteyes video` is untouched entirely.
The preview frame files live outside the recording directory, and `PreviewOverlayCorner` is null and
therefore not serialised.

Test: record with the preview never enabled. `manifest.json` must contain **no `PreviewOverlayCorner`**
and its `FfmpegCommand` must contain **no `pipe:1` and no `mjpeg`**. The recording directory must
contain exactly the files #28 produces. Unit tests:
`FfmpegArgsTests.VideoCapture_WithoutAPreview_IsExactlyWhatItWasBefore`,
`CameraCapture_WithoutAPreview_IsExactlyWhatItWasBefore`,
`PreviewManifestTests.Manifest_WithoutAnOverlayCorner_DoesNotWriteTheFieldAtAll`.

### AC12 - gate

Built from an **isolated worktree** (`git worktree`), not the checkout the running tray app can lock -
so the green is not a stale binary.

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.   0 Warning(s)   0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 1011, Skipped: 0, Total: 1011
```

1011 tests, up from 926 on #28's head - **85 new**.

---

## 5. What I touched of #28, and why

Only the stdout wiring, in three places, and only because the camera frames CANNOT come from anywhere
else while ffmpeg holds the device:

| File | Change |
|------|--------|
| `Video/FfmpegRecorder.cs` | optional `PreviewTap` parameter; when present, hand it `StandardOutput.BaseStream` instead of calling `BeginOutputReadLine()`. Those two readers are mutually exclusive - wiring both leaves the preview reading a closed pipe. |
| `Video/ICameraProcess.cs` (`FfmpegCameraProcess`) | same, via an optional constructor parameter. `ICameraProcess` itself is UNCHANGED, so #28's fakes and its failure-path tests are untouched. |
| `Video/FfmpegCameraRecorder.cs` | `Create` takes the optional tap and passes `previewStream:` to the args. |

Everything #28 owns is otherwise untouched: `Open()`, `Stop()`, the probe, `CameraStopKind`,
`CameraStderrComplete`, three-state `CameraComplete`, `StrandedCameraOwner`. All 926 of its tests still
pass. Two specific compatibilities I checked deliberately:

- `IsOutputOpenReport` matches `"Output #0"`. With a second output ffmpeg prints `Output #0` (camera.mp4)
  and `Output #1` (the pipe), in that order - verified against real ffmpeg 9.0.1 - so the open probe is
  unaffected. The args tests pin that camera.mp4 stays output #0.
- The preview taps are **not** start/stop steps. A tap failure must never be collected into
  `RecordingStopReport` or reach the "this stop failed" surface. They are disposed in the stop's
  `finally`, AFTER the writers, because a tap's pump only ends when ffmpeg closes its pipe.

---

## 6. Every new check was shown to FAIL first

Committed: `mutation-evidence.py` (repo-relative, re-runnable) and its recorded run
`mutation-evidence.txt`. 18 mutations, each breaking one decision the way a careless implementation
would; **all 18 FIRED**. The load-bearing ones:

| # | Known-bad implementation | Caught by |
|---|--------------------------|-----------|
| M4 | the tap stops draining the pipe while the preview is hidden | `Pump_WhileNotPublishing_STILL_DRAINS_ANDWritesNothing` |
| M5 | a publish failure stops the drain | `Pump_WhenPublishingFails_KeepsDrainingAndReportsTheFailure` |
| M9 | `IsStale` treats "no frame has ever arrived" as fine | `IsStale_NoFrameHasEverArrived_IsStale` |
| M13 | the preview is a FILE output on ffmpeg (the shape measured to truncate a recording) | `PreviewOutput_GoesToStdoutAndNeverToAFile` |
| M14 | the preview output is added to every recording | `VideoCapture_WithoutAPreview_IsExactlyWhatItWasBefore` |
| M15 | scale-then-decimate (the shape measured at 19-37 drops) | `PreviewOutput_GoesToStdoutAndNeverToAFile` |
| M18 | the manifest writes a default corner instead of leaving the field absent | `Manifest_WithoutAnOverlayCorner_DoesNotWriteTheFieldAtAll` |

---

## 7. What the tests CANNOT see - stated, not implied

1. **No unit test covers `RecordingService`'s arming decision.** `StartVideo` needs real monitors and
   real ffmpeg, and I did not add a seam for it. The runtime check that closes it is AC11's:
   `manifest.json`'s `FfmpegCommand` must carry no `pipe:1` for an unarmed recording, and `/status`'s
   `PreviewArmed` / `PreviewAvailable` say what the service decided.
2. **No test drives the WPF window.** `HudWindow` layout, the resize grip, the corner alignment and the
   UIA names are verified only by QA's running-app checks. What IS tested is every decision behind
   them (`HudPreviewState`), which is where the logic errors would live.
3. **Nothing here has been run against a real camera by me.** This machine's camera was not exercised;
   AC3 and AC8's camera arms are QA's. The camera argument SHAPE is unit-tested; the device behaviour
   is not.
4. **`preview-cost-check.py` exercises the ffmpeg side only** - the argument list, the pipe, and the
   frame boundaries. It does not run the C# tap, the HUD, or the camera.
5. **The MJPEG framer finds frame ends by scanning for the EOI marker.** Byte stuffing makes that safe
   inside entropy data, but an EOI inside a JPEG-encoded EXIF thumbnail would cut a frame short.
   ffmpeg's mjpeg encoder writes no such thumbnail; if one ever appeared, the frame would fail
   `JpegFrame.IsComplete` at the consumer and be dropped rather than shown wrong.

---

## Smokes worth scoping

- **api-smoke** - `/status` gained eight fields; the existing routes are untouched.
- **gui-smoke** - the HUD gained controls. Its UIA names are listed in section 4.
- A **camera** recording is the one area I could not exercise; please cover AC3 and AC8 there.

## CenCon impact

No drift. The component map is unchanged - this adds a monitoring surface inside an existing window and
a second output on an existing process. The privacy posture (**visible, controllable**) is
**strengthened**: the person can now see what is being recorded while it is recorded, the preview is
off by default, its frames never leave the machine, they are written outside the recording, and they
are deleted when the preview is hidden or the recording ends. `WDA_EXCLUDEFROMCAPTURE` is untouched.
