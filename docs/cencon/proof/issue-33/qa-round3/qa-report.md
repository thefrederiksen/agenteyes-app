# Issue #33 - QA report (round 3)

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

Verdict: **FAIL. The round-3 fix closes the round-2 regression on the path QA reproduced it on, and
leaves the SAME defect class live on the other path: a HUD that is CONSTRUCTED with the preview
already visible records a size nobody chose and writes it to `config.json`.**

- Repository: `thefrederiksen/agenteyes-app` (`thefrederiksen/AgentEyes` in the skill files is ARCHIVED)
- Issue: #33 - PR: #34 - Branch: `issue-33-hud-live-preview` at `849ac58`
- Round-2 tip (the code QA failed on 2026-08-28): `081598b`
- Verified: 2026-08-28. Displays: DISPLAY3 primary 0,0 1920x1080; DISPLAY1 1920,-5 1920x1080;
  DISPLAY2 1173,1080 1366x768. App built and run from the ISOLATED worktree
  `D:\ReposFred\agenteyes-qa33-r3` (detached at `849ac58`), `bin\x64\Release\`, never the shared checkout.
- QA never asked the human to run anything. Every number below was produced by this QA session.

---

## 1. What round 3 fixed, stated first because it is real

The round-2 regression is gone on the path it was reported on. QA re-ran its own round-2 script
**verbatim** (`../qa-round2/ac7-repro.ps1.txt`) against a clean `--no-incremental` build of this head
(`repro-849ac58.txt`):

| | parent `46367a7` (round 1) | `081598b` (round 2, FAILED) | `849ac58` (round 3) |
|---|---|---|---|
| HUD after toggling the preview on | 520x400 | **367x52** | **520x400** |
| preview `Image` on recording 2 | rect=1553,113,490,276 | **rect=Empty** | **rect=1553,113,490,276** |
| config after a toggle-only recording 1 | null / null | **367 / 52** | **null / null** |

AC7's resize-and-return half also now works end to end, measured
(`ac7-end-to-end-and-route-probes.txt`):

```
resize by UIA TransformPattern to 3x the default width, panel visible:
  HUD rect after resize:  X=940 Y=340 W=1560 H=400        (CanResize=True)
  stop
  config: "HudLeft": 940, "HudTop": 340, "HudWidth": 1560, "HudHeight": 400
  start a new recording
  HUD as opened:          X=940 Y=340 W=1560 H=400
  preview Image:          rect=1449,422,544,306            (scaled with the window)
```

And the four route probes QA aimed at the new transition guard all HELD, on the toggle path
(`route-probe-monitors-minimise.txt`): three off/on toggle cycles, a move across all three monitors,
a minimise/restore with the panel up, and toggles across a move - the HUD came back at 520x400 every
time and `config` ended `"HudWidth": null, "HudHeight": null`. Nothing was recorded that nobody chose.

**That is the whole of the good news, and it covers exactly one of the two paths that open the panel.**

---

## 2. The blocking defect

### AC7 - a size nobody chose is written to config, on the constructor path - FAILED

> AC7: *"... After stopping and starting a new recording, the HUD returns at the size and screen
> position it was left at."*
> The developer's own handoff, section 5, sets the expected result twice and names the rule:
> *`"HudWidth": null, "HudHeight": null` <- ... NOTHING was resized, so nothing may be written: a size
> in config is a claim that somebody chose one.*
> `HudSizeMemory`'s own class comment: *"THAT is what is remembered, and nothing else ever reaches
> the config."*

**Reproduction** - `qa33r3-unchosen.ps1.txt`, run against the CLEAN (uninstrumented) build, output
`unchosen-size-cleanbuild.txt`:

```
exe lastwrite: 08/28/2026 22:58:29
-- RECORDING 1: the person TOGGLES the preview on and resizes NOTHING --
  pill:                    HUD rect: X=1537 Y=16 W=367 H=52
  toggle ON:               HUD rect: X=1537 Y=16 W=520 H=400
  config after recording 1  EXPECT null / null:   "HudWidth": null,  "HudHeight": null      <- CORRECT
-- RECORDING 2: HUD is CONSTRUCTED with the preview already on. Still nobody resizes. --
  HUD as opened:           HUD rect: X=1537 Y=16 W=520 H=400
                           IMAGE rect=1553,113,490,276
  config after recording 2  EXPECT null / null:   "HudWidth": 520,   "HudHeight": 400       <- DEFECT
```

Recording 2 is not an edge case. It is **every recording after the first time a person turns the
preview on**, because `HudPreviewVisible: true` then lives in the config and the HUD reaches
`ApplyPreviewState` from its constructor rather than from the toggle.

### It is not cosmetic: the value written is the size the WINDOW LANDS AT, not the size anybody chose

520x400 happens to equal the default, so the first instance is invisible. The mechanism is not.
Second reproduction, same clean build, seeded with a remembered size the window cannot take
(`MinWidth = 260`, HudWindow.cs:102) - `qa33r3-clampdrift.ps1.txt`, output in the same file:

```
  config before  (the person's remembered size):   "HudWidth": 200,   "HudHeight": 100
  HUD as opened (MinWidth is 260):                 HUD rect: X=1644 Y=16 W=260 H=100
  config after   EXPECT 200 / 100 unchanged - nobody touched the HUD:
                                                   "HudWidth": 260,   "HudHeight": 100
```

The person's stored 200 was silently replaced by 260 by a recording in which nobody touched the HUD.
Whatever the window's first layout produces - a minimum-size clamp, a display-scaling round trip, a
future change to `DefaultPreviewWidth`/`DefaultPreviewHeight` - is written back as that person's
deliberate choice, and compounds from there.

### Root cause, with file:line and the ordering that shows it

`src/AgentEyes.App/HudPreviewSizing.cs:82-88`:

```csharp
window.SizeToContent = SizeToContent.Manual;
window.Width = width;
window.Height = height;

// Whatever WPF did above, the transition is over once the layout it started completes.
// Subscribing AFTER the assignments means any layout they ran synchronously has already
// happened, so this cannot end the transition early ...
new SettleWhenLaidOut(window, memory).Subscribe();
```

That comment is true only when the window is ALREADY SHOWN. On the constructor path
(`HudWindow.cs:330`, `ApplyPreviewState(fromUser: true)` on a window with no HWND) the three
assignments run **no layout at all**, so the one-shot is armed BEFORE the command's layout instead
of after it. `FrameworkElement.LayoutUpdated` then fires on the next completed layout pass in the
dispatcher - which has nothing to do with this window - and ends the transition while the window is
still 0x0. The first size the window ever reports is therefore attributed to the person.

Proved by instrumenting the three seams in QA's own worktree copy (reverted afterwards; the tree was
`git status`-clean and rebuilt `--no-incremental` before every number quoted in section 1 and 2).
`qaprobe-trace-round3.txt`, the two paths minutes apart on the same machine:

```
--- RECORDING 1, the person toggles the preview on: CORRECT ---
22:55:41.386 QAPROBE OpenPanel commanded 520x400 settling=True isLoaded=True
22:55:41.393 QAPROBE SizeChanged panel=True stc=Manual aw=367 ah=52  settlingBefore=True  -> memAfter=none
22:55:41.402 QAPROBE SizeChanged panel=True stc=Manual aw=520 ah=52  settlingBefore=True  -> memAfter=none
22:55:41.418 QAPROBE SizeChanged panel=True stc=Manual aw=520 ah=400 settlingBefore=True  -> settlingAfter=False memAfter=none
22:55:41.448 QAPROBE LayoutUpdated one-shot fires: settlingBefore=False aw=520 ah=400 isLoaded=True
22:55:51.816 hud: saving position left=1537 top=16 width=none height=none

--- RECORDING 2, the HUD is constructed with the preview already on: THE DEFECT ---
22:55:58.793 QAPROBE OpenPanel commanded 520x400 settling=True isLoaded=False
22:55:58.805 QAPROBE LayoutUpdated one-shot fires: settlingBefore=True aw=0 ah=0 isLoaded=False   <- ends the
                                                                     transition before any size is reported
22:55:58.810 QAPROBE SizeChanged panel=True stc=Manual aw=520 ah=400 settlingBefore=False -> memAfter=520x400
22:56:12.280 hud: saving position left=1537 top=16 width=520 height=400

--- the clamp probe: the recorded size is not even the commanded one ---
22:57:52.312 QAPROBE OpenPanel commanded 200x100 settling=True isLoaded=False
22:57:52.329 QAPROBE LayoutUpdated one-shot fires: settlingBefore=True aw=0 ah=0 isLoaded=False
22:57:52.345 QAPROBE SizeChanged panel=True stc=Manual aw=260 ah=100 settlingBefore=False -> memAfter=260x100
22:58:04.788 hud: saving position left=1644 top=16 width=260 height=100
```

Two more facts from the same trace, both relevant to a fix:

- `22:55:46.490 SizeChanged panel=True stc=WidthAndHeight aw=260 ah=52` - on the stop path the
  `panelVisible` gate is TRUE while the window auto-sizes back to the pill. Only the `manuallySized`
  gate stops that report. The two gates are not independent in practice (see section 4, Q5).
- The commanded size does arrive as a `SizeChanged` on the constructor path too (`aw=520 ah=400`
  above). Had the transition still been outstanding, `Observe`'s `Landed` branch would have ended it
  and recorded nothing - which is exactly what recording 1 does. The one-shot is what breaks it.

**A fix must not simply delete the one-shot** - it exists so a command that lands at a slightly
different size cannot wedge the memory shut, and mutation M26 covers that. The transition has to end
on a layout of THIS window that carries out THIS command, not on the next layout pass to come along;
on an unshown window that is not the next one.

---

## 3. Criteria table

| AC | Verdict | How it was established this round |
|----|---------|-----------------------------------|
| **AC1 toggle shows the panel, hidden by default** | **PASS** | fresh config -> 367x52 pill; toggle -> 520x400; `Image rect=1553,113,490,276`; toggle again -> 367x52 (section 1, `repro-849ac58.txt`, `route-probe-monitors-minimise.txt`) |
| AC2 screen preview is live | NOT RE-VERIFIED | see section 5 |
| AC3 camera live AND one device open | NOT RE-VERIFIED | see section 5 |
| AC4 four corners composite | NOT RE-VERIFIED | see section 5 |
| AC5 corner reaches the manifest | NOT RE-VERIFIED | see section 5 |
| AC6 no HUD in the output | NOT RE-VERIFIED | see section 5 |
| **AC7 resizable AND persists** | **FAIL** | the resize/return half works (section 1); a size nobody chose reaches config on the constructor path (section 2) |
| AC8 toggling mid-recording is safe | NOT RE-VERIFIED | see section 5 |
| AC9 bounded cost | NOT RE-VERIFIED | see section 5 |
| AC10 preview failure never harms the recording | NOT RE-VERIFIED | see section 5 |
| AC11 no regression when the preview is off | NOT RE-VERIFIED | see section 5 |
| **AC12 gate** | **PASS** | build clean, 1051 tests green, 32/32 developer mutations re-run by QA, red-against-round-2 re-run by QA (section 4) |

**2 of 12 verified this round, 1 FAILED, 9 not re-verified. Blocked is not passed and unchanged-by-diff
is not passed either** - they are recorded as NOT VERIFIED THIS ROUND, exactly as in round 2.

---

## 4. The gate and the mutations

### The numbers, run by QA in its own worktree

```
dotnet build AgentEyes.sln -c Release --no-incremental
  Build succeeded.  2 Warning(s)  0 Error(s)
      (both pre-existing xUnit1031 in PostRecordingQueueTests.cs:309,314 - not this PR)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 1051, Skipped: 0, Total: 1051, Duration: 5 s
```

The developer's counts are accurate.

### The developer's 32 mutations - all FIRED, re-run independently

`python docs/cencon/proof/issue-33/mutation-evidence.py`, executed by QA:
**32 of 32 FIRED** (`dev-mutation-sweep-rerun-by-qa.txt`). The load-bearing ones are real: M24
(round 2's shipped code reconstructed) fails 4 of 5; M23, M26, M27, M28, M32 all fire.

### The red-against-round-2 demonstration - re-run independently

`python docs/cencon/proof/issue-33/round3/red-against-head.py`, executed by QA:
`Failed! - Failed: 4, Passed: 1, Total: 5` (`red-against-round2-rerun-by-qa.txt`), and the tree was
restored (`git status src tests` empty afterwards). The claim that the new tests can see round 2's
defect is TRUE. `HudPreviewSizingOrderTests` really does drive a real WPF window through the
production code.

### QA's four round-2 blind spots, re-aimed and re-run - THE POINT OF THIS ROUND

`qa-mutation-round3.py` / `.txt`, written by QA, run against the FULL suite (1051 tests), not a filter:

| QA probe | Round 2 | Round 3 |
|---|---|---|
| Q1 the HUD stops seeding its memory from the config | SILENT | **FIRED** (1 failed) |
| Q2 the panel's opening size no longer comes from the in-run memory (`Q2a`, the faithful re-aim) | SILENT | **FIRED** (5 failed) |
| Q3 the call site always claims manually-sized - **the defect that shipped in round 2** | SILENT | **FIRED** (2 failed) |
| Q4 the call site always claims auto-sized | SILENT | **FIRED** (3 failed) |

All four of the round-2 blind spots are closed. Q3 - the mutation that injected the exact shipped
defect and left 58 tests green last round - now turns the suite red. That claim of the developer's
is TRUE and QA verified it rather than accepting it.

Three further probes QA added against the NEW transition machinery:

| probe | result |
|---|---|
| Q6 the transition is never entered (`OpenPanel` does not arm the settling state) | **FIRED** (6 failed) |
| Q7 `Observe` ignores the transition (round 2's defect rebuilt inside the new design) | **FIRED** (5 failed) |
| Q5 the `panelVisible` gate alone is broken, the sizing-mode gate left honest | **SILENT** |

**Q5 is an honest limit, stated rather than hidden.** No test isolates the `panelVisible` gate; the
developer's M30 breaks BOTH gates at once and fires on the sizing-mode half, and M32 is the
sizing-mode half alone. On every path the app actually takes, the sizing-mode gate already blocks
whatever the panel-visible gate would have blocked (confirmed in the trace at 22:55:46: the stop
reports `panel=True stc=WidthAndHeight`, stopped by the sizing-mode gate alone). So the second gate
is defence in depth today, not a live decision - but it is NOT independently covered, and a future
change that leaves the window manually sized while the panel is down would find that out at runtime.

**One further blind spot QA found, closed by inspection rather than by test.** A mutation that passes
`ShowPanel` a DIFFERENT `HudSizeMemory` instance from the one `Attach` observes compiles, leaves all
1051 tests green, and reproduces round 2's defect exactly (the observed memory is never armed, so the
pill and the half-applied sizes are recorded). The IL guards check that the calls are PRESENT, not
that they share an object. QA closed this by reading the code instead: `HudWindow.cs:87` is the only
`new HudSizeMemory` in the window, and `HudWindow.cs:325` and `HudWindow.cs:500` both pass the same
`_size` field. Correct today; not defended by a test.

---

## 5. What was NOT verified this round, and why

`git diff --stat 081598b..HEAD` touches `src/AgentEyes.App/HudPreviewSizing.cs` (new),
`HudSizeMemory.cs`, `HudWindow.cs`, two test files and six files under `docs/cencon/proof/issue-33/`.
**Zero files under `src/AgentEyes.Core/`** (checked: `git diff --name-only 081598b..HEAD | grep -c
"src/AgentEyes.Core/"` = 0), so `ApplyWindowStyles`, `WDA_EXCLUDEFROMCAPTURE`, the preview tap, the
ffmpeg command line and the manifest are untouched by construction.

That is a structural fact, and it is NOT a pass. AC2, AC3, AC4, AC5, AC6, AC8, AC9, AC10 and AC11 were
NOT re-exercised this round: QA stopped at the blocking defect rather than spending an hour of audible
recordings on Core code this diff does not reach. Two of them additionally could not have been driven
through the UI in this session even had QA tried: the `Preview mode both` and `Preview corner ...`
buttons were `ElementNotEnabledException` because the preset in use (`f95b1fb7...`) carries no camera
track, so `CameraModesEnabled` is false (`ac7-end-to-end-and-route-probes.txt`) - AC3, AC4 and AC5 need
a camera-armed preset and the round-1 Core harness. They will be re-verified in full on the round that
passes.

One incidental observation, recorded for the developer and NOT counted against any criterion: during
recording 1 the preview `Image` is absent from the UIA tree entirely (`IMAGE: not found`) and only
appears from recording 2 onward, when the panel is up from construction. It has been like this since
round 1 (round 2's report shows the same shape) and is unrelated to this diff, but it means "the
picture is there" cannot be asserted by UIA on the very first toggle of a fresh config.

**PrintWindow screenshots are useless for this feature and were not used as proof.** Every HUD grab
came back `distinctColors=1`, i.e. blank - which is correct: the HUD is `WDA_EXCLUDEFROMCAPTURE`
(the repo's project instructions name this trap explicitly). HUD state above is asserted by UIA rects and by the app's own
log, never by a screen grab.

---

## 6. Diff review - the developer's claims, checked

- **"No `src/AgentEyes.Core/**` file is touched"** - TRUE (section 5).
- **"1051 tests green"** - TRUE, re-run by QA.
- **"32 of 32 FIRED"** - TRUE, re-run by QA.
- **"demonstrated RED against round 2's code"** - TRUE, re-run by QA: 4 of 5 fail.
- **"All four of QA's blind-spot probes now fire"** - TRUE for the four decisions; QA re-aimed and
  re-ran its own probes rather than accepting the developer's re-aiming, and all four fire.
- **"Nothing else ever reaches the config"** (`HudSizeMemory` class comment, and the handoff's
  expected output) - **FALSE on the constructor path.** This is the defect.
- **"I did not run the app this round"** (handoff 3.5) - TRUE, and it is why this shipped: the
  developer's own section-5 expected output contains the line that the running app disproves. The
  stated reason (another session owning the control port and the capture devices) did not hold at the
  time QA ran: no `AgentEyesApp.exe` was running and 127.0.0.1:7882 was free.
- ASCII-only, enterprise logging (the new `hud: preview panel opening/down` lines are what made this
  legible), try-catch at entry points, responsive UI: no violations found in the round-3 diff.
- Privacy posture untouched: `ApplyWindowStyles` and `WDA_EXCLUDEFROMCAPTURE` are not in this diff.
- CenCon: no drift; `HudPreviewSizing` is an extraction inside the existing App component.

---

## 7. What the Developer Agent has to do

1. End the panel-open transition on a layout of THIS window that carries out THIS command. On a window
   that has not been shown, the next `LayoutUpdated` in the dispatcher is not that layout, and today it
   ends the transition at 0x0 (`HudPreviewSizing.cs:88`).
2. Re-run the developer's own handoff section 5 against the running app. The line
   `config after recording 2: "HudWidth": null, "HudHeight": null` is the check that fails, and it is
   already written down as the expected result.
3. Add a test that drives the CONSTRUCTOR path. `HudPreviewSizingOrderTests` currently drives only the
   already-shown window; `ShowPanel` called before `Show()`, followed by `Show()`, is the shape that
   fails, and the rig is already capable of it.
4. Consider covering the two blind spots named in section 4 (the `panelVisible` gate in isolation; the
   two call sites sharing one memory instance). Neither is a live defect today - both are undefended.

---

## 8. Environment

Restored after the run: `config.json` restored from `config.qa33r3.backup.json` (backup then deleted),
this session's 11 test recordings deleted from `%USERPROFILE%\Videos\AgentEyes\` (recordings belonging
to other sessions left untouched), the instrumentation reverted with `git checkout` and the tree
rebuilt `--no-incremental` and confirmed `git status`-clean, and the installed v1.6.2 tray app
restarted from `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray` (verified idle on `/status`).
The QA app instances ran from the worktree and were never installed. Branch
`issue-28-camera-failure-boundaries` and the worktrees belonging to other sessions were not touched.

---

## 9. Re-runnable artefacts committed beside this report

| file | what it is |
|------|-----------|
| `repro-849ac58.txt` | QA's round-2 script, verbatim, against this head - shows AC1 fixed |
| `unchosen-size-cleanbuild.txt` | the blocking defect and the 200 -> 260 drift, on the clean build |
| `qa33r3-unchosen.ps1.txt` / `qa33r3-clampdrift.ps1.txt` | the two reproductions above |
| `ac7-end-to-end-and-route-probes.txt` / `qa33r3-ac7-e2e.ps1.txt` | AC7 end to end, plus repeated toggles, move-across-toggle, minimise/restore |
| `route-probe-monitors-minimise.txt` / `qa33r3-monitors.ps1.txt` | the three-monitor + minimise route probe on the toggle path (it HOLDS) |
| `qaprobe-trace-round3.txt` / `repro-849ac58-instrumented.txt` | the instrumented ordering that proves the cause |
| `qa-mutation-round3.py` / `.txt` / `-q2a.txt` | QA's own seven mutations against the full suite |
| `dev-mutation-sweep-rerun-by-qa.txt` | the developer's 32, re-run by QA |
| `red-against-round2-rerun-by-qa.txt` | the red-against-round-2 demonstration, re-run by QA |
| `qa-hud-uia-r3.ps1.txt` | the round-3 HUD UIA driver (adds `image`, `minrestore`) |

---

**VERDICT: FAIL. AC7 is unmet: on the constructor path - every recording after the first time a person
turns the preview on - the HUD records a size nobody chose and writes it to `config.json`, and the
value written is the size the window lands at rather than the size anybody left it at (measured: a
stored 200x100 silently became 260x100). `flow:qa-failed`.**
