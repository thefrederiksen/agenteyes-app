using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.DevThrottle;
using AgentEyes.Packaging;

namespace AgentEyes
{
    /// <summary>
    /// Runs the automatic repair passes - missing titles (issue #138) and missing Library thumbnails
    /// (issue #141) - for as long as the app is up (issue #142).
    ///
    /// It lives at APP level, not inside the window, for two reasons that both cost a QA bounce:
    ///  - AgentEyes is normally started with <c>--tray</c>, which never constructs MainWindow. A
    ///    repair timer owned by the window therefore did not exist at all in the app's normal mode.
    ///  - Recordings are driven through the REST Control API as often as through the window, and the
    ///    API path has no window to call back into. It signals <see cref="PostRecording.Completed"/>
    ///    like every other stop path, and this service listens to that one event.
    ///
    /// Triggers: app start (<see cref="StartupDelay"/> after launch), every
    /// <see cref="RepairSchedule.Interval"/>, a sign-in, and the end of every recording's
    /// post-processing.
    ///
    /// Issue #152 gave it the pass that was missing: <see cref="ResumeUnfinishedAsync"/> finishes
    /// recordings whose post-processing never completed - a leftover pending mux, a missing
    /// thumbnail, a missing transcript. That work used to hang off <c>MainWindow.Loaded</c>, which in
    /// the app's normal --tray shape NEVER RUNS, so an interrupted recording (a crash, an update
    /// restart, one transient failure) was stranded forever. It is bounded by the same per-recording
    /// attempt ceilings the other passes use, so unattended recovery cannot spend without limit.
    /// </summary>
    internal sealed class RepairService : IDisposable
    {
        /// <summary>
        /// How long after launch the first pass runs. Long enough that the app finishes starting -
        /// window paint, preset load, sign-in - before ffmpeg and a hosted call compete with it;
        /// short enough that a library opened right after launch shows repaired cards while the user
        /// is still looking at it, and that a recording interrupted by the last shutdown is finished
        /// within seconds of the next start rather than at the next tick (issue #152).
        /// </summary>
        public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

        // ---- the per-recording work, as replaceable steps (issue #154) ------
        //
        // Same pattern as PostRecording's stages, and for the same reason: the capture guard added in
        // issue #154 sits immediately before each of these, and the only way to prove the guard
        // actually STOPS the costly work is to run the real loop with a step that records whether it
        // was invoked. Production always runs the defaults; RestoreDefaultSteps puts them back.

        /// <summary>Resume one recording's outstanding post-processing (dir, hostedWorkAllowed).</summary>
        internal static Func<string, bool, PostRecordingOutcome> ResumeStep =
            (dir, hostedWorkAllowed) => PostRecording.Resume(dir, hostedWorkAllowed);

        /// <summary>Name one untitled recording - a hosted call.</summary>
        internal static Func<string, Task<bool>> TitleStep = dir => TitleBackfill.TitleAsync(dir);

        /// <summary>Generate one missing Library thumbnail - ffmpeg. The attempt is counted BEFORE
        /// the work, so a file ffmpeg can never read drops out of the pass. Returns true when a
        /// thumbnail was produced.</summary>
        internal static Func<string, bool> ThumbStep = dir =>
        {
            Thumbnails.NoteThumbAttempt(dir);
            return Thumbnails.Ensure(dir) != null;
        };

        /// <summary>Puts the production steps back. For tests that inject an observable step.</summary>
        internal static void RestoreDefaultSteps()
        {
            ResumeStep = (dir, hostedWorkAllowed) => PostRecording.Resume(dir, hostedWorkAllowed);
            TitleStep = dir => TitleBackfill.TitleAsync(dir);
            ThumbStep = dir =>
            {
                Thumbnails.NoteThumbAttempt(dir);
                return Thumbnails.Ensure(dir) != null;
            };
        }

        private readonly Func<bool> _isRecording;
        private readonly RepairGate _gate = new();
        private Timer? _timer;
        private bool _disposed;

        /// <param name="isRecording">Reads live capture state; a pass must not start mid-recording
        /// (<see cref="RepairSchedule.ShouldRunNow"/>).</param>
        public RepairService(Func<bool> isRecording)
        {
            _isRecording = isRecording ?? throw new ArgumentNullException(nameof(isRecording));
        }

        /// <summary>
        /// The one-at-a-time gate: every pass this service runs - resume, titles, thumbnails - takes
        /// it, so a timer tick, a finished recording and a sign-in arriving together cannot put three
        /// passes on the same recordings at once. Exposed so the state is observable.
        /// </summary>
        public RepairGate Gate => _gate;

        /// <summary>Optional progress text sink (the window's status line). Raised on a background
        /// thread - the subscriber marshals for itself.</summary>
        public Action<string>? Status { get; set; }

        /// <summary>Optional notification that the library on disk changed and any open view should
        /// reload. Raised on a background thread.</summary>
        public Action? LibraryChanged { get; set; }

        /// <summary>Optional notification that the pass stopped because the DevThrottle wallet is
        /// empty, so a UI can surface it. Raised on a background thread.</summary>
        public Action? CreditsExhausted { get; set; }

        /// <summary>Arms the triggers: the post-recording signal, a sign-in, and the periodic
        /// timer.</summary>
        public void Start()
        {
            PostRecording.Completed += OnRecordingCompleted;
            AccountState.Changed += OnAccountStateChanged;
            _timer = new Timer(OnTimerTick, null, StartupDelay, RepairSchedule.Interval);
            Log.Info($"[RepairService] Start: first pass in {StartupDelay.TotalSeconds:0}s, "
                + $"then every {RepairSchedule.Interval.TotalMinutes:0} minute(s)");
        }

        /// <summary>
        /// The periodic tick. An entry point on a timer thread, so it must not let an exception
        /// escape - <see cref="RunAsync"/> already swallows and logs, and the discard keeps the
        /// timer thread free while the pass runs.
        /// </summary>
        private void OnTimerTick(object? state) => _ = RunAsync("timer");

        /// <summary>
        /// A recording finished post-processing - whichever stop path drove it. Post-processing is
        /// exactly where a title or a thumbnail goes missing (a 429 on the naming call, a poster
        /// ffmpeg read a moment too early), so repair it NOW rather than at the next restart, which
        /// on this app may be days away.
        /// </summary>
        private void OnRecordingCompleted(string dir) =>
            _ = RunAsync($"recording finished ({Path.GetFileName(dir)})");

        /// <summary>
        /// Signing back in is exactly when a title the wallet or a dead key cost you can finally be
        /// recovered. The window runs the transcription backfill on the same event, but the window
        /// is not always there - in tray mode this is the only listener.
        /// </summary>
        private void OnAccountStateChanged()
        {
            if (!AccountState.IsSignedIn) return;   // a 401 raises this too; nothing to repair
            _ = RunAsync("signed in");
        }

        /// <summary>
        /// One repair pass: finish what was left unfinished, then name what is untitled, then
        /// generate what has no thumbnail. Never throws - it is driven from timers and background
        /// completions with nothing above it to report a failure.
        /// </summary>
        public async Task RunAsync(string trigger)
        {
            try
            {
                if (!RepairSchedule.ShouldRunNow(_isRecording()))
                {
                    Log.Info($"[RepairService] RunAsync: trigger={trigger} - recording in progress; skipped");
                    return;
                }

                if (!_gate.TryEnter())
                {
                    Log.Info($"[RepairService] RunAsync: trigger={trigger} - a repair pass is already running; skipped");
                    return;
                }

                // Issue #154: the check above is a check-then-act - a recording can start in the
                // moment between it and the gate, and used to run the whole pass anyway. The epoch is
                // taken HERE, after the gate, and re-tested before every costly stage below - and
                // before the queue drain that follows the gate - so a capture that starts (or starts
                // and finishes) mid-pass stops all of it.
                int epoch = CaptureSignal.Epoch;

                try
                {
                    Log.Info($"[RepairService] RunAsync: trigger={trigger}");

                    if (CaptureYielded(epoch, $"RunAsync trigger={trigger}")) return;

                    // FIRST, because everything below it depends on a finished recording: complete
                    // any post-processing that was interrupted or that failed a stage (issue #152).
                    await ResumeUnfinishedAsync(epoch);

                    // Titling is a hosted call: signed out it cannot succeed, and running it anyway
                    // would spend this recording's title budget on a certainty. Signing in
                    // re-triggers the pass through the window's account handler.
                    if (AccountState.IsSignedIn) await BackfillMissingTitlesAsync(epoch);
                    else Log.Info("[RepairService] RunAsync: not signed in; skipping the title pass");

                    // Thumbnails are local ffmpeg work, so they repair whether signed in or not.
                    await BackfillMissingThumbsAsync(epoch);

                    Log.Info($"[RepairService] RunAsync: trigger={trigger} done");
                }
                finally
                {
                    _gate.Exit();
                }

                // Outside the gate on purpose (issue #154): the passes above release their per-stage
                // claims as they finish, and a full post-recording sequence that was refused by one
                // of those claims is waiting for exactly this. Draining inside the gate would also
                // starve the repair pass that PostRecording.Completed triggers out of it, since that
                // pass would find the gate still held. Off this thread, like every other long piece
                // of work in this service.
                //
                // Guarded like every other costly stage: the loops above return from THEMSELVES when
                // they yield to a capture, and control still arrives here - so without this check a
                // pass that had just correctly stood down would start a full mux anyway. (Drain has
                // its own capture guard for the release-triggered path; this one is what makes the
                // decision the pass already took stick.)
                if (CaptureYielded(epoch, $"RunAsync drain trigger={trigger}")) return;
                await Task.Run(() => PostRecordingQueue.Drain());
            }
            catch (Exception ex)
            {
                Log.Error($"[RepairService] RunAsync FAILED: trigger={trigger}", ex);
                Status?.Invoke("");
            }
        }

        /// <summary>
        /// True when a capture has started since <paramref name="epoch"/> was taken, or one is
        /// running right now, or one is coming up - in which case the pass must stop before its next
        /// costly stage (issue #154).
        ///
        /// THREE tests, and each covers a case the other two cannot see.
        ///
        ///  - <c>IsRecording</c> is the live session flag. It catches a capture that is fully up.
        ///  - The EPOCH catches a capture that started AND finished between two stages of this pass,
        ///    which a sample of a live flag cannot see and which is exactly when repair ffmpeg was
        ///    competing with capture.
        ///  - <see cref="RecordingWorkset.CaptureInProgress"/> catches the START of a capture, which
        ///    the first two miss TOGETHER (issue #154, QA round 1 - measured 480 ms wide in the real
        ///    app). <c>RecordingService.BeginSession</c> claims the directory and bumps the epoch
        ///    before a single writer starts, but <c>_state</c> only becomes "recording" once every
        ///    writer is up - an ffmpeg gdigrab spawn and a WASAPI loopback init later. A pass that
        ///    read the epoch inside that window read the ALREADY-BUMPED value, so
        ///    <c>ChangedSince</c> was false, and the live flag was still idle: both signals said "no
        ///    capture" while one was unambiguously starting, and the pass ran hosted title calls and
        ///    thumbnail ffmpeg straight through it.
        ///
        /// The claim is the signal that cannot drift: it exists from before the first writer until
        /// <c>Stop</c>'s finally (or the start rollback) releases it - so it covers the whole of the
        /// capture, and conservatively the synchronous stop as well, since the release is in that
        /// finally. <c>BeginSession</c> takes it BEFORE it bumps the epoch precisely so that the
        /// three tests together leave no instant uncovered; that ordering is asserted from the
        /// compiled IL.
        ///
        /// WHAT THIS IS AND IS NOT, since round 2 of this issue got it wrong. This is the pass's own
        /// conservative guard, and it is a SAMPLE: by itself it is a check-then-act, because a
        /// capture can claim the machine in the instant after it returns false. It is NOT what makes
        /// criterion 4 true. What makes criterion 4 true is that every costly step goes through
        /// <see cref="RecordingWorkset.TryAdmitStep"/> and
        /// <see cref="RecordingWorkset.TryRunStep{T}"/>, whose begin transition is taken under the
        /// same monitor a capture publishes its claim under - so a step cannot begin after a capture
        /// has announced itself. This check still earns its place ahead of that: the epoch sees a
        /// capture that started AND finished between two stages, which no claim read can, and it
        /// stops the whole remaining pass rather than one recording.
        ///
        /// TWO limits, stated because a guard nobody can see the edge of is a guard nobody can
        /// review:
        ///  - it holds while the claim is GRANTED. Since issue #154 round 3, a capture whose claim is
        ///    REFUSED does not start at all (<c>RecordingService.BeginSession</c> throws), so there is
        ///    no such thing as a live capture without a claim any more.
        ///  - a capture that starts AND fully stops between this pass's gate check and its epoch read
        ///    is invisible to all three. It is two adjacent statements wide, and a capture that is
        ///    already over has nothing left for repair to collide with.
        /// </summary>
        private bool CaptureYielded(int epoch, string where)
        {
            bool recording = _isRecording();
            bool claimed = RecordingWorkset.CaptureInProgress;
            bool started = CaptureSignal.ChangedSince(epoch);
            if (!recording && !claimed && !started) return false;

            Log.Info($"[RepairService] {where}: yielding to capture "
                + $"(recording={recording}, captureClaimHeld={claimed}, captureStartedDuringThisPass={started})");
            Status?.Invoke("");
            return true;
        }

        /// <summary>
        /// Finishes every recording whose post-recording sequence never completed (issue #152) - a
        /// leftover pending mux, a missing thumbnail, a missing transcript.
        ///
        /// This is the pass that did not exist in the app's normal shape. The transcription backfill
        /// was wired to <c>MainWindow.Loaded</c> and AgentEyes normally starts with --tray, which
        /// never constructs a window, so an interrupted recording - a crash, an update restart, one
        /// transient ffmpeg or network failure - stayed half-processed forever. Running it here means
        /// it happens 20 seconds after every launch, on every periodic tick, and on sign-in, with no
        /// window involved.
        ///
        /// Signed out, the packaging stage is left outstanding rather than attempted: it would fail
        /// on a certainty and spend one of the recording's three transcription attempts to do it.
        /// Every attempt is bounded (transcription 3, thumbnail 3, mux 3), so an unattended pass
        /// cannot spend forever on a recording that can never finish.
        ///
        /// Ungated on purpose - the caller holds <see cref="Gate"/>.
        /// </summary>
        public async Task ResumeUnfinishedAsync(int captureEpoch)
        {
            var unfinished = await Task.Run(() => PostRecordingPlan.FindUnfinished(RecordingPaths.Root));
            await ResumeAsync(unfinished, captureEpoch);
        }

        /// <summary>
        /// The resume loop over an already-scanned list. Split out from
        /// <see cref="ResumeUnfinishedAsync"/> (issue #154) so the capture guard can be exercised
        /// against a real loop over known directories, without a scan of the machine's recordings
        /// root and without ffmpeg or a hosted call.
        /// </summary>
        internal async Task ResumeAsync(IReadOnlyList<string> unfinished, int captureEpoch)
        {
            if (unfinished == null) throw new ArgumentNullException(nameof(unfinished));
            if (unfinished.Count == 0) return;

            bool hostedWorkAllowed = AccountState.IsSignedIn;
            Log.Info($"[RepairService] ResumeAsync: {unfinished.Count} recording(s) with unfinished "
                + $"post-processing (hostedWork={hostedWorkAllowed})");
            Status?.Invoke($"Finishing {unfinished.Count} recording(s)...");

            bool any = false;
            foreach (string dir in unfinished)
            {
                // Issue #154: re-tested per recording, because resuming one runs the deferred mux
                // and the transcription upload - the most expensive work this service does.
                if (CaptureYielded(captureEpoch, $"ResumeAsync before {Path.GetFileName(dir)}"))
                {
                    // Recordings already finished in this loop are on disk; an open Library must be
                    // told, or they sit unshown until some other trigger refreshes it.
                    if (any) LibraryChanged?.Invoke();
                    return;
                }

                // Sequential on purpose: predictable credit spend, one ffmpeg at a time, and no
                // several-hundred-MB uploads in flight at once.
                //
                // Issue #154 round 3: admission and the step's start are ONE ordered decision
                // against a capture start (RecordingWorkset), and BOTH happen on the pool thread
                // that runs the step. Admitting on this thread and starting the work over there
                // would put a thread hand-off inside the very window the admission closes.
                var (admission, resumed) = await Task.Run(() => AdmitAndResume(dir, hostedWorkAllowed));
                if (admission != RepairStepAdmission.Admitted)
                {
                    // A capture owns the machine. Stand down for the rest of the pass, exactly as
                    // the guard above does - and show what is already finished.
                    if (any) LibraryChanged?.Invoke();
                    Status?.Invoke("");
                    return;
                }

                var outcome = resumed!;   // Admitted means the step ran and returned its outcome
                any |= outcome.Completed.Count > 0;

                if (outcome.Error != null && DevThrottleClient.IsCreditsFailure(outcome.Error))
                {
                    // An empty wallet fails identically on every remaining recording.
                    CreditsExhausted?.Invoke();
                    Log.Info("[RepairService] ResumeAsync: out of credits - stopping");
                    break;
                }
            }

            Status?.Invoke("");
            if (any) LibraryChanged?.Invoke();
        }

        /// <summary>
        /// One recording's resume, admitted and started as one decision (issue #154 round 3). Runs
        /// on the pool thread that does the work, so nothing - not even a thread hand-off - sits
        /// between the coordination decision and the step.
        ///
        /// The admission takes NO directory claim: <see cref="PostRecording.Resume"/> takes its own
        /// full-pipeline claim, and claiming here first would refuse the very work being admitted.
        /// </summary>
        private static (RepairStepAdmission Admission, PostRecordingOutcome? Outcome) AdmitAndResume(
            string dir, bool hostedWorkAllowed)
        {
            var admission = RecordingWorkset.TryAdmitPass($"resume {Path.GetFileName(dir)}", out var step);
            if (admission != RepairStepAdmission.Admitted) return (admission, null);

            try
            {
                return RecordingWorkset.TryRunStep(step, () => ResumeStep(dir, hostedWorkAllowed), out var outcome)
                    ? (RepairStepAdmission.Admitted, outcome)
                    : (RepairStepAdmission.CaptureYielded, null);
            }
            finally
            {
                RecordingWorkset.EndStep(step);
            }
        }

        /// <summary>
        /// Names recordings that transcribed but never got a title (issue #138). Separate from the
        /// transcription pass because the two fail independently: a recording can transcribe first
        /// time and still lose its title to one stalled request.
        ///
        /// Ungated on purpose - the caller (<see cref="RunAsync"/>) holds <see cref="Gate"/>. It used
        /// to be reachable from the window's transcription backfill as well; that backfill was
        /// removed in issue #152 and RunAsync is now the only caller.
        /// </summary>
        public async Task BackfillMissingTitlesAsync(int captureEpoch)
        {
            var untitled = await Task.Run(() => TranscriptionBacklog.FindMissingTitles(RecordingPaths.Root));
            await TitleAsync(untitled, captureEpoch);
        }

        /// <summary>
        /// The title loop over an already-scanned list. Split out (issue #154) so the capture guard
        /// is testable against the real loop with no hosted call and no scan.
        /// </summary>
        internal async Task TitleAsync(IReadOnlyList<string> untitled, int captureEpoch)
        {
            if (untitled == null) throw new ArgumentNullException(nameof(untitled));
            if (untitled.Count == 0) return;

            Log.Info($"[RepairService] TitleAsync: {untitled.Count} recording(s) to name");
            Status?.Invoke($"Naming {untitled.Count} recording(s)...");

            bool any = false;
            foreach (string dir in untitled)
            {
                // Issue #154: re-tested before EVERY hosted call, not once for the whole pass. This
                // is the check the old one-time read let a recording walk straight past.
                if (CaptureYielded(captureEpoch, $"TitleAsync before {Path.GetFileName(dir)}"))
                {
                    // Titles already written in this loop are on disk; an open Library must be told,
                    // or they sit unshown until some other trigger happens to refresh it.
                    if (any) LibraryChanged?.Invoke();
                    return;
                }

                // A title repair covers ONE stage (issue #154). It must be claimed as such, or a
                // full post-recording sequence refused by it is dropped instead of retried.
                //
                // Round 3: the claim and the capture test are ONE decision (admission), and the
                // hosted call starts through TryRunStep, whose begin transition is taken under the
                // same monitor a capture publishes its claim under. Claiming and then calling was
                // still a check-then-act - a capture could claim in between and the call went out
                // during it.
                var admission = RecordingWorkset.TryAdmitStep(dir, "title repair", out var step);
                if (admission == RepairStepAdmission.CaptureYielded)
                {
                    if (any) LibraryChanged?.Invoke();
                    Status?.Invoke("");
                    return;
                }
                if (admission != RepairStepAdmission.Admitted) continue;   // someone else has it

                try
                {
                    if (!RecordingWorkset.TryRunStep(step, () => TitleStep(dir), out var naming))
                    {
                        // A capture claimed the machine after this step was admitted: NOTHING was
                        // called, and the rest of the pass stands down.
                        if (any) LibraryChanged?.Invoke();
                        Status?.Invoke("");
                        return;
                    }

                    if (await naming!) any = true;
                }
                catch (Exception ex)
                {
                    Log.Error("[RepairService] title backfill " + dir, ex);
                    if (DevThrottleClient.IsCreditsFailure(ex))
                    {
                        CreditsExhausted?.Invoke();
                        Log.Info("[RepairService] TitleAsync: out of credits - stopping");
                        Status?.Invoke("");
                        if (any) LibraryChanged?.Invoke();
                        return;
                    }
                    // Anything else is this recording's problem; the next may still name fine.
                }
                finally
                {
                    RecordingWorkset.EndStep(step);
                }
            }

            Status?.Invoke("");
            LibraryChanged?.Invoke();
        }

        /// <summary>
        /// Generates thumbnails for recordings that have none (issues #19/#141/#142). Scans the
        /// recordings root, so a recording that finished after the Library list was built - or one
        /// whose thumbnail failed while the app stayed up - is repaired too. This is the ONLY
        /// automatic thumbnail generator: every attempt is counted against
        /// <see cref="Thumbnails.MaxThumbAttempts"/>, so a file ffmpeg can never read drops out
        /// of the pass instead of being retried forever. Its ceiling is
        /// <see cref="Thumbnails.MaxThumbAttempts"/>, separate from the title and transcription
        /// budgets since issue #148, because ffmpeg costs CPU and a hosted call costs credits.
        ///
        /// Ungated on purpose - the caller holds <see cref="Gate"/>.
        /// </summary>
        public async Task BackfillMissingThumbsAsync(int captureEpoch)
        {
            var missing = await Task.Run(() => Thumbnails.FindMissing(RecordingPaths.Root));
            await ThumbsAsync(missing, captureEpoch);
        }

        /// <summary>
        /// The thumbnail loop over an already-scanned list. Split out (issue #154) so the capture
        /// guard is testable against the real loop without ever reaching ffmpeg.
        /// </summary>
        internal async Task ThumbsAsync(IReadOnlyList<string> missing, int captureEpoch)
        {
            if (missing == null) throw new ArgumentNullException(nameof(missing));
            if (missing.Count == 0) return;

            Log.Info($"[RepairService] ThumbsAsync: {missing.Count} recording(s) missing a thumbnail");
            Status?.Invoke($"Generating {missing.Count} missing thumbnail(s)...");

            bool any = await Task.Run(() =>
            {
                bool generated = false;
                // Sequential: one ffmpeg at a time, so a backlog cannot saturate the machine while
                // the user is working.
                foreach (string dir in missing)
                {
                    // Issue #154: re-tested before EVERY ffmpeg run. A thumbnail backlog is minutes
                    // of CPU, and the one-time read at the top of the pass let all of it run
                    // alongside a capture that started after it.
                    if (CaptureYielded(captureEpoch, $"ThumbsAsync before {Path.GetFileName(dir)}")) return generated;

                    // Claimed for the attempt: NoteThumbAttempt is a load-mutate-save of
                    // manifest.json, and the packaging pass writes the same file. FindMissing
                    // already skips claimed recordings; this closes the gap between that scan and
                    // the write. Claimed as a STAGE (issue #154) - it is one stage, not the whole
                    // sequence, so a full pipeline refused by it must be retried, not dropped.
                    //
                    // Round 3: the claim and the capture test are ONE decision, and ffmpeg starts
                    // through TryRunStep - claiming and then spawning ffmpeg was still a
                    // check-then-act with a capture able to land in between.
                    var admission = RecordingWorkset.TryAdmitStep(dir, "thumbnail repair", out var step);
                    if (admission == RepairStepAdmission.CaptureYielded) return generated;
                    if (admission != RepairStepAdmission.Admitted) continue;   // someone else has it

                    try
                    {
                        // A capture that claimed the machine after this step was admitted stops it
                        // before ffmpeg is spawned, and stands the rest of the pass down.
                        if (!RecordingWorkset.TryRunStep(step, () => ThumbStep(dir), out var made)) return generated;

                        if (made) generated = true;
                        else Log.Info($"[RepairService] ThumbsAsync: {Path.GetFileName(dir)} produced no thumbnail");
                    }
                    catch (Exception ex)
                    {
                        // This recording's problem; the next one may still generate fine.
                        Log.Error("[RepairService] thumb repair " + dir, ex);
                    }
                    finally
                    {
                        RecordingWorkset.EndStep(step);
                    }
                }
                return generated;
            });

            Status?.Invoke("");
            if (any) LibraryChanged?.Invoke();   // show the repaired cards
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PostRecording.Completed -= OnRecordingCompleted;
            AccountState.Changed -= OnAccountStateChanged;
            _timer?.Dispose();
            _timer = null;
            Log.Info("[RepairService] Dispose: repair triggers disarmed");
        }
    }
}
