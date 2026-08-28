using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AgentEyes.Video
{
    /// <summary>
    /// Raised when a camera stop could not get ffmpeg off the webcam (issue #28, gate defect 2).
    ///
    /// It exists so that "the camera is stopped" can never be a guess. A stop that sent "q", timed
    /// out, killed, and STILL sees a live process has not stopped anything: ffmpeg is writing
    /// camera.mp4 and holding an exclusive DirectShow device. Reporting that as a clean stop is what
    /// let the service go idle, release the capture claim, and offer to record again while the
    /// webcam was still taken.
    /// </summary>
    internal sealed class CameraStopFailedException : Exception
    {
        public CameraStopFailedException(string deviceName, string outputPath, string logPath)
            : base($"the camera ffmpeg for \"{deviceName}\" could not be terminated - it is STILL RUNNING and "
                   + $"still holds the camera and {outputPath}. See {logPath}.")
        {
            DeviceName = deviceName;
        }

        public string DeviceName { get; }
    }

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
    ///
    /// THE THREE RULES THE REVIEW GATE ADDED (round 2). All three are the same rule wearing different
    /// clothes - a camera claim is only ever made from a PRESENCE that was observed:
    ///
    ///  1. OPENED means ffmpeg REPORTED the camera open, not "it has not failed yet". A fixed 400 ms
    ///     sleep proved nothing: a busy, unplugged or unsupported device that takes 500 ms to fail
    ///     passed the probe, and its later exit was then filed as a harmless mid-run loss - turning a
    ///     camera that never recorded a frame into a silent screen-only recording, which is the exact
    ///     outcome decision 3 exists to prevent. The presence the probe waits for is named in
    ///     <see cref="StartAndProbe"/>, and WHICH presence it is decides AC3 as much as AC9 - see
    ///     the measurements there.
    ///  2. STOPPED means the OS process is gone. A timeout that logs a warning, a kill whose failure
    ///     is swallowed, and a second wait nobody reads are three ways of reporting a stop that did
    ///     not happen. When ffmpeg survives all of it this throws, and the caller reports a FAILED
    ///     stop instead of going idle with the webcam still held.
    ///  3. LOST means the process ended before the user asked it to - observed from the process
    ///     itself at stop time, not from whether an exit callback happened to have been delivered
    ///     yet. The callback is a convenience; <c>HasExited</c> is the fact.
    /// </summary>
    internal sealed class FfmpegCameraRecorder : IDisposable
    {
        /// <summary>
        /// How long a freshly started camera is given to report itself open before the open is called
        /// a failure. Generous on purpose - webcams take a moment to warm up, and this is a deadline
        /// for FAILING, not a delay that every start pays: a camera that reports itself open in
        /// 600 ms is recording 600 ms after it was asked to.
        /// </summary>
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(8);

        /// <summary>How often the open probe looks at the process.</summary>
        private static readonly TimeSpan ProbePoll = TimeSpan.FromMilliseconds(25);

        /// <summary>How long a clean "q" quit is given before the process is killed.</summary>
        private const int QuitTimeoutMs = 8000;

        /// <summary>How long the kill is given before the stop is declared FAILED.</summary>
        private const int KillTimeoutMs = 3000;

        private readonly ICameraProcess _proc;
        private readonly StringBuilder _stderr = new();
        private readonly string _logPath;
        private readonly TimeSpan _openTimeout;

        /// <summary>
        /// Set when a stop has been ASKED FOR. It is deliberately not "this recorder is finished":
        /// a stop that could not terminate ffmpeg has finished nothing, and <see cref="Dispose"/>
        /// has to be able to try again. Conflating the two is gate defect 2 - the flag was set at the
        /// top of Stop, so the one retry left in the object could never run.
        /// </summary>
        private bool _stopRequested;

        /// <summary>Set only when the OS process is CONFIRMED gone. The gate on doing the stop's work
        /// twice, and the only state that makes <see cref="Stop"/> a no-op.</summary>
        private bool _terminated;

        /// <summary>
        /// The output position ffmpeg last reported (its "time=" progress field), in milliseconds -
        /// the number of seconds of camera actually written. Read at stop for the manifest, and it is
        /// the ONLY honest answer for a camera that died mid-run: wall time would claim footage the
        /// file does not contain.
        /// </summary>
        private long _mediaMs;

        /// <summary>
        /// Set the first time ffmpeg reports a progress tick carrying a real output position. It is
        /// NOT what the open probe waits for - see <see cref="StartAndProbe"/> - because libx264's
        /// frame threading holds the first encoded frame back by seconds. It is kept because it is
        /// the honest answer to "did this camera ever produce encoded output", which is worth having
        /// in the stop log.
        /// </summary>
        private volatile bool _wroteOutput;

        /// <summary>
        /// Set when ffmpeg reports that it OPENED the DirectShow input - the "Input #0, dshow, from
        /// 'video=...'" header it dumps only after the capture graph ran and the stream parameters
        /// were read off the device. Half of the open probe's presence.
        /// </summary>
        private volatile bool _inputOpenReported;

        /// <summary>
        /// Set when ffmpeg reports that it OPENED camera.mp4 for writing - the "Output #0, mp4, to
        /// '...'" header. The other half: input open plus output open is ffmpeg saying, in its own
        /// words, that this camera is being recorded to this file.
        /// </summary>
        private volatile bool _outputOpenReported;

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

        private FfmpegCameraRecorder(ICameraProcess proc, string deviceName, string outputPath, string commandLine,
            string logPath, DateTime startedUtc, TimeSpan openTimeout)
        {
            _proc = proc;
            DeviceName = deviceName;
            OutputPath = outputPath;
            CommandLine = commandLine;
            _logPath = logPath;
            StartedUtc = startedUtc;
            _openTimeout = openTimeout;
        }

        /// <summary>
        /// Open the camera and start writing <paramref name="outPath"/>.
        ///
        /// Throws <see cref="UsageException"/> naming the camera when ffmpeg cannot open the device -
        /// absent, in use by another application, refusing the requested framerate, or simply never
        /// producing a frame. That is decision 3: a camera recording that cannot film the camera
        /// FAILS, it never silently records screen-only.
        ///
        /// NOTHING is written into the recording directory on that failure path - not even an ffmpeg
        /// log - because a failed start must leave no directory behind for the Library and the repair
        /// passes to find (issue #28, AC8/AC9). ffmpeg's stderr goes to the APPLICATION log instead,
        /// where it is just as diagnosable and belongs to no recording.
        /// </summary>
        public static FfmpegCameraRecorder Start(string dshowCameraName, int fps, int crf, string outPath)
        {
            Log.Info($"[FfmpegCameraRecorder] Start: camera=\"{dshowCameraName}\" fps={fps} crf={crf} out={outPath}");

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

            var rec = new FfmpegCameraRecorder(
                new FfmpegCameraProcess(psi, dshowCameraName), dshowCameraName, outPath, cmd,
                outPath + ".ffmpeg.log", DateTime.UtcNow, OpenTimeout);
            rec.StartAndProbe();

            Log.Info($"[FfmpegCameraRecorder] Start: camera=\"{dshowCameraName}\" is recording to {outPath}");
            return rec;
        }

        /// <summary>
        /// The same recorder over a supplied process - the seam the failure-path tests drive
        /// (issue #28, gate round 2). Identical logic to <see cref="Start"/> from the moment the
        /// process starts; only the process and the probe deadline are injected, so a test can reach
        /// the delayed-failure, failed-termination and exit/stop-race paths that a real ffmpeg will
        /// not perform on request.
        /// </summary>
        internal static FfmpegCameraRecorder StartOver(ICameraProcess proc, string deviceName, string outPath,
            string logPath, TimeSpan openTimeout)
        {
            var rec = new FfmpegCameraRecorder(
                proc, deviceName, outPath, "(supplied process)", logPath, DateTime.UtcNow, openTimeout);
            rec.StartAndProbe();
            return rec;
        }

        /// <summary>
        /// Start the process and hold the recording start until this camera has PROVED it is open -
        /// gate defect 3.
        ///
        /// THE PROOF IS FFMPEG'S OWN OPEN REPORT: the "Input #0, dshow, from 'video=...'" header it
        /// dumps only after the DirectShow capture graph ran and the stream parameters were read off
        /// the device, AND the "Output #0, mp4, to '...'" header it dumps once camera.mp4 is open for
        /// writing. Together they are ffmpeg saying, in its own words, that this camera is being
        /// recorded to this file. There is exactly ONE input and ONE output on the command line
        /// (<see cref="FfmpegArgs.CameraCapture"/>), so neither line can be about anything else.
        ///
        /// Everything else is a failure:
        ///
        ///  - the process ended (any exit code, zero included - ffmpeg exiting cleanly the instant it
        ///    was asked to film is still a camera that filmed nothing);
        ///  - the deadline passed without both reports.
        ///
        /// Both throw, and both make sure the process is dead before they do: a probe that gave up on
        /// a still-running ffmpeg and walked away would replace one leak with another.
        ///
        /// WHY NOT THE FIRST PROGRESS TICK, which is what round 2 of this fix waited for. Because that
        /// tick reports ENCODED output, and libx264 frame-threading holds the first encoded frame back
        /// by seconds - while the camera is already filming. Measured on the eMeet C960 with the
        /// shipped ffmpeg (2026-08-28; 1920x1080 mjpeg in, x264 veryfast, threads=34), relative to
        /// the moment the process started:
        ///
        ///     0.373s  first frame actually captured (quit time minus the resulting file duration)
        ///     0.635s  "Input #0, dshow, from 'video=HD Webcam eMeet C960'"
        ///     0.660s  "Output #0, mp4, to '...camera.mp4'"
        ///     2.696s  first progress tick carrying a real time= (frame=13 time=00:00:00.36)
        ///
        /// The screen recorder is started only after this probe returns, so whatever the probe costs
        /// is head footage that camera.mp4 carries and recording.mp4 does not. Waiting for the tick
        /// bought 2.3s of it and broke AC3's 1.0s duration clause; waiting for the open report costs
        /// ~0.26s and keeps AC3 and AC9 both. The three failures decision 3 names - busy, unplugged,
        /// unsupported framerate - all abort inside ffmpeg's input open and print NEITHER header
        /// (verified against the shipped ffmpeg: a held camera exits -5 in 0.23s, an absent one in
        /// 0.03s, and neither emits "Input #0"), so this presence rejects every one of them.
        ///
        /// What it deliberately does NOT claim is that a frame was ENCODED. A device that opens and
        /// then stops delivering is a MID-RUN loss, which decision 4 governs: the screen recording
        /// survives, the loss is a WARNING naming the camera, and the manifest records the track as
        /// truncated with the seconds actually captured. That is a REPORTED failure, not a silent one,
        /// and it is the only case this presence hands to decision 4 that the progress tick would have
        /// failed at the start.
        /// </summary>
        private void StartAndProbe()
        {
            _proc.Start(OnStderrLine, OnExited);

            var probe = Stopwatch.StartNew();
            while (true)
            {
                // Exit first: a process that has ended is not recording, whatever it printed on the
                // way out.
                if (_proc.HasExited)
                {
                    // READ THE EXIT CODE FIRST. Disposing the process releases the handle every
                    // property needs, and reading it afterwards throws "No process is associated with
                    // this object" - which once replaced the real, actionable "the camera is already
                    // in use" failure with a meaningless one.
                    int exitCode = _proc.ExitCode;
                    throw FailOpen($"ffmpeg exited with code {exitCode}",
                                   $"(ffmpeg exited with code {exitCode})", killFirst: false);
                }

                if (_inputOpenReported && _outputOpenReported) break;

                if (probe.Elapsed >= _openTimeout)
                {
                    string missing = _inputOpenReported
                        ? "it opened the camera but never opened camera.mp4"
                        : "it never reported the camera open";
                    throw FailOpen(
                        $"ffmpeg did not report the camera open within {_openTimeout.TotalSeconds:0.#}s ({missing})",
                        $"(it did not open within {_openTimeout.TotalSeconds:0.#}s - {missing})",
                        killFirst: true);
                }

                Thread.Sleep(ProbePoll);
            }

            // No second exit check here on purpose. The loop reads HasExited at the TOP of the same
            // iteration that sees the two flags, so a death in the microseconds after that read is
            // indistinguishable from a death one poll later - which is a MID-RUN loss, reported by
            // OnExited and by Stop (decision 4). A re-check would be a branch no test can reach.
            _opened = true;
            Log.Info($"[FfmpegCameraRecorder] StartAndProbe: camera=\"{DeviceName}\" reported the camera and "
                     + $"{Path.GetFileName(OutputPath)} open after {probe.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// ffmpeg's report that it OPENED the DirectShow camera: the input header it dumps only after
        /// the capture graph ran and the stream parameters were read off the device. Pure string
        /// inspection, so the open probe's presence is unit testable without ffmpeg.
        ///
        /// Matched on the header SHAPE and not on the device name, because the command line carries
        /// exactly one input and it is this camera - and because a name match that a future ffmpeg
        /// quoted differently would fail a working camera rather than reject a broken one.
        /// </summary>
        internal static bool IsInputOpenReport(string? line) =>
            line != null
            && line.StartsWith("Input #0", StringComparison.Ordinal)
            && line.Contains(", dshow,", StringComparison.Ordinal);

        /// <summary>
        /// ffmpeg's report that it OPENED the output file for writing: the output header it dumps
        /// once the muxer is set up on camera.mp4. The other half of the open probe's presence.
        /// </summary>
        internal static bool IsOutputOpenReport(string? line) =>
            line != null && line.StartsWith("Output #0", StringComparison.Ordinal);

        /// <summary>
        /// Give up on a camera that never started recording: make sure ffmpeg is gone, release the
        /// process, and BUILD the actionable failure. Returns the exception so that every caller is
        /// a visible `throw` - a helper that only throws by convention is one edit away from falling
        /// through into "opened".
        /// </summary>
        private Exception FailOpen(string logReason, string userReason, bool killFirst)
        {
            string err = _stderr.ToString();
            // Deliberately NOT written into the recording directory: a failed start must leave no
            // recording behind for the Library and the repair passes to find (AC8/AC9).
            Log.Error($"[FfmpegCameraRecorder] Start FAILED: camera=\"{DeviceName}\" {logReason} "
                      + $"cmd={CommandLine}{Environment.NewLine}{err}");

            // A probe that timed out is looking at a LIVE process holding the webcam. Leaving it
            // there would trade the defect the gate found for a worse one.
            if (killFirst && !_proc.HasExited)
            {
                try { _proc.Kill(); }
                catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Start: killing the stalled ffmpeg for \"{DeviceName}\" failed", ex); }
                if (!_proc.WaitForExit(KillTimeoutMs))
                    Log.Error($"[FfmpegCameraRecorder] Start: the stalled ffmpeg for \"{DeviceName}\" is STILL RUNNING "
                              + "after the kill - it still holds the camera");
            }

            // Nothing left to stop, and nothing to write: short-circuit Dispose so it cannot put the
            // ffmpeg log into the recording directory this start must leave empty.
            _stopRequested = true;
            _terminated = true;
            _proc.Dispose();

            return new UsageException(
                $"the camera \"{DeviceName}\" could not be opened {userReason}. "
                + "Likely cause: " + DiagnoseOpenFailure(err, DeviceName));
        }

        private void OnStderrLine(string line)
        {
            _stderr.AppendLine(line);

            // The open probe's presence (gate defect 3): ffmpeg's own headers for the input it
            // opened and the file it is writing. See StartAndProbe for why these and not the tick.
            if (IsInputOpenReport(line)) _inputOpenReported = true;
            else if (IsOutputOpenReport(line)) _outputOpenReported = true;

            // ffmpeg writes its progress with a carriage return, which .NET treats as a line break,
            // so each "time=" tick arrives here as its own line. Shared with the screen recorder
            // rather than parsed a second way.
            long ms = FfmpegRecorder.ParseProgressMs(line);
            if (ms >= 0)
            {
                Interlocked.Exchange(ref _mediaMs, ms);
                _wroteOutput = true;
            }
        }

        /// <summary>
        /// Decision 4 lives here: a camera that dies mid-run says so, loudly, in the log - and does
        /// NOT touch the screen recording. Exited fires for a clean stop too, which _stopRequested
        /// tells apart. This is a CONVENIENCE, not the authority: <see cref="Stop"/> re-reads the
        /// process itself, because this callback may simply not have been delivered yet.
        /// </summary>
        private void OnExited()
        {
            if (_stopRequested || !_opened) return;
            _lostMidRun = true;
            Log.Warn($"[FfmpegCameraRecorder] the camera \"{DeviceName}\" stopped during the recording "
                     + $"(ffmpeg exited on its own) - the screen recording continues; camera.mp4 is truncated at "
                     + $"{CapturedSeconds:F1}s. See {_logPath}");
        }

        /// <summary>
        /// Stop the camera and finalize camera.mp4.
        ///
        /// This NEVER throws for a camera that already died (decision 4): the loss was reported when
        /// it happened, the screen recording is the artifact that matters, and turning it into a stop
        /// failure here would mark an otherwise clean recording as failed.
        ///
        /// It DOES throw <see cref="CameraStopFailedException"/> when ffmpeg survives the quit AND
        /// the kill (gate defect 2). That is not the same event at all: the process is alive, it owns
        /// the webcam and the output file, and the caller must report a failed stop rather than go
        /// idle. Safe to call again - <see cref="Dispose"/> does exactly that, and the retry is the
        /// last chance the recording has to get the device back.
        /// </summary>
        public void Stop()
        {
            bool firstCall = !_stopRequested;
            _stopRequested = true;
            if (_terminated) return;

            // Gate defect 4. Observed from the PROCESS, and observed here - before the quit below
            // makes "it has exited" ambiguous. A camera that died a moment before the user stopped,
            // whose Exited callback has not been delivered yet, is a mid-run loss, and the manifest
            // has to say so: writing CameraTruncated:false over a camera file that ends early tells
            // an editor the take is complete when it is not.
            if (_opened && _proc.HasExited && !_lostMidRun)
            {
                _lostMidRun = true;
                Log.Warn($"[FfmpegCameraRecorder] Stop: the camera \"{DeviceName}\" had already exited when the stop "
                         + $"arrived - camera.mp4 is truncated at {CapturedSeconds:F1}s. See {_logPath}");
            }

            if (firstCall)
                Log.Info($"[FfmpegCameraRecorder] Stop: camera=\"{DeviceName}\" captured={CapturedSeconds:F1}s "
                         + $"lostMidRun={_lostMidRun} reportedEncodedOutput={_wroteOutput}");

            if (!_proc.HasExited)
            {
                try
                {
                    _proc.SendQuit();
                }
                catch (Exception ex)
                {
                    // stdin closes when ffmpeg exits; that is the mid-run-loss case, already reported.
                    Log.Warn($"[FfmpegCameraRecorder] Stop: could not send 'q' to the camera ffmpeg "
                             + $"(\"{DeviceName}\"): {ex.Message}");
                }

                if (!_proc.WaitForExit(QuitTimeoutMs))
                {
                    Log.Warn($"[FfmpegCameraRecorder] Stop: the camera ffmpeg (\"{DeviceName}\") did not quit "
                             + $"within {QuitTimeoutMs / 1000}s - killing it; camera.mp4 may be truncated");
                    try { _proc.Kill(); }
                    catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Stop: kill failed for \"{DeviceName}\"", ex); }

                    if (!_proc.WaitForExit(KillTimeoutMs))
                    {
                        // The whole point of gate defect 2: this is NOT a stop. ffmpeg is alive, it
                        // still holds an exclusive DirectShow device and still owns camera.mp4.
                        // _terminated stays false, so Dispose gets one more attempt at it.
                        WriteFfmpegLog();
                        Log.Error($"[FfmpegCameraRecorder] Stop FAILED: the camera ffmpeg (\"{DeviceName}\") survived "
                                  + $"the kill and is still running - it still holds the camera and {OutputPath}");
                        throw new CameraStopFailedException(DeviceName, OutputPath, _logPath);
                    }
                }
            }

            _terminated = true;
            WriteFfmpegLog();
            Log.Info($"[FfmpegCameraRecorder] Stop: camera=\"{DeviceName}\" done, {CapturedSeconds:F1}s in {OutputPath}");
        }

        private void WriteFfmpegLog()
        {
            try { File.WriteAllText(_logPath, _stderr.ToString()); }
            catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] writing {_logPath} failed", ex); }
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

        /// <summary>
        /// Last owner of the process. Retries the stop when one is still owed - a stop that threw
        /// because ffmpeg would not die left <c>_terminated</c> false precisely so this can try
        /// again. This is an entry point (it runs from a using/finally and from the stop sequence),
        /// so it reports rather than propagates: the caller is usually already carrying a failure.
        /// </summary>
        public void Dispose()
        {
            if (!_terminated)
            {
                try { Stop(); }
                catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Dispose: stopping \"{DeviceName}\" failed", ex); }
            }
            _proc.Dispose();
        }
    }
}
