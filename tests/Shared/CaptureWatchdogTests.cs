using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// Issue #113. ONE source file, compiled into all three suites (AgentEyes.Tests on Windows,
// AgentEyes.Tests.Linux, AgentEyes.Tests.Mac) through a <Compile Include="..\Shared\..." Link> in each
// csproj, because the capture watchdog is platform-neutral and the same guard serves all three. The
// clock and the file-growth source are FAKED: nothing here launches ffmpeg or waits in real time.
//
// EVERY "DOES NOT FIRE" TEST HAS A PAIRED "FIRES" TEST THROUGH THE SAME HARNESS. A watchdog that never
// fires passes every absence assertion below; it cannot pass Stall_GrowthStops_FiresStalledInsideTheWindow,
// NeverStarted_NoOutputPastTheStartupWindow_Fires or Exited_ProcessEnds_FiresAtTheNextCheck.
namespace AgentEyes.Tests.Shared
{
    using CaptureFault = global::AgentEyes.Video.CaptureFault;
    using CaptureLiveness = global::AgentEyes.Video.CaptureLiveness;
    using CaptureWatchdog = global::AgentEyes.Video.CaptureWatchdog;
    using FfmpegArgs = global::AgentEyes.Video.FfmpegArgs;
    using ICaptureClock = global::AgentEyes.Video.ICaptureClock;
    using RecordingStopFailure = global::AgentEyes.RecordingStopFailure;
    using RecordingStopSequence = global::AgentEyes.RecordingStopSequence;
    using RecordingStopStep = global::AgentEyes.RecordingStopStep;

    /// <summary>A clock the test moves by hand.</summary>
    internal sealed class FakeCaptureClock : ICaptureClock
    {
        public static readonly DateTime Origin = new(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc);
        public TimeSpan Monotonic { get; private set; }
        public DateTime UtcNow => Origin + Monotonic;
        public void Advance(double seconds) => Monotonic += TimeSpan.FromSeconds(seconds);
    }

    /// <summary>A capture the test drives: an output file size and an optional exit code.</summary>
    internal sealed class FakeCapture
    {
        public long? Size;          // null = the file does not exist yet
        public int? ExitCode;       // null = the process is running
        public void Grow(long bytes) => Size = (Size ?? 0) + bytes;
    }

    public sealed class CaptureWatchdogTests
    {
        private static readonly double Interval = CaptureWatchdog.DefaultCheckInterval.TotalSeconds;

        private static (CaptureWatchdog Dog, FakeCaptureClock Clock, FakeCapture Capture) NewWatchdog()
        {
            var clock = new FakeCaptureClock();
            var capture = new FakeCapture();
            var dog = new CaptureWatchdog("raw.mp4", () => capture.Size, () => capture.ExitCode, clock);
            return (dog, clock, capture);
        }

        /// <summary>Advance one check interval, apply <paramref name="change"/>, and check.</summary>
        private static CaptureLiveness Step(CaptureWatchdog dog, FakeCaptureClock clock, Action? change = null)
        {
            clock.Advance(Interval);
            change?.Invoke();
            return dog.Check();
        }

        // ---- the thresholds the issue depends on ---------------------------------------------

        [Fact]
        public void Defaults_StallPlusOneCheck_FitInsideTheSixtySecondWindow()
        {
            // Issue #113 AC1/AC2: detection within 60 s. The worst case is a stall that begins just
            // after a check saw growth: it is decided StallTimeout later, at the next check.
            double worst = CaptureWatchdog.DefaultStallTimeout.TotalSeconds + Interval;
            Assert.True(worst <= 50, $"worst-case detection {worst}s leaves under 10 s of margin inside 60 s");
            Assert.True(CaptureWatchdog.DefaultStallTimeout.TotalSeconds >= 30,
                "a stall timeout under 30 s would end recordings on an ordinary slow disk");
        }

        // ---- growth continues: never fires ----------------------------------------------------

        [Fact]
        public void Healthy_FileGrowsEveryCheckForTenMinutes_NeverFaultsAndReportsGrowing()
        {
            var (dog, clock, capture) = NewWatchdog();
            int checks = (int)(600 / Interval);
            for (int i = 0; i < checks; i++)
            {
                var live = Step(dog, clock, () => capture.Grow(40_000));
                Assert.Null(live.Fault);
                Assert.Equal("growing", live.State);
            }
            Assert.Equal(checks * 40_000L, dog.Last.Bytes);
        }

        [Fact]
        public void Healthy_FileGrowsOnlyEveryFortySeconds_NeverFaults()
        {
            // A capture running far below real time (the starved Linux capture ran at 0.08x) still
            // writes a fragment every two seconds of MEDIA. 40 s between growths is inside the window.
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 1; i <= 300; i++)
            {
                var live = Step(dog, clock, () => { if (i % 20 == 0) capture.Grow(1_000); });
                Assert.Null(live.Fault);
            }
        }

        [Fact]
        public void Healthy_GrowthStopsForJustUnderTheTimeout_ThenResumes_DoesNotFault()
        {
            var (dog, clock, capture) = NewWatchdog();
            Step(dog, clock, () => capture.Grow(1_000));
            int quietChecks = (int)(CaptureWatchdog.DefaultStallTimeout.TotalSeconds / Interval) - 1;
            for (int i = 0; i < quietChecks; i++) Assert.Null(Step(dog, clock).Fault);
            var resumed = Step(dog, clock, () => capture.Grow(1_000));
            Assert.Null(resumed.Fault);
            Assert.Equal("growing", resumed.State);
        }

        // ---- growth stops: fires ---------------------------------------------------------------

        [Fact]
        public void Stall_GrowthStops_FiresStalledInsideTheWindow()
        {
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 0; i < 10; i++) Step(dog, clock, () => capture.Grow(40_000));
            double stalledAt = clock.Monotonic.TotalSeconds;

            CaptureLiveness live;
            do { live = Step(dog, clock); } while (live.Fault == null && clock.Monotonic.TotalSeconds < stalledAt + 120);

            Assert.NotNull(live.Fault);
            Assert.Equal(CaptureFault.KindStalled, live.Fault!.Kind);
            Assert.Equal("faulted", live.State);
            double latency = clock.Monotonic.TotalSeconds - stalledAt;
            Assert.InRange(latency, CaptureWatchdog.DefaultStallTimeout.TotalSeconds, 60);
            // The message names the file and the byte count it stopped at.
            Assert.Contains("raw.mp4", live.Fault.Message);
            Assert.Contains("400000 bytes", live.Fault.Message);
            Assert.Equal(FakeCaptureClock.Origin + clock.Monotonic, live.Fault.DetectedUtc);
        }

        [Fact]
        public void Stall_NotBeforeTheTimeout_NotGrowingIsReportedButNotAFault()
        {
            var (dog, clock, capture) = NewWatchdog();
            Step(dog, clock, () => capture.Grow(5_000));
            var quiet = Step(dog, clock);
            Assert.Null(quiet.Fault);
            Assert.Equal("not-growing", quiet.State);
            Assert.Equal(Interval, quiet.SecondsSinceGrowth);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.5)]
        [InlineData(1.9)]
        public void Stall_WhereverInTheIntervalItBegins_IsDetectedWithinSixtySeconds(double offset)
        {
            // The stall begins `offset` seconds after a check that saw growth. Detection latency is
            // measured from the stall's start to the check that decides, as QA will measure it.
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 0; i < 5; i++) Step(dog, clock, () => capture.Grow(1_000));
            clock.Advance(offset);
            capture.Grow(1_000);                    // the last write, just before the freeze
            double stalledAt = clock.Monotonic.TotalSeconds;
            clock.Advance(Interval - offset);
            dog.Check();

            CaptureLiveness live;
            do { live = Step(dog, clock); } while (live.Fault == null && clock.Monotonic.TotalSeconds < stalledAt + 120);

            Assert.Equal(CaptureFault.KindStalled, live.Fault?.Kind);
            Assert.True(clock.Monotonic.TotalSeconds - stalledAt <= 60,
                $"detected {clock.Monotonic.TotalSeconds - stalledAt:F1}s after the stall");
        }

        [Fact]
        public void Exited_ProcessEnds_FiresAtTheNextCheck()
        {
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 0; i < 5; i++) Step(dog, clock, () => capture.Grow(1_000));
            var live = Step(dog, clock, () => capture.ExitCode = 137);
            Assert.Equal(CaptureFault.KindExited, live.Fault?.Kind);
            Assert.Contains("code 137", live.Fault!.Message);
        }

        [Fact]
        public void Fault_IsFinal_GrowthAfterTheDecisionDoesNotClearIt()
        {
            var (dog, clock, capture) = NewWatchdog();
            Step(dog, clock, () => capture.Grow(1_000));
            CaptureLiveness live;
            // Bounded: with the watchdog broken this must FAIL, not hang the suite.
            do { live = Step(dog, clock); } while (live.Fault == null && clock.Monotonic.TotalSeconds < 120);
            Assert.NotNull(live.Fault);
            var fault = live.Fault;

            var after = Step(dog, clock, () => capture.Grow(50_000));
            Assert.Same(fault, after.Fault);
            Assert.Equal("faulted", after.State);
        }

        // ---- the start-up window -----------------------------------------------------------------

        [Fact]
        public void Startup_NoBytesYet_InsideTheWindow_DoesNotFireAndReportsStarting()
        {
            var (dog, clock, _) = NewWatchdog();
            int checks = (int)(CaptureWatchdog.DefaultStartupTimeout.TotalSeconds / Interval) - 1;
            for (int i = 0; i < checks; i++)
            {
                var live = Step(dog, clock);
                Assert.Null(live.Fault);
                Assert.Equal("starting", live.State);
                Assert.Null(live.LastGrowthUtc);
            }
        }

        [Fact]
        public void Startup_SlowFirstOutputThenGrowth_DoesNotFire()
        {
            // The starved Linux capture of 2026-11-14 took well over ten seconds to emit anything.
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 0; i < 15; i++) Assert.Null(Step(dog, clock).Fault);      // 30 s of nothing
            for (int i = 0; i < 60; i++) Assert.Null(Step(dog, clock, () => capture.Grow(2_000)).Fault);
        }

        [Fact]
        public void NeverStarted_NoOutputPastTheStartupWindow_Fires()
        {
            var (dog, clock, _) = NewWatchdog();
            CaptureLiveness live;
            do { live = Step(dog, clock); } while (live.Fault == null && clock.Monotonic.TotalSeconds < 120);
            Assert.Equal(CaptureFault.KindNeverStarted, live.Fault?.Kind);
            Assert.InRange(clock.Monotonic.TotalSeconds,
                CaptureWatchdog.DefaultStartupTimeout.TotalSeconds,
                CaptureWatchdog.DefaultStartupTimeout.TotalSeconds + Interval);
        }

        [Fact]
        public void NeverStarted_AnEmptyFileIsNotOutput()
        {
            // ffmpeg creates the file before it writes to it. Zero bytes is not a start.
            var (dog, clock, capture) = NewWatchdog();
            capture.Size = 0;
            CaptureLiveness live;
            do { live = Step(dog, clock); } while (live.Fault == null && clock.Monotonic.TotalSeconds < 120);
            Assert.Equal(CaptureFault.KindNeverStarted, live.Fault?.Kind);
        }

        // ---- the liveness signal /status carries (AC3) ---------------------------------------------

        [Fact]
        public void Signal_AdvancesWhileGrowing_AndStandsStillWhenStalled()
        {
            var (dog, clock, capture) = NewWatchdog();
            var a = Step(dog, clock, () => capture.Grow(10_000));
            for (int i = 0; i < 4; i++) Step(dog, clock, () => capture.Grow(10_000));
            var b = Step(dog, clock, () => capture.Grow(10_000));
            Assert.True(b.Bytes > a.Bytes);
            Assert.True(b.LastGrowthUtc > a.LastGrowthUtc);

            for (int i = 0; i < 5; i++) Step(dog, clock);
            var c = Step(dog, clock);
            Assert.Equal(b.Bytes, c.Bytes);
            Assert.Equal(b.LastGrowthUtc, c.LastGrowthUtc);
            Assert.Equal(12.0, c.SecondsSinceGrowth);
        }

        // ---- a gap between checks is not a stall --------------------------------------------------

        [Fact]
        public void Gap_LongPauseBetweenChecks_IsNotCountedAsAStall()
        {
            // A suspended laptop: the next check comes ten minutes later and the file has not grown,
            // because nothing on the machine ran. That is not this capture failing.
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 0; i < 5; i++) Step(dog, clock, () => capture.Grow(1_000));
            clock.Advance(600);
            Assert.Null(dog.Check().Fault);
            for (int i = 0; i < 30; i++) Assert.Null(Step(dog, clock, () => capture.Grow(1_000)).Fault);
        }

        [Fact]
        public void Gap_ThenNoGrowth_StillFiresOneWindowAfterTheResume()
        {
            // The paired control: the gap restarts the window, it does not switch the watchdog off.
            var (dog, clock, capture) = NewWatchdog();
            for (int i = 0; i < 5; i++) Step(dog, clock, () => capture.Grow(1_000));
            clock.Advance(600);
            dog.Check();
            double resumedAt = clock.Monotonic.TotalSeconds;
            CaptureLiveness live;
            do { live = Step(dog, clock); } while (live.Fault == null && clock.Monotonic.TotalSeconds < resumedAt + 120);
            Assert.Equal(CaptureFault.KindStalled, live.Fault?.Kind);
            Assert.InRange(clock.Monotonic.TotalSeconds - resumedAt, CaptureWatchdog.DefaultStallTimeout.TotalSeconds, 60);
        }

        // ---- the stop report carries the verdict ------------------------------------------------

        [Fact]
        public void StopSequence_KnownFailure_LeadsTheReportEvenWhenEveryWriterStopsCleanly()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ae-113-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var fault = new CaptureFault(CaptureFault.KindStalled, "raw.mp4 stopped growing", FakeCaptureClock.Origin, 70);
                bool writerStopped = false, manifestSaved = false;
                var report = RecordingStopSequence.Run(
                    dir,
                    new[] { new RecordingStopStep("video", () => writerStopped = true, null) },
                    () => manifestSaved = true,
                    () => { },
                    new[] { new RecordingStopFailure("capture watchdog", new global::AgentEyes.Video.CaptureFaultException(fault)) });

                Assert.True(writerStopped);
                Assert.True(manifestSaved);
                Assert.True(report.Failed);
                Assert.Equal("capture watchdog", report.Failures[0].Stage);
                Assert.Contains("capture stalled: raw.mp4 stopped growing", report.Summary());
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ---- a capture that fell behind at the stop (the 2026-11-14 Linux recording) --------------

        [Fact]
        public void BehindAtStop_TheEvidenceRecording_IsRecordedAsBehind()
        {
            // ~/Videos/AgentEyes/2026-11-14_102532_video: reported 6.46 s, ffmpeg wrote 1.33 s.
            var fault = global::AgentEyes.RecordingService.BehindAtStop("recording.mp4", 1.33, 6.46, FakeCaptureClock.Origin);
            Assert.NotNull(fault);
            Assert.Equal("behind", fault!.Kind);
            Assert.Contains("1.33s of a 6.46s session", fault.Message);
            Assert.Contains("short by 5.13s", fault.Message);
        }

        [Theory]
        [InlineData(29.26, 29.58)]   // a healthy stop measured 2026-11-14: 0.32 s of pipeline lag
        [InlineData(604.62, 603.19)] // the 10-minute healthy run: ffmpeg ahead of the stopwatch
        [InlineData(10.0, 12.9)]     // just inside the tolerance
        public void BehindAtStop_LatencyNotLoss_IsNotAFault(double captured, double session)
        {
            Assert.Null(global::AgentEyes.RecordingService.BehindAtStop("raw.mp4", captured, session, FakeCaptureClock.Origin));
        }

        [Fact]
        public void BehindAtStop_JustPastTheTolerance_IsAFault()
        {
            double session = 20.0, captured = session - global::AgentEyes.RecordingService.BehindToleranceSeconds - 0.01;
            Assert.NotNull(global::AgentEyes.RecordingService.BehindAtStop("raw.mp4", captured, session, FakeCaptureClock.Origin));
        }

        // ---- the capture writes a file that survives an unclean end (AC5) --------------------------

        [Fact]
        public void FragmentedOutput_IsOnTheWindowsCaptureImmediatelyBeforeTheOutputPath()
        {
            var args = FfmpegArgs.VideoCapture(new System.Drawing.Rectangle(0, 0, 1920, 1080), "Mic", 30, 23,
                "out.mp4", previewStream: true);
            AssertFragmentedBefore(args, "out.mp4");
        }

        /// <summary>
        /// The fragmenting options sit directly before <paramref name="outPath"/>, so they bind to the
        /// MP4 output and not to the preview output that may follow it.
        /// </summary>
        internal static void AssertFragmentedBefore(IReadOnlyList<string> args, string outPath)
        {
            var frag = FfmpegArgs.FragmentedOutput();
            Assert.Equal(new[]
            {
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
                "-frag_duration", "2000000",
                "-flush_packets", "1",
            }, frag);
            int at = args.ToList().IndexOf(outPath);
            Assert.True(at >= frag.Count, $"output path {outPath} not found after the fragment options");
            Assert.Equal(frag, args.Skip(at - frag.Count).Take(frag.Count).ToList());
        }
    }
}
