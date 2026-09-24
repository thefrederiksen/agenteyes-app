using System;
using AgentEyes.AlwaysOn;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The three keep settings of always-on (issue #79) - keep before the speech, keep after the speech,
    /// and the silence that closes a clip: their defaults, their ranges, the reason given for a value
    /// outside them, and how a duration is said on the page and in the log.
    /// </summary>
    public class AlwaysOnKeepSettingsTests
    {
        private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        [Fact]
        public void Defaults_AreTheOwnersDecisions_AndInsideTheirRanges()
        {
            Assert.Equal(S(10), AlwaysOnKeepSettings.DefaultKeepBefore);
            Assert.Equal(S(10), AlwaysOnKeepSettings.DefaultKeepAfter);
            Assert.Equal(TimeSpan.FromMinutes(5), AlwaysOnKeepSettings.DefaultSilenceGap);
            Assert.Equal(S(120), AlwaysOnKeepSettings.KeepBeforeMax);
            Assert.Equal(S(30), AlwaysOnKeepSettings.SilenceGapMin);
            Assert.Equal(TimeSpan.FromMinutes(30), AlwaysOnKeepSettings.SilenceGapMax);
            Assert.Null(AlwaysOnKeepSettings.Problem(
                AlwaysOnKeepSettings.DefaultKeepBefore, AlwaysOnKeepSettings.DefaultKeepAfter, AlwaysOnKeepSettings.DefaultSilenceGap));
        }

        [Theory]
        [InlineData(0, 0, 30)]           // every lower end at once
        [InlineData(120, 1800, 1800)]    // every upper end at once; keep-after may equal the gap
        [InlineData(10, 10, 300)]
        [InlineData(30, 300, 300)]
        [InlineData(0, 120, 121)]
        public void Problem_InsideTheRanges_IsNull(double before, double after, double gap)
        {
            Assert.Null(AlwaysOnKeepSettings.Problem(S(before), S(after), S(gap)));
        }

        [Theory]
        [InlineData(-1, 10, 300, "Keep before the speech must be 0 s to 2 min, not -1 s.")]
        [InlineData(121, 10, 300, "Keep before the speech must be 0 s to 2 min, not 2 min 1 s.")]
        [InlineData(10, 10, 29, "Close the clip after silence must be 30 s to 30 min, not 29 s.")]
        [InlineData(10, 10, 1801, "Close the clip after silence must be 30 s to 30 min, not 30 min 1 s.")]
        [InlineData(10, -1, 300, "Keep after the speech must be 0 s to the silence gap (5 min), not -1 s.")]
        [InlineData(10, 301, 300, "Keep after the speech must be 0 s to the silence gap (5 min), not 5 min 1 s.")]
        [InlineData(10, 31, 30, "Keep after the speech must be 0 s to the silence gap (30 s), not 31 s.")]
        public void Problem_OutsideARange_SaysWhichSettingAndWhy(double before, double after, double gap, string expected)
        {
            Assert.Equal(expected, AlwaysOnKeepSettings.Problem(S(before), S(after), S(gap)));
        }

        [Fact]
        public void Problem_SeveralOutOfRange_ReportsTheFirstInPageOrder()
        {
            // Before, then the gap, then after - the order the page lists them.
            Assert.StartsWith("Keep before the speech", AlwaysOnKeepSettings.Problem(S(200), S(500), S(20)));
            Assert.StartsWith("Close the clip after silence", AlwaysOnKeepSettings.Problem(S(10), S(500), S(20)));
        }

        [Fact]
        public void Validate_OutOfRange_ThrowsAUsageExceptionWithTheReason()
        {
            var ex = Assert.Throws<UsageException>(() => AlwaysOnKeepSettings.Validate(S(200), S(10), S(300)));
            Assert.Equal("Keep before the speech must be 0 s to 2 min, not 3 min 20 s.", ex.Message);
        }

        [Fact]
        public void Validate_InRange_DoesNotThrow()
        {
            AlwaysOnKeepSettings.Validate(S(10), S(10), S(300));
            AlwaysOnKeepSettings.Validate(S(0), S(0), S(30));
            AlwaysOnKeepSettings.Validate(S(120), S(1800), S(1800));
        }

        [Theory]
        [InlineData(0, "0 s")]
        [InlineData(10, "10 s")]
        [InlineData(59.5, "59.5 s")]
        [InlineData(60, "1 min")]
        [InlineData(90, "1 min 30 s")]
        [InlineData(150, "2 min 30 s")]
        [InlineData(300, "5 min")]
        [InlineData(1800, "30 min")]
        [InlineData(-3, "-3 s")]
        public void Describe_SaysADurationAsThePageDoes(double seconds, string expected)
        {
            Assert.Equal(expected, AlwaysOnKeepSettings.Describe(S(seconds)));
        }
    }
}
