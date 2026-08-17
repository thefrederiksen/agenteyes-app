using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.Audio;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Mirrors the Gateway's WavSplitterDurationTests (devthrottle repo). Proves AgentEyes' duplicated
    /// <see cref="WavSplitter.TrySplitByDuration"/> splits by duration, prefers a quiet cut, and is
    /// lossless + non-overlapping. Transcription reliability epic (devthrottle_internal#324).
    /// </summary>
    public class WavSplitterDurationTests
    {
        private const int Rate = 16000;

        private static byte[] BuildWav(int frames, short sample, int gapStart = -1, int gapEnd = -1)
        {
            var pcm = new byte[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                short v = (gapStart >= 0 && f >= gapStart && f < gapEnd) ? (short)0 : sample;
                pcm[f * 2] = (byte)(v & 0xFF);
                pcm[f * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return PcmWav.Wrap(pcm, Rate, 1, 16);
        }

        private static byte[] PatternWav(int frames)
        {
            var pcm = new byte[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                short v = (short)(f % 1000 - 500);
                pcm[f * 2] = (byte)(v & 0xFF);
                pcm[f * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return PcmWav.Wrap(pcm, Rate, 1, 16);
        }

        private static int DataFrames(byte[] part) => (part.Length - WavSplitter.WavHeaderBytes) / 2;

        [Fact]
        public void LongWav_EachPartWithinDurationAndByteBudget()
        {
            var wav = PatternWav(300 * Rate);
            Assert.True(WavSplitter.TrySplitByDuration(wav, 60, 90, 5, 4_000_000, out var parts));
            Assert.True(parts!.Count >= 4);
            Assert.All(parts, p =>
            {
                Assert.True(DataFrames(p) <= 90 * Rate);
                Assert.True(p.Length <= 4_000_000);
            });
        }

        [Fact]
        public void Split_IsLosslessAndNonOverlapping()
        {
            int frames = 250 * Rate;
            var wav = PatternWav(frames);
            Assert.True(WavSplitter.TrySplitByDuration(wav, 60, 90, 5, 4_000_000, out var parts));

            var rejoined = new List<byte>(frames * 2);
            foreach (var p in parts!) rejoined.AddRange(p[WavSplitter.WavHeaderBytes..]);

            var originalPcm = wav[WavSplitter.WavHeaderBytes..];
            Assert.Equal(originalPcm.Length, rejoined.Count);
            Assert.True(originalPcm.AsSpan().SequenceEqual(rejoined.ToArray()));
        }

        [Fact]
        public void ShortClip_SinglePart()
        {
            var wav = PatternWav(10 * Rate);
            Assert.True(WavSplitter.TrySplitByDuration(wav, 60, 90, 5, 4_000_000, out var parts));
            Assert.Single(parts!);
        }

        [Fact]
        public void NotPcmWav_ReturnsFalse()
        {
            var notWav = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
            Assert.False(WavSplitter.TrySplitByDuration(notWav, 60, 90, 5, 4_000_000, out var parts));
            Assert.Null(parts);
        }

        [Fact]
        public void PrefersASilentCutNearTheTarget()
        {
            int gapStart = 30000, gapEnd = 31000;
            var wav = BuildWav(4 * Rate, sample: 8000, gapStart, gapEnd);

            Assert.True(WavSplitter.TrySplitByDuration(wav, 2, 3, 1, 100_000_000, out var parts));
            Assert.True(parts!.Count >= 2);

            int firstCut = DataFrames(parts[0]);
            Assert.True(firstCut >= gapStart - 400 && firstCut <= gapEnd + 400);
            Assert.NotEqual(2 * Rate, firstCut);
        }
    }
}
