using System.IO;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #36 - the parser run against a REAL camera's real output, captured on this machine on
    /// 2026-08-28 and committed so the number is reproducible from the artifact rather than from
    /// instrumentation nobody can re-run.
    ///
    /// THE CAPTURE IS THE EVIDENCE FOR THE WHOLE DESIGN, not just for the parser. The camera on this
    /// machine ("HD Webcam eMeet C960") produces 1920x1080 - SIXTEEN BY NINE - while the preset
    /// editor's preview buffer is a fixed 4:3 320x240. ffmpeg therefore letterboxes it: the picture
    /// occupies 320x180 with a 30-pixel black bar above and below. A circle drawn as if the picture
    /// filled the pane would sit a third of a frame off vertically - and would look entirely
    /// convincing doing it, which is why this is read from ffmpeg rather than assumed.
    ///
    /// Reproduce the capture:
    ///   ffmpeg -hide_banner -f dshow -thread_queue_size 512 -i "video=&lt;camera&gt;" -an
    ///     -vf "scale=320:240:force_original_aspect_ratio=decrease,pad=320:240:(ow-iw)/2:(oh-ih)/2:black"
    ///     -r 10 -pix_fmt bgr24 -f rawvideo -flush_packets 1 -t 2 pipe:1 &gt; NUL 2&gt; stderr.txt
    ///
    /// WHAT IT CANNOT SEE: any camera other than this one. It is one real sample, not a survey - and
    /// the synthetic cases (a 4:3 camera, an MJPEG camera, an output-only log, an empty log) are in
    /// CameraOverlaySyncTests alongside it.
    /// </summary>
    public class CameraFrameSizeRealCaptureTests
    {
        private const string FixturePath = @"tests\AgentEyes.Tests\fixtures\camera\emeet-c960-preview-stderr.txt";

        private static string Fixture() => File.ReadAllText(Path.Combine(RepoSource.Root, FixturePath));

        [Fact]
        public void TheCapturedLog_IsThere_AndContainsBothStreamBlocks()
        {
            // The instrument first. A missing or truncated fixture would make the assertion below
            // pass or fail for the wrong reason.
            string log = Fixture();

            Assert.Contains("Input #0, dshow", log);
            Assert.Contains("Output #0, rawvideo", log);
            Assert.Contains("1920x1080", log);
            Assert.Contains("320x240", log);
        }

        [Fact]
        public void ARealCamerasLog_ReportsTheCAMERAsFrame_AndNotThePaddedPreviewBuffer()
        {
            var size = CameraFrameSize.FromFfmpegLog(Fixture());

            Assert.NotNull(size);
            Assert.Equal(1920, size!.Value.Width);
            Assert.Equal(1080, size.Value.Height);
            // The number that matters: this camera is 16:9, so the 4:3 preview pane IS letterboxed.
            Assert.Equal(16.0 / 9.0, size.Value.Aspect, 4);
            Assert.NotEqual(new CameraFrameSize(320, 240), size.Value);
        }

        [Fact]
        public void TheLetterboxingIsReal_SoTheAdornerHasToAccountForIt()
        {
            // The consequence, computed with the same helper the editor uses: a 1920x1080 picture
            // inside the 320x240 buffer leaves a 30-pixel bar top and bottom. Drawing the circle
            // against the pane instead of against this rectangle is a 30/240 = 12.5% vertical error
            // at the pane, and a third of the circle's own height at the default diameter.
            var size = CameraFrameSize.FromFfmpegLog(Fixture())!.Value;

            var content = AgentEyes.Preview.OverlayFit.Contain(320, 240, size.Width, size.Height);

            Assert.Equal(320, content.Width, 3);
            Assert.Equal(180, content.Height, 3);
            Assert.Equal(0, content.X, 3);
            Assert.Equal(30, content.Y, 3);
        }
    }
}
