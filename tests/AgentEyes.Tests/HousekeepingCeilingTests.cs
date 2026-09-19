using System.Collections.Generic;
using System.Linq;
using AgentEyes.Housekeeping;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The footprint ceiling (issue #55): an age rule alone does not protect a disk from one very long
    /// capture, so past a total the owner set, the oldest unpinned recordings are aged forward to the
    /// preserved-original boundary.
    ///
    /// Every test here is about a REFUSAL as much as an action, because the ceiling's whole risk is
    /// that it reaches further than the owner asked.
    /// </summary>
    public class HousekeepingCeilingTests
    {
        private const int TierDays = 30;

        private static HousekeepingCandidate One(string name, long gb, int age, bool pinned = false) => new()
        {
            Recording = name,
            Bytes = gb * 1024 * 1024 * 1024,
            AgeDays = age,
            Pinned = pinned,
        };

        private static long Gb(long n) => n * 1024 * 1024 * 1024;

        [Fact]
        public void Advance_NoCeilingSet_AdvancesNothing()
        {
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("a", 40, 1), One("b", 40, 2) }, ceilingBytes: 0, TierDays);

            Assert.Empty(advanced);
        }

        [Fact]
        public void Advance_TotalWithinTheCeiling_AdvancesNothing()
        {
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("a", 5, 1), One("b", 5, 2) }, Gb(20), TierDays);

            Assert.Empty(advanced);
        }

        [Fact]
        public void Advance_OverTheCeiling_TakesTheOldestFirst()
        {
            // 30 GiB against a 20 GiB ceiling: the oldest 10 GiB has to go.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("newest", 10, 1), One("middle", 10, 5), One("oldest", 10, 9) },
                Gb(20), TierDays);

            Assert.Equal(new[] { "oldest" }, advanced.ToArray());
        }

        [Fact]
        public void Advance_StopsAsSoonAsItIsUnderTheCeiling()
        {
            // It must not keep going once the total fits. 30 GiB against 25 GiB needs ONE recording.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("a", 10, 1), One("b", 10, 5), One("c", 10, 9) },
                Gb(25), TierDays);

            Assert.Single(advanced);
        }

        [Fact]
        public void Advance_NeverAdvancesAPinnedRecording()
        {
            // The pin outranks the ceiling: the pin is "not this one", the ceiling is "not this much".
            // Here the oldest and largest is pinned, so the next one down is taken instead.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("pinned-oldest", 20, 40, pinned: true), One("next", 20, 9) },
                Gb(20), TierDays);

            Assert.DoesNotContain("pinned-oldest", advanced);
            Assert.Contains("next", advanced);
        }

        [Fact]
        public void Advance_EverythingPinned_AdvancesNothingEvenWayOverTheCeiling()
        {
            // An empty answer is the honest one. Being over the ceiling is not a reason to delete
            // something the owner said to keep.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("a", 50, 90, pinned: true), One("b", 50, 80, pinned: true) },
                Gb(1), TierDays);

            Assert.Empty(advanced);
        }

        [Fact]
        public void Advance_ARecordingAlreadyPastTheBoundary_IsNotCountedAsAnAdvance()
        {
            // It is already going to lose its originals on age alone. Its bytes still come off the
            // total, so the ceiling does not then reach a younger recording it did not need to.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("ancient", 20, 100), One("young", 10, 2) },
                Gb(15), TierDays);

            Assert.Empty(advanced);
        }

        [Fact]
        public void Advance_OnlyEverReachesTheTierBoundary_NeverInventsANewTier()
        {
            // The contract: the returned set is read as "treat these as exactly TierDays old". This
            // test is the statement that the ceiling returns NAMES and never an age of its own, which
            // is what keeps it from being a second, undocumented retention policy.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("a", 100, 10) }, Gb(1), TierDays);

            Assert.Equal(new[] { "a" }, advanced.ToArray());
        }

        [Fact]
        public void Advance_ARecordingInsideTheProtectedWindow_IsNeverAdvancedWhateverThePressure()
        {
            // The review of pull request #57 proved this live: without a floor, a recording made the
            // SAME DAY lost both preserved originals under a 1 GiB ceiling. The epic sanctions
            // advancing OLD recordings early; it does not sanction erasing a hot recording's raw
            // backup, so young bytes stay in the total like pinned ones.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("same-day", 40, 0), One("six-days", 40, 6) }, Gb(1), TierDays);

            Assert.Empty(advanced);
        }

        [Theory]
        [InlineData(6, false)]
        [InlineData(7, true)]
        public void Advance_TheProtectedWindowBoundary_HoldsOnBothSides(int ageDays, bool expected)
        {
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("a", 40, ageDays) }, Gb(1), TierDays);

            Assert.Equal(expected, advanced.Contains("a"));
        }

        [Fact]
        public void Advance_OnlyYoungRecordingsOverTheCeiling_AdvancesNothingRatherThanReachingDown()
        {
            // Over the ceiling with nothing old enough to take: the disk stays over the ceiling and
            // that is the honest answer, because the alternative is deleting a young recording's
            // preserved originals, which no tier would ever touch on its own.
            var advanced = HousekeepingCeiling.Advance(
                new[] { One("day-one", 30, 1), One("day-two", 30, 2) }, Gb(1), TierDays);

            Assert.Empty(advanced);
        }

        [Fact]
        public void Advance_NoRecordings_AdvancesNothing()
        {
            Assert.Empty(HousekeepingCeiling.Advance(new List<HousekeepingCandidate>(), Gb(1), TierDays));
        }
    }
}
