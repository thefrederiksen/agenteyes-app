using System.Collections.Generic;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class DeviceResolverTests
    {
        private static List<(int, string)> Devices() => new()
        {
            (0, "Microphone (Realtek High Definition Audio)"),
            (1, "Microphone (Yeti Stereo Microphone)"),
            (2, "Line In (HD Webcam)"),
        };

        [Fact]
        public void Resolves_unique_substring_to_number()
        {
            Assert.Equal(1, DeviceResolver.Resolve(Devices(), "Yeti"));
        }

        [Fact]
        public void Match_is_case_insensitive()
        {
            Assert.Equal(1, DeviceResolver.Resolve(Devices(), "yeti"));
        }

        [Fact]
        public void No_match_throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(Devices(), "Nonexistent"));
        }

        [Fact]
        public void Ambiguous_match_throws()
        {
            // "Microphone" matches devices 0 and 1.
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(Devices(), "Microphone"));
        }

        [Fact]
        public void Empty_device_list_throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(new List<(int, string)>(), "x"));
        }

        [Fact]
        public void Empty_fragment_throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(Devices(), "  "));
        }

        [Fact]
        public void ResolveName_returns_matching_name()
        {
            var names = new List<string> { "Microphone (Yeti Stereo Microphone)", "Line In" };
            Assert.Equal("Microphone (Yeti Stereo Microphone)", DeviceResolver.ResolveName(names, "Yeti"));
        }

        [Fact]
        public void ResolveName_ambiguous_throws()
        {
            var names = new List<string> { "Mic A", "Mic B" };
            Assert.Throws<UsageException>(() => DeviceResolver.ResolveName(names, "Mic"));
        }
    }
}
