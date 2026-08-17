using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace AgentEyes.Packaging
{
    /// <summary>A screenshot/frame placed on the timeline at a given offset.</summary>
    internal sealed class WalkthroughShot
    {
        public double OffsetSeconds { get; set; }
        public string RelativePath { get; set; } = "";
    }

    /// <summary>
    /// Pure builder: interleave screenshots and transcript segments by offset into a single
    /// self-contained walkthrough.html. Side-effect free so it is fully unit-testable.
    /// (We render our own HTML rather than copy Avalonia's MarkdownHtmlRenderer to avoid pulling
    /// in Avalonia dependencies - see vendor/PROVENANCE.md.)
    /// </summary>
    internal static class WalkthroughBuilder
    {
        public static string Build(string title, IReadOnlyList<WalkthroughShot> shots,
            IReadOnlyList<TranscriptSegment> segments)
        {
            // Merge into one timeline ordered by offset. Shots sort before speech at the same offset.
            var timeline = new List<(double At, int Kind, string Html)>();
            foreach (var s in shots)
            {
                timeline.Add((s.At(), 0, ShotHtml(s)));
            }
            foreach (var seg in segments)
            {
                timeline.Add((seg.StartSeconds, 1, SpeechHtml(seg)));
            }

            var ordered = timeline
                .Select((item, idx) => (item, idx))
                .OrderBy(x => x.item.At)
                .ThenBy(x => x.item.Kind)
                .ThenBy(x => x.idx)
                .Select(x => x.item.Html);

            var sb = new StringBuilder();
            sb.Append(HeadHtml(title));
            sb.Append("<article class=\"wt\">\n");
            sb.Append("<h1>").Append(Enc(title)).Append("</h1>\n");

            if (!shots.Any() && !segments.Any())
            {
                sb.Append("<p class=\"empty\">No screenshots or transcript segments were produced.</p>\n");
            }

            foreach (var html in ordered)
            {
                sb.Append(html).Append('\n');
            }

            sb.Append("</article>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static string ShotHtml(WalkthroughShot s) =>
            $"<figure class=\"shot\"><img src=\"{Enc(s.RelativePath)}\" alt=\"screenshot at {Timecodes.Clock(TimeSpan.FromSeconds(s.OffsetSeconds))}\"/>"
            + $"<figcaption>{Timecodes.Clock(TimeSpan.FromSeconds(s.OffsetSeconds))}</figcaption></figure>";

        private static string SpeechHtml(TranscriptSegment seg) =>
            $"<p class=\"line\"><span class=\"ts\">{Timecodes.Clock(TimeSpan.FromSeconds(seg.StartSeconds))}</span> {Enc(seg.Text)}</p>";

        private static string Enc(string s) => WebUtility.HtmlEncode(s);

        private static string HeadHtml(string title) =>
            "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\"/>\n"
            + $"<title>{Enc(title)}</title>\n<style>\n"
            + "body{font-family:Georgia,serif;color:#2D3748;background:#E2E8F0;margin:0;line-height:1.7;}\n"
            + ".wt{max-width:820px;margin:0 auto;background:#fff;padding:2.5rem 2.5rem 4rem;box-shadow:0 4px 24px rgba(26,54,93,.15);}\n"
            + "h1{font-family:'Palatino Linotype',Georgia,serif;color:#1A365D;border-bottom:3px solid #D69E2E;padding-bottom:.35em;}\n"
            + ".line{margin:.5em 0;}\n.ts{font-family:'Courier New',monospace;font-size:.8em;color:#fff;background:#1A365D;border-radius:3px;padding:1px 6px;margin-right:6px;}\n"
            + ".shot{margin:1.4em 0;text-align:center;}\n.shot img{max-width:100%;border:1px solid #CBD5E0;border-radius:6px;box-shadow:0 2px 10px rgba(26,54,93,.12);}\n"
            + ".shot figcaption{font-family:'Courier New',monospace;font-size:.78em;color:#4A5568;margin-top:.4em;}\n"
            + ".empty{color:#4A5568;font-style:italic;}\n</style>\n</head>\n<body>\n";
    }

    internal static class WalkthroughShotExtensions
    {
        public static double At(this WalkthroughShot s) => s.OffsetSeconds;
    }
}
