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

        // ---- the final path is only ever created by the rename (issue #136) ----

        [Fact]
        public void MuxingTempPath_SitsBesideTheOutputAndIsNotTheOutput()
        {
            string temp = AudioMix.MuxingTempPath("/recordings/rec/recording.mp4");
            Assert.NotEqual("/recordings/rec/recording.mp4", temp);
            // Same directory, so File.Move into place is a rename and not a copy - which is the
            // whole reason the publish is atomic.
            Assert.Equal(Path.GetDirectoryName("/recordings/rec/recording.mp4"), Path.GetDirectoryName(temp));
            Assert.EndsWith(".muxing.tmp", temp);
        }

        [Fact]
        public void PassTwo_ReallyWritesTheMuxingTemp_AndNotTheFinalPath()
        {
            // THE TEST FOR THE RENAME, and it took three attempts to write one that can fail.
            //
            // The obvious versions cannot. Asserting "the final file exists and is valid, and no temp
            // is left" passes whether pass 2 writes the temp or the final path, because the finally
            // cleans up either way. Asserting a planted temp is gone passes too, for the same reason.
            // I checked by reverting the rename and watching all four of my new tests stay green,
            // which is the defect class this repo keeps paying for.
            //
            // What distinguishes the two implementations is ONE thing: where pass 2's ffmpeg opens
            // its output. So make the temp path IMPOSSIBLE to open as a file - a directory cannot be
            // opened for writing on any platform - and the mux must fail. If pass 2 were writing the
            // final path, the directory in the way would be irrelevant and the mux would succeed.
            // Deterministic, no timing, and it fails the moment the rename is taken out.
            string raw = VideoWithMicTrack("raw.mp4", 5);
            string final = Path.Combine(_root, "recording.mp4");
            string muxing = AudioMix.MuxingTempPath(final);
            Directory.CreateDirectory(muxing);

            AssertPassFailed(() => AudioMix.ProcessVideoMic(raw, final, Defaults()));

            Assert.False(File.Exists(final),
                "pass 2 wrote the FINAL path - the atomic publish is gone, and a process death can "
                + "leave a moov-less recording.mp4 again (issue #136 / #128 defect 2)");
            Directory.Delete(muxing, recursive: true);
        }

        [Fact]
        public void ProcessVideoMic_PublishesOverAFragmentFromAnEarlierAttempt()
        {
            // A fragment at the final path from an older binary is replaced by a complete recording,
            // not appended to or left half-overwritten - the publish is a whole-file replacement.
            string raw = VideoWithMicTrack("raw.mp4", 5);
            string final = Path.Combine(_root, "recording.mp4");
            File.WriteAllText(final, "a fragment from an earlier attempt, with no moov atom");

            AudioMix.ProcessVideoMic(raw, final, Defaults());

            Assert.True(File.Exists(final));
            Assert.Equal("h264", StreamCodec(final, "v"));
            Assert.Equal("aac", StreamCodec(final, "a"));
            Assert.False(File.Exists(AudioMix.MuxingTempPath(final)),
                "the muxing temp was left in the recording directory");
        }

        [Fact]
        public void MuxInTwoPasses_MuxPassFails_LeavesNothingAtTheTempEither()
        {
            // A pass 2 that cannot succeed must clean up the temp it was writing, not just the final
            // path. Plant a sentinel AT THE TEMP so this cannot pass by the temp never existing.
            string final = Path.Combine(_root, "recording.mp4");
            string muxing = AudioMix.MuxingTempPath(final);
            File.WriteAllText(muxing, "a half-written mux from an attempt that died");
            string tone = Tone("audio.wav", 1);

            AssertPassFailed(() => AudioMix.MuxInTwoPasses(
                Path.Combine(_root, "no-such-video.mp4"), final, "test mux pass fails",
                wav => new[] { "-y", "-i", tone, "-c:a", "pcm_f32le", wav }));

            Assert.False(File.Exists(muxing), "the failed mux left its temp behind");
            Assert.False(File.Exists(final), "the failed mux put something at the final path");
        }

        [Fact]
        public void MuxInTwoPasses_StaleTempsFromAKilledAttempt_DoNotSurviveAndDoNotCorruptTheOutput()
        {
            // NAMED FOR WHAT IT ACTUALLY PROVES. An earlier draft of this called itself
            // "...AreClearedAtTheStart" and claimed to cover MuxInTwoPasses' start-of-run
            // RemoveStaleTemp calls. It did not: disabling those two lines leaves this test green,
            // because ffmpeg's "-y" overwrites both temps and the finally removes them anyway. The
            // start-of-run removal exists for its WARN message, which no test here can fail on, and
            // the handoff says so rather than counting it as covered.
            //
            // What this DOES prove, and it is worth proving: a mux that finds litter from a killed
            // attempt still produces a correct recording and leaves nothing behind - the stale bytes
            // are not read as input, and they do not survive.
            string raw = VideoWithMicTrack("raw.mp4", 5);
            string final = Path.Combine(_root, "recording.mp4");
            string muxing = AudioMix.MuxingTempPath(final);
            File.WriteAllText(Intermediate, new string('x', 4096));   // a half-written pass-1 WAV
            File.WriteAllText(muxing, new string('y', 4096));         // a half-written pass-2 mp4

            AudioMix.ProcessVideoMic(raw, final, Defaults());

            Assert.False(File.Exists(Intermediate), "a stale intermediate survived the next attempt");
            Assert.False(File.Exists(muxing), "a stale muxing temp survived the next attempt");
            Assert.True(File.Exists(final));
            Assert.Equal("h264", StreamCodec(final, "v"));
            Assert.Equal("aac", StreamCodec(final, "a"));
            // The stale 4 KB of 'x' and 'y' are nowhere in the result: a real recording, right length.
            Assert.InRange(MediaProbe.DurationSeconds(final), 4.0, 6.5);
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

            AssertPassFailed(() => AudioMix.MuxInTwoPasses(
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

            AssertPassFailed(() => AudioMix.MuxInTwoPasses(
                Path.Combine(_root, "no-such-video.mp4"), final, "test mux pass fails",
                wav => new[] { "-y", "-i", tone, "-c:a", "pcm_f32le", wav }));

            Assert.False(File.Exists(Intermediate), "the failed mux pass left the intermediate behind");
            Assert.False(File.Exists(final), "the failed mux left its partial output at the final path");
        }

        [Fact]
        public void MuxInTwoPasses_CleanupCannotRemoveATemp_TheStageErrorSurvivesWithTheCleanupFailureAppended()
        {
            // Issue #128 round two's D5, as a RULE rather than a site. Round two fixed the delete in
            // RunStage; MuxInTwoPasses did not exist when that defect was filed, and it has three
            // deletes of its own. The defect D5 names is a cleanup delete that THROWS and replaces
            // the exception saying why the mux failed - measured on #128, where an IOException from
            // a File.Delete reached the manifest and the real stall error was lost.
            //
            // The known-BAD input: a DIRECTORY at the intermediate's path. File.Delete cannot remove
            // a directory on any platform (and File.Exists does not see one, so the stale-temp
            // sweep at the top of the mux leaves it alone), so the cleanup is guaranteed to fail
            // here. The audio pass is given a missing input so it fails for its own, separate
            // reason - the one that must survive.
            //
            // Three arms. Expected: ONE error carrying BOTH reasons. Bad: only the cleanup failure,
            // which is the defect. Empty: no exception at all would mean the mux did not fail, which
            // is a broken instrument and the first assertion catches it.
            string final = Path.Combine(_root, "recording.mp4");
            string missing = Path.Combine(_root, "no-such-input.wav");
            Directory.CreateDirectory(Intermediate);

            var ex = Record.Exception(() => AudioMix.MuxInTwoPasses(
                "raw.mp4", final, "test cleanup cannot remove a temp",
                wav => new[] { "-y", "-i", missing, "-c:a", "pcm_f32le", wav }));

            Assert.NotNull(ex);
            Assert.IsType<UsageException>(ex);
            // The reason the mux failed is still the FIRST thing the message says, and the stage
            // error itself is kept as the inner exception rather than discarded.
            Assert.NotNull(ex!.InnerException);
            Assert.StartsWith(ex.InnerException!.Message, ex.Message);
            // And the cleanup failure is appended, not hidden.
            Assert.Contains("could not be removed", ex.Message);
            Assert.Contains(Intermediate, ex.Message);

            Directory.Delete(Intermediate);
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

        /// <summary>
        /// Assert that <paramref name="mux"/> failed, WITHOUT pinning the exception type - and the
        /// reason is a Windows-only race in someone else's code that this test had no business
        /// failing on.
        ///
        /// A pass that cannot succeed (a missing input) makes ffmpeg exit almost immediately. On
        /// Windows <c>Ffmpeg.RunCore</c> calls <c>KillOnCloseJob.Assign</c> straight after
        /// <c>Process.Start</c>, and assigning an already-exited process to a job object fails, so
        /// the run throws <see cref="System.ComponentModel.Win32Exception"/>
        /// ("could not tie ffmpeg (pid N) to AgentEyes' lifetime") INSTEAD OF the
        /// <see cref="UsageException"/> that names the real ffmpeg error. Measured on the Windows CI
        /// job for issue #136; it is a latent defect in the issue #128 wiring, reported there, and
        /// not this issue's to fix.
        ///
        /// What this file is actually about is the CLEANUP after a failed pass, which happens in a
        /// <c>finally</c> and is therefore unaffected by which of the two exceptions came out. So the
        /// assertion is "it failed, with one of the two exceptions a failed ffmpeg run can produce",
        /// and the file assertions that follow are the real check. Pinning
        /// <see cref="UsageException"/> here made this test fail for a reason it was not testing.
        /// </summary>
        private static void AssertPassFailed(Action mux)
        {
            var ex = Record.Exception(mux);
            Assert.NotNull(ex);
            Assert.True(
                ex is UsageException || ex is System.ComponentModel.Win32Exception,
                $"expected the failed pass to surface as UsageException (or, on Windows, the "
                + $"KillOnCloseJob race's Win32Exception) but got {ex!.GetType().FullName}: {ex.Message}");
        }


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
