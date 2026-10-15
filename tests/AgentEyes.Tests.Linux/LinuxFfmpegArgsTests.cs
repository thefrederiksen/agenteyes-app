using System.Drawing;
using AgentEyes.Platform.Linux;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class LinuxFfmpegArgsTests
    {
        private static readonly Rectangle Desktop = new(0, 0, 1920, 1080);

        [Fact]
        public void X11VideoCapture_ScreenAndMic_ExactCommandLine()
        {
            var args = FfmpegArgs.X11VideoCapture(Desktop, ":0", "alsa_input.mic", 30, 23, "/r/raw.mp4", Desktop);

            Assert.Equal(new[]
            {
                "-y", "-f", "x11grab", "-thread_queue_size", "1024", "-framerate", "30",
                "-video_size", "1920x1080", "-draw_mouse", "1", "-i", ":0+0,0",
                "-f", "pulse", "-thread_queue_size", "1024", "-fragment_size", "3840", "-i", "alsa_input.mic",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
                "-c:a", "aac", "-b:a", "128k",
                "/r/raw.mp4",
            }, args);
        }

        [Fact]
        public void X11VideoCapture_NoMic_HasNoAudioInputOrCodec()
        {
            var args = FfmpegArgs.X11VideoCapture(Desktop, ":0", null, 30, 23, "/r/raw.mp4", Desktop);

            Assert.DoesNotContain("pulse", args);
            Assert.DoesNotContain("aac", args);
        }

        [Fact]
        public void X11VideoCapture_RegionOffset_GoesIntoTheGrabInput()
        {
            var args = FfmpegArgs.X11VideoCapture(new Rectangle(100, 200, 640, 480), ":1.0", null, 15, 23, "/o.mp4", Desktop);

            Assert.Contains(":1.0+100,200", args);
            Assert.Contains("640x480", args);
        }

        [Fact]
        public void X11VideoCapture_RegionPastTheDesktop_IsGrabbedClampedAndPaddedBack()
        {
            // a 1080x1920 vertical on a 1080-tall desktop (the issue #69 case)
            var args = FfmpegArgs.X11VideoCapture(new Rectangle(0, 0, 1080, 1920), ":0", null, 30, 23, "/o.mp4", Desktop);

            Assert.Contains("1080x1080", args);
            Assert.Contains("pad=1080:1920:0:0:black", args);
        }

        [Fact]
        public void X11VideoCapture_Preview_AppendsThePreviewOutput()
        {
            var args = FfmpegArgs.X11VideoCapture(Desktop, ":0", null, 30, 23, "/o.mp4", Desktop, previewStream: true);
            Assert.Equal(FfmpegArgs.PreviewOutput(), args.GetRange(args.Count - FfmpegArgs.PreviewOutput().Count, FfmpegArgs.PreviewOutput().Count));
        }

        [Fact]
        public void V4l2CameraCapture_DeviceNode_VideoOnly()
        {
            var args = FfmpegArgs.V4l2CameraCapture("/dev/video0", 30, 23, "/r/camera.mp4");

            Assert.Equal(new[]
            {
                "-y", "-f", "v4l2", "-thread_queue_size", "1024", "-framerate", "30", "-i", "/dev/video0",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23", "-an", "/r/camera.mp4",
            }, args);
        }

        [Fact]
        public void V4l2CameraCapture_NotADeviceNode_Throws()
        {
            Assert.Throws<UsageException>(() => FfmpegArgs.V4l2CameraCapture("HD Pro Webcam C920", 30, 23, "/o.mp4"));
        }

        [Fact]
        public void X11Still_OneFrameOfTheRegion()
        {
            var args = FfmpegArgs.X11Still(new Rectangle(10, 20, 101, 51), ":0", "/s/shot.png");

            Assert.Contains("-frames:v", args);
            Assert.Contains(":0+10,20", args);
            Assert.Equal("/s/shot.png", args[^1]);
        }

        [Fact]
        public void GrabInput_DisplayAndOffset()
        {
            Assert.Equal(":0+5,-7", LinuxDisplay.GrabInput(":0", 5, -7));
        }

        [Fact]
        public void PulseStreamArguments_SourceRateChannels_RawS16ToStdout()
        {
            var args = PulseStream.Arguments("alsa_output.x.monitor", 48000, 2);

            Assert.Equal(new[]
            {
                "-hide_banner", "-nostats", "-loglevel", "warning", "-f", "pulse", "-fragment_size", "3840",
                "-i", "alsa_output.x.monitor", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1",
            }, args);
        }

        [Fact]
        public void Measure_S16Block_PeakAndEnergy()
        {
            // samples: 0, 16384 (0.5), -32768 (1.0)
            var buf = new byte[] { 0, 0, 0x00, 0x40, 0x00, 0x80 };
            var level = PulseStream.Measure(buf, buf.Length);

            Assert.Equal(1.0f, level.Peak);
            Assert.Equal(3, level.Samples);
            Assert.Equal(1.25, level.SumSquares, 6);
        }

        [Fact]
        public void IsInputOpenReport_V4l2Header_IsTheOpenReport()
        {
            Assert.True(FfmpegCameraRecorder.IsInputOpenReport("Input #0, video4linux2,v4l2, from '/dev/video0':"));
            Assert.False(FfmpegCameraRecorder.IsInputOpenReport("Input #0, dshow, from 'video=Cam':"));
        }
    }
}
