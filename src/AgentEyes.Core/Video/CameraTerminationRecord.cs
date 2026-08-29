using System;

namespace AgentEyes.Video
{
    /// <summary>
    /// WHAT AGENTEYES ACTUALLY DID TO THE CAMERA PROCESS, AND WHAT EACH ATTEMPT OBSERVED - and the
    /// ONE place a <see cref="CameraStopKind"/> is worked out from it.
    ///
    /// WHY THIS TYPE EXISTS (gate rounds 3, 4, 5 and 6 - all four found the same shape of defect).
    /// Every one of those rounds rejected a different CALL SITE that had assigned
    /// <see cref="CameraStopKind.Abandoned"/> without having earned it, and each round taught that
    /// one call site to guess correctly. The gate then found the next one. That is not a coding-error
    /// pattern, it is a design error:
    ///
    ///   "abandoned" IS A CLAIM ABOUT HISTORY - the process survived a quit AND a kill AND a retry -
    ///   but it was being ASSIGNED at individual call sites, and NO CALL SITE KNOWS THE FULL HISTORY
    ///   OF ATTEMPTS MADE AGAINST THE PROCESS. There is always another call site.
    ///
    /// So no call site assigns a stop kind any more. <see cref="FfmpegCameraRecorder"/> has no
    /// settable stop-kind field at all. It reports OBSERVATIONS to this record - "I attempted a
    /// quit", "the quit was delivered", "I issued a kill", "the kill was refused", "the process was
    /// already gone and I had not touched it", "it exited with code N after I had touched it" - and
    /// <see cref="StopKind"/> DERIVES the answer from the whole history, as a pure function of it.
    ///
    /// WHY A NEW CALL SITE CANNOT RECORD AN UNEARNED STOP KIND. There is no method here that takes a
    /// <see cref="CameraStopKind"/>, so "abandoned" is not something a caller can say - it is
    /// something the history either shows or does not. Every observation is checked against what the
    /// record already knows before it is admitted:
    ///
    ///  - <see cref="QuitDelivered"/> is refused unless a quit was attempted in the current round, so
    ///    a delivery cannot be claimed for a quit that was never sent.
    ///  - <see cref="KillConfirmedProcessGone"/> and <see cref="KillRefused"/> are refused unless a
    ///    kill was issued in the current round, so a kill outcome cannot be claimed for a kill that
    ///    was never issued, and a round cannot report two outcomes.
    ///  - <see cref="ExitObservedAfterTermination"/> is refused unless this recorder had already
    ///    interfered with the process, so an exit nobody caused cannot be read as an answer.
    ///  - <see cref="ProcessGoneWithoutAnyAttempt"/> records NOTHING once any attempt has been made.
    ///    That is its contract, not a swallow: the stop retry re-reaches that observation point on
    ///    every later pass, and by then "the process has exited" means something completely
    ///    different, so it must not be able to write "exited-early" over a fight already in progress.
    ///
    /// The worst a wrong or missing observation can now produce is an ABSENT stop kind and a
    /// completeness of "unknown" - which is the honest answer, and the one the amended spec asks for
    /// in every case this code did not anticipate. It can no longer produce a CLAIM.
    ///
    /// MONOTONICITY. <see cref="AbandonedEarned"/> is a function of a counter that only ever rises,
    /// and it is tested FIRST in <see cref="StopKind"/>. So an earned "abandoned" is never replaced
    /// by a later outcome (gate round 6, defect 2): a stranded ffmpeg that finally accepts a
    /// recovery quit or a recovery kill DID still survive the quit, the kill and the retry, and the
    /// durable historical record says so for ever. What the later success correctly changes is the
    /// LIVE status - <see cref="FfmpegCameraRecorder.IsAbandoned"/>, which asks the process itself
    /// every time - not this record of what happened.
    ///
    /// WHAT THIS RECORD CANNOT SEE. It knows only what <see cref="FfmpegCameraRecorder"/> tells it.
    /// It cannot see a termination issued by anything else - another process, Task Manager, the OS -
    /// and it does not try to: a process that ends for a reason nothing here watched produces no
    /// stop kind at all, which is exactly what an unwatched ending is worth.
    /// </summary>
    internal sealed class CameraTerminationRecord
    {
        /// <summary>
        /// How many termination ROUNDS have to be REFUSED before the history shows "abandoned".
        ///
        /// The definition of <see cref="CameraStopKind.Abandoned"/> is "it survived the quit, the
        /// kill AND the retry". A single round IS the quit-then-kill sequence, so "and the retry"
        /// is exactly a SECOND round that was also refused. Two.
        ///
        /// This number is the whole of the definition, and it lives here rather than at any call
        /// site, which is what stops the count being re-guessed from a place that cannot see it.
        /// <see cref="FfmpegCameraRecorder.Dispose"/> reads it too, so that a Dispose which is
        /// ITSELF the first round performs the retry it promises rather than recording a
        /// three-clause observation off one (gate round 6, defect 1).
        /// </summary>
        public const int RefusedRoundsForAbandoned = 2;

        /// <summary>
        /// The ONLY exit codes an ffmpeg that ANSWERED "q" is observed to return - the whole
        /// accepted set, listed rather than described by a range (gate round 5, defect 1).
        ///
        ///  - <c>0</c>: ffmpeg's ordinary success exit. Every clause of the encode ran, including
        ///    the muxer's trailer.
        ///  - <c>255</c>: what ffmpeg returns when it stops because the interactive "q" was pressed
        ///    rather than because the input ended. It still ran its own exit path and still wrote
        ///    the MP4 trailer, and it is the code AC17's positive control actually observes, so it
        ///    is accepted on exactly the same footing as 0.
        ///
        /// EVERYTHING ELSE IS NOT A CLEAN QUIT, and that is the whole point of enumerating rather
        /// than testing <c>exitCode &gt;= 0</c>: that test made every value from 0 to
        /// <see cref="int.MaxValue"/> proof that the file was finalized, so a "q" that was delivered
        /// and then met a muxer, disk or encoder failure on the way out - ffmpeg's own exit 1 -
        /// wrote <c>clean-quit</c> / <c>yes</c> over a take ffmpeg had explicitly reported as
        /// failed. An error code the process chose for itself is the strongest evidence available
        /// that the take is NOT good; reading it as a clean quit is the fail-open defect this issue
        /// exists to remove.
        ///
        /// Widening this set is a deliberate act: it needs an ffmpeg build OBSERVED to answer "q"
        /// with that code, and it belongs here, next to the two that were. An unanticipated code is
        /// not a failure to write - the stop kind is simply ABSENT and the take is <c>unknown</c>,
        /// which is what the amended contract requires of every case this class did not anticipate.
        ///
        /// It lives HERE, beside the derivation that reads it, rather than on the recorder: the
        /// exit code is an OBSERVATION the recorder reports, and what that observation is worth is
        /// this record's judgement to make. It is still enumerated, and it is still exactly these
        /// two members.
        /// </summary>
        private static readonly int[] QuitExitCodes = { 0, 255 };

        /// <summary>The camera this history is about - for the log lines only.</summary>
        private readonly string _deviceName;

        /// <summary>Termination rounds OPENED. A round is one pass of the recorder's termination
        /// sequence; a pass that never touched the process is still opened, and simply carries no
        /// attempts.</summary>
        private int _roundsOpened;

        /// <summary>Rounds in which at least one quit or kill was actually issued.</summary>
        private int _roundsWithAnAttempt;

        /// <summary>Rounds that ENDED WITH THE PROCESS STILL ALIVE after this recorder had issued
        /// its kill and waited for it. The counter <see cref="AbandonedEarned"/> is derived from,
        /// and it only ever rises.</summary>
        private int _refusedRounds;

        private int _quitsAttempted;
        private int _quitsDelivered;
        private int _killsAttempted;

        // ---- per-round bookkeeping, reset by BeginRound ------------------------------------
        private bool _roundQuitAttempted;
        private int _roundKills;
        private bool _roundHadAnAttempt;
        private bool _roundSettled;

        /// <summary>The process was CONFIRMED gone at a moment when this recorder had never asked
        /// it to stop. Latched, because it is a fact about a moment that has passed.</summary>
        private bool _goneUntouched;

        /// <summary>A kill this recorder issued was CONFIRMED to have ended the process.</summary>
        private bool _killConfirmedGone;

        /// <summary>The process was seen to exit after this recorder had interfered with it, and
        /// this is the code it exited with.</summary>
        private bool _exitObserved;
        private int _exitCode;

        public CameraTerminationRecord(string deviceName) => _deviceName = deviceName;

        /// <summary>True once this recorder has issued a quit or a kill at the process - i.e. once a
        /// later "it has exited" can no longer be read as "it ended before anybody asked it to".</summary>
        public bool AnyAttemptMade => _quitsAttempted + _killsAttempted > 0;

        /// <summary>True once at least one kill this recorder issued was refused. The live-status
        /// half of AC16: it is what makes a stranded process worth holding a handle for, from the
        /// FIRST refusal onwards - long before "abandoned" has been earned.</summary>
        public bool AnyKillRefused => _refusedRounds > 0;

        /// <summary>How many termination rounds ended with the process still alive.</summary>
        public int RefusedRounds => _refusedRounds;

        /// <summary>
        /// THE THREE-CLAUSE OBSERVATION, and the only way it can ever be true: the recorder's
        /// termination sequence was refused in <see cref="RefusedRoundsForAbandoned"/> distinct
        /// rounds - the attempt, and the retry.
        ///
        /// It rises and never falls, because <see cref="_refusedRounds"/> does.
        /// </summary>
        public bool AbandonedEarned => _refusedRounds >= RefusedRoundsForAbandoned;

        /// <summary>Open a termination round. Every pass of the recorder's termination sequence
        /// opens exactly one, before it touches the process.</summary>
        public void BeginRound()
        {
            _roundsOpened++;
            _roundQuitAttempted = false;
            _roundKills = 0;
            _roundHadAnAttempt = false;
            _roundSettled = false;
            Log.Info($"[CameraTerminationRecord] BeginRound: camera=\"{_deviceName}\" round={_roundsOpened} "
                     + $"history={Describe()}");
        }

        /// <summary>A "q" is about to be written to ffmpeg's stdin. Recorded BEFORE the write, so a
        /// write that throws still counts as this recorder having interfered with the process.</summary>
        public void QuitAttempted()
        {
            Require(_roundsOpened > 0, "a quit was attempted outside any termination round");
            _quitsAttempted++;
            _roundQuitAttempted = true;
            CountRoundAsAttempted();
        }

        /// <summary>The "q" was written without error, i.e. it actually REACHED the process. A quit
        /// that never arrived cannot have been answered by it (gate round 4, defect 1).</summary>
        public void QuitDelivered()
        {
            Require(_roundQuitAttempted, "a quit was reported delivered without having been attempted");
            _quitsDelivered++;
        }

        /// <summary>A kill is about to be issued. Recorded BEFORE the call for the same reason
        /// <see cref="QuitAttempted"/> is: a Kill that throws is still interference.</summary>
        public void KillAttempted()
        {
            Require(_roundsOpened > 0, "a kill was attempted outside any termination round");
            _killsAttempted++;
            _roundKills++;
            CountRoundAsAttempted();
        }

        /// <summary>The wait after the kill says the operating system has ended the process.</summary>
        public void KillConfirmedProcessGone()
        {
            Require(_roundKills > 0, "a kill outcome was reported without a kill having been issued");
            Settle();
            _killConfirmedGone = true;
            Log.Info($"[CameraTerminationRecord] camera=\"{_deviceName}\" the kill was CONFIRMED: {Describe()}");
        }

        /// <summary>The wait after the kill timed out and the process is still there. THIS is the
        /// counter "abandoned" is built from, and one refusal is two thirds of the definition - never
        /// the whole of it.</summary>
        public void KillRefused()
        {
            Require(_roundKills > 0, "a kill outcome was reported without a kill having been issued");
            Settle();
            _refusedRounds++;
            Log.Warn($"[CameraTerminationRecord] camera=\"{_deviceName}\" the kill was REFUSED: {Describe()}");
        }

        /// <summary>
        /// The process was seen to exit AFTER this recorder had interfered with it, carrying this
        /// exit code. Whether that counts as an answer to "q" is not the caller's decision - it is
        /// <see cref="StopKind"/>'s, from this code and from whether a quit was ever delivered.
        /// </summary>
        public void ExitObservedAfterTermination(int exitCode)
        {
            Require(AnyAttemptMade, "an exit was reported as a termination outcome before anything was attempted");
            Settle();
            _exitObserved = true;
            _exitCode = exitCode;
            Log.Info($"[CameraTerminationRecord] camera=\"{_deviceName}\" the process exited with {exitCode}: {Describe()}");
        }

        /// <summary>
        /// The process was CONFIRMED gone at a moment when this recorder had never asked it to stop.
        ///
        /// RECORDS NOTHING ONCE ANY ATTEMPT HAS BEEN MADE, and that is the contract rather than a
        /// swallow (gate round 5, defect 2). The recorder reaches this observation point again on
        /// every retry pass, and by then a process found gone ended for a reason nothing here
        /// watched - it is not exited-early, and letting a later pass write that would make the
        /// durable record depend on when somebody next looked.
        /// </summary>
        public void ProcessGoneWithoutAnyAttempt()
        {
            if (AnyAttemptMade) return;
            _goneUntouched = true;
        }

        /// <summary>
        /// THE DERIVATION. The one place a <see cref="CameraStopKind"/> comes from, and a pure
        /// function of the history above.
        ///
        /// Null is a first-class answer and the DEFAULT one: none of the four kinds describes a
        /// process this recorder never watched end, so none of them is written, the manifest field
        /// is ABSENT, and <see cref="FfmpegCameraRecorder.Completeness"/> answers "unknown".
        /// </summary>
        public CameraStopKind? StopKind
        {
            get
            {
                // FIRST, AND MONOTONE. Once the process has survived the recorder's termination
                // sequence twice, it survived it - whatever happens afterwards. A later recovery
                // quit or kill that finally lands changes IsAbandoned (a fact about the process
                // now); it does not rewrite what was observed (gate round 6, defect 2).
                if (AbandonedEarned) return CameraStopKind.Abandoned;

                // It ended before this recorder ever asked it to.
                if (_goneUntouched) return CameraStopKind.ExitedEarly;

                // It was shot rather than asked, so ffmpeg never wrote the MP4 trailer.
                if (_killConfirmedGone) return CameraStopKind.ForceKilled;

                // It answered a quit that ACTUALLY REACHED IT, with a code an ffmpeg that answered
                // "q" is observed to return. Both clauses, or this is not a clean quit.
                if (_exitObserved && _quitsDelivered > 0 && Array.IndexOf(QuitExitCodes, _exitCode) >= 0)
                    return CameraStopKind.CleanQuit;

                return null;
            }
        }

        /// <summary>The whole history on one ASCII line, for the recorder's log.</summary>
        public string Describe() =>
            $"rounds={_roundsOpened} attemptedRounds={_roundsWithAnAttempt} refusedRounds={_refusedRounds} "
            + $"quits={_quitsAttempted}(delivered={_quitsDelivered}) kills={_killsAttempted} "
            + $"exit={(_exitObserved ? _exitCode.ToString() : "none")} goneUntouched={_goneUntouched} "
            + $"killConfirmed={_killConfirmedGone} kind={CameraObservation.Text(StopKind) ?? "(not observed)"}";

        /// <summary>Count the current round as a termination round the first time it touches the
        /// process, and only the first time.</summary>
        private void CountRoundAsAttempted()
        {
            if (_roundHadAnAttempt) return;
            _roundHadAnAttempt = true;
            _roundsWithAnAttempt++;
        }

        /// <summary>One round reports ONE outcome. Two would mean the recorder had lost track of
        /// what it was doing, and this record would be recording a history that never happened.</summary>
        private void Settle()
        {
            Require(!_roundSettled, "a termination round reported a second outcome");
            _roundSettled = true;
        }

        /// <summary>
        /// FAIL EXPLICITLY, NEVER SILENTLY. A violated precondition here is a programming error at a
        /// call site - the exact class of mistake four review rounds kept finding - so it throws
        /// rather than admitting an observation the history does not support. The tests run every
        /// production path, so a call site added later that tries to shortcut the sequence breaks
        /// the build's own suite instead of writing a claim into a manifest.
        /// </summary>
        private void Require(bool condition, string what)
        {
            if (condition) return;
            string message = $"[CameraTerminationRecord] camera=\"{_deviceName}\": {what}. History: {Describe()}";
            Log.Error(message);
            throw new InvalidOperationException(message);
        }
    }
}
