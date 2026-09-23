REJECT

# PR #32 / issue #28 review gate - round 8

Reviewed branch head: `99e3fcd1a621db20a5027e8e7c5e01b648942bd8`

Round-9 product fix: `84221fda4e52769170927f6d3cf62b5ebcb94e4a`

Known-bad comparison revision: `c1d037e`

Stack target: `origin/issue-36-circular-camera-overlay` at `75e62adbb1ddd8ddcf6e8075fc3298442a242ddb`

## Verdict

The surgical runtime fix is correct under the requested attacks, and the issue-36 stack merge is conflict-free and green. The branch head is nevertheless not approvable because the explicitly required full `git diff --check` fails on two committed round-9 proof artifacts.

### Blocking finding: full branch diff check is red

`git diff --check origin/main...HEAD` reports 611 trailing-whitespace errors:

- 578 in `docs/cencon/proof/issue-28/qa-round9-probe.cs.txt`
- 33 in `docs/cencon/proof/issue-28/qa-round9-probe-results.txt`

The same defect is present in the staged throwaway stack merge (`git diff --check --cached`). The product-fix delta alone, `c1d037e..84221fd`, passes `git diff --check`, so this is isolated to the later QA evidence commit rather than the implementation or its committed tests.

Required correction: normalize those two committed artifacts so the full branch and stack-merge diff checks produce zero diagnostics, then rerun the gate. No product-code change is indicated by this review.

## Round-7 blocker reproduction and fix

I compiled one independent x64 probe outside the branch and ran the same assertions first against known-bad `c1d037e`, then against the fixed branch artifact.

Known-bad presence check:

| Row | Observed at `c1d037e` |
| --- | --- |
| `PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | `clean-quit / yes` (failed the required `absent / unknown` assertion) |
| analogous prior-delivery / undelivered retry / exit 255 | `clean-quit / yes` (failed the required `absent / unknown` assertion) |

This reproduced the lifetime-delivery cross-round join before the fixed result was trusted.

Fixed branch results:

| Required row | Observed |
| --- | --- |
| `PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | `quits=2`, `kills=1`, exited 0, stop kind absent, completeness `unknown` |
| `PRIOR_UNDELIVERED_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | `quits=2`, `kills=1`, exited 0, stop kind absent, completeness `unknown` |
| `PRIOR_REFUSAL_THEN_DISPOSE_RETRY_QUIT_DELIVERED_EXIT_0` | `quits=2`, `kills=1`, exited 0, `clean-quit / yes` |

Additional fixed-branch controls:

- prior delivered quit plus an undelivered retry exit 255: absent / `unknown`
- prior undelivered quit plus the retry's own delivered quit and exit 255: `clean-quit / yes`
- current-round delivered quit plus exit 1: absent / `unknown`
- direct `Dispose`, first kill refused and retry kill confirmed: `force-killed / no`
- direct `Dispose`, two refused rounds: `abandoned / unknown`
- repeated `Dispose` while live: remained `abandoned / unknown`
- later recovery quit after abandonment: historical kind remained `abandoned / unknown`, while live `IsAbandoned` became false
- failed `Stop`, then unwatched process death, then `Dispose`: absent / `unknown`

All five independent probe tests passed on the fixed artifact.

## Derivation and cross-round audit

The change replaces the lifetime join with two correlated facts:

- `BeginRound()` resets `_roundQuitDelivered`.
- `QuitDelivered()` records delivery in the current round only and still requires a current-round quit attempt.
- `ExitObservedAfterTermination()` snapshots that round's delivery into `_exitFollowedADeliveredQuit` beside the observed exit code.
- `StopKind` requires the exit observation, that paired delivery snapshot, and membership in the enumerated `{ 0, 255 }` exit-code set.

I traced every production caller of `BeginRound`, quit/kill observations, exit observation, and process-gone observation. I found no remaining unsafe cross-round join:

- refused-round count is deliberately lifetime and monotone because it is the historical definition of `abandoned`;
- gone-untouched and kill-confirmed are terminal observations on production paths;
- exit code and delivered-quit evidence now update as one same-round pair;
- session/output/stderr facts used by completeness are recording evidence rather than termination-round evidence, and `yes` remains behind the existing clean-quit, complete-stderr, opened-output, and advancing-at-stop clauses.

The compiled-artifact probe inspected 248 core types and found:

- zero fields storing a `CameraStopKind` outside the enum vocabulary;
- zero writable `CameraStopKind` properties;
- exactly two methods accepting the type, both the expected `CameraObservation.Text(kind)` renderers;
- the accepted quit exit-code set exactly `{ 0, 255 }`;
- `QuitDelivered()` throws both without a current-round attempt and after a new round has invalidated the prior round's delivery.

The committed round-9 tests also include the previously requested direct guard test, so that earlier non-blocking coverage follow-up is closed.

## Regression verification

The implementation delta from `c1d037e` to `84221fd` changes only:

- `src/AgentEyes.Core/Video/CameraTerminationRecord.cs`
- `tests/AgentEyes.Tests/CameraFailurePathTests.cs`

The QA commit from `84221fd` to branch head changes proof documents only; product and test sources are identical to the developer commit.

Independent verification after restore:

- Release build: succeeded, 0 errors; four pre-existing xUnit analyzer warnings.
- Positive x64 targeted discovery: exactly 130 tests across `CameraFailurePathTests`, `CameraTrackTests`, `CaptureClaimOwnershipTests`, `ManifestWriterIlTests`, `SessionManifestTests`, and `StrandedCameraOwnerTests`.
- Targeted run: 130 passed, 0 failed, 0 skipped.
- Full branch suite: 951 passed, 0 failed, 0 skipped.
- Prior round-6 direct-dispose and monotone-abandoned blockers: passed in both committed tests and independent unusual-sequence probes.
- Round-5 exit-code and observation-boundary behavior: exact `{0,255}` clean-quit set retained; other exit codes remain absent / `unknown`.
- AC17 positive controls: current-round exits 0 and 255 still reach `clean-quit / yes`; incomplete stderr remains `unknown` in the committed suite.
- AC1/AC3/AC4 and AC3/AC9 ownership/balance surfaces are outside the two-file product delta and remain covered by the green full and targeted suites.

## Issue-36 stack merge

Throwaway merge command: `git merge --no-commit --no-ff origin/issue-28-camera-failure-boundaries` from `origin/issue-36-circular-camera-overlay`.

- merge completed automatically;
- zero unmerged index entries;
- 38 staged merged files positively inventoried;
- Release build succeeded with 0 errors and the same four warnings;
- full stack suite: 1,163 passed, 0 failed, 0 skipped;
- staged full diff check: failed on the same 611 whitespace errors in the two round-9 proof artifacts.

No product files, commits, remote branches, permanent merges, or GitHub state were changed during this review.
