# Issue #33 - QA report (round 1)

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

Verdict: **FAIL - 11 of 12 acceptance criteria verified, AC7 FAILED.**

- Repository: `thefrederiksen/agenteyes-app` (the `thefrederiksen/AgentEyes` repo named in the skill
  files is ARCHIVED)
- Issue: #33  -  PR: #34  -  Branch: `issue-33-hud-live-preview` at `178cf2a`
- Base: `issue-28-camera-failure-boundaries` at `0558585`; the whole feature is one commit on top of it
- Verified: 2026-08-28, on `HD Webcam eMeet C960` (a second camera, `OBS Virtual Camera`, was present
  and never opened), ffmpeg 9.0.1 for the recorder and ffmpeg 8.0.1 for the QA probes
- Built and tested from an ISOLATED worktree `D:\ReposFred\agenteyes-qa33` (detached at `178cf2a`),
  never the shared checkout the running tray app locks. Output read from `bin\x64\Release\`.

QA never asked the human to run anything. Every number below was produced by this QA session.

---

## 1. The blocking defect

### AC7 - "HUD is resizable and persists" - FAILED (the persistence half)

> AC7: *"The HUD can be resized to at least 3x its default width with the preview visible and the
> preview scales to fit. **After stopping and starting a new recording, the HUD returns at the size
> and screen position it was left at.**"*

**Resizing works.** Driven through UI Automation's `TransformPattern` (`CanResize=True`), the HUD
went from its 520x400 preview default to **1600x760** - 3.08x the default width - and the preview
scaled with it: the screen `Image` grew from `490x276` to `1184x666` and the camera inset to
`469x264` (30% of the panel). Evidence: `qa/ac7-resized.png`.

**Persistence does not.** Reproduction, exactly as run:

```
1. Fresh config; record; toggle "Show preview" on; stop.        -> next recording is armed
2. Record again. HUD opens with the panel showing at 520x400.
3. UIA TransformPattern.Resize(1600, 760).
     HUD rect after resize: X=1537 Y=16 W=1600 H=760            [observed]
4. Click the HUD's own STOP button. Wait for the stop to finish.
5. Read %LOCALAPPDATA%\AgentEyes\config.json:
     "HudLeft": 1537,   "HudTop": 16,
     "HudWidth": null,  "HudHeight": null                       [observed - the SIZE was not saved]
6. Start a new recording and read the HUD's bounding rectangle:
     HUD rect: X=1537 Y=16 W=520 H=400                          [observed]
```

Expected at step 6: `W=1600 H=760`. Actual: `W=520 H=400` - the hard-coded
`DefaultPreviewWidth/Height`. The *position* is restored; the *size* is lost on every ordinary stop.

**Root cause, with file:line.** `HudWindow.SavePosition()` guards the size write:

- `src/AgentEyes.App/HudWindow.cs:677-681`
  ```csharp
  if (SizeToContent == SizeToContent.Manual && ActualWidth > 0 && ActualHeight > 0)
  {
      _cfg.HudWidth = ActualWidth;
      _cfg.HudHeight = ActualHeight;
  }
  ```
- `SavePosition()` is only called from the `Closed` handler, `HudWindow.cs:311`.
- But `HudWindow.SetStatus(...)` - which runs on **every ordinary stop** (the HUD STOP button via
  `RunOnce`, and the main window / tray stop, which call `_hud.SetStatus`) - executes
  `SizeToContent = SizeToContent.WidthAndHeight;` at **`HudWindow.cs:374`**, *before* the window
  closes. By the time `Closed` fires the guard is false, so `HudWidth`/`HudHeight` are never written.
- The same reset happens at **`HudWindow.cs:492`** when the preview is merely hidden, so
  "resize -> hide the preview -> stop" loses the size too.

`SavePosition` at `HudWindow.cs:677-681` is the **only** writer of `HudWidth`/`HudHeight` in the whole
solution (`grep -rn "HudWidth\|HudHeight" --include=*.cs src/` returns `Config.cs:25-26`,
`HudWindow.cs:483-484` reading them, and these two lines writing them). There is no other path that
could save the size.

**Why no test caught it.** The only test that touches these fields asserts they are *null*
(`tests/AgentEyes.Tests/HudPreviewStateTests.cs:289-290`,
`Config_Defaults_AreAHiddenScreenPreviewInTheBottomRight`). Nothing exercises the save-and-restore
round trip. This is consistent with the developer's own stated limit #2 ("no test drives the WPF
window") - but the criterion is still unmet, and a criterion is not satisfied by being untested.

---

## 2. Criteria table

| AC | Verdict | How it was established |
|----|---------|------------------------|
| AC1 toggle exists, defaults off | PASS | UIA + PrintWindow |
| AC2 screen preview is live | PASS | published-frame bytes differ + HUD captures differ + counters climb |
| AC3 camera live AND one device open | PASS | process inventory + frame difference + ffprobe + manifest |
| AC4 four corners composite | PASS | four HUD screenshots |
| AC5 corner reaches the manifest | PASS | manifest.json read on 4 recordings |
| AC6 no mirror tunnel, no HUD in output | PASS | frames cropped from recording.mp4, plus a known-bad run proving the check fires |
| **AC7 resizable AND persists** | **FAIL** | resize verified; persistence reproducibly lost (section 1) |
| AC8 toggling mid-recording is safe | PASS | full toggle sequence during one recording + ffprobe |
| AC9 bounded cost | PASS, with a documented limit | 5 paired 60s runs; both numbers reported (section 3) |
| AC10 preview failure never harms the recording | PASS | preview directory deleted mid-run, twice, two harnesses |
| AC11 no regression | PASS | unarmed manifest + command line + file set |
| AC12 gate | PASS | build + 1011 tests + all 18 mutations re-run independently |

---

## 3. Evidence, criterion by criterion

### AC1 - toggle exists and defaults off - PASS

`%LOCALAPPDATA%\AgentEyes\config.json` was replaced with a fresh minimal file carrying no `Hud*`
keys. `/status` then reported `PreviewArmed: false` before any recording.

During the first recording, the HUD's UIA tree (`qa/qa-hud-uia.ps1.txt -Action inspect`):

```
HUD rect: X=1537 Y=16 W=367 H=52
  [Button] name='Show preview' help='hidden' enabled=True
  [Button] name='HUD stop' ...
```

- The button's UI Automation **name is exactly `Show preview`**, and it stays that name in both
  states; `HelpText` carries the state (`hidden` / `showing`).
- No preview panel in the tree; HUD is the 367x52 pill. `/status` -> `PreviewPublishing: false`.
- Screenshot: `qa/ac1-hidden.png`.

Invoking it: `HelpText` -> `showing`, the visible label -> `Hide preview`, the HUD grows to
**520x400**, and the whole control strip appears (`Preview mode screen|camera|both`,
`Preview corner top-left|top-right|bottom-left|bottom-right`, `HUD preview status`,
`HUD preview message`, `HUD resize`). Screenshot: `qa/ac1-shown-unarmed.png`. Invoking again returned
it to `hidden` and the pill size.

**The opt-in-per-recording consequence - judged, not waved through.** On that first recording after a
fresh config the panel shows *"Live preview starts with your NEXT recording. The recorder's preview
feed is set up when a recording begins, and this one was started with the preview switched off."*
(`qa/ac1-shown-unarmed.png`), and `/status` flips `PreviewArmed` to `true`.

QA's judgement: **acceptable, not a defect.** AC1's contract is that the control exists, defaults off,
and that toggling shows and hides the panel - all of which it does. The absence of a picture on that
one recording is forced by the issue's own assumption C1: the preview must come from the recording's
ffmpeg, and ffmpeg's outputs are fixed at process start, so an unarmed recording physically cannot
grow a feed without restarting the process that is recording. The behaviour is *explicit* rather than
silent - the panel states the reason in plain language and the status line reads `screen |
unavailable` - and paying the cost on every recording is what would have destroyed AC11's guarantee
and AC9's control run. This is flagged for Product as a UX call (a first-run hint elsewhere in the app
would remove the surprise entirely), not as a QA failure.

### AC2 - screen preview is live - PASS

Two independent instruments, both of which had to *change*, with a deliberate source of change.

1. **Published frame bytes.** A topmost, non-activating window (`qa/qa-harness.cs.txt`, class `Churn`;
   `WS_EX_NOACTIVATE`, no synthesised input) painted a changing counter and a moving rectangle on the
   captured monitor. Reading `%LOCALAPPDATA%\AgentEyes\preview\screen.jpg` 2.5 s apart:

   ```
   AC2 screen t0=62586854CAFADC15 len=14379  t+2.5=8452E6374DCD5306 len=14453  DIFFER=True
   framesRead screen 66->91   (25 frames in 2.5s = exactly the 10 fps the tap is configured for)
   JPEG markers: FFD8 .. FFD9
   ```
   The two frames are committed: `qa/ac2-screen-t0.jpg` (counter `QA33 0079`) and
   `qa/ac2-screen-t2.jpg` (counter `QA33 0101`, rectangle moved). Both are real 480x270 desktop
   pictures, not blank.

   *Both failure arms were exercised.* An earlier run of the same probe against a STATIC desktop
   returned `DIFFER=False` while `framesRead` still climbed 75 -> 100 - i.e. the instrument does
   distinguish "the frames are not moving" from "the frames are not arriving", and the pass above is
   not an artefact of a check that always says yes.

2. **The HUD panel itself.** Two PrintWindow captures of the HUD 3 s apart during an armed recording:
   `qa/ac2-hud-screen-t0.png` SHA256 `6F7FC41D...`, `qa/ac2-hud-screen-t3.png` SHA256 `4F13BD6B...` -
   different files, both rendering the live desktop.

### AC3 - camera preview is live AND the device is opened once - PASS

**Exactly one process holds the camera.** A full `Win32_Process` inventory of every `ffmpeg.exe` on
the machine, taken 4 s into an armed camera recording with the camera preview publishing:

```
--- ffmpeg processes: 2 ---
  pid=16788 (-f dshow)=1 (-i video=)=1 pipe:1=True mjpeg=True     <- the camera recorder
  pid=15028 (-f dshow)=0 (-i video=)=0 pipe:1=True mjpeg=True     <- gdigrab, the screen recorder
PROCESSES HOLDING THE CAMERA DEVICE = 1
```

The camera process's own command line, read from the OS (not from the repo):

```
ffmpeg -hide_banner -y -f dshow -thread_queue_size 1024 -framerate 30 -i "video=HD Webcam eMeet C960"
       -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 -an ...\camera.mp4
       -map 0:v -vf fps=10,scale=-2:270:flags=neighbor -q:v 8 -pix_fmt yuvj420p -an
       -f mjpeg -flush_packets 1 pipe:1
```

One `-f dshow`, one `-i video=`, and the preview is a **second output on that same process** writing
to stdout - which is assumption C1 satisfied in the only way it can be. No second device open exists
anywhere on the machine.

**The camera preview is live.** `preview\camera.jpg` read 2.5 s apart:
`t0=16D3D87178745CE8 len=14353`, `t+2.5=42C246180D13289B len=14371`, `DIFFER=True`;
`PreviewCameraFramesRead` 66 -> 91. Committed: `qa/ac3-camera-t0.jpg`, `qa/ac3-camera-t2.jpg` - real
webcam pictures.

**camera.mp4 is unaffected.** ffprobe, every armed run:

| run | screen file | camera.mp4 | delta | CameraComplete | CameraStopKind |
|-----|------------|-----------|-------|----------------|----------------|
| armed + AC10 kill | raw 23.800 | 24.400 | 0.600 | yes | clean-quit |
| armed + AC8 toggling | raw 33.967 | 33.967 | 0.00003 | yes | clean-quit |
| armed, full app path | **recording.mp4 49.867** | 50.500 | 0.633 | yes | clean-quit |
| 5x armed 60 s | raw 62.93-63.47 | - | 0.400-0.567 | yes | clean-quit |

Every delta is inside #28's 1.0 s limit, and every armed recording recorded `CameraComplete: "yes"` on
a clean stop.

### AC4 - both mode composites in the selected corner - PASS

In `both` mode, each of the four corner buttons was invoked through UIA and the HUD captured with
PrintWindow. Four captures, four distinct inset positions, each with the status line agreeing:

| file | inset | status line |
|------|-------|-------------|
| `qa/ac4-top-left.png` | top-left | `both top-left \| live` |
| `qa/ac4-top-right.png` | top-right | `both top-right \| live` |
| `qa/ac4-bottom-left.png` | bottom-left | `both bottom-left \| live` |
| `qa/ac4-bottom-right.png` | bottom-right | `both bottom-right \| live` |

`/status` `PreviewOverlayCorner` tracked each click (`top-left`, `top-right`, `bottom-left`,
`bottom-right`) in the same order.

### AC5 - the corner reaches the manifest - PASS

- `both` + `top-left` (harness) -> `"PreviewOverlayCorner": "top-left"` in `manifest.json`.
- `both`, four corners cycled, ending `top-right` -> `"PreviewOverlayCorner": "top-right"`: the LAST
  corner framed is what is written, as documented.
- `both` + `bottom-right` through the full app -> `"PreviewOverlayCorner": "bottom-right"`.
- Preview never enabled -> the property is **absent from the file entirely**, not present-and-null. A
  key-set diff of an armed against an unarmed manifest is exactly one line:
  ```
  20d19
  < PreviewOverlayCorner
  ```

### AC6 - no mirror tunnel and no HUD in the output - PASS (with the check shown to fire)

Setup: `MQS_HUD_CAPTURABLE` **unset** (the shipped default, so the exclusion is on), HUD moved to
screen coordinates `2300,300` at `520x400` - inside the captured monitor, whose gdigrab origin is
`-offset_x 1920 -offset_y -5`, so the HUD occupies `380,305 520x400` inside each captured frame.
Recorded 45 s with the preview ON in `both` mode, full app post-processing.

Six frames sampled across `recording.mp4`, cropped to `crop=520:400:380:305`
(`qa/ac6-hudregion-01..06.png`): **every one shows only the File Explorer window that was underneath
the HUD.** No HUD, no preview panel, no mirror tunnel.

**The check was proved capable of failing.** The same recording, the same crop coordinates, with
`MQS_HUD_CAPTURABLE=1` (the deliberate opt-out of the exclusion - the known-bad configuration):
`qa/ac6-known-bad-capturable-03.png` shows the **entire HUD**, its preview panel, and a visible mirror
tunnel inside it. So a passing crop is a real absence and not a mis-aimed rectangle.

A PrintWindow capture of the HUD taken with the exclusion on returned a solid black image
(`qa/ac6-hud-printwindow-excluded.png`, distinct colours = 1), independently confirming
`WDA_EXCLUDEFROMCAPTURE` was actually in force for that run. `ApplyWindowStyles` is untouched by the
diff (assumption C5 intact).

### AC8 - toggling mid-recording is safe - PASS

During one armed 30 s recording: corner set to each of the four values, publishing turned OFF,
publishing turned ON again, corner changed once more, then stop.

```
AC8 corner -> top-left      (status corner=top-left)
AC8 corner -> top-right     (status corner=top-right)
AC8 corner -> bottom-left   (status corner=bottom-left)
AC8 corner -> bottom-right  (status corner=bottom-right)
AC8 publishing OFF: PreviewPublishing=False framesRead=128 frameFileExists=False
AC8 STILL DRAINING while hidden: framesRead 128 -> 143
AC8 publishing ON again: PreviewPublishing=True frameFileExists=True corner=top-right
```

The middle line is the load-bearing one: with the panel hidden the tap **keeps taking frames off
ffmpeg's pipe** (128 -> 143 in 1.5 s). That is what stops an unread anonymous pipe filling and
blocking the ffmpeg writing the recording.

Result: `raw.mp4` 33.9667 s, `camera.mp4` 33.9666 s - a delta of **0.00003 s** -
`CameraComplete: "yes"`, `CameraStopKind: "clean-quit"`, `PreviewOverlayCorner: "top-right"`.

Through the running app the same sequence (show, mode changes, four corners, resize, stop) also left a
valid pair: `recording.mp4` 49.867 s / `camera.mp4` 50.500 s, `CameraComplete: "yes"`.

### AC9 - bounded cost - PASS, with a stated limit

Five paired 60-second recordings (1920x1080 @ 30 fps, camera + loopback, identical changing content in
both arms), alternating control and preview so machine drift affects both.

**Both numbers, as the criterion asks:**

```
screen ffmpeg drop=   control  16, 18, 30, 15,  4      median 16   mean 16.6
                      preview   9, 40, 14, 42, 27      median 27   mean 26.4
camera ffmpeg drop=   0 in all ten runs
raw.mp4 delivered frames (~63 s at 30 fps, expected ~1890):
                      control 1841, 1829, 1761, 1812, 1437   mean 1736
                      preview 1764, 1803, 1881, 1810, 1858   mean 1823
raw-vs-camera duration delta:
                      control 0.500 0.500 0.233 0.633 0.467   all < 1.0 s
                      preview 0.467 0.467 0.567 0.400 0.500   all < 1.0 s
```

**The honest reading, stated rather than implied.** On the literal metric AC9 names - `drop=` in the
ffmpeg log - the preview was *lower* in 2 of my 5 pairs and *higher* in 3, with a higher median. That
metric cannot resolve the question on this machine: identical CONTROL runs spanned 4 to 30 drops, and
the control run that reported the FEWEST drops (4) delivered the FEWEST frames of any run in the
experiment (1437 of ~1890) - so `drop=` is not even monotone with the thing it is supposed to
proxy. Combining my 5 pairs with the developer's committed 5 pairs at 30 s
(`preview-cost-check.txt`), the preview is lower in 5 and higher in 5 of 10 pairs: an exact coin toss.

On the metric that does measure the cost AC9 exists to bound - frames actually delivered into the
recorded file, and the duration limit - the preview is **not worse**: it delivered more frames on
average than the control, and all ten runs met #28's 1.0 s duration limit.

QA rules this PASS on that basis and records the limit explicitly: *a single control-vs-preview `drop=`
comparison is not a discriminating instrument here, and this pass rests on delivered frames and the
duration limit rather than on the drop counter.* This is a statement of what the check cannot see, not
a relaxation of the criterion - the criterion's own numbers are reported above in full.

### AC10 - preview failure never harms the recording - PASS (the most important check)

**Independent confirmation of the developer's central claim.** The claim was that routing the preview
as an ffmpeg `image2` FILE output is actively dangerous, and that routing it to stdout with an
unconditional drain is what makes a preview failure cost a picture instead of a recording. QA
confirmed the *shipped* half by inducing the failure, and confirmed the *rejected* half by mutation
(M13, below): the check that forbids a file output fires against a known-bad implementation.

**Induced failure, run 1 (harness, direct against `RecordingService`).** 20-second armed recording,
camera + screen, publishing on. At t = 11.5 s the entire `%LOCALAPPDATA%\AgentEyes\preview` directory
was deleted:

```
KILL: deleting preview dir. framesRead screen=100 camera=101 failed=False
KILL: directory deleted
  +1s state=recording framesRead screen=111 camera=111 PreviewFailed=True screenFileExists=False
  +2s state=recording framesRead screen=121 camera=122 PreviewFailed=True screenFileExists=False
  +3s state=recording framesRead screen=132 camera=132 PreviewFailed=True screenFileExists=False
  +4s state=recording framesRead screen=142 camera=142 PreviewFailed=True screenFileExists=False
  +5s state=recording framesRead screen=152 camera=152 PreviewFailed=True screenFileExists=False
  +6s state=recording framesRead screen=162 camera=161 PreviewFailed=True screenFileExists=False
stopping at elapsed=20.4
```

- The recording **continued and completed full length** (20.41 s of a 20 s request; the earlier
  file-output shape truncated a 15 s recording to 5.133 s).
- `framesRead` **kept climbing** through the failure - the drain never stopped. That climbing count is
  the proof the pipe is still being emptied, which is what protects the recording.
- `PreviewFailed` went true; the app log carried
  `[WARN] [PreviewTap] Publish FAILED: track=... - Could not find a part of the path ... The preview
  will go stale and say so; the recording is unaffected.`
- Both files valid: `raw.mp4` 23.800 s, `camera.mp4` 24.400 s (delta 0.600 s), `CameraComplete: "yes"`,
  `CameraStopKind: "clean-quit"`.

**Induced failure, run 2 (the running WPF app, HUD visible).** Same deletion, mid-recording, with the
panel showing. The panel replaced the picture within ~2 s with
*"Preview unavailable - no frames from the recorder. The recording is unaffected."* and the status line
read `both bottom-right | no frames` (`qa/ac10-after-kill.png`). **No frozen last frame** - the frame
that had been on screen a moment earlier (`qa/ac10-before-kill.png`) was cleared, not left in place.
`PreviewFailed: true` on `/status`, `WARN` in the log, `State: recording` throughout.

Code review agrees with both observations: the drain's `catch` drops to read-and-discard rather than
stopping (`PreviewTap.cs:210-216`), `Publish` swallows and reports its own failures without rethrowing
into the drain (`PreviewTap.cs:233-255`), the taps are disposed in the stop's `finally` and are
deliberately NOT stop-sequence steps (`RecordingService.cs`, `DisposePreviewTaps`), and the panel
judges frame AGE with never-arrived counting as stale (`HudPreviewState.IsStale`).

### AC11 - no regression - PASS

A control recording with `PreviewArmed` false, camera + screen:

- `manifest.json` contains **no `PreviewOverlayCorner`** (key-set diff against an armed manifest is
  exactly that one key).
- `FfmpegCommand` contains **no `pipe:1` and no `mjpeg`**:
  ```
  ffmpeg -y -f gdigrab -thread_queue_size 1024 -framerate 30 -offset_x 1920 -offset_y -5
         -video_size 1920x1080 -i desktop -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 raw.mp4
  ```
- The camera process's command line likewise ends at `camera.mp4` with no second output.
- Same file set as before the feature: `camera.mp4`, `camera.mp4.ffmpeg.log`, `manifest.json`,
  `raw.mp4`, `raw.mp4.ffmpeg.log`, `sys_native.wav`.
- `/status` -> `PreviewAvailable: false`, `PreviewArmed: false`, both frame paths null.
- `PreviewArmed` is set in exactly two places (`App.xaml.cs:59`, `HudWindow.cs:639`), both in the WPF
  app. The CLI never arms one, so `agenteyes video` is untouched. This closes the developer's stated
  limit #1 (no unit test covers `RecordingService`'s arming decision) at runtime, in both directions:
  armed -> `pipe:1` present, unarmed -> absent.

### AC12 - gate - PASS

From the isolated worktree, with no AgentEyes process holding the output:

```
dotnet build AgentEyes.sln -c Release
  ...
  Build succeeded.
      2 Warning(s)
      0 Error(s)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 1011, Skipped: 0, Total: 1011, Duration: 10 s
```

1011 tests, matching the developer's claim. The two warnings are pre-existing `xUnit1031` warnings in
`tests/AgentEyes.Tests/PostRecordingQueueTests.cs:309,314`, a file this PR does not touch - but the PR
body's "0 Warning(s)" is inaccurate.

**Mutation evidence re-run independently.** `python docs/cencon/proof/issue-33/mutation-evidence.py`
executed by QA in its own worktree: **all 18 mutations FIRED**, including every load-bearing one -
M4 (tap stops draining while hidden), M5 (a publish failure stops the drain), M9 (`IsStale` treats
never-arrived as fine), M13 (the preview as a FILE output - the measured recording-truncating shape),
M14 (the preview added to every recording), M18 (a default corner written instead of an absent field).
The script itself fails closed: a first attempt, run while a QA app instance held the build output,
reported every mutation as `SILENT - NO SUMMARY LINE` rather than as a pass, which is the correct
behaviour for a broken instrument.

---

## 4. Secondary findings (not blocking; for the developer's next pass)

1. **Config is written on the HUD construction path.** `HudWindow`'s constructor calls
   `ApplyPreviewState(fromUser: true)`, which reaches `SavePreviewChoices()` ->
   `_cfg.Save()` - synchronous file I/O on the UI thread while the HUD is being put on screen. The
   code's own comment two lines above says this happens "only when a person actually chose something -
   never on the construction path". One of the two is wrong. (CLAUDE.md standard 1.)

2. **The `HUD preview status` text is not readable through UI Automation.** The handoff (section 4,
   AC4) offers it as "a focus-free assertion of the same thing". It is not:
   `AutomationProperties.SetName` (`HudWindow.cs:327-328`) replaces the element's UIA Name with the
   static label `HUD preview status`, masking the live text (`both top-left | live`). The same applies
   to `HUD preview message`. Only a screenshot can read them today. Consider
   `AutomationProperties.HelpText`, or a `Name` that carries the value.

3. **PR body accuracy.** It claims `0 Warning(s)`; the build emits 2 (pre-existing, see AC12).

4. **Observed but out of scope for #33.** In one QA run gdigrab failed at start with
   `Failed to capture image (error 5)` (a transient environment condition - it worked immediately
   before and after). The screen ffmpeg exited after 0.08 s, yet `/status` reported `State: recording`
   for 91 more seconds and the stop produced no `recording.mp4`. The preview output failed as a
   *consequence* of the dead input, not as a cause - the preview is not implicated - but a screen
   writer that dies at start while the app reports a healthy recording is worth its own issue.

---

## 5. Method compliance

- Independent verification: every number above was produced by this QA session in its own worktree.
  The developer's `mutation-evidence.txt` and `preview-cost-check.txt` were re-run, not quoted.
- No check here passes on an absence alone. AC6's absence claim is backed by a known-bad run in which
  the same check FIRES. AC2's difference claim is backed by a static-desktop run in which it correctly
  reports `DIFFER=False`. AC9's pass rests on a stated instrument limit rather than a redefinition,
  with the criterion's own numbers reported in full.
- Nothing was handed to the human to run.
- Environment restored afterwards: `config.json` restored from backup, the 21 QA recordings deleted,
  the installed v1.6.2 tray app restarted.

## 6. Re-runnable artefacts committed beside this report

| file | what it is |
|------|-----------|
| `qa/qa-harness.cs.txt` | the QA harness: drives `RecordingService` directly (arming, taps, the AC10 kill, the AC8 toggle sequence, the AC9 pairs) and paints the changing content AC2 needs |
| `qa/qa-hud-uia.ps1.txt` | UIA + PrintWindow driver for the HUD (inspect / click / read / resize / shot) |
| `qa/qa-main-uia.ps1.txt` | UIA driver for the main window (preset menu, REC) |
| `qa/ac*.png`, `qa/ac*.jpg` | the captures referenced above |

---

**VERDICT: FAIL. 11 of 12 acceptance criteria verified. AC7's persistence half is unmet and
reproducible; `flow:qa-failed`.**
