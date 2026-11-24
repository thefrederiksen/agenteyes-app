using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes.Headless;
using AgentEyes.Takes;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>Issues #117, #229 and #230: the camera mode chooser, the camera+mic command line,
    /// the take store, the teleprompter's phrases and the --camera-only CLI flag.</summary>
    public class TakesTests
    {
        // An excerpt of the real `ffmpeg -f dshow -list_options true -i video="HD Pro Webcam C920"`
        // on SOREN_NORTH (2026-11-24).
        private const string C920 = @"
[in#0 @ 0001] DirectShow video device options (from video devices)
[in#0 @ 0001]  Pin ""Capture"" (alternative pin name ""0"")
[in#0 @ 0001]   pixel_format=yuyv422  min s=640x480 fps=5 max s=640x480 fps=30
[in#0 @ 0001]   pixel_format=yuyv422  min s=640x480 fps=5 max s=640x480 fps=30 (tv, bt470bg/bt709/unknown, topleft)
[in#0 @ 0001]   pixel_format=yuyv422  min s=1280x720 fps=5 max s=1280x720 fps=10
[in#0 @ 0001]   pixel_format=yuyv422  min s=1600x896 fps=5 max s=1600x896 fps=7.5
[in#0 @ 0001]   pixel_format=yuyv422  min s=1920x1080 fps=5 max s=1920x1080 fps=5
[in#0 @ 0001]   pixel_format=yuyv422  min s=2304x1536 fps=2 max s=2304x1536 fps=2
[in#0 @ 0001]   vcodec=h264  min s=1920x1080 fps=5 max s=1920x1080 fps=30
[in#0 @ 0001]   vcodec=mjpeg  min s=640x480 fps=5 max s=640x480 fps=30
[in#0 @ 0001]   vcodec=mjpeg  min s=1280x720 fps=5 max s=1280x720 fps=30
[in#0 @ 0001]   vcodec=mjpeg  min s=1920x1080 fps=5 max s=1920x1080 fps=30
[in#0 @ 0001]   vcodec=mjpeg  min s=1920x1080 fps=5 max s=1920x1080 fps=30 (pc, bt470bg/bt709/unknown, center)
";

        [Fact]
        public void Parse_C920_ReadsEveryModeOnce()
        {
            var modes = CameraModes.Parse(C920);
            Assert.Equal(9, modes.Count);
            Assert.Contains(new CameraMode(null, "yuyv422", 1600, 896, 7.5), modes);
            Assert.Contains(new CameraMode("mjpeg", null, 1920, 1080, 30), modes);
        }

        [Fact]
        public void Choose_C920At30_PicksMjpeg1080()
        {
            var mode = CameraModes.Choose(CameraModes.Parse(C920), 30);
            Assert.Equal("mjpeg 1920x1080@30", mode.Describe());
        }

        [Fact]
        public void Choose_RawOnlyCamera_PicksTheLargestRawModeThatReachesTheRate()
        {
            var modes = CameraModes.Parse(@"
  pixel_format=yuyv422  min s=640x480 fps=5 max s=640x480 fps=30
  pixel_format=yuyv422  min s=1280x720 fps=5 max s=1280x720 fps=30
  pixel_format=yuyv422  min s=1920x1080 fps=5 max s=1920x1080 fps=5");
            Assert.Equal("yuyv422 1280x720@30", CameraModes.Choose(modes, 30).Describe());
        }

        [Fact]
        public void Choose_SizeTheCameraDoesNotOffer_IsRefusedWithTheModesItDoes()
        {
            var ex = Assert.Throws<UsageException>(() => CameraModes.Choose(CameraModes.Parse(C920), 30, 3840, 2160));
            Assert.Contains("3840x2160", ex.Message);
            Assert.Contains("mjpeg 1920x1080@30", ex.Message);
        }

        [Fact]
        public void Choose_NoModes_Throws()
        {
            Assert.Throws<UsageException>(() => CameraModes.Choose(Array.Empty<CameraMode>(), 30));
        }

        [Fact]
        public void CameraCapture_WithModeAndMic_RequestsTheModeAndRecordsTheMicInOneInput()
        {
            var args = FfmpegArgs.CameraCapture("HD Pro Webcam C920", 30, 20, "recording.mp4",
                mode: new CameraMode("mjpeg", null, 1920, 1080, 30), dshowMic: "Microphone (FDUCE SL40 Audio Device)");
            string s = string.Join(" ", args);
            Assert.Contains("-rtbufsize 512M -vcodec mjpeg -video_size 1920x1080", s);
            Assert.Contains("video=HD Pro Webcam C920:audio=Microphone (FDUCE SL40 Audio Device)", args);
            Assert.Contains("-c:a aac", s);
            Assert.DoesNotContain("-an", args);
            Assert.Equal("recording.mp4", args.Last());
        }

        [Fact]
        public void CameraCapture_RawMode_RequestsThePixelFormat()
        {
            var args = FfmpegArgs.CameraCapture("Cam", 30, 23, "camera.mp4", mode: new CameraMode(null, "yuyv422", 1280, 720, 30));
            Assert.Contains("-pixel_format yuyv422 -video_size 1280x720", string.Join(" ", args));
            Assert.Contains("-an", args);
        }

        private static TakeStore NewStore(out string path)
        {
            path = Path.Combine(Path.GetTempPath(), "agenteyes-takes-" + Guid.NewGuid().ToString("N"), "takes.json");
            return new TakeStore(path);
        }

        private static TakeList List(params (string id, string text)[] takes) => new()
        {
            Title = "Test",
            Takes = takes.Select(t => new Take { Id = t.id, Text = t.text, TargetSeconds = 10 }).ToList(),
        };

        [Fact]
        public void TakeStore_AttemptsAreNumberedPerTakeAndSurviveAReload()
        {
            var store = NewStore(out string path);
            try
            {
                store.Load(List(("open", "Hello."), ("close", "Bye.")));
                Assert.Equal(1, store.AddAttempt("open", "a.mp4", "d1", 12.3).Number);
                Assert.Equal(2, store.AddAttempt("open", "b.mp4", "d2", 11).Number);
                Assert.Equal(1, store.AddAttempt("close", "c.mp4", "d3", 4).Number);
                store.Choose("open", 2);

                // A video session re-sends its list: the attempts of unchanged ids stay.
                store.Load(List(("open", "Hello again."), ("new", "New.")));
                var reread = new TakeStore(path).Snapshot();
                var open = reread.Takes.Single(t => t.Id == "open");
                Assert.Equal("Hello again.", open.Text);
                Assert.Equal(new[] { 1, 2 }, open.Attempts.Select(a => a.Number));
                Assert.Equal(2, open.Chosen);
                Assert.Empty(reread.Takes.Single(t => t.Id == "new").Attempts);
                Assert.DoesNotContain(reread.Takes, t => t.Id == "close");
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Fact]
        public void TakeStore_ChooseAnAttemptThatDoesNotExist_Throws()
        {
            var store = NewStore(out string path);
            try
            {
                store.Load(List(("open", "Hello.")));
                Assert.Throws<UsageException>(() => store.Choose("open", 1));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Theory]
        [InlineData("", "x")]
        [InlineData("a", "")]
        public void TakeStore_TakeWithoutIdOrText_IsRefused(string id, string text)
        {
            var store = NewStore(out string path);
            try { Assert.Throws<UsageException>(() => store.Load(List((id, text)))); }
            finally { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Fact]
        public void TakeStore_Parse_ReadsTheControlApiBody()
        {
            var list = TakeStore.Parse(@"{""title"":""Video"",""readingMode"":""teleprompter"",
                ""takes"":[{""id"":""open"",""title"":""The opening"",""text"":""Hi."",""targetSeconds"":15,
                ""instructions"":""Look at the camera"",""background"":""DevThrottle black""}]}");
            Assert.Equal(TakeStore.Teleprompter, list.ReadingMode);
            Assert.Equal(15, list.Takes[0].TargetSeconds);
            Assert.Equal("DevThrottle black", list.Takes[0].Background);
        }

        [Fact]
        public void Phrases_SplitsAtSentencesAndLongSentencesAtCommas()
        {
            var phrases = TakeStore.Phrases(
                "In October I merged more than two thousand changes. In one month. "
                + "Before I had a software factory, I merged fewer than seventy a month.");
            Assert.Equal(new[]
            {
                "In October I merged more than two thousand changes.",
                "In one month.",
                "Before I had a software factory,",
                "I merged fewer than seventy a month.",
            }, phrases);
        }

        [Fact]
        public void StartBody_CameraOnly_SendsTheModeAndSize()
        {
            var body = HeadlessCli.StartBody(CliArgs.Parse(new[]
                { "--camera-only", "--camera", "C920", "--mic", "FDUCE", "--camera-size", "1920x1080" }));
            Assert.Equal(RecordingService.CameraOnlyMode, body["mode"]);
            Assert.Equal(1920, body["cameraWidth"]);
            Assert.Equal(1080, body["cameraHeight"]);
        }

        [Fact]
        public void StartBody_CameraOnlyWithLoopback_IsRefused()
        {
            Assert.Throws<UsageException>(() => HeadlessCli.StartBody(CliArgs.Parse(new[]
                { "--camera-only", "--camera", "C920", "--loopback" })));
        }
    }
}
