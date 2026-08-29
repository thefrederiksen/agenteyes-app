# Issue #33 - Developer handoff, ROUND 4

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

- Repository: `thefrederiksen/agenteyes-app` (`thefrederiksen/AgentEyes` in the skill files is ARCHIVED)
- Issue #33 - PR #34 - branch `issue-33-hud-live-preview`
- Round-3 tip QA failed: `849ac58`; QA's report: `docs/cencon/proof/issue-33/qa-round3/qa-report.md`
- Gate: `dotnet build AgentEyes.sln -c Release` clean, `dotnet test AgentEyes.sln -c Release`
  **1068 passed, 0 failed** (was 1051). Run by me, in my own worktree. **I also ran the app.**

---

## 1. The one thing that matters: I stopped patching paths

Three rounds, one defect class: **a layout event mistaken for a person's intent**, so a size nobody
chose is written to `config.json`.

| round | the transition that produced the bogus size | the fix |
|---|---|---|
| 1 | the stop auto-sizes the HUD back to the pill before `Closed` | read the size as it happens, not at save time |
| 2 | `SizeToContent = Manual` re-lays the window out and reports the pill from inside the assignment | suppress reports during the panel-open transition |
| 3 | on an UNSHOWN window the one-shot `LayoutUpdated` fires at 0x0, ending the transition early | *(this round)* |

Every one of those fixes was a **blocklist**: enumerate a transition, suppress it. A blocklist can
only ever exclude what somebody has already been burned by, and WPF has an open-ended supply of
layout-driven size changes nobody has enumerated - a DPI change, a monitor change, a restore from
minimise, a theme change, whatever panel issue #36 adds next.

**Round 4 inverts the polarity.** A size is recorded ONLY when a person resizing the window is
positively identified. Nothing in the HUD's sizing code subscribes to `SizeChanged` or
`LayoutUpdated` any more, so there is no path from a layout pass to the memory at all - not for a
transition anyone has thought of, and not for one nobody has.

### The three gestures, and why each one cannot be a layout event

| gesture | how it is identified | why layout cannot forge it |
|---|---|---|
| **the sizing border** | the Win32 resize-modal loop: `WM_ENTERSIZEMOVE` -> at least one **`WM_SIZING`** -> `WM_EXITSIZEMOVE` | `WM_SIZING` is not "the size changed" (that is `WM_SIZE`) - it is "a human is dragging a sizing edge". **Measured**: absent from the window being shown, from every programmatic resize, and from the peer-driven resize; present 16 times in one drag. `window-message-evidence.md` |
| **the panel's resize grip** | `Thumb.DragDelta` - a mouse gesture on a control | a layout pass does not raise `DragDelta` |
| **UI Automation TransformPattern** | `ITransformProvider.Resize` on the HUD's own automation peer | a typed method call from outside the process |

**The third one is new, and it is what made rounds 2 and 3 impossible to get right.** I measured it
rather than assuming it: `WindowAutomationPeer` does **not** implement `ITransformProvider`
(`IsAssignableFrom` is `False`), so UI Automation resizes a WPF window through the **default HWND
provider** - producing `WM_WINDOWPOSCHANGED` + `WM_SIZE` and nothing else, i.e. byte-for-byte what a
layout pass produces. There was no way to tell an accessibility tool's deliberate resize from the
window's own layout, because at the point where the code was listening they are the same event.
`HudWindowAutomationPeer` now serves the pattern from WPF, and the intent arrives typed. Both runs of
the probe are in `window-message-evidence.md`, with the probe source beside it.

That also means the round-3 test rig's `SetWindowPos` "user resize" was an honest mistake: its
comment said "which is exactly what UI Automation's TransformPattern.Resize does", and it **was**, at
the time. The rig now drives the real peer, and the bare `SetWindowPos` is kept as
`AResizeWithNoGestureBehindIt` - the negative control it actually is.

### What makes the class impossible rather than merely absent

`HudSizeMemory` has ONE mutator, `RecordUserResize`. Three IL guards hold the call graph shut
(`HudUserResizeTests`):

- `RecordUserResize` is called only from `HudUserResize.Record` - and from something.
- `Record` is reached only from the three gesture entry points - and from all three.
- the sizing classes contain no `add_SizeChanged` / `add_LayoutUpdated` at all.

A panel added to this window next year cannot reintroduce the defect by resizing the window during
its own layout, because resizing the window is not what records a size. A gesture is, and a gesture
has to come through `HudUserResize`. Adding a fourth writer turns those tests red.

---

## 2. The change

| file | what |
|---|---|
| `src/AgentEyes.App/HudUserResize.cs` | **new.** The three gestures, the resize-modal-loop state machine, and `HudWindowAutomationPeer`. The only caller of `RecordUserResize`. |
| `src/AgentEyes.App/HudSizeMemory.cs` | **shrunk to a store.** `Observe` / `OpenPanel` / `Settled` / `PanelClosed` / `Settling` are gone; `PreferredSize` (a pure read) and `RecordUserResize` (the one mutator) remain. |
| `src/AgentEyes.App/HudPreviewSizing.cs` | `Attach` and the `SettleWhenLaidOut` one-shot are gone. `ShowPanel` / `HidePanel` are now just the sizing command. |
| `src/AgentEyes.App/HudWindow.cs` | constructs `HudUserResize`, calls `Watch()`, wires the grip to `ByGrip`, overrides `OnCreateAutomationPeer`. `ResizeBy` moved into `ByGrip`. |

`src/AgentEyes.Core/**` is untouched, so `ApplyWindowStyles`, `WDA_EXCLUDEFROMCAPTURE`, the preview
tap, the ffmpeg command line and the manifest are unreachable from this diff by construction.

### One deliberate subtlety, because it is not obvious

`Record` requires that the preview panel was up **when the gesture BEGAN**, not when it ended.
Windows switches a window out of auto-sizing the moment its border is dragged, so by the end of a
drag of the PILL's border the window looks exactly like a panel that was open all along. Reading the
sizing mode at the end would record the pill's 900x300 as the size the person left the preview panel
at. `DraggingThePillsBorderWhileThePanelIsDown_IsNotAPanelSize` measures this, and mutation **M26**
(read it at the end) fires.

That check is a NARROWING, never an authorisation: it can only suppress a recording. The
authorisation is the caller, and there are three callers.

---

## 3. Tests

**1068 passed, 0 failed** (`green-round4.txt`). No test was weakened or deleted. The two files that
encoded the old blocklist semantics were rewritten to encode the allowlist, and every SCENARIO they
covered is preserved one-for-one - round 1's stop-before-close, round 2's pill, the hide/show inside
one recording, the seeding from config, the degenerate sizes, and all five IL wiring guards. The
questions they used to ask ("is the transition over", "is this report trustworthy") no longer exist,
because nothing observes the window.

### New, and each demonstrated RED first

| test | what it reproduces | shown red by |
|---|---|---|
| `ShowPanel_FromTheConstructorBeforeTheWindowIsShown_RemembersNothing` | **QA's blocking defect.** A HUD constructed with the preview already on, never touched. | `red-against-round3.py`, M24 |
| `AHandsOffRecording_WithARememberedSizeTheWindowCannotTake_ChangesNothing` | **QA's second reproduction**: seeded 200x100, `MinWidth` clamps to 260x100, the memory must come out 200x100. | `red-against-round3.py`, M24 |
| `AHandsOffRecording_ToggledOnAndOffRepeatedly_ChangesNothing` | nothing accumulates over a whole recording | M24 |
| `AResizeWithNoGestureBehindIt_IsNeverRemembered` | the negative control for the whole design | M24 |
| `MovingTheWindowWithoutResizingIt_IsNeverRemembered` | a move runs the same modal loop | M23, M24 |
| `DraggingTheSizingBorder_IsRemembered` / `DraggingTheGrip_IsRemembered` | the two gestures that are not UI Automation | M22, M32 |
| `DraggingThePillsBorderWhileThePanelIsDown_IsNotAPanelSize` | the sizing-mode-at-gesture-start subtlety | M26, M30 |
| `HudUserResizeTests` (17) | the state machine + the four IL structural guards | M22-M32 |

**`red-against-round3.py` / `.txt`** puts round 3's decision procedure back inside round 4's shape -
one line, `_window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);` - and runs the tests:
**Failed 17, Passed 40**, with both of QA's reproductions RED. Round 3's code cannot simply be
checked out under these tests (its API is gone), so this is the honest equivalent.

**`mutation-evidence.py` re-aimed and re-run: 32 of 32 FIRED, 0 SILENT** (`mutation-evidence.txt`).
M19, M20 and M22-M32 were re-aimed at the decisions that exist now; **M19 and M20 reported "MUTATION
DID NOT APPLY - INVESTIGATE" on the first run**, which is the instrument telling the truth about
having gone stale, and they were re-aimed rather than dropped.

**QA's round-3 blind spots** (report section 4): Q5 (the `panelVisible` gate never covered in
isolation) is **gone as a question** - there is no `panelVisible` claim any more; the window's own
`SizeToContent` is the only fact consulted, and `M30`/`M32` both fire on it. The "two call sites
share one memory instance" blind spot is now covered by a test rather than by reading:
`HudWindow_ConstructsExactlyOneSizeMemory`.

---

## 4. I ran the app. Here is the output.

Round 3 shipped because I did not - my own expected result contained the line the running app
disproves. `running-app-round4.txt`, driven with `verify33.ps1.txt` (REST for state, UIA for the HUD
and the main window's REC button; never force-foregrounded, never a screen grab - the HUD is
`WDA_EXCLUDEFROMCAPTURE`). Built `--no-incremental` from my own worktree
`D:\ReposFred\agenteyes-dev33-r4`, `bin\x64\Release\`, exe timestamp printed in the log.

```
==== (A) NO UNCHOSEN SIZE REACHES CONFIG - the defect QA found in round 3 ====
-- RECORDING 1: the person TOGGLES the preview on and resizes NOTHING --
  pill:                    HUD rect: X=1537 Y=16 W=367 H=52
  toggle ON:               -> HUD rect: X=1537 Y=16 W=520 H=400
  config after recording 1:  "HudWidth": null, "HudHeight": null
-- RECORDING 2: the HUD is CONSTRUCTED with the preview already on. Nobody resizes. --
  HUD as opened (EXPECT 520x400):  HUD rect: X=1537 Y=16 W=520 H=400
                                   IMAGE rect=1553,113,490,276
  config after recording 2:  "HudWidth": null, "HudHeight": null      <- QA's defect, FIXED
-- RECORDING 3: same again, to show nothing accumulates --
  config after recording 3:  "HudWidth": null, "HudHeight": null

==== (B) THE CLAMP DRIFT: a hands-off recording must leave config BYTE-IDENTICAL ====
  config before:                   "HudWidth": 200, "HudHeight": 100
  HUD as opened (MinWidth is 260): HUD rect: X=1644 Y=16 W=260 H=100
  config after:                    "HudWidth": 200, "HudHeight": 100  -> PASS

==== (C) AC7 END TO END: resize to 3x, stop, start again ====
  toggle ON:               -> W=520 H=400
  move to 940,340:         X=940 Y=340 W=520 H=400
  resize to 1560x400:      CanResize=True -> X=940 Y=340 W=1560 H=400
  config: "HudLeft": 940, "HudTop": 340, "HudWidth": 1560, "HudHeight": 400
-- a NEW recording --
  HUD as opened:           X=940 Y=340 W=1560 H=400
                           IMAGE rect=1449,422,544,306                <- scaled with the window
  config unchanged:        1560 / 400

==== (D) A MOVE IS NOT A RESIZE ====
  toggle ON, move to 300,200, move to 700,500
  config after:  "HudLeft": 700, "HudTop": 500, "HudWidth": null, "HudHeight": null
```

Every "MUST HOLD" from the bounce is measured above: AC1 opens at the documented 520x400 with a
non-zero `Image`; AC7 returns at the size **and** position it was left at, with the preview scaled;
a hands-off recording with the preview already visible and a remembered size leaves config unchanged.

**One pre-existing observation, recorded and NOT fixed here** (QA noted it in round 3 as unrelated
to the diff, and it is still exactly as it was since round 1): on the very FIRST toggle of a fresh
config the preview `Image` is absent from the UIA tree (`IMAGE: not found`); it appears from the
second recording onward, when the panel is up from construction. Unchanged by this diff.

---

## 5. How QA should test it

1. **The blocking defect** - re-run QA's own `qa-round3/qa33r3-unchosen.ps1.txt` and
   `qa33r3-clampdrift.ps1.txt` verbatim. Expected: `null / null` after BOTH recordings, and
   `200 / 100` unchanged after the clamp run. Or run `round4/verify33.ps1.txt`, which is all four
   scenarios in one pass. **The HUD is created by the MAIN WINDOW's REC button
   (`MainWindow.xaml.cs:1110`), not by `/record/start`** - a recording started through the REST API
   shows no HUD at all, which cost me two runs.
2. **AC7 end to end** - `qa-round3/qa33r3-ac7-e2e.ps1.txt` unchanged.
3. **The route probes** - `qa-round3/qa33r3-monitors.ps1.txt` (three monitors, minimise/restore,
   repeated toggles). These are exactly the transitions the design is meant to make irrelevant.
4. **A move is not a resize** - new this round: with the panel up, `Hud move` twice and check that
   `HudWidth`/`HudHeight` stay null while the position updates.
5. **The gate** - `dotnet build ... -c Release` + `dotnet test ... -c Release` (1068), then
   `mutation-evidence.py` (32/32 FIRED) and `round4/red-against-round3.py` (17 red, both of QA's
   reproductions among them).
6. **AC2-AC6, AC8-AC11** were not re-exercised by me this round: zero files under
   `src/AgentEyes.Core/` are touched (`git diff --name-only 849ac58..HEAD | grep -c "src/AgentEyes.Core/"`
   = 0). That is a structural fact and NOT a pass; they need the camera-armed preset and the round-1
   Core harness.

Reminders: the focus-free layers are REST / UIA / PrintWindow; never force-foreground and synthesize
input; the HUD is `WDA_EXCLUDEFROMCAPTURE`, so a PrintWindow grab of it is blank by design - assert
HUD state via UIA rects and the app's log, never a screen grab.

---

## 6. CenCon impact, and a note for issue #36

**No drift.** `HudUserResize` is a new class inside the existing App component; the component map and
the privacy posture are unchanged, and `ApplyWindowStyles` / `WDA_EXCLUDEFROMCAPTURE` are not in this
diff.

**For issue #36 (circular camera overlay), which also draws in the HUD preview:** two things changed
under it, both small.
- `HudWindow.ResizeBy` no longer exists; the grip goes through `_userResize.ByGrip`. If #36 needs to
  resize the HUD from code, that is `ByGrip` or nothing - a bare `Width =` will resize the window and
  be remembered by nobody, which is the correct behaviour.
- `HudWindow` now overrides `OnCreateAutomationPeer`. If #36 adds an automation peer of its own it
  must go through `HudUserResize.CreatePeer`, or the UI Automation resize route is lost (mutation M25
  is exactly that mistake, and it fires).
- #36 may add `SizeChanged` handlers for its own layout freely; `_previewSurface.SizeChanged` already
  does. Those are harmless by construction now and the IL guard is scoped so it does not flag them.

**Environment left as found:** `config.json` restored from backup and verified; this session's 9 test
recordings deleted from `%USERPROFILE%\Videos\AgentEyes\` (other sessions' recordings untouched); the
installed v1.6.2 tray app restarted from `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray` and
verified idle on `/status`. Branches `issue-28-camera-failure-boundaries`, `issue-35-...` and
`issue-36-...` were not touched. **One thing I did disturb and am flagging rather than hiding:** an
idle `AgentEyesApp.exe` left running from the finished `agenteyes-qa28-r9` QA session held port 7882
and the shared config file; it was stopped so this verification could run, and was not restarted (no
live session owns it - the only other agenteyes session is a Codex document review).

**I believe this is finished.**
