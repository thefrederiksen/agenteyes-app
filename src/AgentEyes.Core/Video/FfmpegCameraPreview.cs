using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AgentEyes.Video
{
    /// <summary>
    /// A live camera preview (issue #29): an ffmpeg process streaming RAW BGR24 frames of a fixed
    /// size on stdout, read here and handed to the caller a frame at a time.
    ///
    /// It is deliberately NOT a mode of <see cref="FfmpegCameraRecorder"/>. The two want opposite
    /// things at exactly the point that matters:
    ///
    ///  - the recorder is stopped with "q" on stdin and waits up to 8 seconds, because it has an
    ///    MP4 to finalize. A preview writes NO FILE, so there is nothing to finalize and nothing to
    ///    truncate - it is stopped with a KILL, which is the fastest guaranteed way to hand the
    ///    exclusive DirectShow device back. Issue #29 makes that release the central requirement:
    ///    the preview must be out of the way before the recording opens the same camera;
    ///  - the recorder treats a camera that will not open as a failed recording. A preview treats it
    ///    as a message to put on screen, naming the device (AC6), with the editor still fully usable.
    ///
    /// The device-open diagnosis is SHARED with the recorder (<see
    /// cref="FfmpegCameraRecorder.DiagnoseOpenFailure"/>) rather than written a second time, so a
    /// camera held by another application reads the same way in both places.
    ///
    /// A KILL IS A REQUEST, NOT AN OUTCOME (issue #35, Review Gate round 1, defect 4 - which is
    /// issue #28's original bug in a different file). This class used to catch a failed
    /// <c>Process.Kill</c>, log a still-running process after the wait, then announce
    /// unconditionally that the camera had been released and dispose the process WRAPPER - and
    /// disposing a wrapper does not terminate an operating-system process, it only throws away the
    /// last handle able to reach a live ffmpeg sitting on the webcam. So:
    ///
    ///  - <see cref="Stop"/> makes no claim at all. It attempts, it waits, and it reports what it
    ///    saw to the log. What is true afterwards is <see cref="IsAbandoned"/>, which asks the
    ///    process itself on every read.
    ///  - <see cref="Stop"/> may be called AGAIN. Every call is a fresh termination attempt, so a
    ///    session that survived one is worth retaining - which is what
    ///    <c>StrandedCameraOwner.Recover</c> does with it.
    ///  - <see cref="Dispose"/> releases the handle ONLY once the process is CONFIRMED gone. While
    ///    it survives, this object stays valid, stays loud, and stays stoppable.
    /// </summary>
    internal sealed class FfmpegCameraPreview : ICameraPreviewSession
    {
        /// <summary>Preview frame width, in pixels (issue #29, assumption B2).</summary>
        public const int FrameWidth = 320;

        /// <summary>Preview frame height, in pixels (issue #29, assumption B2).</summary>
        public const int FrameHeight = 240;

        /// <summary>Preview frames per second (issue #29, assumption B2).</summary>
        public const int FrameRate = 10;

        /// <summary>Bytes in one BGR24 preview frame - the unit the reader counts in.</summary>
        public const int FrameBytes = FrameWidth * FrameHeight * 3;

        /// <summary>How long a killed ffmpeg is given to actually die before we stop waiting.</summary>
        private const int KillWaitMs = 3000;

        private readonly ICameraPreviewProcess _proc;
        private readonly Action<byte[]> _onFrame;
        private readonly Action<string> _onFailed;
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly Stopwatch _since = Stopwatch.StartNew();

        /// <summary>Serializes termination attempts. A stop can take seconds, and a recording start
        /// and a closing dialog can both be inside one - two kills racing over the same handle would
        /// make "is it still running?" unanswerable at exactly the moment it matters.</summary>
        private readonly object _stopGate = new object();

        private Thread? _reader;

        /// <summary>0 while running, 1 once <see cref="Stop"/> has been asked for at least once. It
        /// is what turns "this process is alive" into "this process is ABANDONED".</summary>
        private int _stopped;

        /// <summary>1 once the process was CONFIRMED gone and the handle was released. After that
        /// the handle must not be touched again - reading it throws.</summary>
        private int _handleReleased;

        /// <summary>1 once <see cref="_onFailed"/> has been raised, so a failure is reported once.</summary>
        private int _reportedFailure;

        private long _frames;

        /// <summary>
        /// The camera's own frame size once ffmpeg has printed it (issue #36). Written by the stderr
        /// callback thread and read by the UI thread, so it is guarded by <see cref="_stderr"/>'s own
        /// lock rather than being a torn read of two ints.
        /// </summary>
        private CameraFrameSize? _sourceSize;

        /// <summary>The exact DirectShow device name this preview is holding.</summary>
        public string DeviceName { get; }

        /// <summary>The operating-system process id of the preview ffmpeg.</summary>
        public int? ProcessId => _proc.ProcessId;

        /// <summary>A preview writes no file - it is a stream to a window. Null, and said rather
        /// than faked with a path that does not exist.</summary>
        public string? OutputPath => null;

        /// <summary>
        /// True while a preview ffmpeg this object ASKED TO DIE is STILL RUNNING.
        ///
        /// It asks the process on every read (never the remembered outcome of the kill that failed),
        /// and it is false before any stop has been attempted - a process nobody has tried to end is
        /// not stranded, it is previewing. Once the handle has been released the process is
        /// confirmed gone, so the answer is false and the released handle is never touched.
        /// </summary>
        public bool IsAbandoned =>
            Volatile.Read(ref _stopped) != 0 && Volatile.Read(ref _handleReleased) == 0 && !_proc.HasExited;

        /// <summary>
        /// The size of the frames the CAMERA is producing, straight out of ffmpeg's "Input #0"
        /// report, or null while ffmpeg has not said (issue #36). The frames handed to the caller are
        /// a padded <see cref="FrameWidth"/>x<see cref="FrameHeight"/> regardless; this is what the
        /// picture inside that padding actually is.
        /// </summary>
        public CameraFrameSize? SourceSize
        {
            get { lock (_stderr) { return _sourceSize; } }
        }

        /// <summary>How many complete frames have been delivered to the caller.</summary>
        public long FramesDelivered => Interlocked.Read(ref _frames);

        private FfmpegCameraPreview(ICameraPreviewProcess proc, string deviceName, Action<byte[]> onFrame,
            Action<string> onFailed)
        {
            _proc = proc;
            DeviceName = deviceName;
            _onFrame = onFrame;
            _onFailed = onFailed;
        }

        /// <summary>
        /// Open <paramref name="deviceName"/> and start streaming preview frames.
        ///
        /// This returns as soon as the process is running - it does NOT wait for the first frame and
        /// does NOT probe the open the way the recorder does, because the caller is a dialog that has
        /// to stay interactive (issue #29, AC2). A camera that cannot be opened is reported through
        /// <paramref name="onFailed"/> a moment later, not thrown from here.
        /// </summary>
        public static FfmpegCameraPreview Start(string deviceName, Action<byte[]> onFrame, Action<string> onFailed)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new UsageException("a camera preview needs an exact DirectShow device name.");

            string exe = FfmpegLocator.Ffmpeg();
            var args = FfmpegArgs.CameraPreview(deviceName, FrameWidth, FrameHeight, FrameRate);
            string cmd = FfmpegArgs.ToCommandLine(exe, args);
            Log.Info($"[FfmpegCameraPreview] Start: camera=\"{deviceName}\" {FrameWidth}x{FrameHeight}@{FrameRate} cmd={cmd}");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args) psi.ArgumentList.Add(a);

            return Start(new FfmpegPreviewProcess(psi, deviceName), deviceName, onFrame, onFailed);
        }

        /// <summary>
        /// The same start, over an injected process. This is the seam the ownership decisions are
        /// tested through (issue #35): "ffmpeg ignored the kill" and "Kill threw" are not states a
        /// real ffmpeg can be asked to enter.
        /// </summary>
        internal static FfmpegCameraPreview Start(ICameraPreviewProcess proc, string deviceName,
            Action<byte[]> onFrame, Action<string> onFailed)
        {
            if (proc == null) throw new ArgumentNullException(nameof(proc));
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new UsageException("a camera preview needs an exact DirectShow device name.");
            if (onFrame == null) throw new ArgumentNullException(nameof(onFrame));
            if (onFailed == null) throw new ArgumentNullException(nameof(onFailed));

            var preview = new FfmpegCameraPreview(proc, deviceName, onFrame, onFailed);
            proc.Start(preview.OnStderrLine);

            preview._reader = new Thread(preview.ReadFrames)
            {
                IsBackground = true,
                Name = "AgentEyes camera preview",
            };
            preview._reader.Start();
            return preview;
        }

        /// <summary>
        /// One line of ffmpeg's stderr. Two jobs: keep the whole log for the failure diagnosis, and
        /// pick the CAMERA's frame size out of the "Input #0" block the first time it appears
        /// (issue #36). The second stops the moment it succeeds, so a long-running preview is not
        /// re-parsing a growing buffer.
        /// </summary>
        private void OnStderrLine(string line)
        {
            lock (_stderr)
            {
                _stderr.AppendLine(line);
                if (_sourceSize != null) return;
                var found = CameraFrameSize.FromFfmpegLog(_stderr.ToString());
                if (found == null) return;
                _sourceSize = found;
                Log.Info($"[FfmpegCameraPreview] camera \"{DeviceName}\" is producing {found} frames "
                         + $"(previewed padded into {FrameWidth}x{FrameHeight})");
            }
        }

        /// <summary>
        /// Pull complete frames off ffmpeg's stdout for as long as it keeps producing them. The frame
        /// size is fixed, so a frame is complete when exactly <see cref="FrameBytes"/> bytes have
        /// arrived - a short read is a partial frame, never a frame.
        /// </summary>
        private void ReadFrames()
        {
            // The reader thread IS an entry point: it is the top of its own call stack, and anything
            // that escapes here would take the process down rather than the preview.
            try
            {
                var stdout = _proc.StandardOutput;
                var frame = new byte[FrameBytes];

                while (Volatile.Read(ref _stopped) == 0)
                {
                    int have = 0;
                    while (have < FrameBytes)
                    {
                        int read = stdout.Read(frame, have, FrameBytes - have);
                        if (read <= 0) { ReportEnded(partialBytes: have); return; }
                        have += read;
                    }

                    if (Volatile.Read(ref _stopped) != 0) return;

                    long n = Interlocked.Increment(ref _frames);
                    if (n == 1)
                        Log.Info($"[FfmpegCameraPreview] first frame from \"{DeviceName}\" after {_since.ElapsedMilliseconds}ms");

                    // A fresh buffer per frame: the frame crosses to the UI thread, so it must not be
                    // the same array the next read is already overwriting.
                    var copy = new byte[FrameBytes];
                    Buffer.BlockCopy(frame, 0, copy, 0, FrameBytes);
                    _onFrame(copy);
                }
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _stopped) != 0) return;   // the pipe died because we killed it
                Log.Error($"[FfmpegCameraPreview] reading frames from \"{DeviceName}\" FAILED", ex);
                RaiseFailure($"The camera \"{DeviceName}\" preview stopped: {ex.Message}");
            }
        }

        /// <summary>
        /// ffmpeg closed its stdout while we were still previewing. Turn that into a message that
        /// names the device: whether it never opened at all (the common case - the camera is held by
        /// another application) or died after producing frames (unplugged mid-preview).
        /// </summary>
        private void ReportEnded(int partialBytes)
        {
            if (Volatile.Read(ref _stopped) != 0) return;

            _proc.WaitForExit(KillWaitMs);
            int exitCode = _proc.HasExited ? _proc.ExitCode : -1;
            string err;
            lock (_stderr) { err = _stderr.ToString(); }
            long delivered = FramesDelivered;

            if (delivered == 0)
            {
                Log.Error($"[FfmpegCameraPreview] the camera \"{DeviceName}\" could not be opened for preview " +
                          $"(ffmpeg exit={exitCode}){Environment.NewLine}{err}");
                RaiseFailure($"The camera \"{DeviceName}\" could not be opened: "
                             + FfmpegCameraRecorder.DiagnoseOpenFailure(err, DeviceName));
                return;
            }

            Log.Warn($"[FfmpegCameraPreview] the camera \"{DeviceName}\" stopped during the preview after " +
                     $"{delivered} frame(s) (ffmpeg exit={exitCode}, {partialBytes} byte(s) of a partial frame discarded)");
            RaiseFailure($"The camera \"{DeviceName}\" stopped sending frames (was it unplugged?).");
        }

        private void RaiseFailure(string message)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            _onFailed(message);
        }

        /// <summary>
        /// ONE TERMINATION ATTEMPT. Safe from any thread, repeatable, and it does not return until it
        /// has finished trying - a recording start is waiting on exactly that (issue #29, AC7).
        ///
        /// A KILL rather than the recorder's graceful "q": there is no output file to finalize, and
        /// the only thing that matters here is how fast the exclusive device is handed back.
        ///
        /// IT PROMISES NOTHING AND CLAIMS NOTHING (issue #35, gate round 1, defect 4). ffmpeg can
        /// ignore a kill and <c>Kill</c> can throw; either way this returns having attempted, and
        /// <see cref="IsAbandoned"/> - which asks the process - is what says whether the camera is
        /// free. Calling it again performs another attempt, which is what makes a retained session
        /// recoverable rather than a museum piece.
        /// </summary>
        public void Stop()
        {
            bool firstAttempt = Interlocked.Exchange(ref _stopped, 1) == 0;

            lock (_stopGate)
            {
                // The handle is released only when the process was confirmed gone, so this is the
                // one state in which there is nothing left to do and nothing left to read.
                if (Volatile.Read(ref _handleReleased) != 0) return;

                var sw = Stopwatch.StartNew();
                if (!_proc.HasExited)
                {
                    // Entry point for the operating system's answer: a Kill that throws is a FAILED
                    // attempt and is recorded as one. It is never allowed to read as a release - that
                    // is the defect this method exists to remove.
                    try { _proc.Kill(); }
                    catch (Exception ex)
                    {
                        Log.Error($"[FfmpegCameraPreview] Stop: killing the preview ffmpeg for \"{DeviceName}\" " +
                                  $"(PID {ProcessId?.ToString() ?? "unknown"}) FAILED - the camera is NOT released", ex);
                    }

                    _proc.WaitForExit(KillWaitMs);
                }

                // Joining from the reader thread itself would be waiting on ourselves: a failure
                // raised from ReadFrames can land here through the owner disposing the session.
                if (firstAttempt)
                {
                    var reader = _reader;
                    if (reader != null && reader != Thread.CurrentThread) reader.Join(KillWaitMs);
                }

                // ASK THE PROCESS. Not the wait's return value, not the fact that a kill was issued -
                // the process itself, which is the only authority on whether it still holds the
                // camera.
                if (!_proc.HasExited)
                {
                    Log.Error($"[FfmpegCameraPreview] Stop: the preview ffmpeg for \"{DeviceName}\" " +
                              $"(PID {ProcessId?.ToString() ?? "unknown"}) is STILL RUNNING after {sw.ElapsedMilliseconds}ms " +
                              "- it still holds the camera. The camera has NOT been released and this session can be " +
                              "stopped again.");
                    return;
                }

                Log.Info($"[FfmpegCameraPreview] Stop: camera=\"{DeviceName}\" CONFIRMED released in " +
                         $"{sw.ElapsedMilliseconds}ms after {FramesDelivered} frame(s)");
            }
        }

        /// <summary>
        /// Last owner of the process. Attempts the stop, then releases the process HANDLE ONLY if the
        /// operating-system process is confirmed gone.
        ///
        /// GATE ROUND 1, DEFECT 4 (and issue #28's gate round 3, defects 1 and 2, which is the same
        /// defect in the recorder). Disposing the wrapper of a live ffmpeg does not take it off the
        /// webcam; it throws away the last thing in this process able to reach it. So a surviving
        /// preview keeps its handle, and this object stays valid and stays stoppable -
        /// <c>StrandedCameraOwner</c> is what holds on to it.
        /// </summary>
        public void Dispose()
        {
            Stop();

            if (IsAbandoned)
            {
                Log.Error($"[FfmpegCameraPreview] Dispose: the preview ffmpeg for \"{DeviceName}\" " +
                          $"(PID {ProcessId?.ToString() ?? "unknown"}) is STILL RUNNING - it still holds the camera. " +
                          "The process handle is KEPT (releasing it would not end the process, only hide it); this " +
                          "session can still be stopped again.");
                return;
            }

            if (Interlocked.Exchange(ref _handleReleased, 1) != 0) return;
            _proc.Dispose();
        }
    }
}
