# Issue #77 - Developer handoff

[Always On] History tab: show every decision, level and problem so a missing clip can be explained.
Branch: `issue-77-history-tab`.

## What changed

| Project / file | Change |
|----------------|--------|
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnHistory.cs` (new) | The event model (`AlwaysOnEvent`: `AtUtc`, `Kind`, `Severity`, `Text`, `Detail`), the filter (`HistoryFilter` All / Decisions / Levels / Problems, `AlwaysOnEvent.Matches`), and the store: one JSON line per event in `%LOCALAPPDATA%\AgentEyes\alwayson\history.jsonl`, loaded lazily (never on the UI thread), trimmed to today + 7 local days at load and at each day change (file rewritten via `.tmp` + move), `Appended` event for the live tab, `Events(since, filter)` newest first, `ParseFilter` for the API. A write that fails is logged and the event stays in memory - the history is a record OF the recording, never a condition FOR it. |
| `src/AgentEyes.Core/AlwaysOn/SilentMicRule.cs` (new) | The silent-microphone rule, pure (see below). |
| `src/AgentEyes.Core/Audio/MicEndpoint.cs` (new) | Reads a capture endpoint's Windows mute state and master volume through NAudio's `MMDevice.AudioEndpointVolume` (IAudioEndpointVolume): the default microphone, or the one the setup's mic name means - `MicEndpoint.Select` (pure): the exact friendly name, else the ONE name it is a prefix of (the WaveIn 31-character prefix). Throws a `UsageException` when there is none or when more than one could be meant (the candidates are named) - never a guess. |
| `src/AgentEyes.Core/AlwaysOn/SoundLog.cs` | Per-source last loud second (`LastLoudUtc(source)`, kept apart from pruning) and the minute's peak and power-average RMS per source (`Levels(source, from, to)`). |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnEngine.cs` | Records every event listed in the issue (below). `Start(options, pausedReason, why)` and `Stop(why)` carry who did it. Reads the mic endpoint at start and once a minute. Applies the rule every tick; the banner text is `AlwaysOnStatus.SilentMic` (plus `MicDevice`, `MicMuted`, `MicVolumePercent`). Constructor takes an `AlwaysOnHistory` and a mic-endpoint reader (tests inject both). |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnOptions.cs` | `DefaultHistoryFile`. |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnCli.cs` | Passes "command line" as the why; keeps its history beside its own `-cli` work folder. |
| `src/AgentEyes.App/AlwaysOnController.cs` | `History` property; passes the caller's why ("Always On page", "control api", "restored at app start", "app exit") to the engine. |
| `src/AgentEyes.App/RestServer.cs` | `GET /always-on/history?since=<iso>&kind=<all|decisions|levels|problems>`; listed in `GET /`. Bad `kind` or `since` -> 400 with the reason. |
| `src/AgentEyes.App/MainWindow.xaml` | The Always On page is now a banner + a `TabControl` (`DkTab` style): tab "Recording" (the existing view, unchanged controls and automation names) and tab "History" (filter radios, status line, virtualized `ListBox`). Amber banner `AOSilentMicBanner` above the tabs. |
| `src/AgentEyes.App/MainWindow.AlwaysOnHistory.cs` (new) | The History tab: immediate "Loading...", `Task.Run` read + row build off the UI thread, collection handed to the list on the UI thread; live inserts dispatched with `Dispatcher.BeginInvoke`; warnings amber, errors red, `Detail` (ffmpeg's tail) as the row tooltip. |
| `src/AgentEyes.App/MainWindow.AlwaysOn.cs` | Wires the History tab; shows/hides the banner from the status snapshot. |
| `src/AgentEyes.App/App.xaml` | `DkAmber` / `DkAmberSurface` brushes (warnings; red stays for errors and the record state). |
| `tests/AgentEyes.Tests/AlwaysOnHistoryTests.cs` (new) | 47 tests, listed per criterion below. |
| `tests/AgentEyes.Tests/MicEndpointTests.cs` (new) | 8 tests on `MicEndpoint.Select` (exact / single prefix / ambiguous / none). |
| `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` | The three new file-write call sites (history append, trim rewrite + move) added to the pinned IL inventory with the file each writes. |

### Events recorded (issue list 1-6)

1. State: `Always-on started (<why>)` (+ ` - paused: <reason>` when it starts paused), `Always-on stopped (<why>)`, `Always-on paused: <reason>`, `Always-on resumed`; a start that fails: `Always-on could not start (<why>): <error>` (Error).
2. Level, once a minute: `Levels: mic floor=-96.7dBFS line=-50.0dBFS (auto); last 60s: loud=4 sustained=yes (4s); mic peak=-20.0dBFS avg=-31.8dBFS` (per counting source). Always Severity Info; ` [mic silent]` is appended to the TEXT while the silent-mic rule below holds - never on the floor alone. The rule's transitions (below) are the Problem events, not the minute lines.
3. Decision: `KEEP piece_... (60s) -> clip_...`, `DELETE piece_... (60s) - no sound within 10 s before it or 10 s after it`; a loose piece from an earlier run deleted at start (Warning).
4. Clip: `Clip saved: <path> - 13 m, 0.4 GB, 14 piece(s)`; `Clip deleted: <path> (...) - the clips were over the N GB cap`.
5. Problem: `Capture failed: <reason>; ffmpeg <state>. ffmpeg said: <last line>` (Error, `Detail` = the full 20-line tail), `Capture restarted (attempt n)...` (Info), `Restart attempt n failed: ...` (Error), `Recording again since HH:mm:ss (the capture failed at HH:mm:ss)` (Info), clip-continues-across-restart (Warning), keeper pass failed (Error), counters not writable (Error), unreadable/set-aside pieces and failed joins (Warning/Error).
6. Device, at start: `Devices: microphone "Headset Microphone (USB Audio)" - Windows: not muted, volume 80%; system sound not recorded; microphone counts. Setup "Default"` (Warning when MUTED).

## The silent-microphone rule as implemented (tester's finding on #77 applied)

The issue's original rule ("floor <= -90 dBFS for 2 minutes") is NOT implemented. The tester found the owner's mic floor reads -96.7 dBFS all day because the Default setup's noise gate zeroes the silence between words; that rule would raise a false banner every day.

`SilentMicRule.Evaluate(windowsMuted, lastLoudUtc, listeningSinceUtc, now)`:

1. **Windows reports the endpoint MUTED** (`IAudioEndpointVolume` via NAudio, read at start and re-read once a minute with the level line) -> silent at once, whatever the levels say.
2. Otherwise, **not one loud second** (RMS above the line) on the microphone since the capture began listening or since the last loud second, for **10 minutes** (`SilentMicRule.NoSoundAfter`) -> silent.
3. The measured floor is never an input. A gated floor of -96.7 dBFS with loud seconds present is NOT silent (`Tick_GatedFloorOfMinus96WithLoudSecondsPresent_DoesNotRaiseTheBanner` - twelve minutes of exactly the owner's pattern: no banner, no warning, every level line Info and still REPORTING `floor=-96.7dBFS`).

ASSUMPTION (the issue left N open after the tester's finding): N = 10 minutes - twice the default silence gap, so the end of an ordinary conversation is not flagged. Constant `SilentMicRule.NoSoundAfter`.

Banner text (the issue's sentence, then why): `The microphone is sending silence - check it is not muted. Windows reports it muted.` / `... No sound above the line for 10 min.` It is `AlwaysOnStatus.SilentMic` (null when not silent, while paused, while off, or when only the system sound counts) and shows on the Always On page above both tabs. The transition into and out of silence is one Problem event each: the banner text (Warning) in; out, `The microphone is sending sound again.` (Info) when the no-sound arm clears on a loud second, or `Windows no longer reports the microphone muted.` (Info) when the mute arm clears - each says only what happened. Cleared within one 15 s tick of a loud second; a Windows unmute is seen within a minute. The verdict is kept across a pause (the banner is hidden while paused); Resume re-reads Windows and records a transition only if the answer changed.

## Acceptance criteria -> how covered

| # | Criterion | Covered by | Tester |
|---|-----------|-----------|--------|
| 1 | History tab lists the events, newest first, local time, plain line, severity; warnings/errors stand out | Store order: `Append_ThenEvents_ReturnsNewestFirst_AndWritesOneJsonLinePerEvent`. Row helpers: `HistoryWhen_TodayShowsTheTime_OlderDaysShowTheDateToo`, `HistoryBadge_WarningsAndErrorsSayThePriority_InfoSaysTheKind`, `HistoryStatusText_SaysTheCountOrWhyThereIsNothing`. Every event kind: `Start_RecordsTheDeviceFactsAndTheStartWithItsReason`, `Tick_EveryMinute_RecordsALevelLineWithPeakAndAverage`, `Engine_KeepDeleteClipAndStop_AreRecorded`, `Tick_CaptureFails_RecordsTheErrorWithFfmpegsTail_TheRestart_AndTheRecovery`, `Pause_AndResume_AreRecorded_TheBannerHidesWhilePaused_AndAMutedMicIsWarnedAboutOnce`, `Start_CaptureFails_RecordsTheErrorAndRethrows`. Colours: warning rows `DkAmber`, error rows `DkRed`, badge WARNING/ERROR. | **PENDING TESTER**: PrintWindow screenshot of the History tab (procedure A). |
| 2 | Filters All / Decisions / Levels / Problems | `Matches_EachFilter_SelectsByKindOrSeverity` (9 cases; Problems = severity Warning/Error OR kind problem, so both ends of a silence or an outage show), `ParseFilter_AcceptsTheFourNamesAndRefusesOthers`, `Events_SinceAndFilter_ReturnOnlyWhatMatches`. Radios `History filter All/Decisions/Levels/Problems` -> `AOHistoryFilter_Changed` -> reload. | Tester clicks each radio (procedure A). |
| 3 | Opens immediately, loads off the UI thread, live updates | Code: `LoadAlwaysOnHistory` sets "Loading..." then `Task.Run(() => History.Events(...))` and builds rows off-thread; `OnAlwaysOnHistoryAppended` -> `Dispatcher.BeginInvoke` -> `ObservableCollection.Insert(0, ...)`. Store lazy-loads on first `Events`, never in the constructor (`AlwaysOnHistory` remarks). `Append_RaisesAppendedWithTheEvent`. | **PENDING TESTER**: live check (procedure B). |
| 4 | Silent/muted mic -> warning event AND banner; clears when the level returns | Rule: `Evaluate_WindowsMuted_IsSilentAtOnce_WhateverTheLevelsSay`, `Evaluate_LoudSecondsPresent_IsNotSilent`, `Evaluate_NoLoudSecondForTenMinutes_IsSilent`, `Evaluate_NoLoudSecondEver_CountsFromWhenListeningBegan`, `Evaluate_MuteStateUnknown_JudgesOnSoundAlone`, `Describe_BothReasons_StartWithTheIssuesSentence`. Engine: `Tick_NoLoudSecondForTenMinutes_RaisesTheBannerAndClearsWhenSoundReturns` (banner + warning + the level line's ` [mic silent]` text flag at 10:00, all level lines Info, cleared one tick after speech with "sending sound again"), `Tick_MuteClearsWhileTheCaptureIsDown_SaysWindowsNoLongerReportsItMuted_NotSoundAgain`, `Tick_GatedFloorOfMinus96WithLoudSecondsPresent_DoesNotRaiseTheBanner` (the tester's test), `Start_OnlySystemSoundCounts_NeverAsksWindowsAboutTheMicrophone_AndNeverRaisesTheBanner`. API: `GetAlwaysOn_ReportsTheSilentMicBannerAndTheMicState`. | **PENDING TESTER**: real muted-mic banner screenshot and the gated-floor no-banner check (procedure C). |
| 5 | Mute state and volume recorded at start; if muted, warning at once | `Start_RecordsTheDeviceFactsAndTheStartWithItsReason` (name, not muted, 80%), `Start_MicMutedInWindows_RaisesTheWarningAndTheBannerImmediately_AndClearsWhenUnmuted`, `Start_MicEndpointCannotBeRead_RecordsAWarningOnce_AndJudgesOnSoundAlone`. | Procedure C, step 1. |
| 6 | `GET /always-on/history?since&kind` returns the same events as JSON | Real HTTP: `GetAlwaysOnHistory_ReturnsTheEventsAsJsonNewestFirst_FilteredByKindAndSince`, `GetAlwaysOnHistory_BadKindOrSince_Is400WithTheReason` (also asserts the discovery entry). | Procedure D. |
| 7 | History survives a restart, trimmed to 7 days | `Events_ANewInstanceOnTheSameFile_ReadsWhatWasAppended`, `Load_EventsOlderThanSevenDays_AreDroppedAndTheFileRewritten`, `Append_OnANewDay_TrimsWhatFellOutOfTheRetention`, `KeepFromUtc_IsMidnightLocalOfTheDaySevenDaysBeforeToday`, `Load_ATornLine_IsSkippedAndTheRestKept`, `Append_FileCannotBeWritten_KeepsTheEventInMemoryAndLogsIt`, `Append_EmptyText_IsRefused`. | Optional: restart the app, the tab still lists yesterday's events. |
| 8 | Build clean, tests green, unit tests for recording, the rule, trimming, the API filter | Below. | - |

## Test run

`dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)`.
`dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1912, Skipped: 0, Total: 1912` (55 of the 1912 are new: 47 in `AlwaysOnHistoryTests`, 8 in `MicEndpointTests`). The first handoff's run was 1902/1902; the review fix pass below added 10.

## Self-review round (before handoff)

An independent review of the diff found two defects and several cleanups; all fixed on the branch, each with a test:

- The no-sound banner used to be cleared with a false "sending sound again" whenever the capture was down, and a capture restart reset the ten-minute clock (a flapping banner on every #81-style restart). Now `_listeningSinceUtc` is set only at Start and Resume, and while the capture is down only the mute arm can change the verdict. Test: `Tick_CaptureRestartsWhileTheMicIsSilent_TheBannerStaysAndNothingIsFalselyCleared`.
- A history file that could not be read was cached as empty, so the next day-change rewrite would have wiped seven days of events on disk. Now a failed read caches nothing, that call answers empty, and nothing is rewritten. Test: `Load_FileLocked_AnswersEmptyForThatCallOnly_NeverRewritesOverIt_AndReadsItNextTime`.
- Resume judged the rule on the mute state from before the pause; it now re-reads Windows first. Test: `Resume_ReReadsWindowsMuteState_SoAnUnmuteDuringThePauseRaisesNoFalseWarning`.
- The once-a-minute endpoint read moved OUT of the engine lock (a wedged audio service must not stall Pause/Stop/the keeper); the rule is judged once per tick, before the level line, so the line's flag is that judgement.
- The History tab retries the load after a failure (the next event, tab change or filter click) instead of inserting live rows into an incomplete list.
- `MicEndpoint.Read` and `AlwaysOnHistory.Events/Count` log their result (the repo's coding-standards logging rule).
- Level lines recorded while the rule held carried Severity Warning at first; the review fix pass below changed that (finding A).

## Review fix pass (after the independent read-only review of PR #88)

The review found no blocking defect; it left six non-blocking notes. Each is fixed on the branch, each behaviour change with a test:

- **A. Level lines are always Info.** A muted hour used to put one WARNING row per quiet minute under Problems - the false-banner-all-day problem in a new shape. `LogLevels` now records every level line as Info; while the rule holds the line's TEXT ends with ` [mic silent]` (`SilentMicRule.LevelFlag`). Only the two transition events are problems. Tests: `Tick_NoLoudSecondForTenMinutes_RaisesTheBannerAndClearsWhenSoundReturns` (all ten level lines Info, the tenth flagged in its text, no level line under Problems), `Tick_GatedFloorOfMinus96WithLoudSecondsPresent_DoesNotRaiseTheBanner` (no flag either).
- **B. Problems shows BOTH ends of a silence or an outage.** `HistoryFilter.Problems` now matches Severity Warning/Error OR Kind == problem, so the Info lines that end a problem (`Recording again since ...`, `The microphone is sending sound again.`, `Windows no longer reports the microphone muted.`) are read beside the warning that began it. Tests: `Matches_EachFilter_SelectsByKindOrSeverity` (9 cases), `Events_SinceAndFilter_ReturnOnlyWhatMatches`, and the Problems assertions in the two silent-mic engine tests. `GET /` still lists `kind?:all|decisions|levels|problems`; the route's doc comment and this note say what problems means.
- **C. MicEndpoint refuses an ambiguous name.** `Find` took the first active endpoint whose friendly name CONTAINED the fragment - two devices sharing "Microphone" made a silent guess. The rule is now `MicEndpoint.Select(fragment, names)` (pure, tested): exact name first; else a single StartsWith match (the WaveIn 31-character prefix); more than one candidate -> `UsageException` naming them with the fix ("use the full device name"); none -> `UsageException` listing the active devices. `Find` disposes every endpoint it does not return. Tests: `MicEndpointTests` (8).
- **D. The clear event says what happened.** When the MUTE arm clears, the event is `Windows no longer reports the microphone muted.` (`SilentMicRule.UnmutedText`) - true also while the capture is down (Retrying), when no sound can arrive. `The microphone is sending sound again.` is kept for the no-sound arm clearing on a loud second. Tests: `Tick_MuteClearsWhileTheCaptureIsDown_SaysWindowsNoLongerReportsItMuted_NotSoundAgain` (new), `Start_MicMutedInWindows_RaisesTheWarningAndTheBannerImmediately_AndClearsWhenUnmuted` (unmute -> UnmutedText, never ClearedText), `Tick_NoLoudSecondForTenMinutes_...` (speech -> ClearedText, never UnmutedText).
- **E. The append log line is truthful when the event is lost.** When the file could not be LOADED and the append fails too, there is no memory copy; the log now says `... and is LOST - the file could not be read earlier either, so it is not in memory`. When the file was loaded it still says `it is kept in memory`. Tests: `Load_FileLocked_AnswersEmptyForThatCallOnly_NeverRewritesOverIt_AndReadsItNextTime` (LOST), `Append_FileCannotBeWritten_KeepsTheEventInMemoryAndLogsIt` (kept). Both assert on their own file path because the test log is shared by the run.
- **F. Pause keeps the verdict.** Pause set `_silentMic = null` with no event and Resume recorded the same warning again - one warning row per pause/resume cycle for a mic muted all along. Pause now retains the verdict; `AlwaysOnStatus.SilentMic` is null while paused (the status layer hides the banner); Resume re-reads Windows and `UpdateSilentMic` records a transition only if the verdict changed. Tests: `Pause_AndResume_AreRecorded_TheBannerHidesWhilePaused_AndAMutedMicIsWarnedAboutOnce` (mute, pause, resume, pause, resume -> exactly one warning row, no cleared row, banner back after each resume), `Resume_ReReadsWindowsMuteState_SoAnUnmuteDuringThePauseRaisesNoFalseWarning` (unmuted during the pause -> one Info `Windows no longer reports the microphone muted.`, no second warning).

Accepted as is, per the review: N = 10 minutes as the named constant `SilentMicRule.NoSoundAfter` (stated assumption for the owner; can become a setting later); the unlocked reads of `_options`/`_state`/`_nextMicReadUtc` before the lock in `Tick`; history I/O under the engine lock; the two pre-existing shared-log flakes.

Gate after the fix pass: `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)`; `dotnet test AgentEyes.sln -c Release` -> `Failed: 0, Passed: 1912, Total: 1912`. One full run before that failed 4 tests outside this change (3 `CameraPreviewTests` on a stranded-camera report from a transient pid, 1 `AlwaysOnAppTests.FromJson_V111Config...` on the shared log file being locked); all 4 passed alone (41/41) and in the next full run - the same shared-run flake class as before, recorded here so QA is not surprised.

Honest note on flakes seen during development (checks-that-fail-open, item 6): one full run out of four failed
`AlwaysOnStallTests.Tick_CaptureFails_WritesTheFullTailAndTheProcessStateToTheLog` and
`PreviewChoresTests.SayingSomethingThroughThePreviewLog_ReachesTheLogWithoutTheCallerWaitingForIt`; both read the
shared per-run log while other tests write it (`FileShare.ReadWrite`), both passed alone (1/1 and 3/3) and in the
next two full runs. The baseline (main before this branch) also failed 3 tests on its first run and 0 on the second.
Not touched by this change; recorded here so QA is not surprised.

What the tests cannot see: the WPF tab itself (no WPF test host in this suite) - the two `MainWindow.*` files are
covered only by their pure helpers and by the build; the tester's screenshots are the check on them.

## PENDING TESTER - procedures (the developer session launched no app, by the owner's rule)

Preconditions: the branch built and deployed by the tester's normal route; a video setup with a microphone;
`agenteyes` Control API on `http://127.0.0.1:7882`.

**A. History tab screenshot (criterion 1, 2).** Always On page -> tab `History tab` (UIA name; the tab control is
`Always-on tabs`). Expected at once: the filter radios (`History filter All` ... `Problems`), the status line
(`Always-on history status`) reading `Loading...` then `N events, newest first - today and the last 7 days.` (or the
"No events yet" line on a fresh install), and the list (`Always-on history`). Then start always-on from the
Recording tab, talk for a minute, be quiet, and let 3+ minutes pass: the tab shows a `state` row
`Always-on started (Always On page)`, a `device` row with the mic's mute state and volume, `level` rows once a
minute, `decision` rows (`KEEP ...`, `DELETE ...`), and after the silence gap (or on Stop) a `clip` row
`Clip saved: <path> - ...`. For a problem row, lock the session (Win+L) for ~1 min or unplug a USB mic: an
`ERROR` row `Capture failed: ...` (hover shows ffmpeg's tail) and later `Capture restarted` / `Recording again since`.
PrintWindow the window with each filter selected. Expected vs Actual per filter: Decisions shows only KEEP/DELETE/Clip
rows; Levels only `Levels:` rows; Problems the WARNING/ERROR rows plus the Info rows of kind problem (`Capture restarted`,
`Recording again since`, the silent-mic clear lines) and never a `Levels:` row; warnings amber, errors red.

**B. Live update (criterion 3).** Keep the History tab open with always-on on. Expected: a new `Levels:` row appears
at the top every ~60 s without any click, and the status count increments by one each time. Also: navigate to the
Record page and back - the tab reloads and includes the events that arrived while it was hidden.

**C. Silent-microphone banner and the gated-floor check (criteria 4, 5).**
1. Mute the mic in Windows (Settings > System > Sound > Input > the device > Mute), then start always-on. Expected at
   once: the amber banner `Silent microphone banner` above the tabs reading
   `The microphone is sending silence - check it is not muted. Windows reports it muted.`; `GET /always-on` ->
   `status.SilentMic` = that text, `status.MicMuted` = true, `status.MicVolumePercent` = the slider; the History tab's
   `device` row says `Windows: MUTED` (WARNING) and a WARNING row with the banner text. PrintWindow it.
2. Unmute in Windows. Expected within ~60 s: the banner is gone, `status.SilentMic` = null, an Info row
   `Windows no longer reports the microphone muted.` (NOT "sending sound again" - nothing was heard yet). Under the
   Problems filter both rows show: the WARNING where the silence began and this Info row where it ended.
   Also: with the mic still MUTED, start a normal recording (always-on pauses) and stop it (always-on resumes) twice -
   Expected: the banner hides while paused and is back after each resume, and the History tab has exactly ONE
   silent-mic WARNING row for the whole sequence.
3. THE TESTER'S FINDING: with the mic unmuted and the Default setup's noise gate ON, talk normally for 12+ minutes
   with pauses. Expected: NO banner at any time; the `level` rows read `mic floor=-96.7dBFS` (or whatever the gate
   outputs) with `loud=` > 0 in the minutes you talked, all `level` severity Info; `GET /always-on/history?kind=problems`
   returns no silent-mic warning.
4. Optional: unmuted, say nothing at all for 10 minutes (a quiet room, or the mic covered). Expected at 10:00: the
   banner with `No sound above the line for 10 min.`, ONE WARNING row, and the 10th level row (still Info, not under
   Problems) ending ` [mic silent]`; speak -> cleared within 15 s with the Info row `The microphone is sending sound again.`.

**D. API (criterion 6).**
`curl "http://127.0.0.1:7882/always-on/history"` -> `{ kind: "all", count, retentionDays: 7, file, events: [...] }`,
newest first, each `{ atUtc, atLocal, kind, severity, text, detail }`.
`curl "http://127.0.0.1:7882/always-on/history?kind=problems"` -> every warning/error plus every event of kind `problem`
whatever its severity (restarts, recoveries, the silent-mic clear lines); never a `level` event.
`curl "http://127.0.0.1:7882/always-on/history?since=2026-09-24T09:00:00Z&kind=levels"` -> level rows from 09:00 UTC.
`curl -i "http://127.0.0.1:7882/always-on/history?kind=errors"` -> `400 { "error": "kind must be all, decisions, levels or problems, got 'errors'.", "code": "bad_request" }`.
`curl "http://127.0.0.1:7882/"` lists `GET /always-on/history {since?, kind?:all|decisions|levels|problems}`.
The file: `%LOCALAPPDATA%\AgentEyes\alwayson\history.jsonl`, one JSON object per line.

## Smokes

The change touches the Always On page (GUI) and the Control API: `gui-smoke.ps1` and `api-smoke.ps1` are the
relevant sweeps; the developer session ran neither (owner's rule - no app launch from this session). The Recording
tab keeps every control and automation name it had, inside the `DkTab` template whose `PART_SelectedContentHost`
keeps tab content visible to UIA.

Reminders: drive the app through the REST API / UIA / PrintWindow only; never force-foreground and synthesize input
without warning the owner; the recording HUD is capture-excluded - assert state via `/always-on` or UIA, not a grab.

## CenCon impact

No drift: no component-map change, no privacy-posture change. The change adds visibility (what always-on did and
why, and Windows' own mute state for the mic it listens to); it records nothing new about the owner's content.

I believe this is finished.
