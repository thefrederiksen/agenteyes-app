using System;
using AgentEyes.Housekeeping;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The page half of the expire tier (issue #59): when a recording's video goes, every frame
    /// image the page showed would become a dead link. These tests hold the rewrite to its law -
    /// frame images become honest notes, everything that is not a frame image survives untouched.
    /// </summary>
    public class WalkthroughRetirementTests
    {
        private static readonly DateTime When = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Rewrite_ReplacesAFrameFileWithAnHonestNote_KeepingTheCaption()
        {
            const string html =
                "<figure class=\"shot\"><img src=\"shots/frame_001.png\" alt=\"screenshot at 00:00:05\"/>"
                + "<figcaption>00:00:05</figcaption></figure>";

            string rewritten = WalkthroughRetirement.Rewrite(html, When);

            Assert.DoesNotContain("<img", rewritten);
            Assert.Contains("the recording expired on 2026-09-19", rewritten);
            Assert.Contains("<figcaption>00:00:05</figcaption>", rewritten);
        }

        [Fact]
        public void Rewrite_ReplacesAConvertedFrameFileToo()
        {
            const string html = "<img src=\"shots/frame_002.jpg\" alt=\"screenshot\"/>";

            string rewritten = WalkthroughRetirement.Rewrite(html, When);

            Assert.DoesNotContain("<img", rewritten);
            Assert.Contains("expired on 2026-09-19", rewritten);
        }

        [Fact]
        public void Rewrite_ReplacesAnOnDemandFrameReference()
        {
            // The form recordings packaged after issue #59 use: the local control endpoint.
            const string html =
                "<figure class=\"shot\"><img src=\"http://127.0.0.1:7882/recordings/rec/frame/12.5\"/>"
                + "<figcaption>00:00:12</figcaption></figure>";

            string rewritten = WalkthroughRetirement.Rewrite(html, When);

            Assert.DoesNotContain("<img", rewritten);
            Assert.Contains("expired on 2026-09-19", rewritten);
        }

        [Fact]
        public void Rewrite_LeavesTheOwnersOwnShotsAlone()
        {
            const string html = "<img src=\"shots/region_640x480.png\" alt=\"a shot I took\"/>";

            string rewritten = WalkthroughRetirement.Rewrite(html, When);

            Assert.Equal(html, rewritten);
        }

        [Fact]
        public void Rewrite_LeavesUnrelatedImagesAlone()
        {
            const string html = "<img src=\"https://example.com/picture.png\" alt=\"not ours\"/>";

            string rewritten = WalkthroughRetirement.Rewrite(html, When);

            Assert.Equal(html, rewritten);
        }

        [Fact]
        public void Rewrite_APagewithNoFrameImages_IsReturnedByteForByte()
        {
            const string html =
                "<html><body><h1>A</h1><p class=\"line\">[00:00:01] said something</p></body></html>";

            Assert.Equal(html, WalkthroughRetirement.Rewrite(html, When));
        }

        [Fact]
        public void Rewrite_KeepsTheTranscriptAroundTheNotes()
        {
            const string html =
                "<p class=\"line\"><span class=\"ts\">00:00:01</span> first thing</p>"
                + "<img src=\"shots/frame_001.png\" alt=\"screenshot\"/>"
                + "<p class=\"line\"><span class=\"ts\">00:00:20</span> last thing</p>";

            string rewritten = WalkthroughRetirement.Rewrite(html, When);

            Assert.Contains("first thing", rewritten);
            Assert.Contains("last thing", rewritten);
            Assert.DoesNotContain("frame_001", rewritten);
        }
    }
}
