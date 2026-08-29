# Issue #28 - Developer handoff, round 7

Branch `issue-28-camera-failure-boundaries`, PR #32. This round answers the Review Gate's
round-5 verdict (`docs/cencon/review/pr32-issue28-gate-round5.md`, committed with this
change - the gate does not commit its own verdicts).

Two blocking defects, plus the follow-up the gate ruled non-blocking and the proof-file
hygiene it named. Nothing else was touched: every previous round that reached further
introduced a new defect.

---

## Blocking defect 1 - every non-negative exit code was a clean quit

`FfmpegCameraRecorder.Stop` read the exit code but tested `exitCode >= 0`, so every value
from 0 to `int.MaxValue` counted as proof that ffmpeg had answered `q` and finalized the
file. That test was reaching for the right thing - a NEGATIVE code is the operating system
reporting an abnormal termination, so ffmpeg never ran its own exit path - but ffmpeg's OWN
error codes are positive. A `q` that is delivered and then meets a muxer, disk or encoder
failure while closing exits 1 and says so, and the manifest recorded `clean-quit` / `yes`
over a take ffmpeg had just reported as failed.

**The accepted set is now enumerated: `{ 0, 255 }`** (`QuitExitCodes`).

- `0` - ffmpeg's ordinary success exit; every clause of the encode ran, trailer included.
- `255` - what ffmpeg returns when it stops because the interactive `q` was pressed rather
  than because the input ended. It still ran its own exit path and still wrote the trailer,
  and it is the code AC17's positive control actually observes.

**255 deliberately REMAINS accepted, on the same footing as 0.** The gate's caveat is the
whole reason the set is a list rather than a range: 255 being acceptable does not make every
other positive code a clean quit, and rejecting 255 would turn every healthy recording on
some machines into `unknown` - the fail-open defect wearing the opposite mask. Widening the
set later needs an ffmpeg build OBSERVED to answer `q` with that code.

Everything else is unanticipated: **no stop kind is written** (the manifest field is absent)
and `Completeness` answers `unknown`, which is what the amended contract requires.

## Blocking defect 2 - `abandoned` was asserted too early and never retracted

`abandoned` is DEFINED as outliving the quit, the kill AND the Dispose retry. `KillOrThrow`
wrote it after the FIRST kill timeout - two clauses of three - and the retry then
deliberately refused to replace or clear it. A camera that ignored `q`, survived the first
kill and then exited ON ITS OWN before the retry was recorded as having survived a retry it
never faced. The manifest is saved only after Stop and Dispose have both run
(`RecordingStopSequence`), so the provisional value became durable.

The two questions are now separated, because only one of them may be answered early:

| Question | Answered by | When |
|---|---|---|
| Is there a live ffmpeg on the webcam RIGHT NOW? | `IsAbandoned` | from the first refused kill - that is when the owner has to decide whether to keep the recorder. It reads the new `_killRefused` and still asks the process every time. |
| How did the process END? | `StopKind` | only once it has ended. `CameraStopKind.Abandoned` is written in `Dispose`, at the first line in the object's life where all three clauses have actually happened. |

`Dispose` never retracts it: a process that survived all three survived all three, so a later
Dispose that finally finds it gone leaves the record alone.

A retry that finds the process gone now writes **nothing at all** - not `abandoned`, and not
`exited-early` either. The two `exited-early` sites in `Stop` used to be guarded by
`_stopKind == null`, which only worked because the provisional value was there to block them.
With it gone the guard has to say what it always meant, so it is now `!_terminationAttempted`:
"the process has already exited" is evidence that it exited EARLY only while nothing has yet
asked it to exit.

**The test that required the overclaim is fixed, as the gate directed.**
`StopKind_WhenTheRetryFindsAnAbandonedProcessGone_StaysAbandonedRatherThanBeingRelabelled` is
now `StopKind_WhenTheProcessDiesBeforeTheDisposeRetry_IsNeverRecordedAsAbandoned`, and it
requires the stop kind to remain **ABSENT** (`Assert.Null`), before and after the retry. The
property the old test was really protecting - a record that satisfied all three clauses is
never relabelled - was not dropped; it has its own test
(`StopKind_WhenTheSurvivingProcessFinallyDiesAfterTheDisposeRetry_StaysAbandoned`).

## Follow-up - the two non-firing CLI mutations

Both transfers were already present and correctly ordered in the product; the defect was in
the guard, which proved only that AT LEAST ONE owner call exists in `Commands.Video`. It is
now per site, using IL exception regions to tell two identical calls apart: the failed-open
transfer is INSIDE a try covered by a Finally region, and the final transfer IS that handler,
which no try covers. Each deletion fails on the assertion about its own boundary. See
section 2 of `mutation-evidence-round7.txt`.

## Hygiene

The five round-6 QA proof files were committed with CRLF, so full-PR `git diff --check`
reported whitespace errors. Normalized to LF. `git diff --check origin/main...HEAD` is now
clean over the WHOLE head, not only `src` and `tests`.

---

## Proof

| Artifact | What it shows |
|---|---|
| `round7-probe.cs.txt` | the seam probe source (round-6 QA probe + 3 cases) |
| `round7-probe-results.txt` | the same probe run against the round-6 tree, round-7 head, and round-7 merged into the stack tip |
| `mutation-evidence-round7.txt` | every new/changed test failing first, and the two CLI mutations |

**The probe fires on the rejected tree and reports honestly on this one.** Against round-6
head `b61553c`:

```
DELIVERED_QUIT_THEN_EXIT_1   stopKind=clean-quit  complete=yes
DIED_BEFORE_DISPOSE_RETRY    hasExited=True isAbandoned=False stopKind=abandoned
```

Against round-7 head:

```
DELIVERED_QUIT_THEN_EXIT_1   stopKind=(not observed)  complete=unknown
DIED_BEFORE_DISPOSE_RETRY    hasExited=True isAbandoned=False stopKind=(not observed) complete=unknown
                             abandonedAfterStop=True    <- the live flag still fires at the first refusal
```

**Nothing else moved.** Identical on both trees, and on the merged stack:

```
HEALTHY                       clean-quit / yes        AC17's positive control, still earned
DELIVERED_QUIT_THEN_EXIT_255  clean-quit / yes        255 still accepted
ONE_TICK_STALL_2_9S           unknown                 round-4 defect 2
ZERO_TICK_INCOMPLETE_STDERR   unknown, no loss claim  round-4 defect 3
FAILED_QUIT_THEN_ERROR_EXIT   (not observed)/unknown  round-4 defect 1
FORCED_KILL_AFTER_OUTPUT      force-killed / no       AC14
EXITED_EARLY                  exited-early / no       AC10
RETAINED_PROCESS_ALIVE        abandoned, claim held   AC16 - it DID survive all three
RETAINED_PROCESS_DIED         claim released, no row  round-4 defect 4
```

## Gate

Run in an isolated worktree (`D:\ReposFred\agenteyes-dev28-r7`), because the running tray app
locks the normal Release output. Release output is under `bin\x64\Release\`.

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Failed: 0, Passed: 942, Skipped: 0, Total: 942
```

**Stack above, verified as round 6 did.** `origin/issue-36-circular-camera-overlay` (which
carries #33 and #35) in a throwaway worktree, this head merged in without committing:

```
git merge --no-commit --no-ff <round-7 head>  ->  Automatic merge went well
dotnet build AgentEyes.sln -c Release         ->  Build succeeded.  0 Error(s), 4 Warning(s)
dotnet test  AgentEyes.sln -c Release         ->  Failed: 0, Passed: 1154, Total: 1154
```

(1154, not the round-6 1152, because this round adds two tests.) The probe was run against
that merged tree too - section C of `round7-probe-results.txt` - and is identical to round-7
head. No upper-layer regression. The concurrent QA session on `issue-33-hud-live-preview` was
not touched; the merge was made into a throwaway worktree, which was removed afterwards.

## Scope and CenCon impact

`AgentEyes.Core` only: `Video/FfmpegCameraRecorder.cs`, plus tests. No change to
`Commands.cs`, to the App, to the Control API, or to the manifest's field names or spellings -
the wire contract test (`TheManifestSpellings_AreTheFourStopKindsAndTheThreeVerdicts`) is
untouched and green. No component-map or privacy-posture drift: **no CenCon drift**.

## How QA should verify

1. **The suite, in your own worktree.** `dotnet build` + `dotnet test` at solution level.
   Never `bin\Release\` - the x64 path is not optional.
2. **The seam, independently.** Build `round7-probe.cs.txt` as a console app named
   `AgentEyes.Tests` (that assembly name is what the `InternalsVisibleTo` in
   `AgentEyes.Core.csproj` lets through), reference `src/AgentEyes.Core`, and run it against
   the rejected tree `b61553c` FIRST. If it does not fire there, the instrument is broken and
   nothing it says about this head means anything.
3. **The exit-code set is a decision, not an accident.** `{ 0, 255 }`, at
   `FfmpegCameraRecorder.QuitExitCodes`. Check that 255 still earns `clean-quit` / `yes` -
   AC17 depends on it - and that 1 does not.
4. **Mutate both CLI boundaries** (`Commands.cs:351` and `Commands.cs:511`), one at a time,
   and confirm each makes `Video_HandsAnAbandonedCameraToAStrandedOwnerAtBothOfItsFailureBoundaries`
   red on its own assertion. Gate the run on a clean build - `--no-build` after a failed
   compile reports on the previous binary.
5. **Whitespace.** `git diff --check origin/main...HEAD` over the whole head, not just
   `src` and `tests`.

No heavy smoke is indicated: this round changes only how an already-terminated process is
CLASSIFIED. It starts nothing, kills nothing new, and touches no audio, ffmpeg-invocation or
UI path. The AC3/AC9 start-ordering balance is not in this diff.

I believe this is finished.
