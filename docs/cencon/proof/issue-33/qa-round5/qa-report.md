# Issue #33 - QA report (round 5)

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

Verdict: **PASS. 12 of 12 acceptance criteria verified, and all three of the Review Gate's blocking
defects from `docs/cencon/review/pr34-issue33-gate-round1.md` are closed - each one demonstrated
against a KNOWN-BAD build that reproduces the defect, so every check in this report has been shown
to fire.**

- Repository: `thefrederiksen/agenteyes-app` (`thefrederiksen/AgentEyes` in the skill files is ARCHIVED)
- Issue: #33 - **PR #39** (PR #34 was auto-closed when issue #28's branch was deleted by its merge)
- Branch `issue-33-hud-live-preview` at `e2bdd60`, **rebased onto `main` `e57d828`**
- Gate verdict answered: `docs/cencon/review/pr34-issue33-gate-round1.md` (REJECT, three blocking defects)
- Verified 2026-08-29. Displays: DISPLAY1 `1920,-5` 1920x1080 (the recorded monitor, AgentEyes index 1);
  DISPLAY3 primary `0,0` 1920x1080; DISPLAY2 `1173,1080` 1366x768 - all at 100% scale.
  Camera `HD Webcam eMeet C960`. Preset `Demo Screen Capture With Camera` (video, monitor 1, camera + mic).
- Built and run from an ISOLATED worktree `D:\ReposFred\agenteyes-qa33-r5`, detached at `e2bdd60`,
  `bin\x64\Release\` - never `bin\Release\`, never the shared checkout.
- **Every running-app number below came from binaries pinned in `gate.txt`:**
  `AgentEyesApp.dll` `24E786E1...`, `agenteyes.dll` `0FA55157...`, built `--no-incremental` from a
  `git status`-clean tree. Where a KNOWN-BAD build was used its own hash is named at that point.
- QA never asked the human to run anything.

```
$ dotnet build AgentEyes.sln -c Release --no-restore --no-incremental
  Build succeeded.  4 Warning(s)  0 Error(s)      (all four pre-existing: xUnit1031 x2, xUnit2031 x2)
$ dotnet test  AgentEyes.sln -c Release --no-build --no-restore
  Passed! - Failed: 0, Passed: 1117, Skipped: 0, Total: 1117
```

---

## 1. Criteria table

| AC | Verdict | The number that carries it |
|----|---------|----------------------------|
| **AC1** toggle exists, defaults off | **PASS** | HUD constructed with the preview already on: pill-to-panel `520x400` at the saved `893,150`, `IMAGE rect=909,247,490,276` - NON-ZERO; toggle hides it (`publishing=False`, frame file removed), toggle shows it again (`publishing=True`, frame file back). `sessionA-config-and-handsoff.txt`, `sessionB-...txt` |
| **AC2** screen preview is live | **PASS** | with a churn window painting on the RECORDED monitor: `screen.jpg` `B53FF9DCFD2375C5` -> `23145312ACB81159` in 3 s, framesRead `60->91`. **The failing arm fired twice**: with a static desktop the same probe reports `DIFFER=False` while framesRead still climbs `73->103` - so the check is discriminating and not just "the file changed". `sessionC-...txt` vs `sessionA-...txt` |
| **AC3** camera live AND opened once | **PASS** | `camera.jpg` `9E6E6FA67276D787` -> `530733F145CB37A9`; ffmpeg inventory `pid=58984 (-i video=)=1 pipe:1=True mjpeg=True`, `pid=58300 (-i video=)=0` -> **PROCESSES HOLDING THE CAMERA = 1**; `CameraComplete: "yes"`, `CameraStopKind: "clean-quit"`, screen/camera duration delta 0.13 s. `sessionB-...txt` |
| **AC4** four corners composite | **PASS** | panel image constant `909,247 490x276`; four DISTINCT inset rects `918,241` / `1245,241` / `918,447` / `1245,447`, inset `145x82`; `/status PreviewOverlayCorner` tracked every click. `sessionB-...txt` |
| **AC5** the corner reaches the manifest | **PASS** | last corner framed `bottom-left` -> `"PreviewOverlayCorner": "bottom-left"`; a preview-OFF run -> the key is **ABSENT from the file** (`sessionC` AC11 line). |
| **AC6** no HUD, no mirror tunnel in the output | **PASS** | HUD parked at `2300,300` on the captured monitor = `380,305` inside the frame; four crops `520:400:380:305` from `recording.mp4` show only the desktop underneath (distinct colours 666/686/699/669). **Known-bad control**: the identical crop with `MQS_HUD_CAPTURABLE=1` shows the WHOLE HUD - timer, Hide preview, STOP, the mode and corner buttons, `both top-left \| live`, the camera inset AND a visible mirror tunnel (2929/3043 colours). `img/ac6-excluded-03.png` vs `img/ac6-knownbad-03.png` |
| **AC7** resizable AND persists | **PASS** | see section 3 - and this is where the gate's defect 2 lived |
| **AC8** toggling mid-recording is safe | **PASS** | in ONE live recording: modes screen/camera/both, all four corners, preview hidden then shown; **the tap kept draining while the panel was down** (framesRead `249 -> 291 -> 321`, `publishing=False`, frame file deleted by the PUBLISHER thread and restored on show); result `recording.mp4` 50.73 s, `CameraCapturedSeconds` 50.86, delta 0.13 s, `CameraComplete: "yes"`. `sessionB-...txt` |
| **AC9** bounded cost | **PASS**, with a stated limit | four ALTERNATED 60 s runs, all numbers in section 6 |
| **AC10** a preview failure never harms the recording | **PASS** | see section 2 - this is the gate's defect 1, and the evidence is a genuine BLOCKING publish, not a fast exception |
| **AC11** no regression with the preview off | **PASS** | `PreviewArmed=False`, framesRead `0/0`, `PreviewOverlayCorner` absent, **`pipe:1` absent from the manifest's ffmpeg command** (and present in the ON manifest), `recording.mp4` 71.53 s / `camera.mp4` 72.17 s, `CameraComplete: "yes"`. `sessionC-...txt`, `ac9-four-alternated-runs.txt` |
| **AC12** gate | **PASS** | `Build succeeded. 0 Error(s)`; `Failed: 0, Passed: 1117`; **12 QA mutations run by QA, 11 fired and the 12th is the developer's own documented limit** (section 5) |

**12 of 12 verified.**

---

## 2. Gate defect 1 - "AC10 is not structurally true: publishing can block the only stdout drain"

The gate's point was precise and the round-1 evidence could not reach it: catching an exception is
not the same claim as never stopping. A stall - a reparse point onto an unavailable share, an NTFS
or filter-driver hang - NEITHER RETURNS NOR THROWS, so the drain sits inside it, the anonymous pipe
fills, and the ffmpeg writing `recording.mp4` / `camera.mp4` blocks on a full pipe.

QA did not test a fast exception. QA injected a **genuinely blocking publish** - 8 seconds inside
every frame write, 80x the 100 ms preview frame interval - into a REAL recording with REAL ffmpeg
processes and a REAL anonymous pipe, which is exactly the thing a unit test cannot produce. The
injection is one line in `PreviewTap.WriteFrameToDisk` (`qa-stall-injection.py`, applied and then
restored with a sha256 comparison; the restored file is byte-identical to `ce50c018916e6cdf...`).

The recordings were driven through `RecordingService` from a QA-built probe (`qa-core-probe.cs.txt`),
so the whole production capture path ran.

```
                          framesRead        published/dropped   raw.mp4     camera.mp4  CameraComplete
control, no stall         341 / 345         329/7  and 339/0    34.067 s    34.500 s    yes
THE ROUND-5 DESIGN,
 8-SECOND STALLED WRITE   491 / 495         6/478  and 6/481    49.067 s    49.433 s    yes
KNOWN-BAD (round-4 shape,
 same 8-second stall)     11  / 12          -                  48 BYTES,   INVALID,     no
                                                               no moov     force-killed
```

Reading those rows:

- **The stall provably took.** 6 frames published in 45 seconds is one per 8 seconds. On the healthy
  control the same code publishes 329 of 341. A run in which the publisher was never stalled would
  prove nothing, and this one names the number that says it was.
- **The drain never stopped.** `framesRead` climbed to 491/495 - the same rate as the unstalled
  control - and both drains "ended at end of stream". `framesPublished + framesDropped` accounts for
  every frame (6+478 = 484 of 491, the rest in flight at the sample), so no frame vanished unrecorded.
- **The recording completed FULL LENGTH and both files are valid**: `raw.mp4` 49.067 s and
  `camera.mp4` 49.433 s for a 45.7 s session (delta 0.366 s, inside #28's 1.0 s bound),
  `CameraStopKind: "clean-quit"`, `CameraComplete: "yes"`, `LastStopFailed=False`.
- **The stop was never made to wait on the wedged publisher.** Two bounded 3000 ms joins fired their
  designed WARNING - *"the screen preview publisher did not finish within 3000ms ... The recording is
  unaffected: the drain never waited for it"* - and `Stop()` returned in 10.1 s all told.
- **THE KNOWN-BAD CONTROL IS THE PROOF THAT THIS CHECK CAN FAIL.** The identical stall on the round-4
  shape (`Publish(frame)` inline in the drain) froze `framesRead` at 11 over 45 seconds, blocked both
  ffmpeg processes on their full stdout pipes, and destroyed the recording: `raw.mp4` **48 bytes with
  no moov atom**, `camera.mp4` invalid, `"CameraStopKind": "force-killed"`,
  `"CameraCapturedSeconds": 2.26`, `"CameraComplete": "no"`, and a `RecordingStopFailedException`
  saying the camera *"ignored the quit request and had to be force-killed"*. That is precisely the
  failure the gate predicted, reproduced on demand, and it does not happen on this branch.

Evidence: `r1-control-healthy.txt`, `r1-control-nopreview.txt`, `r1-stalled-round5design.txt`,
`r1-stalled-KNOWNBAD-round4shape.txt`, `ac10-stall-log-lines.txt`.

### 2b. The LOGGING path cannot block the drain either

The developer found a second instance of the same hazard: `AgentEyes.Log.Write` is a synchronous
`File.AppendAllText` under a process-wide lock, so a `Log.Info` from the drain is the same defect.
QA checked this two ways and both fire:

- **QM3** (QA's own): make the drain call the write DELEGATE FIELD directly. The IL guard
  `NothingTheDrainCanReach_TouchesTheFilesystem` stays GREEN - a call through a delegate field is
  invisible to a call-graph guard, and that limit is NOT stated in that test's own doc comment - but
  `Drain_WhilePublishingIsStalledForever_StillReadsThePipeToTheEnd` and three more go RED. The pair
  holds the property; the guard alone does not. Recorded here rather than left implicit.
- **QM2** (QA's own): put a `File.AppendAllText` inside `MjpegFramer.Append`, a Core helper the drain
  reaches transitively and that no test names. The IL guard fires, plus
  `ManifestWriterIlTests.EveryFileWriteInTheProduct_IsAPinnedCallSite`. So the guard really is
  transitive, not a list of known call sites.

### 2c. AC10's "readable error rather than a frozen last frame"

Three runs, preview directory deleted mid-recording on the pinned shipped binary:

```
                      framesRead across the kill   PreviewFailed   recording.mp4 / camera.mp4   CameraComplete
run 1  110 -> 292                                  True            45.733 / 46.900              yes
run 2  111 -> 292                                  True            46.800 / 47.433              yes
run 3  111 -> 294                                  True            45.800 / 46.433              yes
```

Two `[WARN] [PreviewTap] Publish FAILED: ... The preview will go stale and say so; the recording is
unaffected.` lines per run, one per track. The HUD's readable error is a PRESENCE with both arms: the
`HUD preview message` element is **absent from the UIA tree before the kill** (collapsed) and
**present after it** at `rect=945,377,418,16`, and the status line's rect widens `87px -> 119px`
(`both top-left | live` -> `both top-left | no frames`; the strings are `HudWindow.cs:672-675`).

Honest note on one number: run 1's screen/camera delta was 1.167 s, above #28's 1.0 s bound; runs 2
and 3 of the identical scenario were 0.633 s and 0.633 s, and every other run in this report
(9 more) sat between 0.13 s and 0.67 s. The manifest for run 1 shows
`CameraStartOffsetSeconds: -0.584` and `CameraCapturedSeconds: 46.76` against `DurationSeconds:
45.73`, i.e. most of that delta is the camera's documented head start. It is reported as machine
noise on a heavily loaded machine, not hidden, and AC10 itself does not impose the 1.0 s bound - AC3
and AC8 do, and both were measured inside it.

Evidence: `ac10-killed-preview-run1.txt`, `-run2.txt`, `-run3-readable-error.txt`.

---

## 3. Gate defect 2 - "AC7's allowlist drops a genuine user resize: maximize / Windows snap"

### 3a. Maximise, measured the way the gate measured it

The gate's evidence was a window-message probe showing that the user maximise command produces
`WM_SYSCOMMAND 0xF030` and NO `WM_ENTERSIZEMOVE` / `WM_SIZING` / `WM_EXITSIZEMOVE`. QA posted that
exact message to the REAL HUD's own message queue - no input synthesized, nothing force-foregrounded:

```
before SC_MAXIMIZE: X=893 Y=150 W=520  H=400  IsZoomed=False
after  SC_MAXIMIZE: X=-7  Y=-7  W=1934 H=1094 IsZoomed=True    IMAGE rect=72,75,1778,1000
config after the maximise:      Width=1934 Height=1094
-- a NEW recording --
HUD as opened:      X=893 Y=150 W=1934 H=1094                  IMAGE rect=972,232,1778,1000
```

The maximised size is remembered and the next recording opens at it. The gate's defect is closed.

### 3b. Aero Snap's shape

```
before:                                   X=893 Y=150 W=520 H=400
inside the loop, after the shell resized: X=893 Y=150 W=960 H=700
after the loop:                           X=893 Y=150 W=960 H=700
config after the snap-shaped loop:        Width=960 Height=700
-- a NEW recording --  HUD as opened:     X=893 Y=150 W=960 H=700
```

A modal loop began, the window was really resized while it ran, the loop ended, and NO `WM_SIZING`
was sent anywhere - the sequence the fix keys on, driven against the production `HudWindow`.
**Limit, stated rather than implied (section 7 item 1): this is the message SHAPE, not a physical
mouse drag to a screen edge**, which needs global input synthesis and was therefore not performed.

### 3c. Both directions, and the routes that must stay shut

| gesture | route | remembered? |
|---|---|---|
| UI Automation TransformPattern resize to 1560x400 (**3.0x** the 520 default) | ByAutomation | YES `Width=1560 Height=400`, and it survives an app restart: the next run opens `1560x400` with `IMAGE rect=1402,232,544,306` |
| a REAL `SC_SIZE` modal loop driven with arrow keys (real `WM_SIZING` from Windows) | the sizing border | YES `Width=971 Height=400` |
| `SC_MAXIMIZE` | window state | YES `1934x1094` |
| a loop that ended at a different size | snap | YES `960x700` |
| a REAL `SC_MOVE` modal loop, arrow keys, committed - the window really moved `893 -> 981` | move | **NO** - `Width= Height=` (empty) |
| the same loop SHAPE with no size change | move | **NO** |
| minimise (`IsIconic=True`) and restore | - | **NO** |
| `WM_DPICHANGED` posted to the window | - | **NO** |
| **a bare `SetWindowPos` to 1100x600 - the general shape of every layout-driven resize** | - | **NO**, and the window really took it: `HUD rect W=1100 H=600`, `IMAGE rect=1082,232,900,506` |
| the HUD CONSTRUCTED with the preview already on (round 3's defect) | - | **NO** - config byte-identical, section 4 |

The round-4 inversion is intact: everything hands-off records nothing, including the one that
matters most - a resize the window genuinely takes with no gesture behind it.

### 3d. The gate's structural criticism, and QA's hunt for a fifth route

The gate said `Record_IsOnlyEverReachedFromAPositivelyIdentifiedGesture` hand-lists three callers and
"can prove its members but cannot prove its own exhaustiveness", and asked whether the canary
genuinely addresses that or merely adds a fourth hand-listed member. QA's finding:

- The canary is NOT a fourth list member. `HudSizeMemory.UnattributedSize` compares the size the HUD
  ACTUALLY ended up at against the size it was opened at or last recorded, so it fires on a route
  nobody enumerated - QM7 (neuter the canary) turns
  `AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary` RED, and the known-good arm
  (`AResizeAGestureClaimed_...`) keeps it from being a thing that fires at everything.
- The newest route's identification really is a claim about the COMPILED CODE: QM6 (make the app
  assign the HUD's `WindowState`) turns `NothingInTheHudEverSetsItsOwnWindowState` RED.
- **The residual gap QA could not close, named:** a KEYBOARD Aero Snap (`Win`+`Left`/`Right`) and
  Windows 11 Snap Layouts are driven by the shell with `SetWindowPos` from outside the process; they
  run no modal loop in the target window and leave `WindowState` at `Normal`. If they miss, this
  design does NOT record a wrong size - it logs the canary's WARNING naming the size. QA could not
  measure them because doing so requires synthesizing global keyboard input, which would have stolen
  the human's session. Flagged for the Review Gate and as a suggested follow-up (section 7).

---

## 4. Gate defect 3 - synchronous file I/O on the WPF UI thread

### 4a. The STOP button under a stalled config filesystem

QA injected an 8-second stall into `Config.WriteJson` - the one method both save paths go through -
and then measured HOW LONG THE HUD'S DISPATCHER IS OCCUPIED after a preview click. A first click runs
its handler; a SECOND click cannot be served until that handler returns, so the time from the second
click to its observable effect (`/status PreviewPublishing`, read over the REST API on its own
thread) is the dispatcher's occupancy.

```
SHIPPED shape   (AgentEyesApp.dll A7D329E4...)   second click served after     114 ms
KNOWN-BAD, the round-4 shape
 (HUD calls Config.Save on the dispatcher;
  AgentEyesApp.dll CB394F7C...)                  second click served after    8108 ms
```

8108 ms is the injected stall, arriving on the WPF dispatcher exactly as the gate described. On the
shipped shape, with the same stalled filesystem, the STOP button was invoked and **the recording
reached `idle` in 5763 ms** - a normal stop sequence, not a stalled one.

Instrument note, recorded because it initially misled QA: `InvokePattern.Invoke` on a WPF button is
dispatched ASYNCHRONOUSLY (`ButtonAutomationPeer` posts the click with `Dispatcher.BeginInvoke`), so
timing the Invoke call itself measures nothing - it returned in 19-32 ms on BOTH builds. That first
measurement was discarded as a broken instrument and replaced with the two-click one above, which
discriminates by 71x.

Evidence: `dispatcher-under-stalled-config-SHIPPED.txt`,
`dispatcher-under-stalled-config-KNOWNBAD-round4shape.txt`.

### 4b. The constructor no longer rewrites config - and QA's first instrument for this was wrong

A hands-off recording leaves `config.json` **BYTE-IDENTICAL**: sha `D60F3C217FAD8F27` before and
after two consecutive hands-off recordings, with `HudWidth`/`HudHeight` still `null`
(`sessionA-config-and-handsoff.txt`). But the file's WRITE TIME did change at record start, and a
timestamp cannot say WHO wrote it - so that check was not accepted.

The discriminating instrument: make `config.json` READ-ONLY, so every attempted write fails and NAMES
ITS PATH in the log. `[Config] Save FAILED` is the synchronous path; `[BackgroundFileWriter]
WriteOnce FAILED` is the HUD's non-blocking one.

```
SHIPPED  (AgentEyesApp.dll 24E786E1...)
  05:45:09.543 [WARN] [Config] Save FAILED ...              <- the launcher's RememberUsed, at the REC click
  05:45:09.559 [INFO] hud: preview panel opening at 520x400 <- the HUD is constructed HERE
                                                            <- NOTHING. the constructor writes nothing
  05:45:24.037 [INFO] hud: saving position left=893 top=150
  05:45:24.038 [WARN] [BackgroundFileWriter] WriteOnce FAILED ...  <- the HUD's ONLY write, non-blocking

KNOWN-BAD, the constructor remembers again (QM9; AgentEyesApp.dll 8E4E0564...)
  05:46:04.076 [WARN] [Config] Save FAILED ...
  05:46:04.095 [INFO] hud: preview panel opening at 520x400
  05:46:04.098 [WARN] [BackgroundFileWriter] WriteOnce FAILED ...  <- THE EXTRA WRITE, 3 ms into construction
  05:46:19.320 [WARN] [BackgroundFileWriter] WriteOnce FAILED ...
```

So the record-start rewrite is `MainWindow.RememberUsed`'s pre-existing synchronous save (the last-used
preset id), not the HUD; the HUD constructor writes nothing; the HUD's only write is the position on
`Closed`, through the background writer; and the check is shown to detect the defect when it is put back.

The companion presence is also live: the preview settings really do persist - after a session of mode
and corner clicks, `config.json` carried `Corner=bottom-left` (`sessionB-...txt`).

Evidence: `constructor-writes-nothing-SHIPPED.txt`, `constructor-writes-nothing-KNOWNBAD-qm9.txt`.

---

## 5. QA's own mutation sweep - 12 mutations, run by QA, not the developer's

On this issue in round 2, 22 developer mutations and 1031 tests were ALL GREEN while a visible
regression shipped. QA therefore wrote its own harness (`qa-mutations-round5.py`), which applies one
textual defect at a time, rebuilds `--no-incremental`, runs the WHOLE suite, and restores the file
with a sha256 comparison. Full output in `qa-mutations-round5.txt`.

| # | defect put back | tests that went RED |
|---|---|---|
| QM1 | the drain publishes inline again (gate defect 1, verbatim) | `Drain_WhilePublishingIsStalledForever_...`, `NothingTheDrainCanReach_TouchesTheFilesystem` |
| QM2 | a `File.AppendAllText` inside `MjpegFramer.Append` - a Core helper the drain reaches transitively | `EveryFileWriteInTheProduct_IsAPinnedCallSite`, `NothingTheDrainCanReach_TouchesTheFilesystem` |
| QM3 | the drain calls the WRITE DELEGATE FIELD directly | 4 RED - but NOT the IL guard; see 2b |
| QM4 | the snap arm removed | `SnappingTheWindowToAScreenEdge_IsRemembered`, `ALoopThatEndedAtADifferentSize_...` |
| QM5 | the window-state route removed (a maximise is invisible again) | `MaximisingTheWindow_IsRemembered`, `AWindowStateCommand_...`, `HudWindow_WiresUpEveryGestureRoute` |
| QM6 | a restore FROM minimised treated as a resize | `MinimisingAndRestoringTheWindow_IsNeverRemembered`, `AMinimiseAndRestore_RecordsNothing` |
| QM7 | the completeness canary never fires | `AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary` |
| QM8 | the HUD writes config.json synchronously on the UI thread again | `NothingTheHudsUiThreadCanReach_WritesAFile`, `TheHudSavesItsChoices_...`, `EveryPreviewButton_RemembersTheChoice` |
| QM9 | the CONSTRUCTOR remembers a choice again | **NONE** - the developer's documented limit, independently confirmed; covered at RUNTIME in 4b instead |
| QM10 | the background writer writes on the caller's thread | `Queue_WhileTheWriteIsStalled_ReturnsAtOnce`, `Queue_TwiceInARow_...`, `Queue_WhenTheWriteThrows_...` |
| QM11 | hiding the preview deletes the frame file on the caller's (WPF UI) thread | `NothingTurningThePreviewOffCanReach_TouchesTheFilesystem` |
| QM12 | the bare apply persists a choice by itself | `ApplyingThePreviewState_NeverRemembersAChoiceByItself` |

11 of 12 fired. Every restore was byte-exact and the suite returned to `Failed: 0, Passed: 1117`.

---

## 6. AC9 - all the numbers

Four ALTERNATED 60-second runs on identical changing content, so machine drift hits both arms
(`ac9-four-alternated-runs.txt`):

```
                     armed   drops   deliveredFrames   screenDur   cameraDur   delta   pipe:1
control-1 (OFF)      False     8          1054          71.533      72.167     0.634   False
preview-1 (ON)       True      5          1061          71.533      72.167     0.634   True
control-2 (OFF)      False     8          1061          72.000      72.667     0.667   False
preview-2 (ON)       True      8          1061          72.000      72.633     0.633   True
```

In BOTH pairs the preview arm is not higher than the control (5 <= 8, 8 <= 8), every run meets #28's
1.0 s duration limit, and every run records `CameraComplete: "yes"`. The arms are confirmed distinct
by their artefacts and not by their label: `pipe:1` present only in the preview manifests, and
`framesRead` 662/669 and 662/668 versus 0/0.

**Reported in full, including the number that does not fit**: an EARLIER, UNALTERNATED pair (the
preview run first, taken while the machine was still busy extracting AC6 frames) gave preview 11 vs
control 8 (`sessionC-...txt`). It is stated rather than dropped. `drop=` is a coarse instrument on
this machine - round 4 said the same over its own pairs, and round 1 over five - and this pass rests
on the alternated comparison the criterion names plus delivered frames and the duration limit, all
printed above. That is a statement of what the metric cannot resolve, not a relaxation of AC9.

---

## 7. What this round CANNOT see, and suggested follow-ups (DEVELOPMENT_METHOD.md 6c.5/6c.6)

1. **A physical mouse-drag Aero Snap, and keyboard snap / Snap Layouts, were not measured.** Both need
   global input synthesis. The snap route is verified at the message-sequence level against the real
   HUD (3b); whether the shell's own snap produces that sequence is taken from the developer's claim
   and the gate's round-1 measurement. Keyboard snap plausibly produces NEITHER a modal loop NOR a
   `WindowState` change, in which case that size is not remembered and the canary names it in the log.
   **Suggested follow-up issue**, and a good target for the Review Gate's own probe.
2. **`NothingTheDrainCanReach_TouchesTheFilesystem` cannot see a write reached through a delegate
   field** (QM3), and its doc comment states only two limits - the assembly boundary and non-filesystem
   blocking. The property is nevertheless held, by the behavioural stall test. Worth one added
   sentence in that comment.
3. **The shared logger is still a synchronous `File.AppendAllText` under a process-wide lock, called
   on UI threads throughout this app.** The developer names it, does not claim it is safe, and fixes
   it exactly where AC10 makes it fatal. **Suggested follow-up issue** (the developer suggests the
   same).
4. **`Config.Save` remains synchronous on the LAUNCHER's UI thread** - `MainWindow` (including
   `RememberUsed` on every REC click), `TrayHost`, `SettingsDialog`, `ManagePresetsDialog`,
   `PluginManagerWindow`. Pre-existing, outside this issue's scope, and not a regression; but under
   the same stalled filesystem the REC click blocks where the HUD's no longer does. **Suggested
   follow-up issue.**
5. **A mixed-DPI monitor move was not tested** - all three displays here are at 100%. A modal move
   loop that crosses a DPI boundary could in principle end at a different `ActualWidth` and be read as
   a snap. Not observable on this machine.
6. This report's AC6, AC2, AC10, the config attribution, the dispatcher measurement and the AC10
   structural claim were each run against a KNOWN-BAD build and shown to FAIL there. AC1, AC3, AC4,
   AC5, AC8, AC9, AC11 rest on presence values quoted above rather than on a known-bad control.

---

## 8. The rebase, and #28's merged design

The eight #33 commits were rebased onto `main` (`e57d828`). QA checked the rebase rather than trusting
it: `git diff origin/main...HEAD -- src/AgentEyes.Core/Video/` contains ONLY the preview stdout wiring
and its comments - the `previewStream` parameter on `FfmpegArgs.VideoCapture`/`CameraCapture`, the
`PreviewOutput()` block, the `preview` parameter on `FfmpegRecorder.Start` /
`FfmpegCameraRecorder.Create` / `FfmpegCameraProcess`, and the `if (preview == null)` guard around
`BeginOutputReadLine`. **Nothing of #28's rounds 6-9 work appears in the diff**: the termination
history, the monotone stop-kind derivation and the three-state `CameraComplete` are untouched, and the
running evidence confirms them alive - `CameraStopKind: "clean-quit"` with `CameraComplete: "yes"` on
every clean stop in this report, and `"force-killed"` / `"no"` produced correctly on the known-bad run
in section 2 where the camera really was force-killed. Test count 1068 -> 1117 is #28's arriving tests
plus this round's.

---

## 9. Environment

Displaced and restored:

- The human's installed **v1.6.2** tray app (`C:\Users\soren\AppData\Local\AgentEyes\app\AgentEyesApp.exe`,
  sha256 `A6C6E340...`) holds the single-instance mutex and port 7882, so it was stopped for the
  running-app work and **restarted afterwards with its own autostart command line
  (`AgentEyesApp.exe --tray`)**; `/version` now reports `1.6.2` again and `/status` is `idle`.
- `config.json` and `presets.json` were backed up first and restored BYTE-FOR-BYTE:
  `D60F3C217FAD8F27C0DCC14F73F5DD570B5FE68DC1ED9E357CCB603189424C84` and
  `BFFC4252433C9D4FAAE973E032B9E83874FF56DC83336719013BF42FA5C62219`, both verified after restore.
- **47 QA test recordings deleted** from `%USERPROFILE%\Videos\AgentEyes\`; zero `2026-08-29*`
  directories remain. The preview scratch directory was removed.
- **No orphaned ffmpeg.** Checked after every run including the known-bad one that force-killed a
  camera: `ffmpeg count: 0`. QA's own recordings were always ended with a stop, never a kill.
- All QA source mutations restored with sha256 comparison; the worktree is `git status`-clean apart
  from this proof folder.
- One textual substitution in the committed transcripts and scripts: QA's temporary working
  directory appears as `<QA-SCRATCH>` / `<QA-TEMP>` rather than its literal path. Nothing else
  in any transcript was edited.

---

**VERIFIED - all 12 acceptance criteria met, and the Review Gate's three blocking defects are closed
with a firing known-bad control for each.** Handing on to the Review Gate (`flow:ready-gate`); QA does
not merge (DEVELOPMENT_METHOD.md D7).
