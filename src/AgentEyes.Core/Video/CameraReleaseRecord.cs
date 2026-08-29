using System;

namespace AgentEyes.Video
{
    /// <summary>What one attempt to hand a camera back actually established.</summary>
    internal enum CameraReleaseOutcome
    {
        /// <summary>It was ESTABLISHED that nothing was being held: no open was in flight and no
        /// session existed. Nothing was released because there was nothing to release.</summary>
        NothingWasHeld,

        /// <summary>Something WAS held and the process behind it is CONFIRMED gone.</summary>
        Released,

        /// <summary>The camera process was asked to die and was OBSERVED still running, or the stop
        /// itself failed. The device is still held.</summary>
        StillHeld,

        /// <summary>Nothing was established either way. NOT a release - see the class remarks.</summary>
        Unknown,
    }

    /// <summary>
    /// WHAT ONE RELEASE ATTEMPT ACTUALLY DID TO A CAMERA PREVIEW, AND WHAT EACH STEP OBSERVED - and
    /// the ONE place a <see cref="CameraReleaseOutcome"/> is worked out from it.
    ///
    /// WHY THIS TYPE EXISTS (issue #35, Review Gate round 1 - all four blocking defects). Every one
    /// of them was a call site ANNOUNCING that the camera was free without having established it:
    ///
    ///  - a close that unregistered the holder before the device was let go, so the arbiter had
    ///    nothing left to ask;
    ///  - a stop whose wait for an in-flight open TIMED OUT, which then returned as though the wait
    ///    had succeeded;
    ///  - a stop that read "no session is published" as "no camera is held", when the session it
    ///    was waiting for had simply not been published yet; and
    ///  - a real ffmpeg stop that logged "released" after a kill it had watched fail.
    ///
    /// That is the same design error issue #28 spent nine rounds removing from the RECORDER, in a
    /// different file: an outcome that is A CLAIM ABOUT HISTORY was being ASSIGNED by call sites,
    /// and NO CALL SITE KNOWS THE WHOLE HISTORY OF THE ATTEMPT.
    ///
    /// So no call site says "released" any more. There is no method here that takes a
    /// <see cref="CameraReleaseOutcome"/> and no method that takes a "released" flag, so a release
    /// is not something a caller can state - it is something the observations either show or do not.
    /// Every observation is checked against what the record already knows before it is admitted, and
    /// the one observation that decides the answer is not passed in at all: <see cref="ObserveAfterStop"/>
    /// ASKS THE SESSION ITSELF whether its process is still running.
    ///
    /// MONOTONICITY. The derivation is ordered WORST FIRST. Once a stop has thrown, or a session has
    /// been seen still holding its device, nothing later in the same attempt can turn that back into
    /// a release; and an unresolved open outranks any success recorded beside it, because a camera
    /// process nobody has a handle to yet is exactly the case the third defect returned "released"
    /// for.
    ///
    /// UNKNOWN IS NOT A RELEASE, AND THAT IS THE WHOLE POINT. <see cref="DeviceConfirmedFree"/> is
    /// true only for <see cref="CameraReleaseOutcome.Released"/> and
    /// <see cref="CameraReleaseOutcome.NothingWasHeld"/> - the two answers that were established.
    /// Everything this attempt failed to establish reads as "the camera may still be held", which is
    /// the honest answer and the one that keeps the holder registered and the handle retained.
    ///
    /// WHAT THIS RECORD CANNOT SEE. It knows only what one release attempt reported to it. It cannot
    /// see a camera held by another PROCESS (the CLI, a browser) - that is not this app's to release
    /// and <see cref="FfmpegCameraRecorder.DiagnoseOpenFailure"/> is what names it - and it cannot
    /// see a device that Windows keeps busy after its holder has exited.
    /// </summary>
    internal sealed class CameraReleaseRecord
    {
        /// <summary>Why the release was asked for - for the log line only.</summary>
        private readonly string _reason;

        /// <summary>An open was still in flight when the attempt began.</summary>
        private bool _openWasInFlight;

        /// <summary>The question "was an open in flight, and did it finish?" has been settled.</summary>
        private bool _openSettled;

        /// <summary>An in-flight open did NOT finish within the wait. The device may be held by a
        /// session that has not been published yet, and no handle to it exists.</summary>
        private bool _openUnresolved;

        /// <summary>How long the wait for the in-flight open actually took.</summary>
        private int _openWaitMs;

        /// <summary>The published session was looked for (rather than assumed absent).</summary>
        private bool _lookedForASession;

        /// <summary>A published session was found and taken to be stopped.</summary>
        private bool _sessionTaken;

        private string _sessionDevice = "";
        private int? _sessionPid;

        /// <summary>The stop call itself failed. A stop that threw has released nothing.</summary>
        private bool _stopThrew;

        /// <summary>The session was asked, AFTER the stop, whether its process was still running.</summary>
        private bool _observedAfterStop;

        /// <summary>...and the answer was yes.</summary>
        private bool _stillHeldAfterStop;

        public CameraReleaseRecord(string reason) => _reason = reason ?? "";

        /// <summary>The session this attempt took, once it has been taken. Null when none was
        /// published - which is NOT the same as "no camera is held"; see the outcome.</summary>
        public ICameraPreviewSession? Session { get; private set; }

        /// <summary>THE DERIVATION. The one place a <see cref="CameraReleaseOutcome"/> comes from, and
        /// a pure function of the observations above. Ordered worst first, so nothing can be
        /// upgraded by a later, friendlier fact.</summary>
        public CameraReleaseOutcome Outcome
        {
            get
            {
                // A stop that threw did not release anything, whatever else was seen.
                if (_stopThrew) return CameraReleaseOutcome.StillHeld;

                // The process was ASKED to die and was OBSERVED still running.
                if (_stillHeldAfterStop) return CameraReleaseOutcome.StillHeld;

                // An open that was still on its way to the device when we gave up waiting. There is
                // no handle to what it may have created, so nothing here can speak for it.
                if (_openUnresolved) return CameraReleaseOutcome.Unknown;

                // The attempt did not even complete its own steps.
                if (!_openSettled || !_lookedForASession) return CameraReleaseOutcome.Unknown;

                if (_sessionTaken)
                    return _observedAfterStop ? CameraReleaseOutcome.Released : CameraReleaseOutcome.Unknown;

                // No open was in flight AND no session was published: an ESTABLISHED absence.
                return CameraReleaseOutcome.NothingWasHeld;
            }
        }

        /// <summary>True only when this attempt ESTABLISHED that the camera is free. Everything it
        /// failed to establish reads as "may still be held".</summary>
        public bool DeviceConfirmedFree =>
            Outcome is CameraReleaseOutcome.Released or CameraReleaseOutcome.NothingWasHeld;

        /// <summary>True only when a camera that WAS held is now confirmed free - what the camera
        /// arbiter counts as a holder having actually let go.</summary>
        public bool AnythingReleased => Outcome == CameraReleaseOutcome.Released;

        /// <summary>The session that survived this attempt, or null when nothing survived it. This
        /// is the handle that must not be discarded.</summary>
        public ICameraPreviewSession? SurvivingSession =>
            Outcome == CameraReleaseOutcome.StillHeld ? Session : null;

        // ---- observations ---------------------------------------------------

        /// <summary>There was no open in flight when the attempt began.</summary>
        public void NoOpenWasInFlight()
        {
            Require(!_openSettled, "the in-flight open was reported twice");
            _openSettled = true;
        }

        /// <summary>An open was in flight and was waited for. <paramref name="finished"/> is the
        /// wait's own answer - false means the open is STILL RUNNING, never "probably fine".</summary>
        public void InFlightOpenWaited(bool finished, int waitedMs)
        {
            Require(!_openSettled, "the in-flight open was reported twice");
            _openSettled = true;
            _openWasInFlight = true;
            _openWaitMs = waitedMs;
            _openUnresolved = !finished;
        }

        /// <summary>The published session was looked for. Recorded because "there was no session"
        /// is only worth something when somebody actually looked - the third defect read an absence
        /// that had never been established.</summary>
        public void LookedForASession(ICameraPreviewSession? session)
        {
            Require(_openSettled, "a session was looked for before the in-flight open was settled");
            Require(!_lookedForASession, "the session was looked for twice");
            _lookedForASession = true;
            if (session == null) return;

            _sessionTaken = true;
            Session = session;
            _sessionDevice = session.DeviceName;
            _sessionPid = session.ProcessId;
        }

        /// <summary>The stop call threw. It has released nothing, and no later observation may say
        /// otherwise.</summary>
        public void StopThrew(Exception ex)
        {
            Require(_sessionTaken, "a stop failure was reported without a session having been taken");
            _stopThrew = true;
            Log.Error($"[CameraReleaseRecord] stopping the preview of \"{_sessionDevice}\" FAILED ({_reason})", ex);
        }

        /// <summary>
        /// ASK THE SESSION whether its camera process is still running, now that it has been stopped.
        ///
        /// The answer is not a parameter. A caller cannot report "it let go" here, because that is
        /// precisely the claim four separate call sites made without having earned it; all a caller
        /// can do is present the session, and the session asks the operating system.
        /// </summary>
        public void ObserveAfterStop(ICameraPreviewSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            Require(_sessionTaken, "a post-stop observation was reported without a session having been taken");
            Require(ReferenceEquals(session, Session), "a post-stop observation named a different session");
            Require(!_observedAfterStop, "a post-stop observation was reported twice");

            _observedAfterStop = true;
            _stillHeldAfterStop = session.IsAbandoned;
            _sessionPid = session.ProcessId;
        }

        /// <summary>The whole attempt on one ASCII line, for the log.</summary>
        public string Describe() =>
            $"outcome={Outcome} reason=\"{_reason}\" openInFlight={_openWasInFlight} "
            + $"openWaitMs={_openWaitMs} openUnresolved={_openUnresolved} lookedForSession={_lookedForASession} "
            + $"sessionTaken={_sessionTaken} sessionDevice=\"{_sessionDevice}\" "
            + $"sessionPid={_sessionPid?.ToString() ?? "unknown"} stopThrew={_stopThrew} "
            + $"askedAfterStop={_observedAfterStop} stillHeldAfterStop={_stillHeldAfterStop}";

        /// <summary>
        /// A sentence for the user when the camera could NOT be established free. It names the device
        /// and the PID, because "the preview could not be stopped" is not something a person can act
        /// on and "PID 24512 still holds Logitech BRIO" is.
        /// </summary>
        public string FailureText()
        {
            string pid = _sessionPid?.ToString() ?? "unknown";
            return Outcome switch
            {
                CameraReleaseOutcome.StillHeld =>
                    $"The camera preview of \"{_sessionDevice}\" could NOT be stopped - its ffmpeg (PID {pid}) is "
                    + "still running and still holds the camera. A recording of that camera will fail until it is gone.",
                CameraReleaseOutcome.Unknown =>
                    "The camera preview did not confirm that it let the camera go - an open that was already on its "
                    + $"way to the device did not finish within {_openWaitMs}ms. The camera may still be held.",
                _ => "",
            };
        }

        /// <summary>
        /// FAIL EXPLICITLY, NEVER SILENTLY. A violated precondition is a programming error at a call
        /// site - the exact class of mistake the Review Gate found four times - so it throws rather
        /// than admitting an observation the attempt does not support.
        /// </summary>
        private void Require(bool condition, string what)
        {
            if (condition) return;
            string message = $"[CameraReleaseRecord] {what}. Attempt: {Describe()}";
            Log.Error(message);
            throw new InvalidOperationException(message);
        }
    }
}
