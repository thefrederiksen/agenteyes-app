using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Housekeeping;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The decay ladder (issue #59): composition inputs at the preserved-original window, the
    /// composed video after KeepVideoDays, and the pure pieces of on-demand frames.
    ///
    /// The planner tests here are about REFUSALS as much as actions - the expire tier is the first
    /// housekeeping tier that deletes the recording itself, so the conditions that stop it are the
    /// most valuable lines in the file.
    /// </summary>
    public class DecayLadderTests
    {
        private static Manifest Packaged(int keepVideoDaysIrrelevant = 0) => new()
        {
            Mode = "video",
            CreatedUtc = "2026-08-01T10:00:00Z",
            Transcript = "transcript.json",
            VideoFile = "recording.mp4",
            Files = new List<string> { "recording.mp4", "transcript.json" },
        };

        private static Dictionary<string, long> DiskWith(params (string Name, long Bytes)[] files)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["transcript.json"] = 1000,
            };
            foreach (var (name, bytes) in files) d[name] = bytes;
            return d;
        }

        private static HousekeepingSettings Ladder(int preservedDays = 7, int keepVideoDays = 30) => new()
        {
            ReportOnly = false,
            PreservedOriginalDays = preservedDays,
            KeepVideoDays = keepVideoDays,
        };

        // ---- the composition inputs ----------------------------------------------------------

        [Fact]
        public void Plan_PastTheWindowWithTheComposedKeeper_DeletesTheInputs()
        {
            var manifest = Packaged();
            manifest.CameraFile = "camera.mp4";
            var disk = DiskWith(("recording.mp4", 5_000_000), ("camera.mp4", 700_000), ("recording.screen.mp4", 10_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 10, Ladder(), out _);

            Assert.Equal(new[] { "camera.mp4", "recording.screen.mp4" },
                steps.Where(s => s.Kind == HousekeepingKind.DeleteCompositionInput)
                     .Select(s => s.File).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void Plan_InsideTheWindow_LeavesTheInputsAlone()
        {
            var manifest = Packaged();
            manifest.CameraFile = "camera.mp4";
            var disk = DiskWith(("recording.mp4", 5_000_000), ("camera.mp4", 700_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 6, Ladder(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeleteCompositionInput);
        }

        [Fact]
        public void Plan_WithoutTheComposedKeeper_LeavesTheInputsAlone()
        {
            // Composition never finished, so camera.mp4 is not an input - it is the recording.
            var manifest = Packaged();
            manifest.CameraFile = "camera.mp4";
            var disk = DiskWith(("camera.mp4", 700_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 40, Ladder(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeleteCompositionInput);
        }

        [Fact]
        public void Plan_AnInputThatIsItselfTheKeeper_IsNeverAnInput()
        {
            var manifest = Packaged();
            manifest.VideoFile = "camera.mp4";
            var disk = DiskWith(("camera.mp4", 700_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 40, Ladder(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeleteCompositionInput);
        }

        // ---- the keeper expires ---------------------------------------------------------------

        [Fact]
        public void Plan_PastKeepVideoDays_PlansOneExpireStepAndNothingPointless()
        {
            var manifest = Packaged();
            var disk = DiskWith(
                ("recording.mp4", 5_000_000),
                ("camera.mp4", 700_000),
                ("shots/frame_001.png", 200_000),
                ("shots/frame_002.jpg", 90_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 31, Ladder(), out _);

            var expire = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.ExpireRecording));
            Assert.Equal("recording.mp4", expire.File);

            // The whole set is named in the step's cost: the keeper, the inputs, and the frames in
            // BOTH spellings - the PNG packaging wrote and the JPEG Tier 2 made of it.
            Assert.Equal(5_000_000 + 700_000 + 200_000 + 90_000, expire.Bytes);

            // The frames are about to be deleted; converting them first would be work done to
            // throw away, so no conversion step may be planned for a recording being expired.
            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.ConvertFramesToJpeg);
        }

        [Theory]
        [InlineData(29, false)]
        [InlineData(30, true)]
        public void Plan_TheKeepVideoBoundary_HoldsOnBothSides(int ageDays, bool expected)
        {
            var disk = DiskWith(("recording.mp4", 5_000_000));

            var steps = HousekeepingPlan.For(Packaged(), disk, ageDays, Ladder(), out _);

            Assert.Equal(expected, steps.Any(s => s.Kind == HousekeepingKind.ExpireRecording));
        }

        [Fact]
        public void Plan_KeepVideoDaysZero_DisablesTheTail()
        {
            var disk = DiskWith(("recording.mp4", 5_000_000));

            var steps = HousekeepingPlan.For(Packaged(), disk, ageDays: 400, Ladder(keepVideoDays: 0), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.ExpireRecording);
        }

        [Fact]
        public void Plan_NoKeeperOnDisk_NothingToExpire()
        {
            var disk = DiskWith(("camera.mp4", 700_000));

            var steps = HousekeepingPlan.For(Packaged(), disk, ageDays: 400, Ladder(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.ExpireRecording);
        }

        [Fact]
        public void Plan_ThePinOutranksTheWholeLadder()
        {
            var manifest = Packaged();
            manifest.Keep = true;
            var disk = DiskWith(("recording.mp4", 5_000_000), ("camera.mp4", 700_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 400, Ladder(), out string? skipped);

            Assert.Empty(steps);
            Assert.NotNull(skipped);
        }

        // ---- a half-done expiry is resumable from the written record ------------------------

        [Fact]
        public void Plan_APendingExpiry_CompletesFromTheWrittenRecordEvenWithNoKeeper()
        {
            // The state a crash between the repoint and the deletes leaves: the manifest no longer
            // names the video, so NO inference from the keeper can find it. Only PendingExpiry can.
            var manifest = new Manifest
            {
                Mode = "video",
                CreatedUtc = "2026-08-01T10:00:00Z",
                Transcript = "transcript.json",
                PendingExpiry = new List<string> { "recording.mp4", "camera.mp4", "shots/frame_001.png" },
            };
            var disk = DiskWith(("recording.mp4", 5_000_000), ("camera.mp4", 700_000), ("shots/frame_001.png", 200_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 40, Ladder(), out _);

            var expire = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.ExpireRecording));
            Assert.Equal(5_900_000, expire.Bytes);

            // The inputs are already inside the pending record; separate steps would double-count.
            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeleteCompositionInput);
        }

        [Fact]
        public void Plan_APendingExpiry_AlsoExpiresTheFramesTheRecordNames()
        {
            var manifest = new Manifest
            {
                Mode = "video",
                CreatedUtc = "2026-08-01T10:00:00Z",
                Transcript = "transcript.json",
                PendingExpiry = new List<string> { "shots/frame_002.jpg" },
            };
            var disk = DiskWith(("shots/frame_002.jpg", 90_000));

            var steps = HousekeepingPlan.For(manifest, disk, ageDays: 40, Ladder(), out _);

            var expire = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.ExpireRecording));
            Assert.Equal(90_000, expire.Bytes);
        }

        [Fact]
        public void Plan_TheReportOnlyPass_CountsWhatAnExpiryWouldReclaim()
        {
            // The report is the thing that earns the switch being turned off: a dry run must name
            // the bytes, or a pass that planned nothing would look identical to one that planned
            // an expiry. Run against a real (dummy-media) recording: report-only touches nothing.
            string root = Path.Combine(Path.GetTempPath(), "agenteyes-decayplan-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, "2026-08-01_120000_video");
            try
            {
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
                    VideoFile = "recording.mp4",
                    DurationSeconds = 2,
                    Files = new List<string> { "recording.mp4", "camera.mp4", "shots/frame_001.png" },
                    Shots = new List<Manifest.ShotEntry> { new() { OffsetSeconds = 0, File = "shots/frame_001.png" } },
                });

                var report = Housekeeper.Run(root, new HousekeepingSettings
                {
                    ReportOnly = true,
                    PreservedOriginalDays = 7,
                    KeepVideoDays = 30,
                }, "test", () => false, new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc));

                Assert.Equal(5_900, report.BytesPlanned);
                Assert.True(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() == 5,
                    "a report-only pass changed something on disk");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        // ---- the page's shot list -------------------------------------------------------------

        [Fact]
        public void ShotsForPage_SkipsEmptyFileEntries_SoThePageNeverRendersAnEmptySrc()
        {
            // Re-packaging an on-demand recording must not double its frames: the earlier pass's
            // empty-File entries are replaced by the fresh set, never rendered as <img src="">.
            var manifest = new Manifest();
            manifest.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 0, File = "" });
            manifest.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 5, File = "" });
            manifest.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 9, File = @"shots\region_640x480.png" });

            var shots = Package.ShotsForPage(manifest);

            var shot = Assert.Single(shots);
            Assert.Equal("shots/region_640x480.png", shot.RelativePath);
            Assert.False(shot.ServedOnDemand);
        }

        [Fact]
        public void WalkthroughImages_AreLazy_SoOnePageDoesNotAskForEveryFrameAtOnce()
        {
            string html = AgentEyes.Packaging.WalkthroughBuilder.Build(
                "t",
                new List<AgentEyes.Packaging.WalkthroughShot>
                {
                    new() { OffsetSeconds = 0, RelativePath = "shots/frame_001.png" },
                },
                Array.Empty<TranscriptSegment>());

            Assert.Contains("<img loading=\"lazy\"", html);
        }

        // ---- on-demand frames: the pure pieces ----------------------------------------------

        [Fact]
        public void OnDemandShots_PlacesOneFramePerInterval_EndingAtTheDuration()
        {
            var shots = Package.OnDemandShots(durationSeconds: 12, intervalSeconds: 5, recordingId: "rec");

            Assert.Equal(new double[] { 0, 5, 10 }, shots.Select(s => s.OffsetSeconds).ToArray());
            Assert.All(shots, s => Assert.True(s.ServedOnDemand));
            Assert.All(shots, s =>
            {
                Assert.StartsWith("http://127.0.0.1:" + LocalAppConfig.Port + "/recordings/rec/frame/", s.RelativePath);
            });
        }

        [Fact]
        public void OnDemandShots_ZeroDuration_ProducesNothing()
        {
            Assert.Empty(Package.OnDemandShots(0, 5, "rec"));
        }

        [Fact]
        public void OnDemandShots_NonPositiveInterval_FallsBackToFiveSeconds()
        {
            var shots = Package.OnDemandShots(11, intervalSeconds: 0, recordingId: "rec");

            Assert.Equal(new double[] { 0, 5, 10 }, shots.Select(s => s.OffsetSeconds).ToArray());
        }

        [Fact]
        public void PersistFrames_OnDemandShots_AreRecordedWithAnEmptyFile()
        {
            // Empty File means "no file; the frame is served on demand". A consumer resolving
            // manifest names against the directory must never be handed an endpoint address.
            var manifest = new Manifest();
            var onDemand = Package.OnDemandShots(6, 5, "rec");

            Package.PersistFrames(manifest, onDemand);

            Assert.Equal(2, manifest.Shots.Count);
            Assert.All(manifest.Shots, s => Assert.Equal("", s.File));
            Assert.Equal(new double[] { 0, 5 }, manifest.Shots.Select(s => s.OffsetSeconds).ToArray());
        }

        [Fact]
        public void PersistFrames_Repackaging_ReplacesItsOwnOnDemandEntries()
        {
            var manifest = new Manifest();
            Package.PersistFrames(manifest, Package.OnDemandShots(6, 5, "rec"));
            Package.PersistFrames(manifest, Package.OnDemandShots(6, 10, "rec"));

            Assert.Equal(1, manifest.Shots.Count);
            Assert.Equal(0, manifest.Shots[0].OffsetSeconds);
        }

        // ---- the mirror between the App's config and Core's reader ---------------------------

        [Fact]
        public void TheAppConfigAndCoresReader_MirrorTheSameDefaults()
        {
            // Core reads the App's config file; a default that drifts between the two would make a
            // recording's walkthrough reference a port or a frame mode the app does not use.
            var cfg = new AgentEyes.App.Config();

            Assert.Equal(LocalAppConfig.DefaultPort, cfg.Port);
            Assert.Equal(LocalAppConfig.DefaultWalkthroughExtractFrames, cfg.WalkthroughExtractFrames);
        }

        [Fact]
        public void TheAppConfig_MapsKeepVideoDaysIntoHousekeeping()
        {
            var cfg = new AgentEyes.App.Config { HousekeepingKeepVideoDays = 90, HousekeepingPreservedOriginalDays = 7 };

            Assert.Equal(90, cfg.HousekeepingSettings().KeepVideoDays);
        }

        [Fact]
        public void TheAppConfig_ClampsAnExpirySoonerThanTheRawCopyWindow()
        {
            // Structural, not by value: an expiry sooner than the preserved-original window would
            // delete the composed video while the raw copies it was cleaned from are still
            // protected - the recording would lose its regenerable form before its raw one.
            var cfg = new AgentEyes.App.Config { HousekeepingKeepVideoDays = 21, HousekeepingPreservedOriginalDays = 30 };

            Assert.Equal(30, cfg.HousekeepingSettings().KeepVideoDays);
        }
    }
}
