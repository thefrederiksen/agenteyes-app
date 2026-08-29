REJECT

# PR #32 / issue #28 review gate  -  round 6

Reviewed commit `6d90ba07680bcfc9c86b984f5f3b2c5794f3dd62` from
`origin/issue-28-camera-failure-boundaries` in an isolated detached worktree. The round-7 QA commit
does not change `src/` or `tests/` from developer commit
`a0d1fe388ff034b2f3d1f950c03f749e783c3f93`; the product fix under review is
`6aef3ac`.

The two round-5 blockers are fixed on their named paths. They are not the reason for this verdict.
The rejection is for two other reachable paths through the same new state machine which violate
the stated definition and durability of `abandoned`.

## Blocking findings

### 1. A direct `Dispose()` earns `abandoned` after only one kill, without the required Dispose retry

`FfmpegCameraRecorder.Dispose()` calls `Stop()` once when `_terminated` is false, catches its
failure, and then unconditionally assigns `CameraStopKind.Abandoned` while the process remains live
([`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1248`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs),
[`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1254`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs),
[`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1267`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs)).
That is valid only when a failed `Stop()` happened *before* entry to `Dispose()`, making the stop
inside `Dispose()` the retry. The method does not establish that precondition.

This is a production path, not merely an unusual public-method sequence. The CLI failure boundary
calls `cameraRec?.Dispose()` directly from its `finally` when work after camera open throws
([`src/AgentEyes.Core/Commands.cs:494`](../../../src/AgentEyes.Core/Commands.cs),
[`src/AgentEyes.Core/Commands.cs:499`](../../../src/AgentEyes.Core/Commands.cs)). If no earlier
explicit camera stop ran, that Dispose performs the first quit and first kill. A process which
survives them has not survived the separately promised Dispose retry, yet the stop kind says it
has.

Independent scenario `DIRECT_DISPOSE_ONE_KILL`, over the reviewed assembly:

```text
quits=1 kills=1 exited=False kind=abandoned complete=unknown abandoned=True
```

The positive count is the defect: `abandoned` is present after one kill, while the contract and
the source comment define it as surviving the quit, kill, **and retry**. The assignment needs proof
that a refused termination attempt already existed on entry, or `Dispose()` needs to perform the
actual retry before it records the three-clause observation. It must also be covered by a direct
Dispose control so a normal Stop-then-Dispose-only test cannot certify the missing path.

### 2. An earned `abandoned` is later overwritten with `clean-quit` / `yes`

The new comment says an earned abandoned observation is "never retracted," but subsequent stop
attempts can replace `_stopKind` without guarding that state. A later quit returning 0 or 255 writes
`CleanQuit` at
[`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1030`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs)
and [`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1034`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs);
a later successful kill similarly writes `ForceKilled` at
[`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1160`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs)
and [`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1164`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs).
Neither path preserves an already-earned `Abandoned`.

This is reachable through normal recovery. `StrandedCameraOwner.Recover()` calls `Dispose()` again
on a retained, still-live recorder
([`src/AgentEyes.Core/StrandedCameraOwner.cs:168`](../../../src/AgentEyes.Core/StrandedCameraOwner.cs),
[`src/AgentEyes.Core/StrandedCameraOwner.cs:179`](../../../src/AgentEyes.Core/StrandedCameraOwner.cs)).
The committed test covers a process which dies before that sweep, so `Stop()` never sends anything;
it does not cover a still-live process which finally accepts the recovery quit or kill.

Independent scenario `RECOVERY_AFTER_ABANDONED`, over the reviewed assembly:

```text
earned=abandoned quits=3 kills=2 exited=True kind=clean-quit complete=yes abandoned=False
```

The live-status flag is right to become false once the process ends. The historical stop kind is
not right to change: the process did survive the original quit, kill, and Dispose retry, and the
durable recording observation cannot depend on whether a later owner sweep succeeds. Relabelling
it also reopens the completeness one-way door and produces `yes` after the same recorder previously
established `abandoned` / `unknown`.

Preserve `Abandoned` across every later termination outcome, and add positive controls for both a
later recovery quit and a later recovery kill. The present test at
[`tests/AgentEyes.Tests/CameraFailurePathTests.cs:1400`](../../../tests/AgentEyes.Tests/CameraFailurePathTests.cs)
only proves preservation when the process is already dead before the later Dispose.

## Round-5 blocker retest and positive controls

I ran an independent executable probe first against rejected commit `b61553c` and then against the
reviewed head. It drives `FfmpegCameraRecorder` through `ICameraProcess`; it does not reuse the
branch's test assertions.

| Scenario | `b61553c` | Reviewed head | Result |
|---|---|---|---|
| `DELIVERED_QUIT_THEN_EXIT_1` | `clean-quit / yes` | stop kind absent / `unknown` | Round-5 blocker fixed |
| `DIED_BEFORE_DISPOSE_RETRY` | `abandoned / unknown` | stop kind absent / `unknown` | Round-5 blocker fixed |
| healthy quit, exit 255, two advances, complete stderr | `clean-quit / yes` | `clean-quit / yes` | AC17 positive control preserved |
| exit 255, two advances, incomplete stderr | `clean-quit / unknown` | `clean-quit / unknown` | 255 does not directly earn completeness |
| survives explicit Stop and Dispose retry | `abandoned / unknown` | `abandoned / unknown` | three-clause control preserved |

The accepted exit-code set is explicitly enumerated as `{ 0, 255 }` at
[`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:221`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs).
`0` is the ordinary successful exit and `255` is the branch's observed ffmpeg interactive-quit
exit. Every tested code outside the set remains unanticipated instead of being ranged into a clean
claim. The independent exit-255 controls also establish that the exit code earns only the stop kind:
complete stderr, output presence, continued advancement, and freshness still independently gate
`yes`.

I also inspected the round-7 mutation record. It contains firing mutations for restoring the
non-negative range, removing 255, admitting 1, both sides of the provisional/final abandoned
assignment, every CLI failure-boundary target individually, and the earlier round-4 regressions.
Those checks support the paths they exercise, but neither of the blockers above is represented in
that matrix.

## Verification

- `git diff --check origin/main...HEAD`: clean. The earlier proof-artifact CRLF failure is corrected.
- Exact-head Release build: succeeded after a required restore, with 0 errors and 4 existing analyzer
  warnings.
- Full exact-head suite: 942 passed, 0 failed, 0 skipped.
- Independently enumerated camera/failure-boundary suite: 121 tests discovered and the same 121
  passed, 0 failed, 0 skipped. This includes the named round-5 cases, AC17 controls, the earlier
  completeness/liveness defects, manifest wiring, ownership, CLI guards, and stranded-camera paths.
- Throwaway integration from `origin/issue-36-circular-camera-overlay` at `75e62ad`, merging
  `origin/issue-28-camera-failure-boundaries` with `--no-commit --no-ff`: merge completed without
  conflicts; Release build succeeded; 1,154 passed, 0 failed, 0 skipped.

The initially attempted no-restore build in the fresh worktree was a broken instrument because five
projects had no assets files; I restored and reran it successfully. Likewise, an initial targeted
discovery omitted the solution's x64 platform and found no test assembly; the x64 rerun positively
discovered 121 tests before executing them. Neither empty result is counted as evidence.
