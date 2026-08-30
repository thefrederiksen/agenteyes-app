REJECT

# PR #48 / issue #46 - Review Gate round 1

## Basis of review

Reviewed PR #48 at head `ff4a2ec97356530f6eed3c2a418733857cef9246` against
`origin/main` at `36c98f13017b666491f89e8d3ab2d09b04ef9141`. The local head, remote branch,
and live PR head all named that same commit when this verdict was written. I read issue #46 and
both owner comments, including the AC6 decision and the amended AC4.

The live issue still carried `flow:ready-qa`, not `flow:ready-gate`, and the branch contained only
the developer handoff under `docs/cencon/proof/issue-46/`; there was no QA report. The target was
therefore not formally ready for this seat. The product defects below independently require
rejection even if that process state is corrected.

## Blocking defects

### 1. A short digital-silence interval makes a valid mixed recording permanently unfinalizable

`MicMeasure.ValueAfter` turns every infinite value into null
(`src/AgentEyes.Core/Audio/MicMeasure.cs:74-88`), and `Measure` treats either null statistic as a
failed measurement and throws (`src/AgentEyes.Core/Audio/MicMeasure.cs:37-49`). This conflates a
missing measurement with ffmpeg's valid representation of digital silence. It also contradicts
AC2's required outcome for an unusable measurement: disable the gate, rather than fail the whole
recording.

I generated a valid 48 kHz WAV containing 100 ms of digital silence followed by one second of tone,
then ran the exact `astats` arguments from `RunAstats`. ffmpeg completed successfully and positively
reported both requested statistics:

```text
INPUT_BYTES=105678
ASTATS_EXIT=0
Overall
RMS level dB: -21.487727
Noise floor dB: -inf
```

The branch's own targeted test confirms that `ParseOverall` returns null for this output:

```text
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

That passing test locks in the failure instead of protecting the product path. Mixed capture
defaults the gate on (`src/AgentEyes.Core/GateDefaults.cs:11-18`), and both mixed-audio and
mixed-video finalization call the newly calibrated chain
(`src/AgentEyes.Core/RecordingService.cs:949-958`). `AudioMix.Calibrate` then calls `Measure`
(`src/AgentEyes.Core/Audio/AudioMix.cs:56-71`) and throws before the final file is written. The
pending mux is cleared only after successful processing (`src/AgentEyes.Core/RecordingService.cs:967-983`),
and automatic mux retries stop after three attempts (`src/AgentEyes.Core/PostRecordingState.cs:62-68`).

Concrete failure scenario: record mic plus system audio while the microphone supplies 100 ms of
zero samples at startup, or leave the microphone muted while valid system audio is captured. The
mux fails, no final `audio.wav` or `recording.mp4` is produced, thumbnail and packaging stay blocked,
and the unchanged raw file makes all three retries fail identically. A correct result for this
measured input is a resolved no-gate decision, not a guessed threshold and not a failed recording.

### 2. AC5's required preset-editor disclosure is absent

AC5 explicitly requires the preset editor to state that the gate threshold is measured rather than
fixed. PR #48 changes no file under `src/AgentEyes.App`, and the actual checkbox still reads only
`Noise gate (mutes mic between phrases)` (`src/AgentEyes.App/PresetEditor.xaml:164-170`). Nothing in
the editor tells the person that the threshold will be derived from the capture.

Concrete failure scenario: open the Audio tab of the preset editor and inspect the gate setting.
The only visible gate copy is the unchanged checkbox text, so the acceptance criterion is not
observable in the product. The `GateDescription` unit test cannot cover this WPF surface.

### 3. The mixed-audio CLI never prints its chosen threshold

The other half of AC5 requires the recording console line to report the chosen threshold. The video
paths print `GateDescription()` after processing (`src/AgentEyes.Core/Commands.cs:424-440`), but the
audio mixed path prints only the pre-capture placeholder from `FxDesc` at line 161, performs the
measurement and mix at line 175, and finishes with generic file/manifest messages at lines 227-234.
There is no post-measurement `GateDescription()` call on this path.

Concrete failure scenario: run `agenteyes audio --mix ...` with the gate enabled. The console says
`gate (measured)` before capture, but after calibration it never reveals whether the gate was
disabled or which dBFS threshold was applied. This leaves the `MixWavs` entry point in scope with
exactly the invisible decision AC5 was written to remove.

## Checks completed

- `dotnet build AgentEyes.sln -c Release` exited 0 and reported `Build succeeded`, 4 analyzer
  warnings, and `0 Error(s)`.
- The issue-specific filtered run executed 23 tests and reported `Failed: 0, Passed: 23, Skipped: 0,
  Total: 23`.
- A full `dotnet test AgentEyes.sln -c Release` did not produce a green result in this review.
  `SetupEngineTests.SetBundleExtractBaseDir_CreatesTheDirectoryAndIsIdempotent` failed after 1 minute
  59 seconds while other worktrees were concurrently running AgentEyes testhosts. I stopped this
  review's run after the failure. I do not attribute that test failure to this diff, but it is not
  independent evidence for AC7 either.
- No product file, test file, commit, remote branch, GitHub issue, or pull request was changed during
  this review. The only workspace change is this verdict file for the orchestrator.
