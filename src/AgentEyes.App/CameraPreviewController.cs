using System;
using System.Threading;
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
    ///  3. the preset leaves Video mode or the tab  -> the editor calls <see cref="Stop"/>
    ///  4. the dialog closes, by ANY route          -> <see cref="Dispose"/> from Window.Closed
    ///  5. a recording opens the camera             -> <see cref="CameraDeviceArbiter"/> calls in
    ///
    /// The session is created on a background thread and every callback arrives on one, so nothing
    /// here touches the UI: a recording start blocks on path 5 until the device is free, and it must
    /// never be waiting on a busy UI thread to get it.
    ///
    /// THREE RULES THIS CLASS NOW ENFORCES, and they are one rule (issue #35, Review Gate round 1,
    /// defects 1, 2 and 3 - and the audit that followed them). The gate found four call sites
    /// ANNOUNCING that the camera was free without having established it, which is the same design
    /// error issue #28 spent nine rounds removing from the recorder:
    ///
    ///  1. DISPOSAL IS OBSERVABLE AND FINAL. A disposed controller can never start a session, so an
    ///     enumeration that finishes after the dialog has closed cannot open a camera into a window
    ///     that is gone. The flag is set INSIDE the same lock <see cref="Select"/> publishes its
    ///     open under, so there is no instant between "not disposed" and "opening" for a close to
    ///     fall into.
    ///  2. THE HOLDER STAYS REGISTERED UNTIL THE DEVICE IS ACTUALLY FREE. Unregistering first left a
    ///     window - up to three seconds of a real ffmpeg stop - in which the camera was held and the
    ///     arbiter had no callback able to release it. So <see cref="Dispose"/> stops FIRST and
    ///     unregisters only on a release it established; a holder that still holds stays askable.
    ///  3. NO PATH REPORTS A RELEASE IT HAS NOT ESTABLISHED. Every release goes through
    ///     <see cref="StopSession"/>, which returns a <see cref="CameraReleaseRecord"/>: an in-flight
    ///     open that timed out is not a release, a stop that threw is not a release, and an
    ///     unpublished session is not proof that nothing is held. What survives is never discarded -
    ///     it is handed to <c>StrandedCameraOwner</c>, the same owner issue #28 built for a recorder
    ///     that outlived its owner.
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
        /// Serializes release attempts. A real stop takes time, and a closing dialog and a starting
        /// recording can both be inside one - two of them interleaving would let the second read the
        /// first's half-finished state and call it an absence, which is defect 3 exactly.
        /// </summary>
        private readonly object _stopGate = new object();

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

        /// <summary>
        /// An open this controller WAITED FOR AND DID NOT SEE FINISH. It may be holding the camera
        /// through a session that has never been published, so until it completes this controller
        /// must not answer "nothing is held" - see <see cref="HoldsCamera"/>. Cleared by the open
        /// itself when it finally lands.
        /// </summary>
        private int _unresolvedOpens;

        /// <summary>1 once <see cref="Dispose"/> has run. Written under <see cref="_gate"/>, so an
        /// open cannot be queued across it.</summary>
        private int _disposed;

        /// <summary>The camera currently selected, or null when the picker is on "(None)".</summary>
        public string? DeviceName { get; private set; }

        public CameraPreviewState State { get; private set; } = CameraPreviewState.Stopped;

        /// <summary>The line the editor shows under/over the pane. Never null.</summary>
        public string StatusText { get; private set; } = NoCameraStatus;

        /// <summary>
        /// True once the editor has closed. Observable ON PURPOSE: "this controller is finished" is
        /// a fact a caller may need to check, and a fact this class must be able to be tested on.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// The size of the frames the CAMERA is producing, as ffmpeg reported them, or null when no
        /// session is running or ffmpeg has not said yet (issue #36).
        ///
        /// The preset editor needs it because the pane shows a PADDED 320x240 buffer: without the
        /// camera's real shape it cannot know where the black bars are, and the circle overlay would
        /// be drawn over the wrong part of the picture. Null is "not observed" and the editor says
        /// so - it does not assume the picture fills the pane.
        /// </summary>
        public CameraFrameSize? SourceSize
        {
            get { lock (_gate) { return _session?.SourceSize; } }
        }

        /// <summary>
        /// True while this controller holds - or may be holding - a camera.
        ///
        /// AN UNRESOLVED OPEN COUNTS (issue #35, gate round 1, defect 3). An open that was waited for
        /// and did not finish may already have the device through a session that has not been
        /// published; reading the empty <c>_session</c> field as "nothing is held" is exactly the
        /// absence the gate rejected.
        /// </summary>
        public bool HoldsCamera
        {
            get
            {
                lock (_gate)
                {
                    return _session != null
                           || Volatile.Read(ref _unresolvedOpens) > 0
                           || State == CameraPreviewState.Starting;
                }
            }
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
        ///
        /// IT REFUSES TWO THINGS, LOUDLY (issue #35, gate round 1, defect 1). A DISPOSED controller
        /// never starts a session: the editor's camera enumeration can finish long after the dialog
        /// was closed, and the old code let that continuation open an exclusive camera into a window
        /// that no longer existed, with the holder already off the arbiter - a webcam held by nothing
        /// visible and nothing able to release it. And a select whose PREVIOUS camera could not be
        /// released does not open the next one: opening a second device while the first is still held
        /// is how one stuck preview becomes two.
        /// </summary>
        public void Select(string? deviceName)
        {
            string? wanted = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName!.Trim();
            Log.Info($"[CameraPreviewController] Select: camera=\"{wanted ?? "(none)"}\" (was \"{DeviceName ?? "(none)"}\", state={State})");

            if (IsDisposed)
            {
                Log.Error($"[CameraPreviewController] Select REFUSED: this controller is disposed (the preset editor " +
                          $"has closed), so it will not open \"{wanted ?? "(none)"}\". A camera opened now would be " +
                          "held by a window nobody can see and released by nothing.");
                return;
            }

            if (wanted != null
                && string.Equals(wanted, DeviceName, StringComparison.OrdinalIgnoreCase)
                && State is CameraPreviewState.Starting or CameraPreviewState.Running)
            {
                return;
            }

            var release = StopSession("the camera selection changed");
            if (!release.DeviceConfirmedFree)
            {
                Announce(CameraPreviewState.Failed, release.FailureText());
                return;
            }

            if (wanted == null)
            {
                DeviceName = null;
                Announce(CameraPreviewState.Stopped, NoCameraStatus);
                return;
            }

            lock (_gate)
            {
                // The disposal flag is written under this same lock, so a Dispose that has begun
                // cannot be overtaken here: either it has not started (and Dispose will wait on the
                // task queued below) or it has, and nothing is queued at all.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    Log.Error("[CameraPreviewController] Select REFUSED: the controller was disposed while the " +
                              "selection was being applied - no camera is opened.");
                    return;
                }

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
        /// until it has finished trying.
        ///
        /// It does not PROMISE the camera is free - nothing here does any more. When the release
        /// could not be established the pane says so, naming the device and the PID, rather than
        /// showing the reassuring text the caller asked for over a camera that is still held.
        /// </summary>
        public void Stop(string status)
        {
            var release = StopSession(status);
            if (release.DeviceConfirmedFree) Announce(CameraPreviewState.Stopped, status);
            else Announce(CameraPreviewState.Failed, release.FailureText());
        }

        /// <summary>
        /// Registered with <see cref="CameraDeviceArbiter"/>: a recording is about to open
        /// <paramref name="recordingDevice"/>, so let go of whatever we hold (issue #29, AC7).
        ///
        /// Returns true only when a camera that WAS held is now CONFIRMED free. A timeout, a failed
        /// kill, or a session that could not be found is not a release and is not counted as one -
        /// the arbiter's number is an observation, not an intention (issue #35, gate round 1,
        /// defect 3).
        /// </summary>
        private bool ReleaseForRecording(string recordingDevice)
        {
            if (!HoldsCamera) return false;

            Log.Info($"[CameraPreviewController] ReleaseForRecording: a recording is opening \"{recordingDevice}\" - " +
                     $"dropping the preview of \"{DeviceName ?? "(none)"}\"");
            var release = StopSession($"a recording is opening \"{recordingDevice}\"");

            if (release.DeviceConfirmedFree)
            {
                Announce(CameraPreviewState.Stopped, "Preview stopped - the camera is in use by a recording.");

                // A holder kept registered by a disposal that could not free the device (see
                // Dispose) has now freed it, so it stops being asked. This is the one place that
                // late unregistration can happen, because it is the one place that late release is
                // established.
                if (IsDisposed) UnregisterHolder("the camera was released on a later recording start");
            }
            else
            {
                Announce(CameraPreviewState.Failed, release.FailureText());
                Log.Error($"[CameraPreviewController] ReleaseForRecording: the preview did NOT confirm it let go of " +
                          $"the camera before the recording of \"{recordingDevice}\" opened it. {release.Describe()}");
            }

            return release.AnythingReleased;
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
                    stale = !ReferenceEquals(_generation, token) || Volatile.Read(ref _disposed) != 0;
                    if (!stale) _session = session;
                }
                if (stale)
                {
                    // THE SAME DISEASE, ONE MORE CALL SITE. This path used to Stop() and then
                    // Dispose() unconditionally - so an open that landed after a close and could not
                    // be killed had its handle thrown away exactly as defect 4 describes. It goes
                    // through the one door now.
                    Log.Info($"[CameraPreviewController] OpenSession: \"{deviceName}\" was superseded while opening - releasing it");
                    ReleaseOrRetain(session, "the open was superseded while it was still in flight");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CameraPreviewController] OpenSession FAILED for \"{deviceName}\"", ex);
                OnFailed(token, $"The camera \"{deviceName}\" could not be previewed: {ex.Message}");
            }
            finally
            {
                // Whatever happened, this open is no longer on its way to the device: either it
                // published a session, or it released/retained the one it made, or it never made one.
                // Only now may a stop that timed out on this task stop saying "something may be held".
                Interlocked.Exchange(ref _unresolvedOpens, 0);
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

            // THE SAME DISEASE, ONE MORE CALL SITE (found by the audit, not by the gate). A camera
            // that reports a failure has usually already exited - but "usually" is a claim, and this
            // path used to Stop() and Dispose() the session on it. It goes through the one door.
            if (session != null) ReleaseOrRetain(session, "the preview reported a failure");
            Announce(CameraPreviewState.Failed, message);
        }

        /// <summary>
        /// THE ONE PLACE A CAMERA IS HANDED BACK, and the one place that decides what actually
        /// happened. Announces nothing - the caller says what the new state is - and it never
        /// claims: what it returns is a record of what it attempted and observed.
        ///
        /// Release attempts are serialized (<see cref="_stopGate"/>), so a recording start arriving
        /// during a closing dialog waits for that close to finish rather than reading its
        /// half-finished state as an absence.
        /// </summary>
        private CameraReleaseRecord StopSession(string reason)
        {
            var record = new CameraReleaseRecord(reason);

            lock (_stopGate)
            {
                Task? opening;
                lock (_gate)
                {
                    _generation = new object();   // anything still in flight is now stale
                    opening = _opening;
                }

                // An open already on its way to the device must finish before this returns, or the
                // camera would be handed to a recording that is about to be fought for it. The open
                // sees the retired generation and releases what it just created.
                if (opening == null || opening.IsCompleted)
                {
                    record.NoOpenWasInFlight();
                    ForgetOpen(opening);
                }
                else
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    bool finished = opening.Wait(OpenWaitMs);
                    record.InFlightOpenWaited(finished, (int)clock.ElapsedMilliseconds);
                    if (finished)
                    {
                        ForgetOpen(opening);
                    }
                    else
                    {
                        // A TIMEOUT IS NOT A RELEASE (issue #35, gate round 1, defect 3). The open is
                        // still running and may already hold the device through a session nothing here
                        // can see, so this controller goes on answering "something may be held" - and
                        // THE TASK IS KEPT, so the next release attempt waits on the same open rather
                        // than finding an empty field and reading it as an absence, which is the same
                        // defect one step later.
                        Interlocked.Exchange(ref _unresolvedOpens, 1);
                        Log.Error($"[CameraPreviewController] StopSession: an in-flight camera open did not finish " +
                                  $"within {OpenWaitMs}ms - the camera may STILL BE HELD by a session that has not " +
                                  $"been published yet. This is NOT a release ({reason})");
                    }
                }

                ICameraPreviewSession? session;
                lock (_gate) { session = _session; }
                record.LookedForASession(session);
                if (session == null)
                {
                    Log.Info($"[CameraPreviewController] StopSession: {record.Describe()}");
                    return record;
                }

                Log.Info($"[CameraPreviewController] stopping the preview of \"{session.DeviceName}\": {reason}");

                // Entry point for the session boundary: a Stop that throws has released nothing, and
                // the record says so rather than the exception being swallowed into a success.
                try { session.Stop(); }
                catch (Exception ex) { record.StopThrew(ex); }

                record.ObserveAfterStop(session);

                if (record.DeviceConfirmedFree)
                {
                    // Only now is it safe to forget it - and only now is it safe to release the
                    // handle, which is what Dispose does once the process is confirmed gone.
                    lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
                    session.Dispose();
                    Log.Info($"[CameraPreviewController] StopSession: {record.Describe()}");
                    return record;
                }

                // IT SURVIVED. The session STAYS in _session - this controller still holds a camera
                // and must go on saying so - and it is ALSO handed to the owner that keeps a camera
                // process reachable after the thing that started it is gone.
                Retain(record.SurvivingSession ?? session, record);
                return record;
            }
        }

        /// <summary>
        /// Let go of one session, or keep it because it would not let go of the camera. The ONE door
        /// every non-<see cref="StopSession"/> path uses, so "a process that survived stays
        /// reachable" cannot be true on one path and false on another.
        /// </summary>
        private void ReleaseOrRetain(ICameraPreviewSession session, string reason)
        {
            var record = new CameraReleaseRecord(reason);
            record.NoOpenWasInFlight();
            record.LookedForASession(session);

            try { session.Stop(); }
            catch (Exception ex) { record.StopThrew(ex); }

            record.ObserveAfterStop(session);

            if (record.DeviceConfirmedFree)
            {
                session.Dispose();
                Log.Info($"[CameraPreviewController] ReleaseOrRetain: {record.Describe()}");
                return;
            }

            Retain(session, record);
        }

        /// <summary>Hand a surviving camera process to the owner that keeps it reachable, and say so
        /// loudly. The handle is never dropped and never disposed while the process is alive.</summary>
        private void Retain(ICameraPreviewSession session, CameraReleaseRecord record)
        {
            Log.Error($"[CameraPreviewController] the camera was NOT released: {record.Describe()}");
            CameraDeviceArbiter.StrandedPreviews.RetainIfStranded(session, dir: null);
        }

        /// <summary>Drop the reference to an open that is no longer on its way to the device - and
        /// only that one, so a newer open queued meanwhile is never forgotten with it.</summary>
        private void ForgetOpen(Task? opening)
        {
            lock (_gate) { if (ReferenceEquals(_opening, opening)) _opening = null; }
        }

        private void Announce(CameraPreviewState state, string status)
        {
            State = state;
            StatusText = status;
            StateChanged?.Invoke(state, status);
        }

        private void UnregisterHolder(string why)
        {
            CameraDeviceArbiter.Unregister(_releaseForRecording);
            Log.Info($"[CameraPreviewController] unregistered from the camera arbiter: {why}");
        }

        /// <summary>
        /// The dialog is gone (by Save, Save as, Cancel, the window close button or Esc - they all
        /// end at Window.Closed). Release the camera and stop being asked to.
        ///
        /// THE ORDER IS THE FIX (issue #35, gate round 1, defect 2). This used to unregister from the
        /// arbiter FIRST and stop the preview afterwards, which left a window - up to three seconds
        /// of a real ffmpeg stop - in which the camera was held and a recording start snapshotting
        /// the holders found NONE. A `POST /record/start` arriving in that window opened the same
        /// exclusive device and failed as camera-in-use. So the stop comes first, and the holder is
        /// unregistered only on a release this method ESTABLISHED. A holder that still holds stays
        /// registered, stays askable, and is retried by the next recording start
        /// (<see cref="ReleaseForRecording"/>) - which is also where it finally unregisters.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                // Written under the same lock Select publishes an open under, so no open can be
                // queued across this point. Disposal is observable and final from here on.
                Volatile.Write(ref _disposed, 1);
            }

            var release = StopSession("the preset editor closed");

            if (!release.DeviceConfirmedFree)
            {
                Log.Error("[CameraPreviewController] disposed, but the camera was NOT released - this holder STAYS " +
                          "REGISTERED with the camera arbiter so the next recording start asks it again, and the " +
                          $"session is retained rather than discarded. {release.Describe()}");
                State = CameraPreviewState.Failed;
                StatusText = release.FailureText();
                return;
            }

            UnregisterHolder("the preset editor closed and the camera is released");
            DeviceName = null;
            State = CameraPreviewState.Stopped;
            StatusText = NoCameraStatus;
            Log.Info("[CameraPreviewController] disposed - the camera is released");
        }
    }
}
