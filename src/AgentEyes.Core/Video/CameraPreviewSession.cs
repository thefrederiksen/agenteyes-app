using System;

namespace AgentEyes.Video
{
    /// <summary>
    /// One running camera preview: something that is holding a camera and streaming frames until it
    /// is stopped. The interface exists so the preview LIFECYCLE (issue #29) can be tested on a
    /// machine with no camera - the state machine drives this, not ffmpeg.
    ///
    /// IT IS A <see cref="IStrandedCameraProcess"/> (issue #35, gate round 1, defect 4). A preview
    /// that survived its stop is a live ffmpeg holding an exclusive webcam, which is the same thing
    /// issue #28's recorder could become, so it is owned by the same
    /// <c>StrandedCameraOwner</c> rather than by a second mechanism written to the same description.
    /// </summary>
    internal interface ICameraPreviewSession : IStrandedCameraProcess
    {
        /// <summary>
        /// Release the camera. Must be idempotent, callable from ANY thread (a recording start calls
        /// it through <see cref="CameraDeviceArbiter"/>), and must not return until it has finished
        /// trying - the next thing that happens is a recording opening that same camera.
        ///
        /// IT DOES NOT PROMISE SUCCESS, AND IT MUST NEVER CLAIM IT (issue #35, gate round 1, defect
        /// 4). ffmpeg can ignore a kill, and a <c>Kill</c> can throw. Whether the device is actually
        /// free afterwards is answered by <see cref="IStrandedCameraProcess.IsAbandoned"/>, which
        /// asks the operating-system process itself - the caller reads that, it is not told.
        ///
        /// It may be called AGAIN after a stop that did not end the process: every call is a fresh
        /// termination attempt, which is what makes a retained session worth retaining
        /// (<c>StrandedCameraOwner.Recover</c>).
        /// </summary>
        void Stop();
    }

    /// <summary>
    /// Creates a preview session for one camera. <paramref name="onFrame"/> is raised once per frame
    /// with a fresh BGR24 buffer, <paramref name="onFailed"/> exactly once with a human-readable
    /// message naming the device when the camera cannot be opened or stops on its own. Both are
    /// raised on a background thread.
    /// </summary>
    internal delegate ICameraPreviewSession CameraPreviewSessionFactory(
        string deviceName, Action<byte[]> onFrame, Action<string> onFailed);
}
