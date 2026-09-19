using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Housekeeping;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The decay ladder against real files and the real encoder (issue #59): the composition-input
    /// tier and the expire tier, both of which delete things a recording cannot get back, so both
    /// are proven end to end here rather than only at the planner level.
    ///
    /// It BUILDS its own recordings with real media - a real mp4 with real frames in it - for the
    /// same reason HousekeeperEndToEndTests does: a skip on a machine without the fixtures is
    /// indistinguishable from a pass in every report this repository produces.
    /// </summary>
    public class DecayEndToEndTests : IDisposable
    {
        private readonly string _root;
        private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        public DecayEndToEndTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-decay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private static HousekeepingSettings Live(int keepVideoDays) => new()
        {
            ReportOnly = false,
            PreservedOriginalDays = 7,
            KeepVideoDays = keepVideoDays,
        };

        /// <summary>
        /// A video recording with real media: a real 2-second mp4 as the keeper, a composition
        /// input beside it, a real extracted frame, a hand-taken shot the video cannot recreate, a
        /// walkthrough page showing both images, and a transcript.
        /// </summary>
        private string MakeVideoRecording(string name, string createdUtc)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            string shots = Path.Combine(dir, "shots");
            Directory.CreateDirectory(shots);

            // The keeper: a real video, two seconds of it.
            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:duration=2:rate=10",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "ultrafast",
                Path.Combine(dir, "recording.mp4"),
            }, "test fixture keeper video");

            // A composition input, as CameraCompose leaves one.
            File.Copy(Path.Combine(dir, "recording.mp4"), Path.Combine(dir, "camera.mp4"));

            // An extracted frame (as packaging wrote) and a hand-taken shot (as the owner took).
            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-i", Path.Combine(dir, "recording.mp4"),
                "-frames:v", "1",
                Path.Combine(shots, "frame_001.png"),
            }, "test fixture extracted frame");
            File.WriteAllText(Path.Combine(shots, "region_640x480.png"), "the owner's own shot");

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");
            File.WriteAllText(Path.Combine(dir, "walkthrough.html"),
                "<html><body>"
                + "<figure class=\"shot\"><img src=\"shots/frame_001.png\" alt=\"screenshot\"/>"
                + "<figcaption>00:00:00</figcaption></figure>"
                + "<figure class=\"shot\"><img src=\"shots/region_640x480.png\" alt=\"mine\"/>"
                + "<figcaption>00:00:01</figcaption></figure>"
                + "<p class=\"line\"><span class=\"ts\">00:00:00</span> something said</p>"
                + "</body></html>");

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = name,
                CreatedUtc = createdUtc,
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                CameraFile = "camera.mp4",
                Walkthrough = "walkthrough.html",
                DurationSeconds = 2,
                Files = new List<string>
                {
                    "recording.mp4", "camera.mp4",
                    "shots/frame_001.png", "shots/region_640x480.png",
                },
                Shots = new List<Manifest.ShotEntry>
                {
                    new() { OffsetSeconds = 0, File = "shots/frame_001.png" },
                    new() { OffsetSeconds = 1, File = "shots/region_640x480.png" },
                },
            });

            return dir;
        }

        // ---- the composition inputs ----------------------------------------------------------

        [Fact]
        public void ALivePass_PastTheWindow_DeletesTheCompositionInputAndClearsTheManifest()
        {
            string dir = MakeVideoRecording("2026-09-01_120000_video", "2026-09-01T12:00:00Z");

            var report = Housekeeper.Run(_root, Live(keepVideoDays: 0), "test", () => false, Now);

            // Eleven days old against a seven-day window: the input goes.
            Assert.False(File.Exists(Path.Combine(dir, "camera.mp4")), "the composition input survived");
            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")), "the keeper did not survive");

            var manifest = Manifest.Load(dir);
            Assert.Null(manifest.CameraFile);
            Assert.DoesNotContain("camera.mp4", manifest.Files);

            Assert.Contains(manifest.Housekeeping,
                h => h.Kind == nameof(HousekeepingKind.DeleteCompositionInput) && h.Outcome == "done");
            Assert.Contains(report.Recordings.SelectMany(r => r.Steps),
                s => s.Kind == nameof(HousekeepingKind.DeleteCompositionInput) && s.Outcome == "done");
        }

        // ---- the expire tier -----------------------------------------------------------------

        [Fact]
        public void ALivePass_PastKeepVideoDays_ExpiresTheRecordingToItsText()
        {
            string dir = MakeVideoRecording("2026-08-01_120000_video", "2026-08-01T12:00:00Z");

            var report = Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);

            // What goes: the video, the composition input, the extracted frame.
            Assert.False(File.Exists(Path.Combine(dir, "recording.mp4")), "the keeper video survived expiry");
            Assert.False(File.Exists(Path.Combine(dir, "camera.mp4")), "the composition input survived expiry");
            Assert.False(File.Exists(Path.Combine(dir, "shots", "frame_001.png")), "the extracted frame survived expiry");

            // What stays: the transcript, the page, the manifest, the owner's own shot.
            Assert.True(File.Exists(Path.Combine(dir, "transcript.json")));
            Assert.True(File.Exists(Path.Combine(dir, "walkthrough.html")));
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(dir, "shots", "region_640x480.png")),
                "a hand-taken shot the video cannot recreate was deleted");

            // The page is honest: the frame image became a note, the owner's own image stayed.
            string html = File.ReadAllText(Path.Combine(dir, "walkthrough.html"));
            Assert.DoesNotContain("shots/frame_001", html);
            Assert.Contains("expired on", html);
            Assert.Contains("src=\"shots/region_640x480.png\"", html);
            Assert.Contains("something said", html);

            // The manifest names nothing that is gone - and still names what stayed: the hand-taken
            // shot survives the expiry and keeps its entry.
            var manifest = Manifest.Load(dir);
            Assert.Null(manifest.VideoFile);
            Assert.Null(manifest.CameraFile);
            Assert.Equal(new[] { "shots/region_640x480.png" },
                manifest.Files.OrderBy(f => f).ToArray());
            Assert.DoesNotContain(manifest.Shots, s => s.File.Contains("frame_"));
            Assert.Contains(manifest.Shots, s => s.File == "shots/region_640x480.png");

            // And the record proves it happened and what it reclaimed.
            var record = Assert.Single(manifest.Housekeeping.Where(h => h.Kind == nameof(HousekeepingKind.ExpireRecording)));
            Assert.Equal("done", record.Outcome);
            Assert.True(record.BytesReclaimed > 0);
        }

        [Fact]
        public void ALivePass_BelowKeepVideoDays_LeavesTheVideoAlone()
        {
            string dir = MakeVideoRecording("2026-09-10_120000_video", "2026-09-10T12:00:00Z");

            Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);

            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")));
        }

        [Fact]
        public void ExpiredRecording_AnotherPassFindsNothingLeftToExpire()
        {
            string dir = MakeVideoRecording("2026-08-01_120000_video", "2026-08-01T12:00:00Z");

            Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);
            var second = Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);

            Assert.DoesNotContain(second.Recordings.SelectMany(r => r.Steps),
                s => s.Kind == nameof(HousekeepingKind.ExpireRecording));
        }

        [Fact]
        public void APendingExpiry_IsCompletedFromTheWrittenRecord_AndTheRecordIsCleared()
        {
            // The state a crash between the manifest repoint and the deletes leaves: the manifest
            // names NO video, so nothing can find the file on disk except the written intent.
            string dir = Path.Combine(_root, "2026-08-01_120000_video");
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), new string('v', 5_000));
            File.WriteAllText(Path.Combine(dir, "camera.mp4"), new string('v', 700));
            File.WriteAllText(Path.Combine(dir, "shots", "frame_001.png"), new string('v', 200));
            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = "2026-08-01_120000_video",
                CreatedUtc = "2026-08-01T12:00:00Z",
                Transcript = "transcript.json",
                PendingExpiry = new List<string>
                {
                    "recording.mp4", "camera.mp4", "shots/frame_001.png",
                },
            });

            Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);

            Assert.False(File.Exists(Path.Combine(dir, "recording.mp4")),
                "a pending expiry did not delete the video the manifest no longer names");
            Assert.False(File.Exists(Path.Combine(dir, "camera.mp4")));
            Assert.False(File.Exists(Path.Combine(dir, "shots", "frame_001.png")));
            Assert.True(File.Exists(Path.Combine(dir, "transcript.json")));
            Assert.Null(Manifest.Load(dir).PendingExpiry);
        }

        [Fact]
        public void ALivePass_WhenTheKeeperIsACompositionInput_CountsAndDeletesItOnce()
        {
            // The planner protects a camera-only recording whose keeper IS camera.mp4. The expiry
            // that follows must not count that one file twice (once as keeper, once as input).
            string dir = Path.Combine(_root, "2026-08-01_120000_video");
            Directory.CreateDirectory(dir);
            long cameraSize = 700;
            long screenSize = 300;
            File.WriteAllText(Path.Combine(dir, "camera.mp4"), new string('v', (int)cameraSize));
            File.WriteAllText(Path.Combine(dir, "recording.screen.mp4"), new string('v', (int)screenSize));
            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = "2026-08-01_120000_video",
                CreatedUtc = "2026-08-01T12:00:00Z",
                Transcript = "transcript.json",
                VideoFile = "camera.mp4",
                DurationSeconds = 2,
                Files = new List<string> { "camera.mp4", "recording.screen.mp4" },
            });

            Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);

            Assert.False(File.Exists(Path.Combine(dir, "camera.mp4")));
            Assert.False(File.Exists(Path.Combine(dir, "recording.screen.mp4")));

            var record = Assert.Single(Manifest.Load(dir).Housekeeping
                .Where(h => h.Kind == nameof(HousekeepingKind.ExpireRecording)));
            Assert.Equal(cameraSize + screenSize, record.BytesReclaimed);
        }

        [Fact]
        public void APendingExpiryWhoseFramesWereRenamed_StillDeletesTheFrames()
        {
            // A conversion tier renamed a frame after the expiry record was written: the record
            // names the PNG, the disk holds the JPEG. The resume must not strand the renamed file.
            string dir = Path.Combine(_root, "2026-08-01_120000_video");
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), new string('v', 5_000));
            File.WriteAllText(Path.Combine(dir, "shots", "frame_001.jpg"), new string('v', 90));
            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = "2026-08-01_120000_video",
                CreatedUtc = "2026-08-01T12:00:00Z",
                Transcript = "transcript.json",
                PendingExpiry = new List<string> { "recording.mp4", "shots/frame_001.png" },
            });

            Housekeeper.Run(_root, Live(keepVideoDays: 30), "test", () => false, Now);

            Assert.False(File.Exists(Path.Combine(dir, "recording.mp4")));
            Assert.False(File.Exists(Path.Combine(dir, "shots", "frame_001.jpg")),
                "a renamed frame survived the expiry because the written record named its old spelling");
            Assert.Null(Manifest.Load(dir).PendingExpiry);
        }

        // ---- on-demand frames ----------------------------------------------------------------

        [Fact]
        public void ExtractJpeg_FromARealVideo_ReturnsARealFrame()
        {
            string dir = MakeVideoRecording("2026-09-15_120000_video", "2026-09-15T12:00:00Z");

            byte[] frame = VideoFrame.ExtractJpeg(Path.Combine(dir, "recording.mp4"), 1);

            // JPEG magic: the bytes really are an image, not an empty or textual success.
            Assert.True(frame.Length > 1000, "the extracted frame is suspiciously small");
            Assert.Equal(0xFF, frame[0]);
            Assert.Equal(0xD8, frame[1]);
        }

        [Fact]
        public void ExtractJpeg_OffsetPastTheEnd_ThrowsRatherThanReturningNothing()
        {
            string dir = MakeVideoRecording("2026-09-15_120000_video", "2026-09-15T12:00:00Z");

            Assert.ThrowsAny<Exception>(() =>
                VideoFrame.ExtractJpeg(Path.Combine(dir, "recording.mp4"), 3600));
        }
    }

    /// <summary>
    /// The ordering law for the expire tier, on the same seam the other tiers use: the page is
    /// rewritten and the manifest stops naming the doomed files BEFORE anything is deleted. It
    /// lives in this collection because <see cref="ManifestStore.InterruptBeforeReplace"/> is a
    /// process-wide static and the rest of the class runs parallel.
    /// </summary>
    [Collection(ManifestSeamCollection.Name)]
    public class ExpireOrderTests : IDisposable
    {
        private readonly string _root;

        public ExpireOrderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-expireorder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            ManifestStore.InterruptBeforeReplace = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void AnExpiryInterruptedAtTheManifest_LeavesEveryReferenceResolving()
        {
            string dir = MakeRecording();

            ManifestStore.InterruptBeforeReplace = _ => throw new IOException("interrupted on purpose");
            try
            {
                Housekeeper.Run(_root, new HousekeepingSettings
                {
                    ReportOnly = false,
                    PreservedOriginalDays = 7,
                    KeepVideoDays = 30,
                }, "test", () => false, new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc));
            }
            finally
            {
                ManifestStore.InterruptBeforeReplace = null;
            }

            // The deletes come AFTER the repoint, so an interrupted repoint means nothing was
            // removed: the video is still on disk and the manifest still names it. The page was
            // already retired - that is fine, it points at nothing that is gone.
            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")),
                "the video was deleted before the manifest was repointed - the ordering is wrong");
            var manifest = Manifest.Load(dir);
            Assert.Equal("recording.mp4", manifest.VideoFile);

            // A later pass completes the expiry, and completes it exactly once.
            Housekeeper.Run(_root, new HousekeepingSettings
            {
                ReportOnly = false,
                PreservedOriginalDays = 7,
                KeepVideoDays = 30,
            }, "test", () => false, new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc));

            Assert.False(File.Exists(Path.Combine(dir, "recording.mp4")));
            Assert.Null(Manifest.Load(dir).VideoFile);
        }

        private string MakeRecording()
        {
            string dir = Path.Combine(_root, "2026-08-01_120000_video");
            Directory.CreateDirectory(dir);
            string shots = Path.Combine(dir, "shots");
            Directory.CreateDirectory(shots);

            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:duration=2:rate=10",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "ultrafast",
                Path.Combine(dir, "recording.mp4"),
            }, "test fixture keeper video");

            File.WriteAllText(Path.Combine(shots, "frame_001.png"), "an extracted frame, in spirit");
            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");
            File.WriteAllText(Path.Combine(dir, "walkthrough.html"),
                "<img src=\"shots/frame_001.png\" alt=\"screenshot\"/>");

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = "2026-08-01_120000_video",
                CreatedUtc = "2026-08-01T12:00:00Z",
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                DurationSeconds = 2,
                Files = new List<string> { "recording.mp4", "shots/frame_001.png" },
                Shots = new List<Manifest.ShotEntry> { new() { OffsetSeconds = 0, File = "shots/frame_001.png" } },
            });

            return dir;
        }
    }
}
