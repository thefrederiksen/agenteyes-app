# Issue #79 - Developer handoff

[Always On] Start each clip 10 s before the speech and close it after 5 min of silence, as settings.
Branch: `issue-79-clip-lead-in`.

## What the owner gets

- A clip now starts **keep-before** (default 10 s) ahead of the first speech and ends **keep-after**
  (default 10 s) past the last, instead of on whole one-minute pieces with 5 minutes either side.
- A clip is closed after the **silence gap** (default 5 min) with no sustained sound; a shorter pause
  stays inside one clip.
- The three values are settings on the Always On page, in config.json, and in `GET /always-on`.
- The capture forces a keyframe every 2 s (was every 60 s), so the first piece of a clip can be cut
  losslessly at the keyframe at or before the lead-in. Only a clip's first and last piece are ever
  cut, with a stream copy; everything between is joined whole. No re-encode anywhere.
- A silent piece is deleted as soon as no speech can still need it (keep-before after its end has
  passed), not after 5 minutes.
- A v1.11.x config.json is brought forward once: its "keep after" (which was the silence that closed a
  clip) becomes the silence gap; keep-before and keep-after take the new 10 s defaults.

The issue's two Assumptions (keep-before 10 s, keep-after 10 s) were resolved by the owner: those are
the defaults implemented.

## What changed

### AgentEyes.Core

| File | Change |
|------|--------|
| `AlwaysOn/AlwaysOnKeepSettings.cs` (new) | The three settings in one place: defaults (10 s / 10 s / 5 min), ranges (before 0-120 s; gap 30 s - 30 min; after 0 s - gap), `Problem()` (the reason in words, or null), `Validate()` (throws `UsageException`), `Describe()` ("10 s", "1 min 30 s", "5 min"). |
| `AlwaysOn/KeeperRule.cs` | Rewritten around SOUND rather than pieces. `Decide(pending, ISoundTimes, now, KeepWindows, OpenClip?, closedSoundUtc, nextClip, final, restartBridge)`. An open clip carries its first and last second of sound; a clip closes once `SilenceGap + SoundSettle` (12 s: the sustained-sound rule can still promote a second that far back) pass with no sound, or on the final pass. `KeeperPlan.Close` is a list of `ClipSpan` (first/last sound, kept start = first - before, kept end = last + 1 s + after). A piece past the open clip's tail WAITS while the clip is open (sound may return within the gap). A silent piece with no clip open is deleted once `keep-before + SoundSettle` have passed since its end. A piece in which one clip ends and the next may begin is COPIED into the closed clip and decided again (`Keep` entries carry `Copy`). Restart bridge (#81) unchanged in behaviour; the engine passes the silence gap. Known limit documented in the class summary (needs gap < before + after + one piece; the defaults are far from it). |
| `AlwaysOn/ClipTrim.cs` (new) | The pure trim planner: for a closed clip's pieces and its span, which piece is cut where. First piece: `InSeconds` = the keyframe at or before the span start (floor to the keyframe grid); last piece: `OutSeconds` = the span end. Pieces wholly outside the span are reported in `Outside` and not joined. |
| `AlwaysOn/AlwaysOnArgs.cs` | `Capture(..., keyframeSeconds = 2)`: `-force_key_frames expr:gte(t,n_forced*2)`; a piece must be a whole number of keyframe intervals (throws otherwise). New `Trim(input, output, inSeconds?, outSeconds?)`: `-y [-ss IN] -i input [-t LEN] -map 0 -c copy -avoid_negative_ts make_zero output` - input seeking lands on the keyframe at or before IN. `Join` unchanged (concat demuxer, `-c copy`). |
| `AlwaysOn/AlwaysOnOptions.cs` | `KeepBefore`, `KeepAfter`, `SilenceGap` (TimeSpans, the new defaults), `Windows` (the three as `KeepWindows`), `KeyframeSeconds` (2). |
| `AlwaysOn/AlwaysOnEngine.cs` | `Start` validates the settings and the piece/keyframe relation (refuses with the reason; always-on stays off). `RunKeeper` passes the sound log as `ISoundTimes`, moves or COPIES kept pieces, logs each CLOSE with the sound times and the kept span. `JoinClip(o, dir, now, span)` plans the trim, runs `Trim` for the first/last piece into `cut_<piece>.mp4` next to the piece (a crash before the join leaves the uncut piece to be joined whole at the next start), joins, counts trimmed-away seconds as discarded, names the clip for where the kept video starts. A holding folder left by an earlier run (no sound log) is joined whole, untrimmed. Status carries `KeepBeforeSeconds`, `KeepAfterSeconds`, `SilenceGapSeconds` (was `KeepAfterMinutes`); the in-progress line says "after 5 min of quiet" via `Describe`. `OpenClipStart` reports the lead-in start. The capture-down check and `IsKeeping` use the silence gap. |
| `AlwaysOn/SoundLog.cs` | Implements `ISoundTimes`: `FirstSound(from, to)` and `LastSound(from, to)` (inclusive whole seconds) beside `AnySound`. |
| `AlwaysOn/ContinuousRecorder.cs` | Passes `o.KeyframeSeconds` to `AlwaysOnArgs.Capture`. |
| `AlwaysOn/AlwaysOnCli.cs` | `--before SEC --after SEC --gap SEC` (were minutes); the same defaults. |

### AgentEyes.App

| File | Change |
|------|--------|
| `Config.cs` | `AlwaysOnKeepBeforeSeconds`, `AlwaysOnKeepAfterSeconds`, `AlwaysOnSilenceGapSeconds` (doubles, defaults from `AlwaysOnKeepSettings`). v1.11.x's `AlwaysOnBeforeMinutes` / `AlwaysOnAfterMinutes` are now nullable, read only to migrate and never written back (`JsonIgnore` when null). `Config.FromJson(text)` (used by `Load`) runs `MigrateAlwaysOnKeepSettings()`: old "keep after" -> silence gap (a hand-edited value outside 30 s - 30 min is brought to the nearest end - the page only ever offered 1-30 min), before/after -> 10 s, old fields cleared, one log line. A no-op for a config with no old fields. |
| `AlwaysOnController.cs` | `KeepSettings(cfg)` reads the three and REFUSES an out-of-range config.json with the reason plus "Change it on the Always On page." (no silent fallback). `BuildOptions` maps them to the engine options. `Settings` exposes the config to the REST server. |
| `MainWindow.xaml` | The Always On page gains a third drop-down, "Close the clip after silence of"; "Keep before the speech" and "Keep after the speech" are relabelled with their ranges in the hints. Rows below shift by one. Automation names: `Keep before the speech`, `Keep after the speech`, `Close the clip after silence of`. |
| `MainWindow.AlwaysOn.cs` | Choices per drop-down inside the ranges (before 0..120 s; after 0..300 s; gap 30 s..30 min), plus the saved value when it is not one of them. `AOSetting_Changed` checks the three together with `AlwaysOnKeepSettings.Problem`; a refused combination is said on the page and the drop-downs go back to what is saved; otherwise saved at once. `RuleInWords(...)` states the rule in one sentence. |
| `RestServer.cs` | `GET /always-on` adds `settings: { keepBeforeSeconds, keepAfterSeconds, silenceGapSeconds }` (the saved values, what the next start uses); `openClip.afterMinutes` is now `openClip.silenceGapSeconds`. The read is logged with the three values. |

### AgentEyes.Tests

| File | Change |
|------|--------|
| `AlwaysOnKeeperTests.cs` | Rewritten for the new rule: the issue's four numeric cases, waiting/deleting/closing, the copied piece, final pass, and the trim planner (24 tests). |
| `AlwaysOnKeepSettingsTests.cs` (new) | Defaults, every range end, the reason text per setting, `Validate`, `Describe` (7 tests, 27 cases). |
| `AlwaysOnTestSounds.cs` (new) | `TestSounds`, an `ISoundTimes` stand-in by second offsets; records the latest time asked about. |
| `AlwaysOnSoundAndArgsTests.cs` | 2 s keyframes, keyframe option, piece/keyframe relation, `Trim` command shape and never-reencodes, `Join` unchanged. |
| `AlwaysOnAppTests.cs` | Config defaults, persistence in seconds, migration from a v1.11.2 config.json (7 tests), controller refusal of out-of-range settings, `BuildOptions` mapping, `RuleInWords`, a real HTTP `GET /always-on` round trip, tray/status text with the gap in seconds. |
| `AlwaysOnEngineTests.cs` | Options carry the 5 min gap explicitly; expectations updated where the trim now leaves a 2-second test piece ahead of the lead-in out of the clip (commented in each test). New: an end-to-end trim on REAL keyframed files (6 s pieces, keyframe every 2 s): the clip is 8 s, not 12, and is named for the keyframe; `Start` refuses out-of-range settings and a piece that is not a whole number of keyframes. |
| `AlwaysOnStallTests.cs` | Ported to the new `Decide` signature (`OpenClip`, `KeepWindows`, `TestSounds`); the restart-bridge cases now carry the speech that makes the piece part of the clip; the stall replay proves ONE clip across the restart with the new trim (8 pieces). |
| `AlwaysOnSoundRuleTests.cs` | The quiet-room replay drives the new keeper: one piece kept (the 10 s tail), the clip closes after the gap, 42 minutes deleted. |
| `ManifestWriterIlTests.cs` | Pins the one new file writer: `AlwaysOnEngine::RunKeeper -> File::Copy x1` (the piece copied into a closed clip). |

## Gate (run by the developer)

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)` (19 warnings, all pre-existing).
- `dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1835, Skipped: 0, Total: 1835`.

## Acceptance criteria -> how each is covered, and how QA verifies

1. **Keeper + trim planner: speech at 125 s, keep-before 10 s -> clip starts at the keyframe at or
   before 115 s and no earlier than 113 s; last speech at 300 s, keep-after 10 s -> ends 310 s (+-2 s);
   2-minute pause with a 5-minute gap -> one clip; 6-minute pause -> two clips.**
   `AlwaysOnKeeperTests`:
   - `Decide_SpeechAt125s_KeepBefore10s_ClipStartsAtTheKeyframeAtOrBefore115sAndNoEarlierThan113s` -
     span start 115 s; the first piece (opened at 60 s) is cut at 54 s = 114 s, on the 2 s grid, and the
     test asserts 113 <= start <= 115.
   - `Decide_LastSpeechAt300s_KeepAfter10s_ClipEndsAt310s` - the last second of sound starts at 299 s,
     the span ends at exactly 310 s; the last piece's `OutSeconds` is 10; the middle pieces are untrimmed.
   - `Decide_TwoMinutePauseInsideSpeech_FiveMinuteGap_IsOneClip`, `Decide_SixMinutePauseInsideSpeech_FiveMinuteGap_IsTwoClips`.
   - The rest of the file covers waiting, deleting after keep-before, closing after the gap, the copied
     piece, the final pass, and the planner (`Plan_*`).
   End to end with real ffmpeg: `AlwaysOnEngineTests.Engine_ClipIsTrimmedToItsSpan_FirstPieceCutOnAKeyframeAndLastPieceCutAtTheTail`
   (6 s pieces with a keyframe every 2 s; talking 0:16-0:19; the written clip is 8 s - a cut that
   missed the keyframe would give 10, no trim 12 - and is named for 0:14).
   QA: `dotnet test --filter "FullyQualifiedName~AlwaysOnKeeperTests"`; read the four AC tests against
   `KeeperRule.SpanStart/SpanEnd` and `ClipTrim.Plan`.

2. **ffmpeg arguments (2 s keyframes) and the trim/join commands (stream copy, no re-encode).**
   `AlwaysOnSoundAndArgsTests`: `Capture_ForcesAKeyframeEveryTwoSeconds_AndCutsSixtySecondPieces`,
   `Capture_PieceLengthFollowsTheOption`, `Capture_KeyframeIntervalFollowsTheOption`,
   `Capture_PieceNotAWholeNumberOfKeyframes_Throws`, `Trim_StartAndEnd_IsAStreamCopyFromTheKeyframeForTheKeptLength`,
   `Trim_StartOnly_SeeksOnTheInputAndKeepsToTheEnd`, `Trim_EndOnly_KeepsFromTheStartForTheGivenLength`,
   `Trim_NeverReencodes`, `Trim_BadCut_Throws`, `Join_CopiesStreamsWithoutReencoding`, `Join_NeverNamesAnEncoder`.
   QA: the exact command lines are in the tests; in a live run the log shows
   `[ContinuousRecorder] Start: ... -force_key_frames expr:gte(t,n_forced*2) ...` and
   `[AlwaysOnEngine] JoinClip: trimmed piece_... (stream copy) - kept Xs to Ys of Zs`.

3. **Settings on the Always On page with the ranges, validated, persisted, returned by GET /always-on.**
   - Ranges/validation: `AlwaysOnKeepSettingsTests` (every end; the reason per setting),
     `AlwaysOnAppTests.KeepSettings_ConfigOutOfRange_RefusesWithTheReasonAndWhereToFixIt`,
     `BuildOptions_KeepSettingsOutOfRange_RefusesToBuild`,
     `AlwaysOnEngineTests.Start_KeepSettingsOutOfRange_ThrowsWithTheReasonAndLeavesAlwaysOnOff`.
   - Persistence: `Config_KeepSettings_RoundTripInSeconds_AndTheOldMinuteFieldsAreNotWritten`,
     `FromJson_CurrentConfig_IsNotMigrated`, `Config_Defaults_AreTheOwnersDecisions`.
   - Page: `RuleInWords_StatesTheThreeKeepSettingsAsThePageShowsThem`; the XAML/behaviour is reviewed
     in `MainWindow.xaml` (rows 3-5) and `MainWindow.AlwaysOn.cs` (`AOSetting_Changed`, `FillKeepSettings`).
   - API: `GetAlwaysOn_ReturnsTheSavedKeepSettingsInSeconds` (a real `RestServer` on a free loopback
     port; `settings.keepBeforeSeconds/keepAfterSeconds/silenceGapSeconds`), `OpenClip_ForTheApi_...`,
     `InProgressLine_StatesInProgressElapsedAndDestination` (the gap said as "2 min 30 s").
   QA, live (focus-free): `GET http://127.0.0.1:7882/always-on` -> `settings` with the three values.
   UIA on the Always On page (`gui-smoke.ps1` patterns): the combos named `Keep before the speech`,
   `Keep after the speech`, `Close the clip after silence of`; pick a value, re-GET `/always-on`, and
   read config.json under `%LOCALAPPDATA%\AgentEyes\` for `AlwaysOnKeepBeforeSeconds` etc. Choosing a
   keep-after longer than the gap is refused on the page (red line) and the drop-down goes back.
   PrintWindow the page for the proof-target screenshot.

4. **Migration test from a v1.11.x config.json.** `AlwaysOnAppTests`:
   `FromJson_V111Config_MigratesKeepAfterToTheSilenceGapAndTakesTheNewSecondDefaults` (a v1.11.2
   config.json text: before=5/after=5 -> gap 300 s, before 10 s, after 10 s; old fields gone from the
   rewritten JSON; everything else untouched; the log line is present),
   `FromJson_V111ConfigWithAChosenKeepAfter_TheGapIsThatManyMinutes` (1, 2.5, 10, 30 min),
   `FromJson_V111ConfigWithAKeepAfterOutsideTheGapsRange_IsBroughtToTheNearestEnd` (0.25 -> 30 s, 45 -> 1800 s),
   `FromJson_V111ConfigWithOnlyKeepBefore_TheGapStaysTheDefault`,
   `MigrateAlwaysOnKeepSettings_RunsOnce_ASecondCallLeavesLaterChoicesAlone`.
   QA, live if wanted: with the app stopped, put `"AlwaysOnBeforeMinutes": 5, "AlwaysOnAfterMinutes": 10`
   into config.json, start the app, `GET /always-on` shows `silenceGapSeconds: 600`; the log has
   `[Config] MigrateAlwaysOnKeepSettings: ...`; the next save drops the old fields.

5. **Measure on a real 10-minute recording the file size with 60 s vs 2 s keyframes (h264_qsv, 10 fps)
   and state it in the PR.** PENDING TESTER (needs the owner's laptop; the developer session may not
   run ffmpeg against real recordings). How: take the capture command from the log line
   `[ContinuousRecorder] Start: <command>`, run it for 600 s once as logged (2 s keyframes) and once with
   `expr:gte(t,n_forced*60)` into two folders, and compare the total size of the pieces. State both
   numbers and the ratio in the PR.

6. **Live on the owner's laptop: speak ~1 minute, then 6 minutes quiet -> exactly one clip; ffprobe shows
   it starts 10-12 s before the first speech and ends ~10 s after the last.** PENDING TESTER (only when
   the owner says the laptop is free). How: start always-on (`POST /always-on/start` or the page), note
   the clock when speech starts and ends, wait 6 minutes; `GET /always-on` lists the clip under
   `clipsKeptToday`; `ffprobe -show_entries format=duration` on it. The log gives the exact numbers:
   `[AlwaysOnEngine] keeper: CLOSE clip_... - sound HH:mm:ss to HH:mm:ss, kept HH:mm:ss to HH:mm:ss
   (10 s before, 10 s after)` and the `trimmed piece_... kept Xs to Ys` lines. The clip's file name is
   the local time the kept video starts (the keyframe at or before first speech - 10 s).

7. **`dotnet build` clean and `dotnet test` green.** See Gate above: 0 errors; 1835 passed, 0 failed.

## Notes for the reviewer

- SoundSettle (12 s) is deliberate: the issue #72 sustained rule promotes a loud second to SOUND only
  once three loud seconds gather within 10 s, so the log's answer about a second can change for up to
  ~12 s. No clip is closed and no piece deleted on an answer that can still change. In practice a clip
  closes at last sound + 5 min + 12 s.
- The test pieces are 2-second files named a minute apart. Where an existing engine test's expected
  clip changed (`Engine_SoundInTheMiddle_...`, the #81 stall replay), it is because the trim planner
  now sees a 2 s piece that lies wholly ahead of the lead-in and leaves it out - a real 60 s piece would
  be cut at the lead-in instead. Each such test says so in a comment. The new end-to-end trim test uses
  real 6 s keyframed pieces so the cut itself is exercised.
- API change: `openClip.afterMinutes` -> `openClip.silenceGapSeconds`; `status.KeepAfterMinutes` ->
  `status.KeepBeforeSeconds/KeepAfterSeconds/SilenceGapSeconds`. A consumer reading the old names
  (none in this repo) must move.
- A config.json edited to an out-of-range value is refused at start with the reason and where to fix it;
  the one clamp is the v1.11 migration's gap (the old page offered 1-30 min, all inside the range; a
  hand-edited outlier is brought to the nearest end and logged) - the alternative was an app that
  cannot start on a file it wrote itself.
- Heavy smokes: none run by the developer (the change's runtime surface is the two pending-tester
  criteria). QA may scope `api-smoke.ps1` to `/always-on` if it wants a live API check.

## CenCon impact

No drift: no component-map change (the same Core engine, App page and Control API), no change to the
privacy posture - the recording indicator and the pause/exclusion controls are untouched, and less
silence is kept than before (10 s each side instead of up to 5 min).

## Reminders for QA

- Focus-free layers only: REST (`127.0.0.1:7882`), UIA, PrintWindow. Never force-foreground the app
  and synthesize input without warning the human.
- The recording HUD is capture-excluded; HUD state is asserted via UIA or `/status`, not a screen grab.
- `dotnet test AgentEyes.sln -c Release` is ~45 s and silent (the always-on tests encode a few tiny
  MP4s with ffmpeg; no screen or microphone capture).

I believe this is finished, except for the two criteria marked PENDING TESTER, which need the owner's
laptop and are handed to the tester session.

## Review fix pass (2026-09-24)

After the tester's live finding on #79 (h264_qsv at 10 fps put forced keyframes 43-77 s apart when
asked for 60 s) and an independent code review of PR #87. Each finding -> what changed.

### Finding 1 (BLOCKING) - the encoder did not honour `-force_key_frames` on time

- **1a, `AlwaysOnArgs.Capture`**: the keyframe is now asked for twice. Besides the unchanged
  `-force_key_frames expr:gte(t,n_forced*2)`, a new `AlwaysOnArgs.GopArgs(encoder, fps, keyframeSeconds)`
  sets the encoder's own GOP, appended right after `EncoderArgs` (so after `-c:v`, before the segment
  muxer): `-g fps*keyframeSeconds` (20 at 10 fps / 2 s) for every encoder; `-forced_idr 1` for
  `h264_qsv` (QSV writes a forced keyframe as a real IDR frame only with it); `-forced-idr 1` for
  `h264_nvenc` (same flag, hyphen spelling); nothing more for `h264_amf` (no such flag exists - `-g`
  is all it takes); `-keyint_min 20 -sc_threshold 0` with `-g 20` for `libx264` (no shortened GOP, no
  scene-cut keyframe shifting the grid). The reason is in the code comment. `EncoderProbe` is
  unchanged (ten frames say nothing about keyframe placement) and a test pins that.
- **1b, tests** (`AlwaysOnSoundAndArgsTests`): `Capture_EveryEncoder_SetsItsOwnGopToTheKeyframeIntervalBesidesTheForcedKeyframes`
  (theory over the four encoders: `-g 20` present once, the encoder's IDR/keyint options present with
  their values, `-force_key_frames` still present, `-g` after `-c:v` and before the muxer's `-f`),
  `Capture_GopFollowsTheFrameRateAndTheKeyframeOption` (30 fps x 5 s -> `-g 150 -keyint_min 150`),
  `Capture_OnlyTheEncodersThatHaveTheFlag_GetAnIdrOption`, `GopArgs_UnknownEncoderOrBadNumbers_Throw`,
  `EncoderProbe_CarriesNoGopOptions`.
- **1c, `ClipTrim` summary**: no longer says it relies on keyframes at 0, k, 2k inside every piece. It
  now states that the trim is an input-side seek with stream copy which lands on the keyframe ACTUALLY
  at or before the asked time, so a late keyframe makes the lead-in LONGER, never shorter; the encoder
  GOP + forced keyframes keep that error within one keyframe interval when the encoder honours them.
  The input-seek trim itself is unchanged, as the tester asked.
- **1d, PENDING TESTER - measure the keyframes on the real encoder (h264_qsv, 10 fps).** After merge,
  on a live piece from `%LOCALAPPDATA%\AgentEyes\alwayson\pieces\` (or a clip's holding folder):

  ```
  ffprobe -skip_frame nokey -select_streams v -show_entries frame=pts_time -of csv=p=0 <piece>
  ```

  Expected: one `pts_time` roughly every 2.0 s (0.0, 2.0, 4.0, ... ; at 10 fps a keyframe may sit
  one frame, 0.1 s, off). Bad: gaps of tens of seconds as the 60 s capture showed. Empty output is a
  broken instrument (wrong path or no video stream), never a pass. Also expect every piece to be 60 s
  again (`-show_entries format=duration`), where the live 60 s capture measured 43-77 s. The log's
  `[ContinuousRecorder] Start:` line must show `-c:v h264_qsv ... -g 20 -forced_idr 1 -force_key_frames expr:gte(t,n_forced*2)`.

### Finding 2 - stale "pending the owner" comment

`AlwaysOnKeepSettings` summary now says the two assumptions were resolved by the owner (10 s / 10 s /
5 min are the defaults) and points at the new `LeadInNote`.

### Finding 3 - a quiet stretch of exactly the gap started a new clip (off by one second)

`KeeperRule.Extend` asked about `[last+1, last+gap]`; a second of sound starting at `last` ends at
`last+1`, so the quiet after it runs from `last+1`, and a stretch of exactly the gap ends at
`last+1+gap`. The window is now `[last+1, last+1+gap]`: exactly the gap stays inside one clip, one
second more starts a new one. The close time moved with it: a clip closes at
`last + 1 s + gap + SoundSettle` (was `last + gap + SoundSettle`), so the log's answer about the last
second the gap can hold is final before the clip closes. The class summary states the boundary.
Tests (`AlwaysOnKeeperTests`): `Extend_QuietStretchOfExactlyTheGap_StaysInsideTheClip` (sound 30-35 s,
then at 336 s: one clip to 340 s), `Extend_QuietStretchOneSecondLongerThanTheGap_StartsANewClip`
(next sound at 337 s: the clip ends at 35 s), `Decide_PauseOfExactlyTheGap_IsOneClip_OneSecondMoreIsTwo`
(the whole rule over 12 pieces), and `Decide_GapNotYetPassed_TheClipStaysOpen` now asserts both arms
(open at 347 s, closed at 348 s). The issue's four AC cases are unchanged and still pass.

### Finding 4 - a clip whose every piece is judged Outside deleted the holding folder

`AlwaysOnEngine.JoinClip`: when the trim planner finds no piece inside the span, the holding folder
is now MOVED whole to the unreadable folder (`%LOCALAPPDATA%\AgentEyes\alwayson\unreadable\clip_...`,
where pieces a join could not read already go; a numbered suffix if the name exists), `LastError`
says "set aside in ...", and the log line says how many pieces and seconds were set aside, not
deleted. "Outside" is judged from ffprobe duration + file-name stamp, which a stall-truncated piece
can fool; kept video is not deleted on a judgement that can be wrong. Those seconds are no longer
counted as discarded (they were not). Test: `AlwaysOnEngineTests.JoinClip_EveryKeptPieceOutsideTheSpan_TheHoldingFolderIsSetAsideNeverDeleted`
(0 s margins, talking at 4:30, a 2-second test piece at 4:00: no clip written, the pending folder
empty, the piece present under `unreadable\clip_*`, the two log lines present).

### Finding 5 - the keeper's known limit was only in a code comment

The smaller change, a page hint, not a `Problem()` refusal: `AlwaysOnKeepSettings.LeadInNote(before,
after, gap, pieceSeconds)` returns one sentence when `gap < before + after + one piece` and null
otherwise. `MainWindow.RuleInWords` appends it to the rule sentence on the Always On page only when it
applies (the defaults carry no note), using the product's `AlwaysOnOptions.DefaultPieceSeconds` (60);
`AlwaysOnEngine.Start` logs it once as a warning when the running options reach it. Tests:
`AlwaysOnKeepSettingsTests.LeadInNote_*` (null at and above the limit, stated under it, the boundary
79 s / 80 s, never a `Problem`, the exact sentence for the reviewer's 30 s gap / 60 s before) and
`AlwaysOnAppTests.RuleInWords_GapShorterThanBeforePlusAfterPlusOnePiece_AddsTheKeeperLimitNote`.
QA, live: choose "Keep before the speech" = 1 min and "Close the clip after silence of" = 30 s on the
page; the rule text ends with "Note: with a silence gap under 2 min 10 s ...". Back at the defaults the
note is gone.

### Not changed (accepted by the reviewer, kept as documented)

The v1.11 migration clamp; the 300 s keep-after choice offered when the gap is 30 s (refused with the
reason on the page); the synchronous `Save()` on the UI thread (the page's pre-existing pattern).

### Gate for this pass (run by the developer)

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)` (20 warnings, none in a
  file this pass touched).
- `dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1857, Skipped: 0, Total: 1857`
  (1835 before the pass + 22 new).

No app was launched and no smoke was run by the developer in this pass; the live keyframe measurement
(1d) and the two earlier PENDING TESTER criteria are the tester's, after merge.
