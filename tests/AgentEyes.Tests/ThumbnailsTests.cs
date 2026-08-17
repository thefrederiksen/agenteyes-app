using System;
using System.IO;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #141: the Library card thumbnail. Issue #77 deferred the audio mux out of the stop, so
    /// the final media file does not exist at stop time - Ensure() must say so in the log instead of
    /// returning null in silence, and it must never throw when the file is absent.
    /// None of these cases reaches ffmpeg: each one returns before the generation step.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public class ThumbnailsTests : IDisposable
    {
        private readonly string _root;

        public ThumbnailsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-thumbs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }

        private string MakeDir(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Ensure_VideoFileMissing_ReturnsNullAndDoesNotThrow()
        {
            // Arrange: the exact state a video recording is in between Stop() and FinalizePending -
            // a manifest naming recording.mp4, and no recording.mp4 on disk yet.
            string dir = MakeDir("2026-08-11_111133_video");
            ManifestStore.Replace(dir, new Manifest { Mode = "video", VideoFile = "recording.mp4", DurationSeconds = 2201.2 });

            // Act - an exception here fails the test, which is the "does not throw" half.
            string? result = Thumbnails.Ensure(dir);

            // Assert
            Assert.Null(result);
            Assert.Null(Thumbnails.PathFor(dir));
        }

        [Fact]
        public void Ensure_AudioFileMissing_ReturnsNull()
        {
            string dir = MakeDir("2026-08-11_111200_audio");
            ManifestStore.Replace(dir, new Manifest { Mode = "audio", AudioFile = "audio.wav", DurationSeconds = 30 });

            Assert.Null(Thumbnails.Ensure(dir));
        }

        [Fact]
        public void Ensure_NoManifest_ReturnsNull()
        {
            string dir = MakeDir("2026-08-11_111300_video");

            Assert.Null(Thumbnails.Ensure(dir));
        }

        [Fact]
        public void Ensure_ShotMode_ReturnsNull()
        {
            // A screenshot's card image is the shot itself; there is nothing to generate.
            string dir = MakeDir("2026-08-11_111400_shot");
            ManifestStore.Replace(dir, new Manifest { Mode = "shot" });

            Assert.Null(Thumbnails.Ensure(dir));
        }

        [Fact]
        public void Ensure_ThumbnailAlreadyPresent_ReturnsItWithoutRegenerating()
        {
            // Idempotence: the existing file is returned even though the video is absent, so a
            // second post-processing pass over the same directory costs nothing.
            string dir = MakeDir("2026-08-11_111500_video");
            ManifestStore.Replace(dir, new Manifest { Mode = "video", VideoFile = "recording.mp4", DurationSeconds = 12 });
            string thumb = Path.Combine(dir, "thumb.jpg");
            File.WriteAllText(thumb, "poster");

            Assert.Equal(thumb, Thumbnails.Ensure(dir));
            Assert.Equal("poster", File.ReadAllText(thumb));
        }

        [Fact]
        public void PathFor_NoThumbnail_ReturnsNull()
        {
            Assert.Null(Thumbnails.PathFor(MakeDir("2026-08-11_111600_video")));
        }

        [Fact]
        public void PathFor_WaveformPng_ReturnsIt()
        {
            string dir = MakeDir("2026-08-11_111700_audio");
            string png = Path.Combine(dir, "thumb.png");
            File.WriteAllText(png, "wave");

            Assert.Equal(png, Thumbnails.PathFor(dir));
        }

        // ---- thumbnail repair backlog (issue #142) -------------------------

        /// <summary>A finished video recording: manifest, media on disk, no thumbnail yet.</summary>
        private string MakeFinished(string name, string mode = "video", int thumbAttempts = 0)
        {
            string dir = MakeDir(name);
            string media = mode == "video" ? "recording.mp4" : "audio.wav";
            File.WriteAllText(Path.Combine(dir, media), "x");
            ManifestStore.Replace(dir, new Manifest
            {
                Mode = mode,
                VideoFile = mode == "video" ? media : null,
                AudioFile = mode == "audio" ? media : null,
                DurationSeconds = 30,
                ThumbAttempts = thumbAttempts,
            });
            return dir;
        }

        [Fact]
        public void NeedsThumb_FinishedVideoWithoutThumb_True()
        {
            Assert.True(Thumbnails.NeedsThumb(MakeFinished("2026-08-11_120000_video")));
        }

        [Fact]
        public void NeedsThumb_FinishedAudioWithoutThumb_True()
        {
            Assert.True(Thumbnails.NeedsThumb(MakeFinished("2026-08-11_120001_audio", "audio")));
        }

        [Fact]
        public void NeedsThumb_ThumbnailPresent_False()
        {
            string dir = MakeFinished("2026-08-11_120002_video");
            File.WriteAllText(Path.Combine(dir, "thumb.jpg"), "poster");

            Assert.False(Thumbnails.NeedsThumb(dir));
        }

        [Fact]
        public void NeedsThumb_MediaNotMuxedYet_False()
        {
            // Between Stop() and FinalizePending the final media file does not exist (issues
            // #77/#141); ffmpeg would have nothing to read. It joins the backlog on a later pass.
            string dir = MakeDir("2026-08-11_120003_video");
            ManifestStore.Replace(dir, new Manifest { Mode = "video", VideoFile = "recording.mp4", DurationSeconds = 30 });

            Assert.False(Thumbnails.NeedsThumb(dir));
        }

        [Fact]
        public void NeedsThumb_ScreenshotFolder_False()
        {
            string dir = MakeDir("2026-08-11_120004_shot");
            File.WriteAllText(Path.Combine(dir, "shot.png"), "x");
            ManifestStore.Replace(dir, new Manifest { Mode = "shot" });

            Assert.False(Thumbnails.NeedsThumb(dir));
        }

        [Fact]
        public void NeedsThumb_NoManifest_False()
        {
            string dir = MakeDir("2026-08-11_120005_video");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "x");

            Assert.False(Thumbnails.NeedsThumb(dir));
        }

        [Fact]
        public void NeedsThumb_AttemptsExhausted_False()
        {
            // A file ffmpeg can never read must drop out of the periodic pass instead of being
            // retried on every tick forever.
            Assert.False(Thumbnails.NeedsThumb(
                MakeFinished("2026-08-11_120006_video", thumbAttempts: Thumbnails.MaxThumbAttempts)));
        }

        [Fact]
        public void NeedsThumb_AttemptsBelowCap_True()
        {
            Assert.True(Thumbnails.NeedsThumb(
                MakeFinished("2026-08-11_120007_video", thumbAttempts: Thumbnails.MaxThumbAttempts - 1)));
        }

        [Fact]
        public void NeedsThumb_MissingDirectory_False()
        {
            Assert.False(Thumbnails.NeedsThumb(Path.Combine(_root, "nope")));
        }

        [Fact]
        public void FindMissing_ReturnsOnlyRepairable_OldestFirst()
        {
            MakeFinished("2026-08-11_120200_video");                       // missing
            MakeFinished("2026-08-11_120100_video");                       // missing, older
            string done = MakeFinished("2026-08-11_120300_video");
            File.WriteAllText(Path.Combine(done, "thumb.jpg"), "poster");  // already has one
            MakeFinished("2026-08-11_120400_video", thumbAttempts: Thumbnails.MaxThumbAttempts);

            var missing = Thumbnails.FindMissing(_root);

            Assert.Equal(2, missing.Count);
            Assert.Equal("2026-08-11_120100_video", Path.GetFileName(missing[0]));
            Assert.Equal("2026-08-11_120200_video", Path.GetFileName(missing[1]));
        }

        [Fact]
        public void FindMissing_MissingRoot_EmptyNotThrow()
        {
            Assert.Empty(Thumbnails.FindMissing(Path.Combine(_root, "does-not-exist")));
        }

        [Fact]
        public void NoteThumbAttempt_IncrementsAndPersists()
        {
            string dir = MakeFinished("2026-08-11_120500_video");

            Thumbnails.NoteThumbAttempt(dir);

            Assert.Equal(1, Manifest.Load(dir).ThumbAttempts);
        }

        [Fact]
        public void NoteThumbAttempt_IsSeparateFromTheOtherBudgets()
        {
            // A recording can transcribe and title first time and still lose its poster frame.
            string dir = MakeFinished("2026-08-11_120501_video");

            Thumbnails.NoteThumbAttempt(dir);

            var m = Manifest.Load(dir);
            Assert.Equal(1, m.ThumbAttempts);
            Assert.Equal(0, m.TitleAttempts);
            Assert.Equal(0, m.TranscribeAttempts);
        }

        [Fact]
        public void NoteThumbAttempt_ThreeTimes_DropsOutOfThePass()
        {
            string dir = MakeFinished("2026-08-11_120600_video");
            for (int i = 0; i < Thumbnails.MaxThumbAttempts; i++) Thumbnails.NoteThumbAttempt(dir);

            Assert.False(Thumbnails.NeedsThumb(dir));
            Assert.DoesNotContain(dir, Thumbnails.FindMissing(_root));
        }

        [Fact]
        public void NoteThumbAttempt_NoManifest_DoesNotThrow()
        {
            string dir = MakeDir("2026-08-11_120700_video");

            Thumbnails.NoteThumbAttempt(dir);   // must not throw
        }

        [Fact]
        public void NeedsThumb_WorkInFlightForTheRecording_False()
        {
            // Issue #142: the repair pass was the ONE manifest writer that ignored the claim set,
            // so it could write ThumbAttempts underneath Package.Run's manifest write - and a
            // recording whose thumbnail failed sits in this backlog for the whole packaging window,
            // which is exactly the population the pass targets.
            string dir = MakeFinished("2026-08-11_120800_video");
            Assert.True(Thumbnails.NeedsThumb(dir));   // repairable when nobody holds it

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "a stand-in owner", out _));
            try
            {
                Assert.False(Thumbnails.NeedsThumb(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }

            Assert.True(Thumbnails.NeedsThumb(dir));   // back in the backlog once released
        }

        [Fact]
        public void FindMissing_ExcludesRecordingsWithWorkInFlight()
        {
            string busy = MakeFinished("2026-08-11_120900_video");
            string free = MakeFinished("2026-08-11_120901_video");

            Assert.True(RecordingWorkset.TryClaim(busy, RecordingWorkKind.Stage, "a stand-in owner", out _));
            try
            {
                var missing = Thumbnails.FindMissing(_root);

                Assert.Equal(new[] { free }, missing);
            }
            finally { RecordingWorkset.ReleaseForTests(busy); }
        }
    }
}
