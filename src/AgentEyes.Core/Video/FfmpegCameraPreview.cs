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

        private readonly Process _proc;
        private readonly Action<byte[]> _onFrame;
        private readonly Action<string> _onFailed;
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private Thread? _reader;

        /// <summary>0 while running, 1 once <see cref="Stop"/> has taken ownership of the shutdown.</summary>
        private int _stopped;

        /// <summary>1 once <see cref="_onFailed"/> has been raised, so a failure is reported once.</summary>
        private int _reportedFailure;

        private long _frames;

        /// <summary>The exact DirectShow device name this preview is holding.</summary>
        public string DeviceName { get; }

        /// <summary>How many complete frames have been delivered to the caller.</summary>
        public long FramesDelivered => Interlocked.Read(ref _frames);

        private FfmpegCameraPreview(Process proc, string deviceName, Action<byte[]> onFrame, Action<string> onFailed)
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
            if (onFrame == null) throw new ArgumentNullException(nameof(onFrame));
            if (onFailed == null) throw new ArgumentNullException(nameof(onFailed));

            string exe = FfmpegLocator.Ffmpeg();
            var args = FfmpegArgs.CameraPreview(deviceName, FrameWidth, FrameHeight, FrameRate);
            string cmd = FfmpegArgs.ToCommandLine(exe, args);
            Log.Info($"[FfmpegCameraPreview] Start: camera=\"{deviceName}\" {FrameWidth}x{FrameHeight}@{FrameRate} cmd={cmd}");

            var psi = new ProcessStartInfo
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

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var preview = new FfmpegCameraPreview(proc, deviceName, onFrame, onFailed);

            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) preview._stderr.AppendLine(e.Data); };

            if (!proc.Start())
                throw new UsageException($"failed to start ffmpeg for the camera preview of \"{deviceName}\".");
            proc.BeginErrorReadLine();

            preview._reader = new Thread(preview.ReadFrames)
            {
                IsBackground = true,
                Name = "AgentEyes camera preview",
            };
            preview._reader.Start();
            return preview;
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
                var stdout = _proc.StandardOutput.BaseStream;
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
            string err = _stderr.ToString();
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
        /// Release the camera. Idempotent, safe from any thread, and it does not return until the
        /// ffmpeg process is gone - a recording start is waiting on exactly that (issue #29, AC7).
        ///
        /// A KILL rather than the recorder's graceful "q": there is no output file to finalize, and
        /// the only thing that matters here is how fast the exclusive device is handed back.
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            var sw = Stopwatch.StartNew();
            if (!_proc.HasExited)
            {
                try { _proc.Kill(entireProcessTree: true); }
                catch (Exception ex)
                {
                    Log.Error($"[FfmpegCameraPreview] Stop: killing the preview ffmpeg for \"{DeviceName}\" failed", ex);
                }
            }
            if (!_proc.WaitForExit(KillWaitMs))
                Log.Error($"[FfmpegCameraPreview] Stop: the preview ffmpeg for \"{DeviceName}\" did not exit within " +
                          $"{KillWaitMs}ms - the camera may still be held");

            // Joining from the reader thread itself would be waiting on ourselves: a failure raised
            // from ReadFrames can land here through the owner disposing the session.
            var reader = _reader;
            if (reader != null && reader != Thread.CurrentThread) reader.Join(KillWaitMs);

            Log.Info($"[FfmpegCameraPreview] Stop: camera=\"{DeviceName}\" released in {sw.ElapsedMilliseconds}ms " +
                     $"after {FramesDelivered} frame(s)");
        }

        public void Dispose()
        {
            Stop();
            _proc.Dispose();
        }
    }
}
