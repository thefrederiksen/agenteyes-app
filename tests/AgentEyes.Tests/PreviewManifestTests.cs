using System.IO;
using System.Text.Json;
using AgentEyes;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33 - the overlay corner on disk (AC5), and the shape of a manifest written WITHOUT one
    /// (AC11).
    ///
    /// WHAT THESE CAN AND CANNOT SEE. They are unit tests over the DATA: one manifest field, its wire
    /// spellings, and its absence when nothing was framed. They do NOT prove that a preview ran, that
    /// frames were live, or that a corner a person clicked reached the file - that needs the running
    /// app, and it is what the running-app proof is for. An empty or absent result here is a defect,
    /// never a pass.
    /// </summary>
    public sealed class PreviewManifestTests
    {
        private static string Json(Manifest m) => JsonSerializer.Serialize(m, Manifest.JsonOptions);

        private static Manifest RoundTrip(Manifest m) =>
            JsonSerializer.Deserialize<Manifest>(Json(m), Manifest.JsonOptions)!;

        // The theory takes the WIRE spelling: the corner enum is internal to the product, and xUnit
        // needs a public signature to discover a test. Round-tripping the string through the enum is
        // what proves the two agree.
        [Theory]
        [InlineData("bottom-right")]
        [InlineData("bottom-left")]
        [InlineData("top-left")]
        [InlineData("top-right")]
        public void Manifest_WithAnOverlayCorner_WritesAndReadsIt(string wire)
        {
            var m = new Manifest
            {
                Mode = "video",
                VideoFile = "recording.mp4",
                CameraFile = "camera.mp4",
                PreviewOverlayCorner = PreviewNames.Text(PreviewNames.Corner(wire)),
            };

            string json = Json(m);
            Assert.Contains($"\"PreviewOverlayCorner\": \"{wire}\"", json);
            Assert.Equal(wire, RoundTrip(m).PreviewOverlayCorner);
        }

        [Fact]
        public void Manifest_WithoutAnOverlayCorner_DoesNotWriteTheFieldAtAll()
        {
            // AC11: a recording made without the overlay is identical in manifest CONTENT to what it
            // was before this feature. Null fields are not serialised, so the property must be
            // ABSENT - not present and null, which would change every existing recording's file.
            var m = new Manifest
            {
                Mode = "video",
                VideoFile = "recording.mp4",
                CameraFile = "camera.mp4",
            };

            string json = Json(m);
            Assert.DoesNotContain("PreviewOverlayCorner", json);
            Assert.Null(RoundTrip(m).PreviewOverlayCorner);
        }

        [Fact]
        public void Manifest_WrittenBeforeThisFeatureExisted_ReadsAsNoCorner()
        {
            // Backward compatibility, stated as a presence: the rest of the record still parses and
            // the missing property reads as "no corner was framed", which is the truth about it.
            const string old = """
                {
                  "Tool": "AgentEyes",
                  "Mode": "video",
                  "VideoFile": "recording.mp4",
                  "CameraFile": "camera.mp4",
                  "CameraComplete": "yes",
                  "DurationSeconds": 31.5
                }
                """;

            var m = JsonSerializer.Deserialize<Manifest>(old, Manifest.JsonOptions)!;

            Assert.Null(m.PreviewOverlayCorner);
            Assert.Equal("camera.mp4", m.CameraFile);
            Assert.Equal("yes", m.CameraComplete);
            Assert.Equal(31.5, m.DurationSeconds);
        }

        [Fact]
        public void ManifestStore_Update_CanSetTheCornerWithoutDisturbingTheCameraRecord()
        {
            // The corner is written at the stop, into a manifest that already exists, through the
            // same read-modify-write every other stop field uses. The camera's own account of itself
            // (issue #28) must come through untouched.
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-preview-manifest", Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                ManifestStore.Replace(dir, new Manifest
                {
                    Mode = "video",
                    VideoFile = "recording.mp4",
                    CameraFile = "camera.mp4",
                    CameraStopKind = "clean-quit",
                    CameraStderrComplete = true,
                    CameraComplete = "yes",
                    CameraCapturedSeconds = 30.2,
                });

                ManifestStore.Update(dir, m => m.PreviewOverlayCorner = PreviewNames.BottomLeft);

                var read = Manifest.Load(dir);
                Assert.Equal("bottom-left", read.PreviewOverlayCorner);
                Assert.Equal("camera.mp4", read.CameraFile);
                Assert.Equal("clean-quit", read.CameraStopKind);
                Assert.Equal("yes", read.CameraComplete);
                Assert.True(read.CameraStderrComplete);
                Assert.Equal(30.2, read.CameraCapturedSeconds);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
