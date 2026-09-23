using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentEyes.Housekeeping
{
    /// <summary>One recording as the ceiling sees it: how big, how old, and whether it is pinned.</summary>
    internal sealed class HousekeepingCandidate
    {
        public string Recording { get; init; } = "";
        public long Bytes { get; init; }
        public int AgeDays { get; init; }
        public bool Pinned { get; init; }
    }

    /// <summary>
    /// The footprint ceiling (issue #55): past a total the owner set, the OLDEST unpinned recordings are
    /// treated as though they had already reached the preserved-original age, so their originals go now
    /// rather than on their own schedule.
    ///
    /// WHY AN AGE RULE IS NOT ENOUGH ON ITS OWN. Age assumes recordings arrive at a steady rate and are
    /// a steady size. They do not: on the machine this was measured on, one 231-minute capture accounted
    /// for 9.7 GiB by itself, and three days of recordings accounted for more than the previous three
    /// weeks. A pure age rule lets a single long session push the disk past anything the owner would have
    /// accepted, and then holds that state for thirty days by design.
    ///
    /// It only ever advances a recording to the Tier 3 boundary. It does not invent a new tier, it does
    /// not touch anything the tiers would not eventually touch anyway, and it never advances a PINNED
    /// recording - the pin outranks the ceiling, because the pin is the owner saying "not this one" and
    /// the ceiling is the owner saying "not this much". A recording inside the protected window
    /// (<see cref="MinProtectedDays"/>) is equally untouchable: the epic sanctions advancing OLD
    /// recordings early, never erasing a hot recording's raw backup.
    ///
    /// Pure, so the policy can be proven without a disk.
    /// </summary>
    internal static class HousekeepingCeiling
    {
        /// <summary>
        /// Days below which the ceiling never advances a recording, whatever the pressure. A young
        /// recording's bytes stay in the running total exactly like a pinned one's, so the pressure
        /// moves to genuinely old recordings - and when there are none, the honest answer is an
        /// empty set, not a same-day capture's preserved originals.
        /// </summary>
        public const int MinProtectedDays = 7;
        /// <summary>
        /// Which recordings should be aged forward to <paramref name="tierDays"/>.
        ///
        /// Returns an empty set when there is no ceiling, when the total is within it, or when the only
        /// recordings over it are pinned - and an empty set is the honest answer in all three cases,
        /// because none of them is a reason to delete something.
        /// </summary>
        public static ISet<string> Advance(
            IReadOnlyList<HousekeepingCandidate> candidates, long ceilingBytes, int tierDays)
        {
            var advanced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ceilingBytes <= 0) return advanced;

            long total = candidates.Sum(c => c.Bytes);
            if (total <= ceilingBytes) return advanced;

            // Oldest first. A recording already past the tier boundary is not "advanced" - it is already
            // there - so it is not counted as one of this pass's advances, but its bytes DO come off the
            // total, because the tiers are about to take them regardless.
            foreach (var c in candidates.OrderByDescending(c => c.AgeDays).ThenByDescending(c => c.Bytes))
            {
                if (total <= ceilingBytes) break;
                if (c.Pinned) continue;

                // The protected window outranks the pressure, the same way a pin does: a recording
                // this young is never aged forward, and its bytes stay in the total.
                if (c.AgeDays < MinProtectedDays) continue;

                if (c.AgeDays < tierDays) advanced.Add(c.Recording);
                total -= c.Bytes;
            }

            return advanced;
        }

        /// <summary>
        /// The ceiling's rule applied to files that can simply be DELETED rather than aged forward
        /// (issue #66, always-on clips): while the total - including <paramref name="fixedBytes"/>, the
        /// bytes that count but can never be taken - is over <paramref name="ceilingBytes"/>, take the
        /// OLDEST unpinned candidate. Same order, same pin rule, as <see cref="Advance"/>. There is no
        /// protected window here: an always-on clip is the product of a rule the owner set, and the
        /// cap is the owner saying how much of it to hold.
        ///
        /// Candidates must come oldest first (the caller knows the exact times; AgeDays is too coarse
        /// for clips minutes apart). Returns the ones to delete, in that order.
        /// </summary>
        public static IReadOnlyList<HousekeepingCandidate> EvictOldest(
            IReadOnlyList<HousekeepingCandidate> oldestFirst, long ceilingBytes, long fixedBytes)
        {
            var evict = new List<HousekeepingCandidate>();
            if (ceilingBytes <= 0) return evict;

            long total = fixedBytes + oldestFirst.Sum(c => c.Bytes);
            foreach (var c in oldestFirst)
            {
                if (total <= ceilingBytes) break;
                if (c.Pinned) continue;
                evict.Add(c);
                total -= c.Bytes;
            }
            return evict;
        }
    }
}
