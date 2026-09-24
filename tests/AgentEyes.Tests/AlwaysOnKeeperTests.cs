using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.AlwaysOn;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The always-on KEEP RULE (issues #66, #79) and the TRIM PLANNER (issue #79), proven without
    /// ffmpeg or a clock: which pieces are kept, deleted or wait, when a clip closes, and where its
    /// first and last piece are cut.
    /// </summary>
    public class AlwaysOnKeeperTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

        /// <summary>The issue's defaults: 10 s before, 10 s after, a clip closes after 5 min of silence.</summary>
        private static readonly KeepWindows W = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

        /// <summary>A short silence gap, for the cases where two clips meet inside one piece.</summary>
        private static readonly KeepWindows ShortGap = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

        private static DateTime At(double seconds) => T0.AddSeconds(seconds);

        /// <summary>Minute-long pieces starting at the given minute offsets from T0.</summary>
        private static List<Piece> Pieces(params int[] minutes) =>
            minutes.Select(m => new Piece($"p{m}", T0 + TimeSpan.FromMinutes(m), T0 + TimeSpan.FromMinutes(m + 1), 1000)).ToList();

        private static List<Piece> PiecesRange(int from, int count) => Pieces(Enumerable.Range(from, count).ToArray());

        private static IEnumerable<string> Names(IEnumerable<Piece> pieces) => pieces.Select(p => p.Path);

        /// <summary>The clip's kept pieces as the joiner sees them: 60 s each, starting where the keeper's did.</summary>
        private static TrimPlan TrimFor(KeeperPlan plan, ClipSpan span) =>
            ClipTrim.Plan(plan.Keep.Where(k => k.Clip == span.Clip)
                              .Select(k => new TrimInput(k.Piece.Path, k.Piece.StartUtc, k.Piece.Duration.TotalSeconds)).ToList(),
                          span.StartUtc, span.EndUtc, AlwaysOnArgs.DefaultKeyframeSeconds);

        // ---- AC1: the issue's four numeric cases -----------------------------------------------

        [Fact]
        public void Decide_SpeechAt125s_KeepBefore10s_ClipStartsAtTheKeyframeAtOrBefore115sAndNoEarlierThan113s()
        {
            var sound = TestSounds.Range(T0, 125, 130);

            var plan = KeeperRule.Decide(Pieces(0, 1, 2, 3), sound, T0 + 10 * Min, W, null, null, 1, final: false);

            // The piece before the lead-in is deleted; the lead-in's piece and the speech's are kept.
            Assert.Equal(new[] { "p0", "p3" }, Names(plan.Delete));
            Assert.Equal(new[] { "p1", "p2" }, Names(plan.Keep.Select(k => k.Piece)));
            var span = Assert.Single(plan.Close);
            Assert.Equal(At(125), span.FirstSoundUtc);
            Assert.Equal(At(115), span.StartUtc);

            var trim = TrimFor(plan, span);
            var first = trim.Parts[0];
            Assert.Equal("p1", first.Path);
            Assert.Equal(54, first.InSeconds);                         // piece p1 opened at 60 s: 60 + 54 = 114
            Assert.True(first.KeptStartUtc <= At(115), $"starts at {(first.KeptStartUtc - T0).TotalSeconds}s, after 115s");
            Assert.True(first.KeptStartUtc >= At(113), $"starts at {(first.KeptStartUtc - T0).TotalSeconds}s, before 113s");
            Assert.Equal(0, first.InSeconds!.Value % AlwaysOnArgs.DefaultKeyframeSeconds);   // on a keyframe
        }

        [Fact]
        public void Decide_LastSpeechAt300s_KeepAfter10s_ClipEndsAt310s()
        {
            // Speech from 125 s until 300 s: its last second of sound is the one starting at 299 s.
            var sound = TestSounds.Range(T0, 125, 299);

            var plan = KeeperRule.Decide(PiecesRange(0, 6), sound, T0 + 6 * Min, W, null, null, 1, final: true);

            var span = Assert.Single(plan.Close);
            Assert.Equal(At(299), span.LastSoundUtc);
            Assert.Equal(At(310), span.EndUtc);
            var trim = TrimFor(plan, span);
            var last = trim.Parts[^1];
            Assert.Equal("p5", last.Path);
            DateTime end = last.StartUtc.AddSeconds(last.OutSeconds!.Value);
            Assert.InRange((end - T0).TotalSeconds, 308, 312);          // 310 s +- 2 s
            Assert.Equal(310, (end - T0).TotalSeconds);
            // Only the first and the last piece are cut; the ones between are joined whole.
            Assert.All(trim.Parts.Skip(1).Take(trim.Parts.Count - 2), p => Assert.False(p.IsTrimmed));
        }

        [Fact]
        public void Decide_TwoMinutePauseInsideSpeech_FiveMinuteGap_IsOneClip()
        {
            var sound = TestSounds.Range(T0, 60, 119).And(240, 299);   // a 2-minute pause between

            var plan = KeeperRule.Decide(PiecesRange(0, 10), sound, T0 + 20 * Min, W, null, null, 1, final: false);

            var span = Assert.Single(plan.Close);
            Assert.Equal(At(60), span.FirstSoundUtc);
            Assert.Equal(At(299), span.LastSoundUtc);
            Assert.Equal(new[] { "p0", "p1", "p2", "p3", "p4", "p5" }, Names(plan.Keep.Select(k => k.Piece)));
            Assert.All(plan.Keep, k => Assert.Equal(span.Clip, k.Clip));
            // The pause's pieces (p2, p3) are inside the clip, joined whole.
            Assert.DoesNotContain(TrimFor(plan, span).Parts, p => (p.Path == "p2" || p.Path == "p3") && p.IsTrimmed);
        }

        [Fact]
        public void Decide_SixMinutePauseInsideSpeech_FiveMinuteGap_IsTwoClips()
        {
            var sound = TestSounds.Range(T0, 60, 119).And(480, 539);   // 361 s from the last sound to the next

            var plan = KeeperRule.Decide(PiecesRange(0, 15), sound, T0 + 30 * Min, W, null, null, 1, final: false);

            Assert.Equal(2, plan.Close.Count);
            Assert.Equal((At(60), At(119)), (plan.Close[0].FirstSoundUtc, plan.Close[0].LastSoundUtc));
            Assert.Equal((At(480), At(539)), (plan.Close[1].FirstSoundUtc, plan.Close[1].LastSoundUtc));
            Assert.NotEqual(plan.Close[0].Clip, plan.Close[1].Clip);
            Assert.Equal(new[] { "p0", "p1", "p2" }, Names(plan.Keep.Where(k => k.Clip == plan.Close[0].Clip).Select(k => k.Piece)));
            Assert.Equal(new[] { "p7", "p8", "p9" }, Names(plan.Keep.Where(k => k.Clip == plan.Close[1].Clip).Select(k => k.Piece)));
            // The silent minutes between the clips are deleted, not kept.
            Assert.Equal(new[] { "p3", "p4", "p5", "p6", "p10", "p11", "p12", "p13", "p14" }, Names(plan.Delete));
        }

        // ---- waiting, deleting, closing ------------------------------------------------------------

        [Fact]
        public void Decide_SilentPieceInsideTheLeadInWindow_WaitsUntilKeepBeforeAndTheSettlePass()
        {
            // Piece 0 ends at 60 s; speech up to 70 s (plus the settle time) would still want it.
            var waiting = KeeperRule.Decide(Pieces(0), TestSounds.Silence(T0), At(60 + 10 + 11), W, null, null, 1, final: false);
            Assert.Empty(waiting.Keep);
            Assert.Empty(waiting.Delete);

            var deleted = KeeperRule.Decide(Pieces(0), TestSounds.Silence(T0), At(60 + 10) + KeeperRule.SoundSettle, W, null, null, 1, final: false);
            Assert.Equal(new[] { "p0" }, Names(deleted.Delete));
        }

        [Fact]
        public void Decide_PieceAfterTheTailWhileTheClipIsOpen_Waits()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1, 2), TestSounds.Range(T0, 30, 35), T0 + 3 * Min, W, null, null, 1, final: false);

            Assert.Equal(new[] { "p0" }, Names(plan.Keep.Select(k => k.Piece)));
            Assert.Empty(plan.Delete);
            Assert.Empty(plan.Close);
            Assert.NotNull(plan.Open);
            Assert.Equal(At(35), plan.Open!.LastSoundUtc);
            Assert.Equal(At(60), plan.Open.LastPieceEndUtc);
        }

        [Fact]
        public void Decide_SoundReturnsWithinTheGap_TheWaitingPiecesAreTheMiddleOfTheClip()
        {
            var open = new OpenClip(1, At(30), At(35), At(60));

            var plan = KeeperRule.Decide(Pieces(1, 2, 3), TestSounds.Range(T0, 30, 35).And(200, 205), At(250), W, open, null, 2, final: false);

            Assert.Equal(new[] { "p1", "p2", "p3" }, Names(plan.Keep.Select(k => k.Piece)));
            Assert.All(plan.Keep, k => Assert.Equal(1, k.Clip));
            Assert.Empty(plan.Close);
            Assert.Equal(At(205), plan.Open!.LastSoundUtc);
            Assert.Equal(2, plan.NextClip);
        }

        [Fact]
        public void Decide_GapPassesWithNoSound_TheClipClosesAndTheQuietPiecesGo()
        {
            var open = new OpenClip(1, At(30), At(35), At(60));

            var plan = KeeperRule.Decide(Pieces(1, 2, 3, 4, 5, 6), TestSounds.Range(T0, 30, 35), At(390), W, open, null, 2, final: false);

            var span = Assert.Single(plan.Close);
            Assert.Equal((1, At(20), At(46)), (span.Clip, span.StartUtc, span.EndUtc));
            Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5" }, Names(plan.Delete));   // p6 may still be a lead-in
            Assert.Empty(plan.Keep);
            Assert.Null(plan.Open);
            Assert.Equal(At(35), plan.ClosedSoundUtc);
        }

        [Fact]
        public void Decide_GapNotYetPassed_TheClipStaysOpen()
        {
            var open = new OpenClip(1, At(30), At(35), At(60));

            // The last second of sound starts at 35 s and ends at 36 s; 36 s + 5 min gap + the 12 s settle
            // time is 348 s. At 347 s the clip is still open; at 348 s it closes.
            var plan = KeeperRule.Decide(Pieces(1, 2), TestSounds.Range(T0, 30, 35), At(347), W, open, null, 2, final: false);

            Assert.Empty(plan.Close);
            Assert.Empty(plan.Delete);
            Assert.Equal(1, plan.Open!.Id);

            var closing = KeeperRule.Decide(Pieces(1, 2), TestSounds.Range(T0, 30, 35), At(348), W, open, null, 2, final: false);
            Assert.Single(closing.Close);
            Assert.Null(closing.Open);
        }

        // ---- the silence gap boundary (review fix pass, finding 3) -----------------------------------

        [Fact]
        public void Extend_QuietStretchOfExactlyTheGap_StaysInsideTheClip()
        {
            // Sound in the second starting at 35 s ends at 36 s. With a 5 min gap the quiet may run to
            // 336 s; sound starting AT 336 s follows a quiet stretch of exactly 300 s and is the same clip.
            var sound = TestSounds.Range(T0, 30, 35).And(336, 340);

            DateTime last = KeeperRule.Extend(sound, At(35), At(1000), W);

            Assert.Equal(At(340), last);
        }

        [Fact]
        public void Extend_QuietStretchOneSecondLongerThanTheGap_StartsANewClip()
        {
            // Sound starting at 337 s follows 301 s of quiet: longer than the gap, so the clip ends at 35 s.
            var sound = TestSounds.Range(T0, 30, 35).And(337, 340);

            DateTime last = KeeperRule.Extend(sound, At(35), At(1000), W);

            Assert.Equal(At(35), last);
        }

        [Fact]
        public void Decide_PauseOfExactlyTheGap_IsOneClip_OneSecondMoreIsTwo()
        {
            // The whole rule, not just Extend: speech to 119 s (last second ends at 120 s), then quiet.
            // Speech again at 420 s (a 300 s pause, exactly the gap) is one clip; at 421 s it is two.
            var one = KeeperRule.Decide(PiecesRange(0, 12), TestSounds.Range(T0, 60, 119).And(420, 430), T0 + 30 * Min, W, null, null, 1, final: false);
            var span = Assert.Single(one.Close);
            Assert.Equal((At(60), At(430)), (span.FirstSoundUtc, span.LastSoundUtc));

            var two = KeeperRule.Decide(PiecesRange(0, 12), TestSounds.Range(T0, 60, 119).And(421, 430), T0 + 30 * Min, W, null, null, 1, final: false);
            Assert.Equal(2, two.Close.Count);
            Assert.Equal(At(119), two.Close[0].LastSoundUtc);
            Assert.Equal(At(421), two.Close[1].FirstSoundUtc);
        }

        [Fact]
        public void Decide_ClosedClipEndsInsideAPieceThatHoldsTheNextSpeech_ThePieceGoesToBothClips()
        {
            // 30 s gap: speech at 5-6 s and again at 50-52 s is two clips, and both touch piece 0.
            var sound = TestSounds.At(T0, 5, 6, 50, 51, 52);

            var plan = KeeperRule.Decide(Pieces(0), sound, At(200), ShortGap, null, null, 1, final: false);

            Assert.Equal(new[] { ("p0", 1, true), ("p0", 2, false) }, plan.Keep.Select(k => (k.Piece.Path, k.Clip, k.Copy)));
            var span = Assert.Single(plan.Close);
            Assert.Equal((1, At(-5), At(17)), (span.Clip, span.StartUtc, span.EndUtc));
            Assert.Equal(2, plan.Open!.Id);
            Assert.Equal((At(50), At(52)), (plan.Open.FirstSoundUtc, plan.Open.LastSoundUtc));
        }

        [Fact]
        public void Decide_ClosedClipEndsInsideAPieceThatMayStillBeALeadIn_ThePieceIsCopiedAndWaits()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1), TestSounds.At(T0, 5, 6), At(65), ShortGap, null, null, 1, final: false);

            Assert.Equal(new[] { ("p0", 1, true) }, plan.Keep.Select(k => (k.Piece.Path, k.Clip, k.Copy)));
            Assert.Single(plan.Close);
            Assert.Empty(plan.Delete);
            Assert.Null(plan.Open);
            Assert.Equal(At(6), plan.ClosedSoundUtc);

            // The next pass, with no new speech once keep-before has passed: the piece goes, and the
            // closed clip's sound does not start a second clip.
            var later = KeeperRule.Decide(Pieces(0, 1), TestSounds.At(T0, 5, 6), At(100), ShortGap, null, plan.ClosedSoundUtc, plan.NextClip, final: false);
            Assert.Equal(new[] { "p0" }, Names(later.Delete));
            Assert.Empty(later.Keep);
            Assert.Empty(later.Close);
        }

        [Fact]
        public void Decide_CopiedPieceThenHearsTheNextSpeech_ItStartsTheNextClip()
        {
            var first = KeeperRule.Decide(Pieces(0, 1), TestSounds.At(T0, 5, 6), At(65), ShortGap, null, null, 1, final: false);

            var sound = TestSounds.At(T0, 5, 6, 65, 66, 67);
            var later = KeeperRule.Decide(Pieces(0, 1), sound, At(100), ShortGap, null, first.ClosedSoundUtc, first.NextClip, final: false);

            Assert.Equal(new[] { ("p0", 2, false), ("p1", 2, false) }, later.Keep.Select(k => (k.Piece.Path, k.Clip, k.Copy)));
            Assert.Equal(At(65), later.Open!.FirstSoundUtc);
        }

        [Fact]
        public void Decide_FinalPass_DecidesEverythingAndClosesTheClip()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1, 2), TestSounds.At(T0, 90), T0 + 3 * Min, W, null, null, 1, final: true);

            Assert.Equal(new[] { "p1" }, Names(plan.Keep.Select(k => k.Piece)));
            Assert.Equal(new[] { "p0", "p2" }, Names(plan.Delete));
            Assert.Equal(new[] { 1 }, plan.Close.Select(c => c.Clip));
            Assert.Null(plan.Open);
        }

        [Fact]
        public void Decide_FinalPassSilent_DeletesWaitingPieces()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1), TestSounds.Silence(T0), T0 + 2 * Min, W, null, null, 1, final: true);

            Assert.Equal(2, plan.Delete.Count);
            Assert.Empty(plan.Close);
        }

        [Fact]
        public void Decide_StopsAtTheFirstWaitingPiece()
        {
            // Piece 0 waits (inside its lead-in window, no sound); piece 1 must not be decided either.
            var plan = KeeperRule.Decide(Pieces(0, 1), TestSounds.Silence(T0), At(65), W, null, null, 1, final: false);

            Assert.Empty(plan.Delete);
            Assert.Empty(plan.Keep);
        }

        [Fact]
        public void Decide_SilentNight_LeavesNothingKept()
        {
            var plan = KeeperRule.Decide(PiecesRange(0, 600), TestSounds.Silence(T0), T0 + TimeSpan.FromHours(11), W, null, null, 1, final: false);

            Assert.Empty(plan.Keep);
            Assert.Equal(600, plan.Delete.Count);
        }

        [Fact]
        public void Decide_OnlyAsksAboutSoundThatHasHappened()
        {
            var sound = TestSounds.At(T0, 30, 31, 32);
            var now = T0 + 2 * Min;

            KeeperRule.Decide(Pieces(0, 1), sound, now, W, null, null, 1, final: false);

            Assert.NotNull(sound.LatestAsked);
            Assert.True(sound.LatestAsked <= now, $"asked about {sound.LatestAsked:HH:mm:ss}, after now {now:HH:mm:ss}");
        }

        [Fact]
        public void Decide_NegativeWindow_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => KeeperRule.Decide(Pieces(0), TestSounds.Silence(T0), T0,
                new KeepWindows(-Min, Min, Min), null, null, 1, final: false));
            Assert.Throws<ArgumentOutOfRangeException>(() => KeeperRule.Decide(Pieces(0), TestSounds.Silence(T0), T0,
                new KeepWindows(Min, Min, -Min), null, null, 1, final: false));
        }

        // ---- the trim planner ------------------------------------------------------------------

        private static TrimInput In(string name, double startSeconds, double seconds = 60) => new(name, At(startSeconds), seconds);

        [Theory]
        [InlineData(115, 54)]     // between keyframes: the one before
        [InlineData(114, 54)]     // exactly on a keyframe: that one
        [InlineData(116.9, 56)]
        [InlineData(61, 0)]       // within the first keyframe interval: no cut at all
        public void Plan_SpanStartsInsideThePiece_CutsAtTheKeyframeAtOrBefore(double spanStart, double expectedIn)
        {
            var trim = ClipTrim.Plan(new[] { In("p1", 60) }, At(spanStart), At(200), 2);

            var part = Assert.Single(trim.Parts);
            Assert.Equal(expectedIn == 0 ? null : expectedIn, part.InSeconds);
            Assert.True(part.KeptStartUtc <= At(spanStart));
            Assert.True(At(spanStart) - part.KeptStartUtc < TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void Plan_SpanStartsBeforeThePiece_TheFirstPieceIsNotCut()
        {
            var trim = ClipTrim.Plan(new[] { In("p1", 60), In("p2", 120) }, At(40), At(200), 2);

            Assert.Null(trim.Parts[0].InSeconds);
            Assert.Null(trim.Parts[0].OutSeconds);
        }

        [Fact]
        public void Plan_OnlyTheFirstAndLastPieceAreCut_AndPiecesOutsideAreLeftOut()
        {
            var pieces = Enumerable.Range(0, 7).Select(m => In($"p{m}", m * 60)).ToList();

            var trim = ClipTrim.Plan(pieces, At(115), At(310), 2);

            Assert.Equal(new[] { "p0", "p6" }, trim.Outside.Select(p => p.Path));
            Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5" }, trim.Parts.Select(p => p.Path));
            Assert.Equal((54.0, (double?)null), (trim.Parts[0].InSeconds!.Value, trim.Parts[0].OutSeconds));
            Assert.Equal(((double?)null, 10.0), (trim.Parts[^1].InSeconds, trim.Parts[^1].OutSeconds!.Value));
            Assert.All(trim.Parts.Skip(1).Take(3), p => Assert.False(p.IsTrimmed));
            Assert.Equal(6 + 60 * 3 + 10, trim.Parts.Sum(p => p.KeptSeconds));
        }

        [Fact]
        public void Plan_OnePieceHoldsTheWholeClip_IsCutAtBothEnds()
        {
            var trim = ClipTrim.Plan(new[] { In("p1", 60) }, At(71), At(95), 2);

            var part = Assert.Single(trim.Parts);
            Assert.Equal(10, part.InSeconds);
            Assert.Equal(35, part.OutSeconds);
            Assert.Equal(25, part.KeptSeconds);
        }

        [Fact]
        public void Plan_ARestartHoleInsideTheClip_NeverCutsTheMiddle()
        {
            // #81: a restart left a 40 s hole and a 2 s leftover inside the clip; all of it is joined whole.
            var pieces = new[] { In("a", 0), In("b", 60, 30), In("left", 90, 2), In("c", 132), In("d", 192) };

            var trim = ClipTrim.Plan(pieces, At(20), At(220), 2);

            Assert.Equal(new[] { "a", "b", "left", "c", "d" }, trim.Parts.Select(p => p.Path));
            Assert.Equal(new[] { false, false, false, false, true }, trim.Parts.Select(p => p.IsTrimmed).Skip(1).Prepend(false));
            Assert.Equal(20, trim.Parts[0].InSeconds);
            Assert.Empty(trim.Outside);
        }

        [Fact]
        public void Plan_BadArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ClipTrim.Plan(new[] { In("p", 0) }, At(0), At(10), 0));
            Assert.Throws<ArgumentException>(() => ClipTrim.Plan(new[] { In("p", 0) }, At(10), At(10), 2));
        }
    }
}
