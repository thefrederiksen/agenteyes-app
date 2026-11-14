using System;
using System.Diagnostics;
using System.IO;
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

        public MicChainStallTests()
        {
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
        };

        // ---- the generated chain -----------------------------------------------------------

        [Fact]
        public void FilterVideoMic_DefaultSettings_SanitizesNonFiniteSamplesBeforeArnndn()
        {
            string s = Join(FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", Defaults()));

            int finite = s.IndexOf(FfmpegArgs.FiniteSamples, StringComparison.Ordinal);
            int arnndn = s.IndexOf("arnndn", StringComparison.Ordinal);
            int level = s.IndexOf("speechnorm", StringComparison.Ordinal);
            Assert.True(finite >= 0, $"no NaN/Inf sanitizer in the default chain: {s}");
            Assert.True(arnndn > finite, $"sanitizer must come BEFORE arnndn (NaN poisons it): {s}");
            Assert.True(level > arnndn, $"speechnorm must still follow arnndn: {s}");
            // It is the very first stage on the mic input.
            Assert.Contains("[0:a]" + FfmpegArgs.FiniteSamples + ",arnndn=", s);
        }

        [Fact]
        public void MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm()
        {
            var o = Defaults(); o.NoiseSuppression = false;
            string s = Join(FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", o));
            Assert.Contains("[0:a]" + FfmpegArgs.FiniteSamples + ",speechnorm=", s);
        }

        [Fact]
        public void MicChain_AllMixPaths_CarryTheSanitizer()
        {
            var o = Defaults();
            Assert.Contains(FfmpegArgs.FiniteSamples, Join(FfmpegArgs.MixTwoWav("m.wav", "s.wav", "o.wav", o)));
            Assert.Contains(FfmpegArgs.FiniteSamples, Join(FfmpegArgs.MuxVideoMixMicSystem("r.mp4", "s.wav", "o.mp4", o)));
        }

        [Fact]
        public void MicChain_NeitherDenoiseNorLevel_OmitsTheSanitizer()
        {
            var o = Defaults(); o.NoiseSuppression = false; o.VoiceLeveling = false;
            string s = Join(FfmpegArgs.FilterVideoMic("raw.mp4", "out.mp4", o));
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

        /// <summary>A 90 s 48 kHz stereo float WAV: speech-band tones, ONE NaN sample at 30 s on channel 0.</summary>
        private string WavWithOneNan(string name, double seconds = 90)
        {
            string path = Path.Combine(_root, name);
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i",
                $"aevalsrc=exprs='if(eq(n,48000*30),0/0,0.2*sin(2*PI*220*t)*sin(2*PI*3*t))|0.2*sin(2*PI*330*t)':s=48000:d={seconds}",
                "-c:a", "pcm_f32le", path,
            }, "test: wav with one NaN");
            return path;
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

            // Run on a worker and wait a bounded time, so that a guard which does NOT fire fails this
            // test by name instead of hanging the whole suite.
            var run = System.Threading.Tasks.Task.Run(() => Ffmpeg.RunWatched(new[]
            {
                "-y", "-i", input, "-af", "speechnorm=e=4:p=0.95", "-c:a", "pcm_f32le", output,
            }, "test stall", limit));
            bool finished = ((System.IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(60));
            sw.Stop();
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
            Ffmpeg.Run(new[] { "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=90", "-c:a", "pcm_s16le", sys },
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

            Assert.Throws<UsageException>(() =>
                AudioMix.RunStage(new[] { "-y", "-i", missing, "-c", "copy", outPath }, outPath, "test failing pass"));

            Assert.False(File.Exists(outPath), "the failed pass left its partial output at the final path");
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
