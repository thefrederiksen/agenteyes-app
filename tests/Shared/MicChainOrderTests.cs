using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgentEyes;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Shared
{
    /// <summary>
    /// THE ORDER GUARD FOR ISSUE #146, compiled into the Windows, Linux and macOS test projects,
    /// because the clean-voice mic chain is shared by every platform's post-processing.
    ///
    /// arnndn accepts only 48 kHz. Hand it anything else and ffmpeg inserts a resampler of its own
    /// at arnndn's input - AFTER issue #128's 480-sample framing stage - which re-chunks the audio,
    /// undoes the framing, and on ffmpeg 9.0.2 stalls post-processing (measured: a 44.1 kHz Windows
    /// microphone, 10 of 10 default-settings recordings). #128's framing was correct and was still
    /// undone by something downstream of it, so what is pinned here is the ORDER: resample, then
    /// frame, then denoise - adjacent, and the resample exactly once.
    ///
    /// HOW IT LOOKS - the #136 pattern (FfmpegMuxShapeTests): no process, no I/O. It REFLECTS over
    /// every static builder on <see cref="FfmpegArgs"/> that takes <see cref="AudioMixOptions"/> (the
    /// only builders that can emit the mic chain, in every partial compiled into this build), invokes
    /// each with denoise ON, and inspects the arguments it really emits.
    ///   * expected - every such list carries the denoise segment in the pinned order, and the
    ///                builders that must exist are present in the sweep by name;
    ///   * bad      - a list with the stages swapped, separated, missing or doubled -> FAIL, naming
    ///                the builder and the whole command;
    ///   * EMPTY    - a builder the sweep cannot invoke, or a sweep that finds none of the known
    ///                builders, is a BROKEN INSTRUMENT and FAILS rather than passing on nothing.
    /// <see cref="TheGuardFires_OnTheSwappedAndTheUnfixedChains"/> runs the detector against the
    /// known-bad graphs, so it is proven to fire rather than assumed to.
    ///
    /// WHAT IT CANNOT SEE: whether ffmpeg, given this order, really feeds arnndn whole 48 kHz frames.
    /// That is a property of ffmpeg's format negotiation, shown on its executed graph by the opt-in
    /// real-ffmpeg tests (MicChainSampleRateTests, AGENTEYES_FFMPEG_TESTS=1) and recorded as evidence
    /// in docs/cencon/proof/issue-146.
    /// </summary>
    public sealed class MicChainOrderTests
    {
        /// <summary>The builders that MUST be in the sweep - the three pass-1 builders that run the mic
        /// chain today. A rename or deletion that drops one would make the guard cover less than it
        /// claims, so their presence is asserted by name.</summary>
        private static readonly string[] RequiredBuilders =
        {
            "FilterMicToWav",
            "MixMicSystemToWav",
            "MixTwoWav",
        };

        /// <summary>The shipped defaults with denoise ON. The model path is only formatted into the
        /// graph, never opened, so no file is needed.</summary>
        private static AudioMixOptions DenoiseOn() => new()
        {
            NoiseSuppression = true,
            VoiceLeveling = true,
            NoiseGate = false,
            GateCalibrated = true,
            MicGain = 1.0,
            SystemGain = 0.7,
            RnnoiseModelPath = "/m/bd.rnnn",
            MicChannels = 2,
        };

        // ---- the detector ------------------------------------------------------------------

        /// <summary>Why a command's denoise segment is out of order, or null when it is in order.</summary>
        internal static string? OrderViolation(string command)
        {
            int arnndn = command.IndexOf("arnndn=", StringComparison.Ordinal);
            if (arnndn < 0) return "no arnndn stage although denoise is on";
            int framing = command.IndexOf(FfmpegArgs.ArnndnFraming, StringComparison.Ordinal);
            if (framing < 0) return "no framing stage (" + FfmpegArgs.ArnndnFraming + ")";
            int rate = command.IndexOf(FfmpegArgs.ArnndnRate, StringComparison.Ordinal);
            if (rate < 0) return "no 48 kHz resample (" + FfmpegArgs.ArnndnRate + ")";
            if (rate > framing) return "the resample comes AFTER the framing - it undoes it";
            if (framing > arnndn) return "the framing comes AFTER arnndn";
            if (!command.Contains(FfmpegArgs.ArnndnRate + "," + FfmpegArgs.ArnndnFraming + ",arnndn=", StringComparison.Ordinal))
                return "something sits between the resample, the framing and arnndn";
            if (command.IndexOf("aresample", rate + 1, StringComparison.Ordinal) >= 0)
                return "a second resample - one after the framing undoes it";
            return null;
        }

        [Fact]
        public void TheGuardFires_OnTheSwappedAndTheUnfixedChains()
        {
            const string head = "[0:a]aeval=x,aformat=channel_layouts=stereo,";
            const string tail = "arnndn=m='bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m]";
            // The graph main shipped before issue #146 (no explicit resample - ffmpeg added its own).
            Assert.NotNull(OrderViolation(head + "asetnsamples=n=480:p=1," + tail));
            // The two stages swapped - measured to stall on 9.0.2 exactly like the unfixed graph.
            Assert.NotNull(OrderViolation(head + "asetnsamples=n=480:p=1,aresample=48000," + tail));
            // Separated by another stage.
            Assert.NotNull(OrderViolation(head + "aresample=48000,anull,asetnsamples=n=480:p=1," + tail));
            // A second resample after the framing.
            Assert.NotNull(OrderViolation(head + "aresample=48000,asetnsamples=n=480:p=1," + tail + ",aresample=44100"));
            // And it does not fire on the fixed graph.
            Assert.Null(OrderViolation(head + "aresample=48000,asetnsamples=n=480:p=1," + tail));
        }

        [Fact]
        public void ArnndnRate_Is48kHz()
        {
            Assert.Equal("aresample=48000", FfmpegArgs.ArnndnRate);
        }

        // ---- the sweep -------------------------------------------------------------------------

        [Fact]
        public void EveryMicChainBuilder_DenoiseOn_ResamplesAheadOfTheFraming()
        {
            var failures = new List<string>();
            var lists = new List<(string Builder, string Command)>();

            foreach (var m in typeof(FfmpegArgs).GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!m.GetParameters().Any(p => p.ParameterType == typeof(AudioMixOptions))) continue;
                if (!ReturnsStringList(m.ReturnType)) continue;

                foreach (var args in ParameterSets(m, failures))
                {
                    try
                    {
                        var produced = (IEnumerable)m.Invoke(null, args)!;
                        lists.Add((m.Name, string.Join(" ", produced.Cast<object>())));
                    }
                    catch (TargetInvocationException ex)
                    {
                        failures.Add($"{m.Name} threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
                    }
                }
            }

            // EMPTY IS A BROKEN INSTRUMENT, not a clean run.
            Assert.True(failures.Count == 0,
                "a mic-chain builder could not be exercised, so this guard covered less than it claims:\n  "
                + string.Join("\n  ", failures));
            var exercised = lists.Select(l => l.Builder).Distinct().ToList();
            foreach (string required in RequiredBuilders) Assert.Contains(required, exercised);

            var violations = lists
                .Select(l => (l.Builder, l.Command, Why: OrderViolation(l.Command)))
                .Where(v => v.Why != null)
                .Select(v => $"{v.Builder}: {v.Why}: {v.Command}")
                .ToList();
            Assert.True(violations.Count == 0,
                "the mic chain must resample to 48 kHz IMMEDIATELY AHEAD of the 480-sample framing, which "
                + "sits immediately ahead of arnndn (issue #146):\n  " + string.Join("\n  ", violations));
        }

        [Fact]
        public void MicChain_NoDenoise_EmitsNoResample()
        {
            // Only arnndn needs 48 kHz. Without it the mic keeps its own rate through the chain.
            var o = DenoiseOn(); o.NoiseSuppression = false;
            Assert.DoesNotContain("aresample", string.Join(" ", FfmpegArgs.FilterMicToWav("raw.mp4", "a.wav", o)));
        }

        private static bool ReturnsStringList(Type t) =>
            t == typeof(string[]) || t == typeof(List<string>)
            || t == typeof(IReadOnlyList<string>) || t == typeof(IEnumerable<string>);

        /// <summary>Fixture arguments for one builder: strings are paths, doubles run at zero and at a
        /// positive offset (the macOS alignment parameter adds a stage), the options are DenoiseOn().
        /// A parameter type with no fixture is recorded as a failure, never skipped.</summary>
        private static IEnumerable<object?[]> ParameterSets(MethodInfo m, List<string> failures)
        {
            var sets = new List<object?[]> { Array.Empty<object?>() };
            foreach (var p in m.GetParameters())
            {
                object?[] values;
                if (p.ParameterType == typeof(string)) values = new object?[] { "fixture.wav" };
                else if (p.ParameterType == typeof(AudioMixOptions)) values = new object?[] { DenoiseOn() };
                else if (p.ParameterType == typeof(double)) values = new object?[] { 0.0, 0.25, -0.25 };
                else if (p.ParameterType == typeof(bool)) values = new object?[] { false, true };
                else
                {
                    failures.Add($"{m.Name}: no fixture for parameter '{p.Name}' of type {p.ParameterType.Name}");
                    return Array.Empty<object?[]>();
                }
                sets = sets.SelectMany(prefix => values.Select(v => prefix.Append(v).ToArray())).ToList();
            }
            return sets;
        }
    }
}
