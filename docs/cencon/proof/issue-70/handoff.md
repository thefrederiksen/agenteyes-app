# Issue #70 - Developer handoff

**Issue:** [Always On] Counters do not say where clips are, or that a clip is still being recorded
**Tracker:** thefrederiksen/agenteyes-app #70
**Branch:** `issue-70-always-on-clip-location` (based on main 2652c19, which includes #72)

I believe this is finished. Build clean, `dotnet test` run by me, no new failures.

## What was implemented

| Area | File | Change |
|------|------|--------|
| Core | `src/AgentEyes.Core/AlwaysOn/AlwaysOnDay.cs` | `ClipPaths` (full path of every clip written today) persisted in `today.json`, cleared at day rollover. An old `today.json` (no list, or `"ClipPaths": null`) loads as an empty list. `Load` now logs. |
| Core | `src/AgentEyes.Core/AlwaysOn/AlwaysOnEngine.cs` | `AlwaysOnStatus` gains `OpenClipStartUtc`, `OpenClipElapsedSeconds`, `ClipsKeptToday` (list of `AlwaysOnClip {File, Folder, Path, Exists}`), plus pure helpers `OpenClipElapsedAt(now)`, `InProgressLine(now)`, `InProgressShort(now)`, `ClipDuration(seconds)`. Engine records each clip's start (first kept piece), appends the written path in `JoinClip`, and while `keeping` every keeper pass raises `Changed` so the page and tray minutes stay live (15 s tick). |
| App | `src/AgentEyes.App/MainWindow.xaml` + `MainWindow.AlwaysOn.cs` | New `AOClipNowText` line (UIA name "Always-on clip in progress"), visible only while `keeping`. New `AOTodayClipsList` (UIA name "Always-on clips kept today"): one link button per clip, label `<file> in <folder>` (`(no longer there)` when the file is gone); click -> `AOClip_Click` -> `AlwaysOnController.RevealClip`. Today line notes clips counted but not listed (kept before this version). No disk access on the UI thread - everything comes from the status snapshot. Line / list changes are logged once per change. |
| App | `src/AgentEyes.App/AlwaysOnController.cs` | `RevealClip(path)`: `explorer.exe /select,"<path>"`; throws a UsageException with the reason if the file is gone (no fallback to some other folder). Logged. |
| App | `src/AgentEyes.App/TrayDot.cs` | While `keeping`, tooltip line 2 is `Clip in progress, 12 min so far - saved to C:\AgentEyes after 5 min quiet.` (today's totals are dropped while keeping: both do not fit in the 127-char limit). |
| App | `src/AgentEyes.App/RestServer.cs` | `GET /always-on` adds top-level `openClip` (`{startUtc, elapsedSeconds, savedTo, afterMinutes}` or `null`) and `clipsKeptToday` (`[{file, folder, path, exists}]`). `status` also carries `OpenClipStartUtc`, `OpenClipElapsedSeconds` (as of the last keeper pass) and `ClipsKeptToday`. `openClip.elapsedSeconds` is computed at request time. Each read is logged. |

"Open clip start" rule: once the keeper has kept a piece, the clip began at that first kept piece.
Between hearing sound and the first decision, the only piece on disk is the one being written, so
the clip starts at the oldest piece in the work folder (documented in `OpenClipStart`).

## Acceptance criteria -> how QA verifies

1. **Page in-progress line while `keeping`.**
   Implemented: `AOClipNowText` = `status.InProgressLine(now)`, e.g.
   `Recording a clip now - 12 min so far. Saved to C:\AgentEyes after 5 min of quiet.`
   Minutes, folder and "after" value come from the live status (refreshed every 15 s keeper pass).
   QA: with always-on `keeping`, open the Always On page; read UIA element "Always-on clip in progress"
   (Text) and PrintWindow the main window. When `listening`, that element is Collapsed.
2. **Tray tooltip short form while `keeping`.**
   QA: unit test `Tooltip_KeepingWithAClipInProgress_SaysHowLongAndWhere` pins the exact text; live,
   read the NotifyIcon tooltip (hover or UIA on the notification area) while `keeping`.
3. **Today's clips listed with file name + folder; click opens the folder with the file selected.**
   QA: UIA list "Always-on clips kept today" -> each Button's Name is `<file> in <folder>`; Invoke one ->
   Explorer opens that folder with the file selected (log line `[AlwaysOnController] RevealClip: opened ...`).
   Note: clips kept today BEFORE this build are counted but not listed (no path was recorded);
   the today line says so ("N of today's clips were kept before AgentEyes listed where clips go...").
4. **`GET /always-on` open clip start + elapsed (null when none) + today's clips with full paths.**
   QA: `curl http://127.0.0.1:7882/always-on` while `keeping` -> `openClip.startUtc`, `openClip.elapsedSeconds`
   non-null; after the clip closes -> `openClip: null` and the new clip in `clipsKeptToday[].path`, with
   `exists: true` and the file present at that path.
5. **Logging, no UI-thread blocking.** New log lines: `[AlwaysOnDay] Load`, `[RestServer] AlwaysOnStatus`,
   `[MainWindow] UpdateAlwaysOnClips` (on change), `[MainWindow] AOClip_Click`, `[AlwaysOnController] RevealClip`.
   `UpdateAlwaysOnClips` reads only the in-memory snapshot (no File.* call); `File.Exists` per clip runs in
   `BuildStatus` on the engine's thread. HudResponsiveness IL guards show no new hit.
6. **Build clean, tests green, unit tests for new status fields.** New tests (all pass):
   - `AlwaysOnEngineTests`: `Status_NoClipInProgress_OpenClipIsNullAndNothingListed`,
     `Status_WhileKeeping_ReportsTheOpenClipsStartAndElapsedSeconds`,
     `Status_SoundHeardBeforeAnyPieceIsDecided_ClipStartsAtTheOldestWaitingPiece`,
     `Status_AfterTheClipCloses_OpenClipIsNullAndTheClipIsListedWithItsFullPath`,
     `Status_TodaysClips_SurviveARestartAndSayWhenAClipIsGone`,
     `Tick_WhileKeeping_RaisesChangedEveryPassSoTheRunningTimeStaysLive`,
     `Day_NewDate_ClearsTheClipList`, `Day_SaveAndLoad_KeepsTheClipList_AndAFileFromBeforeTheListLoadsEmpty`
   - `AlwaysOnAppTests`: `Tooltip_KeepingWithAClipInProgress_SaysHowLongAndWhere`,
     `Tooltip_KeepingWithALongFolder_KeepsTheInProgressFactAndCutsAtTheLimit`,
     `InProgressLine_StatesInProgressElapsedAndDestination`, `ClipDuration_RoundsDownToWholeMinutes` (6 cases),
     `OpenClip_ForTheApi_IsNullWithNoClipAndFreshElapsedWithOne`,
     `RevealClip_ClipNoLongerThere_ThrowsWithTheReasonInsteadOfOpeningAnotherFolder`

## Gate results (run by the developer, 2026-09-23)

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.` `0 Error(s)`
- `dotnet test AgentEyes.sln -c Release` on this branch -> `Failed: 4, Passed: 1714, Total: 1718`
- Same command on main 2652c19 (separate worktree) -> `Failed: 4, Passed: 1695, Total: 1699`
- The 4 failures are identical on main and are pre-existing, not introduced here:
  `HudResponsivenessTests.NothingTheHudsUiThreadCanReach_WritesAFile`,
  `PreviewTapTests.NothingOnARecordingsCriticalPaths_TouchesTheFilesystemOrTheSharedLogger`,
  `PreviewTapTests.NothingTheDrainCanReach_TouchesTheFilesystem`,
  `PreviewTapTests.NothingTurningThePreviewOffCanReach_TouchesTheFilesystem`.

## Mutation evidence (checks fire on known-bad code)

Each mutation applied to `AlwaysOnEngine.cs`, the issue-#70 status tests run, then the file restored:

| Mutation | Result |
|----------|--------|
| `JoinClip` does not append the clip path | FAIL x2: `Status_AfterTheClipCloses_...`, `Status_TodaysClips_SurviveARestart...` |
| `BuildStatus` never sets `OpenClipStartUtc` | FAIL x3: `Status_WhileKeeping_...`, `Status_SoundHeardBeforeAnyPieceIsDecided_...`, `Tick_WhileKeeping_...` |
| a keeping pass is not raised as `Changed` | FAIL x1: `Tick_WhileKeeping_RaisesChangedEveryPass...` |
| pre-decision start uses the NEWEST piece instead of the oldest | PASSES - equivalent mutant: in every reachable state of that branch only one piece is on disk (any finished waiting piece is kept on the same pass, which opens the clip and takes the other branch). Stated here rather than claimed covered. |

## What was NOT exercised live, and why

AgentEyes v1.11.0 is installed and running on this laptop (always-on to `C:\AgentEyes`; at the time of
writing `GET /always-on` on the installed instance reported `State: off`, 3 clips today). The app is
single-instance (global mutex `AgentEyes-singleinstance`) and owns port 7882 and the shared
`%LOCALAPPDATA%\AgentEyes` config, so the freshly built app cannot be started beside it without
stopping or disturbing the installed one - which I was told not to do. Therefore NO running-app proof
of the new page line, list, tooltip or API fields was produced by the developer. A headless check
confirmed the WPF binding the list uses (a private record's `Label`/`Path`) resolves.

## Suggested QA scope

- **gui** smoke area: Always On page (new text + list, click-to-reveal) - UIA / PrintWindow.
- **api** smoke area: `GET /always-on` only (read-only).
- Reminders: use REST / UIA / PrintWindow (focus-free); do not force-foreground + synthesize input
  without warning the human; the recording HUD is capture-excluded, so assert recording state via
  `/status` or UIA, not a screen grab. Running the new build live requires the installed instance to
  be out of the way - coordinate with the human before touching it.

## CenCon impact

No drift. No component-map change. Privacy posture ("visible / controllable") is strengthened, not
weakened: the app now says when a clip is being kept and where it will land.
