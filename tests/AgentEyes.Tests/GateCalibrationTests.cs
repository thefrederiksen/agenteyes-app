using System;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #46: the gate threshold is derived from the measured capture, never from a constant.
    ///
    /// The defect these lock down: a fixed 0.02 (-34 dBFS) threshold was applied to every
    /// microphone. On the reference take (noise floor -87.9 dBFS, RMS -38.1 dBFS) that sat ABOVE
    /// the speech and the gate alone added 19 seconds of silence to a 162 second recording.
    /// </summary>
    public class GateCalibrationTests
    {
        // The reference take that exposed the defect.
        private const double QuietMicFloorDb = -88.0;
        private const double QuietMicRmsDb = -38.0;

        [Fact]
        public void ThresholdDb_quiet_clean_mic_stays_well_below_the_voice()
        {
            var db = GateCalibration.ThresholdDb(new MicLevels(QuietMicFloorDb, QuietMicRmsDb));

            Assert.NotNull(db);
            // AC2(a): at least 12 dB of headroom under the voice. The old fixed -34 dBFS was 4 dB
            // ABOVE it, which is the whole bug.
            Assert.True(db!.Value <= QuietMicRmsDb - GateCalibration.SpeechHeadroomDb,
                $"threshold {db.Value} dBFS must be at least {GateCalibration.SpeechHeadroomDb} dB below the {QuietMicRmsDb} dBFS voice");
            Assert.True(db.Value > QuietMicFloorDb, "threshold must still sit above the noise floor");
        }

        [Fact]
        public void ThresholdDb_quiet_clean_mic_is_far_below_the_old_fixed_threshold()
        {
            var db = GateCalibration.ThresholdDb(new MicLevels(QuietMicFloorDb, QuietMicRmsDb));

            // -34 dBFS is what the removed constant 0.02 meant.
            Assert.NotNull(db);
            Assert.True(db!.Value < -34.0,
                $"the measured threshold {db.Value} dBFS must be below the old fixed -34 dBFS");
        }

        [Fact]
        public void ThresholdDb_noisy_mic_clears_the_floor_and_the_voice()
        {
            // AC2(b): a genuinely noisy room, where a gate has real work to do.
            var db = GateCalibration.ThresholdDb(new MicLevels(-45.0, -20.0));

            Assert.NotNull(db);
            Assert.True(db!.Value > -45.0, "threshold must be above the noise floor to gate anything");
            Assert.True(db.Value <= -20.0 - GateCalibration.SpeechHeadroomDb,
                "threshold must still clear the voice by the full headroom");
        }

        [Fact]
        public void ThresholdDb_when_floor_and_voice_are_too_close_disables_the_gate()
        {
            // AC2(c): 5 dB of span cannot hold a 10 dB floor margin and 12 dB of headroom at once.
            // The honest answer is "do not gate", NOT a guessed number.
            Assert.Null(GateCalibration.ThresholdDb(new MicLevels(-30.0, -25.0)));
        }

        [Fact]
        public void ThresholdDb_at_exactly_the_usable_span_still_gates()
        {
            var levels = new MicLevels(-60.0, -60.0 + GateCalibration.MinUsableSpanDb);
            var db = GateCalibration.ThresholdDb(levels);

            Assert.NotNull(db);
            Assert.Equal(-50.0, db!.Value, 6);
        }

        [Fact]
        public void ThresholdDb_just_under_the_usable_span_does_not_gate()
        {
            var levels = new MicLevels(-60.0, -60.0 + GateCalibration.MinUsableSpanDb - 0.1);
            Assert.Null(GateCalibration.ThresholdDb(levels));
        }

        [Fact]
        public void ThresholdDb_rejects_a_nan_measurement()
        {
            // NaN is a broken instrument, not a level - fail loudly rather than quietly not gating.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GateCalibration.ThresholdDb(new MicLevels(-88.0, double.NaN)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GateCalibration.ThresholdDb(new MicLevels(double.NaN, -38.0)));
        }

        [Fact]
        public void ThresholdDb_treats_digital_silence_as_do_not_gate_not_as_an_error()
        {
            // REGRESSION - Review Gate round 1, defect 1. astats reports a noise floor of -inf for a
            // take containing digital silence (100 ms of zero samples is enough). That is a valid
            // measurement, and throwing on it failed the whole recording: the mux threw, the pending
            // mux was never cleared, and all three retries failed identically against the same file.
            // The correct answer is a resolved no-gate decision.
            Assert.Null(GateCalibration.ThresholdDb(new MicLevels(double.NegativeInfinity, -21.5)));
            Assert.Null(GateCalibration.ThresholdLinear(new MicLevels(double.NegativeInfinity, -21.5)));
        }

        [Fact]
        public void ThresholdDb_treats_an_entirely_silent_track_as_do_not_gate()
        {
            Assert.Null(GateCalibration.ThresholdDb(
                new MicLevels(double.NegativeInfinity, double.NegativeInfinity)));
            Assert.Null(GateCalibration.ThresholdDb(new MicLevels(-88.0, double.NegativeInfinity)));
        }

        [Fact]
        public void ThresholdLinear_matches_the_decibel_decision()
        {
            var levels = new MicLevels(QuietMicFloorDb, QuietMicRmsDb);
            double expected = GateCalibration.ToLinear(GateCalibration.ThresholdDb(levels)!.Value);

            Assert.Equal(expected, GateCalibration.ThresholdLinear(levels)!.Value, 12);
            Assert.Null(GateCalibration.ThresholdLinear(new MicLevels(-30.0, -25.0)));
        }

        [Theory]
        [InlineData(0.0, 1.0)]
        [InlineData(-6.0, 0.5011872336272722)]
        [InlineData(-34.0, 0.0199526231496888)]   // the removed constant, in decibels
        public void ToLinear_converts_decibels_to_amplitude(double db, double expected)
        {
            Assert.Equal(expected, GateCalibration.ToLinear(db), 9);
        }

        [Fact]
        public void ToDb_round_trips_ToLinear()
        {
            Assert.Equal(-78.0, GateCalibration.ToDb(GateCalibration.ToLinear(-78.0)), 9);
        }

        [Fact]
        public void Levels_and_gate_descriptions_are_plain_ascii_even_for_infinities()
        {
            // REGRESSION. .NET renders an infinity as the Unicode INFINITY symbol, and the first
            // silent take measured after the -inf fix wrote "noise floor -inf" as a NON-ASCII
            // character into AgentEyes-20260830.log. This repo is ASCII-only everywhere, logs
            // included.
            var silent = new MicLevels(double.NegativeInfinity, double.NegativeInfinity);
            var normal = new MicLevels(-88.0, -38.0);

            foreach (var text in new[]
            {
                silent.ToString(),
                normal.ToString(),
                GateCalibration.Text(double.NegativeInfinity),
                GateCalibration.Text(double.PositiveInfinity),
                GateCalibration.Text(double.NaN),
                GateCalibration.Text(-77.9),
            })
            {
                Assert.All(text, ch => Assert.InRange(ch, (char)0x20, (char)0x7E));
            }

            Assert.Contains("-inf", silent.ToString());
        }

        [Fact]
        public void GateDescription_is_plain_ascii_for_every_state()
        {
            var o = new AudioMixOptions { NoiseGate = true };
            var states = new System.Collections.Generic.List<string> { o.GateDescription() };
            o.GateCalibrated = true;
            states.Add(o.GateDescription());
            o.GateThresholdLinear = GateCalibration.ToLinear(-77.9);
            states.Add(o.GateDescription());
            o.NoiseGate = false;
            states.Add(o.GateDescription());

            foreach (var text in states)
            {
                Assert.All(text, ch => Assert.InRange(ch, (char)0x20, (char)0x7E));
            }
        }

        // ---- what the filter chain does with the decision ----------------------

        private static AudioMixOptions GatedOpts() => new()
        {
            NoiseSuppression = false,
            VoiceLeveling = false,
            NoiseGate = true,
        };

        [Fact]
        public void MicChain_refuses_to_build_a_gate_that_was_never_measured()
        {
            var o = GatedOpts();   // NoiseGate on, GateCalibrated still false

            var ex = Assert.Throws<UsageException>(
                () => FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", o));
            Assert.Contains("has not been measured", ex.Message);
        }

        [Fact]
        public void MicChain_omits_the_gate_when_the_measurement_found_no_room_for_one()
        {
            var o = GatedOpts();
            o.GateCalibrated = true;
            o.GateThresholdLinear = null;   // measured, and the answer was "do not gate"

            string s = string.Join(" ", FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", o));
            Assert.DoesNotContain("agate", s);
        }

        [Fact]
        public void MicChain_uses_the_measured_threshold_verbatim()
        {
            var o = GatedOpts();
            o.GateCalibrated = true;
            o.GateThresholdLinear = GateCalibration.ThresholdLinear(
                new MicLevels(QuietMicFloorDb, QuietMicRmsDb));

            string s = string.Join(" ", FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", o));
            Assert.Contains($"agate=threshold={o.GateThresholdLinear!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}", s);
            Assert.DoesNotContain("agate=threshold=0.02", s);
        }

        [Fact]
        public void Gate_turned_off_by_the_person_needs_no_measurement()
        {
            var o = GatedOpts();
            o.NoiseGate = false;

            string s = string.Join(" ", FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", o));
            Assert.DoesNotContain("agate", s);
        }

        [Fact]
        public void GateDescription_tells_the_three_states_apart()
        {
            var off = GatedOpts(); off.NoiseGate = false;
            Assert.Equal("gate off", off.GateDescription());

            var unmeasured = GatedOpts();
            Assert.Contains("not yet measured", unmeasured.GateDescription());

            var noRoom = GatedOpts(); noRoom.GateCalibrated = true;
            Assert.Contains("no room", noRoom.GateDescription());

            var measured = GatedOpts();
            measured.GateCalibrated = true;
            measured.GateThresholdLinear = GateCalibration.ToLinear(-78.0);
            Assert.Contains("-78.0 dBFS", measured.GateDescription());
        }

        [Fact]
        public void GateApplies_only_when_asked_for_and_a_threshold_was_found()
        {
            var o = GatedOpts();
            Assert.False(o.GateApplies);                    // not measured

            o.GateCalibrated = true;
            Assert.False(o.GateApplies);                    // measured, no room

            o.GateThresholdLinear = 0.001;
            Assert.True(o.GateApplies);

            o.NoiseGate = false;
            Assert.False(o.GateApplies);                    // person turned it off
        }

        // ---- parsing the measurement ------------------------------------------

        private const string AstatsSample = @"
[Parsed_astats_0 @ 0000] Channel: 1
[Parsed_astats_0 @ 0000] RMS level dB: -11.111111
[Parsed_astats_0 @ 0000] Noise floor dB: -22.222222
[Parsed_astats_0 @ 0000] Channel: 2
[Parsed_astats_0 @ 0000] RMS level dB: -33.333333
[Parsed_astats_0 @ 0000] Noise floor dB: -44.444444
[Parsed_astats_0 @ 0000] Overall
[Parsed_astats_0 @ 0000] Peak level dB: -14.398967
[Parsed_astats_0 @ 0000] RMS level dB: -38.117568
[Parsed_astats_0 @ 0000] Noise floor dB: -87.888855
";

        [Fact]
        public void ParseOverall_reads_the_summary_not_a_channel()
        {
            var levels = MicMeasure.ParseOverall(AstatsSample);

            Assert.NotNull(levels);
            Assert.Equal(-87.888855, levels!.Value.NoiseFloorDb, 6);
            Assert.Equal(-38.117568, levels.Value.RmsDb, 6);
        }

        [Fact]
        public void ParseOverall_returns_null_when_a_figure_is_missing()
        {
            Assert.Null(MicMeasure.ParseOverall("[astats] Overall\n[astats] RMS level dB: -38.1\n"));
            Assert.Null(MicMeasure.ParseOverall("[astats] Overall\n[astats] Noise floor dB: -88.0\n"));
            Assert.Null(MicMeasure.ParseOverall("ffmpeg: no audio stream found"));
            Assert.Null(MicMeasure.ParseOverall(""));
            Assert.Null(MicMeasure.ParseOverall(null!));
        }

        [Fact]
        public void ParseOverall_reads_digital_silence_as_a_measurement_not_a_missing_figure()
        {
            // REGRESSION - Review Gate round 1, defect 1. This test previously asserted NULL here,
            // which is what locked the defect in: -inf is ffmpeg's VALID report that the quietest
            // part of the take is digital zero. Erasing it to "no measurement" made Measure throw,
            // and that took the whole recording down with it.
            var levels = MicMeasure.ParseOverall(@"
[astats] Overall
[astats] RMS level dB: -21.487727
[astats] Noise floor dB: -inf
");

            Assert.NotNull(levels);
            Assert.True(double.IsNegativeInfinity(levels!.Value.NoiseFloorDb));
            Assert.Equal(-21.487727, levels.Value.RmsDb, 6);

            // And it resolves the way AC2(c) requires: no gate, not a failure.
            Assert.Null(GateCalibration.ThresholdDb(levels.Value));
        }

        [Fact]
        public void ParseOverall_reads_a_fully_silent_track()
        {
            var levels = MicMeasure.ParseOverall(@"
[astats] Overall
[astats] RMS level dB: -inf
[astats] Noise floor dB: -inf
");

            Assert.NotNull(levels);
            Assert.True(double.IsNegativeInfinity(levels!.Value.RmsDb));
            Assert.Null(GateCalibration.ThresholdDb(levels.Value));
        }

        // ---- upgrading from a manifest that carried the old fixed threshold ----

        /// <summary>
        /// A deferred mux written by a build that still had the hardcoded threshold. The stale
        /// number must NOT survive into the resumed mux - it is the defect, and the resumed run has
        /// the capture on disk and can measure it properly.
        /// </summary>
        private const string LegacyPendingMuxManifest = @"{
  ""Tool"": ""AgentEyes"",
  ""Mode"": ""video"",
  ""Label"": ""video"",
  ""CreatedUtc"": ""2026-08-11T18:30:17.7654321Z"",
  ""VideoFile"": ""recording.mp4"",
  ""PendingMux"": {
    ""Mode"": ""video"",
    ""Source"": ""mixed"",
    ""RawVideo"": ""raw.mp4"",
    ""SysWav"": ""sys_native.wav"",
    ""FinalFile"": ""recording.mp4"",
    ""RawDurationSeconds"": 61.4,
    ""Options"": {
      ""MicGain"": 1.0,
      ""SystemGain"": 0.7,
      ""NoiseSuppression"": true,
      ""NoiseGate"": true,
      ""GateThreshold"": 0.02,
      ""VoiceLeveling"": true,
      ""RnnoiseModelPath"": ""C:/AgentEyes/models/bd.rnnn""
    }
  }
}";

        [Fact]
        public void A_legacy_pending_mux_does_not_carry_its_stale_threshold_forward()
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(
                LegacyPendingMuxManifest,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var options = manifest.PendingMux!.Options;

            // The person's choice to gate is honored...
            Assert.True(options.NoiseGate);
            // ...but the old 0.02 is gone and nothing is measured yet.
            Assert.Null(options.GateThresholdLinear);
            Assert.False(options.GateCalibrated);

            // So a resumed mux cannot build a chain until it measures the capture - it can never
            // silently apply the stale threshold.
            var ex = Assert.Throws<UsageException>(
                () => FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", options));
            Assert.Contains("has not been measured", ex.Message);
        }

        [Fact]
        public void ParseOverall_feeds_the_reference_take_to_a_sane_threshold()
        {
            // End to end over the numbers actually measured on the recording that exposed the bug.
            var levels = MicMeasure.ParseOverall(AstatsSample)!.Value;
            double db = GateCalibration.ThresholdDb(levels)!.Value;

            Assert.Equal(-77.888855, db, 6);
            Assert.True(db < -34.0, "the measured threshold must land far below the old fixed one");
        }
    }
}
