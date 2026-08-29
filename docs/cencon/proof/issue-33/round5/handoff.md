# Issue #33 - Developer handoff, round 5 (the Review Gate's three blocking defects)

**Branch:** `issue-33-hud-live-preview`, rebased onto `main`
**PR:** #39 (replaces #34 - see "The rebase" below)
**Gate verdict answered:** `docs/cencon/review/pr34-issue33-gate-round1.md` (committed on this branch;
the gate never commits its own verdicts)

I believe this is finished.

---

## 0. THE REBASE - read this first, it changes what the diff is against

Issue #28 was approved and squash-merged to `main` as `e57d828`. Deleting its branch auto-closed
PR #34, whose base was that branch. GitHub cannot reopen a pull request whose base branch is gone
and cannot re-target a closed one, so:

- The eight #33 commits were rebased with `git rebase --onto origin/main e485561`
  (`e485561` was #28's branch head that #33 was cut from - NOT `91bbe1d`, which is #28's later
  round-8/9 verdict commit and is not an ancestor of this branch).
- **The rebase produced no conflicts.** #28's rounds 6-9 restructured `FfmpegCameraRecorder`'s
  termination history and stop-kind derivation; #33's changes to that file are the `preview`
  parameter on `Create` and the `FfmpegCameraProcess` constructor, which do not overlap those
  hunks. #28's merged design is untouched - verified by reading the three wiring files against
  `origin/main` after the rebase (`FfmpegCameraRecorder.cs`, `ICameraProcess.cs`,
  `FfmpegRecorder.cs`; the whole diff there is the stdout wiring and its comments).
- Branch force-pushed; **PR #39** opened against `main` with #34's body, amended to say why.
- Test count moved 1068 -> 1093 on the rebase alone: that is #28's rounds 6-9 tests arriving from
  `main`. It is now 1117 with this round's additions.

**For the two branches stacked on this one** (`issue-35-preset-editor-two-columns`,
`issue-36-circular-camera-overlay`) - what they must adapt to, all in files #33 owns:

| Changed | From | To |
|---|---|---|
| `HudPreviewSizing.HidePanel` | `void` | `string?` (the completeness canary; caller logs it) |
| `HudWindow.ApplyPreviewState(bool fromUser)` | one method with a flag | `ApplyPreviewState()` (no persistence) and `ApplyAndRememberPreviewChoice()` (applies + persists) |
| `HudWindow` config writes | `_cfg.Save()` | `_cfg.SaveWithoutBlockingTheUiThread()` |
| `PreviewTap` publishing | inline, on the drain thread | a publisher thread; `Publishing = false` no longer deletes the file synchronously |
| `PreviewTap.TryCreateAt` | `(track, framePath)` | `(track, framePath, Action<byte[]>? writeFrame = null)` |
| `MjpegFramer.Append` | logged its oversize drop | counts it only (`OversizeDrops`); `PreviewTap` logs it |
| new files | - | `src/AgentEyes.App/BackgroundFileWriter.cs`, `tests/AgentEyes.Tests/HudResponsivenessTests.cs` |
| `HudUserResizeTests.HudWindow_WiresUpAllThreeGestures` | - | renamed `HudWindow_WiresUpEveryGestureRoute` (a fifth assertion added) |

Nothing in #35's or #36's own area was touched.

---

## 1. GATE DEFECT 1 - publishing could block the only stdout drain (AC10)

**What was wrong.** `PreviewTap.Drain` called `Publish(frame)` synchronously between two pipe reads,
and `Publish` did `File.WriteAllBytes` + `File.Move` on that thread. Those threads are the only
readers of the screen and camera ffmpeg stdout pipes. A stall - a directory reparse point onto an
unavailable share, an NTFS or filter-driver hang - neither returns nor throws, so the catch never
runs, the pipe fills, and the ffmpeg writing `recording.mp4` / `camera.mp4` blocks on it. The
killed-directory evidence only ever proved that FAST exceptions are caught.

**What it is now.** The drain and the publisher are two threads joined by a bounded latest-frame
slot:

```
ffmpeg stdout --[drain thread]--> one-frame slot --[publisher thread]--> preview\<track>.jpg
```

- `PreviewTap.Offer` is the drain's entire contact with publishing: `Interlocked.Exchange` into the
  slot plus one `AutoResetEvent.Set`. Wait-free; no lock the publisher could hold while stuck in a
  filesystem call; no queue that can grow. A superseded frame is COUNTED (`FramesDropped`), so
  `FramesPublished + FramesDropped` accounts for every frame the drain offered (at most two are in
  flight at any instant).
- `Publishing = false` (the HUD's Hide click, on the WPF UI thread) now REQUESTS the frame-file
  delete and the publisher performs it. The setter does no I/O.
- **The drain no longer logs either.** `AgentEyes.Log` is a synchronous `File.AppendAllText` taken
  under a process-wide lock, so a `Log.Info` on the drain thread is exactly the same hazard. The
  drain enqueues notes and the publisher writes them; `MjpegFramer.Append` now counts its oversize
  drop instead of logging it, and `PreviewTap` reports the count from the publisher thread. This was
  found BY the new IL guard, not by inspection.
- `Dispose` joins the publisher with a bounded timeout and says so if it does not finish - a
  publisher wedged in a filesystem call is the scenario this design exists for, so a stop must never
  wait on it.

**How QA can check it**

| Check | Where |
|---|---|
| Behavioural: the drain reaches end of stream with all 40 frames while the publisher is provably inside a 30-second stalled write | `PreviewTapTests.Drain_WhilePublishingIsStalledForever_StillReadsThePipeToTheEnd` |
| Behavioural: hiding the preview returns in <200ms while the publisher is stalled | `PreviewTapTests.TurningThePreviewOff_WhileThePublisherIsStalled_ReturnsAtOnce` |
| Structural (IL, transitive): nothing reachable from `PreviewTap::Drain` touches the filesystem | `PreviewTapTests.NothingTheDrainCanReach_TouchesTheFilesystem` |
| Structural (IL, transitive): nothing reachable from `PreviewTap::set_Publishing` touches the filesystem | `PreviewTapTests.NothingTurningThePreviewOffCanReach_TouchesTheFilesystem` |
| Runtime, worth a live pass: `/status` `previewScreenFramesRead` climbing while the panel is shown, and `recording.mp4` duration inside #28's bound | REST `127.0.0.1:7882` |

The stall is injected at ONE seam - `TryCreateAt`'s optional `writeFrame` delegate. Everything else
in those tests (the framer, the slot, both threads, every counter) is production code. A real
filesystem cannot be made to hang inside a unit test, which is exactly why the round-1 evidence
could not reach this.

---

## 2. GATE DEFECT 2 - the allowlist dropped maximize and Windows snap (AC7)

**What was wrong.** The Win32 route recorded only a modal loop containing `WM_SIZING`. The gate
measured that a user maximize produces `WM_SYSCOMMAND 0xF030`, `WM_WINDOWPOSCHANGED`, `WM_SIZE` and
NO `WM_ENTERSIZEMOVE` / `WM_SIZING` / `WM_EXITSIZEMOVE`, so the maximized size was never recorded.
Aero Snap is the same class of miss from the other direction: it resizes the window through a MOVE
loop that sends no `WM_SIZING`.

**What it is now** - four positively-identified routes, not three:

1. **The modal loop**, recognised as a resize when EITHER `WM_SIZING` arrived OR the window came out
   of the loop a different size than it went in. The second arm is what catches Aero Snap. A plain
   move produces an identical size and still records nothing. An unpaired `WM_EXITSIZEMOVE` records
   nothing either (there is no starting size to compare against).
2. **A window-STATE change** - `Window.StateChanged`, read one dispatcher turn later at Background
   priority so WPF's layout (Render priority, higher) has settled the size. `Minimized`, and the
   restore FROM `Minimized`, are excluded: a restore puts a size back rather than choosing one.
3. The panel grip. 4. UI Automation's TransformPattern. (Both unchanged.)

**The structural weakness the gate named, and what was done about it.** The gate's point was that a
hand-listed allowlist proves its members but not its own exhaustiveness, and that adding maximize as
a fourth hand-listed member does not fix that. Two things carry that weight now, and neither is
another list:

- **The newest route's identification is a claim about the COMPILED CODE, not about Windows.** A
  `WindowState` change is treated as the person's doing *because nothing in AgentEyesApp ever assigns
  this window's `WindowState`* - so every state change it sees came from outside the app.
  `HudUserResizeTests.NothingInTheHudEverSetsItsOwnWindowState` reads that off the IL. The day
  somebody adds an app-driven maximize to the HUD, the route stops being a positive identification
  and the suite says so, by name, with the fix in the failure message.
- **The code now DETECTS its own incompleteness.** `HudSizeMemory.UnattributedSize` compares the size
  the HUD actually ended up at against the size it was opened at or last recorded, and
  `HudPreviewSizing.HidePanel` returns that description at the last instant it can still be asked
  (the next assignment auto-sizes the window back to the pill). A fifth resize route that nobody has
  enumerated is then a WARNING naming the size, instead of a silently wrong config. It deliberately
  does NOT record the size - a size nobody was shown to have chosen is the defect this whole design
  exists to prevent.

That is stated as a limit, not as a proof: no in-process test can demonstrate that Windows has no
fifth way to resize a window. The honest claim is written into
`Record_IsOnlyEverReachedFromAPositivelyIdentifiedGesture`'s own doc comment.

**How QA can check it**

| Check | Where |
|---|---|
| Snap (a move loop that ended at a different size) is remembered - end to end, real WPF window, real window messages | `HudPreviewSizingOrderTests.SnappingTheWindowToAScreenEdge_IsRemembered` |
| Maximize is remembered, and the window really grew | `HudPreviewSizingOrderTests.MaximisingTheWindow_IsRemembered` |
| Minimise + restore is NOT remembered | `HudPreviewSizingOrderTests.MinimisingAndRestoringTheWindow_IsNeverRemembered`, `HudUserResizeTests.AMinimiseAndRestore_RecordsNothing` |
| The same three at message level | `HudUserResizeTests.ALoopThatEndedAtADifferentSize_...`, `AWindowStateCommand_...`, `AnExitWithNoLoopBeforeIt_RecordsNothing` |
| The canary fires on a size no gesture claimed (known-bad) and stays silent on one a gesture did (known-good) | `HudPreviewSizingOrderTests.AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary`, `AResizeAGestureClaimed_IsNotReportedByTheCompletenessCanary`, `AHandsOffRecording_ReportsNoUnattributedSize` |
| The WindowState claim | `HudUserResizeTests.NothingInTheHudEverSetsItsOwnWindowState` |
| Runtime, worth a live pass: maximize the HUD via UIA, stop, start again, confirm it opens maximized-size | UIA + `%LOCALAPPDATA%\AgentEyes\config.json` |

**Round 4's inversion is intact.** Nothing subscribes to `SizeChanged` or `LayoutUpdated`; all nine
hands-off route tests, `AResizeWithNoGestureBehindIt_IsNeverRemembered`,
`MovingTheWindowWithoutResizingIt_IsNeverRemembered` and
`DraggingThePillsBorderWhileThePanelIsDown_IsNotAPanelSize` are unchanged and green.

---

## 3. GATE DEFECT 3 - synchronous file I/O on the WPF UI thread

**What was wrong.** The HUD's click handlers reached `Config.Save`, a synchronous `File.WriteAllText`,
straight off the dispatcher that serves the STOP button. The constructor made it worse by passing
`fromUser: true`, so every HUD ever built rewrote config.json while it was being put on screen -
against what the adjacent comment said.

**What it is now.**

- `BackgroundFileWriter` (new): a latest-wins slot and one writer thread. `Queue(text)` is an
  interlocked swap and an event set - no I/O. Superseded writes and failures are COUNTED
  (`Superseded`, `Failures`), not swallowed. `Flush(ms)` is the bounded shutdown wait.
- `Config.SaveWithoutBlockingTheUiThread()` serialises on the caller's thread (in-memory,
  microseconds - and it is what stops the writer ever seeing a half-changed object) and queues the
  bytes. `Config.Save()` is unchanged in behaviour for the launcher's modal dialogs. Both go through
  one `WriteJson` under one lock, so the two paths cannot land on the file at the same moment.
- The writer's thread is started by `Config.Load()` - at startup, before any window exists - and
  never lazily from a UI path. That is load-bearing: it is what keeps the write loop out of the call
  graph reachable from the HUD, so the IL guard measures the UI thread's own work.
- `App.OnExit` flushes a pending save (bounded, 2s), so a choice made moments before exit is not lost
  to the design that made choosing it quick.
- The `fromUser` flag is gone. It is two methods now - `ApplyPreviewState()` persists nothing,
  `ApplyAndRememberPreviewChoice()` is what a click calls - because an argument is invisible to a
  call-graph guard and a call is not. The constructor calls the first.

**How QA can check it**

| Check | Where |
|---|---|
| Behavioural: a save returns in <200ms while the write is stalled for 30s | `HudResponsivenessTests.Queue_WhileTheWriteIsStalled_ReturnsAtOnce` |
| Behavioural: the newest state reaches the file; the superseded one is counted | `HudResponsivenessTests.Queue_TwiceInARow_...` |
| Behavioural: a throwing write is counted and the writer keeps working | `HudResponsivenessTests.Queue_WhenTheWriteThrows_...` |
| Structural (IL, transitive over 16 UI-thread seeds): nothing the HUD's UI thread reaches writes a file | `HudResponsivenessTests.NothingTheHudsUiThreadCanReach_WritesAFile` |
| The companion presence: every preview button still persists the choice | `HudResponsivenessTests.EveryPreviewButton_RemembersTheChoice` |
| The apply itself persists nothing | `HudResponsivenessTests.ApplyingThePreviewState_NeverRemembersAChoiceByItself` |
| The writer thread is started from `Config.Load` and the flush from `App.OnExit` | `HudResponsivenessTests.TheConfigWritersThread_...`, `ApplicationExit_FlushesAPendingConfigSave` |

---

## 4. What these checks CANNOT see - stated, not implied (DEVELOPMENT_METHOD.md 6c.5/6c.6)

1. **The shared logger is a known, unfixed synchronous file append on UI threads.**
   `AgentEyes.Log.Write` does `File.AppendAllText` under a process-wide lock. The HUD calls it, and so
   does every other window in this app, on every UI thread, and has since long before this issue. It
   lives in AgentEyes.Core, so it is outside the App-assembly closure the HUD guard walks - that is a
   real limit of that guard and NOT a claim that it is safe. It IS fixed where AC10 makes it fatal
   (the preview drain and the publishing toggle, section 1). Making it non-blocking app-wide is its
   own work item with its own risk (a line lost at a crash is a line lost from the crash report).
   **Suggested follow-up issue.**
2. **The IL closures stop at the assembly boundary.** A filesystem call the HUD reaches through a
   method in AgentEyes.Core is invisible to `NothingTheHudsUiThreadCanReach_WritesAFile`. The one
   that matters is covered separately by the Core-side guard on `set_Publishing`.
3. **A call-graph guard cannot see what the CONSTRUCTOR itself calls.** Every HUD button's Click
   handler is a lambda declared in the constructor, and the IL folds a lambda back into its declaring
   method, so everything any button can do is "reachable from `.ctor`" by construction. Mutation M10
   below puts the constructor's save back and NO test fires - recorded rather than hidden. What is
   guarded instead is the property that makes the constructor's choice safe: `ApplyPreviewState` has
   no path to a save at all, and every button does.
4. **The gesture allowlist still cannot prove its own exhaustiveness.** Section 2 - the runtime canary
   is the answer, and it detects rather than proves.
5. **A write reached through a delegate FIELD is invisible to a call-graph guard.** That shape is
   covered behaviourally instead (`Queue_WhileTheWriteIsStalled_ReturnsAtOnce`).

---

## 5. Evidence in this folder

| File | What it is |
|---|---|
| `green-round5.txt` | `dotnet build ... --no-incremental` (Build succeeded, 4 pre-existing warnings, 0 errors) and `dotnet test` (**Failed: 0, Passed: 1117**) |
| `red-against-round4.py` | The mutation harness: puts each defect back, one at a time, rebuilds, runs the whole suite, restores the file byte-exactly |
| `red-against-round4.txt` | Its recorded run - **ten mutations, every expected check fired** |

Mutation summary (full output in `red-against-round4.txt`):

| # | Defect put back | Tests that went RED |
|---|---|---|
| M1 | the drain publishes inline again | `Drain_WhilePublishingIsStalledForever_...`, `NothingTheDrainCanReach_TouchesTheFilesystem` |
| M2 | hiding the preview deletes the frame on the caller's thread | `NothingTurningThePreviewOffCanReach_TouchesTheFilesystem` |
| M3 | the drain logs through the shared logger | `NothingTheDrainCanReach_TouchesTheFilesystem` |
| M4 | a snap is not treated as a resize | `SnappingTheWindowToAScreenEdge_IsRemembered`, `ALoopThatEndedAtADifferentSize_...` |
| M5 | a maximize is invisible again | `MaximisingTheWindow_IsRemembered`, `AWindowStateCommand_...`, `HudWindow_WiresUpEveryGestureRoute` |
| M6 | the app sets the HUD's own WindowState | `NothingInTheHudEverSetsItsOwnWindowState` |
| M7 | the completeness canary never fires | `AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary` |
| M8 | the HUD writes config.json on the UI thread again | `NothingTheHudsUiThreadCanReach_WritesAFile`, `EveryPreviewButton_RemembersTheChoice`, `TheHudSavesItsChoices_...` |
| M9 | the background writer writes on the caller's thread | `Queue_WhileTheWriteIsStalled_ReturnsAtOnce`, `Queue_TwiceInARow_...` |
| M10 | the constructor remembers a choice again | **none - the documented limit, item 3 above** |

Also updated: `ManifestWriterIlTests.PinnedFileWrites` - the preview frame write moved from
`PreviewTap::Publish` to `PreviewTap::WriteFrameToDisk` (publisher thread only), `Config::Save` to
`Config::WriteJson`, and `BackgroundFileWriter::WriteToDisk` is new. That inventory is the reason the
new writer could not be added quietly.

---

## 6. No running-app proof from the developer this round, and why

The gate policy asks the agent that changed the code to run the heavy smokes **when the change
touches that area**, and this one does. It was not possible here without disturbing the human:

- The installed v1.6.2 tray app is running from `C:\Users\soren\AppData\Local\AgentEyes\app\`. It
  holds the `AgentEyes-singleinstance` mutex and port 7882, so a second `AgentEyesApp.exe` from this
  worktree shows "AgentEyes is already running" and shuts down. Displacing the human's app to run a
  smoke is not a developer decision.
- The CLI never arms a preview (`RecordingService.PreviewArmed` is set only by the app), so
  `agenteyes.exe` exercises none of this.

So this round's gate is `dotnet build` + the full suite + the ten-mutation sweep, all in an isolated
worktree at `D:\ReposFred\agenteyes-dev33-r4` (`bin\x64\Release\`). **No ffmpeg process was started by
this round, and none is left behind.** The runtime passes worth QA's time are listed per defect
above; the AC9 60-second control/preview pairs from round 4 are the ones most worth repeating, since
the publishing path now has one more thread in it.

## 7. CenCon impact

No drift. The component map is unchanged (one new internal helper class in `AgentEyes.App`), and the
privacy posture is untouched: nothing here opens a device, adds an output, weakens
`WDA_EXCLUDEFROMCAPTURE`, or changes what reaches disk in a recording. AC3/C1 device ownership and
AC6/C5, which the gate ruled correct, were not touched.
