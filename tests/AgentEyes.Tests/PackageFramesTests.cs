using System.Linq;
using AgentEyes;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Package.PersistFrames must write the extracted key frames into manifest.Shots
    /// (the documented plugin contract surface) without losing manual marker shots,
    /// and stay idempotent when a recording is re-packaged.
    /// </summary>
    public class PackageFramesTests
    {
        private static WalkthroughShot Frame(double off, int n) =>
            new() { OffsetSeconds = off, RelativePath = $"shots/frame_{n:000}.png" };

        [Fact]
        public void PersistFrames_AddsExtractedFrames_OrderedByOffset()
        {
            var manifest = new Manifest();
            var frames = new[] { Frame(10, 3), Frame(0, 1), Frame(5, 2) };

            Package.PersistFrames(manifest, frames);

            Assert.Equal(3, manifest.Shots.Count);
            Assert.Equal(new[] { 0.0, 5.0, 10.0 }, manifest.Shots.Select(s => s.OffsetSeconds));
            Assert.Equal("shots/frame_001.png", manifest.Shots[0].File);
        }

        [Fact]
        public void PersistFrames_PreservesManualMarkers_MergedByOffset()
        {
            var manifest = new Manifest();
            manifest.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 1.51, File = "shots/00m01s.png" });

            Package.PersistFrames(manifest, new[] { Frame(0, 1), Frame(5, 2) });

            Assert.Equal(3, manifest.Shots.Count);
            Assert.Contains(manifest.Shots, s => s.File == "shots/00m01s.png");          // marker kept
            Assert.Equal(new[] { 0.0, 1.51, 5.0 }, manifest.Shots.Select(s => s.OffsetSeconds));
        }

        [Fact]
        public void PersistFrames_IsIdempotent_OnRepackage()
        {
            var manifest = new Manifest();
            manifest.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 1.51, File = "shots/00m01s.png" });

            Package.PersistFrames(manifest, new[] { Frame(0, 1), Frame(5, 2) });
            Package.PersistFrames(manifest, new[] { Frame(0, 1), Frame(5, 2) });   // re-run

            Assert.Equal(3, manifest.Shots.Count);   // no duplicated frame_* entries
            Assert.Single(manifest.Shots, s => s.File == "shots/00m01s.png");
            Assert.Equal(2, manifest.Shots.Count(s => s.File.Contains("frame_")));
        }
    }
}
