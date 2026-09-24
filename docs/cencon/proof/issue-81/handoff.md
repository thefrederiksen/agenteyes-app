# Issue #81 - Developer handoff

[Always On] Capture stalls for ~2.5 min, and the restart splits the clip and drops the lead-in.
Branch: `issue-81-capture-stall`.

## Root cause (from the code and the owner's log of 2026-09-24, read only)

Both stalls have the same AgentEyes-side shape: **ffmpeg stayed alive while its picture stopped**, so
the segment muxer - which can only cut a new piece on a video keyframe - never opened another piece,
and the supervisor only noticed ~150 s later (`HungAfter` was two pieces + 30 s). The restart then ran a
FINAL keeper pass, which closed the open clip and deleted the restart's leftovers.

**Stall 1 (09:01:45) - proven trigger: the screen grab lost access to the desktop.** The truncated
stderr in the log still holds the decisive lines:

```
[in#0/gdigrab @ ...] Failed to capture image (error 5)
[in#0/gdigrab @ ...] Error during demuxing: I/O error
```

Error 5 is ERROR_ACCESS_DENIED from the screen copy: the input desktop was not readable (a secure
desktop / session switch). The Windows Winlogon/Operational log has exactly one session notification
all morning, at **09:02:03** (event 811/812, notification 5, handled by TermSrv and Sens) - inside the
09:01:45-09:02:45 window where the piece should have been cut. ffmpeg did NOT exit: the microphone and
the system-sound pipe kept it running, so one piece grew with sound and no picture. The test burst
(#78) starts at 09:03:48 in the log, after the failure - it did not cause stall 1. (The tests write
into the same daily log file, which is why "fake ffmpeg: device lost" lines are interleaved there;
they never touch the live capture.)

**Stall 2 (09:23:17) - no error text; cause NOT proven. Working hypothesis: an audio input went
silent without an error and ffmpeg held the picture back to stay in sync.** Evidence: (a) no error line at all in the
tail; (b) the system-sound pipe kept being read - `PipeFeeder` never reported a stall, so the
pipe/amix side was consuming; (c) ffmpeg ignored `q` for 15 s and was killed (a thread blocked in an
input); (d) right after `q`, two new pieces opened (09:26:03, 09:26:05, 2 s and 1 s) - the picture
flowed again the moment the inputs were closed, and the forced-keyframe expression caught up in two
quick cuts. That matches the dshow microphone (USB headset) stopping delivery without EOF: amix
(`duration=longest`) waits for it, and ffmpeg's scheduler holds back the screen input that has run
ahead of the stalled audio output. This cannot be proven from the old log because the tail was
truncated; the new full tail + process state (below) will show it on the next occurrence.

Stall 1's trigger is outside AgentEyes (a Windows desktop switch). Stall 2's trigger is UNPROVEN: the
hypothesis above points outside AgentEyes (a USB audio device), but an ffmpeg/encoder hang is not ruled
out; the full tail logged from now on is what will settle it. What was in AgentEyes, and is fixed here
whatever the trigger: a dead input did not count as a failure, a hang took ~150 s to notice,
the evidence was truncated, and the restart split the clip.

## What changed

| File | Change |
|------|--------|
| `src/AgentEyes.Core/AlwaysOn/FfmpegStderr.cs` (new) | ffmpeg's stderr kept as whole lines (ring of 200). `Tail()` = last 20 lines in full. `FatalInputLine` = first "Error during demuxing" line (an input died while ffmpeg lives on). |
| `src/AgentEyes.Core/AlwaysOn/ContinuousRecorder.cs` | Uses `FfmpegStderr`. `HasExited` is also true when an input died. New `ProcessState`: "exited with code N" or "still running (pid N) - <why it counts as failed>". |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnEngine.cs` | `HungAfter` = piece length + 20 s (was 2 x piece + 30 s). Supervise logs reason + process state + last 20 stderr lines in full (`LastRestartReport`). The restart runs a NON-final keeper pass and keeps the open clip. Two captures in a row that die before writing a whole piece -> the next restart waits on the backoff (no 4-per-minute churn while a session stays locked). A capture that stays down longer than the after-window writes the open clip. Each restart is recorded (`status.RestartsToday`, `status.Restarts`). A piece's end is now min(next piece's start, its last write), so a restart's hole shows as a hole. |
| `src/AgentEyes.Core/AlwaysOn/KeeperRule.cs` | `restartBridge` (engine passes KeepAfter): a kept piece after a hole up to that long continues the open clip; each hole is reported in `plan.Bridged` and logged with its length. A piece under 5 s (`ShortPiece`) right after the open clip is joined to it, sound or not. |
| `src/AgentEyes.Core/AlwaysOn/AlwaysOnDay.cs` | `Restarts` list (per day, persisted): `AtUtc`, `Reason`, `LastPieceStartUtc`, `RecoveredUtc`. This is the feed for the History tab (#77). |
| `tests/AgentEyes.Tests/AlwaysOnStallTests.cs` (new) | 22 tests (17 in round 1, 5 more in round 2), below. |
| `tests/AgentEyes.Tests/AlwaysOnEngineTests.cs` | Fake recorder gains `ProcessState`; `Tick_KeeperFailsDuringARestart_TheCaptureStillRestarts` now dates its piece's write time on the test clock (the restart pass is no longer final, so it reads the piece's end from that). |

No App / REST code changed: `GET /always-on` serializes `AlwaysOnStatus` whole, so `restartsToday`
and `restarts` appear there automatically. No UI change. CenCon impact: no drift (no component-map or
privacy-posture change; the recording indicator behaviour is unchanged - a failed capture still shows
"retrying" when it is not recording).

## Acceptance criteria -> how QA verifies

1. **Supervise logs the last 20 stderr lines in full and the exit code / still-alive state.**
   Unit: `Tail_MoreLinesThanTheTail_ReturnsTheLast20EachInFull` (25 lines in, last 20 out, a 750-char
   line intact, no "..."), `Tail_NothingWritten_SaysSo`, `Add_AllDayOfLines_KeepsOnlyTheCapacity`,
   `Tick_CaptureFails_WritesTheFullTailAndTheProcessStateToTheLog` (the written log holds
   all 20 lines and the state). Code: `AlwaysOnEngine.RestartFailedCapture` log line
   `Supervise: <reason>; restarting. ffmpeg <state>. Its last 20 lines:` followed by the lines.
2. **Stall detected within 75 s of the last piece's expected end.** `HungAfter(60)` = 80 s from the
   piece's start; with the 15 s tick the worst case is 35 s past the expected end.
   Unit: `HungAfter_OneMinutePieces_IsCaughtWithin75SecondsOfTheExpectedEnd`,
   `Tick_NoNewPiece_IsRestartedWithin75SecondsOfTheExpectedEnd` (drives the 15 s tick grid; caught 28 s
   past the end), `Tick_NextPieceLateButInsideTheMargin_IsNotRestarted` (no false restart).
   A dead input (stall 1's exact lines) is caught on the NEXT tick (<= 15 s):
   `Add_TheScreenGrabDies_IsSeenAsADeadInput`, `IsFatalInputError_OrdinaryLines_AreNot`.
3. **A restart keeps the open clip; the gap is logged with its duration.**
   `Engine_StallDuringAClip_TheRestartContinuesTheSameClipWithItsLeadIn` replays stall 2 through the real
   engine, keeper and joiner: sound at 2:30, hang in the 3:00 piece, caught at 4:30, two short
   leftovers, new capture from 4:47, speech at 5:30 -> exactly ONE clip, starting at 0:00, holding all
   9 pieces. Regression-checked: with the old final pass restored the test fails (two clips).
   `Decide_KeptPieceAfterARestartHoleWithinTheBridge_ContinuesTheClipAndReportsTheHole`,
   `Decide_HoleLongerThanTheBridge_StartsANewClip`, `Decide_NegativeRestartBridge_Throws`,
   `Tick_CaptureDownLongerThanTheAfterWindow_WritesTheOpenClip` (a clip is not held open forever while
   retrying). Log line: `keeper: clip_<start> continues across a capture restart - a gap of Ns (HH:mm:ss
   to HH:mm:ss) has no piece; the clip is not split there`.
4. **Short pieces (< 5 s) from a restart are joined or dropped without closing the clip.** Joined.
   `Decide_ShortSilentPieceAfterTheOpenClip_IsJoinedAndTheClipStaysOpen` (the 2 s + 1 s leftovers of
   stall 2, silent, are kept in clip 7, which stays open), `Decide_ShortPieceWithNoOpenClip_IsDecidedByTheNormalRule`.
   Log line: `keeper: KEEP piece_... (2s) -> clip_... (shorter than 5s - a restart's leftover - joined to the clip it follows)`.
   Also `Tick_CapturesKeepDyingAtOnce_TheSecondRestartWaitsOnTheBackoff`.
5. **Root cause stated, fixed if in AgentEyes, restart harmless for external triggers.** Above.
6. **Unit tests for stall timing, restart-continues-clip, short pieces, full stderr.** Above -
   `AlwaysOnStallTests`, 17 tests.
7. **Live 2 h run - PENDING TESTER (after merge).** Not run by the developer (owner rule: no app launch,
   no capture on this machine from the dev session). What the tester checks:
   - Always-on on, owner working normally for 2 hours.
   - `GET http://127.0.0.1:7882/always-on` -> `status.restartsToday` and `status.restarts[]`. Pass if 0,
     or for EVERY entry: `recoveredUtc` is set and `recoveredUtc - (lastPieceStartUtc + 60 s)` <= 75 s.
     (Round 2: `recoveredUtc` is the start of the first piece the NEW capture opened - recording resumed -
     not the launch; `lastPieceStartUtc` is the newest piece the FAILED capture opened, its first piece
     included; it is null only when that capture never opened a piece - then use `atUtc - 80 s` as the
     reference.) An entry with `recoveredUtc` null while `status.state` is `retrying` is a capture that
     has not come back - a FAIL unless the session was locked at the time.
   - The log (`%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-<date>.log`): every `Supervise:` error shows
     `ffmpeg exited with code N` or `ffmpeg still running (pid N)...` and up to 20 full stderr lines, no
     leading "...". Copy those lines into the proof - they name the trigger for a stall like #2.
   - If a restart happened during a clip: the next `keeper:` lines show `continues across a capture
     restart - a gap of Ns`, and the clip file in the clips folder spans the restart (one file, not two).
   - Optional trigger for stall 1's path: lock the session (Win+L) for ~1 min while always-on runs. Expect
     `an input died: ... Error during demuxing` within 15 s, restarts with backoff while locked, recovery
     after unlock, and the open clip (if any) intact.
8. **`dotnet build` clean and `dotnet test` green.** `dotnet build AgentEyes.sln -c Release`: 0 errors.
   `dotnet test AgentEyes.sln -c Release`: 1747 passed, 0 failed.
   NOTE for QA: `CameraPreviewTests` (3 tests: `ASessionThatSurvivesItsStop_IsNotCountedAsARelease`,
   `Dispose_WithACleanStop_RetainsNothingAndUnregisters`, `Dispose_WhoseCameraSurvivesTheStop_...`) flake
   under full-suite load on this machine - reproduced WITHOUT this branch's tests
   (`--filter FullyQualifiedName!~AlwaysOnStallTests`, 1 of 3 runs). They pass alone and in most full runs.
   Not touched by this change.

## Smokes

The change touches the always-on capture path. The dev session ran no smoke (owner rule). QA/tester:
the live check in item 7 covers it; no GUI change, so no gui-smoke is needed.

Reminders: drive the app through the REST API / UIA / PrintWindow only; never force-foreground and
synthesize input without warning the owner; the recording HUD is capture-excluded - assert state via
`/always-on` or UIA, not a screen grab.

I believe this is finished.

## Round 2 - Codex fixes

Review verdict: PR #82 comment 5818513011 (REQUEST CHANGES). All items addressed.

**Blocking 1 [P1] - stats I/O broke the recorder lifecycle.** All today.json writes on the restart
path go through `AlwaysOnEngine.SaveDay`, which logs an IOException / UnauthorizedAccessException as an
ERROR and returns; the history stays in memory and in the status, and the next save writes it. The
recovery stamp no longer lives inside `TryRestart`'s try (it moved to `NoteRecovery`, after a start has
fully succeeded), so a save failure can never send a started capture to the catch. `StartRecorder` now
refuses to start while `_recorder` is set, so a second capture can never overwrite the reference.
Regressions: `Tick_StatsFileLockedWhenACaptureFails_TheCaptureStillRestarts` (stats file held with
FileShare.None during the failing tick: the capture is stopped and restarted, Listening),
`Tick_StatsFileLockedWhenTheRestartRecovers_NoSecondCaptureIsStarted` (locked while the recovery is
stamped: still exactly 2 recorders, the live one running, and Stop stops it).

**Blocking 2 [P2] - a known-dead replacement was marked recovered.** `StartRecorder` checks
`HasExited` (process exit, dead input, stalled feeder) right after `Start`; a replacement that is
already failing is disposed (which stops its ffmpeg) and reported as a failed start -> Retrying on the
backoff. `RecoveredUtc` is stamped only by `NoteRecovery`: the new capture is running, not failing, and
has opened a piece of its own (ffmpeg opens a piece on the first encoded picture); the value is that
piece's start. Regressions: `Tick_ReplacementIsAlreadyFailingWhenItStarts_StaysRetryingAndIsNotRecovered`,
`Tick_ReplacementOpensAPiece_OnlyThenIsTheRestartRecovered`. The backoff test now lets the replacement
start healthy and die on the next pass (it no longer expects Listening for a dead replacement).

**Non-blocking 1 - the first piece before `_recorderStartedUtc`.** The launch time is now taken BEFORE
ffmpeg starts, and the current capture's pieces are known BY NAME (every piece not already in the folder
at launch), not by comparing whole-second names with a sub-second clock. Regression:
`Tick_FirstPieceOpenedDuringTheStartUpWait_CountsAsTheCapturesLastPiece` (launch at 0.6 s, first piece
named 0 s, start takes 1.5 s: the stall is caught within 75 s and `LastPieceStartUtc` is that piece).
Live-check instructions in item 7 updated.

**Non-blocking 2 - stall 2 is a hypothesis.** The root-cause section now says so.

**Non-blocking 3 - the stderr test reads the WRITTEN log.**
`Tick_CaptureFails_WritesTheFullTailAndTheProcessStateToTheLog` reads this test run's own log file
(issue #78 isolates it) and asserts the Supervise ERROR line with the process state, and each of the
20 stderr lines (900+ characters each) as its own whole log line, with no "..." anywhere.

Mutation check: reverting each fix (plain `_day.Save` on the restart path, no post-start `HasExited`
check, the old time comparison for the capture's pieces) makes those four regressions fail.

Gate: `dotnet build AgentEyes.sln -c Release` 0 errors; `dotnet test AgentEyes.sln -c Release`
1767 passed, 0 failed (the `CameraPreviewTests` load flake noted in item 8 recurred in one of two runs;
unrelated, it also occurs without this branch's tests). 22 tests in `AlwaysOnStallTests`.
