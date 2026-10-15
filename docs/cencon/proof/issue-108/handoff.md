# Issue 108 handoff - a stale screenshot, and an aggregate mistaken for system audio

Both defects were found by reading the merged code from #104, not by a failure in use. Both are
the same family: a check whose pass condition does not prove the thing it claims.

## Defect 1 - a stale screenshot could be reported as a fresh capture

`Screenshot.CaptureRect` proves a capture happened by passing `File.Exists(outputPath)` into
`Failure(...)`, because `screencapture` can exit ZERO and write nothing when the Screen Recording
permission is refused. Nothing removed the target first, so a file left by an EARLIER run also
satisfied that check.

Fix: a new `ClearStaleTarget(outputPath)` runs before the capture. After it returns, the file's
presence means exactly one thing - this run wrote it. If the old file cannot be removed the
command stops with a clear message rather than continuing to a check it has already invalidated.

No try-catch was added: the coding standards put try-catch at entry points only, so the delete is
allowed to throw and the post-delete existence check converts the one case worth explaining.

## Defect 2 - "Aggregate" was treated as a loopback driver

`MacSystemAudio.KnownDrivers` listed `Aggregate`. An Aggregate Device is a generic macOS facility
for combining audio interfaces; it is not a loopback driver. A user's aggregate of two
MICROPHONES would have been selected as the system-audio source, recording the room while
claiming to record the machine - the exact substitution that class's own comment promises never
to make.

Fix: `Aggregate` removed, with a comment recording why it must not come back. An aggregate that
genuinely wraps a loopback can still be chosen explicitly with `--mic`, by its own name.

## Acceptance criteria

| AC | Criterion | Evidence |
|---|---|---|
| 1 | The target is removed before capture | `ClearStaleTarget` called from `CaptureRect` before `ToolRunner.Run` |
| 2 | A regression test covers the removal | `MacScreenshotStaleTargetTests`, 3 tests |
| 3 | An unremovable stale file fails clearly | post-delete existence check throws `UsageException` naming the path and `--out` |
| 4 | `Aggregate` is gone from `KnownDrivers` | removed, with a comment explaining why |
| 5 | A regression test rejects an aggregate device | `Find_AnAggregateDeviceThatIsNotALoopback_IsNotChosen` |
| 6 | Real loopbacks are still found | existing BlackHole tests pass, plus `Find_AnAggregateWrappingBlackHole_StillFindsTheLoopback` |
| 7 | Both configurations still clean and green | below |

    macOS build   -> Build succeeded, 0 Error(s)
    macOS tests   -> Failed: 0, Passed: 60   (was 55; the five new tests are counted)
    Linux build   -> Build succeeded, 0 Error(s)
    Linux tests   -> Failed: 0, Passed: 47, Skipped: 1   (unchanged)

## The regression test was proved able to fail

A regression test that passes against the old code is decoration. The defect was put back and the
test re-run:

    with "Aggregate" restored -> Failed: 1, Passed: 0
    with the fix in place     -> Failed: 0, Passed: 1

## What is NOT proven

* **The end-to-end path for defect 1 is verified by reading, not by test.** The three new tests
  exercise `ClearStaleTarget` directly and prove it removes the target. That `CaptureRect` calls
  it before running the tool is plain in the source but is not asserted by a test, because doing
  so means actually invoking `screencapture` - which would make the suite depend on a permission
  the hosted runner cannot be granted, and would stop these tests being pure logic. The weaker
  coverage is deliberate; the gap is real and is stated rather than glossed.
* **Neither defect was ever observed in use**, and neither fix has been exercised against a real
  capture. No frame of video has been captured on the verifying machine at any point, because the
  Screen Recording permission is not granted there. Everything above is logic.
* Defect 1 was reachable mainly through a repeated `--out` to a fixed path; recordings use
  timestamped directories. Its severity is judged low and that judgement is not measured.
