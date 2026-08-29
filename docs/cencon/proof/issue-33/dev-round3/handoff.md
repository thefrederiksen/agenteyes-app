# Issue #33 - Developer handoff, round 3 (Review Gate round 2 on PR #39)

Branch `issue-33-hud-live-preview`, PR #39. This round answers the three blocking defects in
`docs/cencon/review/pr39-issue33-gate-round2.md`, which is committed with this change.

I believe this is finished.

---

## The one sentence

Every defect this feature has had is the same sentence:

> **PREVIEW WORK CAN BLOCK SOMETHING THAT MUST NOT BLOCK.**

Round 1 moved publishing off the pipe-reading thread, and the Gate confirms that hole is closed.
Round 2 found the identical hazard one method further out - in the LIFECYCLE around the drain, and
in the config writer the round-1 fix itself introduced. This round fixes them as instances of that
one rule, and then AUDITS for the rest of the class rather than waiting for a fourth round to find
them.

Two new components in `AgentEyes.Core/Preview` carry the rule, and both have the same shape as the
frame slot that already worked: hand the work over, do not wait for it.

| Component | What it owns | What a caller pays |
|-----------|--------------|--------------------|
| `PreviewLog` | every log line the preview writes | an enqueue and an event set. Never waits. |
| `PreviewChores` | every filesystem operation the preview performs | a bounded wait (`BudgetMs` = 2000ms) - or none |

`AgentEyes.Log` is a `Directory.CreateDirectory` plus a `File.AppendAllText` under a **process-wide
lock**, so on these threads a log line is both a filesystem call and a lock another thread may be
holding inside one. That is why the logger is treated as I/O here and not as decoration.

---

## Defect 1 - recording start and stop could block on preview I/O, unbounded

**Fixed.** `PreviewTap` performs no filesystem call and no log append on any caller's thread.

- `TryCreateAt` (called by `RecordingService.StartVideo` for both tracks, before any writer starts)
  no longer creates the directory or deletes stale frames itself. It asks `PreviewChores.Prepare`
  and waits **at most 2000ms**. Missing that budget costs the preview - the tap is not created, a
  WARNING is logged, and the recording starts without a preview (AC10).
- `Dispose` (called by `RecordingService.Stop` before the service returns to idle) logs through
  `PreviewLog`, keeps its two bounded joins, and does **no** trailing flush or delete. The published
  frame is removed by the publisher on its way out; when the publisher was never started the removal
  goes to `PreviewChores` with the same budget; and when the publisher did **not** finish it is
  wedged in a filesystem call **on that exact path**, so the stop path deliberately does not follow
  it in - handing the same path to a second thread only wedges that one too. The cost is a stale
  frame file, removed by the next recording's preparation, and a WARNING.
- `RemoveFrameFile` and the drain's old `Note`/`FlushNotes` queue are gone; there is now one logging
  mechanism and one deleting mechanism, each owned by a thread nothing waits on.
- The write-frame delegate is resolved on the **publisher** thread rather than in the constructor, so
  the start path does not so much as name a file API.

### Why the tests are what they are

The Gate's note was explicit: QA's round-5 stall injected a delegate at `_writeFrame` while
`Dispose`'s real `RemoveFrameFile` and the shared logger ran against healthy local paths, so it
certified a stop path it never exercised. A behavioural test can only stall what it can reach, and a
real filesystem cannot be made to hang inside a unit test. So the claim is split, and each half is
tested where it can actually be settled:

1. **Which calls are on which thread** - read from the compiled IL, over the REAL production paths,
   transitively, spelling-independently:
   `PreviewTapTests.NothingOnARecordingsCriticalPaths_TouchesTheFilesystemOrTheSharedLogger`, seeded
   with all four threads a recording depends on (`Drain`, `TryCreateAt`, `Dispose`, `set_Publishing`)
   and flagging `System.IO` **and `AgentEyes.Log`**. Against the PR head it names 23 offending call
   sites, including the exact ones the Gate cited.
   Companion presence: `ThePreviewsFilesystemWork_StillHappens_OnTheChoresWorker` - the work did not
   simply disappear, and `PreviewChores::DoRemove` is now the only method in `AgentEyes.Preview` that
   calls `File::Delete`.
2. **That the stop path really does nothing itself** - measured, with a real thread genuinely wedged:
   `PreviewTapTests.Dispose_WhileThePublisherIsWedged_ReturnsAndDoesNoFileWorkOfItsOwn`. Two
   presences: Dispose returns inside its bounded joins, and **the published frame is still on disk**
   afterwards. The second is the discriminator - it is the evidence that the stopping thread
   performed no filesystem work - and it is exactly what the round-2 code fails.
   Companion presence: `TheNextRecording_RemovesAFrameAnEarlierOneCouldNotClearUp`.
3. **That a caller's wait is bounded** - measured, in `PreviewChoresTests`, against a worker that is
   provably stuck: `ACallerWaitingOnAWedgedWorker_GivesUpOnItsBudget`, with the broken-instrument arm
   (a run where the worker never stalled fails rather than passing). Each test uses its OWN worker;
   wedging the process-wide one would be a test that breaks every test after it.

---

## Defect 2 - the resize canary reported to nobody on the normal stop path

**Fixed at the production call site.** `HudPreviewSizing.HidePanel` now **logs the canary itself**,
between computing it and the auto-size that destroys the evidence. The explicit Show/Hide click no
longer logs it separately, so both routes - and any route added later - report identically. The
string is still returned, but only so a test can read the same words that were logged; no caller can
drop it any more, which is precisely how `HudWindow.SetStatus` came to drop it.

Tests:

- `HudSizeMemoryTests.HidePanel_ReportsTheUnaccountedSizeItself_BeforeTheAutoSizeDestroysIt` - IL, and
  ORDER not just presence: asked -> reported -> auto-sized.
- `HudSizeMemoryTests.TheOrdinaryStop_ReachesTheReportingHidePanel` - the companion, so the guard
  above cannot be satisfied by a `HidePanel` the stop path never calls.
- `HudPreviewSizingOrderTests.AResizeNoGestureClaimed_ReachesTheLogOnTheOrdinaryStop` - **behavioural,
  and it does not look at the return value at all.** It drives a real WPF window through the fifth
  resize route with no gesture behind it, takes the panel down the way `SetStatus` does, and reads
  **the log** for the words the canary exists to produce. Only what this run appended is read, so a
  line another test wrote cannot certify it, and an empty window fails as a broken instrument.
- `HudPreviewSizingOrderTests.AnOrdinaryStopWithNothingUnaccountedFor_SaysNothingAboutMissingRoutes` -
  the known-good arm, so the test above cannot pass over a canary that shouts at everything.

---

## Defect 3 - the background config writer could revert a newer setting

**Fixed by removing the second writer.** `Config.Save` no longer writes the file. It queues its
snapshot through the same `BackgroundFileWriter` every other save uses and then waits for it, bounded
at 2000ms (`BackgroundFileWriter.WriteNow`). One writer, one thread, one order: **the last save made
is the last save written**, whichever kind of caller made it. Blocking is now only about waiting for
the write, never about performing it.

That is the right shape rather than a sequence number or a merge, because the underlying fact is that
every save serialises the WHOLE document. With one document and one writer there is nothing to
sequence and nothing to merge - and a mutex was never going to help, because a mutex decides who goes
first, not who goes last.

Tests:

- `HudResponsivenessTests.ANewerBlockingSaveAfterAQueuedOne_LandsLast_AndTheOldShapeDoesNot` - the
  Gate's reproduction, deterministic, **both arms in one test**. The known-bad arm holds the writer
  inside the older snapshot while the newer one is written directly (the shape this branch shipped)
  and asserts the file ends up holding `"older queued snapshot"`; the shipped arm sends the newer save
  through the one writer and asserts `"newer blocking snapshot"`. This is the mixed
  synchronous/asynchronous case the existing latest-wins tests did not cover.
- `HudResponsivenessTests.EverySaveGoesThroughTheOneWriter_SoTheLastSaveMadeIsTheLastWritten` - the
  structural half: `Config::WriteJson` is named by exactly one thing, the field initializer that hands
  it to the writer. The moment any save path calls it again, the two kinds of writer stop being
  ordered.

---

## The audit (asked for, and it found more of the same)

Sweeping the feature for the same sentence turned up four more instances, all preview-owned code
introduced by this PR, all on the WPF dispatcher that serves the STOP button, all now on `PreviewLog`:

| Where | What it was |
|-------|-------------|
| `HudPreviewSizing.ShowPanel` / `HidePanel` | `Log.Info` on the dispatcher |
| `PreviewFrameFeed.Want` / `Start` / `Dispose` (and its reader loop) | `Log.Info` / `Log.Warn` on the dispatcher; `Dispose` is on the stop path |
| `HudUserResize` (all five lines) and `HudWindowAutomationPeer` | `Log.Info` on the dispatcher |
| `HudWindow.TogglePreview` / `ChooseMode` / `ChooseCorner` | `Log.Info` / `Log.Warn` in the click handlers |

Held shut by `HudResponsivenessTests.ThePreviewsOwnUiThreadCode_NeverCallsTheSharedLoggerDirectly`
(absence) plus its companion presence (each of those types still reports what it does). Against the
PR head that guard names nine offending call sites.

**What is deliberately NOT changed, and why.** The HUD's non-preview lines - the status label, the
discard click, the saved position, the window styles - and every other window in this app still call
the shared logger directly, exactly as they did before this feature existed. Making the whole app's
logger non-blocking is app-wide work with its own risks (a line lost at a crash is a line lost from
the crash report) and belongs in its own issue; this branch's own documentation already said so. This
issue owns the preview, and the preview is clean. `App.OnExit` now settles the preview log lane,
bounded at 1000ms, beside the existing bounded config flush.

---

## Every new test demonstrated failing first

The Gate's standing complaint is that green suites have coexisted with shipped regressions on this
issue. So every new test was run against something that does not have the fix.

**Against the actual PR head `c1eb48e`** - a separate worktree, head's production code, these tests
copied in: `docs/cencon/proof/issue-33/dev-round3/head-c1eb48e-new-tests.txt`

```
Failed!  - Failed: 6, Passed: 2, Skipped: 0, Total: 8
```

The two that pass are the deliberate companion-presence tests (they are meant to hold on both sides;
they exist so the guards beside them cannot be satisfied by an absence).

**By mutation, for the three tests whose subject did not exist on head** (they cannot be compiled
there): `docs/cencon/proof/issue-33/dev-round3/mutations.txt`. Each reverts ONE production line to
head's shape and re-runs.

| Mutation | Test | Result |
|----------|------|--------|
| `HidePanel` only RETURNS the canary | `AResizeNoGestureClaimed_ReachesTheLogOnTheOrdinaryStop` | FAIL - `Not found: "A resize route is unaccounted for"` (and the known-good arm stayed green) |
| `WriteNow` writes the file directly | `ANewerBlockingSaveAfterAQueuedOne_LandsLast...` | FAIL - `Expected: "newer blocking snapshot" / Actual: "older queued snapshot"` |
| a chore is carried out on the caller's thread | `ACallerWaitingOnAWedgedWorker_GivesUpOnItsBudget` | FAIL - `held its caller for 30005ms against a 250ms budget` |

---

## Gate

`docs/cencon/proof/issue-33/dev-round3/gate.txt`

```
dotnet build AgentEyes.sln -c Release --no-restore --no-incremental
  Build succeeded.  4 Warning(s)  0 Error(s)

dotnet test AgentEyes.sln -c Release --no-build --no-restore
  Passed!  - Failed: 0, Passed: 1135, Skipped: 0, Total: 1135
```

The four warnings are the pre-existing xUnit analyzer warnings in `PostRecordingQueueTests.cs` and
`StrandedCameraOwnerTests.cs`, both unchanged by this branch. The suite was run four times to check
the new log-reading and timing tests for flakiness; it was green every time. Built and run from an
isolated worktree, from `bin\x64\Release\`; the installed v1.6.2 tray app was not displaced, no
ffmpeg was started, and no config or preset file was touched.

## No existing test was weakened

1117 -> 1135 tests, +18, none removed and none relaxed. Exactly one existing assertion changed:
`ManifestWriterIlTests`' pinned inventory of every file write in the product. Two entries
(`PreviewTap::TryCreateAt` and `PreviewTap::RemoveFrameFile`) became one (`PreviewChores::DoRemove`)
because that is where the deletes moved, and the note above the block now records WHICH THREAD each
preview write is on - which is the property this round is about. The inventory is the same size in
substance and is stricter in what it says.

## Not regressed

The Gate's confirmed-working list was left alone: the drain's isolation from publishing (round 1's
fix - still asserted by its own IL test, and now by a second, stricter one), AC3/C1 device ownership,
AC6/C5 `WDA_EXCLUDEFROMCAPTURE`, the opt-in-per-recording behaviour, and everything QA verified
across five rounds (AC1, AC2, AC4, AC5, AC7's persistence, AC8, AC9, AC11, AC12, and the inversion on
nine hands-off routes). `HudPreviewSizingOrderTests` and `HudUserResizeTests` are untouched apart from
the two added tests.

---

## How QA should verify this

Nothing here changes what the app does when the machine is healthy, so the interesting checks are the
unhealthy ones.

**Defect 1 - the recording lifecycle (the check that matters).** Point the preview directory at
something that does not answer. Rename `%LOCALAPPDATA%\AgentEyes\preview` aside and replace it with a
directory symlink or junction onto an unreachable UNC path
(`mklink /D "%LOCALAPPDATA%\AgentEyes\preview" "\\192.0.2.1\share"` - TEST-NET-1, guaranteed
unroutable), then:

1. `POST /record/start` on `http://127.0.0.1:7882` with the preview armed. **The recording must
   start** - within a second or two, not after a share timeout. `/status` shows it recording.
2. Let it run, then `POST /record/stop`. **Stop must return and `/status` must go back to `idle`**,
   not sit in `finalizing`.
3. The log must carry `[PreviewChores] Prepare for ... did not finish within 2000ms` (or a Prepare
   failure) and `[PreviewTap] TryCreate: no preview for the screen track`. The recording's own
   manifest and duration must be unaffected.
4. Remove the junction and restore the real directory afterwards.

**Defect 2 - the canary.** Start a recording, show the preview, resize the HUD through a route that
runs no modal loop and no `WindowState` change (Windows keyboard snap / Snap Layouts is the one QA
left unmeasured), leave the panel visible, and press Stop. Either the size is recorded by a gesture
route (fine), or the log carries `A resize route is unaccounted for (issue #33, AC7)` naming both
sizes. What must NOT happen is silence.

**Defect 3 - config ordering.** With a recording running: toggle the HUD preview (queues a snapshot),
then immediately change the capture folder in Settings, or a plugin, or run-at-login. Exit the app and
read `config.json`: the LAST change made must be the one on disk. Repeat with the order reversed.

**Everything else** is unchanged behaviour and is covered by the existing round-5 QA scripts under
`docs/cencon/proof/issue-33/qa-round5/`.

Reminders carried forward: the focus-free layers are REST / UIA / PrintWindow; never force-foreground
and synthesize input without warning the human; the HUD is capture-excluded, so HUD and recording
state is asserted via UIA or `/status`, never a screen grab.

## CenCon impact

No drift. No change to the component map and no change to the privacy posture - the preview still
publishes only to `%LOCALAPPDATA%\AgentEyes\preview`, never into a recording directory, and the
always-on recording indicator and hard user control are untouched. Two new internal classes inside an
existing component (`AgentEyes.Core/Preview`).
