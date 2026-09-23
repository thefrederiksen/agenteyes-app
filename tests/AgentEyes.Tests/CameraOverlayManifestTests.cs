using System.IO;
using System.Text.Json;
using AgentEyes;
using AgentEyes.App;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #36 - what the circular overlay writes to disk, and what a recording that never used it
    /// still looks like.
    ///
    /// TWO CLAIMS ARE UNDER TEST AND THEY PULL IN OPPOSITE DIRECTIONS. AC4 wants the shape, the
    /// circle, the corner and the inset size in manifest.json. AC10 wants a recording made with the
    /// preview OFF to be byte-identical in manifest content to what it was before this feature. Both
    /// are checked here as PRESENCES: the fields are asserted by name and value when they should be
    /// there, and their ABSENCE is asserted against the serialized text - not against a null
    /// property, which would still change every existing recording's file.
    ///
    /// WHAT THESE CANNOT SEE: that a real recording actually wrote these fields, and - the claim
    /// underneath the whole design - that camera.mp4 was not cropped. Both need the running app;
    /// they are the ffprobe numbers in the proof (AC5). An empty result here is a broken instrument.
    /// </summary>
    public sealed class CameraOverlayManifestTests
    {
        private static string Json(Manifest m) => JsonSerializer.Serialize(m, Manifest.JsonOptions);

        private static Manifest RoundTrip(Manifest m) =>
            JsonSerializer.Deserialize<Manifest>(Json(m), Manifest.JsonOptions)!;

        private static Manifest VideoWithCamera() => new()
        {
            Mode = "video",
            VideoFile = "recording.mp4",
            CameraFile = "camera.mp4",
        };

        [Fact]
        public void Manifest_WithACircleOverlay_WritesShapeCircleCornerAndInset()
        {
            var framing = new CameraOverlaySettings
            {
                Shape = PreviewNames.Circle,
                Corner = PreviewNames.TopLeft,
                InsetFraction = 0.42,
                Circle = new CameraOverlayCircle { CentreX = 0.61, CentreY = 0.29, Diameter = 0.55 },
            }.Canonical();

            var m = VideoWithCamera();
            m.PreviewOverlayShape = framing.Shape;
            m.PreviewOverlayCorner = framing.Corner;
            m.PreviewOverlayInset = framing.InsetFraction;
            m.PreviewOverlayCircle = framing.Circle;

            string json = Json(m);
            Assert.Contains("\"PreviewOverlayShape\": \"circle\"", json);
            Assert.Contains("\"PreviewOverlayCorner\": \"top-left\"", json);
            Assert.Contains("\"PreviewOverlayCircle\"", json);
            Assert.Contains("\"CentreX\": 0.61", json);
            Assert.Contains("\"CentreY\": 0.29", json);
            Assert.Contains("\"Diameter\": 0.55", json);
            Assert.Contains("\"PreviewOverlayInset\": 0.42", json);

            var read = RoundTrip(m);
            Assert.Equal("circle", read.PreviewOverlayShape);
            Assert.Equal("top-left", read.PreviewOverlayCorner);
            Assert.Equal(0.42, read.PreviewOverlayInset!.Value, 6);
            Assert.NotNull(read.PreviewOverlayCircle);
            Assert.True(read.PreviewOverlayCircle!.SameAs(framing.Circle),
                $"The circle did not survive the manifest: wrote {framing.Circle}, read back "
                + $"{read.PreviewOverlayCircle}.");
        }

        [Fact]
        public void Manifest_WithARectangleOverlay_WritesNoCircleGeometry()
        {
            // A rectangle frames the whole camera frame, so there is no circle to reproduce. The
            // field is ABSENT rather than carrying numbers nothing used.
            var m = VideoWithCamera();
            m.PreviewOverlayShape = PreviewNames.Rectangle;
            m.PreviewOverlayCorner = PreviewNames.BottomRight;
            m.PreviewOverlayInset = 0.30;

            string json = Json(m);
            Assert.Contains("\"PreviewOverlayShape\": \"rectangle\"", json);
            Assert.DoesNotContain("PreviewOverlayCircle", json);
            Assert.Null(RoundTrip(m).PreviewOverlayCircle);
        }

        [Fact]
        public void Manifest_WithNoOverlayAtAll_WritesNoneOfTheOverlayFields()
        {
            // AC10. A recording made with the preview off has the manifest it always had: not the
            // fields set to null, but the fields ABSENT from the file.
            var m = VideoWithCamera();

            string json = Json(m);
            foreach (string field in new[]
            {
                "PreviewOverlayShape", "PreviewOverlayCorner", "PreviewOverlayCircle", "PreviewOverlayInset",
            })
            {
                Assert.DoesNotContain(field, json);
            }

            var read = RoundTrip(m);
            Assert.Null(read.PreviewOverlayShape);
            Assert.Null(read.PreviewOverlayCorner);
            Assert.Null(read.PreviewOverlayCircle);
            Assert.Null(read.PreviewOverlayInset);
        }

        [Fact]
        public void Manifest_WrittenBeforeThisFeature_StillLoadsWithNoOverlay()
        {
            // A real pre-#36 manifest body. It must not throw and must not invent a framing.
            const string old = """
            {
              "Tool": "AgentEyes",
              "Mode": "video",
              "VideoFile": "recording.mp4",
              "CameraFile": "camera.mp4",
              "PreviewOverlayCorner": "bottom-left"
            }
            """;

            var m = JsonSerializer.Deserialize<Manifest>(old, Manifest.JsonOptions)!;

            Assert.Equal("bottom-left", m.PreviewOverlayCorner);
            Assert.Null(m.PreviewOverlayShape);
            Assert.Null(m.PreviewOverlayCircle);
            Assert.Null(m.PreviewOverlayInset);
        }

        [Fact]
        public void ManifestStore_Update_KeepsTheOverlayGeometryItWrites()
        {
            // The stop writes the overlay through a read-modify-write of the manifest the START
            // wrote (issue #155). If the copy step forgot a field, the recording would record a
            // shape with no geometry - so each one is read back off disk.
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-overlay-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ManifestStore.Replace(dir, VideoWithCamera());
                ManifestStore.Update(dir, m =>
                {
                    m.PreviewOverlayShape = PreviewNames.Circle;
                    m.PreviewOverlayCorner = PreviewNames.TopRight;
                    m.PreviewOverlayInset = 0.25;
                    m.PreviewOverlayCircle = new CameraOverlayCircle
                    {
                        CentreX = 0.4,
                        CentreY = 0.33,
                        Diameter = 0.7,
                    };
                });

                var read = Manifest.Load(dir);
                Assert.Equal("circle", read.PreviewOverlayShape);
                Assert.Equal("top-right", read.PreviewOverlayCorner);
                Assert.Equal(0.25, read.PreviewOverlayInset!.Value, 6);
                Assert.NotNull(read.PreviewOverlayCircle);
                Assert.Equal(0.4, read.PreviewOverlayCircle!.CentreX, 6);
                Assert.Equal(0.33, read.PreviewOverlayCircle.CentreY, 6);
                Assert.Equal(0.7, read.PreviewOverlayCircle.Diameter, 6);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- the preset side of the same values ----------------------------

        [Fact]
        public void Preset_SavedBeforeThisFeature_ReadsBackAsACircleWithTheDefaults()
        {
            // AC1 for existing users: presets.json written before issue #36 has no "Overlay"
            // property at all, so the property initializer stands - the documented defaults.
            const string old = """
            [ { "Id": "abc", "Name": "Default", "Mode": "video", "Fps": 30 } ]
            """;

            var presets = JsonSerializer.Deserialize<System.Collections.Generic.List<CapturePreset>>(old)!;

            Assert.Single(presets);
            var overlay = presets[0].Overlay;
            Assert.NotNull(overlay);
            Assert.Equal(CameraOverlayShape.Circle, overlay.ShapeValue);
            Assert.Equal(0.50, overlay.Circle.CentreX, 3);
            Assert.Equal(0.42, overlay.Circle.CentreY, 3);
            Assert.Equal(0.60, overlay.Circle.Diameter, 3);
            Assert.Equal(PreviewCorner.BottomRight, overlay.CornerValue);
        }

        [Fact]
        public void Preset_RoundTripsItsOverlayThroughJson()
        {
            var p = new CapturePreset { Name = "Talking head", Mode = "video" };
            p.Overlay.Shape = PreviewNames.Rectangle;
            p.Overlay.Corner = PreviewNames.TopLeft;
            p.Overlay.InsetFraction = 0.45;
            p.Overlay.Circle.CentreX = 0.2;
            p.Overlay.Circle.CentreY = 0.8;
            p.Overlay.Circle.Diameter = 0.35;

            var read = JsonSerializer.Deserialize<CapturePreset>(JsonSerializer.Serialize(p))!;

            Assert.Equal("rectangle", read.Overlay.Shape);
            Assert.Equal("top-left", read.Overlay.Corner);
            Assert.Equal(0.45, read.Overlay.InsetFraction, 6);
            Assert.Equal(0.2, read.Overlay.Circle.CentreX, 6);
            Assert.Equal(0.8, read.Overlay.Circle.CentreY, 6);
            Assert.Equal(0.35, read.Overlay.Circle.Diameter, 6);
        }

        [Fact]
        public void Preset_Clone_CopiesTheOverlayInsteadOfSharingIt()
        {
            var p = new CapturePreset();
            var clone = p.Clone();

            clone.Overlay.Circle.CentreY = 0.9;
            clone.Overlay.Shape = PreviewNames.Rectangle;

            Assert.Equal(0.42, p.Overlay.Circle.CentreY, 3);
            Assert.Equal("circle", p.Overlay.Shape);
        }
    }
}
