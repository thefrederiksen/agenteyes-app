using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEyes.Housekeeping
{
    /// <summary>
    /// Rewrite a walkthrough page for a recording whose video has expired (issue #59).
    ///
    /// The expire tier deletes the composed video and the extracted frames, so every image the page
    /// shows would become a dead link - the exact artifact-destroying state the frame conversion's
    /// ordering law exists to prevent. This replaces each image that pointed at an extracted frame
    /// (a file under shots/, or a frame served on demand by the local control endpoint) with a note
    /// that says what happened. The caption beside it - the timecode - survives, so the reader still
    /// sees WHEN each moment was.
    ///
    /// Pure: html in, html out, no file system. The caller writes the result.
    /// </summary>
    internal static class WalkthroughRetirement
    {
        // The walkthrough's own image element: <img src="..." .../>. Builder-emitted, so the shape is
        // known; matching the src afterwards is what keeps this honest rather than assumed.
        private static readonly Regex ImgTag = new(
            @"<img\b[^>]*\bsrc=""(?<src>[^""]*)""[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True for a source that points at a frame extracted from the now-deleted video.</summary>
        internal static bool IsFrameReference(string src)
        {
            if (string.IsNullOrEmpty(src)) return false;
            string s = src.Trim();

            // A frame file under shots/ - the form every recording packaged before this change uses.
            if (s.StartsWith(HousekeepingPlan.FramesDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return HousekeepingPlan.IsExtractedFrameFile(s);
            }

            // A frame served on demand by the local control endpoint - the form recordings packaged
            // after it use: http://127.0.0.1:PORT/recordings/{id}/frame/{offset}.
            return s.Contains("/recordings/", StringComparison.OrdinalIgnoreCase)
                && s.Contains("/frame/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Replace every frame image with an honest note. A page with no frame images is returned
        /// unchanged, byte for byte.
        /// </summary>
        public static string Rewrite(string html, DateTime whenUtc)
        {
            string date = whenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return ImgTag.Replace(html, m =>
            {
                if (!IsFrameReference(m.Groups["src"].Value)) return m.Value;
                return "<span class=\"retired-frame\" style=\"display:block;margin:1.4em 0;"
                     + "text-align:center;font-family:'Courier New',monospace;font-size:.78em;"
                     + $"color:#4A5568;\">the recording expired on {date}; "
                     + "its screenshots went with its video</span>";
            });
        }
    }
}
