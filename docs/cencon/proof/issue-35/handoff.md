# Issue #35 - Developer handoff

**The preset editor is now a tabbed, generously sized window with a live camera preview.**

Branch: `issue-35-preset-editor-two-columns` (branched from `issue-33-hud-live-preview`, then
`origin/issue-29-camera-preview` merged in - see "What came in from #29").
I believe this is finished.

---

## What the human asked for, and what changed under it

The issue started as "widen it to two columns so the scrollbar goes away". Mid-implementation the
human replaced that with a SCOPE CHANGE comment: tabs, a bigger window, and a Camera tab with a live
preview you can test before recording. The two-column work was discarded; what is on the branch is
the tabbed version.

The editor is now `1000 x 760`, resizable, with:

| Where | What it holds |
|-------|---------------|
| Header (outside the tabs, always visible) | `NameBox`, `NoteBox` |
| **Capture** tab | `MonitorBox`, `ShowAreaButton`, `FullRadio`/`RegionRadio`/`SelectAreaButton`/`RegionLabel`, the whole `RegionOptions` block, and MODE (`ModeShot`/`ModeAudio`/`ModeVideo`, `FpsBox`) |
| **Audio** tab | `MicBox`, `SrcMic`/`SrcSystem`/`SrcMixed`, `DenoiseCheck`/`GateCheck`/`LevelCheck`, `MicVol`/`SysVol` and their readouts |
| **Camera** tab | `CameraBox`, `CameraHint`, and the 480x360 live preview pane (`CameraPreviewPanel`/`CameraPreviewImage`/`CameraPreviewStatus`) |
| Footer | `ErrorText`, `SaveButton`, `SaveAsButton`, `CancelButton` |

Each tab keeps its own `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`) as the small-screen
safety net. At the default size none of them engages.

## What came in from #29 (assumption D3)

`origin/issue-29-camera-preview` was merged into this branch rather than reimplemented, exactly as
D3 asks. That brings `CameraPreviewController`, `CameraDeviceArbiter`, `CameraPreviewSession`,
`FfmpegCameraPreview` and `CameraPreviewTests`. **No second mechanism that can hold the camera was
invented.** This issue moves that pane onto the Camera tab, enlarges it to 480x360, and adds ONE new
release trigger to the existing single choke point (`UpdateCameraPreview`): leaving the Camera tab
stops the preview, exactly as leaving Video mode already did.

One merge consequence QA should know about: on this code line the camera recorder opens in two steps
(`FfmpegCameraRecorder.Create` then `.Open()`), and `CameraDeviceArbiter.ReleaseForRecording` sits in
`Create` (`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:493`). #29's IL guard test asserted the
call site was in a method called `Start`, which does not exist on this line, so the assertion was
updated to `Create` - the guarantee is unchanged, the method name is not.

---

## Acceptance criteria

All numbers below are measured, not asserted. The instrument is committed:
`docs/cencon/proof/issue-35/probe/` (see "How QA can re-run everything").
Raw output: [`layout-measurement.txt`](layout-measurement.txt).

| AC | Verdict | Evidence |
|----|---------|----------|
| **AC1** no scrollbar at default size | PASS | At 1000x760 every tab reports `ComputedVerticalScrollBarVisibility = Collapsed`. Extent/viewport: Capture 285.7/527.9, Audio 311.8/527.9, Camera 515.6/527.9. Screenshots `tab-capture-default-760.png`, `tab-audio-default-760.png`, `tab-camera-default-760.png`. |
| **AC2** every setting visible at once | **CONTRADICTED BY THE AMENDED AC3 - see below.** | Measured facts: all 41 named controls are present in the live window; 40 are rendered across the three tabs (the 41st, `RegionWarn`, is `Visibility="Collapsed"` until a region overflows its monitor, as before). No control needs scrolling on any tab at the default size. |
| **AC3 (replaced)** tabbed layout, no scrollbar on any tab | PASS | 3 tabs (Capture / Audio / Camera); the per-tab measurements above; one screenshot per tab. |
| **AC4** nothing lost or renamed | PASS | [`xname-diff.txt`](xname-diff.txt): 38 names before, 48 after, **0 removed, 0 renamed**. The 10 additions are new controls only (`EditorTabs`, the three `*Tab`s, the three `*Scroll`s, and #29's three preview parts). |
| **AC5** behaviour unchanged | PASS | A fully-populated preset loaded into the editor and read straight back out is byte-identical on the Save path (same instance, id included) and identical apart from its new id on the Save-as path; written through `PresetStore` to the real `presets.json` and re-loaded, it is identical again. Full JSON in `layout-measurement.txt`. `LoadFrom`, `ReadInto`, `Validate`, `Save_Click`, `SaveAs_Click`, `Cancel_Click` and every region/mode handler are unchanged by this issue - the diff on `PresetEditor.xaml.cs` is the tab handler, the window-state helpers and the one tab clause in `UpdateCameraPreview`. |
| **AC6** still usable on a small screen | PASS | At 1000x600 the Camera tab's ScrollViewer becomes `Visible` with `ScrollableHeight = 147.8`; the one control that then needs scrolling (`CameraPreviewPanel`) is reached by scrolling to it ("still unreachable after scrolling to it: none"). Screenshot `tab-camera-height-600.png`. |
| **AC7** gate | PASS | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded. 0 Error(s)`. `dotnet test AgentEyes.sln -c Release` -> `Failed: 0, Passed: 1043`. Built and run from an isolated worktree (`D:\ReposFred\agenteyes-wt-35`), never the locked tray-app output. |
| **AC8** Camera tab with a live preview pane | PASS | Pane is 480x360 (floor is 320x240); selecting `HD Webcam eMeet C960` produced live frames - `BitmapSource 320x240` rendered `Uniform` into the pane. Screenshot `tab-camera-live.png` shows the actual room. |
| **AC9** preview releases the device | PASS | With the preview RUNNING, `CameraDeviceArbiter.ReleaseForRecording` returned in **409 ms**, and a real `FfmpegCameraRecorder.Create + Open` on the same camera then opened in **634 ms** (budget 2000 ms). After switching away from the Camera tab, `CameraPreviewController.HoldsCamera == false` and a real camera recording opened in **596 ms**. |
| **AC10** tab and window placement remembered | PASS | Set tab=2, 1100x740 at (120,80), closed: `config.json` holds `PresetEditorTab: 2`, `PresetEditorWidth: 1100`, `PresetEditorHeight: 740`, `PresetEditorLeft: 120`, `PresetEditorTop: 80`. Reopened: tab=2, 1100x740, (120,80). "AC10 round trip: PASS". |

### AC2 needs a human ruling - it contradicts the amended AC3

AC2 requires `MonitorBox`, `MicBox` and `CameraBox` to be **simultaneously visible**. The amended
AC3 requires those same settings to live on **separate tabs**. A tab shows one page at a time, so
the two cannot both be true, whatever the implementation.

I did not redefine AC2 to make it pass and I am not claiming it passes. What was built is the
explicit human ruling (tabs). What partly preserves AC2's intent: the preset's identity (`NameBox`,
`NoteBox`) and the Save/Save as/Cancel row were deliberately kept OUTSIDE the tabs so they are
visible from every tab. **AC2 should be amended by the Product Agent or the human** to something the
tabbed design can satisfy - e.g. "every control is reachable in at most one tab switch and fully
visible without scrolling on its own tab", which is measured and PASSES today.

A second consequence worth recording: with tabs, controls belonging to an unselected tab are not in
the UI Automation tree at all. `scripts\gui-smoke.ps1` never opens this dialog, so nothing in the
current smoke breaks - but any future UIA that drives the preset editor must select the tab first.

---

## How QA can re-run everything

The tray app is single-instance (`App.OnStartup` takes the `AgentEyes-singleinstance` mutex), so a
second copy cannot be started while an installed AgentEyes is running. The proof harness therefore
loads the freshly built `AgentEyesApp`, loads `App.xaml` for the real styles, and constructs the real
`PresetEditor` window - same window type, same XAML, same build - showing it with
`ShowActivated = false` so it never steals focus, and capturing it with `PrintWindow`.

```
dotnet build AgentEyes.sln -c Release
dotnet test  AgentEyes.sln -c Release
dotnet run -c Release --project docs\cencon\proof\issue-35\probe\PresetEditorProbe.csproj -- ^
    docs\cencon\proof\issue-35
```

The harness backs up and restores both `%LOCALAPPDATA%\AgentEyes\config.json` and `presets.json`
(proving AC5 and AC10 means actually writing them). It is NOT in `AgentEyes.sln`, so it never
affects the gate.

**What the harness cannot see, stated so it is not overclaimed:**

* It measures the dialog with no `Owner`, so `WindowStartupLocation="CenterOwner"` falls back to the
  screen. Nothing else about the window differs.
* It exercises `LoadFrom`/`ReadInto` (what Save and Save as call) rather than clicking the buttons -
  `DialogResult` can only be set on a window opened with `ShowDialog`.
* AC8/AC9 depend on a camera being attached. On a machine with none, the harness says
  "AC8 and AC9 CANNOT be observed here - this is a missing observation, NOT a pass" and moves on.

**One trap that cost time and will cost QA the same:** this machine's webcam rejects a 15 fps
DirectShow open outright (`Could not set video options`, ffmpeg exit -5, in ~110 ms). That looks
exactly like device contention and is not - it is a camera capability. The harness records at
**30 fps**, the preset default. If a camera check fails in ~100 ms rather than ~600 ms, suspect the
frame rate before suspecting the release.

### Areas worth a smoke (QA decides)

* **gui-smoke** - the change is confined to `PresetEditor`, which `gui-smoke.ps1` does not open, so
  the smoke is unlikely to say anything new. Cheap enough to run if QA wants the reassurance that the
  launcher and REC path are untouched.
* **api-smoke** - not touched by this issue. The one place the REST surface meets this change is
  `POST /record/start` with a camera preset while the editor is open, which AC9 covers directly and
  more precisely (the harness opens a real camera recording and times it).

---

## Unit tests added

`tests/AgentEyes.Tests/PresetEditorLayoutTests.cs` (8 tests): every pre-existing `x:Name` and its
control type still present; the settings split across Capture/Audio/Camera with name/note outside the
tabs; every tab keeps an `Auto` ScrollViewer; the preview pane is at least 320x240 and renders
`Uniform`; the window is generously sized and resizable; `UpdateCameraPreview` gates on the Camera
tab and `EditorTabs_Changed` reaches it; the tab/size/position are both written and read back.

Each one is a PRESENCE claim, and each was run against a known-bad input to show it FIRES -
[`mutation-evidence.txt`](mutation-evidence.txt) records all four mutations (renamed control,
removed safety net, undersized pane, ungated preview) and the failure each produced.

**What these tests cannot see:** whether the rendered content actually fits without scrolling, and
whether the preview really releases the camera. Those are runtime facts and are proven by the
harness above and by `CameraPreviewTests`.

## CenCon impact

No drift. The component map is unchanged; the privacy posture is unchanged and, if anything,
reinforced - the live preview is a visible, user-initiated pane that holds the camera only while its
tab is open, and hands the device back on every exit path.
