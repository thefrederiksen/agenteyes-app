using System;
using System.Threading.Tasks;
using AgentEyes;
using AgentEyes.Video;

namespace AgentEyes.App
{
    /// <summary>Where a camera preview is in its life (issue #29).</summary>
    internal enum CameraPreviewState
    {
        /// <summary>Nothing is held. The only state in which a camera is free for a recording.</summary>
        Stopped,

        /// <summary>A camera has been asked for; no frame has arrived yet.</summary>
        Starting,

        /// <summary>Frames are arriving.</summary>
        Running,

        /// <summary>The camera could not be opened, or stopped on its own. Nothing is held.</summary>
        Failed,
    }

    /// <summary>
    /// The lifecycle of the preset editor's live camera preview (issue #29) - separated from the
    /// dialog on purpose, so the rule that actually matters can be TESTED without a camera: after
    /// every exit path, no session is held.
    ///
    /// The exit paths are the whole point. A DirectShow camera is exclusive, so a preview still
    /// holding the device when a recording starts BREAKS that recording. There are five ways out and
    /// each one lands on <see cref="Stop"/>:
    ///
    ///  1. the user picks a different camera        -> <see cref="Select"/> stops the previous one
    ///  2. the user picks "(None)"                  -> <see cref="Select"/> with null
    ///  3. the preset leaves Video mode             -> the editor calls <see cref="Stop"/>
    ///  4. the dialog closes, by ANY route          -> <see cref="Dispose"/> from Window.Closed
    ///  5. a recording opens the camera             -> <see cref="CameraDeviceArbiter"/> calls in
    ///
    /// The session is created on a background thread and every callback arrives on one, so nothing
    /// here touches the UI: a recording start blocks on path 5 until the device is free, and it must
    /// never be waiting on a busy UI thread to get it.
    /// </summary>
    internal sealed class CameraPreviewController : IDisposable
    {
        /// <summary>What the status line shows between the camera being asked for and its first frame.</summary>
        public const string StartingStatus = "Starting camera...";

        /// <summary>What the status line shows when the picker is on "(None)".</summary>
        public const string NoCameraStatus = "No camera selected.";

        /// <summary>How long a stop waits for an in-flight camera open to finish before it gives up
        /// and says so. Opening is a process launch, so this is generous by design.</summary>
        private const int OpenWaitMs = 5000;

        private readonly CameraPreviewSessionFactory _factory;
        private readonly Func<string, bool> _releaseForRecording;
        private readonly object _gate = new object();

        /// <summary>
        /// Identifies the CURRENT attempt. Every callback carries the token it was created with, so a
        /// frame or a failure from a session that has already been stopped is dropped instead of
        /// re-animating a preview the user has moved on from.
        /// </summary>
        private object _generation = new object();

        private ICameraPreviewSession? _session;

        /// <summary>
        /// The in-flight open, if any. A stop MUST wait for it: between the factory launching the
        /// camera process and this class learning about it there is an instant in which the device is
        /// held by a session nobody has a handle to yet, and a recording starting in that instant
        /// would collide with it.
        /// </summary>
        private Task? _opening;

        /// <summary>The camera currently selected, or null when the picker is on "(None)".</summary>
        public string? DeviceName { get; private set; }

        public CameraPreviewState State { get; private set; } = CameraPreviewState.Stopped;

        /// <summary>The line the editor shows under/over the pane. Never null.</summary>
        public string StatusText { get; private set; } = NoCameraStatus;

        /// <summary>True while this controller holds (or is opening) a camera.</summary>
        public bool HoldsCamera
        {
            get { lock (_gate) { return _session != null || State == CameraPreviewState.Starting; } }
        }

        /// <summary>Raised on state changes, from a background thread. The editor marshals.</summary>
        public event Action<CameraPreviewState, string>? StateChanged;

        /// <summary>One BGR24 frame, on a background thread. The editor marshals.</summary>
        public event Action<byte[]>? FrameReceived;

        /// <param name="factory">How a session is created. Defaults to the real ffmpeg preview;
        /// tests substitute a session that needs no camera.</param>
        public CameraPreviewController(CameraPreviewSessionFactory? factory = null)
        {
            _factory = factory ?? FfmpegCameraPreview.Start;
            _releaseForRecording = ReleaseForRecording;
            CameraDeviceArbiter.Register(_releaseForRecording);
            Log.Info("[CameraPreviewController] created and registered with the camera arbiter");
        }

        /// <summary>
        /// Show <paramref name="deviceName"/>, or nothing when it is null/blank. Returns immediately:
        /// the camera is opened on a background thread and the status line says so until the first
        /// frame arrives (issue #29, AC2).
        ///
        /// Re-selecting the camera already showing is a no-op - it does NOT drop and re-open the
        /// device, which would make a stray SelectionChanged flicker the preview.
        /// </summary>
        public void Select(string? deviceName)
        {
            string? wanted = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName!.Trim();
            Log.Info($"[CameraPreviewController] Select: camera=\"{wanted ?? "(none)"}\" (was \"{DeviceName ?? "(none)"}\", state={State})");

            if (wanted != null
                && string.Equals(wanted, DeviceName, StringComparison.OrdinalIgnoreCase)
                && State is CameraPreviewState.Starting or CameraPreviewState.Running)
            {
                return;
            }

            StopSession("the camera selection changed");

            if (wanted == null)
            {
                DeviceName = null;
                Announce(CameraPreviewState.Stopped, NoCameraStatus);
                return;
            }

            lock (_gate)
            {
                var token = new object();
                _generation = token;
                DeviceName = wanted;
                // Opening a camera launches a process; the dialog must not wait for it (standard 1).
                // Queued INSIDE the lock so _opening is never observed as "nothing is opening" while
                // an open is already on its way to holding the device.
                _opening = Task.Run(() => OpenSession(token, wanted));
            }
            Announce(CameraPreviewState.Starting, StartingStatus);
        }

        /// <summary>
        /// Release the camera and say why. Idempotent, safe from any thread, and it does not return
        /// until the device is free.
        /// </summary>
        public void Stop(string status)
        {
            StopSession(status);
            Announce(CameraPreviewState.Stopped, status);
        }

        /// <summary>
        /// Registered with <see cref="CameraDeviceArbiter"/>: a recording is about to open
        /// <paramref name="recordingDevice"/>, so let go of whatever we hold (issue #29, AC7).
        /// Returns true when something was actually released.
        /// </summary>
        private bool ReleaseForRecording(string recordingDevice)
        {
            if (!HoldsCamera) return false;

            Log.Info($"[CameraPreviewController] ReleaseForRecording: a recording is opening \"{recordingDevice}\" - " +
                     $"dropping the preview of \"{DeviceName ?? "(none)"}\"");
            Stop("Preview stopped - the camera is in use by a recording.");
            return true;
        }

        private void OpenSession(object token, string deviceName)
        {
            // Task body: its own stack, so it catches (standard 4). A camera that will not open is a
            // message on the pane naming the device, not a crash and not a silent blank pane.
            try
            {
                var session = _factory(deviceName, frame => OnFrame(token, frame), message => OnFailed(token, message));
                bool stale;
                lock (_gate)
                {
                    stale = !ReferenceEquals(_generation, token);
                    if (!stale) _session = session;
                }
                if (stale)
                {
                    Log.Info($"[CameraPreviewController] OpenSession: \"{deviceName}\" was superseded while opening - releasing it");
                    session.Stop();
                    session.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CameraPreviewController] OpenSession FAILED for \"{deviceName}\"", ex);
                OnFailed(token, $"The camera \"{deviceName}\" could not be previewed: {ex.Message}");
            }
        }

        private void OnFrame(object token, byte[] frame)
        {
            lock (_gate) { if (!ReferenceEquals(_generation, token)) return; }

            if (State != CameraPreviewState.Running)
                Announce(CameraPreviewState.Running, "");

            FrameReceived?.Invoke(frame);
        }

        private void OnFailed(object token, string message)
        {
            ICameraPreviewSession? session;
            lock (_gate)
            {
                if (!ReferenceEquals(_generation, token)) return;
                session = _session;
                _session = null;
            }

            Log.Warn($"[CameraPreviewController] preview failed: {message}");
            if (session != null) { session.Stop(); session.Dispose(); }
            Announce(CameraPreviewState.Failed, message);
        }

        /// <summary>Release whatever is held and retire the current generation. Announces nothing -
        /// the caller says what the new state is.</summary>
        private void StopSession(string reason)
        {
            Task? opening;
            lock (_gate)
            {
                _generation = new object();   // anything still in flight is now stale
                opening = _opening;
                _opening = null;
            }

            // An open already on its way to the device must finish before this returns, or the camera
            // would be handed to a recording that is about to be fought for it. The open sees the
            // retired generation and releases what it just created.
            if (opening != null && !opening.IsCompleted && !opening.Wait(OpenWaitMs))
                Log.Error($"[CameraPreviewController] StopSession: an in-flight camera open did not finish " +
                          $"within {OpenWaitMs}ms - the camera may still be held ({reason})");

            ICameraPreviewSession? session;
            lock (_gate)
            {
                session = _session;
                _session = null;
            }
            if (session == null) return;

            Log.Info($"[CameraPreviewController] stopping the preview of \"{session.DeviceName}\": {reason}");
            session.Stop();
            session.Dispose();
        }

        private void Announce(CameraPreviewState state, string status)
        {
            State = state;
            StatusText = status;
            StateChanged?.Invoke(state, status);
        }

        /// <summary>
        /// The dialog is gone (by Save, Save as, Cancel, the window close button or Esc - they all
        /// end at Window.Closed). Release the camera and stop being asked to.
        /// </summary>
        public void Dispose()
        {
            CameraDeviceArbiter.Unregister(_releaseForRecording);
            StopSession("the preset editor closed");
            DeviceName = null;
            State = CameraPreviewState.Stopped;
            StatusText = NoCameraStatus;
            Log.Info("[CameraPreviewController] disposed - the camera is released");
        }
    }
}
