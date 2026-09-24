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
    /// The issue's two assumptions were RESOLVED by the owner before implementation: keep-before 10 s,
    /// keep-after 10 s (not the full silent stretch), and the 5 min silence gap. Those are the defaults.
    ///
    /// <see cref="LeadInNote"/> states the keeper's one known limit for a combination the ranges allow
    /// (a gap shorter than keep-before + keep-after + one piece); it is a note, never a refusal.
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

        /// <summary>
        /// The keeper's known limit, in words, or null when these settings are clear of it (issue #79,
        /// review fix pass). A piece kept whole for an OPEN clip (it holds the clip's tail) sits in that
        /// clip's holding folder; when the clip then closes and the NEXT clip's lead-in reaches back into
        /// that same piece, the part of the lead-in inside it is not in the next clip (never speech - the
        /// next sound came after the piece was decided). That needs the silence gap to be shorter than
        /// keep-before + keep-after + one piece. The defaults (10 s, 10 s, 5 min, 60 s pieces) are far
        /// from it; the ranges allow it (a 30 s gap with 60 s of keep-before), so the page and the log
        /// state it rather than refuse it. Written for the page: one sentence, no jargon.
        /// </summary>
        public static string? LeadInNote(TimeSpan keepBefore, TimeSpan keepAfter, TimeSpan silenceGap, int pieceSeconds)
        {
            if (pieceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(pieceSeconds), "the piece length must be positive");
            TimeSpan limit = keepBefore + keepAfter + TimeSpan.FromSeconds(pieceSeconds);
            if (silenceGap >= limit) return null;
            return $"Note: with a silence gap under {Describe(limit)} (keep before + keep after + one {Describe(TimeSpan.FromSeconds(pieceSeconds))} "
                   + "piece of recording), the start of a clip that follows the one before it closely can be cut short at a piece boundary; "
                   + "no speech is lost.";
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
