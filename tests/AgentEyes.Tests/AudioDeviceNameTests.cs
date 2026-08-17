using Xunit;
using AgentEyes.Audio;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #9: WAVEINCAPS truncates device names to 31 chars; AudioCapture.FullName
    /// recovers the full WASAPI FriendlyName by prefix match.
    /// </summary>
    public class AudioDeviceNameTests
    {
        // Exactly 31 chars - what WaveIn reports for a longer real name.
        private const string Truncated = "Microphone (FDUCE SL40 Audio De";
        private const string Full = "Microphone (FDUCE SL40 Audio Device)";

        [Fact]
        public void Truncated_name_recovers_the_single_matching_friendly_name()
        {
            var friendly = new[] { "Microphone (HD Webcam eMeet C960)", Full };
            Assert.Equal(Full, AudioCapture.FullName(Truncated, friendly));
        }

        [Fact]
        public void Short_name_is_already_complete_and_kept_verbatim()
        {
            // Under 31 chars = never truncated; no lookup, even when a longer
            // friendly name happens to share the prefix.
            var friendly = new[] { "Microphone (Yeti) Pro Edition" };
            Assert.Equal("Microphone (Yeti)", AudioCapture.FullName("Microphone (Yeti)", friendly));
        }

        [Fact]
        public void Ambiguous_prefix_keeps_the_wavein_name()
        {
            // Two identical USB mics: both friendly names share the truncated prefix.
            var friendly = new[] { Full, Full };
            Assert.Equal(Truncated, AudioCapture.FullName(Truncated, friendly));
        }

        [Fact]
        public void No_matching_friendly_name_keeps_the_wavein_name()
        {
            var friendly = new[] { "Microphone (HD Webcam eMeet C960)" };
            Assert.Equal(Truncated, AudioCapture.FullName(Truncated, friendly));
        }

        [Fact]
        public void Prefix_match_is_case_insensitive()
        {
            var friendly = new[] { Full.ToUpperInvariant() };
            Assert.Equal(Full.ToUpperInvariant(), AudioCapture.FullName(Truncated, friendly));
        }
    }
}
