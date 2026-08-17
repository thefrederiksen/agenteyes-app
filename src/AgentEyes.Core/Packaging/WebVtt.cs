using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// WebVTT (.vtt) read/write for transcript segments (issue #98). WebVTT is the subtitle
    /// format Microsoft/Teams and browsers understand, so it is AgentEyes' interchange artifact:
    /// every later transcription slice (translate, burn-in, import) reads and writes this format.
    ///
    /// The mapping is 1:1 with an in-memory <see cref="TranscriptSegment"/>: one VTT cue per
    /// segment, cue timing formatted as <c>HH:MM:SS.mmm --> HH:MM:SS.mmm</c>. A single-segment
    /// transcript (today's DevThrottle Whisper output) produces a single-cue VTT; a multi-cue
    /// transcript round-trips cue-for-cue.
    /// </summary>
    internal static class WebVtt
    {
        /// <summary>Default language code for the source transcript (issue #98, assumption A1).</summary>
        public const string DefaultLanguage = "en";

        /// <summary>The recording-folder file name for a language's WebVTT, e.g. transcript.en.vtt.</summary>
        public static string FileNameFor(string language) => $"transcript.{language}.vtt";

        /// <summary>
        /// Serialize transcript segments to a WebVTT document: a <c>WEBVTT</c> header line, then one
        /// cue per segment. Uses <c>\n</c> line endings (valid WebVTT; browsers/Teams accept it).
        /// </summary>
        public static string Write(IEnumerable<TranscriptSegment> segments)
        {
            if (segments is null) throw new ArgumentNullException(nameof(segments));
            var sb = new StringBuilder();
            sb.Append("WEBVTT\n\n");
            foreach (var seg in segments)
            {
                sb.Append(FormatTimestamp(seg.StartSeconds));
                sb.Append(" --> ");
                sb.Append(FormatTimestamp(seg.EndSeconds));
                sb.Append('\n');
                sb.Append(seg.Text ?? string.Empty);
                sb.Append("\n\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parse a WebVTT document into transcript segments. Recognizes each cue by its
        /// <c>--&gt;</c> timing line; cue text is every following non-blank line joined with
        /// <c>\n</c>. Cue identifiers, the header, NOTE blocks, and cue settings after the end
        /// timestamp are ignored. Accepts either <c>HH:MM:SS.mmm</c> or <c>MM:SS.mmm</c> timing.
        /// </summary>
        public static List<TranscriptSegment> Read(string content)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));
            var result = new List<TranscriptSegment>();
            var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                int arrow = lines[i].IndexOf("-->", StringComparison.Ordinal);
                if (arrow < 0) continue;

                var parts = lines[i].Split(new[] { "-->" }, StringSplitOptions.None);
                double start = ParseTimestamp(parts[0].Trim());

                // The end timestamp may be followed by cue settings (e.g. "align:start") - take
                // only the timestamp token.
                string endToken = parts[1].Trim();
                int space = endToken.IndexOf(' ');
                if (space >= 0) endToken = endToken.Substring(0, space);
                double end = ParseTimestamp(endToken);

                var textLines = new List<string>();
                int j = i + 1;
                for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
                {
                    textLines.Add(lines[j]);
                }

                result.Add(new TranscriptSegment
                {
                    StartSeconds = start,
                    EndSeconds = end,
                    Text = string.Join("\n", textLines),
                });
                i = j;
            }

            return result;
        }

        /// <summary>Format a second offset as a WebVTT cue timestamp: <c>HH:MM:SS.mmm</c>.</summary>
        public static string FormatTimestamp(double seconds)
        {
            if (seconds < 0) seconds = 0;
            long totalMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
            long ms = totalMs % 1000;
            long totalSec = totalMs / 1000;
            long s = totalSec % 60;
            long m = (totalSec / 60) % 60;
            long h = totalSec / 3600;
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}.{3:D3}", h, m, s, ms);
        }

        /// <summary>Parse a WebVTT cue timestamp (<c>HH:MM:SS.mmm</c> or <c>MM:SS.mmm</c>) to seconds.</summary>
        public static double ParseTimestamp(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new FormatException("empty WebVTT timestamp.");

            string[] dot = token.Split('.');
            int ms = 0;
            if (dot.Length > 1)
            {
                string frac = dot[1].PadRight(3, '0');
                if (frac.Length > 3) frac = frac.Substring(0, 3);
                ms = int.Parse(frac, CultureInfo.InvariantCulture);
            }

            string[] hms = dot[0].Split(':');
            int h = 0, m, sec;
            if (hms.Length == 3)
            {
                h = int.Parse(hms[0], CultureInfo.InvariantCulture);
                m = int.Parse(hms[1], CultureInfo.InvariantCulture);
                sec = int.Parse(hms[2], CultureInfo.InvariantCulture);
            }
            else if (hms.Length == 2)
            {
                m = int.Parse(hms[0], CultureInfo.InvariantCulture);
                sec = int.Parse(hms[1], CultureInfo.InvariantCulture);
            }
            else
            {
                throw new FormatException($"invalid WebVTT timestamp: {token}");
            }

            return (h * 3600) + (m * 60) + sec + (ms / 1000.0);
        }
    }
}
