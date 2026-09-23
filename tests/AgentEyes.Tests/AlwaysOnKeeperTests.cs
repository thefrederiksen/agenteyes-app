using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.AlwaysOn;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The always-on KEEP RULE (issue #66), proven without ffmpeg or a clock: which pieces are kept,
    /// which are deleted, which wait, and when a clip closes.
    /// </summary>
    public class AlwaysOnKeeperTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

        /// <summary>Minute-long pieces starting at the given minute offsets from T0.</summary>
        private static List<Piece> Pieces(params int[] minutes) =>
            minutes.Select(m => new Piece($"p{m}", T0 + TimeSpan.FromMinutes(m), T0 + TimeSpan.FromMinutes(m + 1), 1000)).ToList();

        /// <summary>A sound log holding sound at the given second offsets from T0.</summary>
        private static Func<DateTime, DateTime, bool> SoundAt(params int[] seconds) =>
            (a, b) => seconds.Any(s => T0.AddSeconds(s) >= a && T0.AddSeconds(s) <= b);

        private static readonly Func<DateTime, DateTime, bool> Silence = (_, _) => false;

        [Fact]
        public void Decide_SilentPieceInsideBeforeWindow_Waits()
        {
            // Piece 0 ends at 1 min; with a 5 min before window it could still be the lead-in to sound
            // until minute 6.
            var plan = KeeperRule.Decide(Pieces(0), Silence, T0 + 3 * Min, Five, Five, null, null, 1, final: false);

            Assert.Empty(plan.Keep);
            Assert.Empty(plan.Delete);
            Assert.Empty(plan.Close);
        }

        [Fact]
        public void Decide_SilentPiecePastBeforeWindow_IsDeleted()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1), Silence, T0 + 6 * Min, Five, Five, null, null, 1, final: false);

            Assert.Equal(new[] { "p0" }, plan.Delete.Select(p => p.Path));
            Assert.Empty(plan.Keep);
        }

        [Fact]
        public void Decide_SoundInsidePiece_KeepsItInANewClip()
        {
            var plan = KeeperRule.Decide(Pieces(0), SoundAt(30), T0 + 2 * Min, Five, Five, null, null, 1, final: false);

            Assert.Single(plan.Keep);
            Assert.Equal(1, plan.Keep[0].Clip);
            Assert.Equal(1, plan.OpenClip);
            Assert.Equal(2, plan.NextClip);
            Assert.Empty(plan.Close);
        }

        [Fact]
        public void Decide_SoundWithinBeforeWindowAfterPiece_KeepsPieceAsLeadIn()
        {
            // Sound at minute 4:30; piece 0 ends at minute 1, and 4:30 is within 5 min of that.
            var plan = KeeperRule.Decide(Pieces(0), SoundAt(270), T0 + 5 * Min, Five, Five, null, null, 1, final: false);

            Assert.Single(plan.Keep);
        }

        [Fact]
        public void Decide_SoundOutsideBeforeWindow_DeletesPiece()
        {
            // Sound at minute 7 is more than 5 min after piece 0 ends (minute 1).
            var plan = KeeperRule.Decide(Pieces(0), SoundAt(420), T0 + 8 * Min, Five, Five, null, null, 1, final: false);

            Assert.Single(plan.Delete);
            Assert.Empty(plan.Keep);
        }

        [Fact]
        public void Decide_PiecesWithinAfterWindowOfSound_AreKeptAsTail()
        {
            // Sound at 0:30. Pieces 1..5 start within 5 min of it (tail); piece 6 starts at 6:00,
            // more than 5 min after the sound, and nothing follows, so it is deleted once its own
            // before window has passed.
            var plan = KeeperRule.Decide(Pieces(0, 1, 2, 3, 4, 5, 6), SoundAt(30), T0 + 13 * Min, Five, Five, null, null, 1, final: false);

            Assert.Equal(new[] { "p0", "p1", "p2", "p3", "p4", "p5" }, plan.Keep.Select(k => k.Piece.Path));
            Assert.All(plan.Keep, k => Assert.Equal(1, k.Clip));
            Assert.Equal(new[] { "p6" }, plan.Delete.Select(p => p.Path));
            Assert.Equal(new[] { 1 }, plan.Close);
            Assert.Null(plan.OpenClip);
        }

        [Fact]
        public void Decide_ClipCloses_WhenPieceAfterItWaitsAfterWindowOfSilence()
        {
            // Sound at 0:30; piece 6 (6:00-7:00) has heard no sound yet but can still become a lead-in
            // until 12:00. It waits - and the clip before it is still finished: 5 min of silence passed.
            var plan = KeeperRule.Decide(Pieces(0, 1, 2, 3, 4, 5, 6), SoundAt(30), T0 + 8 * Min, Five, Five, null, null, 1, final: false);

            Assert.Equal(6, plan.Keep.Count);
            Assert.Empty(plan.Delete);
            Assert.Equal(new[] { 1 }, plan.Close);
            Assert.Null(plan.OpenClip);
        }

        [Fact]
        public void Decide_OpenClipWithNoFollowingPiece_StaysOpen()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1), SoundAt(90), T0 + 2 * Min + TimeSpan.FromSeconds(5), Five, Five, null, null, 1, final: false);

            Assert.Equal(2, plan.Keep.Count);
            Assert.Empty(plan.Close);
            Assert.Equal(1, plan.OpenClip);
            Assert.Equal(T0 + 2 * Min, plan.OpenClipEndUtc);
        }

        [Fact]
        public void Decide_ContiguousPieceOnLaterPass_JoinsTheOpenClip()
        {
            var plan = KeeperRule.Decide(Pieces(2), SoundAt(90), T0 + 3 * Min, Five, Five, 7, T0 + 2 * Min, 8, final: false);

            Assert.Equal(7, plan.Keep.Single().Clip);
            Assert.Equal(8, plan.NextClip);
            Assert.Empty(plan.Close);
        }

        [Fact]
        public void Decide_GapBeforePiece_StartsNewClipAndClosesOld()
        {
            // The open clip ended at minute 2; this piece starts at minute 4 (a pause, a restart).
            var plan = KeeperRule.Decide(Pieces(4), SoundAt(250), T0 + 5 * Min, Five, Five, 7, T0 + 2 * Min, 8, final: false);

            Assert.Equal(new[] { 7 }, plan.Close);
            Assert.Equal(8, plan.Keep.Single().Clip);
            Assert.Equal(8, plan.OpenClip);
        }

        [Fact]
        public void Decide_StopsAtFirstWaitingPiece()
        {
            // Piece 0 waits (now is inside its before window, no sound); piece 1 must not be decided
            // either, even though nothing would have kept it.
            var plan = KeeperRule.Decide(Pieces(0, 1), Silence, T0 + 4 * Min, Five, Five, null, null, 1, final: false);

            Assert.Empty(plan.Delete);
            Assert.Empty(plan.Keep);
        }

        [Fact]
        public void Decide_FinalPass_DecidesEverythingAndClosesTheClip()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1, 2), SoundAt(90), T0 + 3 * Min, Five, Five, null, null, 1, final: true);

            Assert.Equal(3, plan.Keep.Count);
            Assert.Equal(new[] { 1 }, plan.Close);
            Assert.Null(plan.OpenClip);
        }

        [Fact]
        public void Decide_FinalPassSilent_DeletesWaitingPieces()
        {
            var plan = KeeperRule.Decide(Pieces(0, 1), Silence, T0 + 2 * Min, Five, Five, null, null, 1, final: true);

            Assert.Equal(2, plan.Delete.Count);
            Assert.Empty(plan.Close);
        }

        [Fact]
        public void Decide_SilentNight_LeavesNothingKept()
        {
            var night = Pieces(Enumerable.Range(0, 600).ToArray());
            var plan = KeeperRule.Decide(night, Silence, T0 + TimeSpan.FromHours(11), Five, Five, null, null, 1, final: false);

            Assert.Empty(plan.Keep);
            Assert.Equal(600, plan.Delete.Count);
        }

        [Fact]
        public void Decide_OnlyAskAboutSoundThatHasHappened()
        {
            DateTime? latestAsked = null;
            Func<DateTime, DateTime, bool> spy = (a, b) => { latestAsked = b; return false; };
            var now = T0 + 2 * Min;

            KeeperRule.Decide(Pieces(0), spy, now, Five, Five, null, null, 1, final: false);

            Assert.Equal(now, latestAsked);
        }

        [Fact]
        public void Decide_NegativeWindow_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                KeeperRule.Decide(Pieces(0), Silence, T0, -Min, Five, null, null, 1, final: false));
        }
    }
}
