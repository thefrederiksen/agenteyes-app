using System;
using System.Drawing;
using System.Linq;
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
        public void AvfVideoCapture_WholeScreen_ExactCommandLine()
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 1, Screen, 30, 23, "/r/raw.mp4");

            Assert.Equal(new[]
            {
                "-y", "-f", "avfoundation", "-thread_queue_size", "1024", "-capture_cursor", "1",
                // nv12 is the 4:2:0 format the encoder wants and the display offers, instead of the
                // demuxer's own yuv420p default that no display supports (issue #114).
                "-pixel_format", "nv12",
                "-framerate", "30", "-i", "1:none",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
                // The output's declared rate. Without it the mp4 carried "1000k fps, 1000k tbn",
                // which x264 sized the file's level from (issue #114).
                "-r", "30",
                "/r/raw.mp4",
            }, args);
        }

        [Fact]
        public void AvfVideoCapture_WholeScreen_NeedsNoFilterAtAll()
        {
            // Cropping the whole display to itself would cost a filter pass for nothing.
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 0, Screen, 30, 23, "/r/raw.mp4");

            Assert.DoesNotContain("-vf", args);
        }

        // ---- issue #114: the microphone is NOT an input of this process ----

        /// <summary>
        /// THE REGRESSION TEST FOR ISSUE #114, and it fails against the argument list that shipped.
        /// That list was "-f avfoundation -thread_queue_size 1024 -i :0" appended as a second input
        /// plus "-c:a aac -b:a 128k", which cost ~88% of the recording's audio. A screen capture
        /// command line may now contain NO audio input, NO audio codec and NO audio bitrate: the
        /// macOS microphone is captured by its own ffmpeg (AvfStream) and muxed in afterwards.
        ///
        /// Asserted as a PRESENCE, not an absence (DEVELOPMENT_METHOD 6c): the list is first required
        /// to be a real AVFoundation screen capture, so a builder that returned nothing at all - or
        /// an exception swallowed into an empty list - fails here instead of passing for free.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void AvfVideoCapture_NeverCapturesTheMicrophone_WhateverTheScreen(int screenIndex)
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, screenIndex, Screen, 30, 23, "/r/raw.mp4");

            // It IS a screen capture (the instrument works).
            Assert.Contains("-f", args);
            Assert.Contains("avfoundation", args);
            Assert.Contains(screenIndex + ":none", args);
            Assert.Contains("libx264", args);
            Assert.Equal("/r/raw.mp4", args[^1]);

            // Exactly ONE input, and it is the display.
            Assert.Equal(1, args.Count(a => a == "-i"));

            // And nothing audio anywhere in it.
            Assert.DoesNotContain(":0", args);
            Assert.DoesNotContain(":1", args);
            Assert.DoesNotContain("-c:a", args);
            Assert.DoesNotContain("-b:a", args);
            Assert.DoesNotContain("aac", args);
            Assert.DoesNotContain("-audio_buffer_size", args);
        }

        /// <summary>
        /// The signature itself is the guard: AvfVideoCapture has no microphone parameter at all, so
        /// no caller can put one back without changing this file. A reflection check, because a
        /// compile error is only visible to whoever compiles it.
        /// </summary>
        [Fact]
        public void AvfVideoCapture_HasNoMicrophoneParameter()
        {
            var method = typeof(FfmpegArgs).GetMethod(
                nameof(FfmpegArgs.AvfVideoCapture),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            Assert.NotNull(method);
            var names = method!.GetParameters().Select(p => p.Name!).ToArray();
            Assert.NotEmpty(names);
            Assert.DoesNotContain(names, n => n.Contains("mic", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("audio", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The frame rate the preset asked for is declared on the output, not left to the demuxer's
        /// microsecond timebase (issue #114, acceptance criterion 1/5).
        /// </summary>
        [Theory]
        [InlineData(10)]
        [InlineData(30)]
        [InlineData(60)]
        public void AvfVideoCapture_DeclaresTheRequestedRateOnTheOutput(int fps)
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 0, Screen, fps, 23, "/r/raw.mp4");

            // Asked of the device...
            int framerate = args.IndexOf("-framerate");
            Assert.True(framerate >= 0);
            Assert.Equal(fps.ToString(), args[framerate + 1]);

            // ...and declared on the output, after the encoder settings and before the file.
            int r = args.IndexOf("-r");
            Assert.True(r > framerate, "-r belongs on the output, after the input");
            Assert.Equal(fps.ToString(), args[r + 1]);
            Assert.True(r + 1 < args.IndexOf("/r/raw.mp4"));
        }

        [Fact]
        public void AvfVideoCapture_Region_BecomesACropFilterNotAnInputOffset()
        {
            // The structural difference from gdigrab and x11grab: AVFoundation grabs the display and
            // the region is a filter. The input must stay the whole display.
            var args = FfmpegArgs.AvfVideoCapture(new Rectangle(100, 200, 640, 480), 0, Screen, 30, 23, "/r/raw.mp4");

            Assert.Contains("-vf", args);
            Assert.Contains("crop=640:480:100:200", args);
            Assert.Contains("0:none", args);
        }

        [Fact]
        public void AvfVideoCapture_ScreenIndexIsUsedVerbatim()
        {
            // Index 2 means ffmpeg's device 2, not the third screen: addressing by position would
            // record the wrong monitor.
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 2, Screen, 30, 23, "/r/raw.mp4");

            Assert.Contains("2:none", args);
        }

        [Fact]
        public void AvfVideoCapture_NegativeIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FfmpegArgs.AvfVideoCapture(FullScreen, -1, Screen, 30, 23, "/r/raw.mp4"));
        }

        [Fact]
        public void AvfVideoCapture_PreviewRequested_AppendsThePreviewOutputLast()
        {
            var args = FfmpegArgs.AvfVideoCapture(FullScreen, 0, Screen, 30, 23, "/r/raw.mp4", previewStream: true);
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
                // info, not warning: ffmpeg prints this capture's start time on the machine's shared
                // capture clock in its input dump, at info level, and on macOS that timestamp is the
                // only thing that can align this WAV with a screen recording made by a different
                // ffmpeg process (issue #114). -nostats keeps it to the one fixed block.
                "-hide_banner", "-nostats", "-loglevel", "info",
                // No buffer option, because AVFoundation has no such option and ffmpeg refuses the
                // whole command line for it (issue #111). -thread_queue_size is a generic ffmpeg
                // INPUT option, not a device-buffer option, and it is what keeps the demuxer draining
                // the device while this end of the pipe is busy (issue #114).
                "-f", "avfoundation", "-thread_queue_size", "1024", "-i", ":1",
                "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1",
            }, args);
        }

        /// <summary>
        /// THE SECOND REGRESSION TEST FOR ISSUE #114, and it fails against the argument list that
        /// shipped. Without a deep input queue, ffmpeg's one-sample-buffer avfoundation demuxer
        /// discards everything that arrives while this capture's reader is stalled - measured at
        /// 25.15 s kept out of a 60.05 s capture while the service was transcribing. The screen
        /// capture has carried this guard from the start; this input never had it.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void AvfStreamArguments_GiveTheDeviceADeepInputQueue(int index)
        {
            var args = AvfStream.Arguments(index, 48000, 1);

            int q = System.Array.IndexOf(args, "-thread_queue_size");
            Assert.True(q >= 0, "the microphone input needs a deep queue or the device drops samples");
            Assert.Equal("1024", args[q + 1]);

            // It is an INPUT option, so it belongs before the -i it applies to.
            int i = System.Array.IndexOf(args, "-i");
            Assert.True(q < i, "-thread_queue_size must precede the input it applies to");
            Assert.Equal(":" + index, args[i + 1]);
        }

        /// <summary>
        /// The log level is load-bearing since issue #114, so it is asserted on its own: at "warning"
        /// ffmpeg prints no input dump, FfmpegRecorder.ParseInputStartSeconds finds nothing, and every
        /// macOS recording silently falls back to an unaligned mux.
        /// </summary>
        [Fact]
        public void AvfStreamArguments_LogLevelIsInfo_SoTheCaptureClockIsReported()
        {
            var args = AvfStream.Arguments(0, 48000, 1);

            int i = System.Array.IndexOf(args, "-loglevel");
            Assert.True(i >= 0, "the log level must be set explicitly, not left to ffmpeg's default");
            Assert.Equal("info", args[i + 1]);
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
