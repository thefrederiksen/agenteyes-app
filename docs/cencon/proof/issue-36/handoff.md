# Issue #36 - Developer Agent handoff

**[Capture] Circular camera overlay - set up a circle around the face, shown in the preview and
recorded as edit metadata**

Branch: `issue-36-circular-camera-overlay`, cut from `issue-35-preset-editor-two-columns` (ffd3970),
NOT from `main` - `main` still carries a rejected camera commit. The working stack is
`issue-28-camera-failure-boundaries` -> `issue-33-hud-live-preview` -> `issue-35-preset-editor-two-columns`
-> this.

I believe this is finished.

---

## The gate

| Check | Result |
|-------|--------|
| `dotnet build AgentEyes.sln -c Release` | `Build succeeded.` **0 Error(s), 2 Warning(s)** |
| `dotnet test AgentEyes.sln -c Release` | **Failed: 0, Passed: 1138, Skipped: 0** |
| Same commands on the base (ffd3970), before any change | `Build succeeded.` 0 Error(s), **the same 2 Warning(s)**; Failed: 0, Passed: **1043** |

Correcting an overclaim I made in the first draft of this note: it said 0 warnings, which was an
incremental-build artifact - the test project had not been recompiled. Forced clean, the build
reports **2 warnings, and they are the SAME two the base reports**, both pre-existing and both in a
file this change does not touch:

```
tests\AgentEyes.Tests\PostRecordingQueueTests.cs(309,42): warning xUnit1031: Test methods should not
  use blocking task operations, as they can cause deadlocks.
tests\AgentEyes.Tests\PostRecordingQueueTests.cs(314,46): warning xUnit1031: (the same)
```

This change contributes zero warnings of its own.

Built and run from an ISOLATED WORKTREE (`D:\ReposFred\agenteyes-wt-36`), not the shared checkout,
so the running tray app could not hand back a stale Release binary. Output is under
`bin\x64\Release\net8.0-windows10.0.19041.0\`.

95 checks are new. **Every one of them has been run against a deliberately broken product and shown
to FIRE** - see `mutation-evidence.md` beside this note: 20 mutations, each removing exactly one
thing this feature claims, each caught. None survived. On this feature a check that cannot fail is
treated as a defect, and the history of this repo is why.

---

## What was built

### The design decision, in one line

The circle is a **framing choice, not a crop** (assumption E1). `camera.mp4` keeps recording the full
rectangular frame at its normal resolution. The circle applies to the PREVIEW, and its geometry goes
into `manifest.json` as edit metadata so a later edit can reproduce the framing - **and move it**,
because no pixels were thrown away. Nothing in this change can reach the camera recorder;
`CameraOverlayUiTests.TheOverlay_NeverReachesTheCameraRecorder` asserts that as a source fact.

### New

| File | What it is |
|------|-----------|
| `src/AgentEyes.Core/Preview/CameraOverlay.cs` | `CameraOverlayShape` (Circle default / Rectangle), `CameraOverlayCircle` (normalised centre + diameter, clamping, viewbox/pixel-bounds), `CameraOverlaySettings` (shape + circle + corner + inset), `OverlayFit.Contain` |
| `src/AgentEyes.Core/Video/CameraFrameSize.cs` | The camera's OWN frame size, parsed out of ffmpeg's `Input #0` block. Null = not observed, never a guess |
| `src/AgentEyes.App/HudOverlayConfig.cs` | The one bridge: preset `--Seed()->` config `--Read()->` HUD `--Write()->` config. One-way; nothing writes back to a preset |

### Changed

- `Manifest.cs` - `PreviewOverlayShape`, `PreviewOverlayCircle`, `PreviewOverlayInset` beside the
  existing `PreviewOverlayCorner`. All null-by-default, so `WhenWritingNull` keeps them out of a
  preview-off recording's file entirely.
- `RecordingService.cs` - `SetPreviewOverlayCorner(string?)` becomes
  `SetPreviewOverlay(CameraOverlaySettings?)`; the framing is copied and canonicalised on the way in,
  taken under the state lock at the stop, and copied into the on-disk manifest by the same
  read-modify-write. `/status` gains `PreviewOverlayShape`.
- `CapturePreset.cs` - `Overlay` (a `CameraOverlaySettings`), deep-cloned. `PresetCapture.Start` now
  takes the `Config` and seeds the HUD's framing from the preset - it is the single funnel every
  recording start goes through.
- `Config.cs` - `HudPreviewShape`, `HudPreviewCircleCentreX/Y`, `HudPreviewCircleDiameter`,
  `HudPreviewInsetFraction`. Flat scalars; `HudPreviewCorner` stays the one home for the corner.
- `HudPreviewState.cs` - takes the framing object instead of a bare corner; exposes `Shape`,
  `Circle`, `InsetFraction`, `Framing`, `ManifestOverlay`. `ManifestCorner` is now derived from
  `ManifestOverlay`, so the two can never disagree.
- `HudWindow.cs` - an `Ellipse` beside the existing `Image`. In circle mode the host is SQUARE with
  **no background and no border**, and the frame is painted through an `ImageBrush` whose relative
  viewbox is the chosen circle's bounding square - a CROP, not a mask over a shrunken whole frame.
  The inset width now comes from the framing (the `InsetWidthFraction = 0.30` constant is gone).
- `PresetEditor.xaml(.cs)` - the setup UI, beside the live pane from #35. Every pre-existing
  `x:Name` is untouched (`PresetEditorLayoutTests` still passes).
- `FfmpegCameraPreview` / `ICameraPreviewSession` / `CameraPreviewController` - expose `SourceSize`.

### The trap this feature walks into, and how it is closed

The preset editor's pane shows a **fixed 320x240 (4:3) buffer that ffmpeg PADS**
(`scale=...:force_original_aspect_ratio=decrease,pad=...`). The HUD's tap does not pad
(`scale=-2:270`). So on any camera that is not 4:3 the two disagree, and a circle drawn as if the
picture filled the pane lands in the wrong place - convincingly.

The camera on this machine is **1920x1080**. Captured live and committed:
`ffmpeg-camera-preview-stderr.txt`, trimmed into the test fixture
`tests/AgentEyes.Tests/fixtures/camera/emeet-c960-preview-stderr.txt` and asserted by
`CameraFrameSizeRealCaptureTests`: the picture occupies **320x180 inside the 320x240 pane, with a
30-pixel black bar above and below**. Without the `CameraFrameSize` parse the circle would have been
a third of its own height out of place on the very first camera anyone tried.

When ffmpeg has not reported the size, `PreviewContentRect()` returns **null** and the editor SAYS SO
("Waiting for the camera picture... nothing is assumed") rather than drawing a circle it cannot
place. That is the fail-closed reading, and mutation M9 proves the check fires if it is replaced with
an assumption.

---

## Acceptance criteria, and how QA exercises each

Everything below drives the app through the **REST Control API** (`http://127.0.0.1:7882`) and
**UI Automation**, never by force-foregrounding the window and synthesising input.

**Before anything: the HUD is capture-excluded** (`WDA_EXCLUDEFROMCAPTURE`). No screen grab can show
it. Its state is asserted through UIA - the automation element **"HUD preview status"** now reads
`"<mode> <shape> <corner> | live"`, e.g. `both circle bottom-right | live`. That string is the
assertable surface for AC1/AC3/AC6/AC7.

**Also before anything - a blocker I could not clear from the developer seat, stated rather than
worked around:** the human's INSTALLED AgentEyes (v1.6.1, `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe`,
PID 51324 at the time of writing) was running and idle. It owns loopback port 7882 and would fight
this build for the exclusive DirectShow camera. **QA must stop that instance before running any of
the below**, then launch this branch's binary from
`src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe`. I deliberately did
not start a second instance: it would have collided with the human's live session, and the
running-app proof is QA's under DEVELOPMENT_METHOD 3.2 / 6b in any case.

### AC1 - circle is the default overlay shape

- Code: `PreviewNames.Shape(null|unknown) == Circle`; `CameraOverlaySettings.Shape` initialises to
  `"circle"`; `HudWindow.LayOutInset` gives the circle host `Brushes.Transparent` and
  `BorderThickness 0`, so the bounding-box corners are genuinely empty.
- QA: fresh config (`%LOCALAPPDATA%\AgentEyes\config.json` moved aside) -> record with a camera and
  the preview in `both` -> read UIA "HUD preview status": expect `both circle bottom-right | live`.
  For the screenshot, set `MQS_HUD_CAPTURABLE=1` before launching (that opts the HUD out of the
  capture exclusion - it exists for exactly this) and grab the HUD window with PrintWindow; the
  screen picture must be visible at the four corners of the inset's bounding box.
- **Empty result = broken instrument:** a status line that does not contain the word `circle` at all
  means the HUD never got a camera, not that the shape is right.

### AC2 - the circle is positionable and sizeable against a live image

- The Camera tab now carries, beside the 480x360 live pane: `OverlayShapeCircle` /
  `OverlayShapeRectangle`, `CircleXSlider` (0..1), `CircleYSlider` (0..1), `CircleSizeSlider`
  (0.1..1), `OverlayCornerBox`, `InsetSizeSlider` (0.15..0.6), `OverlayResetButton`, plus the live
  adorner (`CameraOverlayAdorner` + `OverlayMaskPath` + `OverlayOutlinePath`) drawn INSIDE
  `CameraPreviewPanel`, over the picture. The circle can also be placed by clicking or dragging on
  the picture; the sliders stay the authoritative value.
- QA: open the preset editor, Camera tab, pick the camera, wait for frames. Drive
  `CircleXSlider`/`CircleYSlider`/`CircleSizeSlider` through UIA's **RangeValue** pattern and
  PrintWindow the editor after each. Three screenshots: default; centre moved (e.g. X=0.25, Y=0.65);
  diameter changed (e.g. 0.25 then 0.9). Each must show a visibly different part of the frame inside
  the circle.
- Read every screenshot. A blank pane is a STOP-and-diagnose: it means the camera never opened, and
  the adorner will correctly be showing nothing.

### AC3 - the choice reaches the recording

- Chain: editor Save -> `preset.Overlay` -> `PresetCapture.Start` -> `HudOverlayConfig.Seed(cfg, p)`
  -> `HudOverlayConfig.Read(cfg)` -> `HudPreviewState` -> `HudWindow`. Unit-covered end to end minus
  the window by `CameraOverlaySyncTests.HudPreviewState_BuiltFromASeededConfig_ShowsThePresetsFraming`.
- QA: save a preset with a distinctive circle (well off centre, small), start a recording from the
  LAUNCHER (Record button - the HUD is shown from there), and put the editor screenshot beside the
  HUD screenshot. Same part of the face inside the circle.

### AC4 - geometry written to the manifest

- QA: after that recording, `manifest.json` in `%USERPROFILE%\Videos\AgentEyes\<dir>\` must carry:
  `"PreviewOverlayShape": "circle"`, `"PreviewOverlayCorner": "<corner>"`,
  `"PreviewOverlayCircle": { "CentreX": .., "CentreY": .., "Diameter": .. }`,
  `"PreviewOverlayInset": ..` - and the circle values must match the preset's, to 6 decimals.
- The numbers are normalised fractions of the camera frame (assumption E2), so they are the same on a
  1080p and a 720p camera.

### AC5 - camera.mp4 is NOT cropped (the load-bearing one)

- QA: three recordings of the same camera - (a) circle overlay, (b) rectangle overlay, (c) preview
  off entirely - then:
  `ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 camera.mp4`
  All three must print **identical** width,height. Quote the three lines; "they looked the same" is
  not the evidence.
- Why it holds: no overlay value reaches `FfmpegArgs` or `FfmpegCameraRecorder` at all. Asserted as a
  source fact by `TheOverlay_NeverReachesTheCameraRecorder`, but ffprobe is the real answer.

### AC6 - rectangle still available

- QA: same preset with `OverlayShapeRectangle` checked -> record -> UIA status reads
  `both rectangle <corner> | live`, and the HUD screenshot shows the bordered box on black that #33
  shipped, auto-heighted to the frame's aspect.

### AC7 - preset and HUD stay in sync

- Two halves. The preset REACHES the HUD (AC3 above). The HUD does NOT reach back: `HudWindow.cs`
  contains no reference to `PresetStore` or `CapturePreset` at all, and `SavePreviewChoices` writes
  only `HudOverlayConfig.Write(_cfg, ...)` + `_cfg.Save()`.
- QA: start a recording from a preset saved with corner `top-right`; click the HUD's `BL` chip
  mid-recording; assert (i) the UIA status now says `bottom-left` immediately, (ii) `manifest.json`
  records `bottom-left` (the framing the person settled on), (iii) `presets.json` STILL says
  `"Corner": "top-right"` for that preset. Quote the presets.json line - the absence claim is proved
  by the presence of the original value.

### AC8 - preview failure is still subordinate

- Nothing in this change touches the tap lifecycle, the writer sequence, or the stop. The circle is
  drawn from the same `PreviewSnapshot` #33 already published; `PaintCameraFrame(null)` clears both
  shapes and returns.
- QA: repeat #33's AC10 with the circle active - delete/lock the preview frame directory mid-run, or
  kill the tap. The recording must keep running and complete normally: `recording.mp4` and
  `camera.mp4` both valid, `"CameraComplete": "yes"` on a clean stop, `/status` `LastStopFailed`
  false.

### AC9 - bounded cost

- The mask is a frozen `ImageBrush` on an `Ellipse`, painted on the same 10 fps UI publish that #33
  already did; it adds no ffmpeg filter, no second decode, and no work on any recording thread. The
  recording's own command line is unchanged by the shape.
- QA: two 60-second recordings on the same machine - overlay ON (circle, `both` mode) and preview OFF
  - and report the `drop=` figure from each run's ffmpeg progress (the same measurement #33's AC9
  used; `Manifest.FfmpegCommand` and the log carry it), plus the camera track duration against the
  session duration. **Report both numbers.** The overlay-on run must drop no more than the control.

### AC10 - no regression with the overlay unused

- With the preview off there is no `ManifestOverlay`, so all four fields are null and
  `WhenWritingNull` keeps them out of the file. Covered by
  `CameraOverlayManifestTests.Manifest_WithNoOverlayAtAll_WritesNoneOfTheOverlayFields` (asserted
  against the serialized TEXT, not against a null property) and by the four-case theory
  `ManifestOverlay_WhenNothingWasFramed_IsNull`.
- QA: diff a preview-off recording's `manifest.json` against a pre-#36 one of the same mode. No new
  keys.

### AC11 - the gate

- See the table at the top. New geometry tests: `CameraOverlayGeometryTests` (normalisation,
  clamping to the frame, the crop, the inset range, the wire spellings, the two nested fits).
  Manifest round-trip: `CameraOverlayManifestTests`. Stop-copy guard: `CameraOverlayStopCopyTests`.
  Built from an isolated worktree.

---

## Suggested smoke scope

- `gui-smoke.ps1` - the preset editor changed shape again. It drives the dialog by `x:Name`, and
  every pre-existing name is intact, but this is the run that proves it.
- `api-smoke.ps1` - `/status` gained `PreviewOverlayShape`; nothing was removed or renamed.
- A real camera recording is worth one run for AC4/AC5/AC9. `agenteyes selftest` is not implicated.

---

## Limits I am stating rather than leaving for QA to discover

1. **No running-app proof from me.** Reason above (the human's live instance owns the port and the
   camera). Everything in this note that needs the running app is written as an instruction, not as
   a claim.
2. **The REST `/record/start` path does not seed the overlay.** `RestServer` holds no `Config`, and
   the HUD is only ever shown from `MainWindow.Record_Click`, so an API-started recording has no HUD
   to frame and writes no overlay geometry. That is consistent with AC10 but it is a real asymmetry:
   if the HUD is ever shown for API-started recordings, `PresetCapture.Start` is the place to route
   it through.
3. **`HudOverlayConfig.Seed` does not write config.json.** It mutates the single shared `Config`
   instance, which the HUD reads directly - deliberate, to keep file I/O off the record-start path.
   The framing is persisted the next time the HUD saves a choice. A restart between those two moments
   loses the seed until the next recording start re-seeds it.
4. **`CameraFrameSize` reads ffmpeg's text.** It is the camera's own report and it is tested against a
   real capture from this machine, but it is one sample plus synthetic cases - not a survey of
   webcams. A camera whose ffmpeg banner has a shape none of those cover would report "not observed",
   and the editor would say so rather than mis-place the circle. That is the failure mode by design.
5. **`CameraOverlayStopCopyTests` reads the SOURCE of one method.** A copy performed by a helper, by
   reflection, or under a different spelling would be reported as missing when it is not. That
   direction is safe (it fails and a human looks); the direction that matters - a field copied
   NOWHERE - is closed, because such a field cannot appear in that method's text.
6. **The source-level UI checks cannot see rendering.** They prove the code asks for a transparent
   square host and a viewbox crop. Whether the screen shows through the corners, and whether the
   circle is round, are the AC1/AC2 screenshots.

## CenCon impact

No drift. The component map is unchanged - no new project, no new process, no new external surface.
The privacy posture (visible / controllable) is unchanged and if anything narrower: the overlay is a
preview and a metadata field, the recorded files are untouched, and the preset editor's camera
release behaviour from #29/#35 is not modified.
