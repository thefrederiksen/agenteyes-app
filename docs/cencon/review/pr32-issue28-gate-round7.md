REJECT

# PR #32 / issue #28 review gate  -  round 7

Reviewed commit `c1d037efbca0d0eb2198eee14001ddfca5762921` from
`origin/issue-28-camera-failure-boundaries` in an isolated detached worktree. The QA head changes
no product or test file from `f11abdd`; the round-8 product redesign is `aa92426`.

The explicit termination history fixes both round-6 blockers and makes an earned `abandoned`
monotone. It does not yet achieve the broader design property that a derived stop kind can use only
the observations which earned that outcome. One cross-round sequence still combines facts from
different termination rounds into `clean-quit` / `yes`.

## Blocking finding

### Quit delivery is global, so a refused old round can certify an exit in a later undelivered round

`CameraTerminationRecord` keeps `_quitsDelivered` as one lifetime counter
([`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:125`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs)).
`QuitDelivered()` increments it without latching delivery to the current round's eventual outcome
([`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:194`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs),
[`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:197`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs)).
`ExitObservedAfterTermination()` later records the newest exit and exit code, again without recording
whether the quit in *that* round was delivered
([`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:235`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs),
[`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:239`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs)).
The derivation then accepts the unrelated pair "some quit was delivered in this recorder's life" +
"the latest observed exit is 0 or 255" at
[`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:285`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs).

Concrete production-order scenario, driven independently through `Stop()` followed by the normal
`Dispose()` retry:

1. Round 1 successfully delivers `q`; ffmpeg does not exit within the quit wait, then survives the
   kill and its wait. The round is recorded refused. Stop kind is absent and completeness is
   `unknown`.
2. `Dispose()` opens the retry round. Its `SendQuit()` write fails while the process exits 0 - the
   same pipe/exit race which the round-4 fix handles. The recorder correctly skips
   `QuitDelivered()` at
   [`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:949`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs)
   through [`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:959`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs),
   observes the exit after the wait, and reports it at
   [`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:962`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs)
   and [`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:999`](../../../src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs).
3. Only one round was refused, so `abandoned` is not earned. The derivation instead reuses round 1's
   stale delivery count with round 2's exit 0 and returns `clean-quit`. With two recent progress
   advances and complete stderr, that reopens completeness to `yes`.

Independent exact-head probe output:

```text
FAIL PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0 quits=2 kills=1 exited=True kind=clean-quit complete=yes abandoned=False
```

The adjacent controls isolate the defect rather than merely rejecting all recovery success:

```text
PASS PRIOR_UNDELIVERED_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0 quits=2 kills=1 exited=True kind=absent complete=unknown abandoned=False
PASS PRIOR_REFUSAL_THEN_DISPOSE_RETRY_QUIT_DELIVERED_EXIT_0 quits=2 kills=1 exited=True kind=clean-quit complete=yes abandoned=False
```

The only changed fact is whether delivery belongs to the exit-producing round. An earlier round
which ended with the process still alive after the quit timeout and kill wait is not positive proof
that a later exit during a failed pipe write answered that old quit. The process may have ended for
an unwatched reason; under the amended contract that ambiguity is an absent stop kind / `unknown`,
not the friendliest claim.

This is exactly the design property under review: the call sites report the right observations, but
the single derivation joins two observations which did not earn one outcome together. Associate
quit delivery with the termination round settled by `ExitObservedAfterTermination` (or latch that
the observed exit followed a delivered quit in its own round), and pin all three rows above. The
current fresh-undelivered test at
[`tests/AgentEyes.Tests/CameraFailurePathTests.cs:1574`](../../../tests/AgentEyes.Tests/CameraFailurePathTests.cs)
starts with zero historical deliveries and therefore cannot see stale evidence from a refused
prior round.

## What the redesign does establish

- No production call site assigns a `CameraStopKind`; `FfmpegCameraRecorder.StopKind` delegates to
  the one derivation in `CameraTerminationRecord`.
- `abandoned` is derived first from a counter which only rises. I found no public sequence which
  replaced an earned `abandoned`, and no path which reopened its `unknown` completeness.
- Direct Dispose now performs the missing retry: the independent probe observed two quits and two
  kills before `abandoned`. When the retry kill landed, it observed `force-killed` / `no` instead.
- A later recovery quit after genuinely earned `abandoned` left the durable kind `abandoned`, kept
  completeness `unknown`, and changed only the live flag to false.
- The round-5 cases remain fixed: delivered quit + exit 1 produced an absent kind / `unknown`, and a
  process dying before the Dispose retry did the same.
- The accepted exit set remains explicitly enumerated as `{ 0, 255 }` at
  [`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:106`](../../../src/AgentEyes.Core/Video/CameraTerminationRecord.cs).
  Exit 255 with incomplete stderr earned only `clean-quit`; completeness remained `unknown`.
- The genuine positive control - one refused round followed by a retry quit which really is delivered
  and exits 0 - still produced `clean-quit` / `yes`.

The developer and QA mutation evidence was read as context, not accepted as the verdict. It shows
that the advertised mutations fired and that the fresh undelivered-exit-0 case protects the
delivery clause. None of those mutations/tests exercises delivery in one refused round followed by
an undelivered accepted-code exit in the next, so the green matrix does not cover this cross-round
join.

## Verification

- `git diff --check origin/main...HEAD`: clean across the full 60-file PR head.
- Exact-head restore: completed successfully before the build.
- Exact-head Release build: succeeded with 0 errors and 4 existing analyzer warnings.
- Full exact-head suite: 947 passed, 0 failed, 0 skipped.
- Independently enumerated camera/failure-boundary suite: 126 tests discovered, then 126 passed,
  0 failed, 0 skipped. The explicit inventory includes the five new round-8 tests.
- Independent 10-scenario executable probe: the rejected `6d90ba0` tree fired four checks, including
  both round-6 blockers; the reviewed head fixed those two and failed only the cross-round delivery
  scenario above. The two controls beside it passed.
- Throwaway integration from `origin/issue-36-circular-camera-overlay` at `75e62ad`, merging this
  head with `--no-commit --no-ff`: no conflicts; restore completed; Release build succeeded;
  1,159 passed, 0 failed, 0 skipped; the staged merge diff check was clean. The independent probe
  reproduced the same cross-round blocker in the merged tree.

## Non-blocking follow-up

The branch's own suite still does not directly pin `CameraTerminationRecord.QuitDelivered()`'s
precondition; QA's uncommitted probe is the only test which made its QA-M10 mutation fire. Commit a
small record-level guard test so the structural promise remains executable after the QA artifact is
gone. This is a coverage follow-up, not the reason for rejection.
