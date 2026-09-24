using System;
using AgentEyes.Audio;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #77, review finding: MicEndpoint used to take the FIRST active capture endpoint whose friendly
    /// name CONTAINED the configured fragment - two devices sharing a word ("Microphone") made a silent
    /// guess. <see cref="MicEndpoint.Select"/> is the selection rule, pure: exact name first, else the one
    /// name the fragment is a prefix of (the WaveIn 31-character prefix), else refuse and name the candidates.
    /// </summary>
    public class MicEndpointTests
    {
        private static readonly string[] Names =
        {
            "Microphone (HD Webcam eMeet C960)",
            "Microphone (FDUCE SL40 Audio Device)",
            "Headset Microphone (USB Audio)",
        };

        [Fact]
        public void Select_ExactName_IsChosenEvenWhenAnotherNameStartsWithIt()
        {
            var names = new[] { "Microphone (Yeti) Pro Edition", "Microphone (Yeti)" };

            Assert.Equal(1, MicEndpoint.Select("Microphone (Yeti)", names));
        }

        [Fact]
        public void Select_ExactName_IgnoresCaseAndSurroundingSpaces()
        {
            Assert.Equal(2, MicEndpoint.Select("  headset microphone (usb audio) ", Names));
        }

        [Fact]
        public void Select_SinglePrefixMatch_TheWaveInTruncatedName_IsChosen()
        {
            // Exactly 31 chars - what WaveIn reports for the longer real name.
            Assert.Equal(1, MicEndpoint.Select("Microphone (FDUCE SL40 Audio De", Names));
        }

        [Fact]
        public void Select_ContainsButNotPrefix_IsNotAMatch()
        {
            // "USB Audio" is INSIDE "Headset Microphone (USB Audio)"; the old Contains rule took it, the prefix rule does not.
            var ex = Assert.Throws<UsageException>(() => MicEndpoint.Select("USB Audio", Names));

            Assert.Contains("no active microphone matches \"USB Audio\"", ex.Message);
            Assert.Contains("Headset Microphone (USB Audio)", ex.Message);                // the active ones are listed
        }

        [Fact]
        public void Select_TwoPrefixMatches_RefusesAndNamesBothCandidates()
        {
            var ex = Assert.Throws<UsageException>(() => MicEndpoint.Select("Microphone", Names));

            Assert.Contains("\"Microphone\" could mean 2 active microphones", ex.Message);
            Assert.Contains("\"Microphone (HD Webcam eMeet C960)\"", ex.Message);
            Assert.Contains("\"Microphone (FDUCE SL40 Audio Device)\"", ex.Message);
            Assert.DoesNotContain("Headset Microphone", ex.Message);                     // not a candidate: it does not START with it
            Assert.Contains("full device name", ex.Message);                             // the exact fix
        }

        [Fact]
        public void Select_TwoDevicesWithTheSameExactName_Refuses()
        {
            var names = new[] { "Microphone (USB Audio)", "Microphone (USB Audio)" };

            var ex = Assert.Throws<UsageException>(() => MicEndpoint.Select("Microphone (USB Audio)", names));

            Assert.Contains("2 active microphones are both named \"Microphone (USB Audio)\"", ex.Message);
        }

        [Fact]
        public void Select_NoActiveEndpoints_SaysSo()
        {
            var ex = Assert.Throws<UsageException>(() => MicEndpoint.Select("Headset Microphone", Array.Empty<string>()));

            Assert.Contains("no active microphone matches \"Headset Microphone\". Active: (none).", ex.Message);
        }

        [Fact]
        public void Select_EmptyFragment_IsAnArgumentError()
        {
            Assert.Throws<ArgumentException>(() => MicEndpoint.Select("   ", Names));
        }
    }
}
