using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AgentEyes.DevThrottle;
using AgentEyes.Packaging;

namespace AgentEyes
{
    /// <summary>
    /// What happens after a recording stops - the WHOLE sequence, in ONE place, for every stop path
    /// (issues #142, #151 and #152).
    ///
    /// History, because it explains why this class is written the way it is. The same defect shipped
    /// three times: issue #141 (the thumbnail moved after the deferred mux, wired into the window
    /// only), issue #142 (the repair pass, wired into the window only), and issue #151 (the tray,
    /// which called a bare <c>RecordingService.Stop</c> and therefore produced raw media and nothing
    /// else - no mixed audio, no thumbnail, no transcript, no title). #142 created this class to be
    /// "one place for every stop path" and then wired only the REST caller to it, leaving the window
    /// running a private staged duplicate. That duplicate is gone: <see cref="Run"/> is now the only
    /// implementation of the sequence, and the app funnels every stop of a kept recording into it
    /// through <c>AgentEyes.App.RecordingStop.Keep</c>.
    ///
    /// Issue #152 made the sequence SURVIVABLE, which is a different property from being shared:
    ///  - Each stage is individually failure-isolated. The stages used to sit in one try block, so a
    ///    transient ffmpeg error while extracting a poster frame skipped packaging entirely and the
    ///    recording was never transcribed and never titled - a thumbnail cost a transcript. A stage
    ///    now only blocks the stages that genuinely DEPEND on it (nothing can be made from media the
    ///    mux never wrote); everything else runs anyway.
    ///  - Each stage's outcome is written to the recording's manifest
    ///    (<see cref="PostRecordingState"/>), so it survives the process.
    ///  - Outstanding work is decided from the artifacts on disk (<see cref="PostRecordingPlan"/>),
    ///    so an interrupted sequence RESUMES at the stage it reached instead of starting over or
    ///    being abandoned. <see cref="Resume"/> is that pass, and <see cref="RepairService"/> runs it
    ///    on a timer at app level - which means it works in --tray mode, where no window is ever
    ///    constructed and the old window-owned backfill did not exist at all.
    ///  - <see cref="IsBusy"/> reports post-recording work in flight, so "is this app safe to
    ///    restart" means no capture AND no post-processing, not capture alone.
    ///
    /// Events:
    ///  - <see cref="Completed"/> fires when a recording's post-processing has finished, success OR
    ///    failure. Anything that must happen "after a recording" subscribes ONCE here instead of
    ///    being bolted onto each stop path again.
    ///  - <see cref="Failed"/> fires for each stage that threw, so a UI that happens to be open can
    ///    say so (an empty wallet is the common case). Nothing is required to subscribe.
    ///  - <see cref="WorkIdle"/> fires when the LAST in-flight post-recording work finishes, which is
    ///    the moment a deferred update restart may finally proceed.
    ///
    /// A caller that deliberately does NOT want this sequence (the HUD's Discard, the guided test
    /// panel's throwaway takes) uses an explicitly NAMED operation on <c>RecordingStop</c> that says
    /// so - skipping post-processing by omission is what this class exists to make impossible.
    /// </summary>
    internal static class PostRecording
    {
        /// <summary>Stage label: the deferred audio mux / system downmix is being completed.</summary>
        public const string StageMixing = "Mixing audio...";

        /// <summary>Stage label: the Library poster/waveform image is being generated.</summary>
        public const string StageThumbnail = "Making the thumbnail...";

        /// <summary>Stage label: packaging - transcription, title, walkthrough.</summary>
        public const string StageTranscribing = "Transcribing...";

        /// <summary>Stage label: the sequence is over (clears a UI status line).</summary>
        public const string StageDone = "";

        /// <summary>
        /// Raised after a recording's post-processing has finished - success OR failure. Failure is
        /// exactly when the repair passes matter (a lost title, a poster ffmpeg could not write), so
        /// this must not be conditional on the packaging having succeeded.
        ///
        /// Raised on whatever thread ran the post-processing (a background thread, never the UI
        /// thread); a WPF subscriber marshals for itself.
        /// </summary>
        public static event Action<string>? Completed;

        /// <summary>
        /// Raised (dir, error) when a STAGE threw. Purely for reporting - the sequence has already
        /// logged the failure, recorded it in the manifest, and carried on with the stages that do
        /// not depend on it - so a window that is open can show "out of credits" while a tray-only
        /// process just logs it. Background thread.
        /// </summary>
        public static event Action<string, Exception>? Failed;

        /// <summary>
        /// Raised when the last in-flight post-recording work finishes and
        /// <see cref="IsBusy"/> goes false (issue #152).
        ///
        /// It exists because <c>RecordingService.Stop</c> announces the end of the CAPTURE, which is
        /// minutes before the end of the WORK: an update restart triggered by that signal used to
        /// kill the mux and the transcription it had not started yet. This is the honest "the app is
        /// idle now" moment. Background thread.
        /// </summary>
        public static event Action? WorkIdle;

        /// <summary>
        /// The app's post-packaging step (recording plugins - issue #13), registered ONCE at startup
        /// by <c>AgentEyes.App.RecordingStop.Configure</c>. Arguments are (recording directory,
        /// progress). Null in the CLI, which has no plugin system. It is registered at app level and
        /// not passed in per stop, because a caller that can forget to pass it is exactly the bug
        /// issue #151 fixed.
        /// </summary>
        public static Action<string, Action<string>?>? AfterPackaging;

        // ---- the stages, as replaceable steps -------------------------------
        //
        // Each stage is one named delegate so the sequence's failure isolation can be exercised
        // directly: a test injects a thumbnail that throws and proves the transcript still happens
        // (issue #152 AC1) without needing ffmpeg, a network, or a wallet. Production always runs the
        // defaults below; RestoreDefaultSteps puts them back.

        /// <summary>Stage 1 - complete the deferred audio mux (issue #77).</summary>
        internal static Action<string> MuxStep = RecordingService.FinalizePending;

        /// <summary>Stage 2 - the Library poster / waveform tile. The attempt is counted BEFORE the
        /// work, so a file ffmpeg can never read drops out of the automatic passes.</summary>
        internal static Action<string> ThumbnailStep = dir =>
        {
            Thumbnails.NoteThumbAttempt(dir);
            Thumbnails.Ensure(dir);
        };

        /// <summary>
        /// Stage 3 - transcription, title, walkthrough. The wallet is checked FIRST so an empty
        /// wallet fails clearly here instead of deep inside transcription, and so it does not spend
        /// one of this recording's three transcription attempts on a certainty.
        /// </summary>
        internal static Action<string> PackageStep = dir =>
        {
            DevThrottleClient.EnsureCreditsForHostedWorkAsync().GetAwaiter().GetResult();
            TranscriptionBacklog.NoteAttempt(dir);
            Package.Run(dir, 5.0, null);
        };

        /// <summary>Puts the production steps back. For tests that inject a failing stage.</summary>
        internal static void RestoreDefaultSteps()
        {
            MuxStep = RecordingService.FinalizePending;
            ThumbnailStep = dir => { Thumbnails.NoteThumbAttempt(dir); Thumbnails.Ensure(dir); };
            PackageStep = dir =>
            {
                DevThrottleClient.EnsureCreditsForHostedWorkAsync().GetAwaiter().GetResult();
                TranscriptionBacklog.NoteAttempt(dir);
                Package.Run(dir, 5.0, null);
            };
        }

        // ---- work in flight --------------------------------------------------

        private static int _workInFlight;

        /// <summary>
        /// True while any post-recording work is in flight - from the moment a keeping stop is
        /// DECIDED until the last stage of that recording has finished (issue #152). The app's
        /// exit/update readiness check reads this: capture ending is not the app going idle.
        /// </summary>
        public static bool IsBusy => Volatile.Read(ref _workInFlight) > 0;

        /// <summary>How many post-recording jobs are in flight. Diagnostics and tests.</summary>
        public static int WorkInFlight => Volatile.Read(ref _workInFlight);

        /// <summary>
        /// Marks post-recording work as in flight until the returned ticket is disposed.
        ///
        /// <c>RecordingStop.Keep</c> takes a ticket BEFORE it stops the capture, because
        /// <c>RecordingService.Stop</c> raises RecordingStopped inside that call and a deferred
        /// update restart listens to it: without the ticket the process answers "no session active"
        /// during the gap between the capture ending and the background sequence starting, and
        /// restarts straight through the recording's post-processing.
        /// </summary>
        public static IDisposable TrackWork(string what)
        {
            int count = Interlocked.Increment(ref _workInFlight);
            Log.Info($"[PostRecording] TrackWork: {what} (in flight={count})");
            return new WorkTicket(what);
        }

        /// <summary>One in-flight post-recording job. Disposing is idempotent - a ticket released
        /// twice must not make the app look idle while work is still running.</summary>
        private sealed class WorkTicket : IDisposable
        {
            private readonly string _what;
            private int _released;

            public WorkTicket(string what) => _what = what;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                int count = Interlocked.Decrement(ref _workInFlight);
                Log.Info($"[PostRecording] TrackWork released: {_what} (in flight={count})");
                if (count == 0) NotifyWorkIdle();
            }
        }

        // ---- the sequence ----------------------------------------------------

        /// <summary>
        /// The post-stop sequence. Blocking - the caller runs it on a background thread. Order
        /// matters (issue #141): the deferred mux writes the final media file, THEN the thumbnail
        /// can be generated from it, THEN packaging transcribes and names. Doing the thumbnail
        /// before packaging means the Library card gets its poster even when transcription fails
        /// (signed out, out of credits) - and since issue #152 a thumbnail that FAILS no longer costs
        /// the transcript either.
        ///
        /// <paramref name="progress"/> receives the Stage* labels as each stage starts and
        /// <see cref="StageDone"/> at the end; it is called on this background thread, so a WPF
        /// subscriber marshals for itself. It is how a window shows the sequence without owning a
        /// private copy of it (issue #151).
        ///
        /// <paramref name="hostedWorkAllowed"/> is true for a stop, which happens seconds after the
        /// recording ended and is the moment the user expects their transcript. It exists (issue
        /// #154) for the QUEUED retry, which can land hours later and after a sign-out: attempting
        /// the packaging stage signed out fails on a certainty and spends one of the recording's
        /// three transcription attempts to do it, so a retry passes the live sign-in state and leaves
        /// packaging outstanding for a pass that can actually succeed - the same rule
        /// <see cref="Resume"/> has always followed.
        /// </summary>
        public static void Run(string dir, Action<string>? progress = null, bool hostedWorkAllowed = true)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("dir is required", nameof(dir));
            Log.Info($"[PostRecording] Run: dir={dir} hostedWork={hostedWorkAllowed}");

            using var work = TrackWork("post-recording " + Path.GetFileName(dir));

            // Claim it for the whole sequence so an automatic repair pass cannot write this
            // recording's manifest underneath us (issue #142).
            //
            // Issue #154: a refusal is not the end of this recording. Who the owner is decides what
            // happens, and the two answers are genuinely different:
            //  - a FULL PIPELINE owner runs every stage this call would, so there is nothing left to
            //    do and returning is correct;
            //  - a STAGE owner (a title repair, a thumbnail repair, a walkthrough rebuild) or a
            //    CAPTURE owner does NOT, so returning here is how a recording ended up with no
            //    packaging at all. That intent is queued and run when the directory is free.
            if (!RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "post-recording", out var claim))
            {
                var owner = RecordingWorkset.OwnerKind(dir);
                if (owner == RecordingWorkKind.FullPipeline)
                {
                    Log.Info($"[PostRecording] Run: {dir} is already being processed by another full "
                        + "pipeline, which runs every stage this call would; nothing to do");
                    return;
                }

                Log.Warn($"[PostRecording] Run: {dir} is held by "
                    + $"{RecordingWorkset.OwnerDescription(dir) ?? "a claim that has just been released"}, "
                    + "which does NOT cover the full sequence - queueing it rather than dropping it");
                PostRecordingQueue.Enqueue(dir, "the directory was claimed by "
                    + (owner?.ToString() ?? "an owner that released it in the same instant"));
                return;
            }

            // This recording is being processed now, so it is no longer waiting for a turn.
            PostRecordingQueue.NoteStarted(dir);

            try
            {
                Execute(dir, hostedWorkAllowed, progress);
                Log.Info($"[PostRecording] Run: dir={dir} done");
            }
            finally
            {
                Report(progress, StageDone);
                RecordingWorkset.Release(claim);
                NotifyCompleted(dir);
            }
        }

        /// <summary>
        /// Finish a recording whose post-processing never completed - a leftover pending mux, a
        /// missing thumbnail, a missing transcript (issue #152). This is what the app-level recovery
        /// pass calls, and it is the reason an interrupted recording is no longer stranded: it runs
        /// only the stages the files on disk say are still outstanding, so an interruption after
        /// stage N resumes at stage N+1.
        ///
        /// Blocking; the caller runs it on a background thread. Never throws - it is driven from a
        /// timer with nothing above it to report a failure - and never raises
        /// <see cref="Completed"/>: this IS the repair pass, and announcing a completion would just
        /// ask it to run itself again.
        /// </summary>
        /// <param name="hostedWorkAllowed">false when the account is signed out, so the packaging
        /// stage is left for a pass that can actually succeed instead of burning an attempt.</param>
        public static PostRecordingOutcome Resume(string dir, bool hostedWorkAllowed, Action<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("dir is required", nameof(dir));
            Log.Info($"[PostRecording] Resume: dir={dir} hostedWork={hostedWorkAllowed}");

            using var work = TrackWork("resume " + Path.GetFileName(dir));

            var outcome = new PostRecordingOutcome(dir);
            if (!RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "resume", out var claim))
            {
                // Deliberately NOT queued (issue #154), unlike Run. This IS the retry: the recovery
                // pass re-reads the artifacts on disk every time it runs, so a recording it could not
                // claim this minute is found again by the next pass. Queuing it too would give the
                // same recording two paths asking for the same work.
                Log.Info($"[PostRecording] Resume: {dir} is held by "
                    + $"{RecordingWorkset.OwnerDescription(dir) ?? "a claim that has just been released"}; "
                    + "leaving it for the next recovery pass");
                outcome.Skipped = true;
                return outcome;
            }

            try
            {
                return Execute(dir, hostedWorkAllowed, progress, outcome);
            }
            finally
            {
                Report(progress, StageDone);
                RecordingWorkset.Release(claim);
            }
        }

        /// <summary>
        /// Runs the outstanding stages for a recording the caller has already claimed.
        ///
        /// The dependency rule, which is the whole of issue #152's first half: a stage blocks only
        /// what genuinely depends on it. Nothing can be made from media the mux never wrote, so a
        /// failed mux leaves the later stages PENDING for the next recovery pass. A failed thumbnail
        /// blocks nothing - the transcript does not come from the poster frame - so packaging runs
        /// anyway. Plugins consume the packaged artifacts, so they run only behind a packaging stage
        /// that ran and succeeded in this pass.
        ///
        /// Each stage's need is re-evaluated at its own moment rather than planned up front: the mux
        /// writes the very file the next two stages test for.
        /// </summary>
        private static PostRecordingOutcome Execute(
            string dir, bool hostedWorkAllowed, Action<string>? progress, PostRecordingOutcome? existing = null)
        {
            var outcome = existing ?? new PostRecordingOutcome(dir);
            try
            {
                // Loud, not silent: every stage below decides what it needs from this file, so a
                // recording without one is not "nothing to do" - it is a stop that failed to write
                // the recording, and it must be reported as one.
                if (!File.Exists(Path.Combine(dir, "manifest.json")))
                    throw new UsageException($"no manifest.json in {dir} - the recording cannot be post-processed.");

                if (PostRecordingPlan.NeedsMux(dir))
                {
                    // Counted before the work: a process killed mid-mux must still consume a try, or
                    // a capture ffmpeg can never mux would re-run on every recovery pass forever.
                    PostRecordingState.NoteStarted(dir, PostStage.Mux);
                    if (!RunStage(dir, PostStage.Mux, StageMixing, progress, MuxStep, outcome))
                    {
                        Log.Info($"[PostRecording] Execute: {Path.GetFileName(dir)} - the mux failed; "
                            + "the thumbnail and packaging stages need the final media file, so they stay "
                            + "outstanding for the next recovery pass");
                        return outcome;
                    }
                }
                else
                {
                    Log.Info($"[PostRecording] Execute: {Path.GetFileName(dir)} - nothing to mux");
                }

                if (PostRecordingPlan.NeedsThumbnail(dir))
                {
                    // Deliberately ignoring the result: a thumbnail is a Library nicety and the
                    // transcript is the recording's value. This ONE line is issue #152's first half.
                    RunStage(dir, PostStage.Thumbnail, StageThumbnail, progress, ThumbnailStep, outcome);
                }
                else
                {
                    Log.Info($"[PostRecording] Execute: {Path.GetFileName(dir)} - thumbnail already present or not applicable");
                }

                if (!PostRecordingPlan.NeedsPackage(dir))
                {
                    Log.Info($"[PostRecording] Execute: {Path.GetFileName(dir)} - already transcribed (or out of attempts); nothing to package");
                    return outcome;
                }

                if (!hostedWorkAllowed)
                {
                    Log.Info($"[PostRecording] Execute: {Path.GetFileName(dir)} - not signed in; leaving the packaging stage outstanding");
                    return outcome;
                }

                if (RunStage(dir, PostStage.Package, StageTranscribing, progress, PackageStep, outcome)
                    && AfterPackaging != null)
                {
                    RunStage(dir, PostStage.Plugins, StageTranscribing, progress,
                        d => AfterPackaging!.Invoke(d, progress), outcome);
                }
            }
            catch (Exception ex)
            {
                // Entry point for the background pass: reaching here means the recording itself could
                // not be read (no manifest.json, unreadable JSON), so no stage could even be judged.
                // Nothing above this can report it, and it must still reach the repair passes.
                Log.Error($"[PostRecording] Execute FAILED before any stage could run: {dir}", ex);
                outcome.Error = ex;
                NotifyFailed(dir, ex);
            }
            return outcome;
        }

        /// <summary>
        /// Runs ONE stage and records its outcome durably. Returns true when the stage's work
        /// succeeded.
        ///
        /// The try/catch here is the failure isolation itself (issue #152), not a swallowed error:
        /// the exception is logged in full, written to the recording's manifest, and announced on
        /// <see cref="Failed"/>. What it stops is one stage taking the REST of the sequence down with
        /// it.
        ///
        /// Note what decides success: the WORK, never the bookkeeping. If the journal write fails
        /// (the manifest was deleted underneath us) the stage still counts as done, because the
        /// artifact it produced is on disk and the artifacts are what the resume rule reads.
        /// </summary>
        private static bool RunStage(
            string dir, string stage, string label, Action<string>? progress,
            Action<string> work, PostRecordingOutcome outcome)
        {
            Report(progress, label);
            Log.Info($"[PostRecording] stage {stage}: start dir={Path.GetFileName(dir)}");
            var sw = Stopwatch.StartNew();

            Exception? error = null;
            try
            {
                work(dir);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (error == null)
            {
                Log.Info($"[PostRecording] stage {stage}: done dir={Path.GetFileName(dir)} in {sw.Elapsed.TotalSeconds:0.0}s");
                outcome.Completed.Add(stage);
                Journal(dir, stage, PostStageState.Done, null);
                return true;
            }

            Log.Error($"[PostRecording] stage {stage} FAILED: dir={dir}", error);
            outcome.Failed.Add(stage);
            outcome.Error = error;
            Journal(dir, stage, PostStageState.Failed, error.Message);
            NotifyFailed(dir, error);
            return false;
        }

        /// <summary>Writes one stage outcome to the recording's manifest. A journal write that fails
        /// is logged and dropped: it must not turn a stage that worked into a stage that failed, and
        /// the resume rule reads the artifacts on disk, not this record.</summary>
        private static void Journal(string dir, string stage, string state, string? error)
        {
            try
            {
                if (state == PostStageState.Done) PostRecordingState.NoteDone(dir, stage);
                else PostRecordingState.NoteFailed(dir, stage, error ?? "");
            }
            catch (Exception ex)
            {
                Log.Error($"[PostRecording] could not record stage '{stage}' as '{state}' for {dir}", ex);
            }
        }

        /// <summary>
        /// Announces that <paramref name="dir"/> has finished post-processing. Called by
        /// <see cref="Run"/> - the single place the sequence ends, on every stop path (issue #151).
        /// </summary>
        public static void NotifyCompleted(string dir)
        {
            Log.Info($"[PostRecording] NotifyCompleted: dir={Path.GetFileName(dir)}");
            var handlers = Completed;
            if (handlers == null) return;

            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                // Isolated per subscriber: this is a fan-out point, and one subscriber throwing must
                // not rob the others of the notification or take down the background stop pass.
                try { handler(dir); }
                catch (Exception ex) { Log.Error("[PostRecording] NotifyCompleted subscriber FAILED", ex); }
            }
        }

        /// <summary>Announces that the sequence for <paramref name="dir"/> threw. Same fan-out
        /// isolation as <see cref="NotifyCompleted"/>: a reporting subscriber must never stop the
        /// pass from reaching its finally block.</summary>
        private static void NotifyFailed(string dir, Exception error)
        {
            var handlers = Failed;
            if (handlers == null) return;

            foreach (Action<string, Exception> handler in handlers.GetInvocationList())
            {
                try { handler(dir, error); }
                catch (Exception ex) { Log.Error("[PostRecording] NotifyFailed subscriber FAILED", ex); }
            }
        }

        /// <summary>Announces that no post-recording work is in flight any more. Same fan-out
        /// isolation: a subscriber that throws must not strand the count or the caller.</summary>
        private static void NotifyWorkIdle()
        {
            var handlers = WorkIdle;
            if (handlers == null) return;

            Log.Info("[PostRecording] WorkIdle: no post-recording work is in flight");
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { Log.Error("[PostRecording] WorkIdle subscriber FAILED", ex); }
            }
        }

        /// <summary>Hands one stage label to the caller's progress sink. A reporting sink that
        /// throws must not abort the sequence it is only describing.</summary>
        private static void Report(Action<string>? progress, string stage)
        {
            if (progress == null) return;
            try { progress(stage); }
            catch (Exception ex) { Log.Error($"[PostRecording] progress '{stage}' FAILED", ex); }
        }
    }

    /// <summary>
    /// What one run of the post-recording sequence did (issue #152): which stages finished, which
    /// failed, and the last failure. The recovery pass reads it to decide whether to keep going -
    /// an empty wallet fails identically on every remaining recording, so the pass stops rather than
    /// working through the whole library to prove it.
    /// </summary>
    internal sealed class PostRecordingOutcome
    {
        public PostRecordingOutcome(string dir) => Dir = dir;

        /// <summary>The recording this outcome describes.</summary>
        public string Dir { get; }

        /// <summary>Stages that ran and succeeded, in order.</summary>
        public List<string> Completed { get; } = new();

        /// <summary>Stages that ran and threw, in order.</summary>
        public List<string> Failed { get; } = new();

        /// <summary>The last failure, or null when nothing failed.</summary>
        public Exception? Error { get; set; }

        /// <summary>True when the sequence did not run at all because another path already owns this
        /// recording. Not a failure - it comes back on the next pass.</summary>
        public bool Skipped { get; set; }

        /// <summary>True when any stage failed.</summary>
        public bool AnyFailed => Failed.Count > 0;
    }
}
