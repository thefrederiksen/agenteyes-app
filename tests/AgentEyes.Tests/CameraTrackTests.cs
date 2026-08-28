using System.IO;
using System.Text.Json;
using AgentEyes;
using AgentEyes.App;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #28 - the camera track's record on disk and on the preset.
    ///
    /// WHAT THESE CAN AND CANNOT SEE. They are unit tests over the DATA the feature adds: the four
    /// manifest fields, the two preset fields, and the open-failure diagnosis. They do NOT prove that
    /// a camera was recorded - that needs a camera, a running app and ffprobe, and it is what the
    /// running-app proof (AC3/AC9/AC10) is for. Each test below states the bad result it would show,
    /// and an EMPTY/absent result is treated as a defect, never as a pass.
    /// </summary>
    public sealed class CameraTrackTests
    {
        // ---- manifest ------------------------------------------------------

        private static Manifest RoundTrip(Manifest m)
        {
            string json = JsonSerializer.Serialize(m, Manifest.JsonOptions);
            return JsonSerializer.Deserialize<Manifest>(json, Manifest.JsonOptions)!;
        }

        private static string Json(Manifest m) => JsonSerializer.Serialize(m, Manifest.JsonOptions);

        [Fact]
        public void Manifest_WithACameraTrack_RoundTripsEveryCameraField()
        {
            // AC4. Note the ON-DISK spelling is PascalCase, as every other manifest property has
            // always been (see the committed fixtures) - the issue text writes the names in
            // camelCase prose, including the pre-existing "files" array.
            var m = new Manifest
            {
                Mode = "video",
                VideoFile = "recording.mp4",
                CameraFile = "camera.mp4",
                CameraStartOffsetSeconds = -0.421,
                CameraCapturedSeconds = 12.34,
                CameraTruncated = false,
            };
            m.Files.Add("recording.mp4");
            m.Files.Add("camera.mp4");

            string json = Json(m);
            Assert.Contains("\"CameraFile\": \"camera.mp4\"", json);
            Assert.Contains("\"CameraStartOffsetSeconds\"", json);
            Assert.Contains("\"camera.mp4\"", json);

            var back = RoundTrip(m);
            Assert.Equal("camera.mp4", back.CameraFile);
            Assert.Equal(-0.421, back.CameraStartOffsetSeconds);
            Assert.Equal(12.34, back.CameraCapturedSeconds);
            Assert.False(back.CameraTruncated);
            Assert.Contains("camera.mp4", back.Files);
        }

        [Fact]
        public void Manifest_WithNoCamera_WritesNoCameraProperties()
        {
            // AC11: a recording made with no camera keeps exactly today's manifest shape. The bad
            // result this catches is a non-nullable camera field defaulting to 0/false and appearing
            // in EVERY manifest, which would make "did this recording have a camera?" unanswerable.
            var m = new Manifest { Mode = "video", VideoFile = "recording.mp4" };
            m.Files.Add("recording.mp4");

            string json = Json(m);
            Assert.DoesNotContain("Camera", json);
            Assert.DoesNotContain("camera.mp4", json);
        }

        [Fact]
        public void Manifest_ATruncatedCameraTrack_RecordsTheSecondsActuallyCaptured()
        {
            // AC10: the camera died 4.2s into a 30s session. The manifest must say BOTH that the
            // track is truncated and how much of it exists - "truncated" with no number would leave
            // an editor guessing, and a number with no flag would look like a complete short take.
            var m = new Manifest
            {
                Mode = "video",
                DurationSeconds = 30.0,
                VideoFile = "recording.mp4",
                CameraFile = "camera.mp4",
                CameraCapturedSeconds = 4.2,
                CameraTruncated = true,
            };

            var back = RoundTrip(m);
            Assert.True(back.CameraTruncated);
            Assert.Equal(4.2, back.CameraCapturedSeconds);
            Assert.Equal(30.0, back.DurationSeconds);   // the screen recording is untouched
        }

        [Fact]
        public void Manifest_AnOlderRecordWithNoCameraProperties_LoadsAsNoCameraTrack()
        {
            // Backward compatibility: a manifest.json written before this feature existed has none of
            // these properties, and must read as "no camera" rather than throwing.
            const string legacy = "{ \"Tool\": \"AgentEyes\", \"Mode\": \"video\", \"VideoFile\": \"recording.mp4\" }";

            var m = JsonSerializer.Deserialize<Manifest>(legacy, Manifest.JsonOptions)!;

            Assert.Null(m.CameraFile);
            Assert.Null(m.CameraStartOffsetSeconds);
            Assert.Null(m.CameraCapturedSeconds);
            Assert.Null(m.CameraTruncated);
        }

        [Fact]
        public void Manifest_CameraFields_SurviveTheStoreOnDisk()
        {
            // The fields go through the real writer (ManifestStore), not just the serializer, because
            // that is the path a recording actually takes.
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyesCameraTrackTest_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var m = new Manifest
                {
                    Mode = "video",
                    VideoFile = "recording.mp4",
                    CameraFile = "camera.mp4",
                    CameraStartOffsetSeconds = -0.4,
                    CameraCapturedSeconds = 9.5,
                    CameraTruncated = false,
                };
                m.Files.Add("camera.mp4");
                ManifestStore.Replace(dir, m);

                var loaded = Manifest.Load(dir);
                Assert.Equal("camera.mp4", loaded.CameraFile);
                Assert.Equal(-0.4, loaded.CameraStartOffsetSeconds);
                Assert.Equal(9.5, loaded.CameraCapturedSeconds);
                Assert.False(loaded.CameraTruncated);
                Assert.Contains("camera.mp4", loaded.Files);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ---- preset --------------------------------------------------------

        [Fact]
        public void CapturePreset_Clone_CarriesTheCameraFields()
        {
            var p = new CapturePreset { Name = "Talking head", Camera = "HD Webcam", CameraFps = 24 };

            var clone = p.Clone();

            Assert.Equal("HD Webcam", clone.Camera);
            Assert.Equal(24, clone.CameraFps);
        }

        [Fact]
        public void CapturePreset_Default_HasNoCamera()
        {
            // The camera is opt-in. A recorder that filmed the user by default would violate the
            // product's whole posture.
            Assert.Null(new CapturePreset().Camera);
        }

        [Fact]
        public void CapturePreset_Summary_NamesTheCameraOnAVideoPreset()
        {
            var p = new CapturePreset { Mode = "video", Source = "mic", Mic = "Yeti", Camera = "HD Webcam", CameraFps = 24 };

            string summary = p.Summary();

            Assert.Contains("HD Webcam", summary);
            Assert.Contains("24fps", summary);
        }

        [Fact]
        public void CapturePreset_Summary_SaysSoWhenThereIsNoCamera()
        {
            var p = new CapturePreset { Mode = "video", Source = "mic", Mic = "Yeti", Camera = null };

            Assert.Contains("no camera", p.Summary());
        }

        [Fact]
        public void CapturePreset_Summary_ShotMode_MentionsNoCamera()
        {
            // Assumption A1: the camera is a video-mode setting, so a screenshot preset must not
            // advertise one even if the field is set.
            var p = new CapturePreset { Mode = "shot", Camera = "HD Webcam" };

            Assert.DoesNotContain("camera", p.Summary(), System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CapturePreset_SavedAndReloaded_KeepsItsCamera()
        {
            // AC6's persistence half, at the serialization level: presets.json is a plain
            // System.Text.Json list, so this is the exact round trip the store performs.
            var p = new CapturePreset { Name = "Talking head", Mode = "video", Camera = "Logitech BRIO 4K", CameraFps = 24 };

            string json = JsonSerializer.Serialize(new[] { p });
            var back = JsonSerializer.Deserialize<CapturePreset[]>(json)!;

            Assert.Equal("Logitech BRIO 4K", back[0].Camera);
            Assert.Equal(24, back[0].CameraFps);
        }

        [Fact]
        public void CapturePreset_SavedBeforeThisFeature_LoadsWithNoCameraAndTheDefaultFps()
        {
            const string legacy = "[{\"Name\":\"Default\",\"Mode\":\"video\",\"Fps\":30}]";

            var back = JsonSerializer.Deserialize<CapturePreset[]>(legacy)!;

            Assert.Null(back[0].Camera);
            Assert.Equal(30, back[0].CameraFps);
        }

        // ---- open-failure diagnosis ----------------------------------------

        [Fact]
        public void DiagnoseOpenFailure_BusyDevice_SaysAnotherApplicationHasIt()
        {
            // AC9's message. ffmpeg reports a camera held by another app as a filter/I/O failure.
            string cause = FfmpegCameraRecorder.DiagnoseOpenFailure(
                "[dshow @ 0001] Could not run filter\nvideo=HD Webcam: I/O error", "HD Webcam");

            Assert.Contains("already in use", cause);
            Assert.Contains("HD Webcam", cause);
        }

        [Fact]
        public void DiagnoseOpenFailure_TheRealFfmpegBusyCameraOutput_SaysAnotherApplicationHasIt()
        {
            // REGRESSION, and the actual bytes rather than a paraphrase. This is ffmpeg 9.0's
            // verbatim stderr for a webcam held by another process, captured on 2026-08-28 while
            // implementing this issue (a browser had the eMeet C960). The first version of
            // DiagnoseOpenFailure looked for "Could not run FILTER" and did not match this at all,
            // so a user whose camera was simply open elsewhere - the most likely failure there is -
            // got "see the application log" instead of the one sentence that fixes it.
            const string realStderr =
                "[dshow @ 000001c99aa571c0] Could not run graph (sometimes caused by a device already in use by other application)\n" +
                "[in#0 @ 000001c99aa56f80] Error opening input: I/O error\n" +
                "Error opening input file video=HD Webcam eMeet C960.\n" +
                "Error opening input files: I/O error\n";

            string cause = FfmpegCameraRecorder.DiagnoseOpenFailure(realStderr, "HD Webcam eMeet C960");

            Assert.Contains("already in use", cause);
            Assert.Contains("HD Webcam eMeet C960", cause);
        }

        [Fact]
        public void DiagnoseOpenFailure_MissingDevice_SaysItIsGone()
        {
            string cause = FfmpegCameraRecorder.DiagnoseOpenFailure(
                "[dshow @ 0001] Could not find video device with name [HD Webcam]", "HD Webcam");

            Assert.Contains("unplugged", cause);
            Assert.Contains("HD Webcam", cause);
        }

        [Fact]
        public void DiagnoseOpenFailure_UnrecognizedError_PointsAtTheLogAndDoesNotInvent()
        {
            // The honest arm: when the stderr says nothing this method recognizes, it must NOT guess
            // a cause. A confident wrong diagnosis is worse than "see the log".
            string cause = FfmpegCameraRecorder.DiagnoseOpenFailure("something nobody has seen before", "HD Webcam");

            Assert.Contains("application log", cause);
            Assert.DoesNotContain("already in use", cause);
            Assert.DoesNotContain("unplugged", cause);
        }

        [Fact]
        public void DiagnoseOpenFailure_NullStderr_DoesNotThrow()
        {
            Assert.False(string.IsNullOrWhiteSpace(FfmpegCameraRecorder.DiagnoseOpenFailure(null!, "HD Webcam")));
        }
    }
}
