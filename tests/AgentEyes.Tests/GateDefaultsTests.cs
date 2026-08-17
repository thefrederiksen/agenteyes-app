using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    // Issue #83: the noise gate defaults OFF for a mic-only source (no speaker bleed to tame; it
    // only risks cutting real speech) and ON for mixed/system. Callers honor an explicit override.
    public class GateDefaultsTests
    {
        // AudioSourceKind is internal, so it cannot be an xUnit [Theory] parameter (public method
        // signature) - exercise the enum overload inside [Fact] bodies instead.
        [Fact]
        public void For_mic_kind_is_off()
        {
            Assert.False(GateDefaults.For(AudioSourceKind.Mic));
        }

        [Fact]
        public void For_mixed_and_system_kinds_are_on()
        {
            Assert.True(GateDefaults.For(AudioSourceKind.Mixed));
            Assert.True(GateDefaults.For(AudioSourceKind.System));
        }

        [Theory]
        [InlineData("mic", false)]
        [InlineData("Mic", false)]
        [InlineData("mixed", true)]
        [InlineData("system", true)]
        public void For_source_string_matches_kind(string source, bool expected)
        {
            Assert.Equal(expected, GateDefaults.For(source));
        }
    }
}
