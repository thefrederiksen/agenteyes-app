using System;
using System.Collections.Generic;
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
            var cfg = new AgentEyes.App.Config { HousekeepingKeepVideoDays = 21 };

            Assert.Equal(21, cfg.HousekeepingSettings().KeepVideoDays);
        }
    }
}
