using System;
using AgentEyes.Preview;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>Starts the process that records the screen. Production: <see cref="FfmpegRecorder.Start"/>.</summary>
    internal delegate IScreenRecorder ScreenRecorderStart(
        Drawing.Rectangle capture, string? mic, int fps, int crf, string outPath, PreviewTap? preview, int screenNumber);

    /// <summary>
    /// The one-shot timer that paces the capture watchdog's checks. <see cref="Arm"/> schedules the
    /// next check; nothing fires until it is called again.
    /// </summary>
    internal interface IWatchdogTimer : IDisposable
    {
        void Arm(TimeSpan due);
    }

    /// <summary>The production <see cref="IWatchdogTimer"/>: a System.Threading.Timer.</summary>
    internal sealed class ThreadingWatchdogTimer : IWatchdogTimer
    {
        private readonly System.Threading.Timer _timer;

        public ThreadingWatchdogTimer(Action tick)
        {
            if (tick == null) throw new ArgumentNullException(nameof(tick));
            _timer = new System.Threading.Timer(_ => tick(), null,
                System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        }

        public void Arm(TimeSpan due) => _timer.Change(due, System.Threading.Timeout.InfiniteTimeSpan);

        public void Dispose() => _timer.Dispose();
    }

    /// <summary>
    /// The points where <see cref="RecordingService"/> reaches the machine for a SCREEN recording
    /// (issue #113, round two): the monitor, the recording folder, the capture process, the
    /// watchdog's clock and timer, the duration probe and post-processing.
    ///
    /// <see cref="Production"/> is what the service has always called; the public constructor uses
    /// it and nothing else does. Tests replace them to drive StartVideo -> watchdog -> verdict ->
    /// Stop with a capture they control, which is the only way a test can see whether the watchdog
    /// is wired into the product at all - QA deleted the line that starts it on round one and every
    /// test stayed green.
    /// </summary>
    internal sealed class RecordingServiceSeams
    {
        public Func<int, MonitorInfo> RequireMonitor { get; init; } = Monitors.Require;

        public Func<string, string?, string> NewRecordingDir { get; init; } = RecordingPaths.NewDir;

        public ScreenRecorderStart StartScreen { get; init; } =
            (capture, mic, fps, crf, outPath, preview, screenNumber) =>
                FfmpegRecorder.Start(capture, mic, fps, crf, outPath, preview, screenNumber);

        /// <summary>
        /// Bytes in the capture's output file, or null when it does not exist - what the watchdog reads
        /// as evidence. A seam so a test can make the read itself THROW and see that the watchdog keeps
        /// deciding rather than going silent (issue #113, the Review Gate's fail-open finding).
        /// </summary>
        public Func<string, long?> OutputFileSize { get; init; } = CaptureWatchdog.FileSize;

        public Func<ICaptureClock> NewWatchdogClock { get; init; } = () => new SystemCaptureClock();

        public Func<Action, IWatchdogTimer> NewWatchdogTimer { get; init; } = tick => new ThreadingWatchdogTimer(tick);

        public Func<string, double> ProbeDurationSeconds { get; init; } = MediaProbe.DurationSeconds;

        public Action<string> PostProcess { get; init; } = dir => PostRecording.Run(dir);

        public static RecordingServiceSeams Production { get; } = new();
    }
}
