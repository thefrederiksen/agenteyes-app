using System;
using AgentEyes.Platform.Mac;

namespace AgentEyes.Audio
{
    /// <summary>
    /// macOS system-audio capture: the virtual loopback device the user has installed, recorded to
    /// sys_native.wav exactly where the Windows WASAPI loopback writes it, so the deferred mux
    /// (AudioMix) is shared. 48 kHz, 16-bit stereo.
    ///
    /// Unlike Windows and Linux, this REQUIRES something of the machine that macOS does not provide -
    /// see <see cref="MacSystemAudio"/>. Start fails with the fix steps on a Mac without one; it
    /// never opens the microphone instead and never writes a silent file.
    ///
    /// Like a pulse monitor and unlike WASAPI loopback, a virtual device delivers silence while
    /// nothing plays, so the timeline does not collapse and no keep-alive player (issue #126) is
    /// needed.
    /// </summary>
    internal sealed class LoopbackCapture : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;

        private AvfStream? _stream;
        private WavStreamWriter? _writer;
        private bool _isRecording;

        /// <summary>The loopback device being recorded, once started.</summary>
        public string? SourceName { get; private set; }

        public event Action<float>? LevelChanged;

        public event Action<AudioLevel>? BufferLevel;

        public void Start(string wavPath)
        {
            if (_isRecording) return;
            Log.Info($"[LoopbackCapture] Start: {wavPath}");
            var stream = Open();
            _writer = new WavStreamWriter(wavPath, SampleRate, Channels);
            _isRecording = true;
            stream.Start();
        }

        private AvfStream Open()
        {
            var device = MacSystemAudio.Require();
            SourceName = device.Name;
            var stream = new AvfStream(device.Index, SampleRate, Channels, "system");
            stream.Data += OnData;
            _stream = stream;
            return stream;
        }

        public void Stop()
        {
            if (!_isRecording) return;
            Log.Info("[LoopbackCapture] Stop");
            _isRecording = false;
            _stream?.Stop();
            var w = _writer;
            _writer = null;
            if (w != null)
            {
                Log.Info($"[LoopbackCapture] Stop: wrote {w.DataBytes} bytes of PCM");
                w.Dispose();
            }
        }

        private void OnData(byte[] buffer, int count)
        {
            _writer?.Write(buffer, count);
            var level = AvfStream.Measure(buffer, count);
            LevelChanged?.Invoke(level.Peak);
            BufferLevel?.Invoke(level);
        }

        public void Dispose()
        {
            try { Stop(); }
            catch (Exception ex) { Log.Error("[LoopbackCapture] Dispose: stop FAILED", ex); }
            if (_stream != null)
            {
                _stream.Data -= OnData;
                _stream.Dispose();
            }
            _writer?.Dispose();
        }
    }
}
