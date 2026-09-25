# Issue #33 - Developer handoff, round 2 (the AC7 bounce)

**[Tray] Live preview in the recording HUD - screen, camera, or both with a corner overlay**

- Repository: `thefrederiksen/agenteyes-app` (the `thefrederiksen/AgentEyes` named in the skill files
  is ARCHIVED and read-only)
- Issue: #33  -  PR: #34  -  Branch: `issue-33-hud-live-preview`
- Round 1 QA verdict: FAIL, 11 of 12 criteria verified, **AC7's persistence half unmet**
  (`docs/cencon/proof/issue-33/qa-report.md`)
- Round 1 handoff: `docs/cencon/proof/issue-33/handoff.md` - still accurate for AC1-AC6, AC8-AC12
- Built and tested from an ISOLATED worktree `D:\ReposFred\agenteyes-dev33`, never the shared
  checkout the running v1.6.2 tray app locks. Output read from `bin\x64\Release\`.

**ONE defect was fixed. Nothing else was touched.** AC3 (one process holds the camera) and AC10
(the preview stays subordinate to the recording) were deliberately left completely alone - no file
under `src/AgentEyes.Core/Preview/`, `Video/`, or `RecordingService` is in this diff. No existing
test was changed, weakened or deleted; the suite went from 1011 to 1031 tests, all of them green.

---

## 1. The defect, confirmed rather than re-derived

QA's diagnosis was exact and I am not restating a different one. Reproduced from the code:

- `HudWindow.SavePosition()` (round-1 `HudWindow.cs:677-681`) was the ONLY writer of
  `Config.HudWidth`/`HudHeight`, and it read the window's LIVE sizing state:
  `if (SizeToContent == SizeToContent.Manual && ActualWidth > 0 && ActualHeight > 0)`.
- `SavePosition()` is called from exactly one place: the `Closed` handler.
- `HudWindow.SetStatus(...)` runs on **every ordinary stop** and executes
  `SizeToContent = SizeToContent.WidthAndHeight;` (round-1 `HudWindow.cs:374`) - *before* the window
  closes. The same reset happens at round-1 `HudWindow.cs:492` when the preview is merely hidden.
- So by the time `Closed` fires the guard is false, `HudWidth`/`HudHeight` are never written, and the
  next recording's HUD opens at the hard-coded `DefaultPreviewWidth/Height` (520x400).

**It is an ORDERING bug, not a save-site bug.** The old save site is perfectly correct in the one
state the window is never in when it matters. That is why a test asking "does the save write the size
when the window is manually sized?" would have passed against the broken code and proved nothing -
see section 4.

---

## 2. What changed

Four small edits plus one new file. Total diff: 3 product/test files touched, 1 product file added,
1 test file added.

### `src/AgentEyes.App/HudSizeMemory.cs` (NEW, 76 lines, no WPF in it)

The HUD's remembered size, modelled the same way `HudPreviewState` models the HUD's preview
decisions: the ordering-sensitive question is answered by something a test can call, and the window
above it does layout only.

```csharp
public void Observe(bool manuallySized, double width, double height)
{
    if (!manuallySized) return;                       // the pill is never a remembered size
    if (!(width > 0) || !(height > 0)) return;        // a layout that has not happened is not a size
    _width = width; _height = height;
}
```

The rule is: **a size is remembered WHEN IT IS TRUE, not when it is needed.** An auto-sized report is
IGNORED, never destructive - the window auto-sizes on its way out of every recording, and forgetting
there would reproduce the very defect being fixed (that is mutation M20).

### `src/AgentEyes.App/HudWindow.cs` - four edits

| Where | Change |
|-------|--------|
| field list (line 80) | added `private readonly HudSizeMemory _size;` |
| constructor (line 87) | `_size = new HudSizeMemory(cfg.HudWidth, cfg.HudHeight);` - seeded from the config the previous HUD wrote |
| constructor (line 324) | `SizeChanged += (_, _) => _size.Observe(SizeToContent == SizeToContent.Manual, ActualWidth, ActualHeight);` - every size the window takes is offered as it happens |
| `ApplyPreviewState` (lines 503-504) | the panel now opens at `_size.Width ?? DefaultPreviewWidth` instead of reading `_cfg.HudWidth` - so hide-then-show inside ONE recording also returns to the size it was left at, with nothing written to disk in between |
| `SavePosition` (lines 690-708) | writes `_size.Width`/`_size.Height` when `_size.HasSize`, and reads NO live window state; added a `Log.Info` line naming exactly what is being persisted |

The new log line is a deliberate instrument for QA:

```
hud: saving position left=1537 top=16 width=1600 height=760
```

`width=none height=none` is what an unresized HUD prints - the fields are still not written when the
preview panel was never shown, so AC11's "identical to before" is untouched.

### `tests/AgentEyes.Tests/HudSizeMemoryTests.cs` (NEW, 20 tests)

Section 4.

### `docs/cencon/proof/issue-33/mutation-evidence.py`

Four new mutations, M19-M22 (section 5). No existing mutation was altered.

---

## 3. Why the change cannot reach AC3 or AC10

QA independently established the two properties that make this feature safe, and they are exactly
what a careless "fix" here could break. It cannot, and this is checkable rather than asserted:

- `git diff --stat origin/issue-33-hud-live-preview` names **no file** under
  `src/AgentEyes.Core/` at all. The camera device, `PreviewTap`'s drain, `FfmpegArgs`, and
  `RecordingService` are byte-identical. Nothing in this diff can open a device or stop a drain.
- Inside `HudWindow`, the diff touches only sizing. `ClosePreview()`, `SetPreviewPublishing(...)`,
  `SetPreviewOverlayCorner(...)`, `ApplyWindowStyles()` and the `_feed` wiring are unchanged.
- M4, M5, M9, M13, M14 and M18 - the mutations that guard the AC3/AC10/AC11 behaviour - were re-run
  after this change and all still FIRE (section 5). The instruments that protect those criteria are
  intact and still capable of failing.

---

## 4. The regression test, and the trap it was written around

`tests/AgentEyes.Tests/HudSizeMemoryTests.cs`, 20 tests. The load-bearing ones replay the SEQUENCE,
because the bug lives in the ordering:

```csharp
memory.Observe(manuallySized: true,  520, 400);    // panel opens
memory.Observe(manuallySized: true, 1600, 760);    // the person resizes
memory.Observe(manuallySized: false, 367,  52);    // the stop: SetStatus auto-sizes back to the pill
Assert.Equal(1600, memory.Width);                  // <- what round 1 lost
```

plus the full round trip (`..._NextRecordingOpensAtTheResizedSize`, which seeds a second memory from
what the first one persisted), the "resize -> hide the preview -> stop" variant, hide-and-show inside
one recording, and the negative cases (an auto-sized-only life remembers nothing; a non-positive size
is not a size and does not destroy an earlier one; a half-written config is "never resized").

### It was demonstrated FAILING against the current code first

Committed at `docs/cencon/proof/issue-33/round2/tests-before-the-fix.txt`. The two compiled-IL guards
were run against the round-1 `HudWindow` (the new class existed, the window was not yet wired to it):

```
Failed AgentEyes.Tests.HudSizeMemoryTests.SavePosition_DoesNotReadTheWindowsLiveSizeAtCloseTime
  HudWindow.SavePosition reads the window's live sizing state at close time:
  System.Windows.Window::get_SizeToContent, System.Windows.FrameworkElement::get_ActualWidth,
  System.Windows.FrameworkElement::get_ActualHeight. ...

Failed AgentEyes.Tests.HudSizeMemoryTests.HudWindow_OffersEverySizeItTakesToTheMemory
  Nothing in AgentEyesApp calls HudSizeMemory.Observe, so the memory can never hold a size and the
  HUD cannot come back at the size it was left at (issue #33, AC7).

Failed!  - Failed: 2, Passed: 18, Skipped: 0, Total: 20
```

Those two read the **compiled IL of AgentEyesApp.dll** through the existing `CompiledCode` helper -
`CallsIn` and `CallSites` THROW rather than return empty when the method or assembly is missing, so
neither can pass by finding nothing. They are the bridge between the decision object and the window
that has to make the decision, and they were red against the code QA failed.

### What these tests CANNOT see - stated, not implied

They drive `HudSizeMemory`, not a WPF window. No unit test in this repo starts one, and one that did
would call `Config.Save()` against the developer's real `%LOCALAPPDATA%\AgentEyes\config.json` -
destroying the human's live v1.6.2 settings. What stays outside every check here is **whether WPF
actually raises `SizeChanged` for a given layout transition.** That is a runtime fact and QA's
reproduction in section 6 is the instrument for it. This limit is written into the test file's own
doc comment as well as here.

---

## 5. Mutation evidence - all 22 FIRED

`python docs/cencon/proof/issue-33/mutation-evidence.py`, re-run in full after the change; recorded in
`docs/cencon/proof/issue-33/mutation-evidence.txt`. The 18 from round 1 still fire; four are new and
each breaks the fix in the shape a careless implementation would:

| # | Known-bad implementation | Result |
|---|--------------------------|--------|
| M19 | the memory keeps whatever the window last reported, pill included | FIRED - 6 failed |
| M20 | the memory FORGETS on an auto-sized report (the measured shape: the stop clears it) | FIRED - 5 failed |
| M21 | `SavePosition` goes back to reading the window's live size at close time (**the original bug**) | FIRED - 1 failed |
| M22 | the window never offers its sizes to the memory (a memory nothing feeds) | FIRED - 1 failed |

M21 is literally the round-1 code restored into the round-2 window; the guard turns red on it.

---

## 6. How QA should verify AC7 - the exact round-1 reproduction

Round 1's reproduction is the right instrument and it should be re-run unchanged. Everything below is
focus-free (UIA / `/status` / a file read); nothing needs the window in the foreground.

1. Back up and replace `%LOCALAPPDATA%\AgentEyes\config.json` with a fresh file carrying no `Hud*`
   keys. Restore it afterwards, as round 1 did.
2. Record; invoke the HUD's `Show preview` button by UIA name; stop. (This arms the next recording;
   see round 1's AC1 note - `PreviewArmed` on `/status`.)
3. Record again. The HUD opens with the panel showing.
   - Read its bounding rectangle - expected **520x400** on a fresh config.
4. `TransformPattern.Resize(1600, 760)` through UIA. Read the rectangle back.
   - Expected: `W=1600 H=760`, and the preview `Image` grows with it (round 1 measured 1184x666).
5. Click the HUD's own `HUD stop` button and let the stop finish.
6. Read `%LOCALAPPDATA%\AgentEyes\config.json`.
   - **Expected: `"HudWidth": 1600.0, "HudHeight": 760.0`** alongside `HudLeft`/`HudTop`.
   - Round 1 actual: `"HudWidth": null, "HudHeight": null`. That is the defect.
   - Cross-check in `%LOCALAPPDATA%\AgentEyes\logs`: the stop must print
     `hud: saving position left=<x> top=<y> width=1600 height=760`.
7. Start a new recording and read the HUD's bounding rectangle.
   - **Expected: `W=1600 H=760` at the same `X`/`Y`.** Round 1 actual: `W=520 H=400`.

Two further paths worth one pass each, because they were the second and third shapes of the same bug:

8. **Resize -> `Hide preview` -> stop -> record again -> `Show preview`.** Expected: the panel opens
   at 1600x760, not 520x400. (Round 1 lost the size here too.)
9. **Never show the preview at all: record, stop.** Expected: `HudWidth`/`HudHeight` stay `null` and
   the log prints `width=none height=none` - the pill's dimensions must never be saved, or the next
   preview panel would open at pill size. This is the AC11 side of the change.

**The three round-1 reminders still hold.** The focus-free layers are REST (`127.0.0.1:7882`), UIA and
PrintWindow; never force-foreground the app and synthesise input without warning the human; the HUD is
`WDA_EXCLUDEFROMCAPTURE`, so HUD state is read via UIA or `/status`, never from a full-screen grab.

**Smokes:** this change touches the HUD window only - no capture pipeline, no REST route, no installer.
`gui-smoke.ps1` is the relevant one if QA wants a sweep; `api-smoke.ps1` and `run-all.ps1` are not
implicated by this diff. QA decides.

---

## 7. Why no running-app proof is attached

DEVELOPMENT_METHOD.md section 3.2 / 6 item 5 assigns running-app proof to QA and the handoff note to
the developer, and there is a concrete hazard here on top of the method: **the human is running v1.6.2
as their live tray app on this machine.** A second `AgentEyesApp.exe` collides with it on port 7882 and
on the single `%LOCALAPPDATA%\AgentEyes\config.json` - the exact file this fix writes - while the
human may be recording. QA drove it in round 1 from a substituted config and restored it afterwards;
that is the safe way to produce this proof and it is QA's seat.

The developer gate that IS mine was run, by me, and is committed:

```
dotnet build AgentEyes.sln -c Release --no-incremental
  Build succeeded.
      2 Warning(s)      <- both pre-existing xUnit1031 in PostRecordingQueueTests.cs:309,314,
      0 Error(s)           a file this PR does not touch. QA was right that round 1's PR body
                           claiming "0 Warning(s)" was inaccurate; this note does not repeat it.

dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 1031, Skipped: 0, Total: 1031, Duration: 4 s
```

(1011 before, 1031 after: the 20 new `HudSizeMemoryTests`. No existing test removed or changed -
`git diff --stat` shows no modification to any existing test file.)

Raw output: `round2/gate-build.txt`, `round2/gate-test.txt`, `round2/tests-before-the-fix.txt`.

---

## 8. CenCon impact

**No drift.** The component map is unchanged (`HudSizeMemory` is a private helper inside
`AgentEyes.App`, alongside `HudPreviewState`). The privacy posture is untouched: nothing here records,
transmits, or reveals anything, and `WDA_EXCLUDEFROMCAPTURE` (assumption C5) is not gone near.

---

## 9. Note for the branches stacked on top of this one

`issue-36-circular-camera-overlay` also edits `HudWindow` (the circular camera inset in the HUD
preview). The overlap is small and worth naming so the rebase is boring:

- This change adds a field near the other preview fields, ONE line in the constructor's event-wiring
  block, TWO lines inside `ApplyPreviewState`'s `if (SizeToContent != SizeToContent.Manual)` block,
  and rewrites the body of `SavePosition`. Nothing in the preview LAYOUT path changed -
  `LayOutInset`, `_previewSurface`, `_cameraHost` and `_screenImage` are untouched, so #36's inset
  drawing does not collide with any of it.
- The one thing #36 must not undo: **do not restore a read of `ActualWidth`/`ActualHeight`/
  `SizeToContent` inside `SavePosition`.** The IL guard
  `SavePosition_DoesNotReadTheWindowsLiveSizeAtCloseTime` will turn red if it comes back, which is
  the point - but the message is easier to act on if it is expected.
- If #36 introduces any other place that changes the HUD's size programmatically, it needs nothing
  extra: the `SizeChanged` hook observes every size the window takes, whoever set it.

---

**I believe this is finished.** One defect, precisely the one QA reported; the persistence half of AC7
is implemented, the regression test was shown failing against the previous code before it was made to
pass, all 22 mutations fire, and the gate is clean at 1031 tests with nothing existing weakened.
