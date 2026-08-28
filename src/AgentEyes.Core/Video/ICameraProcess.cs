using System;
using System.Diagnostics;

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

        /// <summary>Ask ffmpeg to quit cleanly ("q" on stdin) so the MP4 is finalized rather than
        /// truncated.</summary>
        void SendQuit();

        /// <summary>Wait up to <paramref name="milliseconds"/> for the process to end. False means
        /// it is STILL RUNNING - never "probably fine".</summary>
        bool WaitForExit(int milliseconds);

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

        public FfmpegCameraProcess(ProcessStartInfo psi, string deviceName)
        {
            if (psi == null) throw new ArgumentNullException(nameof(psi));
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new ArgumentException("a camera process must name its device", nameof(deviceName));
            _deviceName = deviceName;
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        }

        public void Start(Action<string> onStderrLine, Action onExited)
        {
            if (onStderrLine == null) throw new ArgumentNullException(nameof(onStderrLine));
            if (onExited == null) throw new ArgumentNullException(nameof(onExited));

            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onStderrLine(e.Data); };
            _proc.OutputDataReceived += (_, _) => { };
            _proc.Exited += (_, _) => onExited();

            if (!_proc.Start())
                throw new UsageException($"failed to start ffmpeg for the camera \"{_deviceName}\".");

            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
        }

        public bool HasExited => _proc.HasExited;

        public int ExitCode => _proc.ExitCode;

        public void SendQuit()
        {
            _proc.StandardInput.Write("q");
            _proc.StandardInput.Flush();
        }

        public bool WaitForExit(int milliseconds) => _proc.WaitForExit(milliseconds);

        public void Kill() => _proc.Kill(entireProcessTree: true);

        public void Dispose() => _proc.Dispose();
    }
}
