# Issue #46 handoff - measured gate threshold replaces the fixed -34 dBFS constant

## What was implemented

The microphone noise gate's threshold is now derived from a measurement of the capture that is
about to be processed, instead of the hardcoded `GateThreshold = 0.02` (-34 dBFS) that was applied
to every microphone regardless of level.

New code:

- `src/AgentEyes.Core/Audio/GateCalibration.cs` - the pure decision. `MicLevels` (measured noise
  floor and RMS, both dBFS) in, a threshold out, or **null meaning "do not gate this take"**. The
  rule has two edges the threshold must clear at once: at least `FloorMarginDb` (10 dB) ABOVE the
  noise floor, and at least `SpeechHeadroomDb` (12 dB) BELOW the measured level. When the span
  between floor and voice is smaller than the two margins together there is no honest threshold, so
  it returns null rather than inventing one - an assumed threshold is the defect being fixed.
- `src/AgentEyes.Core/Audio/MicMeasure.cs` - one ffmpeg `astats` pass over the captured file,
  parsing the `Overall` summary only (never a per-channel figure). It throws ONLY when the figures
  are ABSENT - a measurement that did not happen. It does NOT throw for `-inf`, which is ffmpeg's
  valid report of digital silence.

  **A `-inf` floor beside a FINITE RMS means the take CONTAINS silence, not that it IS silent.**
  A slow-starting or briefly muted microphone emits a few hundred milliseconds of zeros and then
  records normally. Those zeros are not noise, so the floor is re-measured with them removed
  (`ZeroTrimFilter`) and the ordinary rule then applies. The RMS is kept from the FULL take - it is
  the level proxy for the whole recording, and trimming would only flatter it.

Changed:

- `src/AgentEyes.Core/AudioMixOptions.cs` - `GateThreshold` (a constant) is replaced by
  `GateThresholdLinear` (measured, nullable) plus `GateCalibrated` (has a measurement been taken).
  Two nulls that mean different things are now distinguishable, which is what lets the chain builder
  refuse to guess. Adds `GateApplies` and `GateDescription()`.
- `src/AgentEyes.Core/Video/FfmpegArgs.cs` (`MicChain`) - emits `agate` only when the person asked
  for a gate AND a measurement produced a threshold. Gate on with no measurement throws a
  `UsageException`, mirroring the existing missing-RNNoise-model guard.
- `src/AgentEyes.Core/Audio/AudioMix.cs` - `Prepare` now also calibrates, per entry point, against
  that entry point's own mic source (`MixWavs` -> the mic WAV; `MuxVideoMixed` and
  `ProcessVideoMic` -> the raw MP4). It **always re-measures** rather than trusting a value already
  on the options, because these options are serialized into the manifest for a deferred mux and a
  stale threshold from an earlier attempt must never be applied to a different capture.
- `src/AgentEyes.Core/Commands.cs` - `FxDesc` announces the gate as measured; the chosen threshold
  is printed after processing.

Not changed, deliberately: `arnndn` and `speechnorm`, the chain order, and `GateDefaults.For`
(decided on the issue - saved presets are user data and are not rewritten).

## How QA should test each criterion

**AC1 / AC2 (the decision is a pure function, with the three required cases)** -
`dotnet test AgentEyes.sln -c Release --filter "FullyQualifiedName~GateCalibrationTests"`.
`ThresholdDb_quiet_clean_mic_stays_well_below_the_voice` is AC2(a),
`ThresholdDb_noisy_mic_clears_the_floor_and_the_voice` is AC2(b), and
`ThresholdDb_when_floor_and_voice_are_too_close_disables_the_gate` is AC2(c). The boundary is
pinned from both sides by `ThresholdDb_at_exactly_the_usable_span_still_gates` and
`ThresholdDb_just_under_the_usable_span_does_not_gate`.

**AC3 (the real file is measured, and it is logged)** - reprocessing the reference capture through
`AudioMix.ProcessVideoMic` logs, and reports through `GateDescription()`:

```
GATE DECISION: gate at -77.9 dBFS (measured)
THRESHOLD LINEAR: 0.00012751381814010864
```

Measured from the capture: noise floor -87.9 dBFS, RMS -38.1 dBFS.

**AC4 (the effect on the real recording)** - driving the product path
(`AudioMix.ProcessVideoMic`) over
`%USERPROFILE%\Videos\AgentEyes\2026-08-30_172406_video\recording.original.mp4`, then
`silencedetect=noise=-50dB:d=0.15`:

| | silent gaps | total silence | peak | RMS |
|---|---|---|---|---|
| raw capture (baseline) | 58 | 29.8s | -13.8 dB | -38.1 dB |
| old chain (fixed -34 dBFS) | 82 | 41.3s | -2.4 dB | -27.2 dB |
| **new chain (measured -77.9 dBFS)** | **50** | **25.1s** | -2.3 dB | -26.6 dB |

**AC4's wording was wrong and was amended on the issue - read this before judging it.** It asked
for the result to land "within 1.5s of the raw 29.8s". The result is 25.1s, which is 4.7s OUTSIDE
that band, on the good side: the new chain has LESS silence than the untouched capture, because
`speechnorm` now lifts quiet passages above the -50 dB detection floor instead of lifting the
wreckage the gate left behind. The band was the wrong shape for the intent; the intent was "must
not ADD silence". The amended criterion is `processed silence <= raw silence`, and the loudness
columns are there so the improvement cannot be a silent track passing on a technicality.

**AC5 (visible, not invisible)** - `FxDesc` renders the gate as `gate (measured)` before a capture
exists and as the resolved decision afterwards; `Commands.Video` prints `GateDescription()` after
processing. `GateDescription_tells_the_three_states_apart` covers all three states.

**AC6 (the default was decided, not silently changed)** - `GateDefaults.For` is untouched;
the decision is recorded on the issue.

**AC7 (gate)** - `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Error(s).
`dotnet test AgentEyes.sln -c Release`: Passed, 1340 tests, 0 failed.

## Two things the reviewer should look at

1. **An existing manifest with a deferred mux loses its `GateThreshold` field.** The round-trip
   fixture caught this. Losing it is the correct outcome - the stale value must not be honored, and
   the resumed mux re-measures - but it is a real instance of open issue #11 (nested unknown
   manifest fields are dropped), because `GateThreshold` is now an unknown property and the
   preservation guarantee does not reach inside `PendingMux/Options`.
   `A_legacy_pending_mux_does_not_carry_its_stale_threshold_forward` pins the upgrade behaviour: the
   person's choice to gate survives, the stale number does not, and the resumed mux cannot build a
   chain until it measures. The fixture `pending-mux.json` was updated to the current shape.
2. **`MicLevels.RmsDb` is the whole track's RMS, not the RMS of the speech alone.** It includes the
   pauses, so it reads lower than the speech does, which pushes the threshold DOWN and makes the
   gate gentler. That error direction is deliberate and documented on the type - the defect being
   fixed was a threshold that was too HIGH - but it is an approximation and a reviewer should agree
   with it rather than discover it.


---

## Review Gate rounds - what changed after each

**Round 1 (REJECT)** - `docs/cencon/review/pr48-issue46-gate-round1.md`. Three defects:
a `-inf` noise floor was read as a failed measurement and threw, which made a mixed recording
permanently unfinalizable (the mux threw, the pending mux never cleared, all three retries failed
identically); AC5's preset-editor disclosure was absent; and the mixed-audio CLI never printed the
resolved threshold. All three fixed in `623ca5b`. A follow-up fixed a Unicode INFINITY symbol
reaching the log, which this repo forbids everywhere.

**Round 2 (REJECT)** - `docs/cencon/review/pr48-issue46-gate-round2.md`. The round-1 repair had
OVER-CORRECTED: any infinity became "do not gate", so 100 ms of startup zeros disabled a genuinely
useful gate for the rest of a noisy take, and the explanation shown to the person
("no room between the noise floor and the voice") was false for that case. Fixed by re-measuring
the floor with digital silence removed, and by reporting the actual reason.

### Reproducing the round-2 fix on real media (QA: this is the check that matters)

The unit tests assert the DECISION; this asserts the MEASUREMENT, which needs real audio. It is
deliberately not in the fast suite - `dotnet test` is contracted to run with no ffmpeg and no audio.

```
ffmpeg -f lavfi -i "anoisesrc=r=48000:c=pink:a=0.003:d=1" -ac 1 nq.wav
ffmpeg -f lavfi -i "sine=frequency=440:r=48000" -t 1 -ac 1 t.wav
ffmpeg -f lavfi -i "anullsrc=r=48000:cl=mono" -t 0.1 z.wav
# pair_clean = nq + t ; pair_zero = z + nq + t   (concat demuxer, -c copy)
```

Measured through `MicMeasure.Measure` and `GateCalibration.ThresholdDb`:

| take | measured | threshold |
|---|---|---|
| pair_clean.wav | noise floor -73.4 dBFS, RMS -24.1 dBFS | **-63.4 dBFS** |
| pair_zero.wav (100 ms of zeros prepended) | noise floor -73.4 dBFS, RMS -24.3 dBFS | **-63.4 dBFS** |
| z.wav (silent throughout) | noise floor -inf, RMS -inf | no gate - "the microphone recorded nothing at all" |

The startup zeros no longer move the decision, which is the whole of round-2 defect 1.
