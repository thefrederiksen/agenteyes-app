using System;
using System.Drawing;
using AgentEyes.Platform.Mac;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class MacFfmpegArgsTests
    {
        private static readonly Size Screen = new(1920, 1080);
        private static readonly Rectangle FullScreen = new(0, 0, 1920, 1080);

        [Fact]
        public void AvfVideoCapture_WholeScreenAndMic_ExactCommandLine()
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 1, Screen, 0, 30, 23, "/r/raw.mp4");

            Assert.Equal(new[]
            {
                "-y", "-f", "avfoundation", "-thread_queue_size", "1024", "-capture_cursor", "1",
                "-framerate", "30", "-i", "1:none",
                "-f", "avfoundation", "-thread_queue_size", "1024", "-audio_buffer_size", "20", "-i", ":0",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
                "-c:a", "aac", "-b:a", "128k",
                "/r/raw.mp4",
            }, args);
        }

        [Fact]
        public void AvfVideoCapture_WholeScreen_NeedsNoFilterAtAll()
        {
            // Cropping the whole display to itself would cost a filter pass for nothing.
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 0, Screen, null, 30, 23, "/r/raw.mp4");

            Assert.DoesNotContain("-vf", args);
        }

        [Fact]
        public void AvfVideoCapture_NoMic_HasNoAudioInputOrCodec()
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 0, Screen, null, 30, 23, "/r/raw.mp4");

            Assert.DoesNotContain(":0", args);
            Assert.DoesNotContain("aac", args);
            Assert.DoesNotContain("-audio_buffer_size", args);
        }

        [Fact]
        public void AvfVideoCapture_Region_BecomesACropFilterNotAnInputOffset()
        {
            // The structural difference from gdigrab and x11grab: AVFoundation grabs the display and
            // the region is a filter. The input must stay the whole display.
            var args = FfmpegArgs.AvfVideoCapture(new Rectangle(100, 200, 640, 480), 0, Screen, null, 30, 23, "/r/raw.mp4");

            Assert.Contains("-vf", args);
            Assert.Contains("crop=640:480:100:200", args);
            Assert.Contains("0:none", args);
        }

        [Fact]
        public void AvfVideoCapture_ScreenIndexIsUsedVerbatim()
        {
            // Index 2 means ffmpeg's device 2, not the third screen: addressing by position would
            // record the wrong monitor.
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 2, Screen, null, 30, 23, "/r/raw.mp4");

            Assert.Contains("2:none", args);
        }

        [Fact]
        public void AvfVideoCapture_NegativeIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FfmpegArgs.AvfVideoCapture(FullScreen, -1, Screen, null, 30, 23, "/r/raw.mp4"));
        }

        [Fact]
        public void AvfVideoCapture_PreviewRequested_AppendsThePreviewOutputLast()
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 0, Screen, null, 30, 23, "/r/raw.mp4", previewStream: true);
            var preview = FfmpegArgs.PreviewOutput();

            Assert.Equal(preview, args.GetRange(args.Count - preview.Count, preview.Count));
        }

        // ---- the clamp-and-pad of an oversized region (issue #69's macOS face) ----

        [Fact]
        public void CropAndPad_RegionInsideTheDisplay_CropsAndDoesNotPad()
        {
            var (crop, pad) = FfmpegArgs.CropAndPad(new Rectangle(10, 20, 640, 480), Screen);

            Assert.Equal("crop=640:480:10:20", crop);
            Assert.Null(pad);
        }

        [Fact]
        public void CropAndPad_SocialFormatTallerThanTheDisplay_ClampsThenPadsBackToTheAskedSize()
        {
            // 1080x1920 vertical on a 1080-high display: only 1080x1080 exists to grab, and the
            // result is padded back to the exact asked-for size - the same contract as Windows.
            var (crop, pad) = FfmpegArgs.CropAndPad(new Rectangle(0, 0, 1080, 1920), Screen);

            Assert.Equal("crop=1080:1080:0:0", crop);
            Assert.Equal("pad=1080:1920:0:0:black", pad);
        }

        [Fact]
        public void CropAndPad_OddSizedRegion_IsEvenizedForYuv420p()
        {
            var (crop, _) = FfmpegArgs.CropAndPad(new Rectangle(0, 0, 641, 481), Screen);

            Assert.Equal("crop=640:480:0:0", crop);
        }

        [Fact]
        public void CropAndPad_RegionCompletelyOffTheDisplay_ThrowsAndSaysWhy()
        {
            var ex = Assert.Throws<UsageException>(
                () => FfmpegArgs.CropAndPad(new Rectangle(5000, 5000, 640, 480), Screen));

            Assert.Contains("spans two monitors", ex.Message);
        }

        [Fact]
        public void FilterChain_CropAndPad_AreJoinedInThatOrder()
        {
            // Order matters: cropping after padding would cut the padding back off.
            Assert.Equal("crop=1:2:3:4,pad=5:6:7:8:black", FfmpegArgs.FilterChain("crop=1:2:3:4", "pad=5:6:7:8:black"));
        }

        [Fact]
        public void FilterChain_EitherAlone_IsThatOneUnchanged()
        {
            Assert.Equal("crop=1:2:3:4", FfmpegArgs.FilterChain("crop=1:2:3:4", null));
            Assert.Equal("pad=5:6:7:8:black", FfmpegArgs.FilterChain(null, "pad=5:6:7:8:black"));
            Assert.Null(FfmpegArgs.FilterChain(null, null));
        }

        // ---- camera and stills ----

        [Fact]
        public void AvfCameraCapture_ExactCommandLine_AndIsVideoOnly()
        {
            var args = FfmpegArgs.AvfCameraCapture(0, 30, 23, "/r/camera.mp4");

            Assert.Equal(new[]
            {
                "-y", "-f", "avfoundation", "-thread_queue_size", "1024", "-framerate", "30",
                "-i", "0:none",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
                "-an", "/r/camera.mp4",
            }, args);
        }

        [Fact]
        public void AvfCameraCapture_NegativeIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.AvfCameraCapture(-1, 30, 23, "/r/c.mp4"));
        }

        [Fact]
        public void ScreencaptureStill_IsSilentPngOfTheRegion()
        {
            var args = FfmpegArgs.ScreencaptureStill(new Rectangle(10, 20, 640, 480), "/r/shot.png");

            Assert.Equal(new[] { "-x", "-t", "png", "-R", "10,20,640,480", "/r/shot.png" }, args);
        }

        [Fact]
        public void ScreencaptureStill_OddRegion_IsEvenized()
        {
            var args = FfmpegArgs.ScreencaptureStill(new Rectangle(0, 0, 641, 481), "/r/shot.png");

            Assert.Contains("0,0,640,480", args);
        }

        // ---- the live microphone stream ----

        [Fact]
        public void AvfStreamArguments_AreRawMonoPcmOnStdout()
        {
            var args = AvfStream.Arguments(1, 48000, 1);

            Assert.Equal(new[]
            {
                "-hide_banner", "-nostats", "-loglevel", "warning",
                "-f", "avfoundation", "-audio_buffer_size", "20", "-i", ":1",
                "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1",
            }, args);
        }

        [Fact]
        public void AvfStreamArguments_StereoSystemCapture_AsksForTwoChannels()
        {
            var args = AvfStream.Arguments(2, 48000, 2);

            Assert.Contains("-ac", args);
            Assert.Equal("2", args[System.Array.IndexOf(args, "-ac") + 1]);
        }
    }
}
