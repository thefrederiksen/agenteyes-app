using System;
using System.Globalization;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// The three keep settings of always-on (issue #79), their defaults and their allowed ranges, in
    /// one place so the page, the config, the Control API and the engine all agree:
    ///
    ///  - KEEP BEFORE the speech: how much of what was on screen before the first sound a clip starts
    ///    with. Default 10 s, 0 - 120 s.
    ///  - KEEP AFTER the speech: how much follows the last sound before the clip ends. Default 10 s,
    ///    0 s up to the silence gap.
    ///  - SILENCE GAP: how long a quiet stretch must last before the clip is closed. A shorter pause
    ///    stays inside one clip. Default 5 min, 30 s - 30 min.
    ///
    /// ASSUMPTIONS of the issue, pending the owner: keep-after 10 s (not the full 5 silent minutes) and
    /// keep-before 10 s (the owner mentioned 10 or 30).
    /// </summary>
    internal static class AlwaysOnKeepSettings
    {
        public static readonly TimeSpan DefaultKeepBefore = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan DefaultKeepAfter = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan DefaultSilenceGap = TimeSpan.FromMinutes(5);

        public static readonly TimeSpan KeepBeforeMax = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan SilenceGapMin = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan SilenceGapMax = TimeSpan.FromMinutes(30);

        /// <summary>What is wrong with these settings, in words for the page and the log, or null when
        /// they are all inside their ranges.</summary>
        public static string? Problem(TimeSpan keepBefore, TimeSpan keepAfter, TimeSpan silenceGap)
        {
            if (keepBefore < TimeSpan.Zero || keepBefore > KeepBeforeMax)
                return $"Keep before the speech must be 0 s to {Describe(KeepBeforeMax)}, not {Describe(keepBefore)}.";
            if (silenceGap < SilenceGapMin || silenceGap > SilenceGapMax)
                return $"Close the clip after silence must be {Describe(SilenceGapMin)} to {Describe(SilenceGapMax)}, not {Describe(silenceGap)}.";
            if (keepAfter < TimeSpan.Zero || keepAfter > silenceGap)
                return $"Keep after the speech must be 0 s to the silence gap ({Describe(silenceGap)}), not {Describe(keepAfter)}.";
            return null;
        }

        /// <summary>Throw a <see cref="UsageException"/> saying what is out of range; a no-op when all are in range.</summary>
        public static void Validate(TimeSpan keepBefore, TimeSpan keepAfter, TimeSpan silenceGap)
        {
            string? problem = Problem(keepBefore, keepAfter, silenceGap);
            if (problem != null) throw new UsageException(problem);
        }

        /// <summary>A duration as the page says it: "10 s", "1 min", "1 min 30 s", "5 min", "-3 s".</summary>
        public static string Describe(TimeSpan t)
        {
            var inv = CultureInfo.InvariantCulture;
            if (t < TimeSpan.Zero) return "-" + Describe(t.Negate());
            double total = Math.Round(t.TotalSeconds, 1);
            if (total < 60) return total.ToString("0.#", inv) + " s";
            int minutes = (int)(total / 60);
            double seconds = Math.Round(total - minutes * 60, 1);
            return seconds == 0
                ? minutes.ToString(inv) + " min"
                : minutes.ToString(inv) + " min " + seconds.ToString("0.#", inv) + " s";
        }
    }
}
