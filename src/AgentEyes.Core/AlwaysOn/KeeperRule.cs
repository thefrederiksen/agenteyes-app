using System;
using System.Collections.Generic;

namespace AgentEyes.AlwaysOn
{
    /// <summary>One finished one-minute piece of the always-on recording.</summary>
    internal sealed record Piece(string Path, DateTime StartUtc, DateTime EndUtc, long Bytes)
    {
        public TimeSpan Duration => EndUtc - StartUtc;
    }

    /// <summary>The questions the keeper asks the sound log (issue #79). Both ranges are inclusive,
    /// in whole seconds; the answer is the start of the second, or null when no second had sound.</summary>
    internal interface ISoundTimes
    {
        DateTime? FirstSound(DateTime fromUtc, DateTime toUtc);
        DateTime? LastSound(DateTime fromUtc, DateTime toUtc);
    }

    /// <summary>The keep settings the keeper works with (issue #79). Ranges are the engine's and the
    /// page's business (<see cref="AlwaysOnKeepSettings"/>); the rule only needs them non-negative.</summary>
    internal readonly record struct KeepWindows(TimeSpan Before, TimeSpan After, TimeSpan SilenceGap);

    /// <summary>The clip the keeper has open between passes (issue #79).</summary>
    /// <param name="Id">The clip's number.</param>
    /// <param name="FirstSoundUtc">Its first second of sound - the lead-in is measured back from here.</param>
    /// <param name="LastSoundUtc">Its last second of sound so far - the tail and the silence gap run from here.</param>
    /// <param name="LastPieceEndUtc">The end of its last kept piece, to see a capture restart's hole.</param>
    internal sealed record OpenClip(int Id, DateTime FirstSoundUtc, DateTime LastSoundUtc, DateTime LastPieceEndUtc);

    /// <summary>
    /// A clip the keeper closed (issue #79): its sound, and the stretch of recording it keeps -
    /// from <see cref="StartUtc"/> (keep-before ahead of the first sound) to <see cref="EndUtc"/>
    /// (keep-after past the end of the last second of sound). The joiner trims the clip's first and
    /// last piece to this stretch.
    /// </summary>
    internal sealed record ClipSpan(int Clip, DateTime FirstSoundUtc, DateTime LastSoundUtc, DateTime StartUtc, DateTime EndUtc);

    /// <summary>What the keeper decided on one pass.</summary>
    internal sealed class KeeperPlan
    {
        /// <summary>
        /// Pieces to keep, in order, each with the clip it joins. <c>Copy</c> is true when the piece
        /// is ALSO still needed after this clip - it may be the lead-in of the next clip - so it is
        /// copied into this clip and stays where it is, to be decided again (issue #79).
        /// </summary>
        public List<(Piece Piece, int Clip, bool Copy)> Keep { get; } = new();

        /// <summary>Pieces with no sound near them, to delete.</summary>
        public List<Piece> Delete { get; } = new();

        /// <summary>Clips that are finished and should be trimmed and joined into one file, in order.</summary>
        public List<ClipSpan> Close { get; } = new();

        /// <summary>The clip left open after this pass, or null for none.</summary>
        public OpenClip? Open { get; set; }

        /// <summary>The last sound of the newest closed clip: sound up to here belongs to a clip that is
        /// finished, and never starts a new one.</summary>
        public DateTime? ClosedSoundUtc { get; set; }

        /// <summary>The next clip number to hand out.</summary>
        public int NextClip { get; set; }

        /// <summary>
        /// Gaps a clip was carried across (issue #81): a capture restart left no piece between
        /// <c>FromUtc</c> and <c>ToUtc</c>, and the clip went on instead of being split there.
        /// </summary>
        public List<(int Clip, DateTime FromUtc, DateTime ToUtc)> Bridged { get; } = new();
    }

    /// <summary>
    /// THE KEEP RULE (issues #66, #79), pure so it can be proven without ffmpeg or a clock.
    ///
    /// CLIPS COME FROM THE SOUND. Seconds of sound that follow each other with no quiet stretch
    /// longer than the SILENCE GAP are one clip; a quiet stretch longer than the gap starts a new
    /// one. A quiet stretch of EXACTLY the gap stays inside the clip: a second of sound lasts one
    /// second, so after sound in the second starting at S the quiet runs from S+1, and the next sound
    /// may start as late as S+1+gap (inclusive) and still belong to the clip (<see cref="Extend"/>).
    /// A clip keeps the recording from KEEP-BEFORE ahead of its first sound to KEEP-AFTER past the end
    /// of its last second of sound - its span (<see cref="ClipSpan"/>). A clip is CLOSED once the
    /// gap has passed with no sound - measured from the end of the last second of sound, plus
    /// <see cref="SoundSettle"/>, see there - or at the final pass.
    ///
    /// PIECES. The continuous recording in one-minute pieces IS the lead-in buffer: a piece is only
    /// deleted once no speech can need it. Pieces are decided in order and the pass stops at the first
    /// one that WAITS - every later piece can only wait longer.
    ///
    ///  - A piece that overlaps the open clip's span is KEPT (moved into the clip's holding folder at
    ///    once, so a crash loses no kept video).
    ///  - A piece past the open clip's span WAITS while the clip is still open: sound may come back
    ///    within the gap and make it the middle of the clip.
    ///  - With no clip open, a piece is kept when sound heard so far starts a clip whose span reaches it
    ///    (sound inside it, sound within keep-before after it, or sound within keep-after before it). It
    ///    is DELETED once keep-before has passed since its end with no such sound - nothing that can
    ///    still happen would make it a lead-in - and WAITS until then.
    ///  - A piece that straddles the end of a closed clip may also be the lead-in of the next clip: it
    ///    is COPIED into the closed clip and decided again for the next one.
    ///
    /// Only the first and last piece of a clip are trimmed to the span (by the joiner, with a stream
    /// copy); every piece in between is joined whole. The span never splits a clip.
    ///
    /// KNOWN LIMIT. A piece kept whole for an OPEN clip (it holds the clip's tail) is in that clip's
    /// holding folder; if the clip then closes with no more sound and the next clip's lead-in reaches
    /// back into that piece, that part of the lead-in is lost (never speech: the next sound came after
    /// the piece was decided). It needs a silence gap shorter than keep-before + keep-after + one
    /// piece, which the defaults (10 s, 10 s, 5 min, 60 s pieces) are far from.
    ///
    /// CAPTURE RESTARTS (issue #81). A restart leaves a hole between the last piece of the old capture
    /// and the first of the new one. A piece after such a hole still CONTINUES the open clip when the
    /// hole is no longer than <c>restartBridge</c> (the engine passes the silence gap), and the hole is
    /// reported in <see cref="KeeperPlan.Bridged"/> so it is logged with its length. Whether the clip
    /// goes on is still decided by the SOUND, so a restart can neither split a clip nor close it. A
    /// piece shorter than <see cref="ShortPiece"/> - what a stopping ffmpeg flushes out - is joined to
    /// the open clip it follows, so a one-second leftover never waits in front of the new capture.
    /// </summary>
    internal static class KeeperRule
    {
        /// <summary>Two pieces closer than this are one continuous recording.</summary>
        public static readonly TimeSpan ContiguityTolerance = TimeSpan.FromSeconds(5);

        /// <summary>A piece shorter than this is a restart's leftover, not a minute of recording (issue #81).</summary>
        public static readonly TimeSpan ShortPiece = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long after a second the sound log's answer about it is final (issue #79). A loud second
        /// becomes SOUND only once enough loud seconds gather within <see cref="SoundLog.SustainSpanSeconds"/>
        /// (issue #72), so the log can still promote a second that far back - plus two seconds for the
        /// level of the seconds being judged to arrive. No clip is closed and no piece deleted on an
        /// answer that can still change.
        /// </summary>
        public static readonly TimeSpan SoundSettle = TimeSpan.FromSeconds(SoundLog.SustainSpanSeconds + 2);

        /// <summary>A second of sound lasts one second: the tail is measured from its end.</summary>
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        /// <summary>Where a clip whose sound starts at <paramref name="firstSoundUtc"/> begins.</summary>
        public static DateTime SpanStart(DateTime firstSoundUtc, KeepWindows w) => firstSoundUtc - w.Before;

        /// <summary>Where a clip whose last second of sound starts at <paramref name="lastSoundUtc"/> ends.</summary>
        public static DateTime SpanEnd(DateTime lastSoundUtc, KeepWindows w) => lastSoundUtc + OneSecond + w.After;

        /// <param name="pending">Finished, undecided pieces, oldest first.</param>
        /// <param name="sound">The sound log.</param>
        /// <param name="nowUtc">Now: only sound up to here has been heard.</param>
        /// <param name="w">Keep-before, keep-after and the silence gap.</param>
        /// <param name="open">The clip still open from the last pass, or null.</param>
        /// <param name="closedSoundUtc">The last sound of the newest clip already closed, or null.</param>
        /// <param name="nextClip">The next clip number to hand out.</param>
        /// <param name="final">True when the recording has ended (stop, pause): every piece is decided on
        /// the sound heard so far and every clip closes.</param>
        /// <param name="restartBridge">The longest hole between pieces a clip is carried across (issue #81);
        /// zero means any hole longer than <see cref="ContiguityTolerance"/> closes the clip.</param>
        public static KeeperPlan Decide(
            IReadOnlyList<Piece> pending, ISoundTimes sound, DateTime nowUtc, KeepWindows w,
            OpenClip? open, DateTime? closedSoundUtc, int nextClip, bool final, TimeSpan restartBridge = default)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (sound == null) throw new ArgumentNullException(nameof(sound));
            if (w.Before < TimeSpan.Zero || w.After < TimeSpan.Zero || w.SilenceGap < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(w), "keep windows cannot be negative");
            if (restartBridge < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(restartBridge), "the restart bridge cannot be negative");

            var pass = new Pass(sound, nowUtc, w, final, restartBridge, open,
                new KeeperPlan { NextClip = nextClip, ClosedSoundUtc = closedSoundUtc });
            foreach (var p in pending)
            {
                if (!pass.Decide(p)) break;
            }
            if (final && pass.Open != null) pass.CloseOpen();
            pass.Plan.Open = pass.Open;
            return pass.Plan;
        }

        /// <summary>
        /// The last second of sound of the clip that has sound at <paramref name="lastUtc"/>: follow the
        /// sound forward as long as each next second of sound comes within the silence gap of the END
        /// of the one before. The second of sound starting at <paramref name="lastUtc"/> ends one second
        /// later, so the window asked about is [last+1, last+1+gap]: a quiet stretch of exactly the gap
        /// keeps the clip going, one second more than the gap starts a new clip (review fix pass, was
        /// off by one second). Only sound already heard (up to <paramref name="nowUtc"/>) is asked about.
        /// </summary>
        public static DateTime Extend(ISoundTimes sound, DateTime lastUtc, DateTime nowUtc, KeepWindows w)
        {
            while (true)
            {
                DateTime? next = sound.LastSound(lastUtc + OneSecond, Min(lastUtc + OneSecond + w.SilenceGap, nowUtc));
                if (next is not DateTime n || n <= lastUtc) return lastUtc;
                lastUtc = n;
            }
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        private enum Step { Decided, Wait, Reconsider }

        /// <summary>One keeper pass: the plan being built and the clip open at this point of it.</summary>
        private sealed class Pass
        {
            private readonly ISoundTimes _sound;
            private readonly DateTime _now;
            private readonly KeepWindows _w;
            private readonly bool _final;
            private readonly TimeSpan _bridge;

            public Pass(ISoundTimes sound, DateTime now, KeepWindows w, bool final, TimeSpan bridge, OpenClip? open, KeeperPlan plan)
            {
                _sound = sound;
                _now = now;
                _w = w;
                _final = final;
                _bridge = bridge;
                Open = open;
                Plan = plan;
            }

            public KeeperPlan Plan { get; }
            public OpenClip? Open { get; private set; }

            public void CloseOpen()
            {
                var o = Open!;
                Plan.Close.Add(new ClipSpan(o.Id, o.FirstSoundUtc, o.LastSoundUtc,
                    SpanStart(o.FirstSoundUtc, _w), SpanEnd(o.LastSoundUtc, _w)));
                Plan.ClosedSoundUtc = o.LastSoundUtc;
                Open = null;
            }

            /// <summary>Decide one piece. False when it waits - and so every later piece with it.</summary>
            public bool Decide(Piece p)
            {
                // A piece is looked at up to twice: against the open clip, and - when that clip ends
                // before or inside it - as the possible start of the next clip.
                while (true)
                {
                    if (Open != null)
                    {
                        var step = AgainstOpenClip(p);
                        if (step == Step.Decided) return true;
                        if (step == Step.Wait) return false;
                        // Reconsider: the open clip closed; p may start the next one.
                    }

                    // No clip open: does sound heard so far start a clip whose span reaches this piece?
                    DateTime from = p.StartUtc - _w.After;
                    if (Plan.ClosedSoundUtc is DateTime closed && from <= closed) from = closed + OneSecond;
                    DateTime to = p.EndUtc + _w.Before;
                    DateTime? first = _sound.FirstSound(from, Min(to, _now));
                    if (first is DateTime f && SpanStart(f, _w) < p.EndUtc)
                    {
                        Open = new OpenClip(Plan.NextClip++, f, Extend(_sound, f, _now, _w), p.StartUtc);
                        continue;   // decide the piece against the clip it starts
                    }

                    if (_final || _now >= to + SoundSettle)
                    {
                        Plan.Delete.Add(p);
                        return true;
                    }
                    // Waits: it may still become the lead-in to sound that has not happened yet.
                    return false;
                }
            }

            private Step AgainstOpenClip(Piece p)
            {
                // The clip's sound may have gone on since the last look.
                var open = Open!;
                DateTime last = Extend(_sound, open.LastSoundUtc, _now, _w);
                if (last != open.LastSoundUtc) Open = open = open with { LastSoundUtc = last };

                // How this piece sits against the clip: right after it, after a restart's hole the clip
                // is carried across, or too far away to belong to it.
                TimeSpan hole = p.StartUtc - open.LastPieceEndUtc;
                bool contiguous = hole.Duration() <= ContiguityTolerance;
                bool bridged = !contiguous && hole > TimeSpan.Zero && hole <= _bridge;
                if (!contiguous && !bridged)
                {
                    CloseOpen();
                    return Step.Reconsider;
                }

                // Closed once the gap has run from the END of the last second of sound (last + 1 s) and
                // the log's answer about the last second the gap could hold is final.
                bool closed = _final || _now >= last + OneSecond + _w.SilenceGap + SoundSettle;
                DateTime spanEnd = SpanEnd(last, _w);

                // A restart's leftover joins the clip it follows while the clip is open, sound or not (issue #81).
                bool leftover = !closed && p.Duration < ShortPiece;
                if (p.StartUtc >= spanEnd && !leftover)
                {
                    // Past the clip's tail. While the clip is open, sound may still come back within the
                    // gap and make this the middle of the clip: wait. Once closed, it is not this clip's.
                    if (!closed) return Step.Wait;
                    CloseOpen();
                    return Step.Reconsider;
                }

                if (bridged) Plan.Bridged.Add((open.Id, open.LastPieceEndUtc, p.StartUtc));
                if (!closed || p.EndUtc <= spanEnd)
                {
                    Plan.Keep.Add((p, open.Id, false));
                    Open = open with { LastPieceEndUtc = p.EndUtc > open.LastPieceEndUtc ? p.EndUtc : open.LastPieceEndUtc };
                    return Step.Decided;
                }

                // The closed clip ends inside this piece; the rest of it may be the lead-in of the next clip.
                DateTime? next = _sound.FirstSound(last + OneSecond, Min(p.EndUtc + _w.Before, _now));
                if (next is DateTime n && SpanStart(n, _w) < p.EndUtc)
                {
                    Plan.Keep.Add((p, open.Id, true));
                    CloseOpen();
                    return Step.Reconsider;      // the next clip's sound is heard: it starts with this piece
                }
                if (!_final && _now < p.EndUtc + _w.Before + SoundSettle)
                {
                    Plan.Keep.Add((p, open.Id, true));
                    CloseOpen();
                    return Step.Wait;            // it may yet be a lead-in: it stays and waits
                }
                Plan.Keep.Add((p, open.Id, false));
                CloseOpen();
                return Step.Decided;
            }
        }
    }
}
