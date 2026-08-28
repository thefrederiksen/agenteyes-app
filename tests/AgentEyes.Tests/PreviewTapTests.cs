using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33 - the preview tap, and above all the ONE property the whole feature rests on:
    /// THE DRAIN IS UNCONDITIONAL (AC10).
    ///
    /// The tap reads a pipe that the ffmpeg WRITING THE RECORDING is filling. A pipe nobody reads
    /// fills, and a full pipe blocks that ffmpeg. So "we stopped reading because the preview was
    /// hidden / the disk was full / a write threw" is not a degraded preview - it is a damaged
    /// recording. Every test below that counts <see cref="PreviewTap.FramesRead"/> while publishing
    /// is off or failing is checking exactly that, and each is stated as a presence: the count is a
    /// NUMBER that must be there, not an error that must be absent.
    ///
    /// Measured evidence for why the frames go through this tap at all rather than being written by
    /// ffmpeg itself is in the handoff note for this issue: giving ffmpeg the preview as a file
    /// output and then removing the directory mid-run terminated the whole process and truncated a
    /// 15-second recording to 5.1 seconds.
    /// </summary>
    public class PreviewTapTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "agenteyes-preview-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a test that left a handle open must not fail the run here */ }
        }

        private string FramePath => Path.Combine(_dir, "screen.jpg");

        private static byte[] Frame(byte marker)
        {
            var f = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
            for (int i = 0; i < 8; i++) f.Add(marker);
            f.Add(0xFF);
            f.Add(0xD9);
            return f.ToArray();
        }

        private static MemoryStream StreamOf(params byte[][] frames) =>
            new(frames.SelectMany(f => f).ToArray());

        // ---- the drain ------------------------------------------------------

        [Fact]
        public void Pump_WhilePublishing_WritesTheNewestFrameOutAndCountsEveryFrame()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;

            var first = Frame(0x11);
            var last = Frame(0x22);
            tap.Pump(StreamOf(first, last));

            Assert.True(tap.WaitForDrain(5000), "the pump did not reach the end of its stream");
            Assert.Equal(2, tap.FramesRead);
            Assert.Equal(2, tap.FramesPublished);
            Assert.False(tap.PublishFailed);
            Assert.True(File.Exists(FramePath));
            // The published frame is the LAST one: this is a monitor, not a recording.
            Assert.Equal(last, File.ReadAllBytes(FramePath));
        }

        [Fact]
        public void Pump_WhileNotPublishing_STILL_DRAINS_ANDWritesNothing()
        {
            // The preview is hidden. ffmpeg is still writing to the pipe, so the pipe is still read -
            // the count proves it - and nothing reaches the disk. This is the AC9 cost story and the
            // AC10 safety story in one assertion.
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            Assert.False(tap!.Publishing);

            tap.Pump(StreamOf(Frame(0x11), Frame(0x22), Frame(0x33)));

            Assert.True(tap.WaitForDrain(5000));
            Assert.Equal(3, tap.FramesRead);
            Assert.Equal(0, tap.FramesPublished);
            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void Pump_WhenPublishingFails_KeepsDrainingAndReportsTheFailure()
        {
            // The preview's own failure: the directory it publishes into is gone. The recording must
            // not notice - so every frame is still taken off the pipe (FramesRead), none is published
            // (FramesPublished), and the failure is reported rather than swallowed.
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            Directory.Delete(_dir, recursive: true);

            tap.Pump(StreamOf(Frame(0x11), Frame(0x22), Frame(0x33)));

            Assert.True(tap.WaitForDrain(5000));
            Assert.Equal(3, tap.FramesRead);
            Assert.Equal(0, tap.FramesPublished);
            Assert.True(tap.PublishFailed);
        }

        [Fact]
        public void Pump_WhenPublishingRecovers_PublishesAgainAndClearsTheFailure()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            Directory.Delete(_dir, recursive: true);

            // First stream: nowhere to publish to.
            tap.Pump(StreamOf(Frame(0x11)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(tap.PublishFailed);

            // The directory comes back. A second tap over the same path publishes normally again -
            // a failed preview is not a permanently broken one.
            Directory.CreateDirectory(_dir);
            var again = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(again);
            again!.Publishing = true;
            again.Pump(StreamOf(Frame(0x44)));
            Assert.True(again.WaitForDrain(5000));
            Assert.False(again.PublishFailed);
            Assert.Equal(Frame(0x44), File.ReadAllBytes(FramePath));
        }

        [Fact]
        public void Publishing_TurnedOff_RemovesThePublishedFrame()
        {
            // Hiding the preview must not leave the last picture on disk: the next reader would find
            // a file that looks live and is a photograph of the past.
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(File.Exists(FramePath));

            tap.Publishing = false;

            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void Dispose_RemovesThePublishedFrameAndIsSafeTwice()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(File.Exists(FramePath));

            tap.Dispose();
            tap.Dispose();

            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void TryCreate_DeletesAFrameLeftByThePreviousRecording()
        {
            // A leftover frame is a picture of a DIFFERENT recording. Left in place it would be shown
            // as this recording's first frame, and the staleness watchdog would need seconds to
            // notice.
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(FramePath, Frame(0x99));

            var tap = PreviewTap.TryCreateAt("screen", FramePath);

            Assert.NotNull(tap);
            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void Pump_LeavesNoTemporaryFileBehind()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11), Frame(0x22)));
            Assert.True(tap.WaitForDrain(5000));

            // A named presence check, not "the directory looks tidy": the ONLY file here is the
            // published frame.
            var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "screen.jpg" }, files);
        }

        [Fact]
        public void Pump_Twice_Throws() =>
            Assert.Throws<InvalidOperationException>(() =>
            {
                var tap = PreviewTap.TryCreateAt("screen", FramePath)!;
                tap.Pump(StreamOf(Frame(0x11)));
                tap.WaitForDrain(5000);
                tap.Pump(StreamOf(Frame(0x22)));
            });

        [Fact]
        public void TryCreateAt_WithNoPathToPublishTo_Throws() =>
            Assert.Throws<ArgumentException>(() => PreviewTap.TryCreateAt("screen", ""));

        // ---- the reader -----------------------------------------------------

        [Fact]
        public void TryRead_MissingFile_IsNoFrameAndNoError()
        {
            var bytes = PreviewFrameFile.TryRead(Path.Combine(_dir, "nothing-here.jpg"), out string? error);
            Assert.Null(bytes);
            Assert.Null(error);
        }

        [Fact]
        public void TryRead_PartialJpeg_IsNoFrame()
        {
            // The exact hazard of reading a file another process replaces ten times a second: bytes
            // are there, but they are not a picture yet.
            Directory.CreateDirectory(_dir);
            var whole = Frame(0x11);
            File.WriteAllBytes(FramePath, whole.Take(whole.Length - 2).ToArray());

            Assert.Null(PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_EmptyFile_IsNoFrame()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(FramePath, Array.Empty<byte>());

            Assert.Null(PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_WholeJpeg_ReturnsExactlyThoseBytes()
        {
            Directory.CreateDirectory(_dir);
            var whole = Frame(0x11);
            File.WriteAllBytes(FramePath, whole);

            Assert.Equal(whole, PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_WhileTheWriterHasTheFileOpen_StillReadsIt()
        {
            // The share flags are not incidental. The tap replaces this file constantly; a reader
            // that demanded exclusive access would break the thing it is reading.
            Directory.CreateDirectory(_dir);
            var whole = Frame(0x11);
            File.WriteAllBytes(FramePath, whole);

            using var writerHandle = new FileStream(
                FramePath, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            Assert.Equal(whole, PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_NoPath_IsNoFrame()
        {
            Assert.Null(PreviewFrameFile.TryRead(null!, out _));
            Assert.Null(PreviewFrameFile.TryRead("", out _));
        }
    }
}
