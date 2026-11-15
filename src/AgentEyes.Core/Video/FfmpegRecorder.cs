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
    internal sealed class FfmpegRecorder : IDisposable
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

        /// <summary>
        /// The tightest observed offset, in milliseconds on <see cref="_clock"/>, between this
        /// recorder's wall clock and ffmpeg's media clock - i.e. where media time 0 sits on our
        /// clock. See <see cref="Stop"/> for why the drain cannot be computed without it. long.MaxValue
        /// until the first progress tick carries a timestamp.
        /// </summary>
        private long _mediaOriginMs = long.MaxValue;

        /// <summary>
        /// The capture clock timestamp of the FIRST frame ffmpeg read from the device, as ffmpeg
        /// reports it ("Input #0, avfoundation, ... start: 152597.730833"), or null when the log has
        /// not said. On macOS this is a CoreMedia host-clock reading, which is the SAME clock the
        /// microphone's own ffmpeg reports its first sample on - so the difference of the two is the
        /// exact offset between this file's t=0 and that WAV's t=0 (issue #114).
        ///
        /// Held as microseconds in a long, and published with Interlocked, because it is WRITTEN on
        /// ffmpeg's stderr thread and READ at the stop: a nullable double is two fields and neither
        /// of them is written atomically, so a reader could see the value without the flag or half
        /// of a torn double. Microseconds are six decimal places, which is every digit ffmpeg prints.
        /// </summary>
        public double? InputStartSeconds
        {
            get
            {
                long micros = System.Threading.Interlocked.Read(ref _inputStartMicros);
                return micros < 0 ? null : micros / 1_000_000.0;
            }
        }

        /// <summary>Backing store for <see cref="InputStartSeconds"/>; negative means "not reported".</summary>
        private long _inputStartMicros = -1;

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
            //
            // THE MICROPHONE IS NOT AN INPUT OF THIS PROCESS ON macOS (issue #114): it is captured
            // by its own ffmpeg (AudioCapture over AvfStream) and the deferred mux joins them - see
            // CaptureTopology, which is what tells a caller which of the two it must do. That split
            // is NOT what fixed #114's audio loss; the measured cause was the recorder running at
            // throttled background QoS, and `ProcessType Interactive` in the launchd plist is the
            // load-bearing fix. FfmpegArgs.AvfVideoCapture carries the numbers and the two reasons
            // the split is kept on its own merits.
            //
            // ASKING FOR ONE HERE IS A PROGRAMMING ERROR AND SAYS SO, rather than being ignored.
            // Ignoring it is what would ship the real damage: this method would return happily, the
            // caller would believe its recording had sound, and the file would be silent with
            // nothing anywhere saying why. Two callers still pass a microphone - the CLI's video
            // recording (Commands.cs) and the headless selftest - and neither can capture a macOS
            // screen at all today, because TCC grants the Screen Recording permission to the launchd
            // agent and not to a shell. They fail here with this message instead of writing a silent
            // file, and teaching them the separate-writer topology is their own work.
            if (dshowMicName != null)
            {
                throw new UsageException(
                    "on macOS the screen recorder cannot capture the microphone: ffmpeg's "
                    + "AVFoundation demuxer loses most of the samples when a display and an audio "
                    + "device share one process (issue #114). Capture it with AudioCapture into its "
                    + "own WAV beside the video and mux it afterwards - see CaptureTopology and "
                    + "RecordingService.StartVideo - and pass no microphone here.");
            }

            var (avfScreen, macScreen) = Platform.Mac.MacDisplay.RequireScreen(screenNumber);
            var args = FfmpegArgs.AvfVideoCapture(
                capture, avfScreen, new Drawing.Size(macScreen.Width, macScreen.Height),
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
                if (ms >= 0)
                {
                    System.Threading.Interlocked.Exchange(ref rec._mediaMs, ms);
                    // Where media time 0 sits on our clock. Reported media time always TRAILS wall
                    // time (the pipeline's latency), so wall-minus-media is an over-estimate of the
                    // origin and the smallest one seen is the tightest estimate of it.
                    long origin = rec._clock.ElapsedMilliseconds - ms;
                    if (origin < System.Threading.Interlocked.Read(ref rec._mediaOriginMs))
                        System.Threading.Interlocked.Exchange(ref rec._mediaOriginMs, origin);
                }
                else if (System.Threading.Interlocked.Read(ref rec._inputStartMicros) < 0)
                {
                    double? start = ParseInputStartSeconds(e.Data);
                    if (start is > 0)
                        System.Threading.Interlocked.Exchange(
                            ref rec._inputStartMicros, (long)Math.Round(start.Value * 1_000_000.0));
                }
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
            // THE TWO CLOCKS ARE NOT THE SAME CLOCK (issue #114). This used to wait for ffmpeg's
            // media time to reach OUR elapsed time, which silently assumed that media time 0 and
            // this stopwatch's zero are the same instant. They are not: the stopwatch starts when
            // this object is constructed, and media time 0 is the first frame the DEVICE delivered -
            // about two seconds later on macOS, where opening a display costs that much warm-up. So
            // the target was ~2 s past the end of the take and the drain ran to its full timeout
            // EVERY time, with ffmpeg still capturing: a 30 s ask came back as a 35.3 s recording
            // whose own duration disagreed with the duration the service reported.
            //
            // The media position that corresponds to "now" is now-minus-the-origin, so that is what
            // we wait for. On Windows and Linux the origin is a few hundred milliseconds and this
            // barely changes the wait; on macOS it is the whole defect.
            //
            // No tick has carried a timestamp yet (origin unknown) means ffmpeg has not reported any
            // output at all. There is nothing to drain TO, so the drain is skipped rather than being
            // given a made-up target - and the "did not catch up" branch below says so.
            long origin = System.Threading.Interlocked.Read(ref _mediaOriginMs);
            long stopAtMs = origin == long.MaxValue ? -1 : _clock.ElapsedMilliseconds - origin;
            var drain = Stopwatch.StartNew();
            bool caughtUp = false;
            while (stopAtMs >= 0 && drain.Elapsed < DrainTimeout)
            {
                if (_proc.HasExited) break;
                if (System.Threading.Interlocked.Read(ref _mediaMs) >= stopAtMs) { caughtUp = true; break; }
                Thread.Sleep(20);
            }
            drain.Stop();

            if (caughtUp)
            {
                Log.Info($"[FfmpegRecorder] Stop: pipeline drained in {drain.ElapsedMilliseconds}ms " +
                         $"(take {stopAtMs}ms, media origin {origin}ms) - full take captured");
            }
            else
            {
                Log.Warn($"[FfmpegRecorder] Stop: pipeline did not reach {stopAtMs}ms within " +
                         $"{DrainTimeout.TotalSeconds:0.#}s (reached " +
                         $"{System.Threading.Interlocked.Read(ref _mediaMs)}ms, media origin " +
                         $"{(origin == long.MaxValue ? "never reported" : origin + "ms")}) - the end " +
                         $"of this recording may be truncated. See {_logPath}");
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
        /// Read the CAPTURE CLOCK timestamp of a device's first frame out of ffmpeg's input dump
        /// ("  Duration: N/A, start: 152597.730833, bitrate: N/A") and return it in seconds, or null
        /// when the line is not that line. Pure, so the alignment that depends on it is tested
        /// without launching ffmpeg (issue #114).
        ///
        /// On macOS this number is a CoreMedia host-clock reading, which every process on the machine
        /// shares - so the same field from the microphone's own ffmpeg is directly comparable, and the
        /// difference is how far the two captures' t=0 are apart. Only the ", start: " form is matched:
        /// the per-stream line carries "start 152597.730833" with no colon and is a different field.
        /// </summary>
        internal static double? ParseInputStartSeconds(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int i = line.IndexOf(", start: ", StringComparison.Ordinal);
            if (i < 0) return null;
            i += ", start: ".Length;

            int end = i;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.' || line[end] == '-')) end++;
            if (end == i) return null;

            return double.TryParse(
                line.AsSpan(i, end - i), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                ? seconds
                : null;
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
