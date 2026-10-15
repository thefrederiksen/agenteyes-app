using System;
using System.Linq;
using AgentEyes.Platform.Mac;

namespace AgentEyes.Audio
{
    /// <summary>
    /// macOS microphone capture: the same surface as the Windows NAudio class, over an
    /// <see cref="AvfStream"/> on the chosen AVFoundation audio input. 48 kHz, 16-bit mono - the
    /// Windows capture format, so mic.wav / audio.wav are the same files to the mux and the
    /// transcriber. Device numbers are positions in the AVFoundation audio listing.
    /// </summary>
    internal sealed class AudioCapture : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 1;

        private readonly AvfStream _stream;
        private WavStreamWriter? _writer;
        private bool _isRecording;

        public bool IsRecording => _isRecording;

        /// <summary>Peak level (0..1) of each ~50 ms block.</summary>
        public event Action<float>? LevelChanged;

        /// <summary>Peak and energy of each block.</summary>
        public event Action<AudioLevel>? BufferLevel;

        public AudioCapture(int deviceNumber)
        {
            var mics = AvfDevices.Read().Audio;
            if (deviceNumber < 0 || deviceNumber >= mics.Count)
                throw new UsageException($"microphone #{deviceNumber} does not exist ({mics.Count} listed). Run 'agenteyes screens'.");
            var mic = mics[deviceNumber];
            Log.Info($"[AudioCapture] ctor: #{deviceNumber} {mic.Name} (AVFoundation :{mic.Index})");
            _stream = new AvfStream(mic.Index, SampleRate, Channels, "mic");
            _stream.Data += OnData;
        }

        /// <summary>Record to a WAV file (and report levels).</summary>
        public void Start(string wavPath)
        {
            if (_isRecording) return;
            Log.Info($"[AudioCapture] Start: {wavPath}");
            _writer = new WavStreamWriter(wavPath, SampleRate, Channels);
            _isRecording = true;
            _stream.Start();
        }

        /// <summary>Report levels only - the video recorder's mic meter; nothing is written.</summary>
        public void StartMonitor()
        {
            if (_isRecording) return;
            Log.Info("[AudioCapture] StartMonitor");
            _isRecording = true;
            _stream.Start();
        }

        public void Stop()
        {
            if (!_isRecording) return;
            Log.Info("[AudioCapture] Stop");
            _stream.Stop();
            _isRecording = false;
            var w = _writer;
            _writer = null;
            if (w != null)
            {
                Log.Info($"[AudioCapture] Stop: wrote {w.DataBytes} bytes of PCM");
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

        /// <summary>The microphones, numbered, by their AVFoundation name.</summary>
        public static (int Number, string Name)[] Devices() =>
            AvfDevices.Read().Audio.Select((m, i) => (i, m.Name)).ToArray();

        public static int ResolveDevice(string micNameFragment) => DeviceResolver.Resolve(Devices(), micNameFragment);

        public void Dispose()
        {
            try { Stop(); }
            catch (Exception ex) { Log.Error("[AudioCapture] Dispose: stop FAILED", ex); }
            _stream.Data -= OnData;
            _stream.Dispose();
            _writer?.Dispose();
        }
    }
}
