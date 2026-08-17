using System.Collections.Generic;
using Xunit;
using AgentEyes.Packaging;

namespace AgentEyes.Tests
{
    public class WalkthroughBuilderTests
    {
        private static List<TranscriptSegment> Segments() => new()
        {
            new TranscriptSegment { StartSeconds = 0, EndSeconds = 4, Text = "Hello and welcome" },
            new TranscriptSegment { StartSeconds = 10, EndSeconds = 14, Text = "Now click the button" },
        };

        private static List<WalkthroughShot> Shots() => new()
        {
            new WalkthroughShot { OffsetSeconds = 7, RelativePath = "shots/00m07s.png" },
        };

        [Fact]
        public void Produces_valid_html_document()
        {
            string html = WalkthroughBuilder.Build("My Walkthrough", Shots(), Segments());
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("</html>", html);
            Assert.Contains("<title>My Walkthrough</title>", html);
        }

        [Fact]
        public void Includes_transcript_text_and_image()
        {
            string html = WalkthroughBuilder.Build("t", Shots(), Segments());
            Assert.Contains("Hello and welcome", html);
            Assert.Contains("Now click the button", html);
            Assert.Contains("<img src=\"shots/00m07s.png\"", html);
        }

        [Fact]
        public void Interleaves_by_offset()
        {
            string html = WalkthroughBuilder.Build("t", Shots(), Segments());
            int firstLine = html.IndexOf("Hello and welcome");
            int image = html.IndexOf("00m07s.png");
            int secondLine = html.IndexOf("Now click the button");
            // segment@0  <  shot@7  <  segment@10
            Assert.True(firstLine < image, "first speech should precede the 7s shot");
            Assert.True(image < secondLine, "the 7s shot should precede the 10s speech");
        }

        [Fact]
        public void Html_encodes_text()
        {
            var segs = new List<TranscriptSegment>
            {
                new TranscriptSegment { StartSeconds = 0, EndSeconds = 1, Text = "a < b & c > d" },
            };
            string html = WalkthroughBuilder.Build("t", new List<WalkthroughShot>(), segs);
            Assert.Contains("a &lt; b &amp; c &gt; d", html);
            Assert.DoesNotContain("a < b & c > d", html);
        }

        [Fact]
        public void Empty_inputs_render_placeholder()
        {
            string html = WalkthroughBuilder.Build("t", new List<WalkthroughShot>(), new List<TranscriptSegment>());
            Assert.Contains("No screenshots or transcript", html);
        }
    }
}
