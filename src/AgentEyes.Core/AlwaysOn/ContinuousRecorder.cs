using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// The always-on capture itself (issue #66): ONE long-lived ffmpeg writing one-minute pieces, the
    /// system sound streamed into it through a named pipe, and the level meters that feed the sound
    /// log. It never stops to look for silence - the keeper decides about pieces after the fact.
    ///
    /// It does not restart itself. When ffmpeg dies (a display change, a lost device), <see cref="HasExited"/>
    /// goes true and the engine's supervisor starts a new one - one owner for restart policy.
    /// </summary>
    internal sealed class ContinuousRecorder : IDisposable
    {
        private static int _pipeCounter;
        private static string? _encoderCache;
        private static readonly object EncoderGate = new();

        private readonly StringBuilder _stderr = new();
        private Process? _proc;
        private NamedPipeServerStream? _pipe;
        private PipeFeeder? _feeder;

        /// <summary>How much system sound may queue for ffmpeg before it counts as stalled.</summary>
        public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(10);
        private LoopbackCapture? _loop;
        private AudioCapture? _mic;
        private bool _stopped;

        public string CommandLine { get; private set; } = "";
        public string Encoder { get; private set; } = "";
        public DateTime StartedUtc { get; private set; }

        /// <summary>True when ffmpeg has ended without being asked to - or is alive but has stopped
        /// taking the system sound, which is the same thing to the supervisor: restart it.</summary>
        public bool HasExited => _proc != null && !_stopped && (_proc.HasExited || _feeder?.Stalled == true);

        /// <summary>The last few hundred characters ffmpeg wrote, for an actionable error.</summary>
        public string StderrTail
        {
            get
            {
                lock (_stderr)
                {
                    string s = _stderr.ToString();
                    return s.Length <= 800 ? s : "..." + s.Substring(s.Length - 800);
                }
            }
        }

        /// <summary>
        /// The best encoder this machine can run, found by a one-second test encode of each in
        /// <see cref="AlwaysOnArgs.EncoderPreference"/> order and remembered for the process.
        /// libx264 is compiled into every ffmpeg AgentEyes ships, so the list always ends in one that
        /// works; if even it fails, ffmpeg itself is broken and that is reported, not hidden.
        /// </summary>
        public static string ChooseEncoder()
        {
            lock (EncoderGate)
            {
                if (_encoderCache != null) return _encoderCache;
                foreach (var enc in AlwaysOnArgs.EncoderPreference)
                {
                    try
                    {
                        Ffmpeg.Run(AlwaysOnArgs.EncoderProbe(enc), $"always-on encoder probe {enc}");
                        Log.Info($"[ContinuousRecorder] ChooseEncoder: {enc} works on this machine - using it");
                        _encoderCache = enc;
                        return enc;
                    }
                    catch (UsageException ex)
                    {
                        Log.Info($"[ContinuousRecorder] ChooseEncoder: {enc} is not available here ({ex.Message.Split('\n')[0]})");
                    }
                }
                throw new UsageException(
                    "no video encoder works on this machine, not even libx264 - the bundled ffmpeg is broken. "
                    + "Reinstall AgentEyes (agenteyes-setup install) to restore it.");
            }
        }

        /// <summary>Start the capture. Throws with the ffmpeg error when it cannot start.</summary>
        public void Start(AlwaysOnOptions o, SoundLog sound)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (sound == null) throw new ArgumentNullException(nameof(sound));
            Directory.CreateDirectory(o.PieceFolder);
            Encoder = ChooseEncoder();

            // System sound: recorded when the preset records it, listened to when it counts. One
            // loopback serves both - streamed to ffmpeg, metered either way.
            bool needLoopback = o.RecordSystem || sound.Listens(SoundSource.System);
            PipeAudioFormat? sysFormat = null;
            string? pipePath = null;
            if (needLoopback)
            {
                _loop = new LoopbackCapture();
                var wf = _loop.Format;
                if (wf.BitsPerSample != 32)
                    throw new UsageException(
                        $"the default playback device delivers {wf.BitsPerSample}-bit audio; always-on streams "
                        + "32-bit float. Set the device to its default format in Windows Sound settings.");
                if (sound.Listens(SoundSource.System))
                    _loop.LevelChanged += p => sound.Observe(SoundSource.System, DateTime.UtcNow, p);
                if (o.RecordSystem)
                {
                    sysFormat = new PipeAudioFormat(wf.SampleRate, wf.Channels);
                    string name = $"agenteyes-alwayson-{Environment.ProcessId}-{Interlocked.Increment(ref _pipeCounter)}";
                    _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 1 << 20);
                    pipePath = $@"\\.\pipe\{name}";
                }
            }

            var args = AlwaysOnArgs.Capture(
                o.Capture, o.Desktop, o.Fps, Encoder, o.DshowMic, o.MicGain, pipePath, sysFormat, o.SystemGain,
                o.PieceSeconds, o.PieceFolder);
            string exe = FfmpegLocator.Ffmpeg();
            CommandLine = FfmpegArgs.ToCommandLine(exe, args);
            Log.Info($"[ContinuousRecorder] Start: {CommandLine}");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Piece names come from ffmpeg's -strftime, which uses the process's local time. In UTC
            // they are unambiguous: local names repeat an hour at the autumn clock change, and ffmpeg
            // would overwrite that hour's pieces (review finding 2).
            psi.Environment["TZ"] = "UTC0";
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostats");
            foreach (var a in args) psi.ArgumentList.Add(a);

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderr)
                {
                    _stderr.AppendLine(e.Data);
                    // All day: keep the tail, not the whole day.
                    if (_stderr.Length > 64_000) _stderr.Remove(0, _stderr.Length - 32_000);
                }
            };
            _proc.OutputDataReceived += (_, _) => { };
            if (!_proc.Start()) throw new UsageException("failed to start ffmpeg for always-on recording.");
            StartedUtc = DateTime.UtcNow;
            try
            {
                // An all-day capture must never outlive AgentEyes (a crash, Task Manager).
                KillOnCloseJob.Assign(_proc);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                StopProcess();
                StopAudio();
                throw new UsageException($"always-on cannot start: {ex.Message} ({ex.NativeErrorCode}).");
            }
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();

            if (_pipe != null)
            {
                // ffmpeg opens its inputs in order and blocks on the pipe until it connects; the
                // loopback must start writing right after, or ffmpeg's probe of the input waits.
                var connect = _pipe.WaitForConnectionAsync();
                if (!connect.Wait(TimeSpan.FromSeconds(15)))
                {
                    string err = StderrTail;
                    StopProcess();
                    throw new UsageException(
                        "always-on ffmpeg did not open the system-sound pipe within 15 seconds - it most "
                        + "likely failed on an earlier input (the screen or the microphone). ffmpeg said: " + err);
                }
                Log.Info("[ContinuousRecorder] Start: ffmpeg connected to the system-sound pipe");
                long bytesPerSecond = (long)sysFormat!.SampleRate * sysFormat.Channels * 4;
                _feeder = new PipeFeeder(_pipe, (long)(bytesPerSecond * StallAfter.TotalSeconds));
            }
            _loop?.StartToStream(_feeder);

            // The microphone: ffmpeg owns it in the recording, so its level is read by a second,
            // shared-mode capture that writes no file - the same arrangement a normal video recording
            // uses for its meter.
            if (sound.Listens(SoundSource.Mic))
            {
                if (string.IsNullOrWhiteSpace(o.MicLevelDevice))
                    throw new UsageException(
                        "the microphone's sound counts but no microphone is set - pick a recording setup with a "
                        + "microphone, or set Windows' default microphone.");
                _mic = new AudioCapture(AudioCapture.ResolveDevice(o.MicLevelDevice));
                _mic.LevelChanged += p => sound.Observe(SoundSource.Mic, DateTime.UtcNow, p);
                _mic.StartMonitor();
            }

            // Surface an immediate failure (a monitor that no longer exists, a mic that will not
            // open) as an error now rather than as a supervisor restart loop later.
            Thread.Sleep(1500);
            if (_proc.HasExited)
            {
                string err = StderrTail;
                StopAudio();
                throw new UsageException($"always-on ffmpeg exited immediately (code {_proc.ExitCode}): {err}");
            }
            Log.Info($"[ContinuousRecorder] Start: recording, encoder={Encoder} pieces={o.PieceFolder}");
        }

        /// <summary>Stop the capture and let ffmpeg finish the piece it is writing.</summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            Log.Info("[ContinuousRecorder] Stop: finishing the current piece");
            StopProcess();
            StopAudio();
            Log.Info("[ContinuousRecorder] Stop: stopped");
        }

        private void StopProcess()
        {
            if (_proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    try
                    {
                        _proc.StandardInput.Write("q");
                        _proc.StandardInput.Flush();
                    }
                    catch (IOException) { /* stdin already closed; the wait below decides */ }
                    // The pipe input would keep ffmpeg reading; closing it ends that input.
                    try { _pipe?.Dispose(); } catch (IOException) { }
                    if (!_proc.WaitForExit(15000))
                    {
                        Log.Warn("[ContinuousRecorder] StopProcess: ffmpeg did not finish within 15s - killing it; "
                                 + "the piece it was writing is lost");
                        _proc.Kill(true);
                        _proc.WaitForExit(5000);
                    }
                }
            }
            catch (InvalidOperationException) { /* never started */ }
        }

        private void StopAudio()
        {
            try { _loop?.Stop(); } catch (Exception ex) { Log.Error("[ContinuousRecorder] StopAudio: loopback stop failed", ex); }
            try { _loop?.Dispose(); } catch (Exception ex) { Log.Error("[ContinuousRecorder] StopAudio: loopback dispose failed", ex); }
            _loop = null;
            try { _mic?.Stop(); } catch (Exception ex) { Log.Error("[ContinuousRecorder] StopAudio: mic stop failed", ex); }
            try { _mic?.Dispose(); } catch (Exception ex) { Log.Error("[ContinuousRecorder] StopAudio: mic dispose failed", ex); }
            _mic = null;
            try { _pipe?.Dispose(); } catch (IOException) { }
            _pipe = null;
            _feeder?.Dispose();
            _feeder = null;
        }

        public void Dispose()
        {
            Stop();
            _proc?.Dispose();
        }
    }
}
