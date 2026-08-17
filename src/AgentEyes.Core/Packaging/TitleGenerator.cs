using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentEyes.Ai;
using AgentEyes.DevThrottle;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// Names a recording from its transcript: a short title + one-line description via DevThrottle
    /// chat inference (issue #88). Requires a signed-in DevThrottle account; with none, recordings
    /// simply keep their preset-derived names (the caller treats a failure as non-fatal).
    /// </summary>
    internal static class TitleGenerator
    {
        public static string Model => DevThrottleClient.ChatModel;
        // Budget for how much transcript the titler sees. Anything longer is sampled across the
        // whole timeline (see TranscriptText), not truncated to the opening.
        private const int MaxTranscriptChars = 48000;

        public static bool IsConfigured => DevThrottleAccount.IsSignedIn;

        /// <summary>Title + description for a recording, plus the token usage the API reported
        /// (null if the provider omitted it) so the caller can record what the call cost.</summary>
        internal sealed record TitleResult(string Title, string Description, AiUsage? Usage, string Model);

        public static async Task<TitleResult> GenerateAsync(IReadOnlyList<TranscriptSegment> segments)
        {
            if (!DevThrottleAccount.IsSignedIn)
                throw new UsageException("Not signed in to DevThrottle - cannot name the recording.");

            string model = DevThrottleClient.ChatModel;
            string transcript = TranscriptText(segments, MaxTranscriptChars);
            if (transcript.Length == 0)
                throw new UsageException("transcript is empty; nothing to title.");

            var (status, body) = await DevThrottleClient.PostChatAsync(BuildRequestBody(transcript, model));
            if (status is < 200 or >= 300)
                throw DevThrottleClient.ErrorFrom(status, body);

            var (title, description) = ParseResponse(body);
            return new TitleResult(title, description, ParseUsage(body), model);
        }

        internal static string BuildRequestBody(string transcript, string model) =>
            JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.2,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You name screen/audio recordings from their spoken transcript. "
                            + "Title the recording as a whole - its main purpose and subject - not just the "
                            + "opening lines or small talk, which are often unrelated to the point of the recording. "
                            + "For a meeting or conversation, capture what it was about and why (for example an "
                            + "intro/getting-to-know-you call, a planning discussion, a demo, an interview). "
                            + "The transcript may be supplied as excerpts separated by '[...]'; weigh all of it, "
                            + "not only the first excerpt. "
                            + "Reply with ONLY a JSON object: {\"title\": ..., \"description\": ...}. No prose, no code fences. "
                            + "The title is at most 8 words, plain and specific, no quotes and no trailing period. "
                            + "The description is one sentence of at most 25 words.",
                    },
                    new { role = "user", content = transcript },
                },
            });

        internal static (string Title, string Description) ParseResponse(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                string content = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString() ?? "";
                using var inner = JsonDocument.Parse(StripFences(content));
                string title = (inner.RootElement.GetProperty("title").GetString() ?? "").Trim();
                string description = inner.RootElement.TryGetProperty("description", out var d)
                    ? (d.GetString() ?? "").Trim()
                    : "";
                if (title.Length == 0)
                    throw new UsageException("DevThrottle returned an empty title.");
                return (title, description);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new UsageException($"unexpected title response: {Snippet(responseJson)}");
            }
        }

        /// <summary>Pull the token "usage" object out of a chat response body, or null when omitted.</summary>
        internal static AiUsage? ParseUsage(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    int prompt = u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                    int completion = u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                    if (prompt > 0 || completion > 0) return new AiUsage(prompt, completion);
                }
            }
            catch (JsonException) { /* no usage */ }
            return null;
        }

        /// <summary>Some models wrap JSON in a ```json ... ``` fence; strip it before parsing.</summary>
        internal static string StripFences(string content)
        {
            string s = content.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s[(nl + 1)..];
                if (s.EndsWith("```")) s = s[..^3];
            }
            return s.Trim();
        }

        /// <summary>
        /// Transcript segments joined to one prompt-sized string. When longer than the budget,
        /// sample evenly-spaced windows across the WHOLE timeline rather than truncating to the opening.
        /// </summary>
        internal static string TranscriptText(IReadOnlyList<TranscriptSegment> segments, int maxChars)
        {
            var sb = new StringBuilder();
            foreach (var s in segments)
            {
                string t = (s.Text ?? "").Trim();
                if (t.Length == 0 || IsNonSpeechMarker(t)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t);
            }
            string full = sb.ToString();
            return full.Length <= maxChars ? full : Sample(full, maxChars);
        }

        /// <summary>Drop segments that are nothing but a bracketed non-speech marker (e.g. "[BLANK_AUDIO]").</summary>
        internal static bool IsNonSpeechMarker(string trimmed)
        {
            if (trimmed.Length < 2) return false;
            if (trimmed[0] == '[' && trimmed[^1] == ']' && trimmed.IndexOf(']') == trimmed.Length - 1) return true;
            if (trimmed[0] == '(' && trimmed[^1] == ')' && trimmed.IndexOf(')') == trimmed.Length - 1) return true;
            return false;
        }

        /// <summary>Excerpt a too-long transcript down to ~maxChars by taking evenly-spaced windows.</summary>
        internal static string Sample(string full, int maxChars)
        {
            const string gap = " [...] ";
            const int windows = 6;
            int budget = maxChars - gap.Length * (windows - 1);
            if (budget < windows) return full.Substring(0, maxChars);
            int win = budget / windows;
            var sb = new StringBuilder(maxChars);
            for (int i = 0; i < windows; i++)
            {
                int start = (int)((long)(full.Length - win) * i / (windows - 1));
                if (i > 0) sb.Append(gap);
                sb.Append(full, start, win);
            }
            return sb.ToString();
        }

        private static string Snippet(string text)
        {
            string oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "...";
        }
    }
}
