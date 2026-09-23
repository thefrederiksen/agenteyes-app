# Issue #33 - QA report (round 2)

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

Verdict: **FAIL. AC7 is still unmet, and the round-2 change REGRESSES AC1 - the preview panel now
opens at the pill's 367x52 with a zero-sized picture, where the parent commit opened it at 520x400.**

- Repository: `thefrederiksen/agenteyes-app` (`thefrederiksen/AgentEyes` in the skill files is ARCHIVED)
- Issue: #33  -  PR: #34  -  Branch: `issue-33-hud-live-preview` at `843e221`
- Parent (round-1 tip, the code QA failed on 2026-08-28): `46367a7`
- Verified: 2026-08-28, `HD Webcam eMeet C960`, monitor 1 = DISPLAY1 at (1920,-5)
- Built from an ISOLATED worktree `D:\ReposFred\agenteyes-qa33-r2` (detached at `843e221`), never the
  shared checkout the installed tray app locks. Output read from `bin\x64\Release\`.
- A second worktree `D:\ReposFred\agenteyes-qa33-prefix` (detached at `46367a7`) was built to run the
  SAME reproduction against the code before the fix, so "regression" is measured rather than asserted.

QA never asked the human to run anything. Every number below was produced by this QA session.

---

## 1. The blocking defect

### AC1 + AC7 - the preview panel opens at the pill's size and saves the pill's size - FAILED

> AC1: *"Toggling it shows the panel; toggling again hides it."*
> AC7: *"The HUD can be resized to at least 3x its default width with the preview visible and the
> preview scales to fit. After stopping and starting a new recording, the HUD returns at the size and
> screen position it was left at."*

**Reproduction** (`ac7-repro.ps1.txt`, run verbatim; output `repro-843e221.txt`). Fresh
`%LOCALAPPDATA%\AgentEyes\config.json` carrying no `Hud*` keys, app built by
`dotnet build AgentEyes.sln -c Release --no-incremental` from a `git status`-clean tree:

```
-- RECORDING 1 --
  HUD as opened (expect the 367x52 pill, preview hidden):
    HUD rect: X=1537 Y=16 W=367 H=52
  clicking 'Show preview' ...
  HUD after the toggle (EXPECT a 520x400 preview panel):
    HUD rect: X=1537 Y=16 W=367 H=52          <- the panel never opens
  config after recording 1:  "HudWidth": 367,  "HudHeight": 52,  "HudPreviewVisible": true
-- RECORDING 2 (preview visible from config) --
  HUD as opened (EXPECT a 520x400 preview panel):
    HUD rect: X=1537 Y=16 W=367 H=52
  preview Image element:  [ControlType.Image] name='' help='' enabled=True rect=Empty
  config after recording 2:  "HudWidth": 367,  "HudHeight": 52,  "HudPreviewVisible": true
```

The whole preview control strip IS in the UIA tree - `Preview mode screen|camera|both`,
`Preview corner ...`, `HUD preview status`, `HUD resize` - and `/status` reports
`PreviewPublishing: true` with `PreviewScreenFramesRead: 75`. Frames are arriving. But the `Image`
that is supposed to display them has `rect=Empty`: the window is 367x52, so there is nothing left
after the control strip. The user turns the preview on and sees no picture, for this recording and
for every recording after it, because `HudWidth: 367` / `HudHeight: 52` are now in the config.

AC7's resize half cannot even be reached from this state: there is no 520x400 panel to take to 3x.

**This is a REGRESSION, measured against the parent commit.** The identical script against
`46367a7` (`repro-46367a7.txt`):

| | parent `46367a7` (before the fix) | head `843e221` (the round-2 fix) |
|---|---|---|
| HUD after toggling the preview on | **520x400** | **367x52** |
| preview `Image` on recording 2 | **rect=1553,113,490,276** (visible) | **rect=Empty** (invisible) |
| `HudWidth` / `HudHeight` written | `null` / `null` (round 1's AC7 defect) | **`367` / `52`** (the pill) |

The change swapped "the size is not saved" for "the PILL is saved and the panel is unusable". The
second is worse: round 1's HUD at least showed a picture.

`HudSizeMemory`'s own class comment names this exact outcome as the thing it exists to prevent -
*"a pill's dimensions must never become the preview panel's remembered size (a saved pill would come
back as a preview panel the size of a pill)"*. That is what now happens on the shipped path.

### Root cause, with file:line and the log that shows the order

The `manuallySized` flag is read at a moment when the window's sizing mode has already flipped but
its measured size has not - and the poisoned value is then read back two statements later.

- `src/AgentEyes.App/HudWindow.cs:324-325`
  ```csharp
  SizeChanged += (_, _) => _size.Observe(
      SizeToContent == SizeToContent.Manual, ActualWidth, ActualHeight);
  ```
- `src/AgentEyes.App/HudWindow.cs:497-505` (`ApplyPreviewState`, the show branch)
  ```csharp
  if (SizeToContent != SizeToContent.Manual)
  {
      SizeToContent = SizeToContent.Manual;            // 499: WPF re-lays out HERE, synchronously,
                                                       //      raising SizeChanged while ActualWidth
                                                       //      and ActualHeight are STILL the pill's
      Width  = _size.Width  ?? DefaultPreviewWidth;    // 503: reads the value line 499 just poisoned
      Height = _size.Height ?? DefaultPreviewHeight;   // 504
  }
  ```

Proved by instrumenting the two seams in QA's own worktree copy (reverted afterwards; the tree was
rebuilt clean before the final gate). Three lines, two milliseconds apart - `qaprobe-trace.txt`:

```
22:09:21.851 [INFO] hud: preview toggled -> shown
22:09:21.855 [INFO] QAPROBE SizeChanged stc=Manual aw=367 ah=52
22:09:21.856 [INFO] QAPROBE memory now w=367 h=52
22:09:21.857 [INFO] QAPROBE ApplyPreviewState show: memory w=367 h=52 -> opening panel at 367x52
```

Line 2 is the assignment at 499 raising `SizeChanged`; line 3 is the handler at 324 recording the
pill as a "manual" size; line 4 is line 503 reading it back. The pill then flows to disk unchanged:

```
22:09:32.784 [INFO] hud: saving position left=1537 top=16 width=367 height=52
```

**Why the developer's own check missed it.** On the CONSTRUCTOR path
(`HudWindow.cs:330`, `ApplyPreviewState(fromUser: true)` on a window with no HWND yet) no layout has
happened, so `SizeToContent = Manual` raises no `SizeChanged` and the panel opens at 520x400
correctly. The defect only appears on a window that is already shown - i.e. exactly the "toggle the
preview on during a recording" path AC1 describes. The first probe line of the run shows the benign
case being correctly ignored:

```
22:09:15.787 [INFO] QAPROBE SizeChanged stc=WidthAndHeight aw=366.99333333333334 ah=52
22:09:15.788 [INFO] QAPROBE memory now w=none h=none
```

**A fix must not simply special-case the pill's dimensions.** The failure is that `Observe` is fed a
size the window has not taken yet. Either the offer has to carry the size the window was ASKED for
rather than the one it currently measures, or the show branch must stop consulting the memory it is
about to overwrite in the same statement block.

---

## 2. Criteria table

| AC | Verdict | How it was established this round |
|----|---------|-----------------------------------|
| **AC1 toggle shows the panel** | **FAIL** | toggling leaves the HUD at 367x52 with `Image rect=Empty` (section 1) |
| AC2 screen preview is live | PASS | published frames differ + `framesRead` climbs (section 3) |
| AC3 camera live AND one device open | PASS | ffmpeg process inventory + frame difference + ffprobe + manifest |
| AC4 four corners composite | NOT RE-VERIFIED - blocked | needs a visible panel through the app UI, which AC1 no longer provides |
| AC5 corner reaches the manifest | PASS | `PreviewOverlayCorner` read on 3 armed recordings, absent on the control |
| AC6 no HUD in the output | NOT RE-VERIFIED - blocked | same reason; `ApplyWindowStyles` is untouched by the round-2 diff |
| **AC7 resizable AND persists** | **FAIL** | the pill is what persists; the 3x resize is unreachable (section 1) |
| AC8 toggling mid-recording is safe | PASS | full corner/publish toggle sequence + ffprobe (section 3) |
| AC9 bounded cost | NOT RE-VERIFIED | round 2 touches no capture code (section 5); round 1's measurement stands |
| AC10 preview failure never harms the recording | PASS | preview directory deleted mid-run; recording ran full length |
| AC11 no regression when the preview is off | PASS | unarmed manifest + ffmpeg command line + file set |
| AC12 gate | PASS on the numbers, but see section 4 | build clean, 1031 tests green, 22+4 mutations run |

Blocked is not passed. AC4, AC6 and AC9 are recorded as NOT VERIFIED THIS ROUND, not as carried
forward from round 1.

---

## 3. What was re-verified, and the proof it ran clean code

The Core-level criteria were re-run with the round-1 QA harness
(`../qa/qa-harness.cs.txt`, rebuilt against this worktree's `AgentEyes.Core`).

**The harness ran unmutated Core code, proved rather than assumed.** After the whole mutation sweep
finished, the `agenteyes.dll` the harness had actually executed was hashed against a fresh rebuild
from the verified-clean tree:

```
Core dll as-run during the harness checks : 678d2d62e855e87420faf7515cc04ae7
Core dll rebuilt now from clean source    : 678d2d62e855e87420faf7515cc04ae7
MATCH
```

### AC2 / AC3 - live frames, one camera holder (`ac2-ac3-live.txt`)

```
--- ffmpeg processes: 2 ---
  pid=21284 (-f dshow)=1 (-i video=)=1 pipe:1=True mjpeg=True     <- the camera recorder
  pid=54580 (-f dshow)=0 (-i video=)=0 pipe:1=True mjpeg=True     <- gdigrab, the screen recorder
PROCESSES HOLDING THE CAMERA DEVICE = 1
AC2 screen t0=79F0E1F32507AB77 len=8448  t+2.5=46932B524B6CE73A len=8467  DIFFER=True
AC3 camera t0=CAB866093611C5E8 len=12651 t+2.5=3EC88471A3EF1A2B len=12690 DIFFER=True
framesRead screen 51->77  camera 53->78
```

The preview is a second output on the SAME ffmpeg process that writes `camera.mp4`; no second open of
the DirectShow device exists anywhere on the machine (assumption C1 intact).

### AC3 / AC8 / AC10 / AC11 - the recordings themselves

ffprobe on every run of this round:

| run | raw.mp4 | camera.mp4 | delta | CameraComplete | CameraStopKind |
|-----|---------|-----------|-------|----------------|----------------|
| armed (AC2/AC3) | 23.2333 | 23.7333 | **0.5000** | yes | clean-quit |
| armed + AC8 toggling | 33.0000 | 33.5000 | **0.5000** | yes | clean-quit |
| armed + AC10 kill | 23.7330 | 24.2333 | **0.5003** | yes | clean-quit |
| unarmed control (AC11) | 23.2333 | 23.7333 | **0.5000** | yes | clean-quit |

All inside #28's 1.0 s limit.

**AC8** (`ac8-toggle.txt`) - four corners set, publishing off, publishing on again, during one live
recording. The load-bearing line is the drain continuing while the panel is hidden:

```
AC8 publishing OFF: PreviewPublishing=False framesRead=111 frameFileExists=False
AC8 STILL DRAINING while hidden: framesRead 111 -> 126
AC8 publishing ON again: PreviewPublishing=True frameFileExists=True corner=top-right
```

**AC10** (`ac10-killhard.txt`) - the whole `%LOCALAPPDATA%\AgentEyes\preview` directory deleted
11.5 s into a 20 s armed recording:

```
KILL: deleting ...\preview framesRead=74
  +1s state=recording framesRead=85/86   PreviewFailed=True
  +2s state=recording framesRead=95/96   PreviewFailed=True
  +3s state=recording framesRead=105/106 PreviewFailed=True
  +4s state=recording framesRead=115/116 PreviewFailed=True
  +5s state=recording framesRead=125/126 PreviewFailed=True
stopping at elapsed=20.5   ->  DurationSeconds 20.51, CameraComplete "yes"
```

The recording ran to full length, the drain kept climbing, and the app log carries the WARNING:

```
21:53:08.171 [WARN] [PreviewTap] Publish FAILED: track=screen frame=...\preview\screen.jpg -
  Could not find a part of the path '...\preview\screen.jpg.tmp'.. The preview will go stale and
  say so; the recording is unaffected.
21:53:24.391 [INFO] [PreviewTap] Drain: track=screen ended at end of stream (framesRead=237 framesPublished=71)
```

237 read against 71 published is the drain surviving the publish failure. The panel-side half of
AC10 (a readable message rather than a frozen frame) was NOT re-checked this round - it needs a
visible panel, which AC1 no longer provides.

**AC11** (`ac11-control.txt`) - unarmed:

```
PreviewAvailable=False PreviewArmed=False   screenFrame=(null) cameraFrame=(null)
pid=53848 (-f dshow)=1 (-i video=)=1 pipe:1=False mjpeg=False
manifest PreviewOverlayCorner present=False ; FfmpegCommand pipe:1=False mjpeg=False
```

Key-set diff, armed manifest against unarmed manifest:

```
only in armed:   ['PreviewOverlayCorner']
only in unarmed: []
```

**AC5** - `PreviewOverlayCorner` was `bottom-left`, `top-right` and `bottom-left` on the three armed
runs (the last corner framed), and absent from the file entirely on the control.

---

## 4. The gate, the mutations, and a trap this round actually sprang

### The numbers

```
dotnet build AgentEyes.sln -c Release --no-incremental
  Build succeeded.  2 Warning(s)  0 Error(s)
      (both warnings are pre-existing xUnit1031 in PostRecordingQueueTests.cs:309,314 - not this PR)

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 1031, Skipped: 0, Total: 1031, Duration: 7 s
```

1031 = round 1's 1011 + the 20 new `HudSizeMemoryTests`. The developer's count is accurate.

### The developer's 22 mutations - all FIRED, re-run independently

`python docs/cencon/proof/issue-33/mutation-evidence.py`, executed by QA in its own worktree: **22 of
22 FIRED**, including the four added this round - M19 (the memory keeps the pill), M20 (the memory
forgets on an auto-sized report), M21 (`SavePosition` back to reading the live size), M22 (the window
never offers its sizes). The developer's claim that the new tests can fail is TRUE.

### It is true and it is not enough - QA's own four mutations, all SILENT

`qa-round2/qa-mutation-round2.py`, written by QA, probes the seams BETWEEN the window and the memory
that the developer's set does not touch (`qa-mutation-round2.txt`):

```
SILENT Q1 the HUD stops seeding its memory from the config (nothing survives a stop)
SILENT Q2 the panel re-opens from the config, not the memory (the pre-fix read)
SILENT Q3 the call site always claims manually-sized (the pill becomes the remembered size)
SILENT Q4 the call site always claims auto-sized (nothing is ever remembered)
       Passed!  - Failed: 0, Passed: 58, Skipped: 0, Total: 58   (in all four)
```

Q3 is the shipped defect, injected deliberately, and 58 tests stayed green. The suite cannot see the
one decision that actually failed: what `manuallySized` is worth AT THE MOMENT the window is asked.
That is a fair limit for a unit test of a WPF-free class - the developer states it in the test file -
but it means the test count is not evidence about AC1 or AC7, and the running app is the only
instrument that could have caught this. It was not run before handoff.

### The trap that sprang, recorded because it nearly produced a false FAIL

A mutation script leaves the LAST mutation's binaries in `bin\x64\Release\`. QA's first pass launched
the app straight after `qa-mutation-round2.py` and observed the AC7 failure - against a **Q4-mutated
`AgentEyesApp.dll`**, not against the PR. The instrumented probe is what exposed it: the file's
timestamp predated the mutation sweep's end. Every runtime observation reported above was therefore
re-taken after `git status` on `src`/`tests` came back empty AND a `--no-incremental` full rebuild,
and the Core binary was hash-matched against a fresh clean build (section 3). The false result and
the real one are independent: the real one reproduces on a clean build, deterministically, and
differs from the parent commit run minutes apart on the same machine.

---

## 5. Diff review - the developer's claims, checked

- **"NO Core file and NO existing test touched"** - TRUE.
  `git diff --stat 46367a7 843e221` touches `src/AgentEyes.App/HudSizeMemory.cs` (new),
  `src/AgentEyes.App/HudWindow.cs`, `tests/AgentEyes.Tests/HudSizeMemoryTests.cs` (new), and four
  files under `docs/cencon/proof/issue-33/`. No `src/AgentEyes.Core/**`, no pre-existing test file.
- **"20 new tests"** - TRUE (1011 -> 1031).
- **"all 22 mutations firing"** - TRUE, re-run independently.
- **"two of them shown red against the previous window first"** - the M21/M22 IL mutations do fire,
  so the claim holds as stated. It does not establish what it was offered for: neither test, nor any
  of the other 18, can see a wrong `manuallySized` argument (Q3 above).
- ASCII-only, enterprise logging (`SavePosition` now logs its values - that log line is what made the
  defect legible), try-catch at entry points: no violations found in the round-2 diff.
- Privacy posture untouched: `ApplyWindowStyles` and `WDA_EXCLUDEFROMCAPTURE` are not in this diff.

---

## 6. Environment

Restored after the run: `config.json` restored from `config.qa33r2.backup.json`, the QA recordings
deleted, the installed v1.6.2 tray app restarted, both QA worktrees removed. The QA app instances were
run from the worktrees and never installed.

---

## 7. Re-runnable artefacts committed beside this report

| file | what it is |
|------|-----------|
| `ac7-repro.ps1.txt` | the AC1/AC7 reproduction: fresh config, two recordings, rects and config after each |
| `repro-843e221.txt` | that script against the PR head - the failure |
| `repro-46367a7.txt` | that script against the parent - the same steps passing |
| `repro-843e221-instrumented.txt` | the instrumented run's console output |
| `qaprobe-trace.txt` | the four log lines that show the ordering, 2 ms apart |
| `qa-mutation-round2.py` / `.txt` | QA's four blind-spot mutations and their SILENT results |
| `ac2-ac3-live.txt`, `ac8-toggle.txt`, `ac10-killhard.txt`, `ac11-control.txt` | the harness runs |
| `qa-hud-uia-round2.ps1.txt` | the round-1 HUD UIA driver plus a `move` action |

---

**VERDICT: FAIL. AC1 and AC7 are unmet on the shipped path, and the round-2 change regresses AC1
against its own parent commit. `flow:qa-failed`.**
