# Issue #77 - Developer handoff

[Always On] History tab: show every decision, level and problem so a missing clip can be explained.
Branch: `issue-77-history-tab`.

## What changed

| Project / file | Change |
|----------------|--------|
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnHistory.cs` (new) | The event model (`AlwaysOnEvent`: `AtUtc`, `Kind`, `Severity`, `Text`, `Detail`), the filter (`HistoryFilter` All / Decisions / Levels / Problems, `AlwaysOnEvent.Matches`), and the store: one JSON line per event in `%LOCALAPPDATA%\AgentEyes\alwayson\history.jsonl`, loaded lazily (never on the UI thread), trimmed to today + 7 local days at load and at each day change (file rewritten via `.tmp` + move), `Appended` event for the live tab, `Events(since, filter)` newest first, `ParseFilter` for the API. A write that fails is logged and the event stays in memory - the history is a record OF the recording, never a condition FOR it. |
| `src/AgentEyes.Core/AlwaysOn/SilentMicRule.cs` (new) | The silent-microphone rule, pure (see below). |
| `src/AgentEyes.Core/Audio/MicEndpoint.cs` (new) | Reads a capture endpoint's Windows mute state and master volume through NAudio's `MMDevice.AudioEndpointVolume` (IAudioEndpointVolume): the default microphone, or the one whose friendly name contains the setup's mic name. Throws a `UsageException` when there is none - never a guess. |
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
| `tests/AgentEyes.Tests/AlwaysOnHistoryTests.cs` (new) | 42 tests, listed per criterion below. |
| `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` | The three new file-write call sites (history append, trim rewrite + move) added to the pinned IL inventory with the file each writes. |

### Events recorded (issue list 1-6)

1. State: `Always-on started (<why>)` (+ ` - paused: <reason>` when it starts paused), `Always-on stopped (<why>)`, `Always-on paused: <reason>`, `Always-on resumed`; a start that fails: `Always-on could not start (<why>): <error>` (Error).
2. Level, once a minute: `Levels: mic floor=-96.7dBFS line=-50.0dBFS (auto); last 60s: loud=4 sustained=yes (4s); mic peak=-20.0dBFS avg=-31.8dBFS` (per counting source). Severity WARNING with ` WARNING: The microphone is sending silence - is it muted?` appended ONLY while the silent-mic rule below holds - never on the floor alone.
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

Banner text (the issue's sentence, then why): `The microphone is sending silence - check it is not muted. Windows reports it muted.` / `... No sound above the line for 10 min.` It is `AlwaysOnStatus.SilentMic` (null when not silent, while paused, while off, or when only the system sound counts) and shows on the Always On page above both tabs. The transition into and out of silence is one Problem event each (Warning / `The microphone is sending sound again.` Info). Cleared within one 15 s tick of a loud second; a Windows unmute is seen within a minute.

## Acceptance criteria -> how covered

| # | Criterion | Covered by | Tester |
|---|-----------|-----------|--------|
| 1 | History tab lists the events, newest first, local time, plain line, severity; warnings/errors stand out | Store order: `Append_ThenEvents_ReturnsNewestFirst_AndWritesOneJsonLinePerEvent`. Row helpers: `HistoryWhen_TodayShowsTheTime_OlderDaysShowTheDateToo`, `HistoryBadge_WarningsAndErrorsSayThePriority_InfoSaysTheKind`, `HistoryStatusText_SaysTheCountOrWhyThereIsNothing`. Every event kind: `Start_RecordsTheDeviceFactsAndTheStartWithItsReason`, `Tick_EveryMinute_RecordsALevelLineWithPeakAndAverage`, `Engine_KeepDeleteClipAndStop_AreRecorded`, `Tick_CaptureFails_RecordsTheErrorWithFfmpegsTail_TheRestart_AndTheRecovery`, `Pause_AndResume_AreRecorded_AndThePauseClearsTheBanner`, `Start_CaptureFails_RecordsTheErrorAndRethrows`. Colours: warning rows `DkAmber`, error rows `DkRed`, badge WARNING/ERROR. | **PENDING TESTER**: PrintWindow screenshot of the History tab (procedure A). |
| 2 | Filters All / Decisions / Levels / Problems | `Matches_EachFilter_SelectsByKindOrSeverity` (8 cases), `ParseFilter_AcceptsTheFourNamesAndRefusesOthers`, `Events_SinceAndFilter_ReturnOnlyWhatMatches`. Radios `History filter All/Decisions/Levels/Problems` -> `AOHistoryFilter_Changed` -> reload. | Tester clicks each radio (procedure A). |
| 3 | Opens immediately, loads off the UI thread, live updates | Code: `LoadAlwaysOnHistory` sets "Loading..." then `Task.Run(() => History.Events(...))` and builds rows off-thread; `OnAlwaysOnHistoryAppended` -> `Dispatcher.BeginInvoke` -> `ObservableCollection.Insert(0, ...)`. Store lazy-loads on first `Events`, never in the constructor (`AlwaysOnHistory` remarks). `Append_RaisesAppendedWithTheEvent`. | **PENDING TESTER**: live check (procedure B). |
| 4 | Silent/muted mic -> warning event AND banner; clears when the level returns | Rule: `Evaluate_WindowsMuted_IsSilentAtOnce_WhateverTheLevelsSay`, `Evaluate_LoudSecondsPresent_IsNotSilent`, `Evaluate_NoLoudSecondForTenMinutes_IsSilent`, `Evaluate_NoLoudSecondEver_CountsFromWhenListeningBegan`, `Evaluate_MuteStateUnknown_JudgesOnSoundAlone`, `Describe_BothReasons_StartWithTheIssuesSentence`. Engine: `Tick_NoLoudSecondForTenMinutes_RaisesTheBannerAndClearsWhenSoundReturns` (banner + warning + flagged level line at 10:00, cleared one tick after speech), `Tick_GatedFloorOfMinus96WithLoudSecondsPresent_DoesNotRaiseTheBanner` (the tester's test), `Start_OnlySystemSoundCounts_NeverAsksWindowsAboutTheMicrophone_AndNeverRaisesTheBanner`. API: `GetAlwaysOn_ReportsTheSilentMicBannerAndTheMicState`. | **PENDING TESTER**: real muted-mic banner screenshot and the gated-floor no-banner check (procedure C). |
| 5 | Mute state and volume recorded at start; if muted, warning at once | `Start_RecordsTheDeviceFactsAndTheStartWithItsReason` (name, not muted, 80%), `Start_MicMutedInWindows_RaisesTheWarningAndTheBannerImmediately_AndClearsWhenUnmuted`, `Start_MicEndpointCannotBeRead_RecordsAWarningOnce_AndJudgesOnSoundAlone`. | Procedure C, step 1. |
| 6 | `GET /always-on/history?since&kind` returns the same events as JSON | Real HTTP: `GetAlwaysOnHistory_ReturnsTheEventsAsJsonNewestFirst_FilteredByKindAndSince`, `GetAlwaysOnHistory_BadKindOrSince_Is400WithTheReason` (also asserts the discovery entry). | Procedure D. |
| 7 | History survives a restart, trimmed to 7 days | `Events_ANewInstanceOnTheSameFile_ReadsWhatWasAppended`, `Load_EventsOlderThanSevenDays_AreDroppedAndTheFileRewritten`, `Append_OnANewDay_TrimsWhatFellOutOfTheRetention`, `KeepFromUtc_IsMidnightLocalOfTheDaySevenDaysBeforeToday`, `Load_ATornLine_IsSkippedAndTheRestKept`, `Append_FileCannotBeWritten_KeepsTheEventInMemoryAndLogsIt`, `Append_EmptyText_IsRefused`. | Optional: restart the app, the tab still lists yesterday's events. |
| 8 | Build clean, tests green, unit tests for recording, the rule, trimming, the API filter | Below. | - |

## Test run

`dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)`.
`dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1899, Skipped: 0, Total: 1899` (three full runs; 42 of the 1899 are new).

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
rows; Levels only `Levels:` rows; Problems only WARNING/ERROR rows; warnings amber, errors red.

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
   `The microphone is sending sound again.`.
3. THE TESTER'S FINDING: with the mic unmuted and the Default setup's noise gate ON, talk normally for 12+ minutes
   with pauses. Expected: NO banner at any time; the `level` rows read `mic floor=-96.7dBFS` (or whatever the gate
   outputs) with `loud=` > 0 in the minutes you talked, all `level` severity Info; `GET /always-on/history?kind=problems`
   returns no silent-mic warning.
4. Optional: unmuted, say nothing at all for 10 minutes (a quiet room, or the mic covered). Expected at 10:00: the
   banner with `No sound above the line for 10 min.`, a WARNING row, and the 10th level row flagged
   `WARNING: The microphone is sending silence - is it muted?`; speak -> cleared within 15 s.

**D. API (criterion 6).**
`curl "http://127.0.0.1:7882/always-on/history"` -> `{ kind: "all", count, retentionDays: 7, file, events: [...] }`,
newest first, each `{ atUtc, atLocal, kind, severity, text, detail }`.
`curl "http://127.0.0.1:7882/always-on/history?kind=problems"` -> only warnings/errors.
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
