using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using AgentEyes;
using AgentEyes.AlwaysOn;
using AgentEyes.Audio;
using AgentEyes.Preview;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Shared
{
    /// <summary>
    /// THE GUARD FOR ISSUE #136: no argument list this build hands to ffmpeg may pair a COPIED VIDEO
    /// STREAM with a FILTER GRAPH.
    ///
    /// What shipped and why it survived. Every post-capture mux was one ffmpeg run of the shape
    /// "-i raw.mp4 -i mic.wav -filter_complex &lt;clean-voice chain&gt; -map 0:v -c:v copy -map [a] -c:a aac".
    /// On macOS that DEADLOCKS: "-c:v copy" lets the whole video be read and queued almost instantly
    /// while the mic is still being filtered, ffmpeg's muxing queue hits its default bound, and the
    /// process then spins at ~99% CPU past the end of the content without ever finalizing - so the
    /// recording is never produced. Measured on devthrottle-mac-mini against the 76-second recording
    /// 2026-11-14_111731_video: the shipped command was still frozen at time=00:01:16.03 after 151 s
    /// and had to be killed, while the same filter graph with NO video present finished in about a
    /// second, and so did the same video copy with NO filter graph present. It takes both halves in
    /// one command, which is why neither half alone ever showed it.
    ///
    /// The fix is structural: the mux is two ffmpeg runs (<see cref="FfmpegArgs.FilterMicToWav"/> and
    /// friends, then <see cref="FfmpegArgs.MuxVideoWithProcessedAudio"/>), so the race does not exist
    /// rather than being given a bigger queue to lose in. This file is what fails if the single-pass
    /// shape is put back anywhere - INCLUDING by a builder written later. Issue #114's
    /// MuxVideoAddMic/MuxVideoAddMicAndSystem, on their own branch, are exactly that shape and this
    /// guard is what will stop them merging un-split.
    ///
    /// HOW IT LOOKS - and it is a PRESENCE check, not an absence one (DEVELOPMENT_METHOD 6c). It
    /// REFLECTS over every static argument builder on <see cref="FfmpegArgs"/> (every partial in this
    /// build, so the macOS and Linux suites cover their own platform's builders too) and on
    /// <see cref="ComposeArgs"/>, INVOKES each one with a synthesized fixture, and inspects the
    /// arguments it really emits. Then:
    ///   * expected result  - no list pairs a copied video stream with a filter graph, and the
    ///                        builders that must exist are all present in the sweep by name;
    ///   * bad result       - a list does -&gt; FAIL, naming the builder and the whole command;
    ///   * EMPTY result     - a builder that could not be invoked, or a sweep that found none of the
    ///                        known builders, is a BROKEN INSTRUMENT and FAILS. A guard that silently
    ///                        exercises nothing is the defect class this repo keeps paying for.
    /// <see cref="TheGuardFires_OnTheExactCommandThatDeadlocked"/> runs the detector against the
    /// known-BAD command line, so the detector is proven to fire rather than assumed to.
    ///
    /// WHAT IT CANNOT SEE, stated rather than left to be discovered:
    ///   * an argument list built somewhere other than the two swept types. The three other places
    ///     in this engine that hold either a stream copy or a filter graph are asserted directly
    ///     below (<see cref="AlwaysOnArgs"/> Capture/Join/Trim), because their parameter types have
    ///     no cheap fixture. A list assembled at run time from values no fixture produces is covered
    ///     by neither.
    ///   * WHETHER the two-pass mux is fast. Only a real run on real media proves that, and the
    ///     runtime proof for this issue is the mux of 2026-11-14_111731_video through
    ///     'agenteyes package' with its wall-clock time.
    ///   * whether a deadlock-free command is otherwise CORRECT. That is what the rest of the suite
    ///     and the audio-level comparison are for.
    /// </summary>
    public sealed class FfmpegMuxShapeTests
    {
        /// <summary>
        /// Options that name no stream and copy EVERYTHING, so they copy the video too.
        /// </summary>
        private static readonly string[] CopyAllOptions = { "-c", "-codec" };

        /// <summary>Options that set the VIDEO stream's codec.</summary>
        private static readonly string[] VideoCodecOptions = { "-c:v", "-codec:v", "-vcodec" };

        /// <summary>
        /// Every ffmpeg option that takes a filter GRAPH (as opposed to a per-stream filter chain).
        /// -lavfi is ffmpeg's documented alias for -filter_complex, which is why it is here: a future
        /// builder spelling it that way must not slip past the guard.
        /// </summary>
        private static readonly string[] FilterGraphOptions =
        {
            "-filter_complex", "-filter_complex_script", "-lavfi",
        };

        /// <summary>
        /// The builders that MUST be in the sweep. If a rename or a deletion drops one, the guard
        /// would quietly cover less than it claims, so the sweep asserts their presence by name.
        /// The four two-pass builders are the fix for this issue; the rest are the other argument
        /// lists in the engine that carry either a copy or a filter graph.
        /// </summary>
        private static readonly string[] RequiredBuilders =
        {
            "FfmpegArgs.FilterMicToWav",
            "FfmpegArgs.MixMicSystemToWav",
            "FfmpegArgs.FilterSystemToWav",
            "FfmpegArgs.MuxVideoWithProcessedAudio",
            "FfmpegArgs.MixTwoWav",
            "FfmpegArgs.BurnSubtitles",
            "FfmpegArgs.VideoCapture",
            "ComposeArgs.CameraInset",
        };

        // ---- the invariant -------------------------------------------------

        [Fact]
        public void NoArgumentListPairsACopiedVideoStreamWithAFilterGraph()
        {
            var sweep = Sweep();

            // EMPTY IS A BROKEN INSTRUMENT, not a clean run.
            Assert.True(sweep.Failures.Count == 0,
                "an argument builder could not be exercised, so this guard covered less than it "
                + "claims - teach Fixture() the parameter type or fix the builder:\n  "
                + string.Join("\n  ", sweep.Failures));

            var exercised = sweep.Lists.Select(l => l.Builder).Distinct().ToList();
            foreach (string required in RequiredBuilders)
            {
                Assert.Contains(required, exercised);
            }

            Assert.True(sweep.Lists.Count >= RequiredBuilders.Length,
                $"only {sweep.Lists.Count} argument lists were produced - the sweep is not reaching "
                + "the builders any more.");

            var violations = new List<string>();
            foreach (var list in sweep.Lists)
            {
                if (VideoCopiedBesideAFilterGraph(list.Args))
                    violations.Add($"{list.Builder}: {string.Join(" ", list.Args)}");
            }

            Assert.True(violations.Count == 0,
                "a copied video stream is back in the same ffmpeg command as a filter graph (issue "
                + "#136 - on macOS that command never finalizes; split it into an audio pass and a "
                + "mux pass, see FfmpegArgs.MuxVideoWithProcessedAudio):\n  "
                + string.Join("\n  ", violations));
        }

        [Fact]
        public void TheGuardFires_OnTheExactCommandThatDeadlocked()
        {
            // The shipped mux of 2026-11-14_111731_video, copied from the service log, as the
            // detector's known-BAD input. A check only ever run against the state you hope passes
            // has demonstrated nothing (DEVELOPMENT_METHOD 6c, item 3).
            var asItShipped = new List<string>
            {
                "-y", "-i", "raw.mp4", "-i", "mic_native.wav",
                "-filter_complex",
                "[1:a]arnndn=m='bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];"
                    + "[m]adelay=517.855:all=1,alimiter=limit=0.95:level=false[a]",
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k", "out.mp4",
            };

            Assert.True(VideoCopiedBesideAFilterGraph(asItShipped));

            // And the two builders that replaced it are each clean ON THEIR OWN - which is the whole
            // point of the split, so assert it rather than inferring it from the sweep.
            Assert.False(VideoCopiedBesideAFilterGraph(
                FfmpegArgs.FilterMicToWav("mic_native.wav", "audio_processed.wav", Defaults())));
            Assert.False(VideoCopiedBesideAFilterGraph(
                FfmpegArgs.MuxVideoWithProcessedAudio("raw.mp4", "audio_processed.wav", "out.mp4")));
        }

        [Fact]
        public void TheGuardFires_OnEverySpellingOfACopiedVideoStream()
        {
            foreach (string copy in CopyAllOptions.Concat(VideoCodecOptions))
            {
                foreach (string graph in FilterGraphOptions)
                {
                    var bad = new[] { "-i", "a.mp4", graph, "[0:a]volume=1[a]", copy, "copy", "out.mp4" };
                    Assert.True(VideoCopiedBesideAFilterGraph(bad),
                        $"the detector missed '{copy} copy' beside '{graph}'");
                }
            }
        }

        [Fact]
        public void TheGuardDoesNotTouchTheCasesThatAreFine()
        {
            // A copied AUDIO stream beside a filter graph is not this defect: the filter graph is on
            // the video, which is being encoded, so there is nothing racing ahead of it. Flagging
            // this would have meant breaking the camera compose.
            var compose = ComposeArgs.CameraInset(
                "screen.mp4", "camera.mp4", "mask.png", "out.mp4", Composition(), -1.5, 23);
            Assert.Contains("-filter_complex", compose);
            Assert.Contains("copy", compose);                         // -c:a copy
            Assert.False(VideoCopiedBesideAFilterGraph(compose));

            // A pure stream copy with no filter graph at all is not it either.
            Assert.False(VideoCopiedBesideAFilterGraph(AlwaysOnArgs.Join("list.txt", "out.mp4")));
            Assert.False(VideoCopiedBesideAFilterGraph(AlwaysOnArgs.Trim("in.mp4", "out.mp4", 1.0, 9.0)));

            // And a filter graph with no copy anywhere is fine.
            Assert.False(VideoCopiedBesideAFilterGraph(
                FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", Defaults())));
        }

        /// <summary>
        /// The argument lists this engine builds OUTSIDE the two swept types, asserted directly
        /// because their parameter types have no cheap fixture. Named here so the enumeration in the
        /// issue #136 handoff is checked by a test rather than only written down.
        /// </summary>
        [Fact]
        public void TheAlwaysOnBuildersCarryNoFilterGraphBesideACopiedVideo()
        {
            // The always-on capture filters audio, but its video is ENCODED, never copied.
            var capture = AlwaysOnArgs.Capture(
                new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080), 10, "libx264",
                "Microphone (Yeti)", 1.0, @"\\.\pipe\agenteyes-sys", new PipeAudioFormat(48000, 2), 0.7,
                60, @"C:\pieces", 2);
            Assert.Contains("-filter_complex", capture);
            Assert.Equal("libx264", capture[capture.IndexOf("-c:v") + 1]);
            Assert.False(VideoCopiedBesideAFilterGraph(capture));

            // The join and the cut copy every stream and filter nothing.
            foreach (var list in new[]
                     {
                         AlwaysOnArgs.Join("list.txt", "out.mp4"),
                         AlwaysOnArgs.Trim("in.mp4", "out.mp4", 1.0, null),
                         AlwaysOnArgs.Trim("in.mp4", "out.mp4", null, 9.0),
                     })
            {
                Assert.Equal("copy", list[list.IndexOf("-c") + 1]);
                foreach (string graph in FilterGraphOptions) Assert.DoesNotContain(graph, list);
            }
        }

        // ---- the split did not drop the processing (issue #136, criterion 4) ----

        [Fact]
        public void TheTwoPassesTogetherStillCarryTheWholeCleanVoiceChain()
        {
            var o = Defaults();
            string pass1 = string.Join(" ", FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", o));
            string pass2 = string.Join(" ",
                FfmpegArgs.MuxVideoWithProcessedAudio("raw.mp4", "audio_processed.wav", "out.mp4"));

            // Pass 1 does ALL of the audio work, in the order issue #128 fixed it into.
            Assert.Contains(FfmpegArgs.FiniteSamples, pass1);
            int finite = pass1.IndexOf(FfmpegArgs.FiniteSamples, StringComparison.Ordinal);
            int arnndn = pass1.IndexOf("arnndn", StringComparison.Ordinal);
            int level = pass1.IndexOf("speechnorm", StringComparison.Ordinal);
            int limiter = pass1.IndexOf("alimiter", StringComparison.Ordinal);
            Assert.True(arnndn > finite && level > arnndn && limiter > level,
                "the clean-voice chain is out of order in pass 1: " + pass1);

            // ... into a float intermediate, so the AAC encoder in pass 2 gets the same samples the
            // one-pass command gave it. s16 here would be a quality step down nobody asked for.
            Assert.Contains("-c:a " + FfmpegArgs.ProcessedAudioCodec, pass1);
            Assert.Equal("pcm_f32le", FfmpegArgs.ProcessedAudioCodec);

            // Pass 2 does NO audio work: it copies the video and encodes the finished audio.
            foreach (string graph in FilterGraphOptions) Assert.DoesNotContain(graph, pass2);
            Assert.DoesNotContain("arnndn", pass2);
            Assert.DoesNotContain("speechnorm", pass2);
            Assert.DoesNotContain("alimiter", pass2);
            Assert.Contains("-map 0:v -c:v copy -map 1:a -c:a aac -b:a 160k", pass2);
        }

        [Fact]
        public void EveryPassOneBuilderWritesTheIntermediateAndNothingElse()
        {
            var o = Defaults();
            foreach (var list in new[]
                     {
                         FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", o),
                         FfmpegArgs.MixMicSystemToWav("raw.mp4", "sys.wav", "audio_processed.wav", o),
                         FfmpegArgs.FilterSystemToWav("sys.wav", "audio_processed.wav", 0.7),
                     })
            {
                // No video stream is mapped, which is what makes the race impossible.
                Assert.DoesNotContain("0:v", list);
                Assert.Equal("audio_processed.wav", list[list.Count - 1]);
                Assert.Equal(FfmpegArgs.ProcessedAudioCodec, list[list.IndexOf("-c:a") + 1]);
            }
        }

        // ---- the detector --------------------------------------------------

        /// <summary>
        /// True when <paramref name="args"/> sets the video stream (or every stream) to "copy" AND
        /// also carries a filter graph - the shape that deadlocks.
        /// </summary>
        private static bool VideoCopiedBesideAFilterGraph(IReadOnlyList<string> args)
            => CopiesTheVideoStream(args) && args.Any(a => FilterGraphOptions.Contains(a));

        private static bool CopiesTheVideoStream(IReadOnlyList<string> args)
        {
            for (int i = 0; i + 1 < args.Count; i++)
            {
                bool option = CopyAllOptions.Contains(args[i]) || VideoCodecOptions.Contains(args[i]);
                if (option && args[i + 1] == "copy") return true;
            }
            return false;
        }

        // ---- the sweep -----------------------------------------------------

        private sealed class ArgList
        {
            public ArgList(string builder, IReadOnlyList<string> args) { Builder = builder; Args = args; }
            public string Builder { get; }
            public IReadOnlyList<string> Args { get; }
        }

        private sealed class SweepResult
        {
            public List<ArgList> Lists { get; } = new();
            public List<string> Failures { get; } = new();
        }

        /// <summary>
        /// Invoke every static argument builder on FfmpegArgs (every partial compiled into THIS
        /// build) and ComposeArgs, and collect the argument lists they produce. A builder that
        /// cannot be invoked is recorded as a FAILURE rather than skipped.
        /// </summary>
        private static SweepResult Sweep()
        {
            var result = new SweepResult();
            foreach (var type in new[] { typeof(FfmpegArgs), typeof(ComposeArgs) })
            {
                foreach (var m in type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!ReturnsStringList(m.ReturnType)) continue;
                    if (m.IsGenericMethodDefinition)
                    {
                        result.Failures.Add($"{type.Name}.{m.Name} is generic - the sweep cannot invoke it");
                        continue;
                    }

                    string label = type.Name + "." + m.Name;
                    var parameterSets = ParameterSets(m, out string? why);
                    if (parameterSets == null)
                    {
                        result.Failures.Add($"{label}: {why}");
                        continue;
                    }

                    foreach (object?[] args in parameterSets)
                    {
                        try
                        {
                            var list = ToStringList(m.Invoke(null, args));
                            if (list == null)
                            {
                                result.Failures.Add($"{label} returned null instead of an argument list");
                                continue;
                            }
                            result.Lists.Add(new ArgList(label, list));
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie && tie.InnerException != null
                                ? tie.InnerException
                                : ex;
                            result.Failures.Add($"{label} threw {inner.GetType().Name}: {inner.Message}");
                        }
                    }
                }
            }
            return result;
        }

        private static bool ReturnsStringList(Type t) =>
            t == typeof(string[])
            || t == typeof(List<string>)
            || t == typeof(IReadOnlyList<string>)
            || t == typeof(IEnumerable<string>);

        private static IReadOnlyList<string>? ToStringList(object? produced)
        {
            if (produced == null) return null;
            var items = new List<string>();
            foreach (object? o in (IEnumerable)produced) items.Add(o?.ToString() ?? "");
            return items;
        }

        /// <summary>
        /// Every combination of fixture values for one builder's parameters - bool parameters are run
        /// BOTH ways, because one of them is what adds or removes a stage. Null (with a reason) when a
        /// parameter type has no fixture, which FAILS the sweep rather than shrinking it.
        /// </summary>
        private static List<object?[]>? ParameterSets(MethodInfo m, out string? why)
        {
            why = null;
            var sets = new List<object?[]> { Array.Empty<object?>() };
            foreach (var p in m.GetParameters())
            {
                var values = FixtureValues(p);
                if (values == null)
                {
                    why = $"no fixture for parameter '{p.Name}' of type {p.ParameterType.Name}";
                    return null;
                }

                var grown = new List<object?[]>();
                foreach (object?[] prefix in sets)
                {
                    foreach (object? v in values)
                    {
                        var next = new object?[prefix.Length + 1];
                        Array.Copy(prefix, next, prefix.Length);
                        next[prefix.Length] = v;
                        grown.Add(next);
                    }
                }
                sets = grown;
            }
            return sets;
        }

        private static List<object?>? FixtureValues(ParameterInfo p)
        {
            Type t = p.ParameterType;
            if (t == typeof(bool)) return new object?[] { false, true }.ToList();
            if (t == typeof(int)) return new object?[] { 30 }.ToList();
            if (t == typeof(int?)) return new object?[] { 0 }.ToList();
            if (t == typeof(double)) return new object?[] { 1.5 }.ToList();
            if (t == typeof(double?)) return new object?[] { 1.5 }.ToList();
            // A path parameter is a path, except where a builder REQUIRES a device node and says so
            // (Linux's V4l2CameraCapture refuses anything outside /dev/). Keyed on the parameter name
            // rather than a second value for every string, which would multiply the sweep out; a
            // rename breaks the instrument loudly, which is the behaviour we want.
            if (t == typeof(string))
            {
                bool deviceNode = p.Name != null
                    && p.Name.Contains("device", StringComparison.OrdinalIgnoreCase);
                return new object?[] { deviceNode ? "/dev/video0" : "fixture.mp4" }.ToList();
            }
            if (t == typeof(Rectangle)) return new object?[] { new Rectangle(0, 0, 1920, 1080) }.ToList();
            if (t == typeof(Rectangle?)) return new object?[] { new Rectangle(0, 0, 1920, 1080) }.ToList();
            if (t == typeof(Size)) return new object?[] { new Size(1920, 1080) }.ToList();
            if (t == typeof(CameraComposition)) return new object?[] { Composition() }.ToList();
            if (t.IsEnum) return new object?[] { Enum.GetValues(t).GetValue(0) }.ToList();
            // The mix/mux builders take these. Suppression and the gate are OFF because both need
            // state a pure fixture cannot have (an RNNoise model on disk, a measured gate threshold)
            // and would throw by design; voice leveling is on, so a filter graph is still emitted.
            if (t == typeof(AudioMixOptions)) return new object?[] { SweepOptions() }.ToList();
            return null;
        }

        /// <summary>The shipped mic defaults, with a model path so the chain builds without disk.</summary>
        private static AudioMixOptions Defaults() => new()
        {
            NoiseSuppression = true,
            VoiceLeveling = true,
            NoiseGate = false,
            GateCalibrated = true,
            MicGain = 1.0,
            SystemGain = 0.7,
            RnnoiseModelPath = "bd.rnnn",
        };

        /// <summary>
        /// What the reflection sweep hands an AudioMixOptions parameter. Denoise off (the sweep has
        /// no model on disk), leveling ON so the graph is not empty - an options fixture that
        /// produced no filter graph would make every mux list trivially pass the invariant.
        /// </summary>
        private static AudioMixOptions SweepOptions() => new()
        {
            NoiseSuppression = false,
            VoiceLeveling = true,
            NoiseGate = false,
            GateCalibrated = true,
            MicGain = 1.0,
            SystemGain = 0.7,
        };

        private static CameraComposition Composition() => CameraComposition.For(
            1920, 1080, 1920, 1080,
            new CameraOverlaySettings
            {
                Corner = "bottom-right",
                Shape = "circle",
                InsetFraction = 0.30,
                Circle = new CameraOverlayCircle { CentreX = 0.50, CentreY = 0.42, Diameter = 0.60 },
            });
    }
}
