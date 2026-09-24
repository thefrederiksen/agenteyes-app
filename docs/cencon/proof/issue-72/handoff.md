# Issue #72 - Developer handoff

[Always On] Room noise counts as sound, so a silent afternoon is kept as one 5-hour clip.
Branch: `issue-72-alwayson-sustained-rms`.

## What changed

| File | Change |
|------|--------|
| `src/AgentEyes.Core/Audio/AudioLevel.cs` (new) | Per-buffer level: peak, sum of squares, and sample count, so buffers add up into a per-second RMS. |
| `src/AgentEyes.Core/Audio/AudioCapture.cs`, `LoopbackCapture.cs` | New `BufferLevel` event next to the existing `LevelChanged` peak event (meters unchanged). |
| `src/AgentEyes.Core/AlwaysOn/ContinuousRecorder.cs` | Always-on feeds `BufferLevel` (not the peak) into the sound log. |
| `src/AgentEyes.Core/AlwaysOn/SoundLog.cs` | The rule. Per-second **RMS**; a second is LOUD when its RMS is over the line; loud seconds are SOUND only when 3 of them fall in some 10-second span. Auto line = 20th percentile of per-second RMS over 10 min + 15 dB, never below -50 dBFS. Fixed lines use the same RMS + sustained rule. New: `CurrentFloorDb`, `Summarize`, `Describe`. A second is judged once it is over (when the next second's first buffer arrives). |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnEngine.cs` | `AlwaysOnStatus.FloorDb` (new) next to `ThresholdDb`; `LogLevels` writes one `[AlwaysOnEngine] levels:` line per minute. |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnCli.cs` | The CLI status line also prints the floor. |
| `src/AgentEyes.App/MainWindow.xaml` | The Auto hint text said "10 dB above the measured noise floor"; now "Auto: 15 dB above the noise floor, at least -50 dBFS". Text only, no layout change. |
| `tests/AgentEyes.Tests/AlwaysOnSoundRuleTests.cs` (new), `fixtures/issue72-quiet-room-levels.csv` (new) | Replay tests, and the measured fixture. |
| `tests/AgentEyes.Tests/AlwaysOnEngineTests.cs` | The two engine tests that faked sound with ONE loud buffer now speak 4 seconds (one buffer is not sound any more). New: status floor/line test, minute-log test. |
| `tests/AgentEyes.Tests/AlwaysOnSoundAndArgsTests.cs` | The old peak-rule sound-log tests are removed (they asserted the old rule); their replacements are in `AlwaysOnSoundRuleTests`. |

`KeeperRule` is unchanged: it still asks `AnySound(from, to)`, which now answers from sustained seconds only.
`GateCalibration` (normal recordings' noise gate) is untouched, as the issue scopes.

Choices made where the issue was silent (flag them if they are wrong):
- With `counts=both`, a second is loud if it is loud on EITHER source, and the 3-in-10 rule is applied to those seconds together.
- A second of digital silence reads as -120 dBFS (`SoundLog.SilenceDb`) so the floor is always a finite number in status and log.
- The floor is still measured and reported when a fixed line is in force (so the owner can see the gap).

## The fixture

`tests/AgentEyes.Tests/fixtures/issue72-quiet-room-levels.csv` - per-second RMS and peak of
`C:\AgentEyes\2026-09-23_16-00-16.mp4`, minutes 282-325 (offset 16920 s, 2580 rows), extracted READ-ONLY
with the ffmpeg command in the file header. Stats on it: RMS 20th percentile -76.7 dBFS, median -75.9.

## Acceptance criteria -> how QA verifies

1. **Replay test from the measured file: Auto reports no sound, the clip closes after the "after" window.**
   `AlwaysOnSoundRuleTests.Replay_QuietRoomFixture_Auto_NoSoundAndTheClipClosesAfterTheAfterWindow`.
   Replays all 2580 seconds into the real `SoundLog` (Auto), asserts `AnySound` false over the stretch,
   `LastSoundUtc` null, floor in [-80, -70], line -50; then runs the real `KeeperRule` over 43 one-minute
   pieces with 5/5 min windows and a clip open from talking that ended 1 s before the stretch: exactly the
   pieces at minutes 0..4 are kept (the "after" tail), clip 1 closes, the other 38 pieces are deleted.
   Fail-closed: the fixture loader asserts exactly 2580 contiguous rows; the test asserts the room DID
   produce loud seconds (2 on this fixture, offsets 33 and 39) that the sustained rule refused, so an
   instrument that hears nothing fails.
   Known-bad arm: `Replay_QuietRoomFixture_OldPeakRule_CountedTheRoomNoiseAsSound` replays the OLD rule
   (per-second peak, 20th-pct peak floor clamped at -70, + 10 dB) over the same fixture and asserts > 500
   loud seconds (956 measured; the issue measured 1108 with a slightly different extraction).
2. **Synthetic speech (20 s at -35 with 1-2 s gaps inside -65 noise) is sound.**
   `Replay_SpeechLikeSeriesInsideNoise_IsSound` - sound in 60..79, none before or after, 16 loud = 16 sound seconds.
3. **Isolated spikes (-20 once every 60 s inside -65) are NOT sound.**
   `Replay_IsolatedSingleSecondSpikesInsideNoise_AreNotSound` - asserts `Summarize` = (30 loud, 0 sound):
   all 30 spikes crossed the line, none counted.
   Also: `Sustained_TwoLoudSecondsInTen_IsNotSound_ThreeIs` (2 in 10 no; 0/10/19 no; 0/5/9 yes),
   `Observe_FixedLine_UsesRmsNotPeak` (a -5 dBFS peak with -55 RMS is not loud),
   `Observe_ManyBuffersInOneSecond_AreSummedIntoOneRms`, `AutoThreshold_IsFloorPlusFifteen_NeverBelowMinusFifty`,
   `Observe_Both_LoudSecondsFromEitherSourceSustainTogether`.
4. **`GET /always-on` reports the auto line and the floor.** `status.ThresholdDb` (line, dBFS RMS) and the new
   `status.FloorDb` (floor it was derived from). Unit: `AlwaysOnEngineTests.Status_Auto_ReportsTheLineAndTheFloorItCameFrom`
   (null/null before anything is heard; floor -65 -> line -50; floor -40 -> line -25). The REST handler
   serialises `AlwaysOnStatus` as-is (`RestServer.AlwaysOnStatus`), so the field appears with no App code change.
   QA live: `curl http://127.0.0.1:7882/always-on` on a build of this branch with always-on started -> `status.FloorDb`
   and `status.ThresholdDb` are numbers.
5. **Once-per-minute log of floor, line, loud seconds, sustained.** `AlwaysOnEngineTests.Tick_LogsTheLevelsOncePerMinute`
   asserts no line before 60 s, the exact line at 60 s
   (`mic floor=-70.0dBFS line=-50.0dBFS (auto); last 60s: loud=4 sustained=yes (4s)`), no new line 45 s later,
   and a fresh `loud=0 sustained=no` line at 120 s; it also finds the line in the real log file. A tick
   10 ms short of the minute still logs (1 s tolerance) - the live run found a strict compare skipping to a
   75 s "minute" on timer jitter.
   Live: `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-YYYYMMDD.log`, grep `[AlwaysOnEngine] levels:`.
6. **Live on this laptop (quiet room -> no clip; 1 min talk + 6 min quiet -> one ~11 min clip).**
   NOT fully exercised by the developer - see "What could not be run live" below. What WAS run live is in
   `live-cli-run.txt`.
7. **Build clean, tests green.** See "Gate" below.

## Gate

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.` `0 Error(s)`.
- `dotnet test AgentEyes.sln -c Release` -> `Failed: 4, Passed: 1695, Total: 1699`.
  The 4 failures are PRE-EXISTING on `main` and unrelated to this change - the same four fail on a clean
  `main` checkout (stash of this branch): `Failed: 4, Passed: 1684, Total: 1688`:
  - `HudResponsivenessTests.NothingTheHudsUiThreadCanReach_WritesAFile` (BackgroundFileWriter/Config.WriteJson -> File.WriteAllText)
  - `PreviewTapTests.NothingOnARecordingsCriticalPaths_TouchesTheFilesystemOrTheSharedLogger`
  - `PreviewTapTests.NothingTheDrainCanReach_TouchesTheFilesystem`
  - `PreviewTapTests.NothingTurningThePreviewOffCanReach_TouchesTheFilesystem` (Log.Write reachable from the preview)
  None of them names a type this branch touches. They need their own issue; this one does not fix them.
  `CameraPreviewTests` is additionally timing-flaky on this machine: across four full runs, 0-3 different
  camera tests failed per run (different ones each time, 5-11 s timeouts); the camera code is untouched here.
- All 97 always-on tests (`--filter FullyQualifiedName~AlwaysOn`) pass, 11 of them new.

Mutation evidence (each run against the source, then restored):
- `SustainMinLoudSeconds = 1` (i.e. one loud second counts, the old behaviour): 5 tests FAIL, including
  `Replay_QuietRoomFixture_Auto_...` and `Replay_IsolatedSingleSecondSpikesInsideNoise_AreNotSound`.
- `AutoMarginDb = 10`, `AutoMinLineDb = -200` (the old margin, no minimum): 5 tests FAIL, including
  `Replay_QuietRoomFixture_Auto_...` and `AutoThreshold_IsFloorPlusFifteen_NeverBelowMinusFifty`.

## What could not be run live, and why

The owner's installed v1.11.0 must not be stopped, restarted, reconfigured or redeployed, and the live
criterion needs a build of THIS branch serving `GET /always-on` on port 7882 (taken by the installed app)
plus a person talking for a minute. So the developer did not run criterion 6 as written.

What was run instead: the freshly built CLI (`agenteyes always-on`, the same engine, no port, no mutex) in
isolation - clips and work folders in a temp directory, `--counts mic --threshold auto --before 1 --after 1`,
no `--mic` so ffmpeg never opened the microphone (only the shared-mode level meter did). Output and the
`levels:` log lines are in `live-cli-run.txt`. It shows the real line and floor on this laptop's default
microphone and what the keeper did with the room as it was during the run. It is NOT proof of the speech
half (nobody spoke on cue) and the room was not guaranteed quiet.

RISK QA MUST CHECK LIVE: on this run the default microphone's level meter measured a floor of -96.7 dBFS
RMS, about 20 dB below the -76 dBFS floor measured from the recorded clip (the meter reads the raw device
without the recording's mic gain, and the default microphone may not be the Realtek one the owner's setup
names). The Auto line therefore sits at its -50 dBFS minimum. Whether normal talking at the built-in mic
reaches -50 dBFS RMS ON THE METER is not proven by any test here - that is exactly the speech half of
criterion 6, and it must be observed live (watch `loud=` in the `levels:` log lines while talking).

For QA (criterion 6): build this branch, and with the owner's go-ahead either (a) run the CLI the same way
for 10+ minutes in a quiet room and then with 1 minute of talk + 6 minutes quiet (`--before 5 --after 5`),
ffprobe the clip(s) in the `--clips` folder; or (b) deploy and use `GET /always-on` as the issue states.
Deploying is a separate, explicitly-requested step.

Smoke scope: always-on only. No HUD, preview, or normal-recording path changed (`LevelChanged` is untouched;
`BufferLevel` is an additional event).

Reminders: drive the app via REST / UIA / PrintWindow only, never force-foreground + synthesize input without
warning the human; the recording HUD is capture-excluded - assert state via UIA or `/status`, not a screen grab.

## CenCon impact

No drift in the component map. Privacy posture unchanged (visible / controllable): the change makes always-on
keep LESS, and the new status field and log line make its decision more visible.

I believe this is finished.
