using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #128: with the default mic settings (denoise + level) post-processing hung forever.
    /// The trigger is a non-finite sample: ONE NaN makes speechnorm spin at 100% CPU without ever
    /// finishing, and ONE NaN into arnndn turns every later sample into NaN. Two fixes, two kinds of
    /// test:
    ///  - the chain now replaces NaN/Inf with 0 at its head (shape tests + a real-ffmpeg run on an
    ///    input that carries a NaN);
    ///  - a post-processing run whose output time stops advancing now FAILS within a bound instead
    ///    of spinning (watchdog unit tests + a real-ffmpeg run of the known-stalling filter).
    /// The real-ffmpeg tests need ffmpeg (the Windows CI job downloads it; FfmpegLocator finds it).
    /// </summary>
    public sealed class MicChainStallTests : IDisposable
    {
        private readonly string _root;
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public MicChainStallTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-128-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private static string Join(System.Collections.Generic.IEnumerable<string> a) => string.Join(" ", a);

        /// <summary>The shipped defaults: denoise on, level on, gate off (mic-only source default).</summary>
        private static AudioMixOptions Defaults(string? model = @"C:\m\bd.rnnn") => new()
        {
            NoiseSuppression = true,
            VoiceLeveling = true,
            NoiseGate = false,
            GateCalibrated = true,
            MicGain = 1.0,
            SystemGain = 0.7,
            RnnoiseModelPath = model,
            MicChannels = 2,   // the NaN fixture is stereo; product paths measure it in AudioMix.Prepare
        };

        // ---- the generated chain -----------------------------------------------------------

        [Fact]
        public void FilterMicToWav_DefaultSettings_SanitizesNonFiniteSamplesBeforeArnndn()
        {
            // Issue #136 moved the mic chain out of the single-pass video mux and into the audio
            // pass of a two-pass mux. The chain is the same chain, so this guard follows it there.
            string s = Join(FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", Defaults()));

            int finite = s.IndexOf(FfmpegArgs.FiniteSamples, StringComparison.Ordinal);
            int arnndn = s.IndexOf("arnndn", StringComparison.Ordinal);
            int level = s.IndexOf("speechnorm", StringComparison.Ordinal);
            Assert.True(finite >= 0, $"no NaN/Inf sanitizer in the default chain: {s}");
            Assert.True(arnndn > finite, $"sanitizer must come BEFORE arnndn (NaN poisons it): {s}");
            Assert.True(level > arnndn, $"speechnorm must still follow arnndn: {s}");
            // It is the very first stage on the mic input.
            Assert.Contains("[0:a]" + FfmpegArgs.FiniteSamples + ",", s);
        }

        [Fact]
        public void MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames()
        {
            // Round two, D3: arnndn on ffmpeg 9.0.2 emits its final, partial frame as 480 NaN
            // samples. The framing stage must sit IMMEDIATELY before arnndn, in every chain that
            // denoises, and must be 480-sample frames with the last one padded (p=1).
            Assert.Equal("asetnsamples=n=480:p=1", FfmpegArgs.ArnndnFraming);
            var o = Defaults();
            foreach (var chain in new[]
            {
                Join(FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", o)),
                Join(FfmpegArgs.MixTwoWav("m.wav", "s.wav", "o.wav", o)),
                Join(FfmpegArgs.MixMicSystemToWav("r.mp4", "s.wav", "a.wav", o)),
            })
            {
                Assert.Contains(FfmpegArgs.ArnndnFraming + ",arnndn=", chain);
                Assert.True(chain.IndexOf(FfmpegArgs.FiniteSamples, StringComparison.Ordinal)
                            < chain.IndexOf(FfmpegArgs.ArnndnFraming, StringComparison.Ordinal), chain);
            }
            var noDenoise = Defaults(); noDenoise.NoiseSuppression = false;
            Assert.DoesNotContain("asetnsamples", Join(FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", noDenoise)));
        }

        [Fact]
        public void MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm()
        {
            var o = Defaults(); o.NoiseSuppression = false;
            string s = Join(FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", o));
            Assert.Contains("[0:a]" + FfmpegArgs.FiniteSamples + ",aformat=channel_layouts=stereo,speechnorm=", s);
        }

        [Fact]
        public void MicChain_AllMixPaths_CarryTheSanitizer()
        {
            var o = Defaults();
            Assert.Contains(FfmpegArgs.FiniteSamples, Join(FfmpegArgs.MixTwoWav("m.wav", "s.wav", "o.wav", o)));
            Assert.Contains(FfmpegArgs.FiniteSamples, Join(FfmpegArgs.FilterMicToWav("r.mp4", "a.wav", o)));
            Assert.Contains(FfmpegArgs.FiniteSamples, Join(FfmpegArgs.MixMicSystemToWav("r.mp4", "s.wav", "a.wav", o)));
        }

        [Fact]
        public void MicChain_NeitherDenoiseNorLevel_OmitsTheSanitizer()
        {
            var o = Defaults(); o.NoiseSuppression = false; o.VoiceLeveling = false;
            string s = Join(FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", o));
            Assert.DoesNotContain("aeval", s);
            Assert.Contains("[0:a]volume=1[m]", s);
        }

        [Fact]
        public void FiniteSamples_IsPerChannelAndKeepsLayout()
        {
            // val(ch) = the sample of the channel this expression runs for; c=same keeps the layout.
            Assert.Contains("isnan(val(ch))", FfmpegArgs.FiniteSamples);
            Assert.Contains("isinf(val(ch))", FfmpegArgs.FiniteSamples);
            Assert.EndsWith(":c=same", FfmpegArgs.FiniteSamples);
        }

        // ---- the watchdog's decision ------------------------------------------------------------

        private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

        [Fact]
        public void Watchdog_AdvancingOutputTime_IsNotStalled()
        {
            var dog = new FfmpegProgressWatchdog(TimeSpan.FromSeconds(10), T0);
            for (int i = 1; i <= 60; i++)
                dog.Observe($"out_time_us={i * 1_000_000}", T0 + TimeSpan.FromSeconds(i));
            Assert.False(dog.IsStalled(T0 + TimeSpan.FromSeconds(65)));
            Assert.Equal(60.0, dog.OutputSeconds);
        }

        [Fact]
        public void Watchdog_FrozenOutputTimeWithProgressStillArriving_IsStalled()
        {
            // The real hang: ffmpeg keeps printing progress blocks, out_time_us never moves.
            var dog = new FfmpegProgressWatchdog(TimeSpan.FromSeconds(10), T0);
            dog.Observe("out_time_us=29930667", T0 + TimeSpan.FromSeconds(1));
            for (int i = 2; i <= 12; i++)
            {
                dog.Observe("total_size=44", T0 + TimeSpan.FromSeconds(i));
                dog.Observe("out_time_us=29930667", T0 + TimeSpan.FromSeconds(i));
                dog.Observe("progress=continue", T0 + TimeSpan.FromSeconds(i));
            }
            Assert.False(dog.IsStalled(T0 + TimeSpan.FromSeconds(11)));   // 10 s frozen: at the limit
            Assert.True(dog.IsStalled(T0 + TimeSpan.FromSeconds(12)));    // 11 s frozen: over it
            Assert.Equal(29.930667, dog.OutputSeconds!.Value, 6);
        }

        [Fact]
        public void Watchdog_NoOutputEver_IsStalledFromStart()
        {
            var dog = new FfmpegProgressWatchdog(TimeSpan.FromSeconds(5), T0);
            dog.Observe("out_time_us=N/A", T0 + TimeSpan.FromSeconds(1));
            dog.Observe("", T0 + TimeSpan.FromSeconds(2));
            dog.Observe(null, T0 + TimeSpan.FromSeconds(3));
            Assert.Null(dog.OutputSeconds);
            Assert.False(dog.IsStalled(T0 + TimeSpan.FromSeconds(5)));
            Assert.True(dog.IsStalled(T0 + TimeSpan.FromSeconds(6)));
        }

        [Fact]
        public void Watchdog_OutputTimeGoingBackwards_DoesNotCountAsProgress()
        {
            var dog = new FfmpegProgressWatchdog(TimeSpan.FromSeconds(5), T0);
            dog.Observe("out_time_us=9000000", T0);
            dog.Observe("out_time_us=1000000", T0 + TimeSpan.FromSeconds(4));
            dog.Observe("out_time_us=9000000", T0 + TimeSpan.FromSeconds(5));
            Assert.True(dog.IsStalled(T0 + TimeSpan.FromSeconds(6)));
        }

        [Fact]
        public void Watchdog_NonPositiveTimeout_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FfmpegProgressWatchdog(TimeSpan.Zero, T0));
        }

        // ---- real ffmpeg ----------------------------------------------------------------------

        /// <summary>
        /// 4,320,500 samples = 90.01 s at 48 kHz, and 4,320,500 % 480 = 20: deliberately NOT a whole
        /// number of arnndn frames. Round one used exactly 90 s (4,320,000 = 9000 x 480), which is
        /// precisely the length that avoids arnndn's own end-of-stream NaN frame on ffmpeg 9.0.2 -
        /// a fixture that passed because it dodged the condition under test (QA, D4).
        /// </summary>
        private const int NotAWholeFrameSamples = 4_320_500;

        /// <summary>A 90.01 s 48 kHz stereo float WAV: speech-band tones, ONE NaN sample at 30 s on channel 0.</summary>
        private string WavWithOneNan(string name)
        {
            Assert.NotEqual(0, NotAWholeFrameSamples % 480);
            string path = Path.Combine(_root, name);
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i",
                "aevalsrc=exprs='if(eq(n,48000*30),0/0,0.2*sin(2*PI*220*t)*sin(2*PI*3*t))|0.2*sin(2*PI*330*t)':s=48000:d=91",
                "-af", $"atrim=end_sample={NotAWholeFrameSamples}",
                "-c:a", "pcm_f32le", path,
            }, "test: wav with one NaN");
            return path;
        }

        /// <summary>
        /// Kill every ffmpeg this machine started after <paramref name="since"/>. Used only on a
        /// test's FAILURE path, when the guard under test did not fire and its ffmpeg would otherwise
        /// spin forever and keep `dotnet test` from returning (QA measured exactly that leak under
        /// mutation B). It can also end a parallel test's ffmpeg - acceptable only because the run is
        /// already failing.
        /// </summary>
        private void KillFfmpegStartedSince(DateTime since)
        {
            foreach (var p in Process.GetProcessesByName("ffmpeg"))
            {
                using (p)
                {
                    try
                    {
                        if (p.StartTime < since) continue;
                        p.Kill(entireProcessTree: true);
                        _out.WriteLine($"killed leaked ffmpeg pid {p.Id}");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        _out.WriteLine($"could not kill ffmpeg pid {p.Id}: {ex.Message}");
                    }
                }
            }
        }

        private string Tone(string name, double seconds)
        {
            string path = Path.Combine(_root, name);
            Ffmpeg.Run(FfmpegArgs.GenerateTone(440, seconds, 0.2, path), "test: tone");
            return path;
        }

        [Fact]
        public void RunWatched_FilterThatStallsOnNan_FailsWithinTheBound()
        {
            // The pre-fix chain's stalling stage on its own, against the trigger, with a short limit.
            string input = WavWithOneNan("nan.wav");
            string output = Path.Combine(_root, "stalled.wav");
            // 10 s, not less: on a loaded 2-core CI runner (the whole suite runs in parallel, other
            // tests start ffmpeg too) a 4 s limit fired before ffmpeg's output time first advanced.
            var limit = TimeSpan.FromSeconds(10);
            var sw = Stopwatch.StartNew();
            DateTime started = DateTime.Now.AddSeconds(-1);

            // Run on a worker and wait a bounded time, so that a guard which does NOT fire fails this
            // test by name instead of hanging the whole suite.
            var run = System.Threading.Tasks.Task.Run(() => Ffmpeg.RunWatched(new[]
            {
                "-y", "-i", input, "-af", "speechnorm=e=4:p=0.95", "-c:a", "pcm_f32le", output,
            }, "test stall", limit));
            bool finished = ((System.IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(60));
            sw.Stop();
            if (!finished) KillFfmpegStartedSince(started);
            Assert.True(finished, "RunWatched was still running after 60 s - the stall guard did not fire");
            var ex = Assert.IsType<UsageException>(run.Exception?.InnerException);
            string msg = ex.Message;   // quoted whole in every failure below - xunit truncates otherwise

            Assert.True(msg.Contains("ffmpeg stalled during test stall") && msg.Contains("limit 10s")
                        && msg.Contains("process killed"), "unexpected stall message: " + msg);
            // Frozen just before the NaN at 30 s: the pre-NaN audio DID flow (so this is the NaN
            // stall, not a run that never started) and the end (90 s) was never reached.
            var at = System.Text.RegularExpressions.Regex.Match(msg, @"at (\d+\.\d)s of output");
            Assert.True(at.Success, "the stall was not frozen at an output time: " + msg);
            double frozenAt = double.Parse(at.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(frozenAt >= 20 && frozenAt <= 30.1, $"frozen at {frozenAt}s, expected just before 30 s: " + msg);
            // Bound: the limit plus start-up and 1 s progress granularity - and nowhere near "forever".
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed.TotalSeconds:F1}s");
        }

        [Fact]
        public void RunWatched_HealthyRun_Completes()
        {
            string input = Tone("ok.wav", 20);
            string output = Path.Combine(_root, "ok-out.wav");
            Ffmpeg.RunWatched(new[] { "-y", "-i", input, "-af", "volume=0.5", output }, "test healthy", TimeSpan.FromSeconds(4));
            Assert.True(new FileInfo(output).Length > 1_000_000);
        }

        [Fact]
        public void MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan()
        {
            // The whole product path (model materialised, chain built, watched run) on the trigger.
            // Before the fix this hangs in speechnorm until the 60 s watchdog fails it. The system
            // input is digital silence, so everything audible in the output is the processed mic.
            string mic = WavWithOneNan("mic.wav");
            string sys = Path.Combine(_root, "sys.wav");
            Ffmpeg.Run(new[] { "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=91",
                    "-af", $"atrim=end_sample={NotAWholeFrameSamples}", "-c:a", "pcm_s16le", sys },
                "test: silent system wav");
            string outWav = Path.Combine(_root, "mixed.wav");
            var o = Defaults(model: null);   // MixWavs materialises the bundled RNNoise model

            var sw = Stopwatch.StartNew();
            AudioMix.MixWavs(mic, sys, outWav, o);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(45), $"took {sw.Elapsed.TotalSeconds:F1}s");
            // Full length, not truncated at the NaN (48 kHz stereo s16 = 192000 B/s).
            Assert.InRange(new FileInfo(outWav).Length, 89L * 192_000, 91L * 192_000);
            // The mic is still there AFTER the NaN at 30 s. Without the sanitizer arnndn turns every
            // sample after the NaN into NaN, which a 16-bit output cannot carry - it would be lost.
            double after = RmsDb(outWav, startSeconds: 40);
            Assert.True(after > -60, $"mic RMS after the NaN point was {after:F1} dB - the mic was lost");
        }

        /// <summary>RMS level (dB) of a file from <paramref name="startSeconds"/> on, via ffmpeg astats.</summary>
        private static double RmsDb(string path, double startSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-hide_banner", "-nostats", "-ss", startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "-i", path, "-af", "astats=measure_perchannel=none", "-f", "null", "-" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var m = System.Text.RegularExpressions.Regex.Match(err, @"RMS level dB: (-?[\d.]+|-inf)");
            Assert.True(m.Success, "astats printed no RMS level - broken instrument: " + err[^Math.Min(400, err.Length)..]);
            return m.Groups[1].Value == "-inf" ? double.NegativeInfinity
                : double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- criterion 11: a failed pass leaves no fragment, and the mux ceiling is not bypassed ----

        [Fact]
        public void RunStage_FailedPass_RemovesThePartialOutput()
        {
            // A fragment at the final path (what a killed mux leaves) and a pass that fails.
            string outPath = Path.Combine(_root, "recording.mp4");
            File.WriteAllText(outPath, "truncated mux output with no moov atom");
            string missing = Path.Combine(_root, "no-such-input.mp4");

            // The OUTCOME is asserted, not the exception type: which error wins is not what this test
            // is about (a type assertion here was a latent CI flake - see the D6 job-object race).
            Assert.ThrowsAny<Exception>(() =>
                AudioMix.RunStage(new[] { "-y", "-i", missing, "-c", "copy", outPath }, outPath, "test failing pass"));

            Assert.False(File.Exists(outPath), "the failed pass left its partial output at the final path");
        }

        [Fact]
        public void RunStage_FailedPassWhosePartialOutputCannotBeRemoved_KeepsTheStageError()
        {
            // Round two, D5: the cleanup File.Delete threw (another process had the file open) and
            // its IOException REPLACED the stall error in the manifest. Hold the fragment open with
            // no delete sharing so the delete must fail, and fail the pass.
            string outPath = Path.Combine(_root, "held.mp4");
            File.WriteAllText(outPath, "fragment another process still has open");
            string missing = Path.Combine(_root, "no-such-input.mp4");

            using (new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var ex = Assert.Throws<UsageException>(() =>
                    AudioMix.RunStage(new[] { "-y", "-i", missing, "-c", "copy", outPath }, outPath, "test held output"));
                Assert.True(ex.Message.StartsWith("ffmpeg failed during test held output", StringComparison.Ordinal),
                    "the stage's own error is not first: " + ex.Message);
                Assert.Contains("could not be removed", ex.Message);
                var inner = Assert.IsType<UsageException>(ex.InnerException);
                Assert.StartsWith("ffmpeg failed during test held output", inner.Message);
            }
        }

        [Fact]
        public void RunStage_OutputStopsAdvancing_IsFailedByTheWatchdog()
        {
            // Round two, D4: QA's mutation F (RunStage calling the UNWATCHED Ffmpeg.Run) left the
            // whole suite green - nothing tested the watchdog's WIRING into the product stage runner.
            // This drives the product runner itself. The input is read at real time (-re) and the
            // first 28 s are trimmed away, so ffmpeg writes no output for 28 s and then finishes ON
            // ITS OWN at ~30 s: with the watchdog (5 s bound) the stage fails at ~6 s; without it the
            // stage SUCCEEDS at ~30 s and this test fails by name - and nothing is left running.
            string output = Path.Combine(_root, "frozen.wav");
            var sw = Stopwatch.StartNew();
            var run = System.Threading.Tasks.Task.Run(() =>
            {
                Ffmpeg.PostProcessStallTimeoutOverride.Value = TimeSpan.FromSeconds(5);
                AudioMix.RunStage(new[]
                {
                    "-y", "-re", "-f", "lavfi", "-i", "sine=f=300:r=48000:d=30", "-af", "atrim=start=28", output,
                }, output, "test frozen stage");
            });
            bool finished = ((System.IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(90));
            sw.Stop();
            Assert.True(finished, "RunStage was still running after 90 s");
            Assert.True(run.Exception != null,
                $"RunStage COMPLETED after {sw.Elapsed.TotalSeconds:F0}s on a stage whose output did not advance for 28 s - " +
                "the product stage runner is not watched");
            var ex = Assert.IsType<UsageException>(run.Exception!.InnerException);
            string msg = ex.Message;
            Assert.True(msg.Contains("ffmpeg stalled during test frozen stage") && msg.Contains("limit 5s")
                        && msg.Contains("process killed"), "unexpected stall message: " + msg);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(25), $"took {sw.Elapsed.TotalSeconds:F1}s - not the 5 s bound");
            Assert.False(File.Exists(output), "the stalled stage left its partial output behind");
        }

        // ---- round two, D3: arnndn's own end-of-stream NaN frame ------------------------------

        /// <summary>The shipped RNNoise model, materialised, as an escaped filter path.</summary>
        private static string ModelFilterPath() =>
            RnnoiseModel.Ensure().Replace('\\', '/').Replace(":", "\\:");

        /// <summary>Run <paramref name="filter"/> over a 300 Hz tone of exactly <paramref name="samples"/>
        /// samples (s16, <paramref name="layout"/>) and count the non-finite samples in the float
        /// output. Counted from the raw f32 bytes - not with astats, which the Lead flagged as a
        /// possibly weak instrument for this frame.</summary>
        /// The exit code is returned rather than thrown: unframed arnndn on 9.0.2 reads past its
        /// buffer on the partial frame, which usually yields NaN and occasionally CRASHES ffmpeg
        /// (measured 1 in 40, exit 0xC0000005) - for the bare run that is the defect reproducing.
        private (int exit, long total, long nonFinite) NonFiniteAfter(string filter, int samples, string layout)
        {
            string raw = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".f32");
            var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffmpeg(), RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[]
            {
                "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=300:r=48000:d=20",
                "-af", $"atrim=end_sample={samples},aformat=sample_fmts=s16:channel_layouts={layout},{filter}",
                "-f", "f32le", "-c:a", "pcm_f32le", raw,
            })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return (p.ExitCode, 0, 0);
            byte[] b = File.ReadAllBytes(raw);
            long bad = 0;
            for (int i = 0; i + 4 <= b.Length; i += 4)
                if (!float.IsFinite(BitConverter.ToSingle(b, i))) bad++;
            return (0, b.Length / 4, bad);
        }

        private static string FfmpegVersion()
        {
            var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffmpeg(), RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi)!;
            string first = p.StandardOutput.ReadLine() ?? "(no output)";
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return first;
        }

        [Theory]
        [InlineData("mono")]
        [InlineData("stereo")]
        public void ArnndnFraming_PartialFinalFrame_ArnndnEmitsNoNonFiniteSample(string layout)
        {
            // 480,123 samples: 1000 whole frames + 123. QA's one-line repro length.
            const int samples = 480_123;
            string model = ModelFilterPath();
            string version = FfmpegVersion();

            // Issue #128 round three (R2-2): the defect is an out-of-bounds read, so whether ONE bare
            // run shows it is per-run chance - QA measured 2 of 30 bare mono runs clean on 9.0.2, and
            // a single-shot requirement failed CI on a correct build (push run 43635548525). The bare
            // run is therefore repeated until it reproduces, up to BareRunLimit times: at the
            // measured clean rate (2/30) ten clean runs in a row is about 2e-12. On an ffmpeg WITHOUT
            // the defect (8.1.1) every run is clean, so the CI requirement below still fails there,
            // after BareRunLimit runs, exactly as before. What counts as reproducing is unchanged: a
            // non-finite sample, or ffmpeg dying on it.
            const int BareRunLimit = 10;
            var bareRuns = new List<(int exit, long total, long nonFinite)>();
            (int exit, long total, long nonFinite) bare;
            do
            {
                bare = NonFiniteAfter($"arnndn=m='{model}'", samples, layout);
                bareRuns.Add(bare);
            }
            while (bare.nonFinite == 0 && bare.exit == 0 && bareRuns.Count < BareRunLimit);
            bool bareReproduced = bare.nonFinite > 0 || bare.exit != 0;

            var framed = NonFiniteAfter($"{FfmpegArgs.ArnndnFraming},arnndn=m='{model}'", samples, layout);
            _out.WriteLine($"{version} | {layout} | {samples} samples (% 480 = {samples % 480}) | " +
                           $"arnndn alone, {bareRuns.Count} run(s): " +
                           string.Join("; ", bareRuns.Select(r => $"exit {r.exit}, {r.nonFinite} non-finite of {r.total}")) +
                           $" | framed: exit {framed.exit}, {framed.nonFinite} of {framed.total}");

            // The instrument must work: arnndn rounds its output up to a whole frame either way.
            Assert.True(framed.exit == 0, $"framed arnndn failed with exit {framed.exit}");
            Assert.True(framed.total > 0, "framed arnndn produced no samples - broken instrument");
            Assert.Equal(0, framed.nonFinite);
            // The framing must not change the length (arnndn rounds up to a whole frame either way).
            // Checked against every bare run that survived; at least one survives in practice (a
            // crash is ~1 in 40), and a sweep with none is said, not silently skipped.
            var survived = bareRuns.Where(r => r.exit == 0).ToList();
            foreach (var r in survived)
                Assert.True(framed.total == r.total,
                    $"output lengths differ (bare {r.total}, framed {framed.total}) - the framing changed the length");
            if (survived.Count == 0)
                _out.WriteLine("length check not made: every bare run crashed");

            // A test that can only pass proves nothing, so record whether THIS ffmpeg reproduces the
            // defect. 9.0.2 (gyan.dev, the shipped build) does; 8.1.1 does not. On CI it MUST
            // reproduce - if a newer ffmpeg stops emitting the frame, this fails and says so, rather
            // than the D3 tests quietly testing nothing.
            if (Environment.GetEnvironmentVariable("CI") == "true")
                Assert.True(bareReproduced,
                    $"{version} did not emit arnndn's end-of-stream NaN frame in {bareRuns.Count} bare run(s) - " +
                    "the D3 tests no longer exercise the defect on CI");
        }

        [Fact]
        public void ProcessVideoMic_DefaultSettingsFiniteS16MonoNotAWholeFrame_Completes()
        {
            // QA's product-path reproduction of D3, shrunk: a finite, NaN-free s16 mono mic (the
            // shape both Windows dshow and the macOS capture produce) whose length is not a whole
            // number of arnndn frames. On ffmpeg 9.0.2 without the framing this stalls at the very
            // end and the watchdog fails it ("output stopped advancing at 89.9s").
            string raw = Path.Combine(_root, "raw.mp4");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=11",
                "-f", "lavfi", "-i", "sine=f=300:r=48000:d=11",
                "-filter_complex", "[1:a]atrim=end_sample=480123,aformat=sample_fmts=s16:channel_layouts=mono,volume=0.5[a]",
                "-map", "0:v", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le",
                "-f", "mov", raw,
            }, "test: finite s16 mono capture");
            string outMp4 = Path.Combine(_root, "recording.mp4");
            var o = Defaults(model: null);

            var sw = Stopwatch.StartNew();
            var run = System.Threading.Tasks.Task.Run(() =>
            {
                Ffmpeg.PostProcessStallTimeoutOverride.Value = TimeSpan.FromSeconds(15);
                AudioMix.ProcessVideoMic(raw, outMp4, o);
            });
            bool finished = ((System.IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(90));
            sw.Stop();
            Assert.True(finished, "ProcessVideoMic was still running after 90 s");
            Assert.True(run.Exception == null,
                $"the default chain failed on finite NaN-free input ({FfmpegVersion()}): {run.Exception?.InnerException?.Message}");
            Assert.True(new FileInfo(outMp4).Length > 10_000, "no final media written");
            _out.WriteLine($"{FfmpegVersion()}: completed in {sw.Elapsed.TotalSeconds:F1}s");
        }

        // ---- round two, D6: a fast-failing ffmpeg must report ffmpeg's error, not the job tie's ----

        [Fact]
        public void TieToLifetime_ProcessAlreadyExited_DoesNotReplaceTheRealResult()
        {
            // Deterministic version of the race the #136 seat hit on CI: the child is ALREADY gone
            // when it is tied to the job object.
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("exit 3");
            using var p = Process.Start(psi)!;
            p.WaitForExit();

            // The fixture must reproduce the hazard, or this test proves nothing: a raw Assign of an
            // exited process throws.
            Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => AgentEyes.AlwaysOn.KillOnCloseJob.Assign(p));

            Ffmpeg.TieToLifetime(p, "test exited child");   // must not throw
            Assert.Equal(3, p.ExitCode);
        }

        [Fact]
        public void RunCore_TiesItsChildOnlyThroughTieToLifetime_InTheCompiledProduct()
        {
            // The test above proves TieToLifetime; this proves the runner USES it. Checked on the
            // compiled assembly, not the source text: in Ffmpeg, the only caller of the raw
            // KillOnCloseJob.Assign is TieToLifetime, and RunCore calls TieToLifetime. (Limit: this
            // sees Ffmpeg's own methods only - another class assigning ffmpeg children to the job
            // directly, as ContinuousRecorder does for capture, is outside what it checks.)
            var assigns = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c == "AgentEyes.AlwaysOn.KillOnCloseJob::Assign");
            var ties = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c == "AgentEyes.Video.Ffmpeg::TieToLifetime");

            Assert.NotEmpty(assigns);   // empty = a broken instrument, not a pass
            var inFfmpeg = assigns.Where(s => s.Method.StartsWith("AgentEyes.Video.Ffmpeg::", StringComparison.Ordinal)).ToList();
            Assert.True(inFfmpeg.Count == 1 && inFfmpeg[0].Method == "AgentEyes.Video.Ffmpeg::TieToLifetime",
                "a raw KillOnCloseJob.Assign in Ffmpeg outside TieToLifetime: " + string.Join(", ", inFfmpeg.Select(s => s.Method)));
            Assert.Contains(ties, s => s.Method == "AgentEyes.Video.Ffmpeg::RunCore");
        }

        // ---- round two, D7: mono mic + stereo system - the shipped default for a mixed recording ----

        [Fact]
        public void MicChain_SanitizerIsPinnedToTheMeasuredMicLayout()
        {
            var mono = Defaults(); mono.MicChannels = 1;
            var stereo = Defaults(); stereo.MicChannels = 2;
            var six = Defaults(); six.MicChannels = 6;
            Assert.Contains(FfmpegArgs.FiniteSamples + ",aformat=channel_layouts=mono,",
                Join(FfmpegArgs.MixTwoWav("m.wav", "s.wav", "o.wav", mono)));
            Assert.Contains(FfmpegArgs.FiniteSamples + ",aformat=channel_layouts=stereo,",
                Join(FfmpegArgs.MixMicSystemToWav("r.mp4", "s.wav", "a.wav", stereo)));
            Assert.Contains(FfmpegArgs.FiniteSamples + ",aformat=channel_layouts=6c,",
                Join(FfmpegArgs.FilterMicToWav("r.mp4", "a.wav", six)));

            var unmeasured = Defaults(); unmeasured.MicChannels = null;
            var ex = Assert.Throws<UsageException>(() => FfmpegArgs.MixTwoWav("m.wav", "s.wav", "o.wav", unmeasured));
            Assert.Contains("channel count has not been measured", ex.Message);
        }

        /// <summary>A short s16 WAV of <paramref name="channels"/> channels: a tone, or silence.</summary>
        private string S16Wav(string name, int channels, bool silent, double seconds = 6)
        {
            string path = Path.Combine(_root, name);
            string src = silent ? $"anullsrc=r=48000:cl={(channels == 1 ? "mono" : "stereo")}:d={seconds}"
                                : $"sine=f=300:r=48000:d={seconds}";
            Ffmpeg.Run(new[] { "-y", "-f", "lavfi", "-i", src, "-af", "volume=0.5",
                "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", path },
                "test: s16 wav");
            return path;
        }

        [Fact]
        public void MixWavs_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg()
        {
            // The configuration the product ships and round one's stereo/stereo fixtures never ran.
            // Unpinned, ffmpeg segfaults (exit 139) in 8 of 10 runs, so ONE run could pass by luck:
            // five runs miss it with probability about 0.2^5 = 0.03%.
            string mic = S16Wav("mic-mono.wav", 1, silent: false);
            string sys = S16Wav("sys-stereo.wav", 2, silent: true);
            for (int i = 0; i < 5; i++)
            {
                string outWav = Path.Combine(_root, $"mixed-{i}.wav");
                var o = Defaults(model: null); o.MicChannels = null;   // the product path measures it
                AudioMix.MixWavs(mic, sys, outWav, o);
                Assert.Equal(1, o.MicChannels);
                Assert.True(new FileInfo(outWav).Length > 100_000, $"run {i}: no mixed output");
            }
        }

        [Fact]
        public void MuxVideoMixed_DefaultSettingsMonoMicStereoSystem_DoesNotCrashFfmpeg()
        {
            // The same pair through the VIDEO mixed path: the mic is the mono track inside raw.mp4.
            string raw = Path.Combine(_root, "raw-mono.mp4");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=6",
                "-f", "lavfi", "-i", "sine=f=300:r=48000:d=6",
                "-map", "0:v", "-map", "1:a", "-ac", "1", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le",
                "-f", "mov", raw,
            }, "test: video with a mono mic track");
            string sys = S16Wav("sys-stereo-v.wav", 2, silent: true);
            for (int i = 0; i < 5; i++)
            {
                string outMp4 = Path.Combine(_root, $"mixed-{i}.mp4");
                var o = Defaults(model: null); o.MicChannels = null;
                AudioMix.MuxVideoMixed(raw, sys, outMp4, o);
                Assert.Equal(1, o.MicChannels);
                Assert.True(new FileInfo(outMp4).Length > 10_000, $"run {i}: no muxed output");
            }
        }

        private string PendingMuxRecording(int muxAttempts)
        {
            string dir = Path.Combine(_root, "rec-" + muxAttempts);
            Directory.CreateDirectory(dir);
            var m = new Manifest
            {
                Mode = "video",
                CreatedUtc = "2026-11-14T17:00:00Z",
                VideoFile = "recording.mp4",
                PendingMux = new Manifest.PendingMuxInfo { Mode = "video", Source = "mic", RawVideo = "raw.mp4", FinalFile = "recording.mp4" },
            };
            if (muxAttempts > 0)
                m.PostProcessing[PostStage.Mux] = new Manifest.PostStageRecord
                {
                    State = PostStageState.Failed, Attempts = muxAttempts,
                    Error = "ffmpeg stalled during process mic audio: output stopped advancing",
                };
            ManifestStore.Replace(dir, m);
            return dir;
        }

        [Fact]
        public void RefuseExhaustedMux_AttemptsUsedUp_ThrowsWithCountAndLastError()
        {
            string dir = PendingMuxRecording(PostRecordingState.MaxMuxAttempts);
            var ex = Assert.Throws<UsageException>(() => Package.RefuseExhaustedMux(dir));
            Assert.Contains($"failed {PostRecordingState.MaxMuxAttempts} time(s)", ex.Message);
            Assert.Contains("ffmpeg stalled during process mic audio", ex.Message);
        }

        [Fact]
        public void RefuseExhaustedMux_AttemptsRemaining_DoesNotThrow()
        {
            Package.RefuseExhaustedMux(PendingMuxRecording(PostRecordingState.MaxMuxAttempts - 1));
            Package.RefuseExhaustedMux(PendingMuxRecording(0));
        }
    }
}
