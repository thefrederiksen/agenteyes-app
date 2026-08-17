using Xunit;
using AgentEyes.Video;

namespace AgentEyes.Tests
{
    public class FfmpegRecorderTests
    {
        [Fact]
        public void DiagnoseImmediateExit_region_out_of_bounds_blames_the_region_not_the_mic()
        {
            // Issue #69 secondary defect: a region-that-exceeds-the-desktop failure must NOT be
            // reported as a mic problem - even if a mic was configured, the gdigrab error wins.
            const string stderr =
                "Capture area (0,0),(1080,1920) extends outside window area (0,-5),(3840,1848)\n" +
                "Error opening input file desktop. I/O error";
            string cause = FfmpegRecorder.DiagnoseImmediateExit(stderr, "Microphone (Yeti)");
            Assert.Contains("desktop bounds", cause);
            Assert.DoesNotContain("microphone", cause);
        }

        [Fact]
        public void DiagnoseImmediateExit_with_no_mic_never_mentions_the_mic()
        {
            // source:none (no mic) - the old message wrongly blamed the mic; it must not now.
            string cause = FfmpegRecorder.DiagnoseImmediateExit("some other ffmpeg error", null);
            Assert.DoesNotContain("microphone", cause);
            Assert.DoesNotContain("DirectShow", cause);
        }

        [Fact]
        public void DiagnoseImmediateExit_with_mic_and_no_region_error_points_at_the_mic()
        {
            string cause = FfmpegRecorder.DiagnoseImmediateExit("could not run graph", "Bad Mic Name");
            Assert.Contains("Bad Mic Name", cause);
            Assert.Contains("DirectShow", cause);
        }
    }
}
