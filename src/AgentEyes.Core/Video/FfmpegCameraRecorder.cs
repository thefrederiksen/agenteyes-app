using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AgentEyes.Video
{
    /// <summary>
    /// The webcam half of a recording (issue #28): a SECOND, independent ffmpeg process writing
    /// camera.mp4 next to the screen recorder's recording.mp4, so a session can be edited afterwards
    /// into a screen-plus-presenter piece instead of a layout baked in at record time.
    ///
    /// It is deliberately its own class rather than a mode of <see cref="FfmpegRecorder"/>, because
    /// the two differ in exactly the ways that matter here:
    ///
    ///  - it is VIDEO ONLY (no audio track - decision 1), so it never participates in the deferred
    ///    audio mux and writes straight to its FINAL path (assumption A4);
    ///  - a camera that cannot be OPENED fails the whole recording start, loudly (decision 3);
    ///  - a camera LOST MID-RUN does not (decision 4). The screen recorder keeps running, the loss is
    ///    logged as a WARNING naming the camera, and the manifest records the track as truncated with
    ///    the seconds actually captured. Anything else would throw away a good screen recording
    ///    because a USB cable moved.
    ///
    /// Like the screen recorder it is stopped with "q" on stdin rather than a kill (assumption A6),
    /// so camera.mp4 is finalized rather than truncated.
    /// </summary>
    internal sealed class FfmpegCameraRecorder : IDisposable
    {
        /// <summary>How long to watch a freshly started camera before calling the open a success.</summary>
        private static readonly TimeSpan OpenProbe = TimeSpan.FromMilliseconds(400);

        private readonly Process _proc;
        private readonly StringBuilder _stderr = new();
        private readonly string _logPath;
        private bool _stopped;

        /// <summary>
        /// The output position ffmpeg last reported (its "time=" progress field), in milliseconds -
        /// the number of seconds of camera actually written. Read at stop for the manifest, and it is
        /// the ONLY honest answer for a camera that died mid-run: wall time would claim footage the
        /// file does not contain.
        /// </summary>
        private long _mediaMs;

        /// <summary>Set when the process exits while the recording is still running (decision 4).</summary>
        private volatile bool _lostMidRun;

        /// <summary>
        /// True once the open probe has passed, i.e. this camera really is recording. It is what
        /// tells a MID-RUN loss (decision 4 - warn, keep the screen recording) apart from a camera
        /// that never opened at all (decision 3 - fail the start): both end as an ffmpeg exit, and
        /// only one of them is a warning about a recording in progress.
        /// </summary>
        private volatile bool _opened;

        /// <summary>The exact DirectShow device name this process is capturing.</summary>
        public string DeviceName { get; }

        /// <summary>Where camera.mp4 is being written (its final path - no deferred mux).</summary>
        public string OutputPath { get; }

        /// <summary>The full ffmpeg command line, for the manifest and the log.</summary>
        public string CommandLine { get; }

        /// <summary>
        /// When the ffmpeg process was actually started, captured the instant Process.Start returned.
        /// This is what <c>CameraStartOffsetSeconds</c> is measured from (assumption A5) - an
        /// alignment HINT of tens of milliseconds, not frame-accurate genlock.
        /// </summary>
        public DateTime StartedUtc { get; }

        /// <summary>True when the camera stopped on its own before <see cref="Stop"/> was called.</summary>
        public bool LostMidRun => _lostMidRun;

        /// <summary>Seconds of camera footage ffmpeg reported writing (0 before its first progress tick).</summary>
        public double CapturedSeconds => Interlocked.Read(ref _mediaMs) / 1000.0;

        public bool HasExited => _proc.HasExited;

        private FfmpegCameraRecorder(Process proc, string deviceName, string outputPath, string commandLine,
            string logPath, DateTime startedUtc)
        {
            _proc = proc;
            DeviceName = deviceName;
            OutputPath = outputPath;
            CommandLine = commandLine;
            _logPath = logPath;
            StartedUtc = startedUtc;
        }

        /// <summary>
        /// Open the camera and start writing <paramref name="outPath"/>.
        ///
        /// Throws <see cref="UsageException"/> naming the camera when ffmpeg cannot open the device -
        /// absent, in use by another application, or refusing the requested framerate. That is
        /// decision 3: a camera recording that cannot film the camera FAILS, it never silently
        /// records screen-only.
        ///
        /// NOTHING is written into the recording directory on that failure path - not even an ffmpeg
        /// log - because a failed start must leave no directory behind for the Library and the repair
        /// passes to find (issue #28, AC8/AC9). ffmpeg's stderr goes to the APPLICATION log instead,
        /// where it is just as diagnosable and belongs to no recording.
        /// </summary>
        public static FfmpegCameraRecorder Start(string dshowCameraName, int fps, int crf, string outPath)
        {
            Log.Info($"[FfmpegCameraRecorder] Start: camera=\"{dshowCameraName}\" fps={fps} crf={crf} out={outPath}");

            // A DirectShow camera is EXCLUSIVE, so anything in this process that is holding one -
            // today that is the preset editor's live preview (issue #29) - is told to let go BEFORE
            // the device is opened, and has released by the time this returns. This is the single
            // choke point for that: every recording path, from the launcher to POST /record/start,
            // reaches the camera through here, so none of them can forget it.
            CameraDeviceArbiter.ReleaseForRecording(dshowCameraName);

            string exe = FfmpegLocator.Ffmpeg();
            var args = FfmpegArgs.CameraCapture(dshowCameraName, fps, crf, outPath);
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
            var rec = new FfmpegCameraRecorder(
                proc, dshowCameraName, outPath, cmd, outPath + ".ffmpeg.log", DateTime.UtcNow);

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                rec._stderr.AppendLine(e.Data);
                // ffmpeg writes its progress with a carriage return, which .NET treats as a line
                // break, so each "time=" tick arrives here as its own line. Shared with the screen
                // recorder rather than parsed a second way.
                long ms = FfmpegRecorder.ParseProgressMs(e.Data);
                if (ms >= 0) Interlocked.Exchange(ref rec._mediaMs, ms);
            };
            proc.OutputDataReceived += (_, _) => { };

            // Decision 4 lives here: a camera that dies mid-run says so, loudly, in the log - and does
            // NOT touch the screen recording. Exited fires for a clean stop too, which _stopped tells
            // apart.
            proc.Exited += (_, _) =>
            {
                if (rec._stopped || !rec._opened) return;
                rec._lostMidRun = true;
                Log.Warn($"[FfmpegCameraRecorder] the camera \"{dshowCameraName}\" stopped during the recording " +
                         $"(ffmpeg exited on its own) - the screen recording continues; camera.mp4 is truncated at " +
                         $"{rec.CapturedSeconds:F1}s. See {rec._logPath}");
            };

            if (!proc.Start())
            {
                throw new UsageException($"failed to start ffmpeg for the camera \"{dshowCameraName}\".");
            }
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            // Give ffmpeg a moment to open the device; a camera that is absent or already held by
            // another application fails here, and the whole recording start fails with it.
            Thread.Sleep(OpenProbe);
            if (proc.HasExited && proc.ExitCode != 0)
            {
                // READ THE EXIT CODE BEFORE DISPOSING. Process.Dispose releases the process handle,
                // and every property that needs it - ExitCode included - throws "No process is
                // associated with this object" afterwards. Reading it inside the message below (i.e.
                // after the Dispose) replaced the real, actionable "the camera is already in use"
                // failure with that meaningless one, which is precisely the failure a user would hit
                // first: a webcam held by a browser or OBS.
                int exitCode = proc.ExitCode;
                string err = rec._stderr.ToString();
                // Deliberately NOT written into the recording directory - see the summary above.
                Log.Error($"[FfmpegCameraRecorder] Start FAILED: camera=\"{dshowCameraName}\" exit={exitCode} " +
                          $"cmd={cmd}{Environment.NewLine}{err}");
                // _stopped short-circuits Dispose -> Stop, whose ONLY side effect would be writing
                // the ffmpeg log into the recording directory - the one thing this path must not do.
                rec._stopped = true;
                proc.Dispose();
                throw new UsageException(
                    $"the camera \"{dshowCameraName}\" could not be opened (ffmpeg exited with code {exitCode}). " +
                    "Likely cause: " + DiagnoseOpenFailure(err, dshowCameraName));
            }

            rec._opened = true;
            Log.Info($"[FfmpegCameraRecorder] Start: camera=\"{dshowCameraName}\" is recording to {outPath}");
            return rec;
        }

        /// <summary>
        /// Stop the camera and finalize camera.mp4.
        ///
        /// This NEVER throws for a camera that already died (decision 4): the loss was reported when
        /// it happened, the screen recording is the artifact that matters, and turning it into a stop
        /// failure here would mark an otherwise clean recording as failed.
        /// </summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            bool lost = _lostMidRun || _proc.HasExited;
            Log.Info($"[FfmpegCameraRecorder] Stop: camera=\"{DeviceName}\" captured={CapturedSeconds:F1}s " +
                     $"lostMidRun={lost}");

            if (!_proc.HasExited)
            {
                try
                {
                    _proc.StandardInput.Write("q");
                    _proc.StandardInput.Flush();
                }
                catch (Exception ex)
                {
                    // stdin closes when ffmpeg exits; that is the mid-run-loss case, already reported.
                    Log.Warn($"[FfmpegCameraRecorder] Stop: could not send 'q' to the camera ffmpeg " +
                             $"(\"{DeviceName}\"): {ex.Message}");
                }

                if (!_proc.WaitForExit(8000))
                {
                    Log.Warn($"[FfmpegCameraRecorder] Stop: the camera ffmpeg (\"{DeviceName}\") did not quit " +
                             $"within 8s - killing it; camera.mp4 may be truncated");
                    try { _proc.Kill(true); } catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Stop: kill failed for \"{DeviceName}\"", ex); }
                    _proc.WaitForExit(3000);
                }
            }

            try { File.WriteAllText(_logPath, _stderr.ToString()); }
            catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Stop: writing {_logPath} failed", ex); }

            Log.Info($"[FfmpegCameraRecorder] Stop: camera=\"{DeviceName}\" done, {CapturedSeconds:F1}s in {OutputPath}");
        }

        /// <summary>
        /// Turn ffmpeg's stderr into an accurate, actionable cause for a camera that would not open.
        /// Pure string inspection - safe to unit test, and it never guesses past what the text says.
        /// </summary>
        internal static string DiagnoseOpenFailure(string stderr, string dshowCameraName)
        {
            stderr ??= "";
            // The wording below is what ffmpeg 9.0 actually prints for a webcam held by another
            // process - observed verbatim while implementing this (a browser had the camera):
            //   [dshow @ ...] Could not run graph (sometimes caused by a device already in use by
            //   other application)
            //   [in#0 @ ...] Error opening input: I/O error
            if (stderr.Contains("already in use", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Could not run graph", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Could not run filter", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Device or resource busy", StringComparison.OrdinalIgnoreCase))
                return $"the camera \"{dshowCameraName}\" is already in use by another application.";
            if (stderr.Contains("Could not find video device", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("no device found", StringComparison.OrdinalIgnoreCase))
                return $"no DirectShow device is named \"{dshowCameraName}\" any more (was it unplugged?).";
            if (stderr.Contains("Could not set video options", StringComparison.OrdinalIgnoreCase))
                return $"the camera \"{dshowCameraName}\" rejected the requested frame rate.";
            return $"see the application log for the ffmpeg error from \"{dshowCameraName}\".";
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                try { Stop(); }
                catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Dispose: stopping \"{DeviceName}\" failed", ex); }
            }
            _proc.Dispose();
        }
    }
}
