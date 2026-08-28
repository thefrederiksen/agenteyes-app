using System;

namespace AgentEyes.Video
{
    /// <summary>
    /// One running camera preview: something that is holding a camera and streaming frames until it
    /// is stopped. The interface exists so the preview LIFECYCLE (issue #29) can be tested on a
    /// machine with no camera - the state machine drives this, not ffmpeg.
    /// </summary>
    internal interface ICameraPreviewSession : IDisposable
    {
        /// <summary>The exact DirectShow device name this session is holding.</summary>
        string DeviceName { get; }

        /// <summary>
        /// Release the camera. Must be idempotent, callable from ANY thread (a recording start calls
        /// it through <see cref="CameraDeviceArbiter"/>), and must not return until the device is
        /// actually free - the next thing that happens is a recording opening that same camera.
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
