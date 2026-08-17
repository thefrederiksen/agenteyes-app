using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AgentEyes
{
    /// <summary>
    /// WHAT kind of work holds a claim on a recording directory (issue #154).
    ///
    /// The kind is not decoration - it is the difference between "someone else is already doing the
    /// whole job, so there is nothing for me to do" and "someone else is doing ONE stage, so my job
    /// still has to happen". Before this existed every claim looked the same, so a title-only repair
    /// claim made the FULL post-recording sequence give up permanently and the recording was never
    /// packaged at all.
    /// </summary>
    internal enum RecordingWorkKind
    {
        /// <summary>A live capture session is writing into the directory (issue #155). Nothing else
        /// may touch it, and no post-recording work can be done until the capture stops.</summary>
        Capture,

        /// <summary>The whole post-recording sequence - mux, thumbnail, package, plugins. A second
        /// full pipeline is genuinely redundant: the owner does every stage this caller would.</summary>
        FullPipeline,

        /// <summary>ONE stage only - a title repair, a thumbnail repair, a walkthrough rebuild. It
        /// does NOT cover the rest of the sequence, so a full pipeline refused by one of these must
        /// be retried rather than dropped.</summary>
        Stage,
    }

    /// <summary>Who holds a claim: what kind of work it is, a human label for the log, and the
    /// IDENTITY of the claimant (issue #154, round 3).
    ///
    /// The identity is what makes a release ownership-specific. Releasing by directory alone removes
    /// whichever claim happens to be there, so a caller whose own claim was REFUSED could tear down
    /// the claim of the owner that refused it - which is exactly what
    /// <c>RecordingService.BeginSession</c> did when a directory name collided.</summary>
    internal readonly struct RecordingClaim
    {
        public RecordingClaim(RecordingWorkKind kind, string what, long id)
        {
            Kind = kind;
            What = string.IsNullOrWhiteSpace(what) ? kind.ToString() : what;
            Id = id;
        }

        /// <summary>Whether the owner covers the full post-recording sequence or one stage.</summary>
        public RecordingWorkKind Kind { get; }

        /// <summary>What the owner is doing ("capture session", "title repair"), for the log.</summary>
        public string What { get; }

        /// <summary>Process-unique identity of THIS claim. Never reused, never 0 for a live claim.</summary>
        public long Id { get; }

        public override string ToString() => $"{Kind}/{What}#{Id}";
    }

    /// <summary>
    /// Proof that a caller owns a claim, and the ONLY thing that can release it (issue #154).
    ///
    /// A ticket is handed out by <see cref="RecordingWorkset.TryClaim"/> and it is worthless unless
    /// the claim it names is still the one on the directory: <see cref="RecordingWorkset.Release"/>
    /// removes the claim only when the identity still matches. A caller that was REFUSED holds a
    /// default (<see cref="Held"/> = false) ticket, and releasing that does nothing at all - which is
    /// the whole point. Releasing by directory name could not tell those two apart.
    /// </summary>
    internal readonly struct RecordingClaimTicket
    {
        internal RecordingClaimTicket(string key, long id)
        {
            Key = key;
            Id = id;
        }

        /// <summary>The NORMALIZED directory key this claim is on ("" when nothing is held).</summary>
        public string Key { get; }

        /// <summary>Identity of the claim, or 0 when the caller does not own one.</summary>
        public long Id { get; }

        /// <summary>True when the caller actually owns a claim and must release it when done.</summary>
        public bool Held => Id != 0;

        public override string ToString() => Held ? $"{Key}#{Id}" : "(no claim)";
    }

    /// <summary>What happened when a repair step asked to be let onto the machine (issue #154).</summary>
    internal enum RepairStepAdmission
    {
        /// <summary>The step owns the directory and may run. It MUST call
        /// <see cref="RecordingWorkset.EndStep"/> when it is finished.</summary>
        Admitted,

        /// <summary>A capture is in progress. The whole pass must stand down, not just this
        /// recording - the guard is about the machine, not about one directory.</summary>
        CaptureYielded,

        /// <summary>Somebody else owns this recording. Skip it and carry on with the next one.</summary>
        DirectoryBusy,
    }

    /// <summary>
    /// An admitted repair step: the coordination decision that let it onto the machine, plus the
    /// directory claim it took (if any).
    ///
    /// It is a two-phase ticket on purpose (issue #154, round 3). Phase one is
    /// <see cref="RecordingWorkset.TryAdmitStep"/>; phase two is
    /// <see cref="RecordingWorkset.TryRunStep{T}"/>, which is the instant the step BEGINS and is the
    /// same critical section a capture takes to publish its claim. See the class comment on
    /// <see cref="RecordingWorkset"/> for why the two phases exist and what the pair does and does
    /// not guarantee.
    /// </summary>
    internal readonly struct RepairStepTicket
    {
        internal RepairStepTicket(long id, string what, RecordingClaimTicket claim)
        {
            Id = id;
            What = what;
            Claim = claim;
        }

        /// <summary>Identity of this admission, or 0 when the step was not admitted.</summary>
        public long Id { get; }

        /// <summary>What the step is ("title repair"), for the log.</summary>
        public string What { get; }

        /// <summary>The directory claim the admission took. Not held for a pass that claims the
        /// directory further down (the recovery pass claims inside
        /// <see cref="PostRecording.Resume"/>).</summary>
        public RecordingClaimTicket Claim { get; }

        /// <summary>True when this ticket came from an admission that succeeded.</summary>
        public bool Admitted => Id != 0;

        public override string ToString() => Admitted ? $"{What}#{Id} {Claim}" : "(not admitted)";
    }

    /// <summary>
    /// The recordings that currently have work in flight (issue #142), WHAT that work is
    /// (issue #154), WHO owns it (issue #154 round 3), and the one place where a repair step and a
    /// capture start are ordered against each other.
    ///
    /// A recording directory is "claimed" while something is writing to it - the capture session
    /// itself (issue #155: the recording has a manifest from the moment it starts, so every scan can
    /// see a directory that is still being captured into), the live stop pass
    /// (mux -> thumbnail -> package), an API-driven stop, a walkthrough rebuild, a title repair. Any
    /// automatic pass that scans the recordings root must leave a claimed directory alone: two
    /// writers on one recording is a race over its files, and before issue #155 also a
    /// load-mutate-save race that silently lost whichever manifest field the loser wrote.
    ///
    /// This used to be a plain HashSet living inside MainWindow, which meant the guard existed only
    /// for the UI code path - the REST API stop path claimed nothing at all, and the thumbnail
    /// repair pass could not see the set. Process-wide and thread-safe, so every path shares one
    /// answer to "is someone already working on this recording?".
    ///
    /// FOUR defects issue #154 fixed here, all of which made the exclusion a lie:
    ///
    ///  - EVERY CLAIM LOOKED THE SAME. A caller refused a claim only knew "someone", so
    ///    <see cref="PostRecording.Run"/> assumed the owner was running the same full sequence and
    ///    returned for good. The repair passes claim a directory for only a title
    ///    (<see cref="RepairService.BackfillMissingTitlesAsync"/>) or only a thumbnail
    ///    (<see cref="RepairService.BackfillMissingThumbsAsync"/>), so a repair claim landing first
    ///    on a just-finished recording cancelled its packaging outright. Claims now carry a
    ///    <see cref="RecordingWorkKind"/>, and <see cref="OwnerKind"/> lets a refused caller decide
    ///    between "already covered" and "still mine to do, later".
    ///
    ///  - THE KEY WAS THE RAW STRING. <c>C:\x\y</c>, <c>C:\x\y\</c> and a relative path resolving to
    ///    the same directory took three independent claims, so the mutual exclusion silently did not
    ///    apply between a CLI relative path and an app absolute path. Every entry point now goes
    ///    through <see cref="Key"/> (<see cref="Path.GetFullPath(string)"/> plus trailing-separator
    ///    normalization), so one directory is one claim however it is spelled. What that does and
    ///    does NOT cover is stated on <see cref="Key"/> itself.
    ///
    ///  - RELEASE WAS BY DIRECTORY NAME, so it removed whichever claim was there rather than the
    ///    caller's own. A capture whose claim had been REFUSED still ran its unconditional release at
    ///    stop and tore down the claim of the pipeline that refused it. A claim is now released
    ///    through the <see cref="RecordingClaimTicket"/> the successful claim handed out, and a
    ///    refused caller holds a ticket that releases nothing.
    ///
    ///  - THE REPAIR GUARD WAS CHECK-THEN-ACT. A repair pass read "no capture", then took its stage
    ///    claim, then invoked ffmpeg or a hosted call - and a capture starting between those steps
    ///    was not seen at all. Moving the read closer to the work only shortened the window. The
    ///    admission of a repair step and the publication of a capture claim now contend on ONE
    ///    monitor (<c>Gate</c>), so they are ORDERED rather than racing (see below).
    ///
    /// HOW THE STEP-VERSUS-CAPTURE ORDER WORKS, and its one honest limit.
    ///
    /// Every mutation of the claim table - a capture claiming, a repair step being admitted, a step
    /// beginning - happens inside <c>Gate</c>. That gives the two events a single, total order:
    ///
    ///  - a capture claim published BEFORE a step's begin transition -> the step sees it inside the
    ///    same critical section and does not run at all;
    ///  - a step that has begun BEFORE the capture claim is published -> the step runs, and the
    ///    capture starts anyway. Capture NEVER waits for repair: the recorder must start when the
    ///    user presses record, and a thumbnail ffmpeg run can take minutes.
    ///
    /// So the invariant is: no repair step BEGINS after a capture has announced itself. The second
    /// case above - a step that was already running when the capture started - is the disclosed,
    /// deliberate limit; the repair passes yield at their next stage boundary, they do not kill work
    /// in flight. The residual physical gap is one instruction: <see cref="TryRunStep{T}"/> invokes
    /// the step delegate IMMEDIATELY after the transition, with no other call in between, and
    /// <c>RepairStepAdmissionTests</c> pins that from the compiled IL. Holding <c>Gate</c> across the
    /// delegate itself would close even that, at the price of blocking the user's recording behind
    /// an ffmpeg run, which is not a trade this product can make.
    ///
    /// LOGGING POLICY HERE, stated because it is a deliberate exception to the repository rule that
    /// every public method logs entry and exit (independent review, non-blocking 3). Everything that
    /// CHANGES state - a claim, a refusal, a release, an admission, a begin, an end - logs, because
    /// those are the events that explain a dropped pipeline or a repair pass that stood down, and
    /// issue #154 exists because they were invisible. The READ-ONLY observers
    /// (<see cref="IsClaimed"/>, <see cref="OwnerKind"/>, <see cref="OwnerDescription"/>,
    /// <see cref="CaptureInProgress"/>, <see cref="RunningSteps"/>) deliberately do NOT log: they are
    /// called once per directory by scans that walk the whole recordings root, several times a
    /// minute, and logging them would bury the state changes above under thousands of lines saying
    /// that nothing happened. A read that FAILS still logs (<see cref="TryKey"/>).
    /// </summary>
    internal static class RecordingWorkset
    {
        private static readonly ConcurrentDictionary<string, RecordingClaim> Claimed =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The one coordination monitor. EVERY mutation of <see cref="Claimed"/> is made under it,
        /// and so is every repair-step admission and begin, so "a capture claimed" and "a repair step
        /// began" are two events in one order instead of two racing reads.
        ///
        /// Never held across capture work, repair work, or a caller-supplied delegate - only across
        /// a dictionary operation and the log line that reports it. Lock order in the app is
        /// <c>RecordingService._lock</c> -> this; nothing here ever takes a service lock, so there is
        /// no path back.
        /// </summary>
        private static readonly object Gate = new();

        /// <summary>Hands out claim and admission identities. Never reused within the process, so a
        /// stale ticket can never match a later claim on the same directory.</summary>
        private static long _nextId;

        /// <summary>Identities of the repair steps that have BEGUN and have not yet ended. Guarded by
        /// <c>Gate</c>. Diagnostics only - it is what lets a capture start say "N repair step(s) were
        /// already running when I started", which is the disclosed overlap rather than a guard
        /// failure. A set rather than a counter so a step that was admitted and then refused at its
        /// begin transition cannot decrement a count it never incremented.</summary>
        private static readonly HashSet<long> RunningStepIds = new();

        /// <summary>
        /// TEST SEAM. Invoked by <see cref="TryRunStep{T}"/> at the exact instant BETWEEN a step's
        /// admission and its begin transition, so a regression test can insert a capture claim into
        /// that window deterministically instead of racing threads for it.
        ///
        /// Production never sets this - a test asserts that no product assembly assigns it - and it
        /// is invoked outside <c>Gate</c>, so what a test does here goes through the same claim path
        /// a real capture uses.
        /// </summary>
        internal static Action? BeforeStepBegins;

        /// <summary>
        /// Raised with the NORMALIZED key each time a claim is released, so work that had to stand
        /// down because a directory was busy can be retried the moment it is free
        /// (<see cref="PostRecordingQueue"/>) instead of waiting for the next timer tick.
        ///
        /// Raised on whichever thread released the claim - typically inside the releasing caller's
        /// finally block - so a subscriber must not do real work on it. Subscriber exceptions are
        /// isolated: a release must never fail because something listening to it threw. Raised
        /// OUTSIDE <c>Gate</c>: a subscriber that claims something must not deadlock or re-enter the
        /// coordination monitor.
        /// </summary>
        public static event Action<string>? Released;

        /// <summary>
        /// The canonical key for a recording directory: the full path with any trailing separator
        /// removed. This is what makes <c>C:\x\y</c>, <c>C:\x\y\</c> and <c>.\y</c> (from a process
        /// whose working directory is <c>C:\x</c>) ONE claim.
        ///
        /// Throws for a null/blank or malformed path rather than inventing a key for it: a claim
        /// silently taken under a key nobody else computes is exactly the failure this method exists
        /// to remove.
        ///
        /// WHAT THIS DOES NOT COVER, stated because a normalizer nobody can see the edge of is a
        /// normalizer nobody can review. This is LEXICAL identity plus case-insensitive comparison,
        /// not filesystem identity. It folds together: case variants, trailing separators, forward
        /// versus backward separators, dot segments, and relative paths. It does NOT fold together
        /// two spellings that only the filesystem knows are the same object -
        /// <c>PROGRA~1</c> versus <c>Program Files</c> (8.3 short names), a junction or symlink and
        /// its target, or a mapped drive (<c>Z:\rec</c>) and the UNC share behind it
        /// (<c>\\server\share\rec</c>). Those remain DIFFERENT keys and would take independent
        /// claims. That is accepted and out of scope for issue #154: every production caller derives
        /// its directory from <see cref="RecordingPaths.Root"/> or from a scan of it, so all of them
        /// spell it the same way, and resolving true filesystem identity means opening a handle per
        /// directory (<c>GetFileInformationByHandle</c>) on a path that a scan walks hundreds of
        /// times. <c>RecordingWorksetTests</c> pins both halves - the spellings that ARE folded, and
        /// this limit.
        /// </summary>
        public static string Key(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                throw new ArgumentException("a recording directory is required", nameof(dir));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        }

        /// <summary>
        /// <see cref="Key"/> for the READ-ONLY paths, which must not throw.
        ///
        /// <see cref="IsClaimed"/> and the owner readers are called once per directory by scans that
        /// walk the whole recordings root, and by UI code deciding whether a card is busy. A path
        /// they cannot key is not claimed - that IS the truth for an unusable path - and reporting it
        /// as an exception would turn a Library refresh into a crash.
        ///
        /// The catch names the exceptions <see cref="Path.GetFullPath(string)"/> can raise for a path
        /// this app can hold rather than catching everything: the repository forbids a catch-all that
        /// turns an unknown failure into a normal answer, and a catch-all here would swallow (for
        /// example) an <c>OutOfMemoryException</c> and report "not claimed". <see cref="TryClaim"/>
        /// deliberately does NOT use this: a claim that cannot be keyed must fail, not silently
        /// succeed at nothing. <see cref="Release"/> no longer needs it at all - a ticket carries the
        /// key that was already computed when the claim was taken.
        /// </summary>
        private static bool TryKey(string dir, out string key)
        {
            key = "";
            try
            {
                key = Key(dir);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException
                                          or NotSupportedException or IOException
                                          or System.Security.SecurityException)
            {
                Log.Error($"[RecordingWorkset] TryKey: '{dir}' is not a usable recording path", ex);
                return false;
            }
        }

        /// <summary>
        /// Claims <paramref name="dir"/> for the caller. Returns true when the caller now owns it -
        /// and <paramref name="ticket"/> is the ONLY thing that can release it - or false when
        /// someone else already owns it and the caller must leave the recording alone, or, when the
        /// caller is a full pipeline refused by a mere <see cref="RecordingWorkKind.Stage"/>, come
        /// back for it (<see cref="PostRecordingQueue"/>).
        ///
        /// A refused caller gets a ticket that holds nothing, so the failure path of a caller that
        /// did not get the claim cannot remove the claim of the owner that refused it (issue #154,
        /// round 3 - <c>RecordingService.BeginSession</c> did exactly that).
        ///
        /// Every refusal is logged with the normalized key, the requester and the OWNER's kind
        /// (issue #154 AC5): a dropped pipeline used to be invisible in the log, which is why this
        /// class of failure went unnoticed.
        /// </summary>
        /// <param name="dir">The recording directory. Normalized by <see cref="Key"/>.</param>
        /// <param name="kind">Whether the caller covers the full sequence or one stage.</param>
        /// <param name="what">What the caller is doing - it names the owner in every log line.</param>
        /// <param name="ticket">The caller's proof of ownership; pass it to <see cref="Release"/>.</param>
        public static bool TryClaim(string dir, RecordingWorkKind kind, string what, out RecordingClaimTicket ticket)
        {
            ticket = default;
            if (string.IsNullOrWhiteSpace(dir)) return false;
            string key = Key(dir);

            RecordingClaim claim;
            bool granted;
            string owner;
            int running;
            lock (Gate)
            {
                claim = new RecordingClaim(kind, what, Interlocked.Increment(ref _nextId));
                granted = Claimed.TryAdd(key, claim);
                if (granted) ticket = new RecordingClaimTicket(key, claim.Id);
                // Read under the same lock as the failed add, so the owner named in the log line is
                // the one that actually refused this caller.
                owner = granted ? "" : Claimed.TryGetValue(key, out var held) ? held.ToString() : "(gone)";
                running = RunningStepIds.Count;
            }

            if (!granted)
            {
                Log.Info($"[RecordingWorkset] TryClaim REFUSED: key={key} requestedBy={claim} owner={owner}");
                return false;
            }

            Log.Info($"[RecordingWorkset] TryClaim: {key} claimed by {claim}"
                + (kind == RecordingWorkKind.Capture && running > 0
                    ? $" - NOTE: {running} repair step(s) were already running when this capture started; "
                      + "they began before it and are not interrupted (see RecordingWorkset's class comment)"
                    : ""));
            return true;
        }

        /// <summary>
        /// Releases the claim <paramref name="ticket"/> names, and announces it on
        /// <see cref="Released"/>.
        ///
        /// Ownership-specific: the claim is removed only while it is still the SAME claim this
        /// ticket was issued for. A ticket that holds nothing (a caller that was refused, or a
        /// finally block that never claimed) releases nothing - it does not fall back to "remove
        /// whatever is on that directory", which is how a failed capture start used to tear down
        /// another owner's claim.
        ///
        /// Safe to call twice: the second call finds a claim that is gone (or a different one) and
        /// says so.
        /// </summary>
        public static void Release(in RecordingClaimTicket ticket)
        {
            if (!ticket.Held) return;

            string key = ticket.Key;
            bool removed;
            string held;
            lock (Gate)
            {
                if (Claimed.TryGetValue(key, out var current) && current.Id == ticket.Id)
                {
                    Claimed.TryRemove(key, out _);
                    removed = true;
                    held = current.ToString();
                }
                else
                {
                    removed = false;
                    held = current.Id == 0 ? "(nothing)" : current.ToString();
                }
            }

            if (removed) Log.Info($"[RecordingWorkset] Release: {key} released by {held}");
            else Log.Info($"[RecordingWorkset] Release: {key} is no longer claim #{ticket.Id} "
                    + $"(it holds {held}) - nothing was released");

            // Outside the lock: a subscriber retries queued work, which claims directories.
            if (removed) NotifyReleased(key);
        }

        /// <summary>
        /// TEST ONLY: drop whatever claim is on <paramref name="dir"/>, whoever owns it.
        ///
        /// Test fixtures take claims by hand to stand in for a capture or a repair stage, and their
        /// Dispose has to be able to clean up a claim it may or may not still hold - a leaked capture
        /// claim would make every later test in the collection yield. Production has no legitimate
        /// use for it, and <c>RepairStepAdmissionTests</c> asserts from the compiled IL that no
        /// product assembly calls it, so this cannot quietly become the ownership hole that
        /// <see cref="Release"/> was.
        /// </summary>
        internal static void ReleaseForTests(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !TryKey(dir, out string key)) return;

            bool removed;
            lock (Gate) removed = Claimed.TryRemove(key, out _);
            if (removed) NotifyReleased(key);
        }

        /// <summary>True while any path holds a claim on <paramref name="dir"/>.</summary>
        public static bool IsClaimed(string dir) =>
            !string.IsNullOrWhiteSpace(dir) && TryKey(dir, out string key) && Claimed.ContainsKey(key);

        /// <summary>
        /// True while a CAPTURE SESSION holds any recording (issue #154).
        ///
        /// The capture claim is taken in <c>RecordingService.BeginSession</c> before the first writer
        /// starts and released in <c>Stop</c>'s finally (and by the start rollback), so it is already
        /// the app's most reliable statement of "a recording is in progress" - and unlike a separate
        /// flag it cannot drift out of step with the claim set that everything else here reads. The
        /// queued-retry drain uses it to stay off the machine while the user is recording, on the
        /// paths that have no <c>IsRecording</c> delegate of their own.
        ///
        /// A lock-free read, deliberately: it is a SAMPLE, and callers that need the answer to be
        /// atomic with their own next action must go through <see cref="TryAdmitStep"/> /
        /// <see cref="TryRunStep{T}"/>, which take the same monitor a capture claim does.
        /// </summary>
        public static bool CaptureInProgress => AnyCaptureHeld();

        /// <summary>Repair steps that have begun and not yet ended. Diagnostics.</summary>
        public static int RunningSteps { get { lock (Gate) return RunningStepIds.Count; } }

        private static bool AnyCaptureHeld()
        {
            foreach (var claim in Claimed.Values)
                if (claim.Kind == RecordingWorkKind.Capture) return true;
            return false;
        }

        /// <summary>
        /// What kind of work owns <paramref name="dir"/> right now, or null when nothing does.
        ///
        /// This is the question a refused caller has to be able to ask: a
        /// <see cref="RecordingWorkKind.FullPipeline"/> owner is doing everything this caller would,
        /// while a <see cref="RecordingWorkKind.Stage"/> or <see cref="RecordingWorkKind.Capture"/>
        /// owner is not - and a null means the claim went away in the meantime, which is a reason to
        /// try again immediately.
        /// </summary>
        public static RecordingWorkKind? OwnerKind(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !TryKey(dir, out string key)) return null;
            return Claimed.TryGetValue(key, out var held) ? held.Kind : null;
        }

        /// <summary>Who owns <paramref name="dir"/> right now, for logging. Null when nothing does.</summary>
        public static string? OwnerDescription(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !TryKey(dir, out string key)) return null;
            return Claimed.TryGetValue(key, out var held) ? held.ToString() : null;
        }

        // ---- repair-step admission (issue #154, criterion 4) -------------------

        /// <summary>
        /// Phase ONE of letting a repair step onto the machine: no capture may be in progress, and
        /// the recording must be free. Takes a <see cref="RecordingWorkKind.Stage"/> claim on
        /// <paramref name="dir"/> when it succeeds.
        ///
        /// The capture test and the claim happen inside the SAME critical section a capture takes to
        /// publish its own claim, so a capture cannot slip between them. Phase two
        /// (<see cref="TryRunStep{T}"/>) is what actually starts the step; the admission is not
        /// permission to run later, it is permission to ask to begin now.
        ///
        /// The caller's own guard (the epoch and the live recording flag - see
        /// <c>RepairService.CaptureYielded</c>) still runs BEFORE this, and it is deliberately not
        /// passed in here: those two are conservative extra signals, while the capture CLAIM is the
        /// decisive one and is the only one that can be read atomically with a capture start.
        /// </summary>
        public static RepairStepAdmission TryAdmitStep(string dir, string what, out RepairStepTicket step)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (string.IsNullOrWhiteSpace(what)) throw new ArgumentException("a step must say what it is", nameof(what));

            step = default;
            string key = Key(dir);

            RepairStepAdmission outcome;
            string owner = "";
            lock (Gate)
            {
                if (AnyCaptureHeld())
                {
                    outcome = RepairStepAdmission.CaptureYielded;
                }
                else
                {
                    var claim = new RecordingClaim(RecordingWorkKind.Stage, what, Interlocked.Increment(ref _nextId));
                    if (Claimed.TryAdd(key, claim))
                    {
                        step = new RepairStepTicket(Interlocked.Increment(ref _nextId), what,
                            new RecordingClaimTicket(key, claim.Id));
                        outcome = RepairStepAdmission.Admitted;
                    }
                    else
                    {
                        outcome = RepairStepAdmission.DirectoryBusy;
                        owner = Claimed.TryGetValue(key, out var held) ? held.ToString() : "(gone)";
                    }
                }
            }

            switch (outcome)
            {
                case RepairStepAdmission.Admitted:
                    Log.Info($"[RecordingWorkset] TryAdmitStep: {key} admitted for {step}");
                    break;
                case RepairStepAdmission.CaptureYielded:
                    Log.Info($"[RecordingWorkset] TryAdmitStep: {key} NOT admitted for '{what}' - a capture is in progress");
                    break;
                default:
                    Log.Info($"[RecordingWorkset] TryAdmitStep: {key} NOT admitted for '{what}' - owned by {owner}");
                    break;
            }
            return outcome;
        }

        /// <summary>
        /// Phase ONE for a pass that must claim the directory FURTHER DOWN rather than here: the
        /// recovery pass takes its own <see cref="RecordingWorkKind.FullPipeline"/> claim inside
        /// <see cref="PostRecording.Resume"/>, so an admission that claimed the directory first would
        /// refuse the very work it is admitting.
        ///
        /// It still takes part in the capture ordering, which is the point: the resume step is the
        /// most expensive work this app does automatically (a deferred mux plus a transcription
        /// upload).
        /// </summary>
        public static RepairStepAdmission TryAdmitPass(string what, out RepairStepTicket step)
        {
            if (string.IsNullOrWhiteSpace(what)) throw new ArgumentException("a step must say what it is", nameof(what));

            step = default;
            bool capture;
            lock (Gate)
            {
                capture = AnyCaptureHeld();
                if (!capture) step = new RepairStepTicket(Interlocked.Increment(ref _nextId), what, default);
            }

            if (capture)
            {
                Log.Info($"[RecordingWorkset] TryAdmitPass: '{what}' NOT admitted - a capture is in progress");
                return RepairStepAdmission.CaptureYielded;
            }

            Log.Info($"[RecordingWorkset] TryAdmitPass: admitted for {step}");
            return RepairStepAdmission.Admitted;
        }

        /// <summary>
        /// Phase TWO: BEGIN an admitted step and run it. Returns false - having run NOTHING - when a
        /// capture claimed the machine between the admission and this call.
        ///
        /// This is the linearization point of the whole guard. The transition is made inside
        /// <c>Gate</c>, the same monitor <see cref="TryClaim"/> publishes a capture claim under, so
        /// the two events have one order and a capture can never land "in between" the decision and
        /// the step. <paramref name="work"/> is invoked IMMEDIATELY after that transition with no
        /// other call in between - an ordering pinned from the compiled IL by
        /// <c>RepairStepAdmissionTests</c>, because a re-read followed by a few statements is exactly
        /// the check-then-act this replaces.
        ///
        /// The delegate is invoked OUTSIDE the monitor and is not interrupted once started: capture
        /// never waits for repair (see the class comment).
        /// </summary>
        /// <param name="step">A ticket from <see cref="TryAdmitStep"/> / <see cref="TryAdmitPass"/>.</param>
        /// <param name="work">The costly step - ffmpeg, a hosted call, the resume pipeline.</param>
        /// <param name="result">What the step returned; default when it did not run.</param>
        /// <returns>True when the step ran.</returns>
        public static bool TryRunStep<T>(in RepairStepTicket step, Func<T> work, out T? result)
        {
            if (!step.Admitted) throw new InvalidOperationException("a step that was not admitted cannot be run");
            if (work == null) throw new ArgumentNullException(nameof(work));

            result = default;
            if (!TryBegin(step)) return false;
            result = work();
            return true;
        }

        /// <summary>
        /// The transition itself: admitted -> running, refused when a capture holds the machine.
        /// Split out so that in <see cref="TryRunStep{T}"/> the step delegate is the very next call
        /// after it, with nothing - not even a log line - in between.
        /// </summary>
        private static bool TryBegin(in RepairStepTicket step)
        {
            BeforeStepBegins?.Invoke();   // test seam: the instant between admission and begin

            bool begun;
            lock (Gate)
            {
                begun = !AnyCaptureHeld();
                if (begun) RunningStepIds.Add(step.Id);
            }

            if (!begun)
                Log.Info($"[RecordingWorkset] TryBegin: {step} does NOT start - a capture claimed the "
                    + "machine after this step was admitted");
            return begun;
        }

        /// <summary>
        /// The step is over: give the directory back (if the admission claimed it) and stop counting
        /// it as running. Call it in a finally - a step that threw is just as finished as one that
        /// returned.
        /// </summary>
        public static void EndStep(in RepairStepTicket step)
        {
            if (!step.Admitted) return;

            lock (Gate) RunningStepIds.Remove(step.Id);
            Log.Info($"[RecordingWorkset] EndStep: {step} finished");
            Release(step.Claim);
        }

        /// <summary>Announces a release. Same fan-out isolation the other process-wide events in this
        /// app use: a subscriber that throws must not take down the caller's finally block.</summary>
        private static void NotifyReleased(string key)
        {
            var handlers = Released;
            if (handlers == null) return;

            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(key); }
                catch (Exception ex) { Log.Error("[RecordingWorkset] Released subscriber FAILED", ex); }
            }
        }
    }
}
