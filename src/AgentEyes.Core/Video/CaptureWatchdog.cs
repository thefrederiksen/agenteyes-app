using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AgentEyes.Video
{
    /// <summary>
    /// What a <see cref="CaptureWatchdog"/> last saw of a capture (issue #113). Every field is an
    /// OBSERVATION of the output file, which is the only evidence that frames are being written:
    /// the elapsed clock and the audio level both kept moving through an 18-hour recording whose
    /// file had stopped growing two minutes in.
    ///
    /// Named CaptureOutputLiveness, not CaptureLiveness: issue #156 owns <see cref="AgentEyes.CaptureLiveness"/>
    /// - whether a capture's CLAIM on its directory is live, asked by a second process - and this is
    /// the other question, whether the capture is PRODUCING OUTPUT, asked by its owner. Inside
    /// namespace AgentEyes the shorter name resolves to #156's type ahead of this one, so a shared
    /// name would mean the wrong type at every unqualified use. The /status field stays
    /// "CaptureLiveness": that is a wire name QA has quoted, not a type.
    /// </summary>
    internal sealed class CaptureOutputLiveness
    {
        public CaptureOutputLiveness(string state, long bytes, DateTime? lastGrowthUtc, double? secondsSinceGrowth,
            double secondsWatched, CaptureFault? fault)
        {
            State = state;
            Bytes = bytes;
            LastGrowthUtc = lastGrowthUtc;
            SecondsSinceGrowth = secondsSinceGrowth;
            SecondsWatched = secondsWatched;
            Fault = fault;
        }

        /// <summary>
        /// "starting" - no output yet, inside the start-up window;
        /// "growing" - the file grew at the latest check;
        /// "not-growing" - the file did not grow at the latest check, not yet long enough to be a fault;
        /// "check-failed" - the latest check could not read the evidence at all (counted as no growth,
        /// so a read that keeps failing reaches the same verdict a file that stopped growing does);
        /// "watchdog-failed" - the service's handling of the latest check threw; the numbers are the
        /// last good reading and are NOT current (set by the service, never by <see cref="CaptureWatchdog"/>);
        /// "faulted" - the watchdog has decided the capture is not producing output (see <see cref="Fault"/>).
        /// </summary>
        public string State { get; }

        /// <summary>The same reading under another state - the numbers are kept, the claim about them changes.</summary>
        public CaptureOutputLiveness WithState(string state) =>
            new(state, Bytes, LastGrowthUtc, SecondsSinceGrowth, SecondsWatched, Fault);

        /// <summary>Bytes in the output file at the latest check (0 when it does not exist yet).</summary>
        public long Bytes { get; }

        /// <summary>Wall-clock UTC time the file was last SEEN to grow, or null before it ever has.</summary>
        public DateTime? LastGrowthUtc { get; }

        /// <summary>Seconds since the file was last seen to grow, or null before it ever has.</summary>
        public double? SecondsSinceGrowth { get; }

        /// <summary>Seconds the watchdog has been watching this capture (excluding check gaps).</summary>
        public double SecondsWatched { get; }

        /// <summary>The fault, once one is detected; null while the capture is healthy.</summary>
        public CaptureFault? Fault { get; }
    }

    /// <summary>
    /// A capture that stopped producing output, as decided by <see cref="CaptureWatchdog"/> (issue #113).
    /// </summary>
    internal sealed class CaptureFault
    {
        public const string KindStalled = "stalled";
        public const string KindNeverStarted = "never-started";
        public const string KindExited = "exited";

        public CaptureFault(string kind, string message, DateTime detectedUtc, double elapsedSeconds)
        {
            Kind = kind;
            Message = message;
            DetectedUtc = detectedUtc;
            ElapsedSeconds = elapsedSeconds;
        }

        /// <summary>One of <see cref="KindStalled"/>, <see cref="KindNeverStarted"/>, <see cref="KindExited"/>.</summary>
        public string Kind { get; }

        /// <summary>What was observed, in one line, naming the file and the numbers.</summary>
        public string Message { get; }

        /// <summary>When the watchdog decided, UTC.</summary>
        public DateTime DetectedUtc { get; }

        /// <summary>Seconds into the capture at which it was decided.</summary>
        public double ElapsedSeconds { get; }

        public override string ToString() => $"capture {Kind}: {Message}";
    }

    /// <summary>
    /// The watchdog's verdict as an exception, so it can lead a recording's stop report (issue #113)
    /// beside the writers' own failures. Never thrown - it is a record, not control flow.
    /// </summary>
    internal sealed class CaptureFaultException : Exception
    {
        public CaptureFaultException(CaptureFault fault) : base(fault.ToString()) { Fault = fault; }
        public CaptureFault Fault { get; }
    }

    /// <summary>The time sources a <see cref="CaptureWatchdog"/> reads, so tests can drive them.</summary>
    internal interface ICaptureClock
    {
        /// <summary>A monotonic clock. Only differences are used.</summary>
        TimeSpan Monotonic { get; }

        /// <summary>The wall clock, used only to REPORT times, never to measure them.</summary>
        DateTime UtcNow { get; }
    }

    internal sealed class SystemCaptureClock : ICaptureClock
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public TimeSpan Monotonic => _sw.Elapsed;
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>
    /// Decides, from POSITIVE evidence, whether a capture is still producing output (issue #113).
    ///
    /// The evidence is the output file growing. A capture is healthy only while its file keeps
    /// getting bigger; the watchdog never infers health from the process being alive, from the clock
    /// running, or from the audio level moving - all three stayed "healthy" through the 18-hour loss
    /// this exists for.
    ///
    /// Three faults, each decided at a <see cref="Check"/>:
    ///  - <see cref="CaptureFault.KindExited"/>: the capture process ended while the recording was
    ///    still running. Decided at once - a dead process cannot produce more output.
    ///  - <see cref="CaptureFault.KindNeverStarted"/>: no output at all within
    ///    <see cref="StartupTimeout"/> of the start (a capture wedged in device initialisation, as
    ///    seen on macOS on 2026-11-14).
    ///  - <see cref="CaptureFault.KindStalled"/>: output appeared, then did not grow for
    ///    <see cref="StallTimeout"/>.
    ///
    /// The thresholds rest on the capture writing fragmented MP4 with a fragment every two seconds
    /// and a flush per fragment (<see cref="FfmpegArgs.FragmentedOutput"/>): that makes the file grow
    /// every two seconds of media on ANY screen content, including a static one, so 45 seconds of
    /// no growth is far outside healthy behaviour. Plain MP4 grows only when ffmpeg's 32 KB write
    /// buffer fills, which on a static screen was measured at over 12 seconds - too irregular a
    /// heartbeat to judge by.
    ///
    /// A GAP BETWEEN CHECKS IS NOT A STALL. If more than <see cref="GapThreshold"/> passes between
    /// two checks (a suspended laptop, a starved timer), the gap is not counted: the stall window
    /// starts again from that check. Otherwise the first check after a resume would find "no growth
    /// for an hour" and end a capture that was frozen only because the whole machine was.
    ///
    /// Pure apart from the two delegates and the clock, so every decision is unit-tested with a fake
    /// clock and a fake size source. Not thread-safe: one owner calls <see cref="Check"/>.
    /// </summary>
    internal sealed class CaptureWatchdog
    {
        /// <summary>
        /// No growth for this long after output began is a stall. With <see cref="DefaultCheckInterval"/>
        /// a stall is detected at most 47 s after the last write - inside the 60-second window issue
        /// #113 sets, with margin for the check that notices it.
        /// </summary>
        public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(45);

        /// <summary>No output at all for this long after the start is a capture that never started.</summary>
        public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(45);

        /// <summary>How often the owner is expected to call <see cref="Check"/>.</summary>
        public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(2);

        /// <summary>Two checks further apart than this are a gap (suspend, starvation), not a stall.</summary>
        public static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(15);

        private readonly string _label;
        private readonly Func<long?> _size;
        private readonly Func<int?> _exitCode;
        private readonly ICaptureClock _clock;

        private TimeSpan _lastCheck;
        private TimeSpan _watched;              // monotonic time watched, gaps excluded
        private TimeSpan? _lastGrowthWatched;   // _watched at the last observed growth
        private long _bytes;
        private DateTime? _lastGrowthUtc;
        private CaptureFault? _fault;
        private int _failedChecks;              // consecutive checks whose read threw (CheckFailed)
        private string? _lastReadError;

        /// <param name="label">The output file's name, for the fault message (e.g. "raw.mp4").</param>
        /// <param name="sizeSource">Bytes in the output file now, or null when it does not exist.</param>
        /// <param name="exitCodeSource">The capture process's exit code once it has ended, null while it runs.</param>
        public CaptureWatchdog(string label, Func<long?> sizeSource, Func<int?> exitCodeSource, ICaptureClock clock,
            TimeSpan? stallTimeout = null, TimeSpan? startupTimeout = null)
        {
            _label = string.IsNullOrWhiteSpace(label) ? throw new ArgumentException("a label is required", nameof(label)) : label;
            _size = sizeSource ?? throw new ArgumentNullException(nameof(sizeSource));
            _exitCode = exitCodeSource ?? throw new ArgumentNullException(nameof(exitCodeSource));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            StallTimeout = stallTimeout ?? DefaultStallTimeout;
            StartupTimeout = startupTimeout ?? DefaultStartupTimeout;
            if (StallTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stallTimeout));
            if (StartupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startupTimeout));
            _lastCheck = _clock.Monotonic;
            Last = new CaptureOutputLiveness("starting", 0, null, null, 0, null);
        }

        public TimeSpan StallTimeout { get; }
        public TimeSpan StartupTimeout { get; }

        /// <summary>The result of the latest <see cref="Check"/> (a "starting" reading before the first).</summary>
        public CaptureOutputLiveness Last { get; private set; }

        /// <summary>
        /// Look at the capture once and decide. Once a fault is decided it is final: later checks
        /// return the same fault, so a file that grows again after the decision cannot un-decide it.
        /// Throws when a source throws; the owner then reports the failed read through
        /// <see cref="CheckFailed"/>, which keeps the decision going instead of losing it.
        /// </summary>
        public CaptureOutputLiveness Check()
        {
            if (_fault != null) return Last;
            Advance();
            long? size = _size();
            int? exit = _exitCode();
            _failedChecks = 0;
            _lastReadError = null;
            return Decide(size, exit, readFailed: false);
        }

        /// <summary>
        /// The owner's latest <see cref="Check"/> threw before it could read the evidence (issue #113,
        /// the Review Gate's fail-open finding). A failed read is NOT evidence of output, so it is
        /// counted exactly as a check that saw no growth: the stall and start-up windows keep running,
        /// and a read that keeps failing reaches the same verdict a file that stopped growing does,
        /// with the read error in its message. The alternative - stop watching - is the shape that
        /// served "growing" with a frozen byte count for the rest of the recording.
        /// </summary>
        public CaptureOutputLiveness CheckFailed(Exception error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            if (_fault != null) return Last;
            // Check may or may not have advanced the clock before it threw; advancing again adds only
            // the time since then, so the watched time is right either way.
            Advance();
            _failedChecks++;
            _lastReadError = error.GetType().Name + ": " + error.Message;
            Log.Warn($"[CaptureWatchdog] CheckFailed: {_label} - check {_failedChecks} in a row could not read the "
                     + $"evidence ({_lastReadError}); counted as no growth");
            // The exit code is still read: a capture that has ended is decided at once with its real
            // exit code, not 45 s later as a stall, even while its file cannot be read (a disk that
            // went away can end ffmpeg and the stat together). A throw here reaches the owner's
            // catch, which keeps the timer armed.
            int? exit = _exitCode();
            return Decide(size: null, exit: exit, readFailed: true);
        }

        private void Advance()
        {
            TimeSpan now = _clock.Monotonic;
            TimeSpan sinceLast = now - _lastCheck;
            _lastCheck = now;
            if (sinceLast > GapThreshold)
            {
                // Not counted: restart the window from here, as if the capture had just grown (or just
                // started). A real stall still shows up one full window after the resume.
                Log.Warn($"[CaptureWatchdog] Check: {_label} - {sinceLast.TotalSeconds:F1}s since the previous check "
                         + "(suspend or starvation); the gap is not counted as a stall");
                if (_lastGrowthWatched != null) _lastGrowthWatched = _watched;
            }
            else
            {
                _watched += sinceLast;
            }
        }

        private CaptureOutputLiveness Decide(long? size, int? exit, bool readFailed)
        {
            // The file is not at its path after output began (issue #113, the Review Gate's chain: a
            // foreign process renamed raw.mp4 from under the live capture). Still no growth - this
            // process can no longer see any output - but the verdict must say WHAT was seen, not
            // "stopped growing", which is a false reason for a capture that may still be writing.
            // FileInfo.Exists is also false when the stat itself fails, so the wording does not claim
            // the file moved, only that it could not be found.
            bool missing = !readFailed && size == null && _lastGrowthWatched != null;

            long bytes = size ?? 0;
            bool grew = bytes > _bytes;
            if (grew)
            {
                _bytes = bytes;
                _lastGrowthWatched = _watched;
                _lastGrowthUtc = _clock.UtcNow;
            }

            double watchedS = _watched.TotalSeconds;
            if (exit != null)
            {
                _fault = new CaptureFault(CaptureFault.KindExited,
                    $"the capture process exited (code {exit.Value.ToString(CultureInfo.InvariantCulture)}) while recording; "
                    + $"{_label} holds {_bytes} bytes",
                    _clock.UtcNow, watchedS);
            }
            else if (_lastGrowthWatched == null)
            {
                if (_watched >= StartupTimeout)
                    _fault = new CaptureFault(CaptureFault.KindNeverStarted,
                        $"{_label} received no output in the first {_watched.TotalSeconds:F0}s "
                        + $"(limit {StartupTimeout.TotalSeconds:F0}s) - the capture never started" + ReadFailureNote(),
                        _clock.UtcNow, watchedS);
            }
            else if (_watched - _lastGrowthWatched.Value >= StallTimeout)
            {
                // Say what was OBSERVED. Only a file that was read and had not grown "stopped growing";
                // a file that could not be read, or could not be found, may still be growing.
                string what = readFailed
                    ? $"{_label} could not be read for the last {_failedChecks} checks ({_lastReadError}); "
                      + $"last seen at {_bytes} bytes, no output seen for "
                    : missing
                    ? $"{_label} could not be found at its path (moved, deleted or not readable; last seen at {_bytes} bytes); "
                      + "no output seen for "
                    : $"{_label} stopped growing at {_bytes} bytes and has not grown for ";
                _fault = new CaptureFault(CaptureFault.KindStalled,
                    what
                    + $"{(_watched - _lastGrowthWatched.Value).TotalSeconds:F0}s (limit {StallTimeout.TotalSeconds:F0}s); "
                    + $"last growth {Iso(_lastGrowthUtc)}",
                    _clock.UtcNow, watchedS);
            }

            double? sinceGrowth = _lastGrowthWatched == null ? null : Math.Round((_watched - _lastGrowthWatched.Value).TotalSeconds, 1);
            string state = _fault != null ? "faulted"
                : readFailed ? "check-failed"
                : _lastGrowthWatched == null ? "starting"
                : grew ? "growing"
                : "not-growing";
            Last = new CaptureOutputLiveness(state, _bytes, _lastGrowthUtc, sinceGrowth, Math.Round(watchedS, 1), _fault);
            return Last;
        }

        private string ReadFailureNote() =>
            _failedChecks == 0 ? "" : $"; the last {_failedChecks} checks could not read it ({_lastReadError})";

        /// <summary>The size of a file, or null when it does not exist - the production size source.</summary>
        public static long? FileSize(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }

        private static string Iso(DateTime? utc) =>
            utc == null ? "(never)" : utc.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
