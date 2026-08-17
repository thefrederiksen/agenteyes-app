using System;
using NAudio.Wave;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Plays a WAV out the default render device (so WASAPI loopback can capture it). Used by the
    /// self-test to inject a known signal without a microphone or the user. Reuses NAudio.
    /// </summary>
    internal sealed class TonePlayer : IDisposable
    {
        private readonly WaveOutEvent _out = new();
        private AudioFileReader? _reader;

        public void Play(string wavPath)
        {
            Stop();
            _reader = new AudioFileReader(wavPath);
            _out.Init(_reader);
            _out.Play();
        }

        public void Stop()
        {
            try { _out.Stop(); } catch { }
            _reader?.Dispose();
            _reader = null;
        }

        public void Dispose()
        {
            Stop();
            _out.Dispose();
        }
    }
}
