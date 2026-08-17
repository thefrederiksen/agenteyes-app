using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Captures system audio output ("what you hear") via WASAPI loopback - the reliable way to
    /// record a movie/video playing on the machine, independent of any microphone. Writes the
    /// device's native mix format; convert to 16 kHz mono downstream (ffmpeg) for transcription.
    ///
    /// Issue #126: WASAPI loopback delivers NO capture callbacks while the render endpoint is idle
    /// (nothing playing), so silent stretches would be missing entirely and the recorded timeline
    /// would collapse - audible content would slide to the front and mix onto the video at the
    /// wrong time. To keep the timeline intact, a silent keep-alive stream is played on the same
    /// endpoint for the whole capture, which keeps the audio engine active so loopback delivers
    /// continuous buffers (real silence included).
    /// </summary>
    internal sealed class LoopbackCapture : IDisposable
    {
        private readonly WasapiLoopbackCapture _capture;
        private readonly ManualResetEventSlim _stopped = new(false);
        private WaveFileWriter? _writer;
        private IWavePlayer? _keepAlive;
        private bool _isRecording;

        public WaveFormat Format => _capture.WaveFormat;

        /// <summary>Peak amplitude of the most recent buffer, 0.0 - 1.0.</summary>
        public event Action<float>? LevelChanged;

        public LoopbackCapture()
        {
            _capture = new WasapiLoopbackCapture(); // default render device
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public void Start(string wavPath)
        {
            if (_isRecording) return;
            _writer = new WaveFileWriter(wavPath, _capture.WaveFormat);
            _stopped.Reset();
            // Start the silence keep-alive BEFORE the capture so the endpoint is already active
            // when the first loopback buffer is requested (issue #126).
            StartSilenceKeepAlive();
            _isRecording = true;
            _capture.StartRecording();
        }

        /// <summary>
        /// Play inaudible silence on the default render endpoint to keep the audio engine streaming,
        /// so WASAPI loopback delivers continuous buffers even when no other app is producing sound.
        /// Best-effort: if it cannot start, capture still works but idle-silence stretches may be lost.
        /// </summary>
        private void StartSilenceKeepAlive()
        {
            try
            {
                var player = new WasapiOut(AudioClientShareMode.Shared, 200);
                player.Init(new SilenceProvider(_capture.WaveFormat));
                player.Play();
                _keepAlive = player;
            }
            catch (Exception ex)
            {
                Log.Warn($"loopback silence keep-alive unavailable ({ex.Message}); "
                    + "idle-silence stretches may collapse the system-audio timeline");
            }
        }

        private void StopSilenceKeepAlive()
        {
            try { _keepAlive?.Stop(); } catch { }
            _keepAlive?.Dispose();
            _keepAlive = null;
        }

        public void Stop()
        {
            if (!_isRecording) return;
            _isRecording = false;
            _capture.StopRecording();
            // Issue #125: StopRecording() is asynchronous - the final DataAvailable buffers arrive
            // on the capture thread and RecordingStopped fires only after they have all been
            // delivered. Wait for that event (bounded) instead of a fixed sleep so the tail is not
            // dropped; the writer is flushed/disposed in the RecordingStopped handler.
            if (!_stopped.Wait(TimeSpan.FromSeconds(2)))
                Log.Warn("loopback stop timed out waiting for final buffers; flushing what arrived");
            // Stop the keep-alive only after the capture has drained, so the tail is not cut short.
            StopSilenceKeepAlive();
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _writer?.Flush();
            _stopped.Set();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);

            // Mix format is 32-bit IEEE float.
            float peak = 0f;
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                float v = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                if (v > peak) peak = v;
            }
            LevelChanged?.Invoke(peak);
        }

        public void Dispose()
        {
            if (_isRecording)
            {
                try { _capture.StopRecording(); } catch { }
            }
            StopSilenceKeepAlive();
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _writer?.Dispose();
            _stopped.Dispose();
        }
    }
}
