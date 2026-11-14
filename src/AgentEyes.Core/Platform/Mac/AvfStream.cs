using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.Platform.Mac
{
    /// <summary>
    /// One live AVFoundation audio capture: an ffmpeg process reading an audio input and writing raw
    /// signed 16-bit little-endian PCM to its stdout, read here in ~50 ms blocks. It is what the
    /// macOS AudioCapture (a microphone) is built on, and it is the twin of the Linux PulseStream.
    ///
    /// Stop is clean, not a kill: 'q' on ffmpeg's stdin makes it flush what it has read and exit, and
    /// the reader drains the pipe to its end before Stop returns, so the tail of the take is kept.
    /// </summary>
    internal sealed class AvfStream : IDisposable
    {
        private readonly int _avfIndex;
        private readonly string _label;
        private readonly StringBuilder _stderr = new();
        private Process? _proc;
        private Thread? _reader;
        private volatile bool _stopping;

        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>
        /// The capture clock timestamp of the FIRST sample this stream's ffmpeg read from the device,
        /// as ffmpeg reports it, or null when the log has not said. On macOS it is a CoreMedia
        /// host-clock reading, the same clock FfmpegRecorder.InputStartSeconds is on, so the two
        /// subtract to the exact offset between this stream's first sample and the recording's first
        /// video frame (issue #114).
        ///
        /// Microseconds in a long, published with Interlocked, for the same reason as
        /// FfmpegRecorder.InputStartSeconds: written on ffmpeg's stderr thread, read at the stop,
        /// and a nullable double is neither one field nor atomically written.
        /// </summary>
        public double? InputStartSeconds
        {
            get
            {
                long micros = Interlocked.Read(ref _inputStartMicros);
                return micros < 0 ? null : micros / 1_000_000.0;
            }
        }

        /// <summary>Backing store for <see cref="InputStartSeconds"/>; negative means "not reported".</summary>
        private long _inputStartMicros = -1;

        /// <summary>One block of PCM: the buffer and how many of its bytes are valid.</summary>
        public event Action<byte[], int>? Data;

        public AvfStream(int avfAudioIndex, int sampleRate, int channels, string label)
        {
            if (avfAudioIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(avfAudioIndex), "an AVFoundation index is zero or above");
            _avfIndex = avfAudioIndex;
            SampleRate = sampleRate;
            Channels = channels;
            _label = label;
        }

        /// <summary>
        /// The ffmpeg arguments for this capture. Pure, unit-tested.
        ///
        /// NO DEVICE-BUFFER OPTION, BECAUSE AVFOUNDATION HAS NONE (issue #111). This list used to
        /// carry dshow's buffer-length option, described here as "the AVFoundation form of the issue
        /// #125 lesson". That belief was wrong: the option belongs to DIRECTSHOW alone, AVFoundation
        /// has never had a buffer-length option under that or any other name, and ffmpeg therefore
        /// answered "Unrecognized option" and exited before opening the device - so the mic level
        /// meter and the system-audio pipe never opened on a Mac at all. pulse's -fragment_size on
        /// Linux is real; this had no macOS counterpart to keep. Do not put one back: the guard in
        /// MacForeignFfmpegOptionTests fails if a foreign option reappears. (The name is left
        /// unspelled on purpose - see the same note on FfmpegArgs.AvfAudioInput.)
        /// </summary>
        internal static string[] Arguments(int avfAudioIndex, int sampleRate, int channels) => new[]
        {
            // -loglevel info, NOT warning, and -nostats so that is all it costs (issue #114). ffmpeg
            // prints the input dump - the line carrying this capture's start time on the machine's
            // shared capture clock - at info level, and on macOS that timestamp is the only thing
            // that can align this WAV with the screen recording's video, which is a different ffmpeg
            // process. With -nostats there is no progress spam, so the whole block is ~15 lines.
            "-hide_banner", "-nostats", "-loglevel", "info",
            "-f", "avfoundation",
            // KEEP DRAINING THE DEVICE WHILE THIS END IS BUSY (issue #114). ffmpeg's avfoundation
            // demuxer holds exactly ONE sample buffer per device and silently discards whatever
            // arrives before the reader takes it, and this capture's reader is at the far end of a
            // pipe inside a .NET service that also transcribes: a garbage collection or a few
            // seconds of memory pressure stalls the pipe, ffmpeg blocks on the write, and the samples
            // that arrive meanwhile are gone with nothing logged. Measured while the service was
            // transcribing its backlog: a 60.05 s capture kept 25.15 s - the same shape of loss this
            // issue is about, on the other side of the same one-buffer demuxer.
            //
            // A deep input queue decouples the two: the demuxer thread keeps taking buffers off the
            // device into the queue while the output is blocked. At roughly 4096 samples per buffer
            // this is minutes of slack for a stall measured in seconds. It is the same guard, with the
            // same value, that the screen capture has carried from the start - this input simply
            // never had it. It is a generic ffmpeg input option, not a foreign demuxer's
            // (MacForeignFfmpegOptionTests still sweeps this list for those).
            "-thread_queue_size", "1024",
            "-i", ":" + avfAudioIndex.ToString(CultureInfo.InvariantCulture),
            "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le",
            "pipe:1",
        };

        public void Start()
        {
            if (_proc != null) throw new InvalidOperationException("this AVFoundation stream was already started");
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
            foreach (var a in Arguments(_avfIndex, SampleRate, Channels)) psi.ArgumentList.Add(a);
            Log.Info($"[AvfStream] Start {_label}: {FfmpegArgs.ToCommandLine(exe, psi.ArgumentList)}");

            var p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderr) _stderr.AppendLine(e.Data);
                if (Interlocked.Read(ref _inputStartMicros) < 0)
                {
                    double? start = FfmpegRecorder.ParseInputStartSeconds(e.Data);
                    if (start is > 0)
                    {
                        Interlocked.Exchange(
                            ref _inputStartMicros, (long)Math.Round(start.Value * 1_000_000.0));
                        Log.Info($"[AvfStream] {_label}: capture clock start {start.Value:F6}s");
                    }
                }
            };
            if (!p.Start()) throw new UsageException($"failed to start ffmpeg for {_label}.");
            _proc = p;
            p.BeginErrorReadLine();

            var stdout = p.StandardOutput.BaseStream;
            _reader = new Thread(() => ReadLoop(stdout)) { IsBackground = true, Name = "AgentEyes-avf-" + _label };
            _reader.Start();

            // Surface an immediate failure (a wrong index, a refused Microphone permission) at the
            // start, as the video recorder does, rather than as a silent empty file at the stop.
            Thread.Sleep(300);
            if (p.HasExited)
            {
                _reader.Join(2000);
                string err;
                lock (_stderr) err = _stderr.ToString().Trim();
                Log.Error($"[AvfStream] Start {_label} FAILED: ffmpeg exited (code {p.ExitCode}): {err}");
                throw new UsageException(
                    $"{_label}: ffmpeg could not open AVFoundation audio input :{_avfIndex} "
                    + $"(exit {p.ExitCode}): {err}. If this Mac has the microphone but macOS has not "
                    + "been asked, grant Microphone access in System Settings > Privacy and Security.");
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
                if (!_stopping) Log.Error($"[AvfStream] {_label}: the PCM pipe failed while recording", ex);
            }
            catch (Exception ex)
            {
                Log.Error($"[AvfStream] {_label}: a level/writer subscriber FAILED - the capture stopped reading", ex);
            }
            Log.Info($"[AvfStream] {_label}: reader finished (stopping={_stopping})");
        }

        public void Stop()
        {
            var p = _proc;
            if (p == null || _stopping) return;
            _stopping = true;
            Log.Info($"[AvfStream] Stop {_label}: asking ffmpeg to finish");
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
                Log.Warn($"[AvfStream] Stop {_label}: ffmpeg stdin already closed ({ex.Message})");
            }

            if (!p.WaitForExit(5000))
            {
                Log.Warn($"[AvfStream] Stop {_label}: ffmpeg did not finish within 5s - killing it");
                p.Kill(true);
                p.WaitForExit(3000);
            }
            if (_reader != null && !_reader.Join(5000))
                Log.Warn($"[AvfStream] Stop {_label}: the reader did not drain within 5s");

            // WHAT FFMPEG ITSELF SAYS IT CAPTURED. Without this, a capture that silently delivered a
            // fraction of its samples looked identical in the log to one that delivered all of them -
            // the only number recorded was how many bytes came out of the pipe, which cannot tell
            // "the device gave us less" apart from "we read less". ffmpeg's closing summary carries
            // its own size= and time=, so the two sides can be compared whenever they disagree.
            string report;
            lock (_stderr) report = _stderr.ToString().Trim();
            if (report.Length > 0)
            {
                var lines = report.Split('\n');
                int from = Math.Max(0, lines.Length - 4);
                Log.Info($"[AvfStream] Stop {_label}: ffmpeg reported "
                         + string.Join(" | ", lines[from..]).Replace("\r", "").Trim());
            }
            Log.Info($"[AvfStream] Stop {_label}: done (ffmpeg exit={(p.HasExited ? p.ExitCode.ToString() : "running")})");
        }

        public void Dispose()
        {
            try { Stop(); }
            catch (Exception ex) { Log.Error($"[AvfStream] Dispose {_label}: stop FAILED", ex); }
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
