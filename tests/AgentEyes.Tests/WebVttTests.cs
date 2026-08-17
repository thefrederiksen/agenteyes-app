using System.Collections.Generic;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// WebVTT read/write (issue #98): a segments -> VTT -> segments round-trip preserves start/end/
    /// text for a multi-cue fixture, the writer emits the WEBVTT header + exact HH:MM:SS.mmm cue
    /// timing, and cue-timestamp formatting/parsing is exact.
    /// </summary>
    public class WebVttTests
    {
        private static List<TranscriptSegment> MultiCue() => new()
        {
            new TranscriptSegment { StartSeconds = 0.0,   EndSeconds = 1.5,    Text = "hello there" },
            new TranscriptSegment { StartSeconds = 1.5,   EndSeconds = 3.0,    Text = "second cue" },
            new TranscriptSegment { StartSeconds = 62.25, EndSeconds = 3725.0, Text = "past a minute and an hour" },
        };

        [Fact]
        public void RoundTrip_MultiCue_PreservesStartEndText()
        {
            var original = MultiCue();

            string vtt = WebVtt.Write(original);
            var parsed = WebVtt.Read(vtt);

            Assert.Equal(original.Count, parsed.Count);
            for (int i = 0; i < original.Count; i++)
            {
                Assert.Equal(original[i].StartSeconds, parsed[i].StartSeconds, 3);
                Assert.Equal(original[i].EndSeconds, parsed[i].EndSeconds, 3);
                Assert.Equal(original[i].Text, parsed[i].Text);
            }
        }

        [Fact]
        public void Write_BeginsWithWebVttHeader()
        {
            string vtt = WebVtt.Write(MultiCue());
            Assert.StartsWith("WEBVTT\n", vtt);
        }

        [Fact]
        public void Write_EmitsExactCueTimingLines_ForKnownFixture()
        {
            var segments = new List<TranscriptSegment>
            {
                new TranscriptSegment { StartSeconds = 0.0, EndSeconds = 1.5, Text = "hello" },
                new TranscriptSegment { StartSeconds = 62.25, EndSeconds = 3725.0, Text = "later" },
            };

            string vtt = WebVtt.Write(segments);

            // Exact WebVTT document for this fixture (WEBVTT header + HH:MM:SS.mmm cue timing).
            string expected =
                "WEBVTT\n\n" +
                "00:00:00.000 --> 00:00:01.500\n" +
                "hello\n\n" +
                "00:01:02.250 --> 01:02:05.000\n" +
                "later\n\n";
            Assert.Equal(expected, vtt);
        }

        [Theory]
        [InlineData(0.0, "00:00:00.000")]
        [InlineData(1.5, "00:00:01.500")]
        [InlineData(62.25, "00:01:02.250")]
        [InlineData(3725.0, "01:02:05.000")]
        public void FormatTimestamp_ProducesHhMmSsMmm(double seconds, string expected)
        {
            Assert.Equal(expected, WebVtt.FormatTimestamp(seconds));
        }

        [Theory]
        [InlineData("00:00:00.000", 0.0)]
        [InlineData("00:00:01.500", 1.5)]
        [InlineData("00:01:02.250", 62.25)]
        [InlineData("01:02:05.000", 3725.0)]
        [InlineData("01:02.250", 62.25)]      // MM:SS.mmm form (hours omitted)
        public void ParseTimestamp_AcceptsVttForms(string token, double expected)
        {
            Assert.Equal(expected, WebVtt.ParseTimestamp(token), 3);
        }

        [Fact]
        public void Read_IgnoresCueSettingsAfterEndTimestamp()
        {
            string vtt = "WEBVTT\n\n00:00:00.000 --> 00:00:02.000 align:start position:10%\ntext here\n\n";
            var parsed = WebVtt.Read(vtt);
            var cue = Assert.Single(parsed);
            Assert.Equal(0.0, cue.StartSeconds, 3);
            Assert.Equal(2.0, cue.EndSeconds, 3);
            Assert.Equal("text here", cue.Text);
        }
    }
}
