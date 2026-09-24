using System;

namespace AgentEyes.AlwaysOn
{
    /// <summary>Why the microphone counts as silent (issue #77).</summary>
    internal enum SilentMicReason
    {
        /// <summary>Windows reports the capture endpoint muted.</summary>
        Muted,
        /// <summary>Not a single second above the line for <see cref="SilentMicRule.NoSoundAfter"/>.</summary>
        NoSound,
    }

    /// <summary>
    /// The silent-microphone rule (issue #77), pure so it is tested.
    ///
    /// THE MEASURED FLOOR IS NOT USED. The issue first said "floor at or below -90 dBFS for two minutes".
    /// The tester found the owner's microphone floor reads -96.7 dBFS ALL DAY, talking or not: the
    /// Default setup's noise gate zeroes the silence between words, so the floor is the gate's output,
    /// not the room. A rule on the floor would raise a false banner every day. Instead:
    ///
    ///  1. Windows says the microphone is MUTED (IAudioEndpointVolume mute state, read through
    ///     <see cref="AgentEyes.Audio.MicEndpoint"/>) - silent at once, whatever the levels say. This is
    ///     what hid the problem on 2026-09-24: the mic was muted until 09:03.
    ///  2. Not muted, but NO LOUD SECOND AT ALL - no second whose RMS was above the line - since the
    ///     capture began listening or since the last loud second, for <see cref="NoSoundAfter"/>. A gated
    ///     microphone still delivers loud seconds while its owner talks, so this does not fire on the
    ///     gate; it fires on a microphone that is unplugged, pointed at nothing, or silent for a long time.
    ///
    /// A microphone that reads a -96.7 dBFS floor WITH loud seconds present is NOT silent.
    /// </summary>
    internal static class SilentMicRule
    {
        /// <summary>
        /// How long without one loud second before the microphone counts as silent (rule 2). Ten
        /// minutes: the default silence gap (5 min) is how long a conversation's pause may be, so a
        /// shorter wait would flag the end of every conversation; this is twice that. ASSUMPTION - the
        /// issue left "N minutes" open after the tester's finding.
        /// </summary>
        public static readonly TimeSpan NoSoundAfter = TimeSpan.FromMinutes(10);

        /// <summary>The banner and the warning event, as the issue words them.</summary>
        public const string BannerText = "The microphone is sending silence - check it is not muted.";

        /// <summary>The flag on a level line recorded while the rule holds.</summary>
        public const string LevelFlag = "WARNING: The microphone is sending silence - is it muted?";

        /// <summary>The event recorded when the rule stops holding.</summary>
        public const string ClearedText = "The microphone is sending sound again.";

        /// <param name="windowsMuted">The endpoint's mute state, or null when it could not be read.</param>
        /// <param name="lastLoudUtc">The last second whose RMS was above the line, or null for none yet.</param>
        /// <param name="listeningSinceUtc">When the current capture began delivering the microphone's level.</param>
        /// <param name="nowUtc">Now.</param>
        /// <returns>Why the microphone is silent, or null when it is not.</returns>
        public static SilentMicReason? Evaluate(bool? windowsMuted, DateTime? lastLoudUtc, DateTime listeningSinceUtc, DateTime nowUtc)
        {
            if (windowsMuted == true) return SilentMicReason.Muted;
            DateTime quietSince = lastLoudUtc.HasValue && lastLoudUtc.Value > listeningSinceUtc ? lastLoudUtc.Value : listeningSinceUtc;
            return nowUtc - quietSince >= NoSoundAfter ? SilentMicReason.NoSound : null;
        }

        /// <summary>The banner text for a reason: the issue's sentence, then why.</summary>
        public static string Describe(SilentMicReason reason) => reason switch
        {
            SilentMicReason.Muted => BannerText + " Windows reports it muted.",
            SilentMicReason.NoSound => BannerText + $" No sound above the line for {NoSoundAfter.TotalMinutes:0} min.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown reason"),
        };
    }
}
