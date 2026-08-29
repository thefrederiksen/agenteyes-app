using System;

namespace AgentEyes.Video
{
    /// <summary>
    /// A camera process that AgentEyes started and may have failed to end - a recording's camera
    /// track (<see cref="FfmpegCameraRecorder"/>) or the preset editor's live preview
    /// (<see cref="FfmpegCameraPreview"/>).
    ///
    /// WHY THE TWO SHARE ONE TYPE. <c>StrandedCameraOwner</c> was written for issue #28's recorder,
    /// and its whole reason to exist - "keeping a handle inside an object that immediately becomes
    /// unreachable does not keep the process recoverable" - is about a LIVE ffmpeg HOLDING AN
    /// EXCLUSIVE WEBCAM, which is exactly what a surviving preview is too. Issue #35's Review Gate
    /// round 1 found the preview repeating the recorder's original defect in a different file, so the
    /// preview is given the owner that already exists rather than a second one written to the same
    /// description and free to drift from it.
    ///
    /// <see cref="IsAbandoned"/> is the load-bearing member and it is a QUESTION, not a stored
    /// answer: it asks the operating-system process itself, every time, so a row that says "this PID
    /// is stuck" stops saying it the moment the process is really gone.
    /// </summary>
    internal interface IStrandedCameraProcess : IDisposable
    {
        /// <summary>The exact DirectShow device the process is holding.</summary>
        string DeviceName { get; }

        /// <summary>The operating-system process id, or null before there is a process. This is what
        /// makes a stuck camera actionable - Task Manager, taskkill and Get-Process all take it.</summary>
        int? ProcessId { get; }

        /// <summary>The file the process owns and may still be writing, or null when it writes none
        /// (a live preview writes no file - it is a stream to a window).</summary>
        string? OutputPath { get; }

        /// <summary>
        /// True while a process AgentEyes ASKED TO DIE is STILL RUNNING - asked of the process
        /// itself on every read, never the remembered outcome of the attempt that failed.
        ///
        /// False before anything has been asked of it: a process nobody has tried to stop is not
        /// stranded, it is working.
        /// </summary>
        bool IsAbandoned { get; }
    }
}
