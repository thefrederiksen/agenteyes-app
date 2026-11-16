using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #146: a microphone that captures at 44.1 kHz made every default-settings recording
    /// stall in post-processing on ffmpeg 9.0.2. arnndn accepts only 48 kHz, so ffmpeg inserted its
    /// own resampler at arnndn's input - AFTER issue #128's 480-sample framing stage - and that
    /// resampler re-chunked the audio and undid the framing. The fix puts an explicit 48 kHz resample
    /// AHEAD of the framing. These tests pin that ORDER three ways:
    ///  - the generated chain: resample, framing, arnndn, adjacent and in that order (pure, in
    ///    tests/Shared/MicChainOrderTests.cs, so it runs on all three platforms' CI);
    ///  - ffmpeg's own executed graph for a 44.1 kHz source: the link into arnndn comes straight from
    ///    the framing stage at 48000 Hz, not from an auto-inserted resampler (this is the check that
    ///    sees the mechanism on ANY ffmpeg version, including ones where it does not stall);
    ///  - the product path on a 44.1 kHz mic: it completes, and denoise and leveling are still
    ///    measurably applied (a chain that completes because a stage stopped working is not a fix).
    /// EVERY test here launches a real ffmpeg, so every one is OPT-IN (AGENTEYES_FFMPEG_TESTS=1) and is
    /// skipped in the default gate - see FfmpegOptInAttributes.cs. Set QA_RECORD_FFMPEG_DIR to the
    /// shipped ffmpeg 9.0.2 to exercise the version that stalls.
    /// </summary>
    public sealed class MicChainSampleRateTests : IDisposable
    {
        private readonly string _root;
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public MicChainSampleRateTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-146-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>The shipped defaults: denoise on, level on, gate off.</summary>
        private static AudioMixOptions Defaults(string? model = @"C:\m\bd.rnnn") => new()
        {
            NoiseSuppression = true,
            VoiceLeveling = true,
            NoiseGate = false,
            GateCalibrated = true,
            MicGain = 1.0,
            SystemGain = 0.7,
            RnnoiseModelPath = model,
            MicChannels = 2,   // the fixtures below are stereo, like the dshow capture that failed
        };

        // The pure ORDER assertions (resample, framing, arnndn - adjacent, once) live in
        // tests/Shared/MicChainOrderTests.cs so they run on the Linux and macOS CI jobs too. This file
        // holds the checks that need a real ffmpeg.

        // ---- ffmpeg's own executed graph ---------------------------------------------------------

        /// <summary>A stereo s16 WAV of a 300 Hz tone at <paramref name="rate"/>, <paramref name="samples"/>
        /// samples long - the shape of a dshow microphone capture.</summary>
        private string ToneWav(int rate, int samples, double volume = 0.5)
        {
            string wav = Path.Combine(_root, $"tone{rate}_{samples}_{Guid.NewGuid():N}.wav");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", $"sine=f=300:r={rate}:d=30",
                "-af", $"atrim=end_sample={samples},aformat=sample_fmts=s16:channel_layouts=stereo,volume={Inv(volume)}",
                "-c:a", "pcm_s16le", wav,
            }, "test: tone fixture");
            return wav;
        }

        private static string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Run <paramref name="args"/> with ffmpeg's executed-graph dump on, and return the
        /// dump. A run that does not finish inside the bound is killed and fails the test, rather
        /// than hanging it - on ffmpeg 9.0.2 the unfixed chain stalls on exactly this input.</summary>
        private string ExecutedGraph(System.Collections.Generic.IReadOnlyList<string> args)
        {
            string dump = Path.Combine(_root, "graph.json");
            var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffmpeg(), RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-print_graphs_format", "json", "-print_graphs_file", dump })
                psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            bool done = p.WaitForExit(60_000);
            if (!done)
            {
                p.Kill(entireProcessTree: true);
                Assert.Fail("ffmpeg did not finish the default mic chain on a 44.1 kHz source within 60 s - stalled");
            }
            Assert.True(p.ExitCode == 0, $"ffmpeg exited {p.ExitCode} on the default mic chain: {err.Result}");
            Assert.True(File.Exists(dump), "ffmpeg wrote no graph dump - broken instrument");
            return File.ReadAllText(dump);
        }

        /// <summary>The source filter id and the sample rate of the (single) input link of the first
        /// filter named <paramref name="filterName"/> in a print_graphs JSON dump. Parsed with a regex
        /// rather than a JSON reader because the dump repeats the "sample_rate" key in one object.</summary>
        private static (string Source, string Rate) InputLink(string json, string filterName)
        {
            var m = Regex.Match(json,
                "\"filter_name\":\\s*\"" + Regex.Escape(filterName) + "\".*?\"filter_inputs\".*?"
                + "\"source_filter_id\":\\s*\"(?<src>[^\"]+)\".*?\"sample_rate\":\\s*\"(?<rate>\\d+)\"",
                RegexOptions.Singleline);
            Assert.True(m.Success, $"no '{filterName}' filter with an input link in the graph dump - broken instrument");
            return (m.Groups["src"].Value, m.Groups["rate"].Value);
        }

        [FfmpegOptInTheory]
        [InlineData(44100)]   // the Windows dshow capture that stalled (issue #146)
        [InlineData(16000)]   // AVFoundation was observed delivering 16 kHz on macOS
        [InlineData(22050)]
        [InlineData(48000)]   // already arnndn's rate: the resample is a pass-through
        public void MicChain_AnySourceRate_ArnndnIsFedStraightFromTheFramingAt48k(int sourceRate)
        {
            // Stereo s16, the shape of a microphone capture. The chain is shared by every
            // platform's post-processing, so this is the one place a non-48 kHz mic from ANY
            // platform reaches arnndn. 480,123 samples is not a whole number of 480-sample frames.
            string mic = ToneWav(sourceRate, 480_123);
            var o = Defaults(model: RnnoiseModel.Ensure());
            var args = FfmpegArgs.FilterMicToWav(mic, Path.Combine(_root, "out.wav"), o);

            string json = ExecutedGraph(args);
            var intoArnndn = InputLink(json, "arnndn");
            var intoFraming = InputLink(json, "asetnsamples");
            _out.WriteLine($"{sourceRate} Hz source | into arnndn: from {intoArnndn.Source} at {intoArnndn.Rate} Hz; " +
                           $"into asetnsamples: from {intoFraming.Source} at {intoFraming.Rate} Hz");

            // The mechanism, stated as a presence: arnndn's input comes from the framing stage itself.
            // Before the fix it came from "auto_aresample_0", which ffmpeg inserted AFTER the framing.
            Assert.Contains("asetnsamples", intoArnndn.Source);
            Assert.Equal("48000", intoArnndn.Rate);
            // ...and the framing already receives 48 kHz, from OUR resample stage.
            Assert.Contains("aresample", intoFraming.Source);
            Assert.DoesNotContain("auto_", intoFraming.Source);
            Assert.Equal("48000", intoFraming.Rate);
        }

        // ---- the product path on a 44.1 kHz mic ------------------------------------------------

        private string Raw44kVideo(string audioFilter)
        {
            string raw = Path.Combine(_root, "raw_" + Guid.NewGuid().ToString("N") + ".mp4");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=11",
                "-f", "lavfi", "-i", audioFilter,
                "-filter_complex", "[1:a]atrim=end_sample=441123,aformat=sample_fmts=s16:channel_layouts=stereo[a]",
                "-map", "0:v", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac",
                "-f", "mp4", raw,
            }, "test: 44.1 kHz capture");
            return raw;
        }

        [FfmpegOptInFact]
        public void ProcessVideoMic_DefaultSettings44kStereoMic_Completes()
        {
            // The issue #146 capture, shrunk: an AAC 44.1 kHz stereo mic track in an MP4, exactly
            // what the Windows dshow recorder writes for a mic whose native rate is 44.1 kHz. On
            // ffmpeg 9.0.2 without the resample this stalls at the very end (measured 10 of 10).
            string raw = Raw44kVideo("sine=f=300:r=44100:d=11,volume=0.5");
            string outMp4 = Path.Combine(_root, "recording.mp4");
            var o = Defaults(model: null);

            var sw = Stopwatch.StartNew();
            var run = System.Threading.Tasks.Task.Run(() =>
            {
                Ffmpeg.PostProcessStallTimeoutOverride.Value = TimeSpan.FromSeconds(15);
                AudioMix.ProcessVideoMic(raw, outMp4, o);
            });
            bool finished = ((IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(90));
            sw.Stop();
            Assert.True(finished, "ProcessVideoMic was still running after 90 s");
            Assert.True(run.Exception == null,
                $"the default chain failed on a 44.1 kHz mic ({Version()}): {run.Exception?.InnerException?.Message}");
            Assert.True(new FileInfo(outMp4).Length > 10_000, "no final media written");
            _out.WriteLine($"{Version()}: completed in {sw.Elapsed.TotalSeconds:F1}s");
        }

        /// <summary>Overall RMS of a file's first audio stream, in dBFS, measured from the decoded
        /// float samples (not from a filter's report).</summary>
        private double RmsDb(string media)
        {
            string raw = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".f32");
            Ffmpeg.Run(new[] { "-y", "-i", media, "-map", "0:a:0", "-f", "f32le", "-c:a", "pcm_f32le", raw }, "test: decode");
            byte[] b = File.ReadAllBytes(raw);
            Assert.True(b.Length >= 4 * 48_000, $"decoded only {b.Length} bytes - broken instrument");
            double sum = 0;
            long n = 0;
            for (int i = 0; i + 4 <= b.Length; i += 4)
            {
                float s = BitConverter.ToSingle(b, i);
                Assert.True(float.IsFinite(s), "a non-finite sample in the processed output");
                sum += (double)s * s;
                n++;
            }
            return 10.0 * Math.Log10(sum / n + 1e-20);
        }

        /// <summary>Run the shipped pass-1 mic builder over <paramref name="mic"/> and return the
        /// processed WAV. Run through the product's stage runner, under its stall watchdog with a
        /// 15 s bound, so a chain that stalls (the issue #146 defect, on ffmpeg 9.0.2) FAILS this
        /// test in seconds instead of hanging the run - measured: unwatched, a swapped chain spun
        /// for over nine minutes.</summary>
        private string RunMicChain(string mic, AudioMixOptions o)
        {
            string outWav = Path.Combine(_root, "p_" + Guid.NewGuid().ToString("N") + ".wav");
            Ffmpeg.PostProcessStallTimeoutOverride.Value = TimeSpan.FromSeconds(15);
            AudioMix.RunStage(FfmpegArgs.FilterMicToWav(mic, outWav, o), outWav, "test: mic chain");
            return outWav;
        }

        [FfmpegOptInFact]
        public void MicChain_44kNoise_DenoiseIsStillApplied()
        {
            // Pure noise at 44.1 kHz through the FULL default chain, against the same chain with
            // only denoise turned off. Measured on 9.0.2: -45.6 dB vs -15.7 dB (30 dB of suppression,
            // even with the leveling stage boosting what is left). 12 dB is the floor asserted.
            string noise = Path.Combine(_root, "noise44.wav");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "anoisesrc=a=0.1:r=44100:d=6",
                "-af", "atrim=end_sample=221123,aformat=sample_fmts=s16:channel_layouts=stereo",
                "-c:a", "pcm_s16le", noise,
            }, "test: noise fixture");
            string model = RnnoiseModel.Ensure();
            var on = Defaults(model);
            var off = Defaults(model); off.NoiseSuppression = false;

            double withDenoise = RmsDb(RunMicChain(noise, on));
            double withoutDenoise = RmsDb(RunMicChain(noise, off));
            _out.WriteLine($"{Version()}: noise RMS with denoise {withDenoise:F1} dB, without {withoutDenoise:F1} dB");
            Assert.True(withoutDenoise - withDenoise >= 12.0,
                $"denoise removed only {withoutDenoise - withDenoise:F1} dB of noise ({withDenoise:F1} vs {withoutDenoise:F1}) - is it still applied?");
        }

        [FfmpegOptInFact]
        public void MicChain_44kQuietVoice_LevelingIsStillApplied()
        {
            // A quiet 44.1 kHz signal through the FULL default chain, against the same chain with
            // only leveling turned off. speechnorm e=4 lifts it up to 4x (+12 dB); measured +12.0 dB.
            string quiet = ToneWav(44100, 221_123, volume: 0.05);
            string model = RnnoiseModel.Ensure();
            var on = Defaults(model);
            var off = Defaults(model); off.VoiceLeveling = false;

            double withLevel = RmsDb(RunMicChain(quiet, on));
            double withoutLevel = RmsDb(RunMicChain(quiet, off));
            _out.WriteLine($"{Version()}: quiet RMS with leveling {withLevel:F1} dB, without {withoutLevel:F1} dB");
            Assert.True(withLevel - withoutLevel >= 6.0,
                $"leveling raised a quiet signal by only {withLevel - withoutLevel:F1} dB ({withLevel:F1} vs {withoutLevel:F1}) - is it still applied?");
        }

        private static string Version()
        {
            var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffmpeg(), RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi)!;
            string first = p.StandardOutput.ReadLine() ?? "(no output)";
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return first;
        }
    }
}
