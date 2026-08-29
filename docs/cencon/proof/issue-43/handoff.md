# Issue #43 - Developer Agent handoff

**"Size on screen" changes a number and nothing else - the inset has no visual feedback.**

Branch `issue-43-inset-schematic`. Built and tested from an isolated worktree
(`D:\ReposFred\agenteyes-dev43`), restored first.

---

## What was wrong, and what I built

The slider was never broken mechanically. It was wired, its label updated, and `InsetFraction` was
saved. What was missing is that **no drawing in the dialog used it**: `RedrawOverlayAdorner` draws
the circle over the LIVE CAMERA picture from `ReadOverlay().Circle`, and the inset fraction has no
part in that. `OverlayCorner_Changed` was worse - it logged the new corner and explicitly redrew
nothing. Two real choices, zero feedback, so both read as dead controls.

Added: a small **schematic of the recording** beside the existing controls (assumption F1) - a 16:9
box (256x144) with the camera inset drawn in the chosen corner at the chosen fraction of the
recording's width, redrawn on every change. The camera's own picture is painted inside that inset
using the frames the live preview beside it is **already** receiving (no second capture is started),
so `Diameter`'s crop and `Size on screen`'s size are visible in one picture.

Also split the controls into the two questions they answer, because that is the confusion that
caused the report:

* **`1 - IN THE CAMERA PICTURE (what is inside the circle)`** - shape, left/right, up/down, Diameter.
* **`2 - ON THE RECORDING (where that circle sits, and how big)`** - Corner, Size on screen, and the
  schematic.

### Files

| File | Change |
|------|--------|
| `src/AgentEyes.Core/Preview/InsetSchematic.cs` | NEW. The pure geometry: where an inset of a given fraction lands in a box of a given size, in a given corner. Mirrors `HudWindow.LayOutInset` deliberately. |
| `src/AgentEyes.App/PresetEditor.xaml` | The two group headings, the schematic (`InsetSchematicBorder` / `Canvas` / two `Path`s / caption), and tightened vertical spacing so the schematic is visible without scrolling. |
| `src/AgentEyes.App/PresetEditor.xaml.cs` | `RedrawInsetSchematic`, `ScreenMotif`, `InsetSchematicFill`, `InsetSchematic_SizeChanged`; `UpdateOverlayUi` now redraws it; `OverlayCorner_Changed` now redraws instead of only logging. |
| `tests/AgentEyes.Tests/InsetSchematicTests.cs` | NEW, 21 tests - the geometry responds to the fraction and the corner, to scale. |
| `tests/AgentEyes.Tests/InsetSchematicUiTests.cs` | NEW, 6 tests - the schematic is actually wired into the dialog, and the two groups are labelled. |

### Two decisions worth knowing before you review

1. **The circle is drawn to scale even when it overflows, and the box clips it.** At 60% a circle is
   60% of the frame's WIDTH and therefore ~107% of a 16:9 frame's HEIGHT. The HUD does exactly this
   (`_previewSurface` sets `ClipToBounds = true`), so the schematic shows the same truth and the
   caption says so in words. Shrinking it to fit would be the drawing quietly disagreeing with the
   recording.
2. **AC4's parenthetical is arithmetically wrong and I did not code to it.** The criterion's body -
   "the inset's size is proportional to the chosen fraction" - is implemented exactly. Its aside says
   60% should be "about four times the area" of 15%; four is the ratio in EACH DIMENSION
   (0.60 / 0.15 = 4), so the area ratio of a square inset is 16. Measured on the running dialog:
   38.1 x 38.1 = 1452 sq px at 15%, 152.4 x 152.4 = 23226 sq px at 60% - **4.00x linear, 16.0x area.**
   Flagging rather than fudging: if the intent really was 4x area, the fraction would have to drive
   area rather than width, which would contradict `InsetFraction`'s own definition and the HUD.

---

## The gate

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 1310, Total: 1310
```

main (0fdd6a0) is 1283; the 27 new tests are the two files above.

### AC8 - how the new tests were demonstrated FAILING first

Full transcript: **`docs/cencon/proof/issue-43/ac8-red-run.txt`**. Two halves, both run with the
repo's own source put back to main and the new test files left in place:

* **Step A** - `Preview/InsetSchematic.cs` moved away: `dotnet build` FAILS with 12
  `error CS0103: The name 'InsetSchematic' does not exist` in `InsetSchematicTests.cs`. The geometry
  those tests assert did not exist in the code that shipped the defect.
* **Step B** - geometry restored, `PresetEditor.xaml` and `PresetEditor.xaml.cs` at main
  (`git checkout main -- ...`), suite re-run with `--no-build` (the UI tests read the SOURCE at run
  time, so no rebuild is needed to point them at old code): **6 of 7 `InsetSchematicUiTests` FAIL** -
  no `InsetSchematicCanvas`, no `RedrawInsetSchematic`, `UpdateOverlayUi` does not draw it,
  `OverlayCorner_Changed` still says "Nothing to redraw", no group headings. The 7th
  (`TheCameraPreviewAndItsCircle_AreUntouched`) is the AC7 regression guard and is SUPPOSED to pass
  against main.
* **Step C** - this branch: build clean, 1310/1310 green.

A test asserting `InsetSizeText.Text` would have been green in both steps. None of these are.

---

## Proof I produced, and what I could NOT run

**Scope restriction in force:** the human is working on this machine, so I did not launch
`AgentEyesApp`, start a recording, drive the Control API or UIA, or run any smoke script. Nothing
was written under `Videos\AgentEyes`; no ffmpeg was started.

**What I did instead:** `docs/cencon/proof/issue-43/render-host/` builds the REAL `PresetEditor`
**offscreen** - it is constructed, its Camera tab selected, measured/arranged and rendered with
`RenderTargetBitmap`. It is never `Show()`n, never foregrounded, and `Window.Loaded` never fires, so
`LoadCamerasAsync` never runs and **no camera device and no ffmpeg is ever opened** (the preview also
refuses: `UpdateCameraPreview` returns early while `_camerasLoaded` is false). To re-run it, fix the
`ProjectReference` path in `render-host/shot.csproj` to your checkout and
`dotnet run -- <output dir>`; the assembly is named `AgentEyes.Tests` so the App's `InternalsVisibleTo`
lets it construct the dialog.

Output: `docs/cencon/proof/issue-43/shots/*.png` (8 renders of the real dialog) and
`docs/cencon/proof/issue-43/measurements.txt` (the inset's actual bounds in each).

| AC | Status | Evidence |
|----|--------|----------|
| **AC1** inset has visible feedback | Met | `shots/ac1-size-15.png` vs `shots/ac1-size-60.png` - the drawn circle is 38.1 px across in one and 152.4 px in the other, at different positions. They differ in the DRAWING, not only the "15%"/"60%" text. |
| **AC2** corner has visible feedback | Met | `shots/ac2-corner-{bottom-right,bottom-left,top-left,top-right}.png` - the same 76.2 px box at four different origins (see `measurements.txt`). |
| **AC3** the two meanings are distinguishable | Met | `shots/ac3-small-crop-large-on-screen.png` (Diameter 20%, Size on screen 55%) vs `shots/ac3-large-crop-small-on-screen.png` (Diameter 95%, Size on screen 15%). **How a person tells them apart:** the two headings name the frame of reference, `Diameter` sits under the LIVE CAMERA picture and moves the circle drawn ON that picture, `Size on screen` sits under the recording schematic and changes how large the camera lands on the recording. With a camera attached the same schematic also shows the crop, because the inset is filled with the cropped camera picture. |
| **AC4** the feedback is to scale | Met, with the note above | `measurements.txt`: 4.00x linear / 16.0x area, and `InsetSchematicTests` asserts both exactly. |
| **AC5** the saved value is unchanged | Believed met - **QA must verify the round-trip and the HUD** | Nothing in the save path changed: `ReadOverlay()`/`ReadInto` are untouched, and `InsetSchematicUiTests.TheSchematic_ChangesNothingThatIsSavedOrRecorded` proves the new drawing never assigns to a control or to `_preset`. The existing round-trip test (`CameraOverlayManifestTests`, `InsetFraction = 0.45`) still passes. I did NOT record from a preset - that needs the app. |
| **AC6** nothing recorded changes | Believed met - **ffprobe is QA's** | No file in the camera recording path was touched; `CameraOverlayUiTests.TheOverlay_NeverReachesTheCameraRecorder` still passes. I could not run ffprobe without recording. |
| **AC7** no regression in the panel | Believed met - **QA must confirm the camera release** | The diff does not touch `CameraPreviewController`, `FfmpegCameraPreview`, `CameraReleaseRecord` or `IStrandedCameraProcess`; every existing `x:Name` in `PresetEditor.xaml` is still there (asserted by `TheCameraPreviewAndItsCircle_AreUntouched` AND the older `CameraOverlayUiTests`). The offscreen shots show the live-preview pane, the shape radios, the three circle sliders and `Reset framing` unchanged. The `POST /record/start` within 2s test needs the running app. |
| **AC8** gate | Met | Above. |

### How QA should verify what I could not

1. **AC1 / AC2 / AC3 in the running app** - open the preset editor, Camera tab (a camera selected
   makes it richer: the inset fills with the live picture). Drag `Size on screen` end to end and
   change `Corner`; grab the window with **PrintWindow** (no need to foreground it). The schematic is
   `InsetSchematicBorder`; its caption `InsetSchematicCaption` reads e.g. "The camera covers 30% of
   the recording's width, bottom-right" and is readable over **UIA**.
2. **AC5** - move `Size on screen`, Save, and read `%LOCALAPPDATA%\AgentEyes\presets.json`; then
   `POST /record/start` with that preset and check `/status` (the HUD is capture-excluded - assert it
   via UIA or `/status`, never a screen grab), and the recording's `manifest.json`
   (`PreviewOverlayInset`).
3. **AC6** - `ffprobe` `camera.mp4` for circle, rectangle and preview-off recordings; the dimensions
   must match a v1.7.0 recording.
4. **AC7** - leave the Camera tab / close the editor and confirm `POST /record/start` succeeds within
   2 seconds; run `scripts\gui-smoke.ps1` (it drives this dialog by `x:Name`) and `api-smoke.ps1`.

**Worth a smoke:** `gui-smoke.ps1` (the panel it drives changed layout) and one camera recording for
AC5/AC6. Nothing in the Core capture path changed, so `api-smoke.ps1` is a formality.

## CenCon impact

No drift. The component map is unchanged (one new pure geometry helper in `AgentEyes.Core/Preview`,
alongside the existing `CameraOverlay.cs`), and the privacy posture is untouched: this drawing starts
no capture of any kind, and the schematic is drawn from numbers the dialog already held.

---

**I believe this is finished.** The defect is fixed at its cause - the two controls now change a
drawing, not only a number - the new tests could not pass against the code that shipped it, and the
build and suite are green. The four criteria that need the running app (AC5, AC6, AC7's release
timing, and the in-app screenshots) are QA's to produce; I have marked them unverified rather than
claimed them.
