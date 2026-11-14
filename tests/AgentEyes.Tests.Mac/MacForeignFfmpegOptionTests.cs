using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using AgentEyes;
using AgentEyes.Platform.Mac;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    /// <summary>
    /// THE GUARD FOR ISSUE #111: no option belonging to another platform's demuxer may appear in an
    /// argument list this build hands to ffmpeg for an AVFoundation capture.
    ///
    /// What shipped and why it survived. Both macOS audio inputs carried "-audio_buffer_size 20",
    /// a DIRECTSHOW option that AVFoundation has never had, so ffmpeg answered "Unrecognized option
    /// 'audio_buffer_size'." and exited 8 before opening a device - every capture with audio on a Mac
    /// failed instantly. The existing tests compared argument strings against expected lists that
    /// CONTAINED the bad flag, so they stayed green while the product could not record. Fixing those
    /// expectations is necessary and not sufficient: this file is the check that fails if the flag,
    /// or any of its DirectShow-only siblings, is put back anywhere in the Mac argument builders.
    ///
    /// HOW IT LOOKS - and it is a PRESENCE check, not an absence one (DEVELOPMENT_METHOD 6c). It
    /// REFLECTS over every static argument builder on <see cref="FfmpegArgs"/> (both partials - the
    /// shared/Windows one and Platform/Mac's) and on <see cref="AvfStream"/>, INVOKES each one with a
    /// synthesized fixture, and inspects the argument values it really emits. Then:
    ///   * expected result  - every AVFoundation list is clean, and the four builders that must exist
    ///                        are all present in the sweep by name;
    ///   * bad result       - a foreign option name appears in an AVFoundation list -> FAIL, naming
    ///                        the builder and the option;
    ///   * EMPTY result     - a builder that could not be invoked, or a sweep that found none of the
    ///                        known AVFoundation builders, is a BROKEN INSTRUMENT and FAILS. A guard
    ///                        that silently exercises nothing is the defect class this repo keeps
    ///                        paying for.
    /// <see cref="TheGuardFires_OnTheExactFlagThatShipped"/> runs the same detector against a
    /// known-BAD list, so the detector is proven to fire rather than assumed to.
    ///
    /// WHAT IT CANNOT SEE, stated rather than left to be discovered:
    ///   * an argument list built somewhere other than these two types - which is why
    ///     <see cref="NoForeignOptionInAnyMacSourceFile"/> also scans the Platform/Mac SOURCE for the
    ///     same option names outside comments. A source scan is defeated by an alias or a computed
    ///     string; the reflection sweep is defeated by a builder that lives elsewhere. Together they
    ///     cover each other's blind spot, and neither covers an argument assembled at run time from
    ///     values no fixture produces.
    ///   * whether ffmpeg accepts the REST of the list. Only a real capture proves that, and the
    ///     runtime proof for this issue is an 'agenteyes audio' run with volumedetect on its file.
    /// </summary>
    public sealed class MacForeignFfmpegOptionTests
    {
        /// <summary>
        /// Option names that belong to another platform's demuxer and are therefore wrong in any
        /// AVFoundation command line. The DirectShow set is from ffmpeg's dshow demuxer; fragment_size
        /// is PulseAudio's (correct on Linux, meaningless here). audio_buffer_size is the one that
        /// shipped.
        /// </summary>
        private static readonly string[] ForeignOptions =
        {
            // DirectShow (ffmpeg -h demuxer=dshow)
            "audio_buffer_size",
            "video_device_number",
            "audio_device_number",
            "video_pin_name",
            "audio_pin_name",
            "list_options",
            "crossbar_video_input_pin_number",
            "crossbar_audio_input_pin_number",
            "show_video_device_dialog",
            "show_audio_device_dialog",
            "use_video_device_timestamps",
            // PulseAudio (ffmpeg -h demuxer=pulse)
            "fragment_size",
        };

        /// <summary>
        /// The AVFoundation builders that MUST be in the sweep. If a rename or a deletion drops one,
        /// this guard would quietly cover less than it claims, so the sweep asserts their presence.
        /// </summary>
        private static readonly string[] RequiredMacBuilders =
        {
            "FfmpegArgs.AvfVideoCapture",
            // FfmpegArgs.AvfAudioInput was the fourth. Issue #114 DELETED it: the macOS screen
            // capture no longer has a microphone input (it cost ~88% of the audio), so the only
            // AVFoundation microphone argument builder left in the product is AvfStream.Arguments,
            // which is on this list and is swept exactly as that one was.
            "FfmpegArgs.AvfCameraCapture",
            "AvfStream.Arguments",
        };

        [Fact]
        public void NoForeignFfmpegOptionInAnyAvfoundationArgumentList()
        {
            var sweep = Sweep();

            // EMPTY IS A BROKEN INSTRUMENT, not a clean run.
            Assert.True(sweep.Failures.Count == 0,
                "a Mac argument builder could not be exercised, so this guard covered less than it "
                + "claims - teach Fixture() the parameter type or fix the builder:\n  "
                + string.Join("\n  ", sweep.Failures));

            var exercised = sweep.Lists.Select(l => l.Builder).Distinct().ToList();
            foreach (var required in RequiredMacBuilders)
            {
                Assert.Contains(required, exercised);
            }

            var avf = sweep.Lists.Where(l => IsAvFoundation(l)).ToList();
            Assert.True(avf.Count >= RequiredMacBuilders.Length,
                $"only {avf.Count} AVFoundation argument lists were produced - the sweep is not "
                + "reaching the Mac builders any more.");

            var violations = new List<string>();
            foreach (var list in avf)
            {
                foreach (string option in Violations(list.Args))
                {
                    violations.Add($"{list.Builder} emits -{option} ({string.Join(" ", list.Args)})");
                }
            }

            Assert.True(violations.Count == 0,
                "an option from another platform's demuxer is back in an AVFoundation command line "
                + "(issue #111 - ffmpeg rejects the whole command line and exits before it opens a "
                + "device):\n  " + string.Join("\n  ", violations));
        }

        [Fact]
        public void TheGuardFires_OnTheExactFlagThatShipped()
        {
            // The detector, run against the KNOWN-BAD list - the microphone input exactly as it was
            // built before this fix. A check only ever run against the state you hope passes has
            // demonstrated nothing (DEVELOPMENT_METHOD 6c, item 3).
            var asItShipped = new List<string>
            {
                "-f", "avfoundation", "-thread_queue_size", "1024", "-audio_buffer_size", "20", "-i", ":0",
            };

            Assert.True(IsAvFoundation(new ArgList("fixture", asItShipped)));
            Assert.Equal(new[] { "audio_buffer_size" }, Violations(asItShipped));

            // ... and the builder that opens a microphone on a Mac today is clean. (It is
            // AvfStream.Arguments since issue #114 removed the screen capture's audio input.)
            Assert.Empty(Violations(AgentEyes.Platform.Mac.AvfStream.Arguments(0, 48000, 1)));
        }

        [Fact]
        public void TheGuardDoesNotTouchTheWindowsDshowMitigation()
        {
            // -audio_buffer_size is CORRECT on dshow (issue #125) and must stay there. The sweep must
            // therefore not class a gdigrab/dshow list as AVFoundation, or fixing this issue would
            // have meant breaking Windows.
            var windows = FfmpegArgs.VideoCapture(
                new Rectangle(0, 0, 1920, 1080), "Microphone (Yeti)", 30, 23, "/r/raw.mp4");

            Assert.Contains("-audio_buffer_size", windows);
            Assert.False(IsAvFoundation(new ArgList("FfmpegArgs.VideoCapture", windows)));
        }

        [Fact]
        public void NoForeignOptionInAnyMacSourceFile()
        {
            // The backstop for an argument list built outside the two swept types: the Platform/Mac
            // SOURCE, with comments removed so the corrected comments that NAME -audio_buffer_size as
            // the bug are not read as the bug.
            string dir = Path.Combine(RepoRoot, "src", "AgentEyes.Core", "Platform", "Mac");
            Assert.True(Directory.Exists(dir), "the Platform/Mac source directory is gone: " + dir);

            // RECURSIVE on purpose: a non-recursive scan would still find files, so the
            // broken-instrument guard below would not fire while a new Platform/Mac subfolder went
            // unscanned. This scan is also the only detector that reaches an argument list built
            // inline rather than by a swept builder - AvfDevices' device listing is one.
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            Assert.True(files.Length > 0, "no Mac source files were scanned - broken instrument.");

            var violations = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComment(lines[i]);
                    foreach (string option in ForeignOptions)
                    {
                        if (code.Contains("\"-" + option + "\"", StringComparison.Ordinal)
                            || code.Contains("\"" + option + "\"", StringComparison.Ordinal))
                        {
                            violations.Add($"{Path.GetFileName(file)}:{i + 1} -> {lines[i].Trim()}");
                        }
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "an option from another platform's demuxer is in the macOS source (issue #111):\n  "
                + string.Join("\n  ", violations));
        }

        // ---- the sweep -----------------------------------------------------

        /// <summary>One argument list a builder really produced, and which builder produced it.</summary>
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
        /// Invoke every static argument builder on FfmpegArgs (both partials) and AvfStream, and
        /// collect the argument lists they produce. A builder that cannot be invoked is recorded as a
        /// FAILURE rather than skipped.
        /// </summary>
        private static SweepResult Sweep()
        {
            var result = new SweepResult();
            foreach (var type in new[] { typeof(FfmpegArgs), typeof(AvfStream) })
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
                            object? produced = m.Invoke(null, args);
                            var list = ToStringList(produced);
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
        /// BOTH ways, because one of them is what adds the preview output. Null (with a reason) when a
        /// parameter type has no fixture, which fails the sweep rather than shrinking it.
        /// </summary>
        private static List<object?[]>? ParameterSets(MethodInfo m, out string? why)
        {
            why = null;
            var sets = new List<object?[]> { Array.Empty<object?>() };
            foreach (var p in m.GetParameters())
            {
                var values = FixtureValues(p.ParameterType);
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

        private static List<object?>? FixtureValues(Type t)
        {
            if (t == typeof(bool)) return new object?[] { false, true }.ToList();
            if (t == typeof(int)) return new object?[] { 30 }.ToList();
            if (t == typeof(int?)) return new object?[] { 0 }.ToList();
            if (t == typeof(double)) return new object?[] { 1.5 }.ToList();
            if (t == typeof(string)) return new object?[] { "fixture.mp4" }.ToList();
            if (t == typeof(Rectangle)) return new object?[] { new Rectangle(0, 0, 1920, 1080) }.ToList();
            if (t == typeof(Rectangle?)) return new object?[] { new Rectangle(0, 0, 1920, 1080) }.ToList();
            if (t == typeof(Size)) return new object?[] { new Size(1920, 1080) }.ToList();
            if (t.IsEnum) return new object?[] { Enum.GetValues(t).GetValue(0) }.ToList();
            // The post-capture mix/mux builders take these. Suppression and the gate are off because
            // both need state a pure fixture cannot have (an RNNoise model path on disk, a measured
            // gate threshold) and would throw by design. Those builders are swept only to prove the
            // sweep reaches EVERY builder; their filter graphs name no demuxer and so are not
            // AVFoundation lists.
            if (t == typeof(AudioMixOptions))
                return new object?[] { new AudioMixOptions { NoiseSuppression = false, NoiseGate = false } }.ToList();
            if (t.IsClass && t.GetConstructor(Type.EmptyTypes) != null)
                return new object?[] { Activator.CreateInstance(t) }.ToList();
            return null;
        }

        /// <summary>An argument list is an AVFoundation one when it names the avfoundation demuxer.</summary>
        private static bool IsAvFoundation(ArgList list) =>
            list.Args.Any(a => a.Equals("avfoundation", StringComparison.OrdinalIgnoreCase))
            || list.Builder.StartsWith("AvfStream.", StringComparison.Ordinal);

        /// <summary>The foreign option names this argument list actually emits, as bare names.</summary>
        private static List<string> Violations(IReadOnlyList<string> args)
        {
            var found = new List<string>();
            foreach (string option in ForeignOptions)
            {
                if (args.Any(a => a.Equals("-" + option, StringComparison.OrdinalIgnoreCase)
                               || a.Equals(option, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(option);
                }
            }
            return found;
        }

        /// <summary>Drop a line's // comment so a comment NAMING the bug does not read as the bug.</summary>
        private static string StripComment(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                return "";
            }
            int i = line.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? line : line.Substring(0, i);
        }

        /// <summary>The repo root, stamped in at build time by the .csproj, as the Windows suite does
        /// it (RepoSource): a scan can then never silently look at nothing.</summary>
        private static string RepoRoot
        {
            get
            {
                string? root = typeof(MacForeignFfmpegOptionTests).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "The RepoRoot assembly metadata is missing - add it to AgentEyes.Tests.Mac.csproj.");
                if (!Directory.Exists(Path.Combine(root, "src")))
                    throw new InvalidOperationException($"No src directory under the stamped repo root '{root}'.");
                return root;
            }
        }
    }
}
