using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.Audio;

namespace AgentEyes.AlwaysOn
{
    /// <summary>Which sound decides what always-on keeps (issue #66).</summary>
    internal enum SoundSource { Mic, System, Both }

    /// <summary>
    /// The always-on sound log (issue #66): which SECONDS had sound above the line, for the sources
    /// that count. It is fed the peak of every audio buffer the level meters already produce, and the
    /// keeper asks it one question - "was there sound between these two times?".
    ///
    /// NO TRANSCRIPTION. Measuring a level is instant and free; the keeper never waits on a model.
    ///
    /// The line is either a fixed dBFS value the owner chose, or AUTO. Auto follows the noise gate's
    /// own calibration rule (<see cref="GateCalibration.FloorMarginDb"/> above the measured noise
    /// floor), measured live: the floor is a low percentile of the last ten minutes of per-second
    /// peaks, so a fan that starts or a room that quietens moves the line with it. Each source keeps
    /// its own floor - a microphone and the system mix have nothing in common.
    ///
    /// Thread safe: the level callbacks arrive on the capture threads, the keeper reads on its own.
    /// </summary>
    internal sealed class SoundLog
    {
        /// <summary>How much history the automatic floor is measured over.</summary>
        public static readonly TimeSpan FloorWindow = TimeSpan.FromMinutes(10);

        /// <summary>The percentile of per-second peaks taken as the noise floor. Low, so that
        /// continuous talk - which has pauses between sentences - does not lift the floor into the
        /// speech it is meant to separate.</summary>
        public const double FloorPercentile = 0.20;

        /// <summary>Seconds of level history needed before the automatic line exists at all. Until
        /// then nothing counts as sound: a line guessed from nothing is the bug GateCalibration exists
        /// to prevent.</summary>
        public const int MinSecondsForAuto = 5;

        /// <summary>The quietest floor the automatic line is measured from. A source delivering
        /// digital silence has a floor of minus infinity, and "anything above minus infinity plus ten"
        /// would count the dither of a muted device as speech.</summary>
        public const double FloorClampDb = -70.0;

        private readonly object _gate = new();
        private readonly double? _fixedThresholdDb;
        private readonly Dictionary<SoundSource, Track> _tracks = new();
        private readonly SortedSet<long> _loudSeconds = new();
        private long? _lastLoud;

        /// <param name="counts">The sources whose sound counts. <see cref="SoundSource.Both"/> means
        /// either the microphone or the system sound.</param>
        /// <param name="fixedThresholdDb">A fixed line in dBFS, or null for Auto.</param>
        public SoundLog(SoundSource counts, double? fixedThresholdDb)
        {
            Counts = counts;
            _fixedThresholdDb = fixedThresholdDb;
            if (counts is SoundSource.Mic or SoundSource.Both) _tracks[SoundSource.Mic] = new Track();
            if (counts is SoundSource.System or SoundSource.Both) _tracks[SoundSource.System] = new Track();
            Log.Info($"[SoundLog] created: counts={counts} threshold={(fixedThresholdDb.HasValue ? $"{fixedThresholdDb.Value:0.#} dBFS" : "auto")}");
        }

        public SoundSource Counts { get; }

        /// <summary>True when the given source's level is being listened to.</summary>
        public bool Listens(SoundSource source) => _tracks.ContainsKey(source);

        /// <summary>The time of the last second that had sound, or null when there has been none.</summary>
        public DateTime? LastSoundUtc
        {
            get
            {
                lock (_gate)
                {
                    // Kept apart from the pruned set: "when was there last sound" is a question for the
                    // tray and the page long after the keeper has stopped needing the second itself.
                    return _lastLoud.HasValue ? DateTimeOffset.FromUnixTimeSeconds(_lastLoud.Value).UtcDateTime : null;
                }
            }
        }

        /// <summary>Feed one audio buffer's peak (0..1) from <paramref name="source"/>. Ignored when
        /// that source does not count.</summary>
        public void Observe(SoundSource source, DateTime utc, float peak)
        {
            if (!_tracks.TryGetValue(source, out var track)) return;
            long second = ToSecond(utc);
            double db = ToDb(peak);
            lock (_gate)
            {
                track.Observe(second, db);
                double? line = ThresholdFor(track);
                if (line.HasValue && db > line.Value)
                {
                    _loudSeconds.Add(second);
                    if (!_lastLoud.HasValue || second > _lastLoud.Value) _lastLoud = second;
                }
            }
        }

        /// <summary>True when any second in [fromUtc, toUtc] (inclusive, whole seconds) had sound.</summary>
        public bool AnySound(DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc < fromUtc) return false;
            long a = ToSecond(fromUtc), b = ToSecond(toUtc);
            lock (_gate)
            {
                return _loudSeconds.GetViewBetween(a, b).Count > 0;
            }
        }

        /// <summary>The line currently in force for a source, in dBFS, or null while Auto has not yet
        /// heard enough to set one.</summary>
        public double? CurrentThresholdDb(SoundSource source)
        {
            if (!_tracks.TryGetValue(source, out var track)) return null;
            lock (_gate) return ThresholdFor(track);
        }

        /// <summary>Forget sound older than <paramref name="beforeUtc"/>. The keeper calls this once
        /// nothing it still has to decide can reach back that far.</summary>
        public void Prune(DateTime beforeUtc)
        {
            long cut = ToSecond(beforeUtc);
            lock (_gate)
            {
                _loudSeconds.RemoveWhere(s => s < cut);
            }
        }

        private double? ThresholdFor(Track track)
        {
            if (_fixedThresholdDb.HasValue) return _fixedThresholdDb.Value;
            double? floor = track.FloorDb();
            if (!floor.HasValue) return null;
            return AutoThreshold(floor.Value);
        }

        /// <summary>The Auto line for a measured floor: the noise gate's own margin above it.</summary>
        public static double AutoThreshold(double floorDb) =>
            Math.Max(floorDb, FloorClampDb) + GateCalibration.FloorMarginDb;

        public static double ToDb(float peak) => peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);

        private static long ToSecond(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        /// <summary>One source's per-second peak history, for the automatic floor.</summary>
        private sealed class Track
        {
            private readonly Queue<(long Second, double Db)> _seconds = new();
            private long _current = long.MinValue;
            private double _currentPeak = double.NegativeInfinity;

            public void Observe(long second, double db)
            {
                if (second != _current)
                {
                    if (_current != long.MinValue) _seconds.Enqueue((_current, _currentPeak));
                    _current = second;
                    _currentPeak = db;
                    long oldest = second - (long)FloorWindow.TotalSeconds;
                    while (_seconds.Count > 0 && _seconds.Peek().Second < oldest) _seconds.Dequeue();
                }
                else if (db > _currentPeak)
                {
                    _currentPeak = db;
                }
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
