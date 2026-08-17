using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Audio;
using AgentEyes.DevThrottle;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Proves AgentEyes' <see cref="BatchTranscription"/> orchestration matches the Gateway: a long WAV is
    /// split by duration and transcribed IN PARALLEL (bounded), joined in ORIGINAL order, with per-chunk
    /// retry and a clean whole-job failure (no silent partials). The real HTTP POST is replaced by a fake
    /// delegate keyed on the chunk-index filename, so order is provable despite parallel completion.
    /// Transcription reliability epic (devthrottle_internal#324).
    /// </summary>
    public class BatchTranscriptionTests
    {
        private const int Rate = 16000;
        private static byte[] Wav(int seconds) => PcmWav.Wrap(new byte[seconds * Rate * 2], Rate, 1, 16);

        private static bool IsTransient(Exception ex) =>
            ex is DevThrottleException d && (d.Status >= 500 || d.Status == 429);

        // Fake transport: records peak concurrency and per-chunk attempts; answers seg{idx} keyed on the
        // filename (audio.{idx}.wav) so the join order is deterministic under parallelism.
        private sealed class FakeTransport
        {
            public int MaxConcurrent;
            private int _inFlight;
            private readonly object _lock = new();
            private readonly ConcurrentDictionary<int, int> _attempts = new();
            public ConcurrentBag<int> FailPermanent { get; } = new();       // throw a permanent 400
            public ConcurrentBag<int> FailTransientOnce { get; } = new();   // throw 503 on the first attempt only

            public int DistinctChunks => _attempts.Count;
            public int Attempts(int idx) => _attempts.TryGetValue(idx, out var v) ? v : 0;

            public async Task<ChunkResult> Transcribe(byte[] audio, string fileName, CancellationToken ct)
            {
                int now = Interlocked.Increment(ref _inFlight);
                lock (_lock) { if (now > MaxConcurrent) MaxConcurrent = now; }
                try
                {
                    int idx = IndexFromName(fileName);
                    int attempt = _attempts.AddOrUpdate(idx, 1, (_, v) => v + 1);
                    await Task.Delay(15, ct);
                    if (FailTransientOnce.Contains(idx) && attempt == 1)
                        throw new DevThrottleException("transient", 503);
                    if (FailPermanent.Contains(idx))
                        throw new DevThrottleException("permanent", 400);
                    return new ChunkResult($"seg{idx}", 1.0);
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            }

            private static int IndexFromName(string name)
            {
                var t = name.Split('.');
                return t.Length >= 3 && int.TryParse(t[^2], out var n) ? n : 0;
            }
        }

        [Fact]
        public async Task LongWav_SplitsAndJoinsInOrder_ConcurrencyBounded()
        {
            var fake = new FakeTransport();
            var result = await BatchTranscription.TranscribeAsync(Wav(300), "audio.wav", fake.Transcribe, IsTransient);

            Assert.True(fake.DistinctChunks >= 4);
            var expected = string.Join(" ", Enumerable.Range(0, fake.DistinctChunks).Select(i => $"seg{i}"));
            Assert.Equal(expected, result.Text);                       // joined in ORIGINAL order
            Assert.True(fake.MaxConcurrent >= 2, $"peak concurrency was {fake.MaxConcurrent}");
            Assert.True(fake.MaxConcurrent <= BatchTranscription.MaxParallelChunks);
            Assert.Equal(fake.DistinctChunks, result.DurationSeconds); // 1s per chunk summed
        }

        [Fact]
        public async Task ShortWav_SingleShot_OneCall()
        {
            var fake = new FakeTransport();
            var result = await BatchTranscription.TranscribeAsync(Wav(5), "audio.wav", fake.Transcribe, IsTransient);

            Assert.Equal(1, fake.DistinctChunks);
            Assert.Equal("seg0", result.Text);
        }

        [Fact]
        public async Task UnderByteBudgetButLong_StillSplitsByDuration()
        {
            // 120 s of 16 kHz mono 16-bit = 3.84 MB: under the 4 MB budget but over the 90 s max chunk.
            var wav = Wav(120);
            Assert.True(wav.Length < BatchTranscription.MaxUploadBytes);

            var fake = new FakeTransport();
            await BatchTranscription.TranscribeAsync(wav, "audio.wav", fake.Transcribe, IsTransient);
            Assert.True(fake.DistinctChunks >= 2);
        }

        [Fact]
        public async Task OneChunkFailsPermanently_JobFailsClean_NoRetry()
        {
            var fake = new FakeTransport();
            fake.FailPermanent.Add(1);
            await Assert.ThrowsAsync<DevThrottleException>(() =>
                BatchTranscription.TranscribeAsync(Wav(300), "audio.wav", fake.Transcribe, IsTransient));
            Assert.Equal(1, fake.Attempts(1));   // a permanent 4xx is not retried
        }

        [Fact]
        public async Task OneChunkFailsTransiently_Retries_ThenJobSucceeds()
        {
            var fake = new FakeTransport();
            fake.FailTransientOnce.Add(1);
            var result = await BatchTranscription.TranscribeAsync(Wav(300), "audio.wav", fake.Transcribe, IsTransient);

            Assert.Equal(2, fake.Attempts(1));   // failed once, retried, succeeded
            var expected = string.Join(" ", Enumerable.Range(0, fake.DistinctChunks).Select(i => $"seg{i}"));
            Assert.Equal(expected, result.Text);
        }
    }
}
