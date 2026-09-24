using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.AlwaysOn;

namespace AgentEyes.Tests
{
    /// <summary>
    /// A stand-in sound log for the keeper tests (issue #79): the seconds, as offsets from a base time,
    /// that had sustained sound. It also records the latest time it was asked about, so a test can prove
    /// the keeper never asks about sound that has not happened yet.
    /// </summary>
    internal sealed class TestSounds : ISoundTimes
    {
        private readonly DateTime _t0;
        private readonly SortedSet<int> _seconds;

        public TestSounds(DateTime t0, IEnumerable<int> seconds)
        {
            _t0 = t0;
            _seconds = new SortedSet<int>(seconds);
        }

        /// <summary>No sound at all.</summary>
        public static TestSounds Silence(DateTime t0) => new(t0, Array.Empty<int>());

        /// <summary>Sound at each of the given second offsets.</summary>
        public static TestSounds At(DateTime t0, params int[] seconds) => new(t0, seconds);

        /// <summary>Sound on every second from <paramref name="from"/> to <paramref name="to"/>, inclusive.</summary>
        public static TestSounds Range(DateTime t0, int from, int to) => new(t0, Enumerable.Range(from, to - from + 1));

        /// <summary>This log plus sound on every second from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public TestSounds And(int from, int to) => new(_t0, _seconds.Concat(Enumerable.Range(from, to - from + 1)));

        /// <summary>The latest "to" any question asked about, or null before the first question.</summary>
        public DateTime? LatestAsked { get; private set; }

        public DateTime? FirstSound(DateTime fromUtc, DateTime toUtc)
        {
            Note(toUtc);
            foreach (int s in _seconds)
            {
                var t = _t0.AddSeconds(s);
                if (t >= fromUtc && t <= toUtc) return t;
            }
            return null;
        }

        public DateTime? LastSound(DateTime fromUtc, DateTime toUtc)
        {
            Note(toUtc);
            foreach (int s in _seconds.Reverse())
            {
                var t = _t0.AddSeconds(s);
                if (t >= fromUtc && t <= toUtc) return t;
            }
            return null;
        }

        private void Note(DateTime toUtc)
        {
            if (LatestAsked == null || toUtc > LatestAsked) LatestAsked = toUtc;
        }
    }
}
