# Developer handoff - issue #28, round 9 (PR #32)

Branch: `issue-28-camera-failure-boundaries`
Gate verdict answered: `docs/cencon/review/pr32-issue28-gate-round7.md` (REJECT, ONE blocking
finding), committed here in the same change - the gate never commits its own verdicts.

Round 8's design is UNCHANGED and was not redesigned. No production call site assigns a
`CameraStopKind`; `FfmpegCameraRecorder.StopKind` still delegates to the single derivation in
`CameraTerminationRecord`; an earned `abandoned` is still derived first from a counter that only
rises. This round is one surgical change INSIDE that derivation.

---

## The defect

`CameraTerminationRecord` kept `_quitsDelivered` as one LIFETIME counter, and
`ExitObservedAfterTermination()` recorded the newest exit without recording whether the quit in
THAT round had been delivered. The derivation therefore accepted the unrelated pair "some quit was
delivered in this recorder's life" + "the latest observed exit is 0 or 255", joining facts from two
different termination rounds.

The production sequence, Stop() then the normal Dispose() retry:

1. Round 1 delivers `q`. ffmpeg does not exit within the quit wait, survives the kill and the kill
   wait; the round is recorded REFUSED. Stop kind absent, completeness `unknown`.
2. `Dispose()` opens the retry round. Its `SendQuit()` write FAILS while the process exits 0 - the
   pipe/exit race the round-4 fix handles. The recorder correctly skips `QuitDelivered()`, observes
   the exit after the wait and reports it.
3. One refused round is not `abandoned`, so the derivation reused round 1's stale delivery count
   with round 2's exit 0 and returned `clean-quit`. With two progress advances and complete stderr
   that reopened completeness to `yes`.

## The fix

`src/AgentEyes.Core/Video/CameraTerminationRecord.cs` only.

- New per-round flag `_roundQuitDelivered`, reset by `BeginRound()`, set by `QuitDelivered()`.
  A delivery is now evidence about the round it happened in.
- `ExitObservedAfterTermination()` latches `_exitFollowedADeliveredQuit = _roundQuitDelivered` -
  the exit and the delivery that could explain it are paired at the one moment BOTH are known,
  while the round they belong to is still known. It is ASSIGNED, not OR-ed, because the newest
  observed exit replaces the previous one, so the delivery fact weighed against it must be
  replaced with it.
- The derivation's clean-quit clause reads `_exitFollowedADeliveredQuit` instead of the lifetime
  `_quitsDelivered > 0`.
- `Describe()` carries `exitAnsweredADeliveredQuit=` so the log shows which fact the derivation
  used. `_quitsDelivered` is retained for diagnostics only.

The design property is intact: the derivation is still the single place a stop kind is decided,
there is still no method that takes a `CameraStopKind`, and a future call site still cannot record
an unearned one. The class header gained a paragraph naming the round-7 property ("every
observation is evidence about its own round, and nothing else").

No call site changed. `FfmpegCameraRecorder` is untouched.

## The three rows, pinned

Driven through `Stop()` then the `Dispose()` retry, exactly as the gate drove them. All three end
with quits=2, kills=1 and a process that exited 0; the ONLY variable is which round delivered a
"q". Verbatim probe output is in `round9-probe-results.txt`.

| Row | Expected | Actual (head) | Actual (this change) |
|-----|----------|---------------|----------------------|
| `PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | absent / unknown | clean-quit / yes (WRONG) | absent / unknown |
| `PRIOR_UNDELIVERED_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | absent / unknown | absent / unknown | absent / unknown |
| `PRIOR_REFUSAL_THEN_DISPOSE_RETRY_QUIT_DELIVERED_EXIT_0` | clean-quit / yes | clean-quit / yes | clean-quit / yes |

Only the first row moved. The two controls are unmoved, so this is not a blanket refusal of
recovery success.

## Tests added (4), and exactly how each was demonstrated load-bearing

All four are in `tests/AgentEyes.Tests/CameraFailurePathTests.cs`. Every mutation below was applied
to the product file, the SOLUTION WAS REBUILT, and the FULL suite re-run; the file was restored and
the suite re-run green afterwards. Full transcript: `mutation-evidence-round9.txt`.

1. `StopKind_WhenARefusedRoundsQuitIsFollowedByAnUndeliveredRetryExit_IsNotACleanQuit`
   **Demonstrated to FAIL against current head.** Mutation **R9-M1** restores the EXACT head
   derivation (`_quitsDelivered > 0`): 1 failed / 950 passed, and the single failure is this test.
   Mutation **R9-M4** (drop the round association at the join, latching from the lifetime count
   instead) also fires it alone. This is the blocking defect.

2. `StopKind_WhenNeitherRoundDeliveredItsQuitAndTheProcessExitedZero_IsNotACleanQuit`
   Control - passes on head, as the gate reported, so it isolates the defect to the cross-round
   join rather than to the delivery clause. **Demonstrated genuinely load-bearing** by mutation
   **R9-M2** (delete the delivery clause entirely, the over-simplified "fix"): 3 failed, and this
   test is one of them.

3. `StopKind_WhenTheRetryRoundsOwnQuitIsDeliveredAndAnswered_IsStillACleanQuit`
   Control - passes on head. **Demonstrated genuinely load-bearing** by mutation **R9-M3**, the
   over-correction the brief warned about (`&& _refusedRounds == 0`, i.e. any refused round
   anywhere poisons the whole recorder): 1 failed / 950 passed, and the single failure is this
   test. This is the test that stops the fix demoting every camera that needed a retry to
   "unknown".

4. `CameraTerminationRecord_QuitDeliveredWithoutAQuitInThisRound_Throws`
   The gate's non-blocking round-7 follow-up: the structural promise pinned AT THE RECORD, so it
   stays executable after QA's uncommitted probe is gone. It also asserts the guard again after
   `BeginRound()` has moved on, which is the cross-round confusion this round is about.
   **Demonstrated load-bearing** by mutation **R9-M5** (remove `QuitDelivered()`'s precondition):
   1 failed / 950 passed, and the single failure is this test.

## Verification (I ran all of it - nothing is left for a human to run)

Isolated worktree `D:\ReposFred\agenteyes-dev28-r7`, restored first so a missing-assets build
could not be mistaken for evidence. Release output read from `bin\x64\Release\` only.

- `dotnet restore AgentEyes.sln`: up to date, completed.
- `dotnet build AgentEyes.sln -c Release`: **Build succeeded, 0 Error(s), 4 Warning(s)** (the four
  pre-existing xUnit analyzer warnings in `PostRecordingQueueTests.cs` and
  `StrandedCameraOwnerTests.cs` - unchanged by this work).
- `dotnet test AgentEyes.sln -c Release`: **Failed: 0, Passed: 951, Skipped: 0** (947 before, plus
  the four new tests).
- Mutation sweep R9-M1..M5: every mutation fired, each caught by exactly the intended test;
  restored and green.
- Three-row probe on head vs this change: `round9-probe-results.txt`. The probe was temporary and
  is NOT committed as code - the three rows it measures are pinned by the committed tests above.
- `git diff --check origin/main...HEAD`: clean across the full PR head.
- Stack merge into `origin/issue-36-circular-camera-overlay`: throwaway integration worktree,
  `--no-commit --no-ff`, no conflicts; restore completed; Release build succeeded; **1,163 passed,
  0 failed, 0 skipped** (1,159 before, plus the four new tests); staged merge diff check clean.
- ASCII-only verified byte-by-byte on every file touched, including the gate verdict, which was
  ASCII-normalized to match the rounds already committed here.

## Not regressed (re-verified by the full green suite plus the probe)

Both round-6 blockers; monotone `abandoned`; the round-5 blockers (`DELIVERED_QUIT_THEN_EXIT_1`,
`DIED_BEFORE_DISPOSE_RETRY`); AC17's positive control (healthy quit, exit 255, two advances,
complete stderr -> clean-quit / yes); exit 255 earning only the stop kind and not completeness; the
accepted exit set still ENUMERATED as `{ 0, 255 }` and not ranged; the genuine three-clause
abandoned control; the round-1/3/4 defects; the AC3/AC9 timing balance.

## CenCon impact

No drift. No component-map change, no privacy-posture change: this is a derivation correctness fix
inside an existing internal type. No public API, no UI, no manifest schema change - only the value
written into an existing manifest field becomes ABSENT in one previously-misreported case.

## How QA should test it

The subject is a pure derivation over an observation history and has no runtime surface of its own,
so `dotnet build` + `dotnet test` plus code reading is the whole gate. No heavy smoke is warranted
by this change (nothing in the App, the Control API, the installer or the audio/ffmpeg path moved).

If QA wants an independent probe rather than trusting these tests, the three rows are reproducible
in a few lines against `FakeCameraProcess`: `QuitEndsIt = false, KillEndsIt = false`, one
`Assert.Throws<CameraStopFailedException>(() => rec.Stop())` for the refused first round, then vary
only round 2 - `proc.QuitFailsWith = () => proc.End(0)` (row 1), the same set from the start with
`if (proc.Quits == 2) proc.End(0)` (row 2), or `proc.QuitEndsIt = true` (row 3) - and read
`rec.StopKind` / `rec.Completeness`. Please build and run any such probe from an isolated worktree:
the running tray app locks the normal Release output and can hand back a stale false green, and
`bin\Release\` (without `x64`) holds a months-stale binary.

Reminders carried for QA: the focus-free layers are REST / UIA / PrintWindow; never force-foreground
and synthesize input without warning the human; the recording HUD is capture-excluded, so HUD and
recording state is asserted via UIA or `/status`, never a screen grab.

**I believe this is finished.**
