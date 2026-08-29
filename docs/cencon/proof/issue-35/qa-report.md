# Issue #35 - QA report (round 1)

**VERDICT: PASS - 10 of 10 acceptance criteria verified, with proof. Handed to the Review Gate
(`flow:ready-gate`). QA does NOT merge (DEVELOPMENT_METHOD.md D7, superseding D5).**

| | |
|---|---|
| Issue | #35 - the preset editor, retitled by the scope change to tabs + a live camera preview |
| PR | #37, branch `issue-35-preset-editor-two-columns`, head `ffd3970` |
| Base | `issue-33-hud-live-preview` - only THIS branch's own diff was verified |
| QA round | 1 (first QA pass on this issue) |
| Verified from | isolated worktree `D:\ReposFred\agenteyes-qa35`, restored + built there, never the tray app's locked output |
| Instruments | `qa-probe.cs.txt` (QA's own, not the developer's harness), `qa-xname-diff.txt`, a real `POST /record/start` against a running app |
| Raw output | [`qa-probe-output.txt`](qa-probe-output.txt) - 37 PASS, 0 FAIL |
| Mutation evidence | [`qa-mutations.txt`](qa-mutations.txt) - 7 mutations, every check quoted FIRING |

The criteria verified are the AMENDED ones: the human's scope-change comment REPLACED AC3 and
added AC8-AC10, and the human's later ruling superseded AC2. AC2 is judged as ruled - "present,
reachable, and fully visible WITHIN ITS TAB at the default size, with no scrollbar on any tab" -
not as originally written. No criterion was redefined by QA.

---

## What QA did NOT reuse

The developer committed its own harness at `docs/cencon/proof/issue-35/probe/`. QA did not run it
and did not read its numbers as evidence. QA wrote a separate instrument
([`qa-probe.cs.txt`](qa-probe.cs.txt)) that:

* loads the freshly built `AgentEyesApp` from the QA worktree and constructs the **real**
  `PresetEditor` window, shown with `ShowActivated = false` so it never takes the human's focus;
* measures each tab's live `ScrollViewer` and each named control's real bounds inside its viewport;
* opens the **real** webcam through the shipped preview path and compares two frames 2 s apart;
* starts a **real** camera recording with `FfmpegCameraRecorder.Create` + `Open` - the exact pair
  `RecordingService.cs:567` uses to serve `POST /record/start` - and times it;
* asserts every screenshot it saves has real content (a blank render throws, it never passes).

It loads App.xaml's styles into a plain `Application` rather than constructing `AgentEyes.App.App`,
because the real `App.OnStartup` takes the single-instance mutex and, with the human's tray app
holding it, pops a modal "AgentEyes is already running" box. That is app behaviour working as
designed, not a defect.

---

## Gate (AC7)

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.   0 Error(s)   (2 pre-existing xUnit1031 warnings)
dotnet test  AgentEyes.sln -c Release   ->  Failed: 0, Passed: 1043, Skipped: 0, Total: 1043
```

Run by QA, in the QA worktree, from `bin\x64\Release\` - and re-run clean after every mutation was
reverted. **PASS.**

---

## Criterion by criterion

### AC1 - no vertical scrollbar at the default size on a 1920x1080 display

Expected: `ComputedVerticalScrollBarVisibility` is `Collapsed`.
Actual, read off the live ScrollViewer of each tab at the XAML default 1000x760 (this machine has
two 1920x1080 monitors plus a 1366x768; the primary is 1920x1080):

| Tab | ScrollViewer | Computed | Extent | Viewport | Scrollable |
|-----|--------------|----------|--------|----------|------------|
| Capture | `CaptureScroll` | **Collapsed** | 285.7 | 527.9 | 0.0 |
| Audio | `AudioScroll` | **Collapsed** | 311.8 | 527.9 | 0.0 |
| Camera | `CameraScroll` | **Collapsed** | 515.6 | 527.9 | 0.0 |

A zero viewport would have failed as a broken instrument rather than passing.
Screenshots read, all three render fully: `qa-tab-capture-default.png`, `qa-tab-audio-default.png`,
`qa-tab-camera-default.png`. **PASS.**

Mutation: `Height="760"` -> `620` made the Camera row report `scrollbar Visible, scrollable 127.8`.

### AC2 (as ruled) - every existing control present, reachable and fully visible within its tab

Expected: each named control is present, and fully inside its tab's viewport at the default size.
Actual - each control's real bounds transformed into its ScrollViewer's viewport rectangle:

| Control | Type | Lives on | Fully visible |
|---------|------|----------|---------------|
| `NameBox` | TextBox | header, outside the tabs | yes |
| `MonitorBox` | ComboBox | Capture | yes |
| `FpsBox` | ComboBox | Capture | yes |
| `ModeShot` / `ModeAudio` / `ModeVideo` | RadioButton | Capture | yes |
| `MicBox` | ComboBox | Audio | yes |
| `MicVol` / `SysVol` | Slider | Audio | yes |
| `CameraBox` | ComboBox | Camera | yes |

Name, note and the Save / Save as / Cancel row sit outside the tabs and are visible from every tab -
visible in all three screenshots. **PASS.**

QA did not fail this for "not all settings visible simultaneously": the human's ruling supersedes
that wording, and QA does not judge against a criterion the human has replaced.

### AC3 (replaced) - tabbed layout, no scrollbar on any tab

`EditorTabs` is a `TabControl` with exactly 3 tabs, headers `Capture | Audio | Camera`, in a
1000x760 resizable window (`MinWidth 820`, `MinHeight 420`, `ResizeMode="CanResize"`). No tab shows
a scrollbar at the default size (table above). One screenshot per tab, all read. **PASS.**

### AC4 - nothing lost or renamed

QA recomputed the list itself from `git show origin/issue-33-hud-live-preview:...PresetEditor.xaml`
against the branch head. [`qa-xname-diff.txt`](qa-xname-diff.txt):

```
before: 38 names    after: 48 names
removed or renamed:   NONE
control type changed: NONE
added: AudioScroll AudioTab CameraPreviewImage CameraPreviewPanel CameraPreviewStatus
       CameraScroll CameraTab CaptureScroll CaptureTab EditorTabs
```

All 38 pre-existing names survive with the same control type; the 10 additions are new controls
only. The developer's count (38/48, 0 removed, 0 renamed) is independently confirmed. **PASS.**

Mutation: renaming `MicVol` to `MicVolume` made the same script report
`REMOVED/RENAMED: ['MicVol']`.

One consequence QA is recording for whoever automates this dialog next, because it is real and
outlives this issue: with a `TabControl`, controls on an unselected tab are not in the UI Automation
tree. `scripts\gui-smoke.ps1` never opens the preset editor, so nothing in the current smoke breaks,
but future UIA against this dialog must select the tab first.

### AC5 - behaviour unchanged

* `QuickSquare_Click` -> `RegionLabel` = `1080 x 1080`. Expected `1080 x 1080`.
* `SetExact_Click` with 800 x 600 -> `RegionLabel` = `800 x 600`. Expected `800 x 600`.
* A fully populated preset (region, mixed source, mic, denoise off, gate true, level off,
  MicVol 123, SysVol 45, 60 fps, camera, cameraFps) loaded into the editor and read straight back
  out through `ReadInto` - the exact call `Save_Click` and `SaveAs_Click` make - is **byte-identical**.
* Written through `PresetStore.Save` to the real `%LOCALAPPDATA%\AgentEyes\presets.json` and
  reloaded: **identical** again.

Save / Save as / Cancel: `Save_Click` and `SaveAs_Click` set `SavedPreset` then `DialogResult = true`;
`Cancel_Click` sets `DialogResult = false`; Esc reaches `Cancel_Click` through `IsCancel="True"`.
None of those methods was touched by this diff. The diff to `PresetEditor.xaml.cs` is the tab
handler, the window-state helpers, the preview plumbing, and one tab clause in `UpdateCameraPreview`.
**PASS.**

### AC6 - still usable at 600px tall

At 1000x600: Capture `Collapsed` (285.7/367.9), Audio `Collapsed` (311.8/367.9), Camera
**`Visible`, scrollable 147.8**. Every control checked - including `CameraPreviewPanel`,
`SquareButton` and `SetExactButton` - became fully visible after `BringIntoView`; the probe counted
zero left unreachable. `qa-tab-camera-h600.png` read: the scrollbar is on the right edge and the
pane is reachable. **PASS.**

### AC8 - a Camera tab with a live preview pane

* `CameraPreviewPanel` measures **480 x 360** at the default size (floor 320 x 240).
* `CameraPreviewImage` renders `Stretch="Uniform"`; with `HD Webcam eMeet C960` selected the source
  is a live `BitmapSource` of **320 x 240**, scaled into the 480 x 360 pane.
* Liveness, the check that separates a real preview from a still: the pane's pixels at t=0 and
  t=+2 s differ in **171,435 of 230,400 bytes**. A static or blank pane would have failed.
* `qa-camera-live-t2s.png` read: the pane shows the actual room, letterboxed, with the picker on
  the camera and the "the preview runs only while this tab is open" note underneath.

**PASS.** Mutation: freezing the bitmap after the first frame produced
`the frame at t=+2s is byte-identical to t=0 - the pane is static, not live`.

### AC9 - the preview is testable without recording, and releases the device

The one that can break recording. Verified on four exit paths and on the REST endpoint the
criterion names. No recording was running while the preview ran (the editor's own path never
touches the recorder).

| Path | What QA observed |
|------|------------------|
| **1. A recording starts WHILE previewing** | Precondition asserted: `HoldsCamera == true`. `FfmpegCameraRecorder.Create` + `Open` on the same camera **opened in 1135 ms** (budget 2000). The preview's ffmpeg (pid 37912) **had exited before** the recording opened. `HoldsCamera == false` afterwards. |
| **2. Leaving the Camera tab** | State `Stopped`, `HoldsCamera == false`. A real recording then opened the camera in **608 ms**. |
| **3. Leaving Video mode** | `HoldsCamera == false`. |
| **4. Closing the editor** (preview confirmed RUNNING at the moment of close) | `HoldsCamera == false`; arbiter `HolderCount` **1 -> 0**, so the controller unregistered itself. A real recording then opened in **638 ms**. Also re-checked on a second editor instance reopened onto the Camera tab: released on close. |
| **The literal `POST /record/start`** | Against a running AgentEyes built from this branch, body `{"mode":"video","screen":2,"source":"mic","mic":"...","fps":30,"camera":"HD Webcam eMeet C960","cameraFps":30}`: **HTTP 200 in 1827 / 1548 / 1317 / 1404 ms** over four runs, `State: recording`, `Camera: HD Webcam eMeet C960`. All four inside the 2000 ms budget; the slowest was the first (cold) run, so the margin is real but not large. |

Why the four paths are the whole set: `Save_Click`, `SaveAs_Click` and `Cancel_Click` all end at
`DialogResult = ...`, Esc reaches `Cancel_Click` via `IsCancel="True"`, and the window X closes
directly - every one of them raises `Window.Closed`, which is where `_cameraPreview.Dispose()` runs.
Path 4 IS that choke point, exercised at runtime.

**PASS**, and the mutations show the checks are not decorative:

* removing `CameraDeviceArbiter.ReleaseForRecording` from `Create` made the recording fail outright
  with *"the camera ... is already in use by another application"*, with the preview ffmpeg still
  alive - exactly the outcome AC9 exists to prevent;
* removing the tab clause fired *"the controller STILL holds the camera after leaving the Camera tab"*;
* removing `_cameraPreview.Dispose()` fired *"STILL holds the camera after the editor closed"* and
  *"arbiter holder count went 1 -> 1"*.

**Stated rather than hidden:** under mutations M2 and M3 the 2-second timing arm still PASSED,
because the arbiter call inside `Create` rescues the recording even when the editor never let go.
A pass built only on the timing budget would have missed both. The `HoldsCamera` / `HolderCount`
presence checks are what catch them; both arms are kept for that reason.

**What this cannot see, stated:** `CameraDeviceArbiter` coordinates holders inside ONE process. A
recording started from the CLI (`agenteyes.exe`, its own process) while the tray app previews cannot
be rescued by it and fails loudly with "already in use". That limitation is documented in the
arbiter's own summary, predates this issue, and is not a #35 defect.

### AC10 - tab, size and position remembered

Set tab 2 (Camera), 1120x700 at (140, 90), closed. `%LOCALAPPDATA%\AgentEyes\config.json` then held
`PresetEditorTab=2 PresetEditorWidth=1120 PresetEditorHeight=700 PresetEditorLeft=140
PresetEditorTop=90`. Reopened: tab 2, 1120x700 at (140, 90).

QA deliberately used the Camera tab rather than an inert one, because reopening straight onto it is
the one restore that also re-arms the live preview from the constructor: the preview reached
`Running` with no error, and released the camera again on close. `RestoreWindowState` also refuses a
position that is no longer on any monitor (`SystemParameters.VirtualScreen`, 120x40 minimum
on-screen), so the dialog cannot reopen where nobody can see it. **PASS.**

Mutation: removing `RememberWindowState()` fired both AC10 checks.

---

## Review findings (no defects; recorded for the gate)

* **Standards.** Enterprise logging is present on every new public path
  (`[PresetEditor]`, `[CameraPreviewController]`, `[CameraDeviceArbiter]`, `[FfmpegCameraPreview]`),
  including the load-bearing release timings. Try-catch sits at entry points only - the tab handler,
  the camera handler, `Window.Closed`, the reader thread, the arbiter's callback boundary - not in
  helpers. The UI never blocks: the camera list loads on a background thread, frames arrive on a
  background thread and are marshalled with `Dispatcher.BeginInvoke`, and a frame arriving while one
  is pending is dropped rather than queued. No fallbacks were added: a camera that will not open
  produces a named message on the pane, not a silent blank. Every changed file is pure ASCII
  (checked, 0 non-ASCII bytes in all ten).
* **Constructor ordering, worth the gate's eye but not a defect.** `RestoreWindowState()` runs
  before `_cameraPreview` is assigned and sets `EditorTabs.SelectedIndex`, which fires
  `EditorTabs_Changed` -> `UpdateCameraPreview()`. That is safe today only because
  `UpdateCameraPreview` returns on `!_camerasLoaded` before touching `_cameraPreview`. Exercised at
  runtime by the AC10 restore-onto-Camera-tab case with no error. It would become a
  NullReferenceException if that first guard were ever removed or reordered.
* **Not a regression, but observed:** after a recording takes the camera from the preview, the
  preview does not come back on its own - the pane says "Preview stopped - the camera is in use by a
  recording." and returns when the user leaves and re-enters the tab. Nothing in #35 asks for
  auto-resume, and the message is honest, so this is recorded, not failed.
* **Privacy posture (visible / controllable) intact and, if anything, stronger:** the preview is a
  user-initiated pane that holds the camera only while its tab is open, says so on screen, and hands
  the device back on every exit path.
* **Below this branch:** the #33 HUD preview and the #28 camera track were not re-verified - they
  have their own issues and gates. Nothing in this diff touches them beyond two call sites
  (`MainWindow.xaml.cs`, `ManagePresetsDialog.cs`) that pass `_cfg` into the editor's constructor,
  and one added line in `FfmpegCameraRecorder.Create`. Full suite green either way.
* **Smokes, targeted not reflexive:** `api-smoke.ps1` is not touched by this diff, and the one place
  the REST surface meets this change - `POST /record/start` with a camera - QA ran directly and more
  precisely (four timed runs above). `gui-smoke.ps1` never opens the preset editor and, separately,
  still points at the non-x64 `bin\Release\` path (the subject of its own branch), so running it here
  would have said nothing about #35.

---

## Environment - what QA displaced and restored

The human's installed **v1.6.2** tray app (`%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray`,
pid 12516) was stopped for roughly four minutes so a build of this branch could hold port 7882 for
the `POST /record/start` runs. Afterwards:

* the dev build was stopped and the installed **v1.6.2** app relaunched with `--tray`
  (`GET /health` -> `{"ok": true}`);
* `config.json` and `presets.json` restored **byte-for-byte** - SHA256 `D60F3C21...9424C84` and
  `BFFC4252...FA5C6221`, identical to the pre-QA backup;
* all four `POST /record/start` test recordings deleted from `%USERPROFILE%\Videos\AgentEyes\`;
  pending transcriptions 0.

---

*QA Agent, CenCon Development Method. Next stop: the Review Gate (`flow:ready-gate`). QA has not
merged and has not closed the issue.*
