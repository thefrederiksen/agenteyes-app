using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Audio;

namespace AgentEyes.DevThrottle
{
    /// <summary>
    /// One timed transcript segment parsed from the hosted Whisper response (issue #99). Times are in
    /// SECONDS (float) and are chunk-LOCAL as returned by a single /audio/transcriptions call; the batch
    /// join shifts them onto the whole-recording timeline. See the segments[] contract in
    /// docs/cencon/proof/issue-99/handoff.md.
    /// </summary>
    internal sealed class TranscriptSegmentDto
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// The text, the audio duration (when known), and the per-segment timing (when the hosted response
    /// carries a segments[] array) of one transcribed chunk. <see cref="Segments"/> is null when the
    /// response has no segments[] (legacy shape) - callers then fall back to a single whole-clip segment.
    /// </summary>
    internal readonly record struct ChunkResult(
        string Text, double? DurationSeconds, IReadOnlyList<TranscriptSegmentDto>? Segments = null);

    /// <summary>
    /// Splits a long PCM WAV into short, bounded parts and transcribes them IN PARALLEL, then joins the
    /// per-part text in original order. A DIRECT MIRROR of the DevThrottle Gateway's
    /// BatchTranscriptionPipeline chunking (open repo) - same algorithm and the SAME constants - so a one
    /// hour AgentEyes recording is many fast requests instead of one that times out. See
    /// docs/architecture/transcription-service-design.md (devthrottle_internal#324). Keep the constants in
    /// sync with the Gateway.
    ///
    /// The actual per-chunk POST is injected as a delegate, so this orchestration is unit-testable without
    /// HTTP and DevThrottleClient owns the real transport.
    /// </summary>
    internal static class BatchTranscription
    {
        // Shared spec constants (must match the Gateway).
        public const int MaxUploadBytes = 4_000_000;
        public const int ChunkTargetSeconds = 60;
        public const int ChunkMaxSeconds = 90;
        public const int ChunkSilenceWindowSeconds = 5;
        public const int MaxParallelChunks = 4;
        public const int PerChunkRetries = 1;

        /// <summary>Transcribe one bounded WAV chunk (the injected transport).</summary>
        public delegate Task<ChunkResult> TranscribeChunk(byte[] audio, string fileName, CancellationToken ct);

        /// <summary>
        /// Transcribe a whole WAV: single-shot when it fits one target window, otherwise split by duration
        /// (silence-aware, lossless, non-overlapping) and transcribe the parts in parallel, bounded to
        /// <see cref="MaxParallelChunks"/>, joining the text in ORIGINAL order. Per-chunk retry on a
        /// transient failure; any chunk still failing fails the whole call (no silent partials). A non-WAV
        /// blob is sent single-shot as-is.
        /// </summary>
        public static async Task<ChunkResult> TranscribeAsync(
            byte[] wav, string fileName, TranscribeChunk transcribeOne, Func<Exception, bool> isTransient,
            CancellationToken ct = default)
        {
            if (wav is null) throw new ArgumentNullException(nameof(wav));
            if (transcribeOne is null) throw new ArgumentNullException(nameof(transcribeOne));

            if (WavSplitter.TrySplitByDuration(
                    wav, ChunkTargetSeconds, ChunkMaxSeconds, ChunkSilenceWindowSeconds, MaxUploadBytes,
                    out var parts) && parts is not null && parts.Count > 1)
            {
                Log.Info($"[BatchTranscription] split {wav.Length} bytes into {parts.Count} parts; " +
                         $"transcribing up to {MaxParallelChunks} in parallel");

                var results = new ChunkResult[parts.Count];
                using var gate = new SemaphoreSlim(MaxParallelChunks);

                async Task ProcessAsync(int idx)
                {
                    await gate.WaitAsync(ct);
                    try { results[idx] = await PostWithRetryAsync(parts[idx], PartFileName(fileName, idx), idx, transcribeOne, isTransient, ct); }
                    finally { gate.Release(); }
                }

                var tasks = new Task[parts.Count];
                for (int i = 0; i < parts.Count; i++) tasks[i] = ProcessAsync(i);
                await Task.WhenAll(tasks);   // throws the first chunk failure -> the whole call fails clean

                string text = string.Join(" ", results.Where(r => !string.IsNullOrEmpty(r.Text)).Select(r => r.Text));
                double duration = results.Sum(r => r.DurationSeconds ?? 0);
                return new ChunkResult(text, duration, JoinSegments(results));
            }

            // One part (WAV within a single window) or a non-WAV blob: a single request, as before.
            var only = parts is { Count: 1 } ? parts[0] : wav;
            return await transcribeOne(only, fileName, ct);
        }

        /// <summary>
        /// Shift each chunk's LOCAL segment times onto the whole-recording timeline by adding the summed
        /// duration of the preceding chunks (the same running total the joined text implies), preserving
        /// original chunk order. Returns null when no chunk reported segments, so the caller keeps the
        /// single-segment fallback.
        /// </summary>
        private static IReadOnlyList<TranscriptSegmentDto>? JoinSegments(IReadOnlyList<ChunkResult> results)
        {
            var joined = new List<TranscriptSegmentDto>();
            double offset = 0;
            foreach (var r in results)
            {
                if (r.Segments is { Count: > 0 })
                {
                    foreach (var s in r.Segments)
                    {
                        joined.Add(new TranscriptSegmentDto
                        {
                            StartSeconds = s.StartSeconds + offset,
                            EndSeconds = s.EndSeconds + offset,
                            Text = s.Text,
                        });
                    }
                }
                offset += r.DurationSeconds ?? 0;
            }
            return joined.Count > 0 ? joined : null;
        }

        private static async Task<ChunkResult> PostWithRetryAsync(
            byte[] audio, string fileName, int chunkIndex, TranscribeChunk transcribeOne,
            Func<Exception, bool> isTransient, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await transcribeOne(audio, fileName, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < PerChunkRetries && isTransient(ex))
                {
                    Log.Info($"[BatchTranscription] chunk {chunkIndex} transient failure " +
                             $"(attempt {attempt + 1}/{PerChunkRetries + 1}): {ex.Message} - retrying");
                }
                catch (Exception ex)
                {
                    Log.Info($"[BatchTranscription] chunk {chunkIndex} failed: {ex.Message}");
                    throw;
                }
            }
        }

        // audio.wav -> audio.3.wav, keeping the extension so the server still decodes the bytes.
        private static string PartFileName(string fileName, int index)
        {
            if (string.IsNullOrEmpty(fileName)) return $"audio.{index}.wav";
            var ext = System.IO.Path.GetExtension(fileName);
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            return $"{stem}.{index}{ext}";
        }
    }
}
