using System;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class TimecodesTests
    {
        [Theory]
        [InlineData(0, "00m00s.png")]
        [InlineData(3, "00m03s.png")]
        [InlineData(65, "01m05s.png")]
        [InlineData(600, "10m00s.png")]
        public void FileName_formats_offset(int seconds, string expected)
        {
            Assert.Equal(expected, Timecodes.FileName(TimeSpan.FromSeconds(seconds)));
        }

        [Theory]
        [InlineData(0, "00m00s")]
        [InlineData(72, "01m12s")]
        public void Label_formats_offset(int seconds, string expected)
        {
            Assert.Equal(expected, Timecodes.Label(TimeSpan.FromSeconds(seconds)));
        }

        [Theory]
        [InlineData(0, "00:00")]
        [InlineData(9, "00:09")]
        [InlineData(125, "02:05")]
        public void Clock_formats_offset(int seconds, string expected)
        {
            Assert.Equal(expected, Timecodes.Clock(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void FileNames_sort_in_chronological_order()
        {
            string a = Timecodes.FileName(TimeSpan.FromSeconds(9));
            string b = Timecodes.FileName(TimeSpan.FromSeconds(12));
            Assert.True(string.CompareOrdinal(a, b) < 0);
        }
    }
}
