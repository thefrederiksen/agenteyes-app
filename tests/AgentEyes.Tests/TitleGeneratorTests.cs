using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using AgentEyes;
using AgentEyes.Packaging;

namespace AgentEyes.Tests
{
    public class TitleGeneratorTests
    {
        // ---- transcript assembly --------------------------------------------

        [Fact]
        public void TranscriptText_joins_segments_and_skips_blanks()
        {
            var segments = new List<TranscriptSegment>
            {
                new() { Text = "Okay, I want a new feature." },
                new() { Text = "   " },
                new() { Text = "The status will change." },
            };
            Assert.Equal("Okay, I want a new feature. The status will change.",
                TitleGenerator.TranscriptText(segments, 1000));
        }

        [Fact]
        public void TranscriptText_drops_non_speech_markers()
        {
            var segments = new List<TranscriptSegment>
            {
                new() { Text = "[BLANK_AUDIO]" },
                new() { Text = "How are you doing?" },
                new() { Text = "(inaudible)" },
                new() { Text = "Let's talk about the launch." },
            };
            Assert.Equal("How are you doing? Let's talk about the launch.",
                TitleGenerator.TranscriptText(segments, 1000));
        }

        [Fact]
        public void IsNonSpeechMarker_only_flags_whole_bracketed_tokens()
        {
            Assert.True(TitleGenerator.IsNonSpeechMarker("[BLANK_AUDIO]"));
            Assert.True(TitleGenerator.IsNonSpeechMarker("(inaudible)"));
            Assert.False(TitleGenerator.IsNonSpeechMarker("I read [the docs] today"));
            Assert.False(TitleGenerator.IsNonSpeechMarker("Real speech here"));
        }

        [Fact]
        public void TranscriptText_keeps_short_transcripts_whole()
        {
            var segments = new List<TranscriptSegment> { new() { Text = "short and sweet" } };
            Assert.Equal("short and sweet", TitleGenerator.TranscriptText(segments, 1000));
        }

        [Fact]
        public void TranscriptText_samples_across_long_transcripts_and_stays_within_budget()
        {
            var segments = new List<TranscriptSegment> { new() { Text = new string('a', 4000) + " ENDMARKER" } };
            string text = TitleGenerator.TranscriptText(segments, 600);
            Assert.True(text.Length <= 600, $"expected <= 600 chars, got {text.Length}");
            Assert.Contains("ENDMARKER", text);   // the end of the recording is represented
            Assert.Contains("[...]", text);        // and it is excerpted, not contiguous
        }

        // ---- request body ----------------------------------------------------

        [Fact]
        public void BuildRequestBody_is_a_chat_request_carrying_the_transcript()
        {
            using var doc = JsonDocument.Parse(TitleGenerator.BuildRequestBody("hello transcript", "zai-org/GLM-4.7-Flash"));
            var root = doc.RootElement;

            Assert.Equal("zai-org/GLM-4.7-Flash", root.GetProperty("model").GetString());

            var messages = root.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Contains("JSON", messages[0].GetProperty("content").GetString());
            Assert.Equal("hello transcript", messages[1].GetProperty("content").GetString());
        }

        // ---- response parsing -------------------------------------------------

        [Fact]
        public void ParseResponse_extracts_title_and_description()
        {
            string content = "{\"title\": \"Auto-title recordings\", \"description\": \"Walkthrough asking for transcript-based names.\"}";
            var (title, description) = TitleGenerator.ParseResponse(ChatCompletion(content));

            Assert.Equal("Auto-title recordings", title);
            Assert.Equal("Walkthrough asking for transcript-based names.", description);
        }

        [Fact]
        public void ParseResponse_missing_description_is_empty_not_an_error()
        {
            var (title, description) = TitleGenerator.ParseResponse(ChatCompletion("{\"title\": \"Just a title\"}"));
            Assert.Equal("Just a title", title);
            Assert.Equal("", description);
        }

        [Fact]
        public void ParseResponse_tolerates_a_markdown_json_fence()
        {
            var (title, _) = TitleGenerator.ParseResponse(ChatCompletion("```json\n{\"title\": \"Fenced title\"}\n```"));
            Assert.Equal("Fenced title", title);
        }

        [Fact]
        public void ParseResponse_empty_title_throws_usage()
        {
            var ex = Assert.Throws<UsageException>(
                () => TitleGenerator.ParseResponse(ChatCompletion("{\"title\": \"  \"}")));
            Assert.Contains("empty title", ex.Message);
        }

        [Fact]
        public void ParseResponse_garbage_throws_usage_with_snippet()
        {
            var ex = Assert.Throws<UsageException>(() => TitleGenerator.ParseResponse("not json at all"));
            Assert.Contains("unexpected title response", ex.Message);
            Assert.Contains("not json at all", ex.Message);
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>Wraps model output the way the chat completions endpoint returns it.</summary>
        private static string ChatCompletion(string content) =>
            JsonSerializer.Serialize(new
            {
                choices = new object[]
                {
                    new { message = new { role = "assistant", content } },
                },
            });
    }
}
