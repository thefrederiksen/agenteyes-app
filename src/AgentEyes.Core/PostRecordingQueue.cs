using System;
using System.Collections.Concurrent;
using System.Threading;
using AgentEyes.DevThrottle;

namespace AgentEyes
{
    /// <summary>
    /// The full post-recording sequences that could not START because something else held the
    /// recording, kept so they can be run when it is free (issue #154).
    ///
    /// THE DEFECT THIS EXISTS FOR. <see cref="PostRecording.Run"/> used to return permanently the
    /// moment any claim was on the directory. The repair passes claim a recording for only a title or
    /// only a thumbnail, so a repair scan that reached a just-finished recording first cancelled its
    /// ENTIRE pipeline: the sequence logged "already being processed", the repair pass finished its
    /// partial work and released, and nobody ever muxed, thumbnailed, transcribed or titled that
    /// recording. There was no queue and no retry - the intent to process it was simply dropped on
    /// the floor.
    ///
    /// So the intent is now kept. A refused full pipeline is queued here, and it is run again:
    ///
    ///  - PROMPTLY, when the claim that refused it is released
    ///    (<see cref="RecordingWorkset.Released"/>). A title repair takes seconds, so in the exact
    ///    scenario above the recording is packaged seconds later instead of never.
    ///  - As a BACKSTOP, from <see cref="RepairService.RunAsync"/>, which drains whatever is still
    ///    queued at the end of every pass.
    ///
    /// And when even that fails, nothing is lost: a recording that is not fully processed is
    /// unfinished on disk, so <see cref="PostRecordingPlan.FindUnfinished"/> keeps finding it and
    /// <see cref="PostRecording.Resume"/> keeps finishing it (issue #152). That durable pass is why
    /// this queue is allowed to be bounded by <see cref="MaxAttempts"/> rather than retrying forever:
    /// the queue's job is to be PROMPT, not to be the last line of defence.
    ///
    /// Process-local by design - it is the in-memory intent of a stop that has just happened. It is
    /// deliberately NOT persisted: the durable record of outstanding work is the recording's own
    /// artifacts and stage journal, and a second source of truth for that is how the two would drift.
    /// </summary>
    internal static class PostRecordingQueue
    {
        /// <summary>
        /// How many times one recording may be re-queued before the prompt retry gives up and leaves
        /// it to the durable recovery pass. Five is enough to outlast a couple of overlapping repair
        /// stages; an unbounded count would let two threads that keep refusing each other spin.
        /// </summary>
        public const int MaxAttempts = 5;

        /// <summary>
        /// One queued recording: how many times it has been queued, and whether the prompt retry has
        /// given up on it.
        ///
        /// The give-up is a TOMBSTONE rather than a removal, and that is the whole point of it. If
        /// the entry were simply deleted, the attempt count would go with it and the very next
        /// refusal would start again at attempt 1 - so the bound would not bound anything. The
        /// tombstone is cleared by <see cref="NoteStarted"/>, i.e. when the recording really is being
        /// processed, so it cannot outlive the condition that created it.
        /// </summary>
        private readonly struct PendingJob : IEquatable<PendingJob>
        {
            public PendingJob(int attempts, bool gaveUp, bool running = false)
            {
                Attempts = attempts;
                GaveUp = gaveUp;
                Running = running;
            }

            /// <summary>How many times this recording has been queued.</summary>
            public int Attempts { get; }

            /// <summary>True once the prompt retry has stopped trying; the recovery pass owns it.</summary>
            public bool GaveUp { get; }

            /// <summary>
            /// True while ONE drainer has reserved this job and is running it (issue #154, round 3).
            ///
            /// This is the reservation that stops two drainers running one recording twice.
            /// <see cref="Drain"/> used to enumerate a snapshot, check the workset and call the
            /// runner, while the job was removed only later, by <see cref="NoteStarted"/>, once the
            /// sequence had won its own claim. Two drainers holding the same snapshot therefore both
            /// called the runner: the first ran the whole pipeline and released, and the second
            /// found the directory free, claimed it, and packaged the recording a SECOND time -
            /// two ffmpeg passes, two transcriptions, two Completed events.
            /// </summary>
            public bool Running { get; }

            /// <summary>Reserved for exactly one drainer.</summary>
            public PendingJob AsRunning() => new(Attempts, GaveUp, running: true);

            /// <summary>Back in the queue, with its attempt count intact.</summary>
            public PendingJob AsQueued() => new(Attempts, GaveUp, running: false);

            // Compared by VALUE, because ConcurrentDictionary.TryUpdate's compare-and-swap is what
            // makes the reservation atomic. The default struct comparison would work by reflection,
            // which is both slow and easy to get silently wrong when a field is added.
            public bool Equals(PendingJob other) =>
                Attempts == other.Attempts && GaveUp == other.GaveUp && Running == other.Running;

            public override bool Equals(object? obj) => obj is PendingJob other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Attempts, GaveUp, Running);
        }

        /// <summary>Normalized directory -> what we know about its queued sequence.</summary>
        private static readonly ConcurrentDictionary<string, PendingJob> Pending =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What a retry actually runs. Production runs the same sequence that was refused; a test
        /// replaces it to observe the retry without ffmpeg, a network or a wallet - the pattern
        /// <see cref="PostRecording.MuxStep"/> and friends already use.
        ///
        /// Hosted work is allowed only while signed IN. A stop runs its sequence seconds after the
        /// recording ends, but a queued retry can land hours later and after a sign-out, and the
        /// packaging stage would then fail on a certainty while spending one of that recording's
        /// three transcription attempts to do it - which is exactly what the hostedWorkAllowed
        /// parameter on the recovery pass exists to prevent. Signed out, packaging is left
        /// outstanding for a pass that can actually succeed.
        /// </summary>
        internal static Action<string> Runner = dir => PostRecording.Run(dir, null, AccountState.IsSignedIn);

        /// <summary>
        /// How a prompt retry gets off the releasing thread. The release happens inside somebody's
        /// finally block, and running a whole post-recording sequence there would block them for
        /// minutes, so production hands it to the thread pool. A test substitutes a synchronous
        /// dispatcher to make the retry deterministic.
        /// </summary>
        internal static Action<Action> Dispatcher = work => ThreadPool.QueueUserWorkItem(_ => work());

        static PostRecordingQueue()
        {
            // Armed on first touch. Every PostRecording.Run touches this class - NoteStarted on the
            // way in, Enqueue when it is refused - so the subscription exists before anything can be
            // waiting on it. Enqueue also re-checks the claim after adding, so a release landing in
            // the gap between a refusal and the enqueue cannot be missed either.
            RecordingWorkset.Released += OnClaimReleased;
        }

        /// <summary>How many recordings are waiting for a free directory. Diagnostics and tests.
        /// A recording the prompt retry has given up on is not waiting, and is not counted.</summary>
        public static int Count
        {
            get
            {
                int waiting = 0;
                foreach (var job in Pending.Values) if (!job.GaveUp) waiting++;
                return waiting;
            }
        }

        /// <summary>True while <paramref name="dir"/> is waiting for its full pipeline to be run.</summary>
        public static bool IsQueued(string dir) =>
            !string.IsNullOrWhiteSpace(dir)
            && Pending.TryGetValue(RecordingWorkset.Key(dir), out var job)
            && !job.GaveUp;

        /// <summary>
        /// Keeps the intent to run the full post-recording sequence for <paramref name="dir"/>,
        /// because something else held the directory when it was asked for.
        ///
        /// Logged as a WARNING, not an Info: this is the moment a recording's packaging did not
        /// happen, and if the retry never lands it is the line that explains why.
        /// </summary>
        public static void Enqueue(string dir, string reason)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("dir is required", nameof(dir));
            string key = RecordingWorkset.Key(dir);

            bool alreadyGivenUp = Pending.TryGetValue(key, out var prior) && prior.GaveUp;
            var job = Pending.AddOrUpdate(
                key,
                new PendingJob(1, gaveUp: false),
                (_, existing) => existing.GaveUp
                    ? existing                                       // a tombstone stays a tombstone
                    : new PendingJob(existing.Attempts + 1, existing.Attempts + 1 > MaxAttempts));

            if (job.GaveUp)
            {
                if (!alreadyGivenUp)
                    Log.Error($"[PostRecordingQueue] Enqueue: {key} could not start its full sequence in "
                        + $"{MaxAttempts} attempts ({reason}) - the prompt retry gives up; the recording is still "
                        + "unfinished on disk and the recovery pass (PostRecording.Resume) remains responsible for it");
                else
                    Log.Info($"[PostRecordingQueue] Enqueue: {key} was already given up on ({reason}) - "
                        + "it stays the recovery pass's responsibility");
                return;
            }

            Log.Warn($"[PostRecordingQueue] Enqueue: {key} queued for its full post-recording sequence "
                + $"(attempt {job.Attempts} of {MaxAttempts}; {reason})");

            // The claim may have been released between the refusal and this line - in which case no
            // Released event is coming for it and the queue would sit there until the next repair
            // pass. Check now, and go straight away if it is already free.
            if (!RecordingWorkset.IsClaimed(key)) DispatchDrain($"{key} is free already");
        }

        /// <summary>
        /// The recording's full sequence has actually started, so it is no longer waiting. Called by
        /// <see cref="PostRecording.Run"/> the moment it wins the claim - including on a first,
        /// never-queued run, which is what keeps a stale entry from an earlier refusal from
        /// re-running a recording that has since been processed.
        /// </summary>
        public static void NoteStarted(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (Pending.TryRemove(RecordingWorkset.Key(dir), out var job))
                Log.Info($"[PostRecordingQueue] NoteStarted: {RecordingWorkset.Key(dir)} is running its full "
                    + $"sequence after {job.Attempts} queued attempt(s)"
                    + (job.GaveUp ? " (the prompt retry had given up on it)" : ""));
        }

        /// <summary>
        /// Runs the queued sequences whose directory is free right now, on the CALLING thread.
        /// Returns how many were started.
        ///
        /// A recording that is still claimed stays queued - the next release, or the next repair
        /// pass, comes back for it. Nothing is removed here: <see cref="NoteStarted"/> removes an
        /// entry only when the sequence really did claim the recording, so a retry that is refused
        /// again keeps its place (and its attempt count).
        ///
        /// EXACTLY ONE drainer runs a given job (issue #154, round 3). Two threads drain at once -
        /// the release-triggered retry runs on whatever thread let a claim go, and the repair pass
        /// drains at the end of every pass - and both can enumerate the same pending job. The
        /// workset claim does NOT decide it: the claim is taken further down, inside
        /// <see cref="PostRecording.Run"/>, so a drainer delayed at the runner boundary while the
        /// other one ran the whole pipeline and released found a free directory and ran it again.
        /// The reservation below is the compare-and-swap that makes "this job is mine" atomic;
        /// everything after it either starts the sequence (<see cref="NoteStarted"/> removes the
        /// entry), re-queues it (<see cref="Enqueue"/> clears the reservation and counts an
        /// attempt), or, if the runner did neither, puts it back exactly as it was.
        /// </summary>
        public static int Drain()
        {
            if (Pending.IsEmpty) return 0;

            // Issue #154: a queued sequence is a deferred mux, a poster ffmpeg and a transcription
            // upload, and this is the ONE place that can start one with no IsRecording delegate of
            // its own - the release-triggered retry runs on whatever thread happened to let a claim
            // go. Capture wins here for the same reason it wins in RepairService. Nothing is lost by
            // waiting: the capture's own claim is released when it stops, which raises Released and
            // brings us straight back here.
            if (RecordingWorkset.CaptureInProgress)
            {
                Log.Info($"[PostRecordingQueue] Drain: a capture is in progress - {Count} queued "
                    + "recording(s) stay queued until it ends");
                return 0;
            }

            int started = 0;
            foreach (var entry in Pending.ToArray())
            {
                string dir = entry.Key;
                if (entry.Value.GaveUp) continue;      // the recovery pass owns this one now
                if (entry.Value.Running) continue;     // another drainer already has it
                if (RecordingWorkset.IsClaimed(dir))
                {
                    Log.Info($"[PostRecordingQueue] Drain: {dir} is still claimed by "
                        + $"{RecordingWorkset.OwnerDescription(dir) ?? "(nobody)"} - it stays queued");
                    continue;
                }

                // RESERVE IT, atomically, BEFORE the runner is invoked. TryUpdate is a
                // compare-and-swap against the exact value this drainer enumerated, so it fails for
                // a snapshot that is out of date - which is precisely the loser of a two-drainer
                // race, whether the winner is still running it, has already removed it
                // (NoteStarted), or has re-queued it with a higher attempt count.
                var reserved = entry.Value.AsRunning();
                if (!Pending.TryUpdate(dir, reserved, entry.Value))
                {
                    Log.Info($"[PostRecordingQueue] Drain: {dir} was taken by another drainer - leaving it to them");
                    continue;
                }

                Log.Info($"[PostRecordingQueue] Drain: retrying the full post-recording sequence for {dir}");
                started++;
                try
                {
                    Runner(dir);
                }
                finally
                {
                    // Give the reservation back only if it is still exactly ours. It will NOT be
                    // when the sequence started (NoteStarted removed the entry) or when it was
                    // refused again (Enqueue replaced it with a fresh, unreserved attempt) - and in
                    // both of those cases touching it would either resurrect a finished recording or
                    // lose an attempt count. What this does cover is a runner that neither started
                    // nor re-queued: without it the job would sit reserved forever and no later
                    // drain would look at it again.
                    if (Pending.TryUpdate(dir, reserved.AsQueued(), reserved))
                        Log.Info($"[PostRecordingQueue] Drain: {dir} was neither started nor re-queued by the "
                            + "runner - it goes back in the queue");
                }
            }

            Log.Info($"[PostRecordingQueue] Drain: started={started} stillQueued={Pending.Count}");
            return started;
        }

        /// <summary>Forgets every queued recording. For tests only - production never empties the
        /// queue except by running what is in it.</summary>
        internal static void Reset()
        {
            Pending.Clear();
            Runner = dir => PostRecording.Run(dir, null, AccountState.IsSignedIn);
            Dispatcher = work => ThreadPool.QueueUserWorkItem(_ => work());
        }

        /// <summary>
        /// A claim was released. If that directory is one we are waiting on, retry it now - this is
        /// the prompt path, and the whole reason a title repair no longer costs a recording its
        /// packaging.
        /// </summary>
        private static void OnClaimReleased(string key)
        {
            if (Pending.IsEmpty) return;
            if (!Pending.TryGetValue(key, out var job) || job.GaveUp) return;
            if (job.Running) return;   // a drainer already has it; a second drain would only refuse
            DispatchDrain($"{key} was released");
        }

        /// <summary>Hands a drain to the dispatcher. An entry point onto somebody else's thread, so
        /// a dispatcher that throws is reported here and never escapes into their finally.</summary>
        private static void DispatchDrain(string why)
        {
            Log.Info($"[PostRecordingQueue] DispatchDrain: {why}");
            try
            {
                Dispatcher(() =>
                {
                    // The drained work runs on a pool thread with nothing above it: an escaping
                    // exception would be an unhandled exception on that thread.
                    try { Drain(); }
                    catch (Exception ex) { Log.Error("[PostRecordingQueue] Drain FAILED", ex); }
                });
            }
            catch (Exception ex)
            {
                Log.Error($"[PostRecordingQueue] DispatchDrain FAILED: {why}", ex);
            }
        }
    }
}
