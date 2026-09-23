using System;
using System.Collections.Generic;

namespace AgentEyes.AlwaysOn
{
    /// <summary>One finished one-minute piece of the always-on recording.</summary>
    internal sealed record Piece(string Path, DateTime StartUtc, DateTime EndUtc, long Bytes)
    {
        public TimeSpan Duration => EndUtc - StartUtc;
    }

    /// <summary>What the keeper decided on one pass.</summary>
    internal sealed class KeeperPlan
    {
        /// <summary>Pieces to keep, in order, each with the clip it joins.</summary>
        public List<(Piece Piece, int Clip)> Keep { get; } = new();

        /// <summary>Pieces with no sound near them, to delete.</summary>
        public List<Piece> Delete { get; } = new();

        /// <summary>Clips that are finished and should be joined into one file, in order.</summary>
        public List<int> Close { get; } = new();

        /// <summary>The clip left open after this pass, or null for none.</summary>
        public int? OpenClip { get; set; }

        /// <summary>The end of the last kept piece of the open clip, for contiguity next time.</summary>
        public DateTime? OpenClipEndUtc { get; set; }

        /// <summary>The next clip number to hand out.</summary>
        public int NextClip { get; set; }
    }

    /// <summary>
    /// THE KEEP RULE (issue #66), pure so it can be proven without ffmpeg or a clock.
    ///
    /// A finished piece [start, end) is KEPT when there was sound anywhere in
    /// [start - after, end + before]: sound inside it, sound up to "after" minutes before it (it is the
    /// tail of that sound), or sound up to "before" minutes after it (it is the lead-in to that sound).
    /// It is DELETED once the clock is past end + before with no sound in that window - nothing that
    /// can still happen would make it a lead-in. Until then it WAITS.
    ///
    /// Pieces are decided in order and the pass stops at the first one that waits: every later piece
    /// has a window starting later, so if the earlier one has heard no sound yet, neither has it.
    ///
    /// CLIPS. Kept pieces that follow each other with no gap join one clip. A clip CLOSES when the
    /// piece after its last kept piece is not kept: that piece's window reaches "after" minutes back,
    /// so it not being kept means "after" minutes have passed with no sound. A closed clip is joined
    /// into one file. Sound that comes back later starts a NEW clip, even when its lead-in touches the
    /// old one - the old clip was already finished and written.
    ///
    /// FINAL PASS (stop or pause): the recording has ended, so every piece is decided on the sound
    /// heard so far and every clip closes.
    /// </summary>
    internal static class KeeperRule
    {
        /// <summary>Two pieces closer than this are one continuous recording.</summary>
        public static readonly TimeSpan ContiguityTolerance = TimeSpan.FromSeconds(5);

        /// <param name="pending">Finished, undecided pieces, oldest first.</param>
        /// <param name="anySound">Whether any second in [from, to] had sound.</param>
        /// <param name="openClip">The clip still open from the last pass, or null.</param>
        /// <param name="openClipEndUtc">The end of that clip's last kept piece.</param>
        /// <param name="nextClip">The next clip number to hand out.</param>
        public static KeeperPlan Decide(
            IReadOnlyList<Piece> pending, Func<DateTime, DateTime, bool> anySound, DateTime nowUtc,
            TimeSpan before, TimeSpan after, int? openClip, DateTime? openClipEndUtc, int nextClip,
            bool final)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (anySound == null) throw new ArgumentNullException(nameof(anySound));
            if (before < TimeSpan.Zero || after < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(before), "keep windows cannot be negative");

            var plan = new KeeperPlan { NextClip = nextClip };
            int? open = openClip;
            DateTime? openEnd = openClipEndUtc;

            foreach (var p in pending)
            {
                DateTime from = p.StartUtc - after;
                DateTime to = p.EndUtc + before;
                // Only sound that has already happened can be asked about; the window's future part
                // is exactly what "wait" waits for.
                DateTime heardTo = to < nowUtc ? to : nowUtc;
                bool sound = anySound(from, heardTo);

                if (sound)
                {
                    bool continues = open.HasValue && openEnd.HasValue
                        && (p.StartUtc - openEnd.Value).Duration() <= ContiguityTolerance;
                    if (!continues)
                    {
                        if (open.HasValue) plan.Close.Add(open.Value);
                        open = plan.NextClip++;
                    }
                    plan.Keep.Add((p, open!.Value));
                    openEnd = p.EndUtc;
                    continue;
                }

                // Not kept. Whether it is deleted now or waits, the clip before it has had "after"
                // minutes of silence and is finished.
                if (open.HasValue)
                {
                    plan.Close.Add(open.Value);
                    open = null;
                    openEnd = null;
                }

                if (final || nowUtc >= to)
                {
                    plan.Delete.Add(p);
                    continue;
                }

                // Waits: it may still become the lead-in to sound that has not happened yet.
                break;
            }

            if (final && open.HasValue)
            {
                plan.Close.Add(open.Value);
                open = null;
                openEnd = null;
            }

            plan.OpenClip = open;
            plan.OpenClipEndUtc = openEnd;
            return plan;
        }
    }
}
