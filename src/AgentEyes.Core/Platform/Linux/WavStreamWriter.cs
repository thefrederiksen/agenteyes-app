using System;
using System.IO;
using System.Text;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// A streaming 16-bit PCM WAV writer (issue #102): the header goes out first with placeholder
    /// sizes, samples are appended as they arrive, and <see cref="Dispose"/> patches the RIFF and data
    /// sizes in. The Linux capture writes mic.wav / sys_native.wav with it - the same files the Windows
    /// NAudio writer produces, read by the same mux.
    /// </summary>
    internal sealed class WavStreamWriter : IDisposable
    {
        private readonly FileStream _file;
        private long _dataBytes;
        private bool _closed;

        public int SampleRate { get; }
        public int Channels { get; }
        public long DataBytes => _dataBytes;

        public WavStreamWriter(string path, int sampleRate, int channels)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            SampleRate = sampleRate;
            Channels = channels;
            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(0);
        }

        public void Write(byte[] buffer, int count)
        {
            if (_closed) throw new ObjectDisposedException(nameof(WavStreamWriter));
            _file.Write(buffer, 0, count);
            _dataBytes += count;
        }

        public void Flush() => _file.Flush();

        private void WriteHeader(long dataBytes)
        {
            const int bits = 16;
            int blockAlign = Channels * bits / 8;
            uint data = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
            var h = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
            BitConverter.GetBytes(36 + data).CopyTo(h, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(h, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(h, 12);
            BitConverter.GetBytes(16).CopyTo(h, 16);
            BitConverter.GetBytes((short)1).CopyTo(h, 20);
            BitConverter.GetBytes((short)Channels).CopyTo(h, 22);
            BitConverter.GetBytes(SampleRate).CopyTo(h, 24);
            BitConverter.GetBytes(SampleRate * blockAlign).CopyTo(h, 28);
            BitConverter.GetBytes((short)blockAlign).CopyTo(h, 32);
            BitConverter.GetBytes((short)bits).CopyTo(h, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
            BitConverter.GetBytes(data).CopyTo(h, 40);
            _file.Position = 0;
            _file.Write(h, 0, h.Length);
            _file.Position = _file.Length;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            WriteHeader(_dataBytes);
            _file.Flush();
            _file.Dispose();
        }
    }
}
