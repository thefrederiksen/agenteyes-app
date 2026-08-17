using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.DevThrottle;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Segment-level timestamps from the hosted DevThrottle Whisper path (issue #99). Proves:
    /// (1) the response parser maps an optional segments[] of { start, end, text } to timed segments;
    /// (2) a legacy body WITHOUT segments[] yields the unchanged single whole-clip fallback;
    /// (3) the batch join shifts per-chunk LOCAL times onto the whole-recording timeline in order;
    /// (4) the packaging write path serializes multiple timed segments into transcript.json (not one block).
    /// No local Whisper.net path exists - this is purely the hosted-response consumer + fallback.
    /// </summary>
    public class TranscriptSegmentsTests
    {
        private const int Rate = 16000;
        private static byte[] Wav(int seconds) => PcmWav.Wrap(new byte[seconds * Rate * 2], Rate, 1, 16);

        // ---- AC1: response WITH segments[] maps to N segments (start/end/text) ----------------------

        [Fact]
        public void ParseTranscriptionResponse_WithSegments_MapsEachSegment()
        {
            string body = """
            {
              "text": "hello world goodbye",
              "duration": 6.0,
              "segments": [
                { "start": 0.0, "end": 2.0, "text": " hello world" },
                { "start": 2.0, "end": 6.0, "text": " goodbye" }
              ]
            }
            """;

            var result = DevThrottleClient.ParseTranscriptionResponse(body);

            Assert.Equal("hello world goodbye", result.Text);
            Assert.Equal(6.0, result.DurationSeconds);
            Assert.NotNull(result.Segments);
            Assert.Equal(2, result.Segments!.Count);

            Assert.Equal(0.0, result.Segments[0].StartSeconds);
            Assert.Equal(2.0, result.Segments[0].EndSeconds);
            Assert.Equal("hello world", result.Segments[0].Text);   // trimmed

            Assert.Equal(2.0, result.Segments[1].StartSeconds);
            Assert.Equal(6.0, result.Segments[1].EndSeconds);
            Assert.Equal("goodbye", result.Segments[1].Text);
        }

        // ---- AC2: response WITHOUT segments[] keeps the single-segment fallback ---------------------

        [Fact]
        public void ParseTranscriptionResponse_NoSegments_ReturnsNullSegments()
        {
            string body = """{ "text": "the whole clip as one block", "duration": 12.5 }""";

            var result = DevThrottleClient.ParseTranscriptionResponse(body);

            Assert.Equal("the whole clip as one block", result.Text);
            Assert.Equal(12.5, result.DurationSeconds);
            Assert.Null(result.Segments);   // absent segments[] -> fallback territory
        }

        [Fact]
        public void ParseTranscriptionResponse_EmptySegmentsArray_ReturnsNullSegments()
        {
            string body = """{ "text": "x", "duration": 1.0, "segments": [] }""";

            var result = DevThrottleClient.ParseTranscriptionResponse(body);

            Assert.Null(result.Segments);   // empty array is treated as absent so the fallback holds
        }

        [Fact]
        public void ToSegments_WithSegments_MapsOnePerReturnedSegment()
        {
            var transcript = new DevThrottleTranscript
            {
                Text = "hello world goodbye",
                DurationSeconds = 6.0,
                Segments = new List<TranscriptSegmentDto>
                {
                    new() { StartSeconds = 0.0, EndSeconds = 2.0, Text = "hello world" },
                    new() { StartSeconds = 2.0, EndSeconds = 6.0, Text = "goodbye" },
                },
            };

            var segments = Transcriber.ToSegments(transcript);

            Assert.Equal(2, segments.Count);
            Assert.Equal(0.0, segments[0].StartSeconds);
            Assert.Equal(2.0, segments[0].EndSeconds);
            Assert.Equal("hello world", segments[0].Text);
            Assert.Equal(2.0, segments[1].StartSeconds);
            Assert.Equal(6.0, segments[1].EndSeconds);
            Assert.Equal("goodbye", segments[1].Text);
        }

        [Fact]
        public void ToSegments_NoSegments_YieldsSingleWholeClipSegment()
        {
            var transcript = new DevThrottleTranscript
            {
                Text = "the whole clip as one block",
                DurationSeconds = 12.5,
                Segments = null,
            };

            var segments = Transcriber.ToSegments(transcript);

            var only = Assert.Single(segments);            // exactly one segment, unchanged fallback
            Assert.Equal(0, only.StartSeconds);
            Assert.Equal(12.5, only.EndSeconds);           // Start=0 .. End=duration
            Assert.Equal("the whole clip as one block", only.Text);
        }

        // ---- AC1 (multi-chunk): batch join shifts LOCAL segment times onto the global timeline -------

        // Fake transport: each chunk reports duration 10 s and one LOCAL segment at 1..2 s labeled by index,
        // so the join must offset chunk i by 10*i and keep original order.
        private static Task<ChunkResult> FakeSegmentedChunk(byte[] audio, string fileName, CancellationToken ct)
        {
            int idx = IndexFromName(fileName);
            var seg = new TranscriptSegmentDto { StartSeconds = 1.0, EndSeconds = 2.0, Text = $"c{idx}" };
            return Task.FromResult(new ChunkResult($"c{idx}", 10.0, new List<TranscriptSegmentDto> { seg }));
        }

        private static int IndexFromName(string name)
        {
            var t = name.Split('.');
            return t.Length >= 3 && int.TryParse(t[^2], out var n) ? n : 0;
        }

        [Fact]
        public async Task BatchJoin_OffsetsPerChunkSegments_ByPrecedingDuration_InOrder()
        {
            // 300 s of audio splits into several parts, each transcribed with one LOCAL 1..2 s segment.
            var result = await BatchTranscription.TranscribeAsync(
                Wav(300), "audio.wav", FakeSegmentedChunk, _ => false);

            Assert.NotNull(result.Segments);
            int chunks = result.Segments!.Count;
            Assert.True(chunks >= 4, $"expected >= 4 chunks, got {chunks}");

            for (int i = 0; i < chunks; i++)
            {
                // chunk i local 1..2 s shifted by 10*i (the summed preceding durations)
                Assert.Equal(1.0 + 10.0 * i, result.Segments[i].StartSeconds, 3);
                Assert.Equal(2.0 + 10.0 * i, result.Segments[i].EndSeconds, 3);
                Assert.Equal($"c{i}", result.Segments[i].Text);   // original order preserved
            }
        }

        [Fact]
        public async Task BatchJoin_NoChunkReportsSegments_ReturnsNullSegments()
        {
            // Legacy transport: text + duration only, no segments -> the joined result also has none.
            static Task<ChunkResult> Legacy(byte[] a, string f, CancellationToken ct) =>
                Task.FromResult(new ChunkResult("t", 10.0));

            var result = await BatchTranscription.TranscribeAsync(Wav(300), "audio.wav", Legacy, _ => false);

            Assert.Null(result.Segments);
        }

        // ---- AC3: the packaging write path serializes multiple timed segments into transcript.json ----

        [Fact]
        public void WriteTranscript_MultiSegment_TranscriptJsonHoldsAllTimedSegments()
        {
            var segments = new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 2.0,  Text = "first cue" },
                new() { StartSeconds = 2.0, EndSeconds = 5.5,  Text = "second cue" },
                new() { StartSeconds = 5.5, EndSeconds = 9.25, Text = "third cue" },
            };

            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-issue99-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Package.WriteTranscript(dir, segments);

                string json = File.ReadAllText(Path.Combine(dir, "transcript.json"));
                var read = JsonSerializer.Deserialize<List<TranscriptSegment>>(json);

                Assert.NotNull(read);
                Assert.Equal(3, read!.Count);   // three timed segments, NOT one block
                for (int i = 0; i < segments.Count; i++)
                {
                    Assert.Equal(segments[i].StartSeconds, read[i].StartSeconds, 3);
                    Assert.Equal(segments[i].EndSeconds, read[i].EndSeconds, 3);
                    Assert.Equal(segments[i].Text, read[i].Text);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
