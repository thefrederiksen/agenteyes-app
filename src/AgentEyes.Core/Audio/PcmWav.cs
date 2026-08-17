using System;
using System.IO;
using System.Text;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Wraps raw PCM in a minimal RIFF/WAV container. Mirrors the DevThrottle Gateway's PcmWav so the two
    /// duplicated transcription chunkers (open Gateway repo, this closed repo) produce byte-identical WAV
    /// parts. See transcription reliability epic (devthrottle_internal#324) and
    /// docs/architecture/transcription-service-design.md.
    /// </summary>
    internal static class PcmWav
    {
        /// <summary>Wrap raw little-endian PCM samples in a RIFF/WAV header (a complete .wav blob).</summary>
        public static byte[] Wrap(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
        {
            if (pcm is null) throw new ArgumentNullException(nameof(pcm));

            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            using var ms = new MemoryStream(44 + pcm.Length);
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcm.Length);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)blockAlign);
            bw.Write((short)bitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Write(pcm, 0, pcm.Length);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
