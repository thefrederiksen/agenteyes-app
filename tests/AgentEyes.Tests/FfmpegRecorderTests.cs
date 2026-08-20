using Xunit;
using AgentEyes.Video;

namespace AgentEyes.Tests
{
    public class FfmpegRecorderTests
    {
        // ---- issue #22: the stop-path drain gate -----------------------------------
        //
        // The capture pipeline runs about a second behind wall time, so quitting the instant the
        // user stops discards audio that was already spoken. Stop() now waits for ffmpeg's own
        // "time=" to reach the stop moment; these cover the parser that gate depends on.

        [Fact]
        public void ParseProgressMs_reads_a_real_progress_line()
        {
            const string line =
                "frame=  219 fps= 30 q=29.0 size=     512KiB time=00:00:07.28 bitrate= 576.1kbits/s speed=1.01x";
            Assert.Equal(7280, FfmpegRecorder.ParseProgressMs(line));
        }

        [Fact]
        public void ParseProgressMs_handles_hours_and_minutes()
        {
            Assert.Equal(3661500, FfmpegRecorder.ParseProgressMs("time=01:01:01.50 bitrate=1kbits/s"));
        }

        [Fact]
        public void ParseProgressMs_returns_negative_when_there_is_no_timestamp()
        {
            // Startup lines carry no time= at all, and ffmpeg emits time=N/A before the first mux.
            Assert.True(FfmpegRecorder.ParseProgressMs("Input #1, dshow, from 'audio=Mic':") < 0);
            Assert.True(FfmpegRecorder.ParseProgressMs("frame=    0 fps=0.0 time=N/A bitrate=N/A") < 0);
            Assert.True(FfmpegRecorder.ParseProgressMs("") < 0);
        }

        [Fact]
        public void ParseProgressMs_is_monotonic_across_a_real_progress_sequence()
        {
            // The drain gate compares this against elapsed wall time, so it must never go backwards
            // on a normal run - a regression here would quit early and truncate the take again.
            string[] seq =
            {
                "frame=    8 fps=5.2 q=29.0 size=       0KiB time=00:00:00.42 bitrate=   0.3kbits/s",
                "frame=   60 fps= 20 q=29.0 size=     256KiB time=00:00:01.86 bitrate= 300.0kbits/s",
                "frame=  180 fps= 28 q=29.0 size=     512KiB time=00:00:05.85 bitrate= 400.0kbits/s",
                "frame=  247 fps= 30 q=-1.0 Lsize=     700KiB time=00:00:08.11 bitrate= 450.0kbits/s",
            };
            long prev = -1;
            foreach (var line in seq)
            {
                long ms = FfmpegRecorder.ParseProgressMs(line);
                Assert.True(ms > prev, $"progress went backwards at: {line}");
                prev = ms;
            }
            Assert.Equal(8110, prev);
        }

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
