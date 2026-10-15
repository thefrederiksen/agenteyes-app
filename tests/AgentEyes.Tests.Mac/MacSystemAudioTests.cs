using System.Collections.Generic;
using AgentEyes.Platform.Mac;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class MacSystemAudioTests
    {
        private static List<AvfDevice> Audio(params string[] names)
        {
            var list = new List<AvfDevice>();
            for (int i = 0; i < names.Length; i++) list.Add(new AvfDevice(i, names[i]));
            return list;
        }

        [Fact]
        public void Find_BlackHoleInstalled_IsTheSystemAudioSource()
        {
            var hit = MacSystemAudio.Find(Audio("MacBook Pro Microphone", "BlackHole 2ch"), out string why);

            Assert.NotNull(hit);
            Assert.Equal("BlackHole 2ch", hit!.Value.Name);
            Assert.Equal(1, hit.Value.Index);
            Assert.Equal("", why);
        }

        [Fact]
        public void Find_OnlyARealMicrophone_IsNullAndSaysWhy()
        {
            // The important case: a stock Mac. It must NOT fall through to the microphone, which
            // would record the room instead of the system and look like a success.
            var hit = MacSystemAudio.Find(Audio("MacBook Pro Microphone"), out string why);

            Assert.Null(hit);
            Assert.Contains("loopback", why);
        }

        [Fact]
        public void Find_NoAudioInputsAtAll_SaysThatRatherThanNoLoopback()
        {
            var hit = MacSystemAudio.Find(Audio(), out string why);

            Assert.Null(hit);
            Assert.Contains("no audio input", why);
        }

        [Fact]
        public void Find_MatchIsCaseInsensitive_BecauseDriverBuildsVaryTheirCasing()
        {
            var hit = MacSystemAudio.Find(Audio("blackhole 16ch"), out _);

            Assert.NotNull(hit);
        }

        [Fact]
        public void Find_PrefersTheEarlierKnownDriver_WhenTwoAreInstalled()
        {
            // Order is preference order, so the choice is deterministic rather than listing-order
            // dependent: two installed drivers must not make the recording device vary per boot.
            var hit = MacSystemAudio.Find(Audio("Soundflower (2ch)", "BlackHole 2ch"), out _);

            Assert.Equal("BlackHole 2ch", hit!.Value.Name);
        }
    }
}
