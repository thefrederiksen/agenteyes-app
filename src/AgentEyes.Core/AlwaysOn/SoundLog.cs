using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgentEyes.Audio;

namespace AgentEyes.AlwaysOn
{
    /// <summary>Which sound decides what always-on keeps (issue #66).</summary>
    internal enum SoundSource { Mic, System, Both }

    /// <summary>What the sound log heard between two times (issue #72), for the keeper's minute log.</summary>
    internal readonly record struct SoundSummary(int LoudSeconds, int SoundSeconds);

    /// <summary>
    /// The always-on sound log (issues #66, #72): which SECONDS had sound, for the sources that count.
    /// It is fed the level of every audio buffer the capture produces, and the keeper asks it one
    /// question - "was there sound between these two times?".
    ///
    /// NO TRANSCRIPTION. Measuring a level is instant and free; the keeper never waits on a model.
    ///
    /// WHAT COUNTS AS SOUND (issue #72). The first rule - one second whose PEAK was 10 dB above a
    /// floor of per-second peaks - kept a silent afternoon as one 5 h 51 min clip: the peaks of a
    /// noisy built-in microphone's room noise spread wider than 10 dB, so the noise crossed the line
    /// on 43% of its seconds, and every click or bump kept a whole window. Now:
    ///
    ///  1. each second is measured by its RMS (the average level over the second), not its peak;
    ///  2. a second is LOUD when its RMS is above the line;
    ///  3. loud seconds are SOUND only when at least <see cref="SustainMinLoudSeconds"/> of them fall
    ///     inside some <see cref="SustainSpanSeconds"/>-second span - sustained sound, like talking.
    ///     An isolated loud second (a click, a bump, a key) never counts.
    ///
    /// The line is either a fixed dBFS RMS value the owner chose, or AUTO: the measured noise floor
    /// (the <see cref="FloorPercentile"/> percentile of the last ten minutes of per-second RMS) plus
    /// <see cref="AutoMarginDb"/>, and never below <see cref="AutoMinLineDb"/>. The floor is measured
    /// live, so a fan that starts or a room that quietens moves the line with it. Each source keeps
    /// its own floor - a microphone and the system mix have nothing in common. The same RMS +
    /// sustained rule applies to a fixed line.
    ///
    /// With <see cref="SoundSource.Both"/>, a second is loud when it is loud on either source, and
    /// the sustained rule is applied to those seconds together.
    ///
    /// A second is judged once it is over - when the first buffer of the next second arrives - so
    /// its RMS covers the whole second.
    ///
    /// Thread safe: the level callbacks arrive on the capture threads, the keeper reads on its own.
    /// </summary>
    internal sealed class SoundLog
    {
        /// <summary>How much history the automatic floor is measured over.</summary>
        public static readonly TimeSpan FloorWindow = TimeSpan.FromMinutes(10);

        /// <summary>The percentile of per-second RMS taken as the noise floor. Low, so that
        /// continuous talk - which has pauses between sentences - does not lift the floor into the
        /// speech it is meant to separate.</summary>
        public const double FloorPercentile = 0.20;

        /// <summary>Seconds of level history needed before the automatic line exists at all. Until
        /// then nothing counts as sound: a line guessed from nothing is the bug GateCalibration exists
        /// to prevent.</summary>
        public const int MinSecondsForAuto = 5;

        /// <summary>How far above the measured floor the Auto line sits, in dB (issue #72).</summary>
        public const double AutoMarginDb = 15.0;

        /// <summary>The lowest the Auto line ever goes, in dBFS RMS (issue #72). A quiet built-in
        /// microphone measures a room floor near -76 dBFS; a line 15 dB above that would still be
        /// reachable by room noise, while talking is far above -50.</summary>
        public const double AutoMinLineDb = -50.0;

        /// <summary>The level a second of digital silence reads as, in dBFS. A finite number, so the
        /// floor stays a number the status and the log can show.</summary>
        public const double SilenceDb = -120.0;

        /// <summary>The span, in seconds, inside which loud seconds must gather to be sound.</summary>
        public const int SustainSpanSeconds = 10;

        /// <summary>How many loud seconds a <see cref="SustainSpanSeconds"/> span needs to be sound.</summary>
        public const int SustainMinLoudSeconds = 3;

        private readonly object _gate = new();
        private readonly double? _fixedThresholdDb;
        private readonly Dictionary<SoundSource, Track> _tracks = new();
        private readonly SortedSet<long> _loudSeconds = new();
        private readonly SortedSet<long> _soundSeconds = new();
        private long? _lastSound;

        /// <param name="counts">The sources whose sound counts. <see cref="SoundSource.Both"/> means
        /// either the microphone or the system sound.</param>
        /// <param name="fixedThresholdDb">A fixed line in dBFS RMS, or null for Auto.</param>
        public SoundLog(SoundSource counts, double? fixedThresholdDb)
        {
            Counts = counts;
            _fixedThresholdDb = fixedThresholdDb;
            if (counts is SoundSource.Mic or SoundSource.Both) _tracks[SoundSource.Mic] = new Track();
            if (counts is SoundSource.System or SoundSource.Both) _tracks[SoundSource.System] = new Track();
            Log.Info($"[SoundLog] created: counts={counts} threshold={(fixedThresholdDb.HasValue ? $"{fixedThresholdDb.Value:0.#} dBFS RMS" : "auto")} "
                + $"sustained={SustainMinLoudSeconds} loud seconds in {SustainSpanSeconds}s");
        }

        public SoundSource Counts { get; }

        /// <summary>True when the given source's level is being listened to.</summary>
        public bool Listens(SoundSource source) => _tracks.ContainsKey(source);

        /// <summary>The time of the last second that had (sustained) sound, or null when there has been none.</summary>
        public DateTime? LastSoundUtc
        {
            get
            {
                lock (_gate)
                {
                    // Kept apart from the pruned set: "when was there last sound" is a question for the
                    // tray and the page long after the keeper has stopped needing the second itself.
                    return _lastSound.HasValue ? DateTimeOffset.FromUnixTimeSeconds(_lastSound.Value).UtcDateTime : null;
                }
            }
        }

        /// <summary>Feed one audio buffer's level from <paramref name="source"/>, captured at
        /// <paramref name="utc"/>. Ignored when that source does not count.</summary>
        public void Observe(SoundSource source, DateTime utc, AudioLevel level)
        {
            if (!_tracks.TryGetValue(source, out var track)) return;
            long second = ToSecond(utc);
            lock (_gate)
            {
                // The line is taken BEFORE the finished second joins the floor history: a second is
                // judged against the room as it was, not against itself.
                double? line = ThresholdFor(track);
                if (track.Observe(second, level, out long done, out double doneDb)
                    && line.HasValue && doneDb > line.Value)
                {
                    MarkLoud(done);
                }
            }
        }

        /// <summary>True when any second in [fromUtc, toUtc] (inclusive, whole seconds) had sustained sound.</summary>
        public bool AnySound(DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc < fromUtc) return false;
            long a = ToSecond(fromUtc), b = ToSecond(toUtc);
            lock (_gate)
            {
                return _soundSeconds.GetViewBetween(a, b).Count > 0;
            }
        }

        /// <summary>How many seconds in [fromUtc, toUtc] were loud, and how many of them were
        /// sustained sound. Both are zero for an empty or backwards range.</summary>
        public SoundSummary Summarize(DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc < fromUtc) return new SoundSummary(0, 0);
            long a = ToSecond(fromUtc), b = ToSecond(toUtc);
            lock (_gate)
            {
                return new SoundSummary(_loudSeconds.GetViewBetween(a, b).Count, _soundSeconds.GetViewBetween(a, b).Count);
            }
        }

        /// <summary>The line currently in force for a source, in dBFS RMS, or null while Auto has not
        /// yet heard enough to set one (or the source does not count).</summary>
        public double? CurrentThresholdDb(SoundSource source)
        {
            if (!_tracks.TryGetValue(source, out var track)) return null;
            lock (_gate) return ThresholdFor(track);
        }

        /// <summary>The measured noise floor of a source, in dBFS RMS, or null until enough has been
        /// heard (or the source does not count). Measured for a fixed line too, so the owner can see
        /// how far the line is above the room.</summary>
        public double? CurrentFloorDb(SoundSource source)
        {
            if (!_tracks.TryGetValue(source, out var track)) return null;
            lock (_gate) return track.FloorDb();
        }

        /// <summary>
        /// One line for the keeper's once-a-minute log (issue #72): per source the floor and the line,
        /// then how many seconds in [fromUtc, toUtc] were loud and whether sustained sound was found.
        /// </summary>
        public string Describe(DateTime fromUtc, DateTime toUtc)
        {
            var sb = new StringBuilder();
            foreach (var source in _tracks.Keys.OrderBy(k => k))
            {
                double? floor = CurrentFloorDb(source);
                double? line = CurrentThresholdDb(source);
                sb.Append(source.ToString().ToLowerInvariant())
                  .Append(" floor=").Append(Db(floor))
                  .Append(" line=").Append(Db(line))
                  .Append(_fixedThresholdDb.HasValue ? " (fixed)" : " (auto)")
                  .Append("; ");
            }
            var sum = Summarize(fromUtc, toUtc);
            int span = (int)Math.Round((toUtc - fromUtc).TotalSeconds) + 1;
            sb.Append($"last {span}s: loud={sum.LoudSeconds} sustained={(sum.SoundSeconds > 0 ? "yes" : "no")} ({sum.SoundSeconds}s)");
            return sb.ToString();
        }

        /// <summary>Forget seconds older than <paramref name="beforeUtc"/>. The keeper calls this once
        /// nothing it still has to decide can reach back that far.</summary>
        public void Prune(DateTime beforeUtc)
        {
            long cut = ToSecond(beforeUtc);
            lock (_gate)
            {
                _loudSeconds.RemoveWhere(s => s < cut);
                _soundSeconds.RemoveWhere(s => s < cut);
            }
        }

        /// <summary>
        /// Record a loud second and promote every loud second of any span that now holds enough of
        /// them to sound. Seconds can arrive out of order (two sources finish their seconds
        /// independently), so every span that could contain <paramref name="second"/> is checked.
        /// Caller holds the lock.
        /// </summary>
        private void MarkLoud(long second)
        {
            if (!_loudSeconds.Add(second)) return;
            for (long start = second - SustainSpanSeconds + 1; start <= second; start++)
            {
                var span = _loudSeconds.GetViewBetween(start, start + SustainSpanSeconds - 1);
                if (span.Count < SustainMinLoudSeconds) continue;
                foreach (long s in span)
                {
                    _soundSeconds.Add(s);
                    if (!_lastSound.HasValue || s > _lastSound.Value) _lastSound = s;
                }
            }
        }

        private double? ThresholdFor(Track track)
        {
            if (_fixedThresholdDb.HasValue) return _fixedThresholdDb.Value;
            double? floor = track.FloorDb();
            if (!floor.HasValue) return null;
            return AutoThreshold(floor.Value);
        }

        /// <summary>The Auto line for a measured floor: <see cref="AutoMarginDb"/> above it, and never
        /// below <see cref="AutoMinLineDb"/>.</summary>
        public static double AutoThreshold(double floorDb) =>
            Math.Max(floorDb + AutoMarginDb, AutoMinLineDb);

        /// <summary>A mean square (0..1 full scale) as dBFS RMS, reading silence as <see cref="SilenceDb"/>.</summary>
        public static double RmsDb(double meanSquare) =>
            meanSquare <= 0 ? SilenceDb : Math.Max(10.0 * Math.Log10(meanSquare), SilenceDb);

        private static string Db(double? db) =>
            db.HasValue ? db.Value.ToString("0.0", CultureInfo.InvariantCulture) + "dBFS" : "none";

        private static long ToSecond(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        /// <summary>One source's per-second RMS: the second being summed, and the history the
        /// automatic floor is measured from.</summary>
        private sealed class Track
        {
            private readonly Queue<(long Second, double Db)> _seconds = new();
            private long _current = long.MinValue;
            private double _sumSquares;
            private long _samples;

            /// <summary>Add a buffer. Returns true, with the finished second and its RMS, when this
            /// buffer belongs to a new second and so closes the one before it.</summary>
            public bool Observe(long second, AudioLevel level, out long doneSecond, out double doneDb)
            {
                doneSecond = 0;
                doneDb = SilenceDb;
                bool closed = false;
                if (second != _current)
                {
                    if (_current != long.MinValue)
                    {
                        doneSecond = _current;
                        doneDb = RmsDb(_samples > 0 ? _sumSquares / _samples : 0.0);
                        _seconds.Enqueue((doneSecond, doneDb));
                        closed = true;
                    }
                    _current = second;
                    _sumSquares = 0;
                    _samples = 0;
                    long oldest = second - (long)FloorWindow.TotalSeconds;
                    while (_seconds.Count > 0 && _seconds.Peek().Second < oldest) _seconds.Dequeue();
                }
                _sumSquares += level.SumSquares;
                _samples += level.Samples;
                return closed;
            }

            public double? FloorDb()
            {
                if (_seconds.Count < MinSecondsForAuto) return null;
                var sorted = _seconds.Select(s => s.Db).OrderBy(d => d).ToArray();
                int i = (int)Math.Floor(FloorPercentile * (sorted.Length - 1));
                return sorted[i];
            }
        }
    }
}
