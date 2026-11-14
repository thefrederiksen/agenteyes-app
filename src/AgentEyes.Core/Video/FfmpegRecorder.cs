using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Drawing = System.Drawing;

namespace AgentEyes.Video
{
    /// <summary>
    /// Screen-video + microphone recorder backed by a single ffmpeg process (gdigrab + dshow -> MP4).
    /// Stop() sends 'q' to ffmpeg's stdin so the MP4 is finalized cleanly (killing it truncates the file).
    /// </summary>
    internal sealed class FfmpegRecorder : IScreenRecorder
    {
        private readonly Process _proc;
        private readonly StringBuilder _stderr = new();
        private readonly string _logPath;
        private bool _stopped;

        /// <summary>Wall clock since the capture started, used to know how much take we owe the file.</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>
        /// The output position ffmpeg last reported (its "time=" progress field), in milliseconds.
        /// This trails wall time by the capture pipeline's latency - roughly a second in practice -
        /// and that gap is exactly the audio that has been spoken but not yet muxed (issue #22).
        /// </summary>
        private long _mediaMs;

        /// <summary>How long Stop() will wait for the pipeline to catch up before quitting anyway.</summary>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        public string OutputPath { get; }
        public string CommandLine { get; }

        /// <summary>
        /// When the ffmpeg process was actually started, captured the instant Process.Start returned
        /// (issue #28). The camera track's CameraStartOffsetSeconds is measured against this - an
        /// alignment HINT of tens of milliseconds between the two independent captures, not
        /// frame-accurate genlock (assumption A5). Set only by <see cref="Start"/>.
        /// </summary>
        public DateTime StartedUtc { get; private set; }

        private FfmpegRecorder(Process proc, string outputPath, string commandLine, string logPath)
        {
            _proc = proc;
            OutputPath = outputPath;
            CommandLine = commandLine;
            _logPath = logPath;
        }

        /// <param name="preview">Issue #33: the HUD's live screen preview, or null for none. When one
        /// is supplied ffmpeg gains a second, small MJPEG output on its STDOUT and this hands the tap
        /// that pipe. The tap drains it unconditionally for the life of the process - an anonymous
        /// pipe nobody reads fills, and a full pipe would block the ffmpeg writing the recording, so
        /// the preview output is added ONLY when there is a tap to drain it.</param>
        /// <param name="screenNumber">The one-based monitor this region belongs to. Windows and Linux
        /// address a region by its position in one virtual desktop and ignore this; macOS opens ONE
        /// display by index and crops inside it, so there it is the display being recorded. It
        /// defaults to the main display so the Windows and Linux call sites are unchanged.</param>
        public static FfmpegRecorder Start(
            Drawing.Rectangle capture, string? dshowMicName, int fps, int crf, string outPath,
            Preview.PreviewTap? preview = null, int screenNumber = 1)
        {
            string exe = FfmpegLocator.Ffmpeg();
            // Pass the virtual-desktop bounds so an oversized social-format region is grabbed clamped
            // and padded back to its exact size instead of failing gdigrab (issue #69).
#if WINDOWS
            var args = FfmpegArgs.VideoCapture(
                capture, dshowMicName, fps, crf, outPath, Monitors.VirtualBounds(), previewStream: preview != null);
#elif MACOS
            // AVFoundation opens ONE display by index and the region is a crop inside it, so the
            // capture needs that display's index and size rather than a desktop offset. The screen is
            // resolved from the region: a Mac region is display-local, and RequireScreen pairs the
            // index ffmpeg wants with the size the crop is clamped into.
            int? avfMic = dshowMicName == null
                ? null
                : Platform.Mac.AvfDevices.RequireMicrophoneIndex(dshowMicName);
            var (avfScreen, macScreen) = Platform.Mac.MacDisplay.RequireScreen(screenNumber);
            var args = FfmpegArgs.AvfVideoCapture(
                capture, avfScreen, new Drawing.Size(macScreen.Width, macScreen.Height), avfMic,
                fps, crf, outPath, previewStream: preview != null);
#else
            // Issue #102: x11grab + pulse. The microphone arrives by its listed description (what
            // DeviceResolver matched) and is opened by its pulse source name.
            string? pulseMic = dshowMicName == null ? null : Platform.Linux.PulseDevices.RequireSourceName(dshowMicName);
            var args = FfmpegArgs.X11VideoCapture(
                capture, Platform.Linux.LinuxDisplay.Require(), pulseMic, fps, crf, outPath,
                Monitors.VirtualBounds(), previewStream: preview != null);
#endif
            string cmd = FfmpegArgs.ToCommandLine(exe, args);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            string logPath = outPath + ".ffmpeg.log";
            var rec = new FfmpegRecorder(proc, outPath, cmd, logPath);

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                rec._stderr.AppendLine(e.Data);
                // ffmpeg writes its progress with a carriage return, which .NET treats as a line
                // break, so each "time=" tick arrives here as its own line.
                long ms = ParseProgressMs(e.Data);
                if (ms >= 0) System.Threading.Interlocked.Exchange(ref rec._mediaMs, ms);
            };
            // Stdout carries the preview stream when there is a tap, and nothing at all when there is
            // not. The two readers are mutually exclusive by construction: BeginOutputReadLine takes
            // the stream for itself, so wiring both would leave the preview reading a closed pipe.
            if (preview == null) proc.OutputDataReceived += (_, _) => { };

            if (!proc.Start())
            {
                throw new UsageException("failed to start ffmpeg.");
            }
            rec.StartedUtc = DateTime.UtcNow;
            proc.BeginErrorReadLine();
            if (preview == null) proc.BeginOutputReadLine();
            else preview.Pump(proc.StandardOutput.BaseStream);

            // Give ffmpeg a moment to initialize the capture; surface an early crash clearly.
            Thread.Sleep(400);
            if (proc.HasExited && proc.ExitCode != 0)
            {
                string err = rec._stderr.ToString();
                File.WriteAllText(logPath, err);
                throw new UsageException(
                    $"ffmpeg exited immediately (code {proc.ExitCode}). See {logPath}. " +
                    "Likely cause: " + DiagnoseImmediateExit(err, dshowMicName));
            }

            return rec;
        }

        /// <summary>Stop recording, finalize the MP4, and return its duration (best-effort).</summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            // Issue #22 / reopens #125: the capture pipeline runs about a second behind wall time,
            // so at the instant the user stops there is roughly a second of already-spoken audio
            // that ffmpeg has read but not yet muxed. Quitting immediately throws it away, which is
            // why takes lost 0.5-2.4s off the END - on a narrated recording, the closing sentence.
            //
            // Shrinking -audio_buffer_size (the #125 mitigation) only reduced the lag; it cannot
            // remove it. So instead of guessing a delay, wait for ffmpeg's own "time=" to reach the
            // moment the user asked to stop. That is self-tuning: a fast machine waits briefly, a
            // slow one waits longer, and neither loses the end of the take.
            long stopAtMs = _clock.ElapsedMilliseconds;
            var drain = Stopwatch.StartNew();
            bool caughtUp = false;
            while (drain.Elapsed < DrainTimeout)
            {
                if (_proc.HasExited) break;
                if (System.Threading.Interlocked.Read(ref _mediaMs) >= stopAtMs) { caughtUp = true; break; }
                Thread.Sleep(20);
            }
            drain.Stop();

            if (caughtUp)
            {
                Log.Info($"[FfmpegRecorder] Stop: pipeline drained in {drain.ElapsedMilliseconds}ms " +
                         $"(take {stopAtMs}ms) - full take captured");
            }
            else
            {
                Log.Warn($"[FfmpegRecorder] Stop: pipeline did not reach {stopAtMs}ms within " +
                         $"{DrainTimeout.TotalSeconds:0.#}s (reached " +
                         $"{System.Threading.Interlocked.Read(ref _mediaMs)}ms) - the end of this " +
                         $"recording may be truncated. See {_logPath}");
            }

            try
            {
                _proc.StandardInput.Write("q");
                _proc.StandardInput.Flush();
            }
            catch
            {
                // stdin may already be closed; fall through to wait/kill.
            }

            if (!_proc.WaitForExit(8000))
            {
                try { _proc.Kill(true); } catch { }
                _proc.WaitForExit(3000);
            }

            File.WriteAllText(_logPath, _stderr.ToString());
        }

        /// <summary>
        /// Read the output position out of one ffmpeg progress line ("... time=00:00:07.28 ...") and
        /// return it in milliseconds, or -1 when the line carries no timestamp. Pure, so the drain
        /// gate that depends on it can be tested without launching ffmpeg (issue #22).
        /// </summary>
        internal static long ParseProgressMs(string line)
        {
            if (string.IsNullOrEmpty(line)) return -1;
            int i = line.IndexOf("time=", StringComparison.Ordinal);
            if (i < 0) return -1;
            i += 5;

            // HH:MM:SS.ss - ffmpeg also emits "time=N/A" while it is still starting up.
            int end = i;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            string v = line.Substring(i, end - i);
            var parts = v.Split(':');
            if (parts.Length != 3) return -1;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out int h)) return -1;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out int m)) return -1;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out double s)) return -1;
            if (h < 0 || m < 0 || s < 0) return -1;

            // Round rather than truncate: 8.11 seconds is 8109.999... in binary, and casting would
            // shave a millisecond off every tick.
            return (long)Math.Round((h * 3600 + m * 60 + s) * 1000.0);
        }

        /// <summary>
        /// Turn ffmpeg's stderr into an accurate, actionable cause for an immediate exit. The
        /// region-out-of-bounds case is NOT the mic - the region-clamp/pad path (issue #69) normally
        /// prevents it, but if gdigrab still reports it we say so; the mic is only blamed when a mic
        /// was actually requested. Pure string inspection - safe to unit test.
        ///
        /// A REJECTED OPTION IS NOT A MISSING DEVICE (issue #111). ffmpeg prints "Unrecognized option
        /// 'x'." / "Option not found" and exits before it opens anything, which is OUR argument list
        /// being wrong, not the user's device: when a macOS build carried the DirectShow-only
        /// -audio_buffer_size, this method answered that the microphone "may not match a DirectShow
        /// device" and sent every reader after the device name on a machine that has no DirectShow at
        /// all. That case now says what the log says. The device cause also names the API this build
        /// actually uses (<see cref="PlatformApiNames"/>) rather than always DirectShow.
        /// </summary>
        internal static string DiagnoseImmediateExit(string stderr, string? dshowMicName)
        {
            stderr ??= "";
            if (stderr.Contains("extends outside window area", StringComparison.OrdinalIgnoreCase))
                return "the capture region extends past the desktop bounds.";
            if (stderr.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Option not found", StringComparison.OrdinalIgnoreCase))
                return "ffmpeg rejected an option in the capture command line - see the log, "
                    + "the device is not the problem.";
            if (!string.IsNullOrWhiteSpace(dshowMicName))
                // "any <API> input device" rather than "a <API> device": the article was only ever
                // right for the one hardcoded word, and "a AVFoundation device" is not English.
                return $"the microphone \"{dshowMicName}\" may not match any {PlatformApiNames.Audio} input device.";
            return "see the log for the ffmpeg error.";
        }

        public bool HasExited => _proc.HasExited;

        // ---- issue #113: what the capture watchdog reads ------------------------------------
        //
        // Additive only. The drain, the stop and the media clock are deliberately untouched here:
        // issue #114 reworks them, and the watchdog needs nothing from inside them.

        /// <summary>
        /// The capture process's exit code once it has ended, or null while it is still running.
        /// Read by <see cref="CaptureWatchdog"/> as positive evidence of a dead capture.
        /// </summary>
        public int? ExitCode => _proc.HasExited ? _proc.ExitCode : null;

        /// <summary>
        /// Seconds of output ffmpeg last reported writing (its "time=" field). After <see cref="Stop"/>
        /// it is ffmpeg's own account of what it muxed - the honest length of a capture that fell
        /// behind, where wall time is not.
        /// </summary>
        public double MediaSeconds => System.Threading.Interlocked.Read(ref _mediaMs) / 1000.0;

        /// <summary>
        /// The longest <see cref="Abandon"/> waits for the killed process to be reaped before it
        /// reports the capture as un-endable. The kill is the whole of an abandon, so this is also
        /// the worst case from the watchdog's verdict to the service being idle again - the bound
        /// CaptureWatchdogTests budgets inside the issue's sixty-second window.
        /// </summary>
        internal static readonly TimeSpan AbandonExitWait = TimeSpan.FromSeconds(3);

        /// <summary>
        /// End a capture the <see cref="CaptureWatchdog"/> has judged dead - AT ONCE (issue #113,
        /// round two).
        ///
        /// This used to call <see cref="Stop"/>, which on a frozen process waited out the whole
        /// drain (5 s) for a pipeline that was not moving and then the quit wait (8 s) for an answer
        /// a frozen process cannot give: about 13.5 s spent waiting on a process already judged
        /// dead, and the recording reported as finishing cleanly the whole time. There is nothing to
        /// drain from a capture that has stopped producing output and no point asking it to quit,
        /// so it is killed. The fragmented, flushed output (<see cref="FfmpegArgs.FragmentedOutput"/>)
        /// is what keeps the file playable through a kill; round one measured that on Linux.
        ///
        /// Deliberately independent of <see cref="Stop"/>, which issue #114 rewrites.
        ///
        /// Throws when the process does not exit within <see cref="AbandonExitWait"/> of the kill:
        /// a capture that might still be writing is a failed stop, not a quiet one.
        /// </summary>
        public void Abandon(string reason)
        {
            Log.Warn($"[FfmpegRecorder] Abandon: {OutputPath} - {reason} - killing the capture process now");
            if (_stopped)
            {
                Log.Info($"[FfmpegRecorder] Abandon: {OutputPath} was already stopped - nothing to kill");
                return;
            }
            _stopped = true;

            var sw = Stopwatch.StartNew();
            // Kill on a process that has already exited (the "exited" verdict) is a no-op in .NET, but
            // the check keeps the log honest about which case this was.
            bool wasRunning = !_proc.HasExited;
            if (wasRunning) _proc.Kill(entireProcessTree: true);
            if (!_proc.WaitForExit((int)AbandonExitWait.TotalMilliseconds))
            {
                Log.Error($"[FfmpegRecorder] Abandon FAILED: {OutputPath} - the capture process did not exit within "
                          + $"{AbandonExitWait.TotalSeconds:F0}s of being killed");
                throw new InvalidOperationException(
                    $"the capture process for {Path.GetFileName(OutputPath)} did not exit within "
                    + $"{AbandonExitWait.TotalSeconds:F0}s of being killed; it may still be running");
            }

            File.WriteAllText(_logPath, _stderr.ToString());
            Log.Info($"[FfmpegRecorder] Abandon: {OutputPath} ended in {sw.ElapsedMilliseconds}ms "
                     + $"({(wasRunning ? "killed" : "had already exited")}, exit {ExitCode?.ToString() ?? "unknown"}, "
                     + $"last reported position {MediaSeconds:F2}s)");
        }

        /// <summary>
        /// Put an already-started process under this class (issue #113, round two). For the test
        /// that measures <see cref="Abandon"/> against a process that ignores the 'q' quit, the way a
        /// frozen ffmpeg does; nothing in the product calls it. Added rather than splitting
        /// <see cref="Start"/>, which issue #114 rewrites.
        /// </summary>
        internal static FfmpegRecorder ForStartedProcess(Process started, string outPath)
        {
            if (started == null) throw new ArgumentNullException(nameof(started));
            var rec = new FfmpegRecorder(started, outPath, started.StartInfo.FileName, outPath + ".ffmpeg.log")
            {
                StartedUtc = DateTime.UtcNow,
            };
            Log.Info($"[FfmpegRecorder] ForStartedProcess: pid={started.Id} out={outPath}");
            return rec;
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                try { Stop(); } catch { }
            }
            _proc.Dispose();
        }
    }
}
