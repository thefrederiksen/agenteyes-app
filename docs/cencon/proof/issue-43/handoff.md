# Issue #43 - Developer Agent handoff

**"Size on screen" changes a number and nothing else - the inset has no visual feedback.**

Branch `issue-43-inset-schematic`. Built and tested from an isolated worktree
(`D:\ReposFred\agenteyes-dev43`), restored first.

> **ROUND 2 (this revision) fixed a regression the first round introduced.** The schematic was
> right, but it made the Camera tab 154 px taller than a 1000x760 window's viewport, so a vertical
> scrollbar came back down the right edge - the exact thing issue #35 existed to remove ("the
> settings box is really bad with the scroll bar on the right side... why don't we just make it
> wider so it doesn't have to scroll?", and #35's AC1 and AC3). Round 2 takes the room from the
> WIDTH, adds the test that would have caught it, and re-takes every screenshot. Jump to
> [Round 2](#round-2---the-scrollbar-regression-and-the-test-that-now-holds-it-shut).

---

## What was wrong, and what I built

The slider was never broken mechanically. It was wired, its label updated, and `InsetFraction` was
saved. What was missing is that **no drawing in the dialog used it**: `RedrawOverlayAdorner` draws
the circle over the LIVE CAMERA picture from `ReadOverlay().Circle`, and the inset fraction has no
part in that. `OverlayCorner_Changed` was worse - it logged the new corner and explicitly redrew
nothing. Two real choices, zero feedback, so both read as dead controls.

Added: a **schematic of the recording** beside the existing controls (assumption F1) - a 16:9 box
(288x162) with the camera inset drawn in the chosen corner at the chosen fraction of the recording's
width, redrawn on every change. The camera's own picture is painted inside that inset using the
frames the live preview beside it is **already** receiving (no second capture is started), so
`Diameter`'s crop and `Size on screen`'s size are visible in one picture.

Also split the controls into the two questions they answer, because that is the confusion that
caused the report:

* **`1 - IN THE CAMERA PICTURE (what is inside the circle)`** - shape, left/right, up/down, Diameter.
* **`2 - ON THE RECORDING (where it sits, and how big)`** - Corner, Size on screen, and the schematic.

### Files

| File | Change |
|------|--------|
| `src/AgentEyes.Core/Preview/InsetSchematic.cs` | NEW. The pure geometry: where an inset of a given fraction lands in a box of a given size, in a given corner. Mirrors `HudWindow.LayOutInset` deliberately. |
| `src/AgentEyes.App/PresetEditor.xaml` | The two group headings, the schematic (`InsetSchematicBorder` / `Canvas` / two `Path`s / caption), the **three-column Camera tab** and the **1280-wide default** (round 2). |
| `src/AgentEyes.App/PresetEditor.xaml.cs` | `RedrawInsetSchematic`, `ScreenMotif`, `InsetSchematicFill`, `InsetSchematic_SizeChanged`; `UpdateOverlayUi` now redraws it; `OverlayCorner_Changed` now redraws instead of only logging. Round 2 adds `LayoutVersion` and stamps/checks it in `RememberWindowState` / `RestoreWindowState`. |
| `src/AgentEyes.App/Config.cs` | Round 2: `PresetEditorLayout` - which panel a remembered window size belongs to. |
| `tests/AgentEyes.Tests/InsetSchematicTests.cs` | NEW, 21 tests - the geometry responds to the fraction and the corner, to scale. |
| `tests/AgentEyes.Tests/InsetSchematicUiTests.cs` | NEW, 6 tests - the schematic is actually wired into the dialog, and the two groups are labelled. |
| `tests/AgentEyes.Tests/PresetEditorFitsWithoutScrollingTests.cs` | NEW (round 2), 7 tests - the real dialog is laid out at its default size and every tab must report `ComputedVerticalScrollBarVisibility == Collapsed`. |

### Two decisions worth knowing before you review

1. **The circle is drawn to scale even when it overflows, and the box clips it.** At 60% a circle is
   60% of the frame's WIDTH and therefore ~107% of a 16:9 frame's HEIGHT. The HUD does exactly this
   (`_previewSurface` sets `ClipToBounds = true`), so the schematic shows the same truth and the
   caption says so in words. Shrinking it to fit would be the drawing quietly disagreeing with the
   recording.
2. **AC4's parenthetical is arithmetically wrong and I did not code to it.** The criterion's body -
   "the inset's size is proportional to the chosen fraction" - is implemented exactly. Its aside says
   60% should be "about four times the area" of 15%; four is the ratio in EACH DIMENSION
   (0.60 / 0.15 = 4), so the area ratio of a square inset is 16. Measured on the rendered dialog:
   42.9 x 42.9 = 1840 sq px at 15%, 171.6 x 171.6 = 29447 sq px at 60% - **4.00x linear, 16.0x
   area.** Flagging rather than fudging: if the intent really was 4x area, the fraction would have
   to drive area rather than width, which would contradict `InsetFraction`'s own definition and the
   HUD.

---

## ROUND 2 - the scrollbar regression, and the test that now holds it shut

### What went wrong

Round 1 stacked the two new groups plus the schematic under the existing controls in a single
420-wide column beside the 480x360 live picture. Measured: **563 px of controls in a 528 px
viewport**, so `CameraScroll` engaged and the dialog got its scrollbar back. Round 1's own proof
screenshot showed it and I did not read it.

Nothing in the suite could see this. `PresetEditorLayoutTests` reads the markup and can only tell
that a `ScrollViewer` EXISTS - it says so in its own summary - and #35 proved the fit ONCE, with a
one-off probe that no later change re-ran.

### How the room was found

**Sideways, which is what #35 asked for in the first place.** The Camera tab is now three columns
instead of two, and the reading order matches the two questions the controls answer:

| Column | What is in it | Width |
|--------|---------------|-------|
| the live picture | `CameraPreviewPanel` + the circle adorner + the release note | 480 (unchanged, 480x360) |
| `CameraFrameControls` | `1 - IN THE CAMERA PICTURE` - shape, left/right, up/down, Diameter, `Reset framing` | 330 |
| `RecordingControls` | `2 - ON THE RECORDING` - Corner, Size on screen, the schematic, the caption | 330 |

`OverlayControls` is still the single `StackPanel` that Video mode enables and disables
(`UpdateModeUi`), so both groups still grey out together - it is horizontal now and holds the two
sub-panels. Two smaller trims paid for the rest: the camera hint moved onto the picker's own row
(-23 px) and the section heading above the picture lost 6 px of top margin.

**The default window is 1280x760, up from 1000x760.** The height did not change. 1180 px of columns
need more than a 1000-wide window's 927 px viewport, and the instruction on this dialog has always
been to take more space rather than scroll. `MinWidth` stays 820 and every tab keeps its
`ScrollViewer`, so a genuinely small screen still degrades to scrolling rather than clipping.

The schematic got BIGGER while doing this - 256x144 -> **288x162** - because the new column had the
room, so AC4's proportionality reads more easily than it did in round 1, not less.

Measured after the change, at the same default size:

```
Camera  tab: 487 px of content in a 528 px viewport   scrollbar = Collapsed   (41 px spare)
Capture tab: 286 px                                    scrollbar = Collapsed
Audio   tab: 312 px                                    scrollbar = Collapsed
horizontally: 1180 px of content in a 1224 px viewport (44 px spare)
```

### The half that would otherwise have shipped broken anyway

`RestoreWindowState` prefers a REMEMBERED size over the XAML default, and `RememberWindowState`
writes one every time the dialog closes. So everyone who has ever opened this editor already has the
old panel's 1000x760 in `config.json` - the new default would never have applied to them, and the
scrollbar would have come back for exactly the people who reported it, while a clean machine looked
fixed.

`PresetEditor.LayoutVersion` (now 2) is stamped into `Config.PresetEditorLayout` with every
remembered size. A size stamped against an older panel is a size for a panel that no longer exists,
so it is discarded and the editor opens at its default; a size stamped against this one is restored
exactly as #35 AC10 requires. Both halves are tested.

### The test - and how it was demonstrated FAILING first

`tests/AgentEyes.Tests/PresetEditorFitsWithoutScrollingTests.cs` builds the REAL `PresetEditor` on
an STA thread, lays it out at the client size its own default `Width`/`Height` give it on this
machine, and asserts `ComputedVerticalScrollBarVisibility == Collapsed` for **CaptureScroll,
AudioScroll and CameraScroll**. The dialog is never `Show()`n, so `Window.Loaded` never fires and
neither the camera enumeration nor ffmpeg ever runs - the suite stays fast and silent (1317 tests,
9 s). The window frame is MEASURED from a probe window rather than named, for the same reason
`TheDisplayUnderTest` exists: a literal would make the test pass or fail on the runner's theme
rather than on the dialog's layout.

Full transcript: **`docs/cencon/proof/issue-43/scrollbar-test-red-then-green.txt`**. It is one file
with two halves:

* **RED**, run against this branch's previous head `2168550` - the layout as round 1 left it, with
  only the new test file added:

  ```
  Failed  ATab_AtTheDefaultWindowSize_ShowsNoVerticalScrollBar(tabName: "CameraTab", ...)
    The CameraTab scrolls at the editor's default 1000x760 window (client 984x721): the content
    is 681 px tall in a 528 px viewport, 154 px too much.
  Failed!  - Failed: 1, Passed: 4
  ```

  Capture and Audio passed in the same run, which is what makes the CameraTab failure a real
  finding rather than a broken rig.

* **GREEN**, after the three-column layout and the 1280-wide default: 7 of 7.

Two guards keep the file honest: a viewport/extent sanity check (a ScrollViewer that was never laid
out reports `Collapsed` and zero, which would have passed silently), and
`EveryTabInTheEditor_IsMeasuredHere`, which fails if a fourth tab is ever added without being added
to the list.

---

## The gate

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 1317, Total: 1317
```

main (0fdd6a0) is 1283; round 1 added 27 tests (1310) and round 2 adds 7 more.

### AC8 - how round 1's tests were demonstrated FAILING first

Full transcript: **`docs/cencon/proof/issue-43/ac8-red-run.txt`**. Two halves, both run with the
repo's own source put back to main and the new test files left in place:

* **Step A** - `Preview/InsetSchematic.cs` moved away: `dotnet build` FAILS with 12
  `error CS0103: The name 'InsetSchematic' does not exist` in `InsetSchematicTests.cs`.
* **Step B** - geometry restored, `PresetEditor.xaml` and `PresetEditor.xaml.cs` at main, suite
  re-run with `--no-build`: **6 of 7 `InsetSchematicUiTests` FAIL** - no `InsetSchematicCanvas`, no
  `RedrawInsetSchematic`, `UpdateOverlayUi` does not draw it, `OverlayCorner_Changed` still says
  "Nothing to redraw", no group headings. The 7th (`TheCameraPreviewAndItsCircle_AreUntouched`) is
  the AC7 regression guard and is SUPPOSED to pass against main.
* **Step C** - this branch: build clean, suite green.

A test asserting `InsetSizeText.Text` would have been green in both steps. None of these are.

---

## Proof I produced, and what I could NOT run

**Scope restriction in force:** the human is working on this machine, so I did not launch
`AgentEyesApp`, start a recording, drive the Control API or UIA, or run any smoke script. Nothing
was written under `Videos\AgentEyes`; no ffmpeg was started and none is running.

**What I did instead:** `docs/cencon/proof/issue-43/render-host/` builds the REAL `PresetEditor`
**offscreen** - it is constructed, its Camera tab selected, measured/arranged and rendered with
`RenderTargetBitmap`. It is never `Show()`n, never foregrounded, and `Window.Loaded` never fires, so
`LoadCamerasAsync` never runs and **no camera device and no ffmpeg is ever opened** (the preview also
refuses: `UpdateCameraPreview` returns early while `_camerasLoaded` is false). To re-run it, fix the
`ProjectReference` path in `render-host/shot.csproj` to your checkout and
`dotnet run -c Release -- <output dir>`; the assembly is named `AgentEyes.Tests` so the App's
`InternalsVisibleTo` lets it construct the dialog.

Round 2 changed the host in one important way: it now lays the dialog out at the **client** size
(window minus a measured frame), not at the window size. Round 1 rendered at the full 1000x760 -
16x39 px MORE room than the window really has - and still produced a scrollbar. Every shot now also
prints what the Camera tab's `ScrollViewer` decided, so "no scrollbar" is a stated measurement and
not something a reader has to spot in a picture.

Output: `docs/cencon/proof/issue-43/shots/*.png` (10 renders of the real dialog at 1264x721, the
true client area of the new 1280x760 default) and `docs/cencon/proof/issue-43/measurements.txt`.

| AC | Status | Evidence |
|----|--------|----------|
| **AC1** inset has visible feedback | Met | `shots/ac1-size-15.png` vs `shots/ac1-size-60.png` - the drawn circle is 42.9 px across in one and 171.6 px in the other, at different positions. They differ in the DRAWING, not only the "15%"/"60%" text. |
| **AC2** corner has visible feedback | Met | `shots/ac2-corner-{bottom-right,bottom-left,top-left,top-right}.png` - the same 85.8 px box at four different origins (see `measurements.txt`). |
| **AC3** the two meanings are distinguishable | Met | `shots/ac3-small-crop-large-on-screen.png` (Diameter 20%, Size on screen 55%) vs `shots/ac3-large-crop-small-on-screen.png` (Diameter 95%, Size on screen 15%). **How a person tells them apart:** the two headings name the frame of reference and now sit over two separate columns, `Diameter` sits in the column beside the LIVE CAMERA picture and moves the circle drawn ON that picture, `Size on screen` sits directly above the recording schematic and changes how large the camera lands on the recording. With a camera attached the same schematic also shows the crop. |
| **AC4** the feedback is to scale | Met, with the note above | `measurements.txt`: 4.00x linear / 16.0x area, and `InsetSchematicTests` asserts both exactly. The box grew to 288x162 in round 2. |
| **AC5** the saved value is unchanged | Believed met - **QA must verify the round-trip and the HUD** | Nothing in the overlay save path changed: `ReadOverlay()`/`ReadInto` are untouched, and `InsetSchematicUiTests.TheSchematic_ChangesNothingThatIsSavedOrRecorded` proves the new drawing never assigns to a control or to `_preset`. The existing round-trip test (`CameraOverlayManifestTests`, `InsetFraction = 0.45`) still passes. I did NOT record from a preset - that needs the app. |
| **AC6** nothing recorded changes | Believed met - **ffprobe is QA's** | No file in the camera recording path was touched; `CameraOverlayUiTests.TheOverlay_NeverReachesTheCameraRecorder` still passes. I could not run ffprobe without recording. |
| **AC7** no regression in the panel | Believed met - **QA must confirm the camera release** | The diff does not touch `CameraPreviewController`, `FfmpegCameraPreview`, `CameraReleaseRecord` or `IStrandedCameraProcess`; every existing `x:Name` in `PresetEditor.xaml` is still there (asserted by `TheCameraPreviewAndItsCircle_AreUntouched` AND the older `CameraOverlayUiTests`), including `OverlayControls`, which is still a `StackPanel`. The live preview is still 480x360. The `POST /record/start` within 2s test needs the running app. |
| **AC8** gate | Met | Above. |
| **#35 AC1/AC3** no scrollbar at the default size | Met, and now tested | `PresetEditorFitsWithoutScrollingTests` (all three tabs), `shots/layout-capture-tab.png`, `shots/layout-audio-tab.png`, and the `scrollbar=Collapsed` line printed beside every Camera shot in `measurements.txt`. |

### How QA should verify what I could not

1. **The scrollbar, in the running app** - open the preset editor. **If you have used it before, your
   config holds a 1000x760 from the old panel**: confirm it opens at 1280x760 anyway (that is the
   `PresetEditorLayout` stamp doing its job), and that no tab shows a vertical scrollbar.
   `ComputedVerticalScrollBarVisibility` is readable over UIA on `CameraScroll`.
2. **AC1 / AC2 / AC3 in the running app** - Camera tab (a camera selected makes it richer: the inset
   fills with the live picture). Drag `Size on screen` end to end and change `Corner`; grab the
   window with **PrintWindow** (no need to foreground it). The schematic is `InsetSchematicBorder`;
   its caption `InsetSchematicCaption` reads e.g. "The camera covers 30% of the recording's width,
   bottom-right" and is readable over **UIA**.
3. **AC5** - move `Size on screen`, Save, and read `%LOCALAPPDATA%\AgentEyes\presets.json`; then
   `POST /record/start` with that preset and check `/status` (the HUD is capture-excluded - assert it
   via UIA or `/status`, never a screen grab), and the recording's `manifest.json`
   (`PreviewOverlayInset`).
4. **AC6** - `ffprobe` `camera.mp4` for circle, rectangle and preview-off recordings; the dimensions
   must match a v1.7.0 recording.
5. **AC7** - leave the Camera tab / close the editor and confirm `POST /record/start` succeeds within
   2 seconds; run `scripts\gui-smoke.ps1` (it drives this dialog by `x:Name`) and `api-smoke.ps1`.

**Worth a smoke:** `gui-smoke.ps1` (the panel it drives changed layout twice now) and one camera
recording for AC5/AC6. Nothing in the Core capture path changed, so `api-smoke.ps1` is a formality.

## CenCon impact

No drift. The component map is unchanged (one new pure geometry helper in `AgentEyes.Core/Preview`,
alongside the existing `CameraOverlay.cs`), and the privacy posture is untouched: this drawing starts
no capture of any kind, and the schematic is drawn from numbers the dialog already held.

One method lesson worth keeping: **#35's most important acceptance criterion was proved once, by
hand, and then guarded by nothing.** A criterion that only a person can see is a criterion the next
change gets to break for free. It is a measured runtime fact now, and it runs on every commit.

---

**I believe this is finished.** The defect is fixed at its cause - the two controls now change a
drawing, not only a number - the regression round 1 introduced is fixed at ITS cause and is now held
shut by a test that fails against round 1's own head, and the build and suite are green. The four
criteria that need the running app (AC5, AC6, AC7's release timing, and the in-app screenshots) are
QA's to produce; I have marked them unverified rather than claimed them.
