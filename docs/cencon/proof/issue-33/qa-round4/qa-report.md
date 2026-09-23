# Issue #33 - QA report (round 4)

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

Verdict: **PASS. 12 of 12 acceptance criteria verified, each with running-app evidence produced by
this QA session. The defect class that survived three rounds is closed, and QA attacked it on nine
routes rather than on the three known instances.**

- Repository: `thefrederiksen/agenteyes-app` (`thefrederiksen/AgentEyes` in the skill files is ARCHIVED)
- Issue: #33 - PR: #34 - Branch: `issue-33-hud-live-preview` at `4b9cb39`
- Round-3 tip QA failed: `849ac58` (report: `../qa-round3/qa-report.md`)
- Verified: 2026-08-29. Displays: DISPLAY3 primary 0,0 1920x1080; DISPLAY1 1920,-5 1920x1080;
  DISPLAY2 1173,1080 1366x768 - all three at 100% scale. Camera `HD Webcam eMeet C960`.
- Built and run from an ISOLATED worktree `D:\ReposFred\agenteyes-qa33-r4` (detached at `4b9cb39`),
  `bin\x64\Release\`, never `bin\Release\` and never the shared checkout.
- **Every running-app number below was produced by a binary whose SHA256 is pinned in `gate.txt`:
  `A55667D5...`, built `--no-incremental` from a `git status`-BYTE-CLEAN tree.** That pin is not
  decoration - see section 2.
- QA never asked the human to run anything.

---

## 1. Criteria table

| AC | Verdict | Instrument, and the number that carries it |
|----|---------|--------------------------------------------|
| **AC1** toggle exists, defaults off | **PASS** | fresh config -> pill `367x52`, button `Show preview` help=`hidden`, no panel in the UIA tree; toggle -> `520x400`; toggle again -> `367x52`. On the CONSTRUCTOR path the panel opens at `520x400` with `IMAGE rect=1553,113,490,276` - non-zero, the round-2 regression is gone. `route-attack.txt` S1/S2/S3 |
| **AC2** screen preview is live | **PASS** | `preview\screen.jpg` `065C9ECA...` -> `B15179AB...` in 3 s, `framesRead 98->127`. **The failing arm was demonstrated**: with the churn window gone the same probe reports `DIFFER=False` while `framesRead 488->529` still climbs. `ac2-ac3-ac4-ac5.txt`, `ac8-and-the-ac2-failing-arm.txt`, `img/ac2-screen-t0.jpg` |
| **AC3** camera live AND opened once | **PASS** | `camera.jpg` `EB16CA24...` -> `B1FAB8C3...`, `framesRead 104->135`; ffmpeg inventory: 3 processes, **exactly 1 with `-i video=`** (pid 9796, `-f dshow`, `pipe:1`, `mjpeg` - the preview is a second output on the recorder); `camera.mp4` 41.267 s vs `recording.mp4` 40.667 s, **delta 0.600 s < 1.0 s**, `CameraComplete: "yes"`, `CameraStopKind: "clean-quit"`. `ac2-ac3-ac4-ac5.txt`, `img/ac3-camera-t0.jpg` |
| **AC4** four corners composite | **PASS** | four corner buttons, four DISTINCT inset rects, panel image constant at `1400,113 490x276`: TL `1409,107` / TR `1736,107` / BL `1409,313` / BR `1736,313`, inset `145x82` (30% of 490); `/status PreviewOverlayCorner` tracked each click. Visually confirmed in `img/ac6-knownbad-03.png` and `img/ac10-before-kill.png`. `ac2-ac3-ac4-ac5.txt` |
| **AC5** corner reaches the manifest | **PASS** | four corners cycled, ending `top-right` -> `"PreviewOverlayCorner": "top-right"`; a run with the preview never enabled -> the key is **ABSENT from the file**. `ac2-ac3-ac4-ac5.txt`, `ac10-ac11.txt` |
| **AC6** no HUD, no mirror tunnel in the output | **PASS** | HUD parked at 2300,300 520x400 = `380,305` inside the captured frame; six frames cropped `520:400:380:305` from `recording.mp4` show only the desktop underneath (`distinctColors` 206-218). **Known-bad control**: the same crop with `MQS_HUD_CAPTURABLE=1` shows the WHOLE HUD, its panel, the camera inset and a visible mirror tunnel (`distinctColors` 1157-1247). `img/ac6-excluded-03.png` vs `img/ac6-knownbad-03.png` |
| **AC7** resizable AND persists | **PASS** | see section 3 - this is the criterion that failed three times |
| **AC8** toggling mid-recording is safe | **PASS** | in ONE live recording: modes screen/camera/both, all four corners, preview hidden then shown; **the tap keeps draining while the panel is hidden** (`framesRead 801 -> 831`, `publishing=False`); result `recording.mp4` 97.400 s / `camera.mp4` 97.967 s, **delta 0.567 s**, `CameraComplete: "yes"`. `ac8-and-the-ac2-failing-arm.txt` |
| **AC9** bounded cost | **PASS**, with a stated limit | two paired 60 s runs, both numbers reported (section 5) |
| **AC10** a preview failure never harms the recording | **PASS** | preview directory deleted at t=11 s; recording ran on to 62 s elapsed / 68.27 s of file; `framesRead 105/113 -> 227/235` **kept climbing**; `PreviewFailed=True`; two `[WARN] [PreviewTap] Publish FAILED` lines; the panel shows **"Preview unavailable - no frames from the recorder. The recording is unaffected."** and the status line reads `both bottom-right | no frames` - not a frozen last frame; `CameraComplete: "yes"`. `ac10-ac11.txt`, `ac10-readable-error.txt`, `img/ac10-before-kill.png` vs `img/ac10-after-kill.png` |
| **AC11** no regression with the preview off | **PASS** | `PreviewArmed=False`, HUD is the `367x52` pill, no `pipe:1` in the manifest's ffmpeg command, `PreviewOverlayCorner` absent, `camera.mp4` 31.933 s vs 31.400 s delta 0.533 s, `CameraComplete: "yes"`. `ac10-ac11.txt` |
| **AC12** gate | **PASS** | `Build succeeded. 0 Error(s)` (2 pre-existing xUnit1031 warnings in `PostRecordingQueueTests.cs:309,314`, not this PR); `Failed: 0, Passed: 1068`; 13/13 QA mutations FIRED; 32/32 developer mutations re-run by QA FIRED; the round-3 reconstruction goes 17 red. `gate.txt`, `qa-mutation-round4.txt`, `dev-mutation-sweep-rerun-by-qa.txt` |

**12 of 12 verified.**

---

## 2. QA's own instrument failed first, and it is recorded here rather than hidden

The FIRST run of QA's route attack REPORTED THE DEFECT AS STILL PRESENT: a plain toggle wrote
`520/400`, a bare `SetWindowPos` wrote `1100/600`, and a seeded `200x100` drifted to `260x100`.

That was **a stale binary produced by QA's own mutation harness**, not the product. The chain that
establishes it, in order:

1. The app log shows the memory acquiring `520x400` between the panel opening and the stop with **no
   `hud: resized by the person via ...` line** - i.e. through a path that logs nothing.
2. The failing run recorded a size after a **bare `SetWindowPos` with no gesture behind it**. In the
   shipped assembly there is no path from a size change to the memory at all: the IL guards
   (section 4) prove `HudSizeMemory::RecordUserResize` has exactly one caller and that the sizing
   classes contain no `add_SizeChanged`/`add_LayoutUpdated`. Only a layout-observing subscription can
   produce that number.
3. QA rebuilt `--no-incremental` from a `git status`-byte-clean tree, pinned the DLL hash
   (`A55667D5...`), and re-ran the whole attack **twice** plus a trimmed repeat: every route came out
   `null / null`. `route-attack.txt`, `route-attack-repeat.txt`.
4. QA then deliberately rebuilt with round 3's mechanism restored - one line,
   `_window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);` - hash `B41A5F92...`, and the run
   reproduced **all three failing numbers exactly**: `520/400`, `1100/600`, `260/100`.
   `route-attack-knownbad-control.txt`.

So the accident bought something the round could not otherwise have had: **the running-app check is
demonstrated to FIRE against a known-bad build** (DEVELOPMENT_METHOD.md 6c item 3). A route attack
that has only ever been run against the state you hope passes proves nothing; this one has been run
against a build that fails it, and it failed it.

Two further instrument faults were caught by assertions rather than by luck, and both are recorded:
a PowerShell parameter named `$armed` silently shadowed the `$ARMED` config here-string (PowerShell
variables are case-insensitive), so one AC9 run wrote the literal text `True` into `config.json`; the
`PreviewArmed`/`state=recording` assertions caught it and the run was redone
(`ac9-pair1-preview.txt` shows the three refusals). A `dotnet build` that fails because the running
app holds `AgentEyesApp.exe` leaves the previous binary in place while reporting `2 Error(s)`; every
build behind a number in this report is a checked `Build succeeded`.

---

## 3. AC7 - the criterion that failed three times

### 3a. Nine routes attacked, not the three known instances

The point of round 4 is that the CLASS is closed, so QA attacked routes nobody has been burned by
yet. All of these ran hands-off inside live recordings, on the pinned binary
(`route-attack.txt`, section A):

| route | expected | observed |
|---|---|---|
| plain toggle on, resize nothing (round 2's route) | no size | `"HudWidth": null, "HudHeight": null` |
| HUD **CONSTRUCTED** with the preview already on (round 3's defect) | opens 520x400, no size | `520x400`, `IMAGE rect=1553,113,490,276`, `null / null` |
| four toggles off/on inside one recording | no size | `null / null` |
| moved across all three monitors | no size | `null / null`, position tracked |
| toggled off and on WHILE being moved | no size | `null / null` |
| minimise and restore with the panel up | no size | `IsIconic=True` then back at `520x400`, `null / null` |
| `WM_DPICHANGED` posted to the window | no size | `null / null` |
| **a bare `SetWindowPos` to 1100x600 - the general shape of every layout-driven resize** | no size, even though the window really takes it | window really became `1100x600`, `IMAGE rect=901,482,900,506`, config `null / null` |
| three further stop/start cycles, and the HUD CLOSED (`WM_CLOSE`) with the panel open | no size | `null / null` throughout |

The bare-`SetWindowPos` row is the one that matters: it is what a DPI change, a monitor change, a
restore and a future panel's own layout all reduce to, and it is what UI Automation itself did before
this round. It is not remembered.

**The clamp drift is gone.** Seeded `HudWidth 200 / HudHeight 100` with the preview on, the window
opened at `260x100` (MinWidth clamps it) and a hands-off recording left config **`200 / 100`
unchanged** - round 3 rewrote it to `260`.

### 3b. AC7 end to end

```
toggle ON:               520x400 at 1537,16
move to 940,340:         X=940 Y=340 W=520  H=400
UIA resize to 1560x400:  CanResize=True -> X=940 Y=340 W=1560 H=400     (3.0x DefaultPreviewWidth=520)
stop
config:                  "HudLeft": 940, "HudTop": 340, "HudWidth": 1560, "HudHeight": 400
-- a NEW recording --
HUD as opened:           X=940 Y=340 W=1560 H=400        <- the size AND the position
preview image:           rect=1449,422,544,306           <- scaled: 490x276 at the default -> 544x306
config unchanged:        1560 / 400
```

### 3c. The three gestures must still WORK - an inversion that forgets a real resize fails AC7 too

| gesture | verified | evidence |
|---|---|---|
| **the sizing border** (Win32 resize-modal loop) | **YES, in the running app** | driven by posting `WM_SYSCOMMAND SC_SIZE` + arrow keys to the HUD's own queue (no global input synthesized, nothing force-foregrounded): `520x400 -> 1191x400`, log `hud: resized by the person via the sizing border to 1191x400`, config `"HudWidth": 1191`. `route-attack.txt` section D |
| **UI Automation TransformPattern** | **YES, in the running app** | `CanResize=True`, resize lands, log `hud: resized by the person via UI Automation to 1560x400`, config `1560 / 400`. Section 3b |
| **the panel's resize grip** | **PARTLY - stated as a limit, not waved through** | see below |

**The grip's honest limit.** The chain is verified in two of its three links and the third is a WPF
platform behaviour:
- `HudWindow::.ctor -> HudUserResize::ByGrip` is present **in the compiled IL**
  (`HudWindow_WiresUpAllThreeGestures`), and removing the `grip.DragDelta +=` line turns that guard
  RED (QA mutation QM13, `qa-guard-names.txt`).
- `ByGrip` really records, against a REAL WPF window through the production code
  (`DraggingTheGrip_IsRemembered`: `520+140 x 400+60`); making `ByGrip` stop recording turns
  `Record_IsOnlyEverReachedFromAPositivelyIdentifiedGesture` RED (QM6).
- **NOT verified:** WPF raising `Thumb.DragDelta` from a physical mouse drag on the grip in the
  running app. The grip exposes only `SynchronizedInputPattern` to UI Automation (measured - no
  `TransformPattern`, so `ThumbAutomationPeer`'s move route is not available here), and posting
  `WM_LBUTTONDOWN`/`WM_MOUSEMOVE`/`WM_LBUTTONUP` at the grip's client point did not reach WPF's input
  pipeline (the window did not resize, and nothing was recorded - a null result, counted as neither
  pass nor fail). Synthesizing global mouse input is forbidden by this repo's own instructions
  without warning the human first, so QA did not.

AC7's text does not name a gesture; it requires the HUD to be resizable to 3x its default with the
preview scaling, and to come back where it was left. That is verified end to end through two real
gestures. The grip's last link is recorded above as what this round could not see.

---

## 4. The four IL guards - each shown to FAIL when violated

A guard that cannot fail is not a guard. QA broke each decision, rebuilt, and named the test that
went red (`qa-guard-names.py` / `.txt`):

| QA mutation | guard that turned RED |
|---|---|
| QM3 - a `SizeChanged` subscription added to the sizing code (round 3's mechanism) | `TheSizingCodeDoesNotSubscribeToLayoutOrSizeChanges` **+ 6 behavioural tests** (10 red of 14) |
| QM5 - a second writer to the memory, from `SavePosition` | `RecordUserResize_IsOnlyEverCalledByHudUserResize` |
| QM6 - `ByGrip` stops recording | `Record_IsOnlyEverReachedFromAPositivelyIdentifiedGesture` |
| QM4 - the `OnCreateAutomationPeer` override removed | `HudWindow_WiresUpAllThreeGestures` |
| QM13 - the `grip.DragDelta` wiring removed | `HudWindow_WiresUpAllThreeGestures` |
| QM7 - `HudWindow` constructs a SECOND `HudSizeMemory` | `HudWindow_ConstructsExactlyOneSizeMemory` |

All five structural guards fire. QM7's guard closes the blind spot QA named in round 3 (two call
sites sharing one memory instance was correct-but-undefended); it is now defended by a test.

**QA's full sweep: 13 of 13 mutations FIRED, 0 SILENT, 0 DID-NOT-APPLY** (`qa-mutation-round4.txt`),
written by QA rather than reused from the developer, and the tree verified GREEN (57/57) after the
restore.

### The developer's claims, checked rather than accepted

- **"1068 tests green"** - TRUE, re-run by QA (`gate.txt`).
- **"32 of 32 mutations FIRED"** - TRUE, re-run by QA: 32 FIRED, 0 SILENT, 0 DID-NOT-APPLY
  (`dev-mutation-sweep-rerun-by-qa.txt`). **M19 and M20, which the developer honestly flagged as
  having reported "MUTATION DID NOT APPLY" on a first run and re-aimed, genuinely fire now**: M19
  `Failed: 4`, M20 `Failed: 2`.
- **"round 3's mechanism turns 17 red"** - TRUE, re-run by QA: `Failed: 17, Passed: 40`, with BOTH of
  QA's round-3 reproductions among them
  (`ShowPanel_FromTheConstructorBeforeTheWindowIsShown_RemembersNothing`,
  `AHandsOffRecording_WithARememberedSizeTheWindowCannotTake_ChangesNothing`).
- **"the sizing-mode must be read at the START of the gesture"** - TRUE and load-bearing. QA's QM2
  (read it at the END) turns `DraggingThePillsBorderWhileThePanelIsDown_IsNotAPanelSize` red. Without
  it, dragging the PILL's border would store the pill's dimensions as the preview panel's size.
- **"zero files under `src/AgentEyes.Core/` are touched"** - TRUE
  (`git diff --name-only 849ac58..HEAD | grep -c "src/AgentEyes.Core/"` = 0). **Recorded as a fact,
  NOT as a pass**: AC2-AC6 and AC8-AC11 were re-exercised in the running app this round anyway,
  because a structural argument is not evidence.

---

## 5. AC9 - both numbers, and what the metric cannot see

Two paired 60 s runs (preset `Demo Screen Capture With Camera`, monitor 1, camera + mic, identical
changing content in both arms), alternated so machine drift hits both:

```
                     screenDrops   deliveredFrames   screenDur   cameraDur   delta
  control-1               4             1022          69.067      69.600     0.533
  preview-1               4             1022          69.067      69.600     0.533
  control-2               5             1022          69.067      69.600     0.533
  preview-2               5             1014          68.533      69.100     0.567
```

The two arms are confirmed distinct by their artefacts, not by the label: the control manifests carry
no `PreviewOverlayCorner` and no `pipe:1` in the ffmpeg command line; the preview manifests carry
both, and the files differ in size (4,990,645 vs 4,995,041 bytes).

On the literal metric AC9 names - `drop=` in the ffmpeg log - **the preview arm is not higher than the
control in either pair** (4 vs 4, 5 vs 5), and every run meets #28's 1.0 s duration limit.

**Stated limit** (round 1 found the same thing over five pairs): `drop=` is a coarse instrument on
this machine, and two pairs cannot resolve a small regression. This pass rests on the criterion's own
comparison plus delivered frames and the duration limit, all of which are reported above in full. It
is a statement of what the check cannot see, not a relaxation of the criterion.

---

## 6. Diff review

- **Design.** The inversion is real and structural, not a fourth patch. `HudSizeMemory` has one
  mutator (`HudSizeMemory.cs:100`); its only caller is `HudUserResize.Record`
  (`HudUserResize.cs:196-203`); `Record`'s only callers are the three gesture entry points
  (`OnWindowMessage` 116-138, `ByGrip` 150-156, `ByAutomation` 164-172). `HudPreviewSizing.ShowPanel`
  is a pure read of the memory (`HudPreviewSizing.cs:46-64`). Nothing in the three sizing classes
  subscribes to layout. The only `SizeChanged` left in the App is
  `_previewSurface.SizeChanged += (_, e) => LayOutInset(...)` (`HudWindow.cs:232`), which lays out the
  camera inset and cannot write a size - the `RecordUserResize` guard makes that structural rather
  than a promise, and the guard's scope note says so explicitly.
- **`HudWindowAutomationPeer`** (`HudUserResize.cs:221-262`) advertises only `Transform` and defers
  everything else to the base peer; `CanResize` matches what the default HWND provider reported, so
  no UI Automation client sees the HUD become less capable. Confirmed live: `gui-smoke`-style UIA
  discovery of the HUD's buttons, text and images works throughout this report.
- **`Record`'s narrowing** is genuinely a narrowing: it can only suppress, never authorise. QM8
  (dropping it) turns `ADragWhileTheWindowIsAutoSized_RecordsNothing` and
  `DraggingThePillsBorderWhileThePanelIsDown_IsNotAPanelSize` red.
- **CLAUDE.md standards.** ASCII-only (the round-4 source diff contains no byte outside 0x20-0x7E);
  logging on every state change (`hud: watching for user resizes`, `preview panel opening at WxH`,
  `preview panel down ... remembering X`, `resized by the person via ...`, `UI Automation move`) - all
  of it load-bearing for this report; no try/catch outside entry points in the three sizing classes
  (there are none at all); no fallbacks - `Watch()` throws with a specific message if the HWND has no
  `HwndSource` rather than degrading; `PreferredSize` throws on a non-positive default.
- **Deliberately not logged**: `ByGrip` does not log per call, because `Thumb.DragDelta` fires once
  per mouse move. The size that survives is logged by `SavePosition`. QA judged this correct and
  notes the consequence for future debugging: a grip resize is the ONE gesture with no
  `resized by the person` line, which is exactly what made section 2's diagnosis take as long as it
  did.
- **Privacy posture / CenCon.** `ApplyWindowStyles` and `WDA_EXCLUDEFROMCAPTURE` are untouched by the
  round-4 diff (the only occurrence is an unchanged context line), and AC6 proves the exclusion is
  live. `HudUserResize` is a new class inside the existing App component; no component-map drift.

---

## 7. Recorded, and NOT counted against any criterion

- **The first toggle of a fresh config shows `IMAGE: not found` in the UIA tree.** Present since
  round 1, unchanged by this diff, and explained by the issue's own assumption C1: ffmpeg's outputs
  are fixed at process start, so a recording begun with the preview off cannot grow a feed. The panel
  says so in plain language and `/status` reads `PreviewArmed=true` for the next recording. Flagged
  for Product as a UX call, as in round 1.
- **Escape-cancelling a border drag** ends the modal loop with `WM_EXITSIZEMOVE` after at least one
  `WM_SIZING`, so the window's (restored) current size is recorded. The value written equals the size
  the window already had, so nothing the person did not choose is stored. Noted, not a defect.
- `camera.mp4.ffmpeg.log` carries no `drop=` counter at all, so the camera-drop column reads `-1`
  throughout. That is the log's shape, not a measurement.

---

## 8. Environment

Restored after the run: `config.json` and `presets.json` restored byte-for-byte from
`%TEMP%\claude\qa33r4-backup` and verified by SHA256; every recording this session created deleted
from `%USERPROFILE%\Videos\AgentEyes\` (other sessions' recordings untouched); the preview directory
recreated by the app; the installed **v1.6.2** tray app restarted from
`%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray` and verified idle on `/status` - it had been
stopped (pid 26416) so this verification could use port 7882. `MQS_HUD_CAPTURABLE` was set only
inside two QA child processes and never persisted. The QA worktree `D:\ReposFred\agenteyes-qa33-r4`
is removed. Branch `issue-28-camera-failure-boundaries` and the worktrees belonging to other sessions
were not touched.

---

## 9. Artefacts committed beside this report

| file | what it is |
|------|-----------|
| `gate.txt` | the byte-clean tree, the build, 1068 tests, and the DLL SHA256 every running-app number came from |
| `route-attack.txt` | the nine-route attack on the pristine binary - all `null / null` |
| `route-attack-repeat.txt` | the same attack, repeated - the run is reproducible |
| `route-attack-knownbad-control.txt` | the SAME attack against a build with round 3's mechanism restored: it FIRES, reproducing 520/400, 1100/600 and 260/100 |
| `ac2-ac3-ac4-ac5.txt` | liveness, the camera-device inventory, the four corner rects, the manifest corner |
| `ac6-no-hud-in-the-output.txt` + `img/ac6-*.png` | the cropped output frames, excluded and known-bad |
| `ac8-and-the-ac2-failing-arm.txt` | AC8, plus the static-screen control that makes AC2 discriminating |
| `ac10-ac11.txt`, `ac10-readable-error.txt`, `img/ac10-*.png` | the killed preview, and the panel's readable error |
| `ac9-pair1.txt`, `ac9-pair1-preview.txt`, `ac9-pair2.txt` | both AC9 pairs, and the three instrument refusals |
| `qa-mutation-round4.py` / `.txt` | QA's own 13 mutations: 13 FIRED, 0 SILENT |
| `qa-guard-names.py` / `.txt` | which named guard turns red for each structural mutation |
| `dev-mutation-sweep-rerun-by-qa.txt` | the developer's 32, re-run by QA |
| `qa33r4-*.ps1.txt`, `qa33r4-churn.cs.txt` | every driver used, committed so the run is repeatable |

---

**VERDICT: PASS. 12 of 12 acceptance criteria verified with running-app evidence, the round-4
inversion confirmed to close the defect class on nine routes rather than three instances, all five
structural guards demonstrated to fail when violated, and the running-app check itself demonstrated
to fire against a known-bad build. `flow:ready-gate` - the Review Gate decides the merge (D7). QA
does not merge.**
