# Issue #28 - Developer handoff, ROUND 8 (Review Gate round 6 REJECT)

PR #32, branch `issue-28-camera-failure-boundaries`.
Gate verdict answered: `docs/cencon/review/pr32-issue28-gate-round6.md` (committed in this change -
the gate never commits its own verdicts, and it was untracked in the working tree).

**This round is a DESIGN FIX, not another patch.** Read the next section before the criterion table.

---

## Why the design changed, and not just the two named lines

The `abandoned` stop kind produced a blocking gate finding in FOUR CONSECUTIVE ROUNDS (3, 4, 5, 6).
Each round fixed the exact path the gate named, and each time the gate found an adjacent path in
the same state machine. That is not a coding-error pattern.

**The root cause, stated plainly:** `abandoned` is a claim about HISTORY - the process survived a
quit AND a kill AND a retry - but it was ASSIGNED at individual call sites (`Stop`, `Dispose`, the
kill-timeout path), and **no call site knows the full history of attempts made against the
process.** Each round taught one more call site to guess correctly. There is always another call
site.

### 1. The history is now RECORDED explicitly

New file: `src/AgentEyes.Core/Video/CameraTerminationRecord.cs`.

One object per recorder holds what AgentEyes actually DID to the ffmpeg process and what each
attempt observed:

| Recorded fact | Written by |
|---|---|
| a termination ROUND was opened | `Stop()`, and `FailOpen()`'s rollback kill |
| a quit was ATTEMPTED (before the write, so a throwing write still counts as interference) | `Stop()` |
| the quit was DELIVERED (the write to stdin returned) | `Stop()` |
| a kill was ATTEMPTED (before the call, so a throwing `Kill` still counts) | `KillOrThrow()`, `FailOpen()` |
| the kill was CONFIRMED to have ended the process | `KillOrThrow()`, `FailOpen()` |
| the kill was REFUSED (the wait timed out, the process is still there) | `KillOrThrow()`, `FailOpen()` |
| the process EXITED with code N, after this recorder had interfered with it | `Stop()` |
| the process was gone and this recorder had NEVER touched it | `Stop()` (both observation points) |

Counters only ever rise. `RefusedRounds` is the count of termination rounds that ended with the
process still alive.

### 2. The stop kind is DERIVED in ONE place, as a pure function of that history

`FfmpegCameraRecorder` no longer has a settable stop-kind field. There were five assignment sites
(`_stopKind = ...` at the old lines 971, 1034, 1075, 1164, 1267); there are now zero.
`FfmpegCameraRecorder.StopKind` is `_history.StopKind`, and that is:

```
if (AbandonedEarned)                       -> abandoned      // RefusedRounds >= 2
if (goneUntouched)                         -> exited-early
if (killConfirmedGone)                     -> force-killed
if (exitObserved && quitDelivered
    && exitCode in QuitExitCodes {0,255})  -> clean-quit
otherwise                                  -> ABSENT (null)  // completeness "unknown"
```

`Completeness` reads the derived kind, so it inherits the same single source of truth.

### 3. It is MONOTONIC - an earned `abandoned` is never replaced

`AbandonedEarned` is a function of a counter that only rises, and it is tested **first**. A later
recovery quit or kill that finally lands is still recorded as an observation, but it cannot change
the derived kind. What the later success correctly changes is the LIVE status, `IsAbandoned`, which
asks the process itself every time (`AnyKillRefused && !_terminated && !_proc.HasExited`).

### 4. WHY A FUTURE CALL SITE CANNOT RECORD AN UNEARNED STOP KIND

This is the property four rounds of patching failed to produce, and it is structural, not a
convention:

* **There is no method on `CameraTerminationRecord` that takes a `CameraStopKind`.** "abandoned" is
  not something a caller can say. It is something the history either shows or does not. A call site
  added tomorrow can only report what it *did*.
* **Every observation is validated against what the record already knows, and a violation THROWS**
  (`InvalidOperationException`, logged first):
  * `QuitDelivered()` is refused unless a quit was attempted in the current round.
  * `KillConfirmedProcessGone()` / `KillRefused()` are refused unless a kill was issued in the
    current round, and a round may report only ONE outcome.
  * `ExitObservedAfterTermination()` is refused unless this recorder had already interfered with
    the process - so an exit nobody caused cannot be read as an answer to a quit.
  * `ProcessGoneWithoutAnyAttempt()` records **nothing** once any attempt has been made. That is
    its documented contract rather than a swallow: the retry re-reaches that observation point on
    every later pass, and it must not be able to write `exited-early` over a fight in progress.
* **The worst a wrong or missing observation can now produce is an ABSENT kind and
  `complete: unknown`** - the honest answer, and the one the amended spec asks for in every
  unanticipated case. It can no longer produce a CLAIM.

`RefusedRoundsForAbandoned = 2` is the whole of the definition ("the attempt, and the retry") and
it lives in one place. `Dispose` reads that same constant, which is how blocker 1 is closed.

---

## The two blocking defects, and how each is closed

### Blocker 1 - a direct `Dispose()` earned `abandoned` after ONE quit and ONE kill

Gate probe: `DIRECT_DISPOSE_ONE_KILL quits=1 kills=1 exited=False kind=abandoned complete=unknown
abandoned=True`.

The gate offered two acceptable fixes: prove a refused attempt already existed on entry, **or make
`Dispose` perform the actual retry**. This change does the second, because the CLI failure boundary
(`Commands.cs:494`, `:499`) really does call `cameraRec?.Dispose()` with no earlier explicit stop,
so that Dispose performs the FIRST quit and the FIRST kill.

`Dispose` now runs termination rounds until the process is confirmed gone, or until the recorder
has accumulated the refused rounds the definition counts:

```csharp
for (int round = 1; round <= CameraTerminationRecord.RefusedRoundsForAbandoned; round++)
{
    if (_terminated) break;
    if (round > 1 && _history.AbandonedEarned) break;   // round 1 ALWAYS runs
    try { Stop(); } catch (Exception ex) { Log.Error(...); }
}
```

It is structurally bounded: every `Stop()` that returns or throws with `_terminated` still false has
recorded exactly one refused round, so the loop cannot run more than twice. Round 1 always runs,
which is what keeps `StrandedCameraOwner.Recover()` able to free a camera from a recorder that had
already earned the observation.

Behaviour now:

| Entry | Rounds run | Result |
|---|---|---|
| direct Dispose, process survives both rounds | 2 (quits=2, kills=2) | `abandoned` - genuinely earned |
| direct Dispose, process dies under the RETRY's kill | 2 | `force-killed` / `no` - NOT abandoned |
| direct Dispose, process dies between rounds | 2 | stop kind ABSENT / `unknown` |
| Stop failed, then Dispose (the normal path) | 1 | `abandoned` - unchanged, kills still 2 |

### Blocker 2 - an earned `abandoned` was overwritten with `clean-quit` / `yes`

Gate probe: `RECOVERY_AFTER_ABANDONED earned=abandoned quits=3 kills=2 exited=True kind=clean-quit
complete=yes abandoned=False`.

Closed by the monotone derivation (point 3 above). `StrandedCameraOwner.Recover()` calling
`Dispose()` again on a retained, still-live recorder now records the later quit or kill as an
observation, but `StopKind` stays `abandoned` and `Completeness` stays `unknown`. `IsAbandoned`
correctly becomes false, because the process really did end. The completeness one-way door does not
reopen.

---

## Also in this change

* **`FailOpen()`'s rollback kill is now recorded as a termination round.** It was invisible to the
  old flags, so a process that died after that refused kill could be mislabelled `exited-early` by
  a later `Stop`. It is now honest in both directions: the exit is not misread, and a Dispose that
  follows that refusal is a genuine RETRY rather than a first attempt. (This is what keeps
  `proc.Kills == 2` on the failed-open recovery tests instead of 3.)
* **`QuitExitCodes { 0, 255 }` moved, still ENUMERATED, with its whole rationale**, from
  `FfmpegCameraRecorder.cs:221` to `CameraTerminationRecord.cs` - beside the derivation that reads
  it. It is not ranged; a pointer comment is left where it used to live. Mutation M5 restores the
  round-5 `exitCode >= 0` range and a test fires.
* **A coverage gap this round's own mutation matrix exposed was closed.** Mutation M6 (record the
  quit as DELIVERED even when the write to stdin threw) fired NO test at first: the existing
  round-4 test pairs an undelivered quit with exit -5, so the exit-code set alone refuses the clean
  quit and the DELIVERY clause is never the thing under test. New test
  `Stop_WhenTheQuitCouldNotBeDeliveredAndTheProcessExitedZero_IsStillNotACleanQuit` drives an
  undelivered quit whose process exits **0** - an accepted code - so the delivery clause stands
  alone. M6 now fires.

## Nothing the gate confirmed fixed was regressed

All 942 pre-existing tests pass **unchanged** - not one was edited to accommodate this redesign,
which is itself evidence that the derived kind reproduces the behaviour the gate approved:

* both round-5 blockers (`DELIVERED_QUIT_THEN_EXIT_1`, `DIED_BEFORE_DISPOSE_RETRY`);
* AC17's positive control (healthy quit, exit 255, two advances, complete stderr -> `clean-quit` / `yes`);
* exit 255 earning only the STOP KIND (255 + incomplete stderr -> `clean-quit` / `unknown`);
* the genuine three-clause control (survives explicit Stop AND the Dispose retry -> `abandoned` / `unknown`);
* the round-1, round-3 and round-4 defects; the AC3/AC9 timing balance.

---

## Proof (all of it run by this agent - the human runs nothing)

| Check | Result |
|---|---|
| `dotnet build AgentEyes.sln -c Release` (own worktree, own restore) | `Build succeeded.` **0 Error(s)**, 4 pre-existing warnings |
| `dotnet test AgentEyes.sln -c Release` | **Passed! Failed: 0, Passed: 947, Skipped: 0, Total: 947** |
| `git diff --check origin/main...HEAD` | clean |
| Stack merge into `origin/issue-36-circular-camera-overlay` | conflict-free; Release build clean; **1159 passed, 0 failed, 0 skipped** |

The stack check was done in a throwaway detached worktree at `origin/issue-36-circular-camera-overlay`
(`75e62ad`), merging `origin/issue-28-camera-failure-boundaries` with `--no-commit --no-ff`: the merge
completed with no conflicts (only `FfmpegCameraRecorder.cs` auto-merged), `dotnet restore` then
`dotnet build -c Release` succeeded with 0 errors and the same 4 pre-existing warnings, and the
suite was **1159 passed, 0 failed, 0 skipped** - the 1154 the gate previously verified plus this
round's 5 new tests. The worktree was then removed.

Release output is `bin\x64\Release\net8.0-windows10.0.19041.0`. A build or a targeted test command
that omits the solution's x64 platform finds no assembly, and that empty result is a broken
instrument, never a pass.

### Failing-first evidence

`docs/cencon/proof/issue-28/mutation-evidence-round8.txt` carries the full quoted output. Method:
the round-7 head source was restored verbatim (`git checkout HEAD -- FfmpegCameraRecorder.cs`, new
file deleted) while the NEW TESTS were kept, then built and run.

| New test | Against the round-7 head |
|---|---|
| `Dispose_WithNoEarlierStop_PerformsTheRetryBeforeAbandonedCanBeEarned` | FAIL - `Expected: 2, Actual: 1` (quits). The retry never ran. |
| `Dispose_WithNoEarlierStop_WhenTheRetryKillLands_RecordsForceKilledNotAbandoned` | FAIL - "the retry must actually terminate the process" |
| `StopKind_WhenALaterRecoveryQuitFinallyLands_KeepsTheEarnedAbandoned` | FAIL - `Expected: Abandoned, Actual: CleanQuit`; and with the assertions swapped, `Expected: Unknown, Actual: Yes` - the gate's `complete=yes` compounding harm, reproduced |
| `StopKind_WhenALaterRecoveryKillFinallyLands_KeepsTheEarnedAbandoned` | FAIL - `Expected: Abandoned, Actual: ForceKilled` |
| `Stop_WhenTheQuitCouldNotBeDeliveredAndTheProcessExitedZero_IsStillNotACleanQuit` | **PASSES** on the head - stated plainly. It is not a round-6 blocker; its known-bad input is mutation M6, not the head. |

### Mutation matrix on this change's own design

Each applied to the fixed source, FULL suite run, source restored byte-for-byte afterwards
(verified with `diff -q`). Every mutation fires at least one test - a mutation firing nothing would
be a broken check, not a clean run.

| # | Mutation | Tests that fired |
|---|---|---|
| M1 | `RefusedRoundsForAbandoned` 2 -> 1 | 3 |
| M2 | the monotone `abandoned` test removed from the top of the derivation | 5 |
| M2b | the monotone test demoted BELOW clean-quit/force-killed (the round-6 overwrite, reintroduced) | 2 (both recovery tests) |
| M3 | `Dispose` runs one round again instead of retrying to the definition | 2 (both direct-dispose tests) |
| M4 | `ProcessGoneWithoutAnyAttempt` drops its precondition | 1 |
| M5 | enumerated quit exit codes replaced by the round-5 `exitCode >= 0` range | 1 |
| M6 | `QuitDelivered` recorded even when the write to stdin threw | 1 (the new delivery test) |

---

## How QA should exercise it

This change has **no new runtime surface** - no UI, no API route, no manifest field added or
renamed. The manifest still carries `CameraStopKind` (`clean-quit` / `force-killed` /
`exited-early` / `abandoned`, or ABSENT) and `CameraComplete` (`yes` / `no` / `unknown`), spelled
exactly as before; only how the stop kind is arrived at has changed.

1. **Independent review of the derivation.** `CameraTerminationRecord.StopKind` is the whole
   decision. Confirm by search that `src/` contains no assignment of a `CameraStopKind` to a field
   anywhere: `grep -rn "CameraStopKind\." src/` should show only the enum, the derivation, the
   manifest spellings, and comparisons. An empty grep result here is a broken command, not a pass -
   check that the pattern matches something first.
2. **Re-run the gate's two round-6 probes** if you keep a probe harness: `DIRECT_DISPOSE_ONE_KILL`
   should now show `quits=2 kills=2` before any `abandoned`, and `RECOVERY_AFTER_ABANDONED` should
   show `kind=abandoned complete=unknown abandoned=False`.
3. **Re-run the mutation matrix** from the table above; the script is reproducible from the
   descriptions in `mutation-evidence-round8.txt`.
4. `dotnet build` + `dotnet test` yourself, from a worktree of your own - the running tray app locks
   the normal Release output and can hand back a stale false green. Restore before building; a
   fresh worktree has no assets files and the resulting failure is a broken instrument, not
   evidence.
5. **Smokes:** api / gui smokes are NOT indicated. Nothing in this change touches the REST Control
   API, the WPF views, audio, ffmpeg invocation, or the recording flow's happy path. If you want a
   runtime check anyway, the camera stop path is the area.

Reminders carried forward: the focus-free layers are REST / UIA / PrintWindow; never force-foreground
the app and synthesize input without warning the human; the recording HUD is capture-excluded, so
HUD state is asserted via UIA or `/status`, never a screen grab.

## CenCon impact

No drift. No change to the component map and no change to the privacy posture (visible /
controllable): this is a correctness fix inside the camera recorder's termination bookkeeping. It
strengthens the posture if anything - a stranded camera process is now less likely to be reported as
a finished one.

## Statement

I believe this is finished. The design change is the fix; the two named paths are consequences of
it, not the extent of it. Build clean, 947/947 green, every new check demonstrated failing against
the round-7 head first, and every guard in the new design demonstrated firing under mutation.
