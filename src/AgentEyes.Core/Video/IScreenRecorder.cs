using System;

namespace AgentEyes.Video
{
    /// <summary>
    /// What <see cref="RecordingService"/> needs from the process recording the screen (issue #113,
    /// round two). <see cref="FfmpegRecorder"/> is the only production implementation.
    ///
    /// It exists so the service's wiring of the capture watchdog - start it with the capture, take
    /// it down before the writers stop, publish its verdict the moment it is reached, end the
    /// recording through <see cref="Abandon"/> - can be tested without launching ffmpeg. QA showed
    /// on round one that deleting the line that starts the watchdog left every test green; a test
    /// can only see that through a capture it controls.
    /// </summary>
    internal interface IScreenRecorder : IDisposable
    {
        /// <summary>The file the capture writes, and the one the watchdog watches.</summary>
        string OutputPath { get; }

        /// <summary>The command line that started the capture, for the manifest.</summary>
        string CommandLine { get; }

        /// <summary>
        /// The capture process's id. Issue #156 records it in the capture's cross-process claim
        /// (<c>capture.owner.json</c>), so the claim stays live while this writer runs even if the
        /// owning AgentEyes process dies. It is on the seam rather than read off the concrete
        /// <see cref="FfmpegRecorder"/> because the service holds the capture through this interface
        /// (#113): one property, owned by the interface, implemented once by the production recorder.
        /// </summary>
        int ProcessId { get; }

        /// <summary>When the capture process was started.</summary>
        DateTime StartedUtc { get; }

        /// <summary>The capture process's exit code once it has ended; null while it runs.</summary>
        int? ExitCode { get; }

        /// <summary>Seconds of output the capture last reported writing.</summary>
        double MediaSeconds { get; }

        /// <summary>
        /// The capture clock timestamp of the FIRST frame the capture read from the device, or null
        /// when it has not reported one (issue #114). The stop subtracts it from the microphone
        /// writer's own reading to align the two files, so the service needs it through this seam.
        /// See <see cref="FfmpegRecorder.InputStartSeconds"/> for why it is held atomically.
        /// </summary>
        double? InputStartSeconds { get; }

        /// <summary>Stop a healthy capture and let it finish its file.</summary>
        void Stop();

        /// <summary>End a capture already judged dead, at once. Throws when it cannot be ended.</summary>
        void Abandon(string reason);
    }
}
