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
        private readonly System.Diagnostics.Stopwatch _clock = new();

        public bool IsRecording => _isRecording;

        /// <summary>
        /// The capture clock timestamp of this microphone's first sample, for aligning it with a
        /// screen recording captured by a different ffmpeg process (issue #114). See
        /// <see cref="AvfStream.InputStartSeconds"/>.
        /// </summary>
        public double? InputStartSeconds => _stream.InputStartSeconds;

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
            _clock.Restart();
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
                // HOW MUCH OF THE TAKE IS ACTUALLY IN THE FILE, stated as a number and checked
                // (issue #114). A byte count on its own cannot be wrong, so it never reported
                // anything: the capture that kept 12% of its samples and the one that kept all of
                // them wrote the same shape of log line. Comparing it with the wall clock is what
                // makes a short capture say so. The warning is deliberately loud - a near-silent
                // recording that nothing complains about is the defect this issue exists for.
                double bytesPerSecond = SampleRate * Channels * 2;
                double captured = w.DataBytes / bytesPerSecond;
                double elapsed = _clock.Elapsed.TotalSeconds;
                _clock.Stop();
                string detail = $"wrote {w.DataBytes} bytes of PCM = {captured:F2}s of audio "
                                + $"over a {elapsed:F2}s capture";
                // The device needs a moment to deliver its first sample, so a capture is expected to
                // be slightly short; a second of slack covers that warm-up without covering loss.
                if (elapsed - captured > 1.0)
                    Log.Warn($"[AudioCapture] Stop: {detail} - {elapsed - captured:F2}s MISSING. "
                             + "The AVFoundation device dropped samples this end did not take in "
                             + "time; the recording will be short of audio by that much.");
                else
                    Log.Info($"[AudioCapture] Stop: {detail}");
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
