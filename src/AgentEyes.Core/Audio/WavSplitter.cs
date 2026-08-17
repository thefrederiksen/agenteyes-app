using System;
using System.Collections.Generic;
using System.Text;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Duration + silence aware PCM WAV splitter. A DIRECT MIRROR of the DevThrottle Gateway's
    /// <c>WavSplitter.TrySplitByDuration</c> (open repo, CcDirector.Core.Audio) so AgentEyes chunks a long
    /// recording exactly the same way - same algorithm, same constants. AgentEyes cannot share code across
    /// the open/closed boundary, so this is the deliberate duplicate specified by
    /// docs/architecture/transcription-service-design.md (devthrottle_internal#324). Keep the two in sync.
    ///
    /// Splits by DURATION, nudging each cut to a nearby quiet point so a word is not sliced. The split is
    /// LOSSLESS and NON-OVERLAPPING: concatenating the parts reproduces the input, so the per-part
    /// transcripts join with a single space and no de-duplication.
    /// </summary>
    internal static class WavSplitter
    {
        /// <summary>The bytes a minimal RIFF/WAV header adds in front of the sample data.</summary>
        public const int WavHeaderBytes = 44;

        /// <summary>
        /// Split a PCM WAV into standalone WAV parts by duration. Every part is at most
        /// <paramref name="maxSeconds"/> long AND at most <paramref name="maxPartBytes"/>. A clip already
        /// within one target window comes back as a single part. Returns false (null parts) for anything
        /// that is not linear PCM WAV.
        /// </summary>
        public static bool TrySplitByDuration(
            byte[] wav, int targetSeconds, int maxSeconds, int silenceWindowSeconds, int maxPartBytes,
            out IReadOnlyList<byte[]>? parts)
        {
            parts = null;
            if (wav is null) throw new ArgumentNullException(nameof(wav));
            if (targetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetSeconds));
            if (maxSeconds < targetSeconds) throw new ArgumentOutOfRangeException(nameof(maxSeconds));
            if (maxPartBytes <= WavHeaderBytes)
                throw new ArgumentOutOfRangeException(nameof(maxPartBytes), "the byte budget must leave room for the WAV header");

            if (!TryParse(wav, out var fmt, out var dataOffset, out var dataLength))
                return false;

            int blockAlign = fmt.Channels * fmt.BitsPerSample / 8;
            if (blockAlign <= 0 || fmt.SampleRate <= 0) return false;

            int framesTotal = dataLength / blockAlign;
            if (framesTotal <= 0) return false;

            long maxFramesByBytes = (maxPartBytes - WavHeaderBytes) / blockAlign;
            long maxFramesByDuration = (long)maxSeconds * fmt.SampleRate;
            int hardMaxFrames = (int)Math.Min(maxFramesByBytes, maxFramesByDuration);
            if (hardMaxFrames <= 0) return false;

            int targetFrames = (int)Math.Min((long)targetSeconds * fmt.SampleRate, hardMaxFrames);
            int windowFrames = (int)Math.Max(0, Math.Min((long)silenceWindowSeconds * fmt.SampleRate, targetFrames - 1));
            int probeFrames = Math.Max(1, fmt.SampleRate * 20 / 1000);   // ~20 ms energy window

            var result = new List<byte[]>();
            int pos = 0;
            while (pos < framesTotal)
            {
                int remaining = framesTotal - pos;
                int cutLen;
                if (remaining <= hardMaxFrames && remaining <= targetFrames + windowFrames)
                {
                    cutLen = remaining;
                }
                else
                {
                    int lo = Math.Max(1, targetFrames - windowFrames);
                    int hi = Math.Min(hardMaxFrames, Math.Min(remaining, targetFrames + windowFrames));
                    if (hi < lo) hi = lo;
                    cutLen = QuietestCut(wav, dataOffset, blockAlign, fmt.BitsPerSample, pos, lo, hi, probeFrames);
                }
                if (cutLen <= 0) cutLen = Math.Min(hardMaxFrames, remaining);

                int byteLen = cutLen * blockAlign;
                var pcm = new byte[byteLen];
                Array.Copy(wav, dataOffset + pos * blockAlign, pcm, 0, byteLen);
                result.Add(PcmWav.Wrap(pcm, fmt.SampleRate, fmt.Channels, fmt.BitsPerSample));
                pos += cutLen;
            }

            if (result.Count == 0) return false;
            parts = result;
            return true;
        }

        private static int QuietestCut(byte[] wav, int dataOffset, int blockAlign, int bitsPerSample,
            int posFrames, int lo, int hi, int probeFrames)
        {
            if (hi <= lo) return Math.Max(1, hi);
            if (bitsPerSample != 16) return hi;

            int step = Math.Max(1, probeFrames / 2);
            long best = long.MaxValue;
            int bestCut = hi;
            for (int c = lo; c <= hi; c += step)
            {
                long e = FrameEnergy(wav, dataOffset, blockAlign, posFrames + c, probeFrames);
                if (e < best) { best = e; bestCut = c; }
            }
            return bestCut;
        }

        private static long FrameEnergy(byte[] wav, int dataOffset, int blockAlign, int centerFrame, int probeFrames)
        {
            int start = Math.Max(0, centerFrame - probeFrames / 2);
            long sum = 0;
            for (int f = 0; f < probeFrames; f++)
            {
                int frameByte = dataOffset + (start + f) * blockAlign;
                if (frameByte + blockAlign > wav.Length) break;
                for (int b = 0; b + 1 < blockAlign; b += 2)
                {
                    short s = (short)(wav[frameByte + b] | (wav[frameByte + b + 1] << 8));
                    sum += Math.Abs((int)s);
                }
            }
            return sum;
        }

        private readonly record struct WavFormat(int SampleRate, int Channels, int BitsPerSample);

        private static bool TryParse(byte[] wav, out WavFormat fmt, out int dataOffset, out int dataLength)
        {
            fmt = default;
            dataOffset = 0;
            dataLength = 0;
            if (wav.Length < 12) return false;
            if (!(wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F')) return false;
            if (!(wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E')) return false;

            bool haveFmt = false;
            bool haveData = false;
            int p = 12;
            while (p + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, p, 4);
                int size = BitConverter.ToInt32(wav, p + 4);
                int body = p + 8;

                if (size < 0 || body + size > wav.Length)
                    size = wav.Length - body;
                if (size < 0) break;

                if (id == "fmt ")
                {
                    if (size < 16) return false;
                    short audioFormat = BitConverter.ToInt16(wav, body);
                    short channels = BitConverter.ToInt16(wav, body + 2);
                    int sampleRate = BitConverter.ToInt32(wav, body + 4);
                    short bitsPerSample = BitConverter.ToInt16(wav, body + 14);
                    if (audioFormat != 1) return false; // linear PCM only
                    fmt = new WavFormat(sampleRate, channels, bitsPerSample);
                    haveFmt = true;
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = size;
                    haveData = true;
                }

                int advance = size + (size & 1);
                p = body + advance;
            }

            return haveFmt && haveData && dataLength > 0;
        }
    }
}
