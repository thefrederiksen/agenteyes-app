using System;
using System.Globalization;

namespace AgentEyes.Video
{
    /// <summary>
    /// Issue #128: decides whether an ffmpeg run has stopped making progress, from the
    /// <c>-progress pipe:1</c> key=value lines it prints.
    ///
    /// The signal is <c>out_time_us</c> - how far into the OUTPUT ffmpeg has written. While a
    /// filter is spinning (the speechnorm-on-NaN stall) ffmpeg keeps printing a progress block every
    /// stats period, but <c>out_time_us</c> is frozen. "Progress lines still arriving" is therefore
    /// NOT liveness; only an ADVANCING output time is. A run whose output time never advances at all
    /// (stuck before the first output) is stalled the same way, measured from construction.
    ///
    /// Pure and clock-injected so the decision is unit-testable without running ffmpeg.
    /// </summary>
    internal sealed class FfmpegProgressWatchdog
    {
        private readonly TimeSpan _stallTimeout;
        private long _bestOutTimeUs = long.MinValue;
        private TimeSpan _lastAdvanceAt;

        /// <param name="stallTimeout">How long the output time may stay frozen before the run is stalled.</param>
        /// <param name="startedAt">The clock reading when ffmpeg was started.</param>
        public FfmpegProgressWatchdog(TimeSpan stallTimeout, TimeSpan startedAt)
        {
            if (stallTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(stallTimeout), "stall timeout must be positive");
            _stallTimeout = stallTimeout;
            _lastAdvanceAt = startedAt;
        }

        /// <summary>The furthest output time seen, in seconds; null before the first valid reading.</summary>
        public double? OutputSeconds => _bestOutTimeUs == long.MinValue ? null : _bestOutTimeUs / 1_000_000.0;

        /// <summary>
        /// Feed one line of ffmpeg's <c>-progress</c> output. Only <c>out_time_us=N</c> lines
        /// matter; anything else (other keys, <c>N/A</c>, blank) is ignored. A reading counts as
        /// progress only when it is strictly greater than every earlier reading.
        /// </summary>
        public void Observe(string? line, TimeSpan now)
        {
            if (line == null) return;
            const string key = "out_time_us=";
            if (!line.StartsWith(key, StringComparison.Ordinal)) return;
            if (!long.TryParse(line.AsSpan(key.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                return;
            if (us > _bestOutTimeUs)
            {
                _bestOutTimeUs = us;
                _lastAdvanceAt = now;
            }
        }

        /// <summary>How long the output time has been frozen as of <paramref name="now"/>.</summary>
        public TimeSpan FrozenFor(TimeSpan now) => now - _lastAdvanceAt;

        /// <summary>True when the output time has not advanced for longer than the stall timeout.</summary>
        public bool IsStalled(TimeSpan now) => FrozenFor(now) > _stallTimeout;
    }
}
