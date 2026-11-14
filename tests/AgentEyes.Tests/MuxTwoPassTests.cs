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
    /// Issue #136: the post-capture video mux runs as TWO ffmpeg passes - the audio filter graph
    /// alone to an intermediate WAV, then the video copy plus that finished WAV with no filter graph.
    /// A single command carrying both deadlocks on macOS: the copied video is read and queued almost
    /// instantly while the mic is still being filtered, the muxing queue hits its default bound, and
    /// ffmpeg spins at ~99% CPU past the end of the content without finalizing.
    ///
    /// FfmpegMuxShapeTests (tests/Shared, compiled into all three suites) holds the SHAPE
    /// invariant. This file is the ORCHESTRATION: that the two passes really run in order, that the
    /// intermediate is cleaned up however the mux ends, and that both passes keep the issue #128
    /// partial-output removal - a fragment left at the final path is what later made the thumbnail
    /// stage fail on "moov atom not found".
    ///
    /// These tests need ffmpeg (the Windows CI job downloads it; FfmpegLocator finds it).
    ///
    /// WHAT THEY DO NOT COVER: they do not prove the mux is FAST. A tone and a 5-second test pattern
    /// cannot lose the race these tests would have to win, so the deadlock itself is not reproduced
    /// here - reproducing it needs about a minute of real speech through arnndn plus a multi-megabit
    /// video, which is a minutes-long audible run, not a unit test. The runtime proof for that is the
    /// mux of 2026-11-14_111731_video recorded in docs/cencon/proof/issue-136/handoff.md.
    /// </summary>
    public sealed class MuxTwoPassTests : IDisposable
    {
        private readonly string _root;

        public MuxTwoPassTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-136-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>The shipped mic defaults: denoise on, level on, gate off.</summary>
        private static AudioMixOptions Defaults() => new()
        {
            NoiseSuppression = true,
            VoiceLeveling = true,
            NoiseGate = false,
            GateCalibrated = true,
            MicGain = 1.0,
            SystemGain = 0.7,
        };

        private string Intermediate => Path.Combine(_root, AudioMix.ProcessedAudioFileName);

        /// <summary>
        /// A short H.264 test pattern with a tone on its audio track - the shape of raw.mp4 on
        /// Windows and Linux, where the mic is captured into the video itself.
        ///
        /// The mic track is STEREO at 48 kHz on purpose, and it is not an incidental choice. ffmpeg
        /// 9.0.2 SEGFAULTS (exit 139) when issue #128's aeval sanitizer runs on a MONO stream that is
        /// then up-mixed to stereo - which amix does when it is fed a mono mic and a stereo system
        /// capture. Measured on devthrottle-mac-mini while implementing issue #136, and reported
        /// against #128: it is that chain's fault, identical before and after the two-pass split, and
        /// out of scope here. A mono fixture would therefore fail these tests for somebody else's
        /// reason. If that crash is fixed, make the mic track mono again - mono is the common mic.
        /// </summary>
        private string VideoWithMicTrack(string name, double seconds)
        {
            string path = Path.Combine(_root, name);
            Ffmpeg.Run(new[]
            {
                "-y",
                "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=15:duration={Inv(seconds)}",
                "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={Inv(seconds)}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ac", "2", "-ar", "48000", "-shortest", path,
            }, "test: video with a mic track");
            return path;
        }

        private string Tone(string name, double seconds)
        {
            string path = Path.Combine(_root, name);
            Ffmpeg.Run(FfmpegArgs.GenerateTone(440, seconds, 0.2, path), "test: tone");
            return path;
        }

        private static string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ---- the two passes really run, and leave nothing behind ----------

        [Fact]
        public void ProcessVideoMic_RunsBothPassesAndLeavesNoIntermediate()
        {
            string raw = VideoWithMicTrack("raw.mp4", 5);
            string final = Path.Combine(_root, "recording.mp4");

            AudioMix.ProcessVideoMic(raw, final, Defaults());

            Assert.True(File.Exists(final), "the two-pass mux produced no final recording");
            Assert.False(File.Exists(Intermediate),
                "the intermediate processed audio was left in the recording directory");

            // The output really is a copied video plus a re-encoded audio track.
            Assert.Equal("h264", StreamCodec(final, "v"));
            Assert.Equal("aac", StreamCodec(final, "a"));
            Assert.InRange(MediaProbe.DurationSeconds(final), 4.0, 6.5);
        }

        [Fact]
        public void MuxVideoSystemOnly_RunsBothPassesAndLeavesNoIntermediate()
        {
            string raw = VideoWithMicTrack("raw.mp4", 5);
            string sys = Tone("sys.wav", 5);
            string final = Path.Combine(_root, "recording.mp4");

            AudioMix.MuxVideoSystemOnly(raw, sys, final, 0.7);

            Assert.True(File.Exists(final));
            Assert.False(File.Exists(Intermediate));
            Assert.Equal("aac", StreamCodec(final, "a"));
        }

        [Fact]
        public void MuxVideoMixed_RunsBothPassesAndLeavesNoIntermediate()
        {
            string raw = VideoWithMicTrack("raw.mp4", 5);
            string sys = Tone("sys.wav", 5);
            string final = Path.Combine(_root, "recording.mp4");

            AudioMix.MuxVideoMixed(raw, sys, final, Defaults());

            Assert.True(File.Exists(final));
            Assert.False(File.Exists(Intermediate));
            Assert.Equal("aac", StreamCodec(final, "a"));
        }

        // ---- a pass that fails leaves no fragment and no intermediate -----

        [Fact]
        public void MuxInTwoPasses_AudioPassFails_RemovesTheIntermediateAndThePartialOutput()
        {
            // A fragment at the final path (what a killed mux leaves) plus an audio pass that cannot
            // succeed. Both must be gone afterwards: issue #128 proved a fragment at the final path
            // is treated by later stages as the finished recording.
            string final = Path.Combine(_root, "recording.mp4");
            File.WriteAllText(final, "truncated mux output with no moov atom");
            string missing = Path.Combine(_root, "no-such-input.wav");

            Assert.Throws<UsageException>(() => AudioMix.MuxInTwoPasses(
                "raw.mp4", final, "test audio pass fails",
                wav => new[] { "-y", "-i", missing, "-c:a", "pcm_f32le", wav }));

            Assert.False(File.Exists(Intermediate), "the failed audio pass left its intermediate behind");
            Assert.False(File.Exists(final), "the failed mux left its partial output at the final path");
        }

        [Fact]
        public void MuxInTwoPasses_MuxPassFails_RemovesTheIntermediateAndThePartialOutput()
        {
            // The audio pass SUCCEEDS and the mux pass fails on a missing video. The intermediate
            // exists by then, so this is the case that proves the cleanup is not just "pass 1 never
            // wrote anything".
            string final = Path.Combine(_root, "recording.mp4");
            File.WriteAllText(final, "truncated mux output with no moov atom");
            string tone = Tone("audio.wav", 1);

            Assert.Throws<UsageException>(() => AudioMix.MuxInTwoPasses(
                Path.Combine(_root, "no-such-video.mp4"), final, "test mux pass fails",
                wav => new[] { "-y", "-i", tone, "-c:a", "pcm_f32le", wav }));

            Assert.False(File.Exists(Intermediate), "the failed mux pass left the intermediate behind");
            Assert.False(File.Exists(final), "the failed mux left its partial output at the final path");
        }

        [Fact]
        public void MuxInTwoPasses_TheIntermediateGoesBesideTheOutput_NotIntoTheSystemTemp()
        {
            // It has to be on the SAME VOLUME as the recording: a ten-minute float WAV is ~115 MB and
            // the system temp can be a smaller disk. The name is also what the cleanup deletes, so
            // pin both.
            Assert.Equal("audio_processed.wav", AudioMix.ProcessedAudioFileName);

            string raw = VideoWithMicTrack("raw.mp4", 2);
            string final = Path.Combine(_root, "recording.mp4");
            string? seen = null;

            AudioMix.MuxInTwoPasses(raw, final, "test intermediate location", wav =>
            {
                seen = wav;
                return new[] { "-y", "-i", raw, "-map", "0:a", "-c:a", "pcm_f32le", wav };
            });

            Assert.Equal(Intermediate, seen);
            Assert.False(File.Exists(Intermediate));
        }

        // ---- helpers ------------------------------------------------------

        /// <summary>The codec name of the first stream of <paramref name="kind"/> ("v" or "a").</summary>
        private static string StreamCodec(string path, string kind)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffprobe(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
                     {
                         "-v", "error", "-select_streams", kind + ":0",
                         "-show_entries", "stream=codec_name", "-of", "csv=p=0", path,
                     })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string outText = p.StandardOutput.ReadToEnd().Trim();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.False(string.IsNullOrWhiteSpace(outText),
                $"ffprobe named no {kind} stream in {path} - broken instrument: {err}");
            return outText;
        }
    }
}
