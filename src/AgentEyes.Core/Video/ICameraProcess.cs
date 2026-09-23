using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AgentEyes.Video
{
    /// <summary>
    /// The OS process behind a camera track, as <see cref="FfmpegCameraRecorder"/> actually uses it
    /// (issue #28, Review Gate round 2).
    ///
    /// WHY THIS SEAM EXISTS. The gate rejected the first implementation with five blocking defects,
    /// and four of them live entirely on paths a real ffmpeg will not take on demand: a device that
    /// takes LONGER than the start probe to fail, an ffmpeg that ignores "q" and then survives a
    /// kill, an exit notification that has not been delivered yet at the instant the user stops.
    /// None of those are reachable through <see cref="Process"/> from a test, which is exactly why
    /// the merged code carried no test over Start, Stop, process ownership, termination failure, or
    /// the exit/stop race - the gate found five calls to a string helper and nothing else. Behind
    /// this interface each of those paths is one line of a fake.
    ///
    /// It is deliberately the SMALLEST surface that covers the ownership decisions: start, observe,
    /// ask to quit, wait, kill, dispose. Nothing about ffmpeg, its arguments, or its files is here -
    /// that all stays in the recorder.
    /// </summary>
    internal interface ICameraProcess : IDisposable
    {
        /// <summary>
        /// Wire the two notifications and start the process. <paramref name="onStderrLine"/> is
        /// called for every stderr line ffmpeg writes (its "time=" progress ticks arrive that way),
        /// and <paramref name="onExited"/> when the process ends on its own. Throws when the process
        /// cannot be started at all.
        /// </summary>
        void Start(Action<string> onStderrLine, Action onExited);

        /// <summary>True once the OS process has ended. The only authority on that - a delivered
        /// exit CALLBACK is a convenience, this is the fact.</summary>
        bool HasExited { get; }

        /// <summary>The process exit code. Read only while <see cref="HasExited"/> is true and
        /// before <see cref="IDisposable.Dispose"/>, which releases the handle it needs.</summary>
        int ExitCode { get; }

        /// <summary>
        /// The operating system process id, or null before the process is started (issue #28,
        /// AC16).
        ///
        /// It exists so that a camera ffmpeg AgentEyes could not kill can be named to the person who
        /// has to deal with it. "A camera process is stuck" is not actionable; "PID 24512 is stuck"
        /// is - it is what Task Manager, taskkill and Get-Process all take. Captured at start and
        /// held, because reading it off a Process AFTER the handle is released throws.
        /// </summary>
        int? ProcessId { get; }

        /// <summary>Ask ffmpeg to quit cleanly ("q" on stdin) so the MP4 is finalized rather than
        /// truncated.</summary>
        void SendQuit();

        /// <summary>Wait up to <paramref name="milliseconds"/> for the process to end. False means
        /// it is STILL RUNNING - never "probably fine".</summary>
        bool WaitForExit(int milliseconds);

        /// <summary>
        /// Wait up to <paramref name="milliseconds"/> for the process's redirected stderr to reach
        /// END OF STREAM, i.e. for every line it ever wrote to have been handed to the callback
        /// given to <see cref="Start"/> (issue #28, gate round 3, defect 3).
        ///
        /// It exists because the recorder draws a conclusion from an ABSENCE - "ffmpeg never
        /// reported writing any output, so camera.mp4 is empty" - and an absence read off a stream
        /// that is still being delivered is not an absence at all. .NET's
        /// <c>Process.WaitForExit(int)</c> deliberately does NOT flush the asynchronous readers, so
        /// a process can be gone while its last progress tick is still in flight. This waits for the
        /// EOF the reader itself reports, which is a presence, and it is bounded so a stuck pipe
        /// cannot hang a stop.
        ///
        /// Returns true when EOF was observed. False means the stderr is INCOMPLETE - the caller
        /// must not read "no tick arrived" as "no tick was ever written".
        /// </summary>
        bool DrainStderr(int milliseconds);

        /// <summary>Kill the process and everything it started.</summary>
        void Kill();
    }

    /// <summary>
    /// The real <see cref="ICameraProcess"/>: one ffmpeg process, started from an already-configured
    /// <see cref="ProcessStartInfo"/>.
    ///
    /// This class holds NO policy. Every decision the gate rejected - how long to probe, what counts
    /// as opened, what a failed kill means - lives in <see cref="FfmpegCameraRecorder"/>, where a
    /// test can reach it. All this does is speak to the operating system.
    /// </summary>
    internal sealed class FfmpegCameraProcess : ICameraProcess
    {
        private readonly Process _proc;
        private readonly string _deviceName;

        /// <summary>
        /// Set when the redirected stderr reports END OF STREAM (the <c>ErrorDataReceived</c> event
        /// with a null <c>Data</c>). It is the only PRESENCE that says "ffmpeg wrote nothing more" -
        /// see <see cref="ICameraProcess.DrainStderr"/>.
        /// </summary>
        private readonly ManualResetEventSlim _stderrEof = new(initialState: false);

        /// <summary>The OS process id, captured the instant Start succeeded. Held rather than read
        /// on demand because <see cref="Process.Id"/> throws once the handle has been released, and
        /// the one moment this is needed is a stop that went wrong.</summary>
        private int? _pid;

        /// <summary>
        /// The HUD's live camera preview, or null (issue #33). When present, ffmpeg's command line
        /// carries a second MJPEG output on STDOUT and this tap owns that pipe. It is the ONLY way to
        /// preview the camera during a recording: ffmpeg holds the DirectShow device exclusively, so
        /// the preview cannot open it a second time (assumption C1).
        /// </summary>
        private readonly Preview.PreviewTap? _preview;

        public FfmpegCameraProcess(ProcessStartInfo psi, string deviceName, Preview.PreviewTap? preview = null)
        {
            if (psi == null) throw new ArgumentNullException(nameof(psi));
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new ArgumentException("a camera process must name its device", nameof(deviceName));
            _deviceName = deviceName;
            _preview = preview;
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        }

        public void Start(Action<string> onStderrLine, Action onExited)
        {
            if (onStderrLine == null) throw new ArgumentNullException(nameof(onStderrLine));
            if (onExited == null) throw new ArgumentNullException(nameof(onExited));

            // A null Data is the reader's END OF STREAM, not a blank line: it fires once, after the
            // last real line has been delivered. That is what DrainStderr waits for.
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) onStderrLine(e.Data);
                else _stderrEof.Set();
            };
            // Stdout carries the preview stream when there is a tap, and nothing at all when there is
            // not. The two readers are mutually exclusive by construction: BeginOutputReadLine takes
            // the stream for itself, so wiring both would leave the preview reading a closed pipe.
            if (_preview == null) _proc.OutputDataReceived += (_, _) => { };
            _proc.Exited += (_, _) => onExited();

            if (!_proc.Start())
                throw new UsageException($"failed to start ffmpeg for the camera \"{_deviceName}\".");

            _pid = _proc.Id;

            _proc.BeginErrorReadLine();
            // The pipe starts filling the instant ffmpeg produces a frame, and a full pipe blocks the
            // process WRITING CAMERA.MP4 - so the drain starts here, with the process, and never later.
            if (_preview == null) _proc.BeginOutputReadLine();
            else _preview.Pump(_proc.StandardOutput.BaseStream);
        }

        public bool HasExited => _proc.HasExited;

        public int ExitCode => _proc.ExitCode;

        public int? ProcessId => _pid;

        public void SendQuit()
        {
            _proc.StandardInput.Write("q");
            _proc.StandardInput.Flush();
        }

        public bool WaitForExit(int milliseconds) => _proc.WaitForExit(milliseconds);

        public bool DrainStderr(int milliseconds) => _stderrEof.Wait(milliseconds);

        public void Kill() => _proc.Kill(entireProcessTree: true);

        /// <summary>
        /// Releases the process HANDLE. It does NOT terminate the OS process - which is exactly why
        /// <see cref="FfmpegCameraRecorder"/> refuses to call it while the process is still alive
        /// (issue #28, gate round 3, defects 1 and 2): closing the last handle to a live ffmpeg does
        /// not stop it recording, it only makes it unreachable.
        /// </summary>
        public void Dispose()
        {
            _proc.Dispose();
            _stderrEof.Dispose();
        }
    }

    /// <summary>
    /// The OS process behind a camera PREVIEW, as <see cref="FfmpegCameraPreview"/> actually uses it
    /// (issue #35, Review Gate round 1, defect 4).
    ///
    /// WHY THIS SEAM EXISTS. It is the same argument <see cref="ICameraProcess"/> makes one class
    /// up, applied to the file the gate found the defect in: "ffmpeg ignored the kill" and "Kill
    /// threw" are not states a real ffmpeg can be asked to enter, so without a seam the preview's
    /// ownership decisions - the ones that decide whether a live process holding the webcam stays
    /// reachable - carried no test at all. Behind this interface each of them is one line of a fake.
    ///
    /// WHY IT IS NOT <see cref="ICameraProcess"/> ITSELF. A preview IS its stdout - the frames are
    /// read off that stream by hand - while the recorder redirects stdout and hands it to an
    /// asynchronous line reader; the two cannot both have it. And a preview writes no file, so it
    /// never sends "q" and has nothing to finalize. Widening the recorder's seam to carry a stream
    /// only the preview may touch would put a trap in issue #28's code. The two agree on the part
    /// that is being fixed here, and it is stated identically in both:
    /// <see cref="HasExited"/> is the only authority on whether the device is still held.
    /// </summary>
    internal interface ICameraPreviewProcess : IDisposable
    {
        /// <summary>Wire the stderr notification and start the process. <paramref name="onStderrLine"/>
        /// is called for every stderr line ffmpeg writes. Throws when it cannot be started at all.</summary>
        void Start(Action<string> onStderrLine);

        /// <summary>The raw stdout stream the preview frames arrive on.</summary>
        Stream StandardOutput { get; }

        /// <summary>True once the OS process has ended. The only authority on that.</summary>
        bool HasExited { get; }

        /// <summary>The process exit code. Read only while <see cref="HasExited"/> is true and before
        /// <see cref="IDisposable.Dispose"/>, which releases the handle it needs.</summary>
        int ExitCode { get; }

        /// <summary>The operating-system process id, or null before the process is started. It is
        /// what names a preview ffmpeg nobody could kill to the person who has to deal with it.</summary>
        int? ProcessId { get; }

        /// <summary>Wait up to <paramref name="milliseconds"/> for the process to end. False means it
        /// is STILL RUNNING - never "probably fine".</summary>
        bool WaitForExit(int milliseconds);

        /// <summary>Kill the process and everything it started. May throw - a kill is a request to
        /// the operating system, not an outcome.</summary>
        void Kill();
    }

    /// <summary>
    /// The real <see cref="ICameraPreviewProcess"/>: one ffmpeg streaming raw frames on stdout.
    ///
    /// It holds NO policy, exactly as <see cref="FfmpegCameraProcess"/> holds none. How long to wait
    /// for a kill, what a refused kill means, and when the handle may be released all live in
    /// <see cref="FfmpegCameraPreview"/>, where a test can reach them.
    /// </summary>
    internal sealed class FfmpegPreviewProcess : ICameraPreviewProcess
    {
        private readonly Process _proc;
        private readonly string _deviceName;

        /// <summary>The OS process id, captured the instant Start succeeded. Held rather than read on
        /// demand because <see cref="Process.Id"/> throws once the handle has been released, and the
        /// one moment it is needed is a stop that went wrong.</summary>
        private int? _pid;

        public FfmpegPreviewProcess(ProcessStartInfo psi, string deviceName)
        {
            if (psi == null) throw new ArgumentNullException(nameof(psi));
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new ArgumentException("a camera preview process must name its device", nameof(deviceName));
            _deviceName = deviceName;
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        }

        public void Start(Action<string> onStderrLine)
        {
            if (onStderrLine == null) throw new ArgumentNullException(nameof(onStderrLine));

            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onStderrLine(e.Data); };

            if (!_proc.Start())
                throw new UsageException($"failed to start ffmpeg for the camera preview of \"{_deviceName}\".");

            _pid = _proc.Id;
            _proc.BeginErrorReadLine();
        }

        public Stream StandardOutput => _proc.StandardOutput.BaseStream;

        public bool HasExited => _proc.HasExited;

        public int ExitCode => _proc.ExitCode;

        public int? ProcessId => _pid;

        public bool WaitForExit(int milliseconds) => _proc.WaitForExit(milliseconds);

        public void Kill() => _proc.Kill(entireProcessTree: true);

        /// <summary>
        /// Releases the process HANDLE. It does NOT terminate the OS process - which is exactly why
        /// <see cref="FfmpegCameraPreview"/> refuses to call it while the process is still alive
        /// (issue #35, gate round 1, defect 4, which is issue #28's gate round 3 defects 1 and 2 in
        /// a different file): closing the last handle to a live ffmpeg does not take it off the
        /// webcam, it only makes it unreachable.
        /// </summary>
        public void Dispose() => _proc.Dispose();
    }
}
