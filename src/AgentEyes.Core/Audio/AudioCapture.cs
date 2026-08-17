using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Microphone capture to a WAV file, with a live peak-level event for the meter.
    ///
    /// VENDORED + ADAPTED from cc-director:
    ///   source: playground/voice-chat/src/VoiceChat.Core/Pipeline/AudioCapture.cs
    ///   commit: c2f0c90 (2026-06-02)
    /// Adaptations: device selection, write to a WaveFileWriter instead of a MemoryStream,
    /// and a LevelChanged peak event for the HUD/console meter. See vendor/PROVENANCE.md.
    /// </summary>
    internal sealed class AudioCapture : IDisposable
    {
        // 16 kHz, 16-bit mono PCM - the format Whisper expects (kept from the original).
        public static readonly WaveFormat CaptureFormat = new(16000, 16, 1);

        private readonly WaveInEvent _waveIn;
        private WaveFileWriter? _writer;
        private bool _isRecording;

        public bool IsRecording => _isRecording;

        /// <summary>Peak amplitude of the most recent buffer, 0.0 - 1.0.</summary>
        public event Action<float>? LevelChanged;

        public AudioCapture(int deviceNumber)
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = CaptureFormat,
                BufferMilliseconds = 50,
            };
            _waveIn.DataAvailable += OnDataAvailable;
        }

        public void Start(string wavPath)
        {
            if (_isRecording) return;
            _writer = new WaveFileWriter(wavPath, _waveIn.WaveFormat);
            _isRecording = true;
            _waveIn.StartRecording();
        }

        /// <summary>
        /// Capture for the level meter only - no file is written. Used during video
        /// recordings, where ffmpeg owns the mic stream and nothing in-process would
        /// otherwise see the samples.
        /// </summary>
        public void StartMonitor()
        {
            if (_isRecording) return;
            _isRecording = true;
            _waveIn.StartRecording();
        }

        public void Stop()
        {
            if (!_isRecording) return;
            _waveIn.StopRecording();
            _isRecording = false;
            _writer?.Dispose();
            _writer = null;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);

            float peak = 0f;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                float a = Math.Abs(sample) / 32768f;
                if (a > peak) peak = a;
            }
            LevelChanged?.Invoke(peak);
        }

        /// <summary>Enumerate input devices as (deviceNumber, name), with full device names.</summary>
        public static (int Number, string Name)[] Devices()
        {
            int count = WaveInEvent.DeviceCount;
            string[] friendly = count > 0 ? CaptureFriendlyNames() : Array.Empty<string>();
            var list = new (int, string)[count];
            for (int i = 0; i < count; i++)
            {
                list[i] = (i, FullName(WaveInEvent.GetCapabilities(i).ProductName, friendly));
            }
            return list;
        }

        /// <summary>
        /// WAVEINCAPS truncates device names to 31 chars ("Microphone (FDUCE SL40 Audio De"),
        /// and those stubs used to leak into presets, manifests and every dropdown (issue #9).
        /// Recover the full name from the WASAPI endpoint list: a truncated WaveIn name is a
        /// prefix of exactly one FriendlyName in the common case. Zero or several prefix
        /// matches (e.g. two identical devices) keep the WaveIn name, which leaves the
        /// substring resolution exactly as strict as before.
        /// </summary>
        internal static string FullName(string waveInName, IReadOnlyList<string> friendlyNames)
        {
            if (waveInName.Length < 31) return waveInName;   // under the limit = not truncated
            string? match = null;
            foreach (var f in friendlyNames)
            {
                if (f.StartsWith(waveInName, StringComparison.OrdinalIgnoreCase))
                {
                    if (match != null) return waveInName;    // ambiguous - keep as-is
                    match = f;
                }
            }
            return match ?? waveInName;
        }

        private static string[] CaptureFriendlyNames()
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var names = new string[endpoints.Count];
            for (int i = 0; i < endpoints.Count; i++)
            {
                using var endpoint = endpoints[i];
                names[i] = endpoint.FriendlyName;
            }
            return names;
        }

        /// <summary>
        /// Resolve a --mic argument to a device number by case-insensitive substring match.
        /// Delegates to the pure DeviceResolver (no silent fallback to a default device).
        /// </summary>
        public static int ResolveDevice(string micNameFragment)
        {
            return DeviceResolver.Resolve(Devices(), micNameFragment);
        }

        public void Dispose()
        {
            if (_isRecording)
            {
                _waveIn.StopRecording();
            }
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _writer?.Dispose();
        }
    }
}
