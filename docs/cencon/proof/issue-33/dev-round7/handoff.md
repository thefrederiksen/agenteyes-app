# Issue #33 - Developer round 7 (PR #39): the resize canary stops crying wolf

QA round 6 passed 11 of 12 and left ONE blocking defect. This round fixes that one defect and
nothing else. The two the Review Gate raised and QA closed with real stalls and a known-bad control
(defect 1, preview I/O on the recording's start and stop paths; defect 3, the config writer reverting
a newer setting) are untouched - not a line of `PreviewChores`, `PreviewLog`, `BackgroundFileWriter`
or `Config` changed.

- Gate: `gate.txt` - `Build succeeded. 0 Error(s)`; `Passed: 1137, Failed: 0`.
- Known-bad arms: `knownbad-both.txt`, `knownbad-noguard.txt`, `knownbad-noclear.txt`,
  driven by `mutate.py.txt`.

---

## The defect, restated in its own terms

`HudPreviewSizing.HidePanel` reported "A resize route is unaccounted for" on EVERY ordinary stop with
the preview panel up, on recordings where nothing had been resized. QA reproduced it 2/2.

The mechanism QA diagnosed is exactly right, and the diagnosis is what the fix is built on:

1. One stop calls `HudWindow.SetStatus` THREE times - `HudWindow.RunOnce` shows `"Stopping..."`
   for the HUD's own Stop button (`HudWindow.cs:375-381`), and MainWindow's `StopProgress.Saving`
   sink pushes `"Saving video..."` and `"Saving audio..."` as the raw files flush
   (`MainWindow.xaml.cs:1162-1166` fed by `RecordingStop.cs:125,127`).
2. Every one of those three calls ran the whole panel teardown, including `HidePanel`.
3. `HidePanel`'s FIRST call was correct: it compared 520x400 against the accounted 520x400, said
   nothing, and then set `SizeToContent = WidthAndHeight`, which collapsed the window to the pill's
   260x52.
4. Nothing moved the yardstick, so calls two and three compared the pill that step 3 had just
   produced against the panel size still held in `_accountedWidth/_accountedHeight` and reported a
   missing resize route. The canary was reporting the fix's own auto-size.

## The fix - both halves in `HidePanel`, none in the callers

The deeper question in the bounce was whether `SetStatus` being called three times, each re-running a
method that auto-sizes the window as a side effect, is itself the thing to fix. **It is, and the
right shape is a single idempotent call - but the guard goes on the method that HAS the side effect,
not on the caller.** That is the one lesson three rounds of this defect have in common: round 1's
canary was dropped by its caller, round 2's fix depended on the caller reading a return value, and
round 6's depended on the caller calling once. A caller-side `_finishing` guard would have fixed
today's symptom and been exactly as good as the next caller somebody writes. So `SetStatus` is
unchanged apart from a comment recording that it runs three times and that everything it reaches must
therefore be idempotent.

**1. `HudPreviewSizing.HidePanel` is a no-op when there is no panel up** -
`src/AgentEyes.App/HudPreviewSizing.cs:106-109`:

```csharp
if (window.SizeToContent != SizeToContent.Manual) return null;
```

This is the exact mirror of `ShowPanel`'s existing first-line guard
(`if (window.SizeToContent == SizeToContent.Manual) return;`, `HudPreviewSizing.cs:50`). The pair was
asymmetric: opening an already-open panel was a no-op, closing an already-closed one was not. Calls
two and three of a stop now return before they report, before they log, and before they re-assign
`SizeToContent`. One stop, one teardown, one `hud: preview panel down` line.

**2. `HudSizeMemory.NotePanelClosed()` retires the yardstick with the panel** -
`src/AgentEyes.App/HudSizeMemory.cs:144-172`, called from `HidePanel` right after the auto-size.
`_accountedWidth/_accountedHeight` mean "the size the window is supposed to have RIGHT NOW", and once
the panel is down the window is not supposed to be any particular size. `UnattributedSize` already
returned null when no panel had ever been opened; it now returns null in the symmetric state as well.

The persisted size (`_width/_height`, what `SavePosition` writes and what the next recording opens
at) is deliberately NOT touched. Hiding the preview and stopping the recording both come through
`HidePanel`, so forgetting it there would lose the person's size before it could be saved - that is
round 1's defect and it stays fixed (`ResizeToThreeTimesTheDefault_ThenStop_IsWhatTheNextRecordingOpensAt`
still asserts the whole two-recording round trip, now driven through the real three-call stop).

**Both halves are load-bearing and each is proved separately below.** The guard alone fixes QA's
reproduction. It does not fix the slower version of the same staleness: with the panel down the HUD
is a pill, the pill's own border can still be dragged, that gesture is deliberately NOT recorded
(the pill's dimensions are not a panel size - `HudUserResize.Record` narrows on
"the panel was up when the gesture began"), and it leaves the window manually sized - so the next
stop's teardown runs for real and a stale yardstick reports the pill's dragged size as a missing
PANEL route. Retiring the yardstick is what closes that one.

---

## The test, which QA correctly called the deeper problem

QA's finding: "the committed known-good test passes only because its rig calls `HidePanel` ONCE where
production calls it THREE times." That is the third rig-versus-production divergence on this issue.
It is closed at the rig, not per test.

`HudRig.StopRecording()` (`tests/AgentEyes.Tests/HudPreviewSizingOrderTests.cs:833-854`) now drives
the stop production performs: production's three status labels, in production's order, held in
`HudRig.StopStatusLabels = { "Stopping...", "Saving video...", "Saving audio..." }`, each one running
the teardown through production's own `HudPreviewSizing.HidePanel` rather than a copy. Every canary
report is kept in `HudRig.CanaryReports`, one entry per call, so a test can assert not only WHETHER
the canary spoke but HOW MANY TIMES. Every test on this file that says "the stop" now calls
`StopRecording()`; none of them calls `HidePanel` once and claims it is a stop.

**What the rig still cannot see, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6).** The
window carries the HUD's sizing shape, not `HudWindow`'s controls, so this drives the teardown
`SetStatus` runs and not `SetStatus` itself. That `SetStatus` reaches that teardown, and that the
ordinary stop reaches `SetStatus`, is asserted against the compiled IL in
`HudSizeMemoryTests.SetStatus_TakesThePanelDownThroughTheSharedSizingPath` and
`TheOrdinaryStop_ReachesTheReportingHidePanel` (both pre-existing, both still green). What no
in-process check proves is that the count STAYS three - it is three because of two call sites in two
files. The guard against a fourth is that the teardown is idempotent, which is what these tests
assert; the count in the rig is production's count today, evidenced by QA's own round-6 capture
(`../qa-round6/d2-canary-runs.txt`, three `hud: status ->` lines and three `hud: preview panel down`
lines per stop, on both builds).

### Both directions, and both demonstrated failing first

`mutate.py.txt` is the script that produced each arm; it edits only `HidePanel`, leaving the tests
untouched, so what changes is the production behaviour and not the instrument. The committed copy
was itself re-run at the end of this round, from the repo root exactly as its header documents,
and reproduced `Failed: 4, Passed: 19` - so the numbers below come from the artifact that is on
the branch, not from instrumentation that was never checked in.

| Arm | What was removed | Result | Evidence |
|---|---|---|---|
| The shipped round-6 code | both halves | **4 FAIL** / 19 pass | `knownbad-both.txt` |
| Guard only | `NotePanelClosed()` | **1 FAIL** / 22 pass | `knownbad-noclear.txt` |
| Yardstick only | the `SizeToContent` guard | **2 FAIL** / 21 pass | `knownbad-noguard.txt` |
| **The fix** | nothing | **0 FAIL** / 23 pass in this class, 1137 in the suite | `gate.txt` |

Quoted output, not the word "passed":

```
knownbad-both.txt  (the code QA failed)
  Failed ...AnUntouchedRecording_StoppedWithThePanelUp_SaysNothingAboutMissingRoutes
   Assert.DoesNotContain() Failure: Sub-string found
   String: ...person was 520x400. A resize route is una...
   Found:  "A resize route is unaccounted for"
  Failed ...AnOrdinaryStopWithNothingUnaccountedFor_SaysNothingAboutMissingRoutes   (same)
  Failed ...APillDraggedAfterThePanelCameDown_IsNotReportedAsAMissingPanelRoute     (same)
  Failed ...AResizeNoGestureClaimed_ReachesTheLogOnTheOrdinaryStop
   Assert.Equal() Failure: Values differ   Expected: 1   Actual: 3

knownbad-noguard.txt  (yardstick retired, teardown not idempotent)
  Failed ...AnOrdinaryStopWithNothingUnaccountedFor_SaysNothingAboutMissingRoutes
   Assert.Equal() Failure: Values differ   Expected: 1   Actual: 3      <- three panel-down lines
  Failed ...AnUntouchedRecording_StoppedWithThePanelUp_SaysNothingAboutMissingRoutes   (same)

knownbad-noclear.txt  (idempotent teardown, stale yardstick)
  Failed ...APillDraggedAfterThePanelCameDown_IsNotReportedAsAMissingPanelRoute
   Assert.DoesNotContain() Failure: Sub-string found
   Found:  "A resize route is unaccounted for"
```

The tests, and what each one is for:

| Test | Direction | Reads |
|---|---|---|
| `AnUntouchedRecording_StoppedWithThePanelUp_SaysNothingAboutMissingRoutes` (NEW) | QA's reproduction verbatim: panel up at the default 520x400 from the config, resize nothing, touch nothing, stop | the log: zero warnings, exactly one `hud: preview panel down`, all three reports null |
| `AnOrdinaryStopWithNothingUnaccountedFor_SaysNothingAboutMissingRoutes` (three-call stop) | a gesture-accounted size, stopped | the log: zero warnings, exactly one panel-down line |
| `APillDraggedAfterThePanelCameDown_IsNotReportedAsAMissingPanelRoute` (NEW) | the stale-yardstick case the guard alone misses | the log: zero warnings |
| `AResizeNoGestureClaimed_ReachesTheLogOnTheOrdinaryStop` (three-call stop) | **the canary still FIRES on a genuinely unrecognised route** - and exactly once | the log: the warning, the size in it, `Occurrences == 1` |
| `AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary` (unchanged) | the same, via the return value | the return value |

Every one of these reads the log window this run appended, and `LogAppendedSince` still fails the test
when that window is empty - an absence produced by a stop that never ran is a broken instrument, not
a clean run. The count assertions are safe against the rest of the suite because xUnit runs a class's
tests sequentially and no other test class writes these HUD lines; that is stated in the helper.

`APillDraggedAfterThePanelCameDown` also asserts `SizeToContent == Manual` before the stop, so the
absence it then claims cannot be the absence of anything happening at all.

---

## The gate

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.   0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed: 1137, Failed: 0, Skipped: 0
```

1135 -> 1137 is the two new tests; four existing tests were tightened, none weakened or deleted.
Built and run from an isolated worktree (`D:\ReposFred\agenteyes-dev33-r4`), `bin\x64\Release\`.

## What I did NOT do, and why

- **No heavy smoke, and no live recording this round.** The change is on the HUD's stop path, so the
  smoke would normally be targeted here. I did not run one because another QA session was actively
  driving the app and holding the camera at the time (`AgentEyes.Tests.exe` PID 21080 out of
  `agenteyes-qa35-r2`, plus the human's installed v1.6.2 tray app on port 7882). Taking the port,
  the camera, `config.json` and the shared log out from under a concurrent session would have
  corrupted its evidence, which is a worse outcome than deferring a probe QA owns anyway
  (DEVELOPMENT_METHOD.md 6/6b: the developer's gate is build + tests; QA produces the running-app
  proof). Nothing about the app was started, stopped or reconfigured; no config or preset file was
  touched; no ffmpeg was left behind.
- **`SetStatus` is not guarded by `_finishing`.** Making the caller call once would fix today's
  symptom, but the round-1 and round-2 defects were both caller-side, and the next caller would be
  free to reintroduce this. The idempotency is on the method with the side effect. The behaviour
  `_finishing` still owns - "once a Saving label is shown, the stop flow owns the close" - is
  unchanged.
- Nothing else on the branch was touched: no `PreviewChores`, `PreviewLog`, `PreviewTap`,
  `PreviewFrameFeed`, `BackgroundFileWriter`, `Config`, `FfmpegArgs` or camera-lifecycle change, so
  AC3/AC5/AC6/AC10/AC11, the drain's isolation, and #28's merged design on main are all untouched by
  construction. `git diff main -- src/` for this round is three files: `HudPreviewSizing.cs` (one
  guard, one call, comments), `HudSizeMemory.cs` (one method, comments), `HudWindow.cs`
  (comment only).

## How QA should verify this in the running app

The runtime check is small and specific - one recording, one stop, then read the log:

1. Fresh config with `HudPreviewVisible: true` (the shipped default), so the panel is up.
2. Start a recording, **resize nothing**, press the HUD's Stop.
3. In `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-<date>.log`, over that stop's window:
   - `hud: status ->` appears **three** times (production's call count is unchanged - this is the
     instrument, and if it is not three the check below proves nothing);
   - `A resize route is unaccounted for` appears **zero** times (round 6 saw two);
   - `hud: preview panel down` appears **once** (round 6 saw three).
4. The genuine direction, on the same build: resize the HUD through UI Automation's TransformPattern
   (the `gui-smoke.ps1` route) so the size IS accounted for, and confirm silence; then force an
   unaccounted size the way the round-6 report did and confirm the warning appears **once**, naming
   the size.

The focus-free layers are unchanged: REST on `127.0.0.1:7882`, UIA, PrintWindow. The recording HUD is
still capture-excluded (`WDA_EXCLUDEFROMCAPTURE`, `HudWindow.cs:766-791`, untouched), so HUD state is
asserted via UIA or `/status`, never a screen grab.

## CenCon impact

No drift. No component-map change, no privacy-posture change - the HUD is still visible and
controllable, and this round makes the app write strictly FEWER log lines about itself.

## Statement

I believe this is finished. The one blocking defect is closed in both directions, each direction was
demonstrated failing first against a mutation of the shipped code, and the rig that let it through
now drives the production stop sequence rather than a one-call imitation of it.
