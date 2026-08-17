using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentEyes.DevThrottle;

namespace AgentEyes.Packaging
{
    /// <summary>One transcript line with its time window.</summary>
    internal sealed class TranscriptSegment
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// Post-recording transcription. AgentEyes runs 100% on DevThrottle (issue #87): the finished
    /// recording's audio is transcribed by DevThrottle-hosted Whisper (whisper-large-v3) through the
    /// signed-in account. When the hosted response carries per-segment timing (a segments[] array,
    /// issue #99) the recording becomes ONE TranscriptSegment per returned segment; when it does not
    /// (legacy shape) it falls back to a single segment spanning the whole clip.
    /// </summary>
    internal static class Transcriber
    {
        /// <summary>
        /// Transcribe a 16 kHz mono WAV file into timestamped segments via DevThrottle: one segment per
        /// hosted-response segment when present, else a single whole-clip segment (fallback).
        /// </summary>
        public static async Task<List<TranscriptSegment>> TranscribeWavAsync(string wavPath)
        {
            Log.Info($"[Transcriber] TranscribeWavAsync: wavPath={wavPath}");
            var result = await DevThrottleClient.TranscribeAsync(wavPath);
            var segments = ToSegments(result);
            Log.Info($"[Transcriber] TranscribeWavAsync: length={result.Text.Length}, segments={segments.Count}");
            return segments;
        }

        /// <summary>
        /// Map a hosted transcription result to transcript segments (issue #99): one
        /// <see cref="TranscriptSegment"/> per returned segment when <see cref="DevThrottleTranscript.Segments"/>
        /// is present and non-empty, otherwise the unchanged single-segment fallback
        /// (Start=0 .. End=duration with the full text). Pure and side-effect free so the mapping and the
        /// fallback are unit-testable without HTTP.
        /// </summary>
        internal static List<TranscriptSegment> ToSegments(DevThrottleTranscript result)
        {
            if (result.Segments is { Count: > 0 })
            {
                return result.Segments
                    .Select(s => new TranscriptSegment
                    {
                        StartSeconds = s.StartSeconds,
                        EndSeconds = s.EndSeconds,
                        Text = s.Text,
                    })
                    .ToList();
            }

            return new List<TranscriptSegment>
            {
                new TranscriptSegment { StartSeconds = 0, EndSeconds = result.DurationSeconds ?? 0, Text = result.Text }
            };
        }
    }
}
