REJECT

# PR #48 / issue #46 - Review Gate round 2

## Basis of review

Reviewed live PR #48 at head `fe128099e86d5370dd9928bd4283abadb48163c9`
against base `36c98f13017b666491f89e8d3ab2d09b04ef9141`. The local head, remote PR
head, and issue branch all named that same commit when this verdict was written. The issue was
open with `flow:ready-gate`; the PR was open, mergeable, and reported a clean merge state.

I read issue #46, its three owner comments, the complete diff and surrounding product code, all
404 lines of `GateCalibrationTests.cs`, the developer handoff, and the round-1 verdict. The three
round-1 product findings are repaired at the current head: digital silence no longer strands the
recording, the preset editor discloses measurement, and all three CLI processing branches print
the resolved gate decision. The defect below is in the way the first repair interprets a valid
measurement.

## Blocking defects

### 1. A 100 ms startup zero disables a useful gate for the entire noisy take

`MicMeasure.ValueAfter` correctly preserves ffmpeg's `-inf` value
(`src/AgentEyes.Core/Audio/MicMeasure.cs:91-100`), but `GateCalibration.ThresholdDb` turns ANY
infinite floor or RMS into a no-gate result (`src/AgentEyes.Core/Audio/GateCalibration.cs:94-100`).
That conflates two different signals:

- an entirely silent track, whose RMS is also `-inf` and which should not be gated; and
- a finite, noisy recording containing one short interval of digital zero, whose overall RMS is
  valid and whose non-zero portion can still have ample room for a useful gate.

I generated one mono WAV consisting of one second of low-level white noise followed by one second
of tone, then a second WAV by prepending exactly 100 ms of zero samples to that same file. I ran
the exact `astats=metadata=1:reset=0` measurement used by `MicMeasure`, with the installed bundled
ffmpeg 9.0.1. Both generation commands and both scans exited 0, both files were non-empty, and both
scans contained an `Overall` section with both required values:

```text
FILE=noisy.wav BYTES=192078 ASTATS_EXIT=0 OVERALL_PRESENT=True
RMS=-12.042588 NOISE_FLOOR=-68.029867

FILE=noisy-with-startup-silence.wav BYTES=201678 ASTATS_EXIT=0 OVERALL_PRESENT=True
RMS=-12.254481 NOISE_FLOOR=-inf

GENERATION_EXITS=0,0
```

Without the startup zeros, the current rule selects `-58.029867 dBFS`: 10 dB above the measured
floor and more than 12 dB below RMS (`GateCalibration.cs:102-107`). Prepending zeros leaves RMS
within 0.22 dB but makes the current code return null, so `AudioMix.Calibrate` records a resolved
no-gate decision (`AudioMix.cs:67-71`) and `FfmpegArgs.MicChain` omits `agate` for the entire take
(`FfmpegArgs.cs:365-374`). The targeted test at
`tests/AgentEyes.Tests/GateCalibrationTests.cs:94-103` executed and passed while asserting exactly
that no-gate result. It therefore locks in this failure rather than guarding the noisy-input case.

The user-visible explanation is false as well. Every null threshold is rendered as
`gate off (measured: no room between the noise floor and the voice)`
(`src/AgentEyes.Core/AudioMixOptions.cs:58-60`), even though a finite RMS paired with a `-inf`
minimum has more measured span, not less.

Concrete failure scenario: a slow-starting or briefly muted microphone emits 100 ms of zeros and
then records a noisy mixed/system-audio take. The gate defaults on specifically to tame speaker
bleed, but one startup artifact disables it for the remaining recording. AC2(b)'s useful-gate
behavior is therefore lost even though the actual noisy signal after startup is unchanged.

The repair needs to distinguish a wholly silent track from a finite-RMS take with zero windows and
derive a usable noise estimate from the latter rather than treating its minimum as the whole
answer. The regression must drive real media through measurement and calibration for both paired
inputs. Expected: both finite-RMS noisy inputs yield a threshold satisfying both AC2 margins. Bad:
the zero-prefixed input silently becomes no-gate. Empty/missing statistics or a failed ffmpeg scan:
broken measurement, never a pass.

### 2. The required independent QA handoff did not happen, and its only handoff is stale

The issue's own `flow:ready-gate` comment states that the same seat wrote the code and the
verification and that no separate QA session ran. The branch-derived inventory of
`docs/cencon/proof/issue-46/` contains one artifact only:

```text
handoff.md  6463 bytes
```

There is no QA report. This is not an optional proof tier: the method requires QA to be a separate
identity (`docs/cencon/DEVELOPMENT_METHOD.md:78-86`), independently review and run the checks, and
commit a QA report before handing the issue to this gate (`DEVELOPMENT_METHOD.md:291-318`). The
Review Gate cannot substitute for that seat because the structural separation is the control being
required.

The developer handoff is also stale after the round-1 repair. It says `MicMeasure` throws when a
figure is non-finite (`docs/cencon/proof/issue-46/handoff.md:17-19`); the current implementation
instead accepts infinities and resolves them to no-gate. A QA reviewer following the committed
handoff is therefore told the opposite of the current behavior at exactly the branch that contains
blocking defect 1.

Concrete failure scenario: approving this PR would let a developer-produced report stand in for
the independent running-app and criterion review, while the sole testing instruction misdescribes
the changed edge case. The noisy-startup defect above is the result that independent QA was meant
to catch before this seat.

## Checks completed

- `dotnet build AgentEyes.sln -c Release` exited 0 and printed `Build succeeded`, 4 analyzer
  warnings, and `0 Error(s)`.
- The derived issue-test inventory contained 28 `GateCalibrationTests`; the run exited 0 and
  printed `Failed: 0, Passed: 28, Skipped: 0, Total: 28`.
- The derived `FfmpegArgsTests` inventory contained 47 tests; the run exited 0 and printed
  `Failed: 0, Passed: 47, Skipped: 0, Total: 47`.
- The exact test that asserts a finite-RMS / `-inf`-floor input is no-gate ran alone and printed
  `Total tests: 1`, `Passed: 1`. That is positive confirmation of the current bad decision, not a
  safety pass.
- A full `dotnet test AgentEyes.sln -c Release --no-build` produced no completion or test summary
  for more than three minutes and was interrupted. At the time, another AgentEyes worktree was
  running the shared-user-environment
  `SetupEngineTests.SetBundleExtractBaseDir_CreatesTheDirectoryAndIsIdempotent` test. This review
  does not claim a full green suite and does not attribute the contention to this diff.
- A derived inventory covered all 12 changed files and printed `NON_ASCII_BYTES=0` for each. A
  known-bad UTF-8 infinity-symbol control printed `NON_ASCII_BYTES=3`, confirming the instrument
  fired as well as passed.
- The reference recording and its original capture exist locally, and the product log contains the
  reported finite reference measurement (`noise floor -87.9 dBFS, RMS -38.1 dBFS -> gate at -77.9
  dBFS`). I did not treat the developer's runtime result as independent QA proof.
- I did not run the WPF GUI, make a new real recording, or claim independent runtime coverage of
  AC4/AC5. No product file, test file, commit, remote branch, GitHub issue, or pull request was
  changed during this review. The only workspace change is this verdict file for the orchestrator.

---

NOTE ON THIS FILE'S PROVENANCE: the gate session wrote this verdict into its own worktree, which
was then removed before the file had been copied into the branch. The text above is that verdict
restored verbatim from the orchestrator's read of it; nothing has been edited, softened or omitted.
