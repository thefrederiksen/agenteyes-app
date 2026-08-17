using System;
using System.IO;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class PackageTests
    {
        [Fact]
        public void WalkthroughDirFor_is_sibling_stem_walkthrough()
        {
            string video = Path.Combine(Path.GetTempPath(), "demos", "bug-demo.mp4");
            string expected = Path.Combine(Path.GetTempPath(), "demos", "bug-demo_walkthrough");
            Assert.Equal(expected, Package.WalkthroughDirFor(video));
        }

        [Fact]
        public void SynthesizeManifest_points_back_at_the_video()
        {
            var created = new DateTime(2026, 6, 6, 11, 21, 0, DateTimeKind.Utc);
            var m = Package.SynthesizeManifest("bug-demo.mp4", 128.134, created);

            Assert.Equal("video", m.Mode);
            Assert.Equal("bug-demo", m.Label);
            Assert.Equal("../bug-demo.mp4", m.VideoFile);
            Assert.Equal(128.13, m.DurationSeconds);
            Assert.Equal(created.ToString("o"), m.CreatedUtc);
        }

        [Fact]
        public void Run_directory_without_manifest_explains_bare_video_path()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var ex = Assert.Throws<UsageException>(() => Package.Run(dir));
                Assert.Contains("agenteyes package <video.mp4>", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Run_missing_path_throws_usage()
        {
            var ex = Assert.Throws<UsageException>(
                () => Package.Run(Path.Combine(Path.GetTempPath(), "does-not-exist-xyz")));
            Assert.Contains("not found", ex.Message);
        }
    }
}
