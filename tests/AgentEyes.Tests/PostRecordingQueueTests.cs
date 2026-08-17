using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #154, criteria 1 and 2: a PARTIAL claim must not cancel the FULL post-recording
    /// sequence.
    ///
    /// The defect these pin down. <see cref="PostRecording.Run"/> returned permanently the moment any
    /// claim was on the directory, on the assumption that the other owner was running the same full
    /// sequence. The repair passes claim a recording for only a title or only a thumbnail, so a
    /// repair scan that reached a just-finished recording first cancelled its ENTIRE pipeline: no
    /// mux, no thumbnail, no transcript, no title, and no retry - the intent was dropped on the
    /// floor.
    ///
    /// Everything here runs on injected stages, so no ffmpeg, no network and no wallet are involved,
    /// and the queue's dispatcher is replaced so the retry is deterministic rather than a thread-pool
    /// race. Every test restores the production steps and empties the queue.
    ///
    /// ONE MORE THING EVERY TEST HERE MUST STATE FOR ITSELF (issue #182): whether the queued retry is
    /// SIGNED IN. The production runner asks the machine - see
    /// <see cref="PostRecordingQueue.Runner"/> - so a test that leaves it alone silently takes the
    /// DevThrottle credential in %LOCALAPPDATA% as a hidden input. Use <see cref="UseQueuedRetry"/>.
    /// To run this file the way the release runner sees it, hide the credential from the test host:
    ///
    ///   USERPROFILE=&lt;a path that does not exist&gt; dotnet vstest ...\AgentEyes.Tests.dll
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class PostRecordingQueueTests : IDisposable
    {
        private readonly string _root;

        public PostRecordingQueueTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-queue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            PostRecordingQueue.Reset();
        }

        public void Dispose()
        {
            PostRecordingQueue.Reset();
            PostRecording.RestoreDefaultSteps();
            PostRecording.AfterPackaging = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>A stopped audio recording with its media on disk and nothing else - no thumbnail,
        /// no transcript. The shape every stop path produces.</summary>
        private string MakeRecording(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "audio.wav"), "not really a wav");
            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "audio",
                Label = "audio",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                AudioFile = "audio.wav",
                DurationSeconds = 12.0,
            });
            return dir;
        }

        /// <summary>Stages that succeed the way the real ones do - by leaving their artifact on
        /// disk, which is what the resume rule reads.</summary>
        private static void InjectSucceedingStages(List<string> ran)
        {
            PostRecording.ThumbnailStep = dir =>
            {
                ran.Add(PostStage.Thumbnail);
                File.WriteAllText(Path.Combine(dir, "thumb.png"), "poster");
            };
            PostRecording.PackageStep = dir =>
            {
                ran.Add(PostStage.Package);
                File.WriteAllText(Path.Combine(dir, "transcript.json"), "[]");
            };
        }

        /// <summary>
        /// The queued retry, with the sign-in question answered by the TEST instead of by whatever
        /// DevThrottle credential happens to sit in %LOCALAPPDATA% on the machine running it.
        ///
        /// Issue #182 - the reason this helper exists. The production default runner is
        /// <c>dir =&gt; PostRecording.Run(dir, null, AccountState.IsSignedIn)</c>, which is correct for
        /// production (a retry landing after a sign-out must not spend a transcription attempt on a
        /// certainty) and is pinned by
        /// <see cref="TheQueuedRetryAsksWhetherItIsSignedIn_InTheCompiledCode"/>. But a TEST that
        /// leaves that default in place inherits the machine's sign-in state as an unstated input:
        /// signed in, <see cref="PostRecording"/> runs the packaging stage; signed out, it
        /// deliberately leaves packaging outstanding and only the thumbnail runs. Four tests here
        /// asserted the two-stage sequence while leaving it to the machine, so they passed on every
        /// developer, QA and review-gate machine (all signed in) and failed on the GitHub release
        /// runner (no credential) with
        /// <c>Expected ["thumbnail", "package"], Actual ["thumbnail"]</c> - failing every release
        /// build from v1.4.1 onward, so nothing published after v1.4.0.
        ///
        /// Sign-in is an INPUT to these scenarios. It is stated here, in both directions.
        /// </summary>
        private static void UseQueuedRetry(bool signedIn) =>
            PostRecordingQueue.Runner = dir => PostRecording.Run(dir, null, hostedWorkAllowed: signedIn);

        // ---- criterion 1: a partial claim must not drop the full sequence ----

        [Fact]
        public void Run_HeldByAPartialStageClaim_IsQueuedInsteadOfDropped()
        {
            // THE defect: a title repair holding the recording used to make this return for good.
            string dir = MakeRecording("2026-08-12_100000_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            PostRecordingQueue.Dispatcher = _ => { };   // observe the queue; no automatic retry yet

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                PostRecording.Run(dir);

                Assert.Empty(ran);                              // it could not run - correct
                Assert.True(PostRecordingQueue.IsQueued(dir),   // ...but it was NOT dropped
                    "a full pipeline refused by a title-only claim must be kept, not abandoned");
                Assert.True(PostRecordingPlan.HasUnfinishedWork(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void Run_HeldByACaptureSession_IsQueuedInsteadOfDropped()
        {
            // A capture claim covers none of the post-recording sequence either.
            string dir = MakeRecording("2026-08-12_100100_audio");
            PostRecordingQueue.Dispatcher = _ => { };

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Capture, "capture session", out _));
            try
            {
                PostRecording.Run(dir);

                Assert.True(PostRecordingQueue.IsQueued(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void Run_HeldByAnotherFullPipeline_IsNotQueued_BecauseThatOwnerRunsEveryStage()
        {
            // The other half of the distinction, and the reason this is not "queue everything": a
            // full-pipeline owner does every stage this call would, so queuing a second one would
            // just run the whole sequence twice over the same recording.
            string dir = MakeRecording("2026-08-12_100200_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            PostRecordingQueue.Dispatcher = _ => { };

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "post-recording", out _));
            try
            {
                PostRecording.Run(dir);

                Assert.Empty(ran);
                Assert.False(PostRecordingQueue.IsQueued(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        // ---- criterion 2: the queued sequence is retried and the recording finishes ----

        [Fact]
        public void Run_QueuedBehindAPartialClaim_RunsWhenItIsReleased_AndTheRecordingIsFullyProcessed()
        {
            // The whole criterion in one test: refused -> queued -> the partial owner finishes ->
            // the full sequence runs -> nothing is outstanding for that recording any more.
            string dir = MakeRecording("2026-08-12_100300_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            UseQueuedRetry(signedIn: true);                   // this scenario is a signed-in retry (#182)
            PostRecordingQueue.Dispatcher = work => work();   // the release IS the retry, on this thread

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            PostRecording.Run(dir);
            Assert.Empty(ran);
            Assert.True(PostRecordingQueue.IsQueued(dir));

            RecordingWorkset.ReleaseForTests(dir);   // the title repair finishes

            Assert.Equal(new[] { PostStage.Thumbnail, PostStage.Package }, ran);
            Assert.False(PostRecordingQueue.IsQueued(dir), "a sequence that ran must leave the queue");
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir), "the recording must end up fully processed");
            Assert.False(RecordingWorkset.IsClaimed(dir), "the retry must release its own claim");

            var manifest = Manifest.Load(dir);
            Assert.Equal(PostStageState.Done, manifest.PostProcessing[PostStage.Thumbnail].State);
            Assert.Equal(PostStageState.Done, manifest.PostProcessing[PostStage.Package].State);
        }

        [Fact]
        public void Drain_RunsTheQueuedSequence_WhenNothingHoldsTheRecordingAnyMore()
        {
            // The backstop path: RepairService drains at the end of every pass, so a queued sequence
            // still lands even if the release-triggered retry never fired.
            string dir = MakeRecording("2026-08-12_100400_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            UseQueuedRetry(signedIn: true);             // this scenario is a signed-in retry (#182)
            PostRecordingQueue.Dispatcher = _ => { };   // deliberately deaf to the release

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "thumbnail repair", out _));
            PostRecording.Run(dir);
            RecordingWorkset.ReleaseForTests(dir);
            Assert.True(PostRecordingQueue.IsQueued(dir), "nothing retried it yet - that is what Drain is for");

            int started = PostRecordingQueue.Drain();

            Assert.Equal(1, started);
            Assert.Equal(new[] { PostStage.Thumbnail, PostStage.Package }, ran);
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void Drain_ASecondDrainerArrivesWhileTheSequenceRuns_ItIsProcessedOnceAndNothingIsRequeued()
        {
            // Two threads can drain at once, and both can see the same free directory. The claim
            // decides it: the winner holds a FULL PIPELINE claim, so the second caller takes the
            // "another full pipeline is doing every stage" branch and returns - it must NOT re-queue
            // the recording, or a finished recording would be run again.
            string dir = MakeRecording("2026-08-12_101000_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            PostRecording.PackageStep = d =>
            {
                ran.Add(PostStage.Package);
                File.WriteAllText(Path.Combine(d, "transcript.json"), "[]");
                PostRecording.Run(d);   // the second drainer, while this sequence holds the claim
            };
            UseQueuedRetry(signedIn: true);   // this scenario is a signed-in retry (#182)
            PostRecordingQueue.Dispatcher = _ => { };

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            PostRecording.Run(dir);
            RecordingWorkset.ReleaseForTests(dir);

            PostRecordingQueue.Drain();

            Assert.Equal(new[] { PostStage.Thumbnail, PostStage.Package }, ran);
            Assert.False(PostRecordingQueue.IsQueued(dir), "the second caller must not re-queue a recording being processed");
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void Drain_TwoDrainersHoldTheSameSnapshot_TheRecordingIsRunOnce()
        {
            // THE interleaving the test above cannot reach, and the one an independent reviewer
            // reproduced: the second drainer arrives while the first is still at the RUNNER boundary,
            // BEFORE it has taken the full-pipeline claim. Nothing had reserved the job at that
            // point - Drain enumerated a snapshot, checked the workset and called the runner, and the
            // entry was removed only later by NoteStarted - so both drainers ran it. The delayed one
            // then found a free directory (the winner had already finished and released) and
            // packaged the recording a SECOND time: two ffmpeg passes, two transcriptions, two
            // Completed events.
            //
            // Without the reservation in Drain this fails with two runner invocations and four
            // stages. The Runner here is the REAL PostRecording.Run, so the claim, NoteStarted and
            // the release all behave as they do in production.
            string dir = MakeRecording("2026-08-12_101500_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            PostRecordingQueue.Dispatcher = _ => { };

            using var firstRunnerEntered = new ManualResetEventSlim(false);
            using var letTheFirstRunnerGo = new ManualResetEventSlim(false);
            int runnerCalls = 0;
            bool firstRunnerWasReleased = false;
            PostRecordingQueue.Runner = d =>
            {
                if (Interlocked.Increment(ref runnerCalls) == 1)
                {
                    firstRunnerEntered.Set();
                    firstRunnerWasReleased = letTheFirstRunnerGo.Wait(TimeSpan.FromSeconds(20));
                }
                PostRecording.Run(d, null, hostedWorkAllowed: true);
            };

            int completed = 0;
            void CountCompletion(string d)
            {
                if (string.Equals(RecordingWorkset.Key(d), RecordingWorkset.Key(dir), StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref completed);
            }

            PostRecording.Completed += CountCompletion;
            try
            {
                PostRecordingQueue.Enqueue(dir, "test");

                var firstDrainer = Task.Run(() => PostRecordingQueue.Drain());
                Assert.True(firstRunnerEntered.Wait(TimeSpan.FromSeconds(20)), "the first drainer never reached its runner");

                int startedBySecond = PostRecordingQueue.Drain();   // same job, stale snapshot

                letTheFirstRunnerGo.Set();
                Assert.True(firstDrainer.Wait(TimeSpan.FromSeconds(20)), "the first drainer never finished");

                Assert.True(firstRunnerWasReleased);
                Assert.Equal(0, startedBySecond);
                Assert.Equal(1, runnerCalls);
                Assert.Equal(1, firstDrainer.Result);
                Assert.Equal(new[] { PostStage.Thumbnail, PostStage.Package }, ran);
                Assert.Equal(1, completed);
                Assert.False(PostRecordingQueue.IsQueued(dir));
                Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
            }
            finally { PostRecording.Completed -= CountCompletion; }
        }

        [Fact]
        public void Drain_AReservedRecordingTheRunnerNeitherStartedNorRequeued_GoesBackInTheQueue()
        {
            // The other side of the reservation: it must not be a one-way door. A runner that does
            // nothing at all (it was refused by something the queue cannot see, or it threw) would
            // otherwise leave the job reserved forever and no later drain would ever look at it.
            string dir = MakeRecording("2026-08-12_101600_audio");
            PostRecordingQueue.Dispatcher = _ => { };
            int runs = 0;
            PostRecordingQueue.Runner = _ => runs++;

            PostRecordingQueue.Enqueue(dir, "test");
            Assert.Equal(1, PostRecordingQueue.Drain());
            Assert.True(PostRecordingQueue.IsQueued(dir), "a job the runner did nothing with must stay queued");

            Assert.Equal(1, PostRecordingQueue.Drain());   // ...and it is drainable again
            Assert.Equal(2, runs);
        }

        [Fact]
        public void Drain_RecordingStillClaimed_LeavesItQueuedAndRunsNothing()
        {
            string dir = MakeRecording("2026-08-12_100500_audio");
            int runs = 0;
            PostRecordingQueue.Dispatcher = _ => { };
            PostRecordingQueue.Runner = _ => runs++;

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                PostRecording.Run(dir);

                Assert.Equal(0, PostRecordingQueue.Drain());
                Assert.Equal(0, runs);
                Assert.True(PostRecordingQueue.IsQueued(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        // ---- the queue's own rules -------------------------------------------

        [Fact]
        public void Enqueue_SpellingVariantsOfOneDirectory_AreOneQueuedRecording()
        {
            string dir = MakeRecording("2026-08-12_100600_audio");
            PostRecordingQueue.Dispatcher = _ => { };

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                PostRecordingQueue.Enqueue(dir, "test");
                PostRecordingQueue.Enqueue(dir + Path.DirectorySeparatorChar, "test");

                Assert.Equal(1, PostRecordingQueue.Count);
                Assert.True(PostRecordingQueue.IsQueued(dir + Path.DirectorySeparatorChar));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void Enqueue_MoreThanMaxAttempts_GivesUpAndLeavesItToTheRecoveryPass()
        {
            // The queue is the PROMPT path, not the last line of defence: an unbounded retry between
            // two owners that keep refusing each other would spin. The recording is still unfinished
            // on disk, so PostRecordingPlan.FindUnfinished keeps finding it.
            string dir = MakeRecording("2026-08-12_100700_audio");
            PostRecordingQueue.Dispatcher = _ => { };

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                for (int i = 0; i < PostRecordingQueue.MaxAttempts; i++) PostRecordingQueue.Enqueue(dir, "test");
                Assert.True(PostRecordingQueue.IsQueued(dir));

                PostRecordingQueue.Enqueue(dir, "test");   // one too many

                Assert.False(PostRecordingQueue.IsQueued(dir));
                Assert.True(PostRecordingPlan.HasUnfinishedWork(dir),
                    "giving up on the prompt retry must leave the recording visible to the recovery pass");
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void NoteStarted_TakesTheRecordingOutOfTheQueue()
        {
            string dir = MakeRecording("2026-08-12_100800_audio");
            PostRecordingQueue.Dispatcher = _ => { };

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                PostRecordingQueue.Enqueue(dir, "test");
                Assert.True(PostRecordingQueue.IsQueued(dir));

                PostRecordingQueue.NoteStarted(dir);

                Assert.False(PostRecordingQueue.IsQueued(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void Enqueue_TheClaimIsAlreadyGone_RetriesImmediately()
        {
            // The race the release event alone cannot cover: the claim is dropped between the refusal
            // and the enqueue, so no release announcement is coming for it.
            string dir = MakeRecording("2026-08-12_100900_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            UseQueuedRetry(signedIn: true);   // this scenario is a signed-in retry (#182)
            PostRecordingQueue.Dispatcher = work => work();

            PostRecordingQueue.Enqueue(dir, "the claim was released a moment ago");

            Assert.Equal(new[] { PostStage.Thumbnail, PostStage.Package }, ran);
            Assert.False(PostRecordingQueue.IsQueued(dir));
        }

        [Fact]
        public void Enqueue_AfterGivingUp_StaysGivenUp_SoTheBoundActuallyBounds()
        {
            // The give-up must be STICKY. If the entry were simply deleted, the attempt count would
            // go with it and the next refusal would start again at attempt 1 - the bound would bound
            // nothing and two owners that keep refusing each other could cycle forever.
            string dir = MakeRecording("2026-08-12_101100_audio");
            int runs = 0;
            PostRecordingQueue.Dispatcher = _ => { };
            PostRecordingQueue.Runner = _ => runs++;

            for (int i = 0; i <= PostRecordingQueue.MaxAttempts; i++) PostRecordingQueue.Enqueue(dir, "test");
            Assert.False(PostRecordingQueue.IsQueued(dir));

            PostRecordingQueue.Enqueue(dir, "test");   // and again, long after

            Assert.False(PostRecordingQueue.IsQueued(dir));
            Assert.Equal(0, PostRecordingQueue.Count);
            Assert.Equal(0, PostRecordingQueue.Drain());
            Assert.Equal(0, runs);
        }

        [Fact]
        public void NoteStarted_ClearsAGiveUp_SoAProcessedRecordingLeavesNoTombstone()
        {
            string dir = MakeRecording("2026-08-12_101200_audio");
            PostRecordingQueue.Dispatcher = _ => { };
            for (int i = 0; i <= PostRecordingQueue.MaxAttempts; i++) PostRecordingQueue.Enqueue(dir, "test");

            PostRecordingQueue.NoteStarted(dir);
            PostRecordingQueue.Enqueue(dir, "test");

            Assert.True(PostRecordingQueue.IsQueued(dir), "a recording that has since been processed starts over clean");
        }

        // ---- the drain yields to capture, like everything else costly ----------

        [Fact]
        public void Drain_ACaptureIsInProgress_LeavesEverythingQueued()
        {
            // A queued sequence is a deferred mux, a poster ffmpeg and a transcription upload. The
            // release-triggered retry runs on whatever thread let a claim go and has no IsRecording
            // delegate, so the capture claim itself is what it reads.
            string dir = MakeRecording("2026-08-12_101300_audio");
            string capturing = Path.Combine(_root, "2026-08-12_101301_audio");
            Directory.CreateDirectory(capturing);
            int runs = 0;
            PostRecordingQueue.Dispatcher = _ => { };
            PostRecordingQueue.Runner = _ => runs++;

            PostRecordingQueue.Enqueue(dir, "test");
            Assert.True(RecordingWorkset.TryClaim(capturing, RecordingWorkKind.Capture, "capture session", out _));
            try
            {
                Assert.Equal(0, PostRecordingQueue.Drain());
                Assert.Equal(0, runs);
                Assert.True(PostRecordingQueue.IsQueued(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(capturing); }

            // ...and the capture ending is itself the trigger that brings the queue back.
            Assert.Equal(1, PostRecordingQueue.Drain());
            Assert.Equal(1, runs);
        }

        [Fact]
        public void Drain_NoCapture_Runs()
        {
            // The control for the test above: without it, "nothing ran" could be a drain that never
            // works at all.
            string dir = MakeRecording("2026-08-12_101400_audio");
            int runs = 0;
            PostRecordingQueue.Dispatcher = _ => { };
            PostRecordingQueue.Runner = _ => runs++;

            PostRecordingQueue.Enqueue(dir, "test");

            Assert.Equal(1, PostRecordingQueue.Drain());
            Assert.Equal(1, runs);
        }

        // ---- a retry that lands after a sign-out must not spend an attempt -----

        [Fact]
        public void Run_HostedWorkNotAllowed_LeavesPackagingOutstandingInsteadOfSpendingAnAttempt()
        {
            // A stop runs seconds after the recording; a QUEUED retry can land hours later and after
            // a sign-out. Attempting the packaging stage signed out fails on a certainty and spends
            // one of the recording's three transcription attempts to do it.
            string dir = MakeRecording("2026-08-12_101500_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);

            PostRecording.Run(dir, null, hostedWorkAllowed: false);

            Assert.Equal(new[] { PostStage.Thumbnail }, ran);   // the local stage still runs
            Assert.True(PostRecordingPlan.NeedsPackage(dir), "packaging must stay outstanding for a pass that can succeed");
        }

        [Fact]
        public void Drain_TheQueuedRetryIsSignedOut_RunsTheLocalStagesAndLeavesPackagingOutstanding()
        {
            // The signed-out half of the queue's behavior, asserted DELIBERATELY (issue #182).
            //
            // Until now this direction was only ever exercised by accident, by whichever machine
            // happened to have no DevThrottle credential - which meant the GitHub runner, where it
            // showed up as four FAILURES rather than as coverage, and nowhere else. The rule itself is
            // real and worth pinning: a queued retry can land hours after the stop and after a
            // sign-out, and attempting the packaging stage then fails on a certainty while spending
            // one of the recording's three transcription attempts to do it.
            //
            // So: the thumbnail still lands (a Library poster costs nothing hosted), packaging does
            // NOT run, and it stays outstanding for a pass that can actually succeed. The mirror image
            // of Drain_RunsTheQueuedSequence_WhenNothingHoldsTheRecordingAnyMore, which is the same
            // scenario signed IN.
            string dir = MakeRecording("2026-08-17_120000_audio");
            var ran = new List<string>();
            InjectSucceedingStages(ran);
            UseQueuedRetry(signedIn: false);
            PostRecordingQueue.Dispatcher = _ => { };

            PostRecordingQueue.Enqueue(dir, "test");
            Assert.Equal(1, PostRecordingQueue.Drain());

            Assert.Equal(new[] { PostStage.Thumbnail }, ran);
            Assert.True(PostRecordingPlan.NeedsPackage(dir),
                "packaging must stay outstanding for a pass that can actually succeed");
            Assert.True(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void TheQueuedRetryAsksWhetherItIsSignedIn_InTheCompiledCode()
        {
            // The wiring half of the test above, read from IL rather than source text: the queue's
            // default runner must consult AccountState rather than hard-coding hostedWorkAllowed.
            //
            // The PRODUCTION default is the static field initializer, which the compiler puts in
            // .cctor - and no behavioral test in this class ever exercises it, because every test
            // starts with Reset(). So .cctor is named explicitly here; asserting merely "somewhere in
            // this class" would be satisfied by Reset alone and would let the production default
            // regress unnoticed.
            string where = CompiledCode.Describe(CompiledCode.CallSites(CompiledCode.CoreAssembly,
                callee => callee == "AgentEyes.DevThrottle.AccountState::get_IsSignedIn"));

            Assert.Contains("AgentEyes.PostRecordingQueue::.cctor", where, StringComparison.Ordinal);
            Assert.Contains("AgentEyes.PostRecordingQueue::Reset", where, StringComparison.Ordinal);
        }

        [Fact]
        public void Enqueue_NoDirectory_Throws()
        {
            Assert.Throws<ArgumentException>(() => PostRecordingQueue.Enqueue("", "test"));
        }
    }
}
