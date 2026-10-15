using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// One live PulseAudio capture (issue #102): an ffmpeg process reading a pulse source and writing
    /// raw signed 16-bit little-endian PCM to its stdout, read here in ~50 ms blocks. It is what the
    /// Linux AudioCapture (a microphone) and LoopbackCapture (the default sink's monitor) are built on.
    ///
    /// Stop is clean, not a kill: 'q' on ffmpeg's stdin makes it flush what it has read and exit,
    /// and the reader drains the pipe to its end before Stop returns, so the tail of the take is kept.
    /// </summary>
    internal sealed class PulseStream : IDisposable
    {
        private readonly string _source;
        private readonly string _label;
        private readonly StringBuilder _stderr = new();
        private Process? _proc;
        private Thread? _reader;
        private volatile bool _stopping;

        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>One block of PCM: the buffer and how many of its bytes are valid.</summary>
        public event Action<byte[], int>? Data;

        public PulseStream(string pulseSource, int sampleRate, int channels, string label)
        {
            if (string.IsNullOrWhiteSpace(pulseSource)) throw new ArgumentException("a pulse source name is required", nameof(pulseSource));
            _source = pulseSource;
            SampleRate = sampleRate;
            Channels = channels;
            _label = label;
        }

        /// <summary>The ffmpeg arguments for this capture. Pure, unit-tested.</summary>
        internal static string[] Arguments(string source, int sampleRate, int channels) => new[]
        {
            "-hide_banner", "-nostats", "-loglevel", "warning",
            "-f", "pulse",
            // Small fragments keep the device-side buffer short, so a stop loses at most a fragment
            // (the issue #125 lesson from dshow's -audio_buffer_size, applied to pulse).
            "-fragment_size", ((sampleRate * channels * 2) / 50).ToString(),   // ~20 ms
            "-i", source,
            "-ac", channels.ToString(),
            "-ar", sampleRate.ToString(),
            "-f", "s16le",
            "pipe:1",
        };

        public void Start()
        {
            if (_proc != null) throw new InvalidOperationException("this pulse stream was already started");
            string exe = FfmpegLocator.Ffmpeg();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in Arguments(_source, SampleRate, Channels)) psi.ArgumentList.Add(a);
            Log.Info($"[PulseStream] Start {_label}: {FfmpegArgs.ToCommandLine(exe, psi.ArgumentList)}");

            var p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
            if (!p.Start()) throw new UsageException($"failed to start ffmpeg for {_label}.");
            _proc = p;
            p.BeginErrorReadLine();

            var stdout = p.StandardOutput.BaseStream;
            _reader = new Thread(() => ReadLoop(stdout)) { IsBackground = true, Name = "AgentEyes-pulse-" + _label };
            _reader.Start();

            // Surface an immediate failure (an unknown source, no pulse server) at the start, as the
            // video recorder does, rather than as a silent empty file at the stop.
            Thread.Sleep(300);
            if (p.HasExited)
            {
                _reader.Join(2000);
                string err;
                lock (_stderr) err = _stderr.ToString().Trim();
                Log.Error($"[PulseStream] Start {_label} FAILED: ffmpeg exited (code {p.ExitCode}): {err}");
                throw new UsageException($"{_label}: ffmpeg could not open pulse source \"{_source}\" (exit {p.ExitCode}): {err}");
            }
        }

        private void ReadLoop(Stream stdout)
        {
            // 50 ms of audio per block, as the Windows WaveIn buffers are.
            var buf = new byte[Math.Max(1024, SampleRate * Channels * 2 / 20)];
            try
            {
                while (true)
                {
                    int filled = 0;
                    while (filled < buf.Length)
                    {
                        int n = stdout.Read(buf, filled, buf.Length - filled);
                        if (n <= 0) break;
                        filled += n;
                    }
                    if (filled > 0)
                    {
                        // keep whole samples only; a trailing odd byte would shift every later sample
                        int whole = filled - (filled % (2 * Channels));
                        if (whole > 0) Data?.Invoke(buf, whole);
                    }
                    if (filled < buf.Length) break;   // end of stream
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (!_stopping) Log.Error($"[PulseStream] {_label}: the PCM pipe failed while recording", ex);
            }
            catch (Exception ex)
            {
                Log.Error($"[PulseStream] {_label}: a level/writer subscriber FAILED - the capture stopped reading", ex);
            }
            Log.Info($"[PulseStream] {_label}: reader finished (stopping={_stopping})");
        }

        public void Stop()
        {
            var p = _proc;
            if (p == null || _stopping) return;
            _stopping = true;
            Log.Info($"[PulseStream] Stop {_label}: asking ffmpeg to finish");
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Write("q");
                    p.StandardInput.Flush();
                }
            }
            catch (IOException ex)
            {
                Log.Warn($"[PulseStream] Stop {_label}: ffmpeg stdin already closed ({ex.Message})");
            }

            if (!p.WaitForExit(5000))
            {
                Log.Warn($"[PulseStream] Stop {_label}: ffmpeg did not finish within 5s - killing it");
                p.Kill(true);
                p.WaitForExit(3000);
            }
            if (_reader != null && !_reader.Join(5000))
                Log.Warn($"[PulseStream] Stop {_label}: the reader did not drain within 5s");
            Log.Info($"[PulseStream] Stop {_label}: done (ffmpeg exit={(p.HasExited ? p.ExitCode.ToString() : "running")})");
        }

        public void Dispose()
        {
            try { Stop(); }
            catch (Exception ex) { Log.Error($"[PulseStream] Dispose {_label}: stop FAILED", ex); }
            _proc?.Dispose();
        }

        /// <summary>Peak and energy of one s16le block, as the Windows capture reports them.</summary>
        internal static AudioLevel Measure(byte[] buffer, int count)
        {
            float peak = 0f;
            double sumSquares = 0.0;
            int samples = 0;
            for (int i = 0; i + 1 < count; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                // widen first: Math.Abs(short.MinValue) overflows, and -32768 is a real full-scale sample
                float a = Math.Abs((int)sample) / 32768f;
                if (a > peak) peak = a;
                sumSquares += (double)a * a;
                samples++;
            }
            return new AudioLevel(peak, sumSquares, samples);
        }
    }
}
