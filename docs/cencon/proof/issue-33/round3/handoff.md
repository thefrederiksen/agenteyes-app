# Issue #33 - Developer handoff, ROUND 3

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

- Repository: `thefrederiksen/agenteyes-app` (`thefrederiksen/AgentEyes` in the skill files is ARCHIVED)
- Issue #33 - PR #34 - branch `issue-33-hud-live-preview`
- Fixes QA round 2's FAIL: `docs/cencon/proof/issue-33/qa-round2/qa-report.md`
- Rounds 1 and 2 are still on the branch (`handoff.md`, `handoff-round2.md`); this note supersedes
  them for everything about the HUD's SIZE. Nothing else in the feature changed.

---

## 1. What QA found, and what I actually fixed

QA's round-2 verdict was in two parts, and the second one is the one that mattered.

**The defect.** Toggling "Show preview" on an already-shown HUD opened the panel at the pill's
367x52 with a zero-sized `Image`, and wrote 367x52 to config so every later recording started
broken. `SizeToContent = Manual` (HudWindow.cs:499) re-laid the window out synchronously and raised
`SizeChanged` while `ActualWidth`/`ActualHeight` were still the pill's; `HudSizeMemory` recorded that
as a deliberate size, and lines 503-504 read it straight back.

**The finding underneath it.** All 1031 tests were green. All 22 of my mutations fired. QA's mutation
of the exact call site that carried the defect was SILENT. Nothing in the suite drove a WPF window,
so nothing could see what `manuallySized` was WORTH at the moment WPF handed it over.

I did not fix line 499. **The root problem is that `SizeChanged` cannot tell a person resizing the
window from the window moving under its own instructions**, and any fix that keeps reading raw
`SizeChanged` reports will record garbage at some other transition instead.

### What now counts as user intent, and why it cannot be true mid-transition

Opening the preview panel is not an assignment, it is a short transition, and the code now says so.

- `HudSizeMemory.OpenPanel(defaultWidth, defaultHeight)` is called BEFORE the window is touched. It
  returns the size to command (the remembered one, or the default) and puts the memory into a
  transition state.
- Every report that arrives while that transition is outstanding is discarded - the pill it is
  leaving, and the half-applied sizes in between.
- The transition ends when the window reports the COMMANDED size (within 1 DIP, for display-scaling
  round trips), and - so it can never wedge shut - also on the first completed layout pass after the
  command, via a one-shot `LayoutUpdated` subscribed AFTER the assignments (so it cannot end the
  transition early).
- Only then is a report attributed to a person. At that point the window is standing still under
  manual sizing with the panel up, and the only things that can change its size are the resize grip,
  a drag of the window border, and UI Automation's `TransformPattern` - all three of them people.

Two independent gates guard it in addition: the panel must be VISIBLE and the window must be
MANUALLY SIZED. Either one alone would have stopped some of these shapes; both are passed as separate
facts on purpose, so one going wrong is not enough (mutations M30/M31/M32 below).

### Measured WPF behaviour, not assumed

Three things I measured with a real window before writing the fix, all now recorded in
`HudPreviewSizingOrderTests`:

1. A window with `SizeToContent.WidthAndHeight` has its `Width`/`Height` **written by WPF** to the
   pill's measurements. They are not sitting unset waiting for the panel.
2. `Width`/`Height` set BEFORE the switch to `Manual` are **discarded** - the window stays the pill's
   size with a zero-height surface. So "just reorder the statements" is NOT a fix; the switch must
   come first. (I tried it; it fails.)
3. Each of those assignments re-lays the window out SYNCHRONOUSLY and raises `SizeChanged` from
   inside the assignment - at the pill's size, and again at width-applied-but-not-height. That second
   report is a defect route QA did not even see: on my display the round-2 code recorded 520x52 from
   it and then set `Height` from the poisoned memory, so the panel opened 520 wide and 52 high.

---

## 2. The change

| File | What changed |
|------|--------------|
| `src/AgentEyes.App/HudSizeMemory.cs` | The decision half, still WPF-free. `Observe` gains the `panelVisible` gate and the transition state; new `OpenPanel` / `Settled` / `PanelClosed` / `Settling`. `OpenPanel` throws on a non-positive default rather than substituting one. |
| `src/AgentEyes.App/HudPreviewSizing.cs` | NEW. The three WPF things the HUD does about its own size - `Attach`, `ShowPanel`, `HidePanel`. Extracted from `HudWindow` for ONE reason: **a test can drive this against a real WPF window, and it cannot drive HudWindow** (which needs a running Application's resources, a RecordingService and the user's real config). This is where the defect lived, so this is where a test had to be able to reach. |
| `src/AgentEyes.App/HudWindow.cs` | Now owns NO sizing logic. The ctor attaches, `ApplyPreviewState` calls `ShowPanel`/`HidePanel`, `SetStatus` calls `HidePanel`. `SavePosition` is unchanged. |
| `tests/AgentEyes.Tests/HudPreviewSizingOrderTests.cs` | NEW. 5 tests that drive a **real WPF window** through the real production sizing code. |
| `tests/AgentEyes.Tests/HudSizeMemoryTests.cs` | Rewritten for the transition, plus 4 new IL guards. |
| `docs/cencon/proof/issue-33/mutation-evidence.py` / `.txt` | M19-M22 updated to the new code; M23-M32 added; optional id filter. |
| `docs/cencon/proof/issue-33/round3/` | This note, the red-against-round-2 demonstration and its output, the green run. |

**No `src/AgentEyes.Core/**` file is touched. No capture, ffmpeg, preview-tap, manifest or window-style
code is touched.** `ApplyWindowStyles` and `WDA_EXCLUDEFROMCAPTURE` are not in this diff, so AC6 and
the privacy posture are untouched by construction (`git diff 081598b..HEAD --stat` shows it).

---

## 3. THE TESTING, which is the part QA asked to change

### 3.1 A test that drives a real WPF window

`HudPreviewSizingOrderTests` puts a real `Window` on a real STA dispatcher in the HUD's sizing shape
(auto-sized to a 367x52 pill, a collapsed panel below it) and drives it with **the production code
itself** - `HudPreviewSizing` and `HudSizeMemory`, not a copy. The window's real `SizeChanged`
sequence is what reaches the memory, in the order WPF actually raises it. The user resize is done
with `SetWindowPos` on the real HWND - which is what UI Automation's `TransformPattern.Resize` does,
the path QA drives AC7 with - not a property assignment that would flatter the code.

The window is fully transparent, never activated, and parked at -8000,-8000, so `dotnet test` stays
as quiet as it was. The suite is 5 s.

### 3.2 It is demonstrated RED against the round-2 code

`python docs/cencon/proof/issue-33/round3/red-against-head.py` restores `HudSizeMemory.cs` and
`HudWindow.cs` from `081598b` (the commit QA failed), puts round 2's sizing sequence back verbatim at
an address the tests can reach, runs the new tests, and restores everything.

Recorded run: `round3/red-against-head.txt`.

```
FAIL ShowPanel_OnAnAlreadyShownPillHud_OpensAtTheDefaultAndRemembersNothing
       expected 400 high, actual 52
FAIL ResizeToThreeTimesTheDefault_ThenStop_IsWhatTheNextRecordingOpensAt
FAIL HidingAndShowingAgainInOneRecording_ReopensAtTheResizedSize
FAIL ShowThenHideWithoutResizing_LeavesNoSizeToPersist
       520x52 would be written to config, but the person never resized the HUD.
       Ordering:
         SizeChanged stc=WidthAndHeight 367x52 panel=False -> memory nothing
         -- show preview --
         SizeChanged stc=Manual 520x52 panel=True -> memory 520x52
         -- hide preview --
         SizeChanged stc=WidthAndHeight 367x52 panel=False -> memory 520x52
Failed!  - Failed: 4, Passed: 1, Skipped: 0, Total: 5
```

Green against round 3: `round3/green-round3.txt` - `Passed! - Failed: 0, Passed: 5`.

Before that script existed I ran the same thing by hand, which is how the fix was designed: with
round 2's `HudSizeMemory` untouched and its sizing sequence lifted into a new file, the four tests
failed 4/4. The script is that run made re-runnable.

### 3.3 QA's four blind-spot mutations now fire

QA's `qa-round2/qa-mutation-round2.py` targets source shapes that have moved, so re-running it will
report "MUTATION DID NOT APPLY" for Q2-Q4 (a broken instrument, correctly reported, not a pass).
Each of its four probes now has a home in `mutation-evidence.py`, and each FIRES:

| QA's probe | Round-3 mutation | Result |
|---|---|---|
| Q1 the HUD stops seeding its memory from the config | **M29** | FIRED (new IL guard `HudWindow_SeedsItsMemoryFromTheSavedConfig`) |
| Q2 the panel re-opens from the config, not the memory | **M27** (HudWindow sizes itself by hand again) | FIRED (new IL guard `ApplyPreviewState_DoesNotSizeTheWindowItself`) |
| Q3 the call site always claims manually-sized | **M32**, QA's text verbatim | **FIRED** (was SILENT in round 2) |
| Q4 the call site always claims auto-sized | **M31** | FIRED |

### 3.4 The whole mutation sweep

`python docs/cencon/proof/issue-33/mutation-evidence.py` - recorded in `mutation-evidence.txt`:

**32 of 32 FIRED.** The load-bearing new ones:

- **M24 "ROUND 2'S SHIPPED CODE, RECONSTRUCTED"** - no transition, and the size read back after the
  switch. FIRED (4 of 5).
- **M23** the transition is never announced, so half-applied reports are trusted. FIRED.
- **M26** the transition never ends -> no resize is ever remembered. FIRED. (This is the wedge the
  `LayoutUpdated` one-shot exists to prevent, and it is under test.)
- **M25** taking the panel down forgets the size. FIRED.
- **M28** the panel always opens at the default, ignoring the person's size. FIRED.
- **M27, M29, M30, M31, M32** as above.

### 3.5 What this STILL cannot see - stated, not hidden

- The rig's window carries the HUD's sizing SHAPE, not the HUD's controls. `HudWindow` itself cannot
  be instantiated in a test (Application resources, RecordingService, and it would write the
  developer's real `%LOCALAPPDATA%\AgentEyes\config.json`). That `HudWindow` reaches these decisions
  through this code and owns none of its own is asserted against the compiled IL instead - four
  guards, three of them new, and M22/M27/M29 prove they fail when the wiring is broken.
- **I did not run the app this round, and here is exactly why.** Another CenCon session (issue #28,
  worktree `agenteyes-qa28-r8`) has its own `AgentEyesApp.exe` running on this machine right now: it
  owns the control API on 127.0.0.1:7882 and would compete for the camera, the audio devices and the
  screen if I started a recording. Killing it would corrupt that session's evidence. That is a real
  reason and not a preference - but it does mean the running-app half is QA's, and section 5 hands
  you the exact reproduction and the numbers to expect.

---

## 4. Acceptance criteria - what changed and how to verify

Only AC1 and AC7 changed this round. Everything else is byte-identical to the code QA already
verified at `081598b`; the diff touches three App files and no Core file at all.

| AC | Status | How QA should verify |
|----|--------|----------------------|
| **AC1 toggling shows the panel** | FIXED | Section 5, recording 1. The panel must open **520x400** with the preview `Image` having a real rect, not `rect=Empty`. |
| AC2 screen preview is live | unchanged | round-2 harness, untouched code |
| AC3 camera live AND exactly one device open | unchanged | untouched: no Core file in this diff |
| AC4 four corners composite | now reachable again | the panel opens at a usable size, so the corner chips can be driven |
| AC5 corner reaches the manifest | unchanged | untouched |
| AC6 no HUD/preview in the output | unchanged | `ApplyWindowStyles` is not in this diff |
| **AC7 resizable AND persists** | FIXED | Section 5, both recordings, plus the config after each |
| AC8 toggling mid-recording is safe | unchanged | untouched |
| AC9 bounded cost | unchanged | no capture code in this diff |
| AC10 preview failure never harms the recording | unchanged | untouched; the panel-side message is now checkable again |
| AC11 no regression when the preview is off | unchanged | untouched |
| AC12 gate | PASS | `Build succeeded. 2 Warning(s) 0 Error(s)` (both pre-existing xUnit1031 in `PostRecordingQueueTests.cs:309,314`); `Passed! - Failed: 0, Passed: 1051` |

---

## 5. The reproduction to run, and the numbers to expect

**Run QA's own round-2 script unchanged** - `qa-round2/ac7-repro.ps1.txt`, against a build of this
branch from an isolated worktree, with a fresh `config.json` carrying no `Hud*` keys. It is the exact
instrument that produced the FAIL, so it is the fairest one to re-run.

Expected, where round 2 gave 367x52 throughout:

```
-- RECORDING 1 --
  HUD as opened (the pill, preview hidden):      X=.... Y=.. W=367 H=52
  clicking 'Show preview' ...
  HUD after the toggle:                          W=520 H=400        <- was 367x52
  preview Image element:                         rect=<non-empty>   <- was rect=Empty
  config after recording 1:                      "HudWidth": null, "HudHeight": null
                                                 <- was 367 / 52. NOTHING was resized, so nothing
                                                    may be written: a size in config is a claim that
                                                    somebody chose one.
-- AC7, in recording 1 --
  UIA TransformPattern.Resize(1560, 400)  (3x the default width)
  HUD rect after resize:                         W=1560 H=400
  the preview surface scales with it (the Image's rect grows)
  stop
  config after recording 1:                      "HudWidth": 1560, "HudHeight": 400
                                                 and HudLeft/HudTop as left
-- RECORDING 2 --
  HUD as opened:                                 W=1560 H=400 at the same X/Y
```

Two extra checks worth making, because they are the states the fix is built around:

1. **Hide and re-show the preview inside one recording** after resizing. It must come back at the
   resized size, with nothing written to disk in between.
2. **Show the preview and stop without ever resizing.** `HudWidth`/`HudHeight` must stay absent.

The app log (`%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-<date>.log`) now narrates it directly:

```
hud: preview panel opening at 520x400 (the default)
hud: preview panel down; the HUD is back to its pill size, remembering 1560x400
hud: saving position left=... top=... width=1560 height=400
```

Reminders carried forward: the focus-free layers are REST (`http://127.0.0.1:7882`) / UIA /
PrintWindow; never force-foreground and synthesize input; the HUD is capture-excluded, so HUD state is
asserted via UIA or `/status`, never a screen grab. The round-2 build trap is real - build from an
isolated worktree, read `bin\x64\Release\`, and never launch straight after a mutation script without
a `--no-incremental` rebuild (QA's own trap note, round-2 report section 4).

---

## 6. For the layers stacked on this branch

`issue-35-preset-editor-two-columns` and `issue-36-circular-camera-overlay` sit on top of this branch,
and #36 also draws in the HUD preview. What they must adapt to, all of it mechanical:

- `HudWindow` no longer assigns `SizeToContent`, `Width` or `Height` anywhere. If #36 needs the panel
  sized or unsized, call `HudPreviewSizing.ShowPanel` / `HidePanel`. **An inline assignment will now
  fail `ApplyPreviewState_DoesNotSizeTheWindowItself` / `SetStatus_TakesThePanelDownThroughTheShared
  SizingPath`** - that guard is deliberate.
- `HudSizeMemory.Observe` now takes four arguments (`panelVisible` first).
- Drawing inside the preview surface is untouched: `_previewSurface`, `LayOutInset`, `ShowFrames` and
  the corner logic are exactly as #36 found them.

## 7. CenCon impact

No drift. No component appears or disappears (`HudPreviewSizing` is an extraction from `HudWindow`,
inside the existing App component), and the privacy posture is untouched - visible and controllable,
`WDA_EXCLUDEFROMCAPTURE` unchanged and not in this diff.

## 8. Gate

```
dotnet build AgentEyes.sln -c Release --no-incremental
  Build succeeded.  2 Warning(s)  0 Error(s)      (both pre-existing xUnit1031, not this PR)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 1051, Skipped: 0, Total: 1051, Duration: 5 s

python docs/cencon/proof/issue-33/mutation-evidence.py
  32 of 32 FIRED

python docs/cencon/proof/issue-33/round3/red-against-head.py
  Failed!  - Failed: 4, Passed: 1, Total: 5     (the new tests, against round 2's code)
```

Built and run from the isolated worktree `D:\ReposFred\agenteyes-dev33-r3`, never the shared checkout.
I ran every one of these myself; nothing is left for the human to run.

**I believe this is finished, with the one limit named in 3.5: the running-app half is QA's this
round, because another session's app instance owns the control port and the capture devices.**
