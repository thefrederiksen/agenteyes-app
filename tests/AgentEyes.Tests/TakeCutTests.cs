using System;
using System.IO;
using AgentEyes.App;
using AgentEyes.Takes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>Issue #230 round 3: cutting an attempt out of the continuous recording.</summary>
    public class TakeCutTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "agenteyes-takecut-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void Span_MarksInTheMiddle_AddsTheMarginBothSides()
        {
            var span = TakeCut.Span(10.0, 15.0, null);

            Assert.Equal(8.0, span.StartSeconds, 3);
            Assert.Equal(17.0, span.EndSeconds, 3);
            Assert.Equal(9.0, span.DurationSeconds, 3);
        }

        [Fact]
        public void Span_StartMarkNearTheBeginning_ClampsToZero()
        {
            var span = TakeCut.Span(0.5, 4.0, null);

            Assert.Equal(0.0, span.StartSeconds, 3);
            Assert.Equal(6.0, span.DurationSeconds, 3);
        }

        [Fact]
        public void Span_EndMarkNearTheEndOfAFinalFile_ClampsToTheFile()
        {
            var span = TakeCut.Span(20.0, 29.5, fileDurationSeconds: 30.0);

            Assert.Equal(18.0, span.StartSeconds, 3);
            Assert.Equal(30.0, span.EndSeconds, 3);
        }

        [Fact]
        public void Span_EndBeforeStart_Throws()
        {
            Assert.Throws<ArgumentException>(() => TakeCut.Span(10.0, 9.0, null));
        }

        [Fact]
        public void Span_StartPastTheEndOfTheFile_Throws()
        {
            Assert.Throws<UsageException>(() => TakeCut.Span(40.0, 45.0, fileDurationSeconds: 30.0));
        }

        [Fact]
        public void Span_SameStartAndEnd_KeepsBothMargins()
        {
            var span = TakeCut.Span(5.0, 5.0, null);

            Assert.Equal(4.0, span.DurationSeconds, 3);
        }

        [Fact]
        public void PositionNeeded_IsTheSpanEndPlusTheWriteLag()
        {
            var span = TakeCut.Span(10.0, 15.0, null);

            Assert.Equal(17.0 + TakeCut.WrittenBeyondSeconds, TakeCut.PositionNeeded(span), 3);
        }

        [Fact]
        public void Args_SeekBeforeInputAndReencode()
        {
            var args = TakeCut.Args("in.mp4", new CutSpan(8.25, 9.5), "out.mp4");

            int ss = args.IndexOf("-ss"), i = args.IndexOf("-i"), t = args.IndexOf("-t");
            Assert.True(ss >= 0 && ss < i, "the seek goes before the input");
            Assert.Equal("8.250", args[ss + 1]);
            Assert.Equal("9.500", args[t + 1]);
            Assert.Contains("libx264", args);
            Assert.DoesNotContain("copy", args);
            Assert.Equal("out.mp4", args[^1]);
        }

        [Fact]
        public void ContinuousClock_NoReading_HasNoFileTime()
        {
            var clock = new ContinuousClock();

            Assert.Null(clock.FileSeconds(5.0));
        }

        [Fact]
        public void ContinuousClock_ZeroPosition_IsIgnored()
        {
            var clock = new ContinuousClock();

            clock.Observe(3.0, 0.0);

            Assert.Null(clock.AnchorSeconds);
        }

        [Fact]
        public void ContinuousClock_KeepsTheEarliestAnchor()
        {
            var clock = new ContinuousClock();

            clock.Observe(5.0, 1.5);   // anchor 3.5 - a late reading
            clock.Observe(6.0, 3.0);   // anchor 3.0 - a closer one
            clock.Observe(7.0, 3.5);   // anchor 3.5 - late again, ignored

            Assert.Equal(3.0, clock.AnchorSeconds!.Value, 3);
            Assert.Equal(7.0, clock.FileSeconds(10.0)!.Value, 3);
        }

        [Fact]
        public void ContinuousClock_MarkBeforeTheFileStarted_ClampsToZero()
        {
            var clock = new ContinuousClock();
            clock.Observe(6.0, 2.0);   // file time 0 at screen 4.0

            Assert.Equal(0.0, clock.FileSeconds(3.0)!.Value, 3);
        }

        [Fact]
        public void AddAttempt_ExplicitNumber_IsKept()
        {
            var store = new TakeStore(Path.Combine(_dir, "takes.json"));
            store.Load(new TakeList { Takes = { new Take { Id = "opening", Text = "Hello." } } });

            var a = store.AddAttempt("opening", "attempt-3.mp4", "dir", 5.0, number: 3);

            Assert.Equal(3, a.Number);
            Assert.Equal(4, store.NextAttemptNumber("opening"));
        }

        [Fact]
        public void AddAttempt_NumberAlreadyUsed_Throws()
        {
            var store = new TakeStore(Path.Combine(_dir, "takes.json"));
            store.Load(new TakeList { Takes = { new Take { Id = "opening", Text = "Hello." } } });
            store.AddAttempt("opening", "attempt-1.mp4", "dir", 5.0, number: 1);

            Assert.Throws<UsageException>(() => store.AddAttempt("opening", "again.mp4", "dir", 5.0, number: 1));
        }

        [Fact]
        public void CameraCapture_KeyframeInterval_AddsGop()
        {
            var args = AgentEyes.Video.FfmpegArgs.CameraCapture("cam", 30, 20, "o.mp4", dshowMic: "mic", keyframeInterval: 30);

            int g = args.IndexOf("-g");
            Assert.True(g >= 0, "a keyframe interval is passed as -g");
            Assert.Equal("30", args[g + 1]);
        }

        [Fact]
        public void CameraCapture_NoKeyframeInterval_LeavesTheEncoderDefault()
        {
            var args = AgentEyes.Video.FfmpegArgs.CameraCapture("cam", 30, 20, "o.mp4", dshowMic: "mic");

            Assert.DoesNotContain("-g", args);
        }

        [Fact]
        public void SessionNote_NamesTheRecordingsFolder()
        {
            string note = TakesWindow.SessionNote(@"C:\Users\x\Videos\AgentEyes");

            Assert.Contains(@"C:\Users\x\Videos\AgentEyes", note);
            Assert.Contains("takes-session", note);
        }

        [Fact]
        public void CameraCapture_KeyframeInterval_TunesZeroLatency()
        {
            // Round 4 (review D1): measured 2.75-3.05 s of progress-clock lag with the defaults,
            // 0.68-0.72 s with this tune; the 2 s cut margin only holds with it.
            var args = AgentEyes.Video.FfmpegArgs.CameraCapture("cam", 30, 20, "o.mp4", dshowMic: "mic", keyframeInterval: 30);

            int tune = args.IndexOf("-tune");
            Assert.True(tune >= 0, "the continuous recording is tuned for zero latency so its progress clock is honest");
            Assert.Equal("zerolatency", args[tune + 1]);
        }

        [Fact]
        public void CameraCapture_NoKeyframeInterval_NoTune()
        {
            var args = AgentEyes.Video.FfmpegArgs.CameraCapture("cam", 30, 20, "o.mp4", dshowMic: "mic");

            Assert.DoesNotContain("-tune", args);
        }

        [Fact]
        public void FinalAnchor_IsTheStopMomentMinusTheFileLength()
        {
            double anchor = ContinuousClock.FinalAnchor(stopWallSeconds: 100.0, fileDurationSeconds: 40.0);

            Assert.Equal(60.0, anchor, 3);
            Assert.Equal(15.0, ContinuousClock.FileSecondsAt(75.0, anchor), 3);
            Assert.Equal(0.0, ContinuousClock.FileSecondsAt(50.0, anchor), 3);
        }

        [Fact]
        public void FinalAnchor_EmptyFile_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ContinuousClock.FinalAnchor(10.0, 0.0));
        }
    }
}
