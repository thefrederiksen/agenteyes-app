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
        /// The size of the frames the CAMERA is producing, as ffmpeg reported them, or null when it
        /// has not said yet (issue #36).
        ///
        /// It is not the size of the buffers handed to <c>onFrame</c> - those are a fixed, padded
        /// 320x240. This is the un-padded camera frame, and it is the only way the preset editor can
        /// put the circle overlay where the picture actually is. NULL MEANS NOT OBSERVED and the
        /// caller must say so; it must never be read as "the picture fills the buffer".
        /// </summary>
        CameraFrameSize? SourceSize { get; }

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
