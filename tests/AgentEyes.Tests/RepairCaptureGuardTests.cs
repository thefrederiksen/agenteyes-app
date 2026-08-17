using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #154, criterion 4: the repair passes must not run ffmpeg or a hosted call during
    /// capture.
    ///
    /// The defect. <c>RepairService.RunAsync</c> read <c>IsRecording</c> ONCE, before it took its
    /// gate, and then ran the whole pass - a title call and an ffmpeg thumbnail per recording - for
    /// as long as it lasted. A recording that started right after that read did not stop any of it:
    /// the guard was a check-then-act, so the exclusion the source comment promised did not exist.
    /// Nothing on the recording-start side told the pass to stand down either.
    ///
    /// How these tests can FAIL, which is the point of them: each costly step is an injected delegate
    /// that COUNTS its invocations, and the loops here are the real ones over real (empty) recording
    /// directories. Delete the guard and the counts go from 0 to 2. Each yielding case is paired with
    /// a control that runs the same loop with no capture and asserts the steps DID run, so a count of
    /// zero can never come from a loop that simply did nothing.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class RepairCaptureGuardTests : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _dirs = new();

        /// <summary>The recording a capture session is writing into - a DIFFERENT directory from the
        /// ones the repair loops walk, because the capture guard is about the machine, not about one
        /// recording: repair must stand down while ANY capture is live.</summary>
        private readonly string _capturing;

        public RepairCaptureGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            for (int i = 0; i < 2; i++)
            {
                string dir = Path.Combine(_root, "2026-08-12_11000" + i + "_audio");
                Directory.CreateDirectory(dir);
                _dirs.Add(dir);
            }
            _capturing = Path.Combine(_root, "2026-08-12_110099_video");
            Directory.CreateDirectory(_capturing);
        }

        public void Dispose()
        {
            RepairService.RestoreDefaultSteps();
            // The capture claim is process-wide and CaptureInProgress is a global read: leaving one
            // behind would make every later test in this collection yield.
            RecordingWorkset.ReleaseForTests(_capturing);
            foreach (string dir in _dirs) RecordingWorkset.ReleaseForTests(dir);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>
        /// The exact interleaving QA measured (480 ms wide in the running app): a capture session has
        /// claimed its directory and bumped the epoch, its writers are still coming up, so
        /// <c>RecordingService._state</c> is not "recording" yet - and the repair pass reads the
        /// epoch right there, AFTER the bump. Both of the guard's original signals say "no capture".
        /// This is <c>RecordingService.BeginSession</c>'s real ordering, replayed.
        /// </summary>
        private int StartACaptureWhoseWritersAreStillComingUp()
        {
            Assert.True(RecordingWorkset.TryClaim(_capturing, RecordingWorkKind.Capture, "capture session", out _));
            CaptureSignal.CaptureStarted();
            return CaptureSignal.Epoch;   // what a pass entering the gate in this window reads
        }

        /// <summary>A service that is never "recording" by the live flag, so only the epoch can stop
        /// it - which is exactly the check-then-act case.</summary>
        private static RepairService NotRecording() => new(() => false);

        // ---- the hosted title call --------------------------------------------

        [Fact]
        public async Task TitleAsync_NoCapture_NamesEveryRecording()
        {
            // The control. Without it, "the step did not run" below could be a loop that never ran.
            using var service = NotRecording();
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; return Task.FromResult(true); };

            await service.TitleAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, calls);
            Assert.All(_dirs, d => Assert.False(RecordingWorkset.IsClaimed(d)));
        }

        [Fact]
        public async Task TitleAsync_ARecordingStartedAfterTheGuardRead_MakesNoHostedCall()
        {
            // THE check-then-act. The pass read "not recording", a capture started, and the live flag
            // alone cannot see it any more once that capture has ended - the epoch can.
            using var service = NotRecording();
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; return Task.FromResult(true); };

            int epoch = CaptureSignal.Epoch;      // what the pass read when it decided to run
            CaptureSignal.CaptureStarted();       // ...and a recording started immediately after

            await service.TitleAsync(_dirs, epoch);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task TitleAsync_RecordingRightNow_MakesNoHostedCall()
        {
            using var service = new RepairService(() => true);
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; return Task.FromResult(true); };

            await service.TitleAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task TitleAsync_ACaptureStartsPartWayThrough_StopsAtThatRecording()
        {
            // The guard is per recording, not per pass: the backlog can be long, and the recording
            // that is being captured must not wait for all of it.
            using var service = NotRecording();
            var named = new List<string>();
            int epoch = CaptureSignal.Epoch;
            RepairService.TitleStep = dir =>
            {
                named.Add(dir);
                CaptureSignal.CaptureStarted();   // the user hits record while the first one names
                return Task.FromResult(true);
            };

            await service.TitleAsync(_dirs, epoch);

            Assert.Equal(new[] { _dirs[0] }, named);
        }

        // ---- the thumbnail ffmpeg run -----------------------------------------

        [Fact]
        public async Task ThumbsAsync_NoCapture_GeneratesEveryThumbnail()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };

            await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, calls);
            Assert.All(_dirs, d => Assert.False(RecordingWorkset.IsClaimed(d)));
        }

        [Fact]
        public async Task ThumbsAsync_ARecordingStartedAfterTheGuardRead_RunsNoFfmpeg()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };

            int epoch = CaptureSignal.Epoch;
            CaptureSignal.CaptureStarted();

            await service.ThumbsAsync(_dirs, epoch);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ThumbsAsync_RecordingRightNow_RunsNoFfmpeg()
        {
            using var service = new RepairService(() => true);
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };

            await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(0, calls);
        }

        // ---- the resume pass (the deferred mux and the transcription upload) ---

        [Fact]
        public async Task ResumeAsync_NoCapture_ResumesEveryRecording()
        {
            using var service = NotRecording();
            var resumed = new List<string>();
            RepairService.ResumeStep = (dir, _) => { resumed.Add(dir); return new PostRecordingOutcome(dir); };

            await service.ResumeAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(_dirs, resumed);
        }

        [Fact]
        public async Task ResumeAsync_ARecordingStartedAfterTheGuardRead_ResumesNothing()
        {
            using var service = NotRecording();
            var resumed = new List<string>();
            RepairService.ResumeStep = (dir, _) => { resumed.Add(dir); return new PostRecordingOutcome(dir); };

            int epoch = CaptureSignal.Epoch;
            CaptureSignal.CaptureStarted();

            await service.ResumeAsync(_dirs, epoch);

            Assert.Empty(resumed);
        }

        // ---- the capture-start window (issue #154, QA round 1) -----------------
        //
        // The residual check-then-act QA found and measured. BeginSession claims the directory and
        // bumps the epoch BEFORE any writer starts, but _state becomes "recording" only after every
        // writer is up - 480 ms later for a real video recording (ffmpeg gdigrab spawn + WASAPI
        // loopback init). A pass that took its epoch inside that window saw an unchanged epoch AND an
        // idle flag, and ran the whole pass alongside a capture that had already started.
        //
        // Without RecordingWorkset.CaptureInProgress in CaptureYielded, every one of these fails with
        // the step count at 2 - which is what QA observed: two thumbnail ffmpeg runs and two hosted
        // title calls during a live capture. Their controls are the NoCapture_ cases above, which run
        // the same loops over the same directories and assert the steps DO run twice.

        [Fact]
        public async Task TitleAsync_ACaptureIsBringingItsWritersUp_MakesNoHostedCall()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; return Task.FromResult(true); };

            int epoch = StartACaptureWhoseWritersAreStillComingUp();

            await service.TitleAsync(_dirs, epoch);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ThumbsAsync_ACaptureIsBringingItsWritersUp_RunsNoFfmpeg()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };

            int epoch = StartACaptureWhoseWritersAreStillComingUp();

            await service.ThumbsAsync(_dirs, epoch);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ResumeAsync_ACaptureIsBringingItsWritersUp_ResumesNothing()
        {
            // The most expensive pass of the three: resuming runs the deferred mux and the
            // transcription upload, and a stage started in this window is not interrupted when
            // _state finally flips - it runs for the whole capture.
            using var service = NotRecording();
            var resumed = new List<string>();
            RepairService.ResumeStep = (dir, _) => { resumed.Add(dir); return new PostRecordingOutcome(dir); };

            int epoch = StartACaptureWhoseWritersAreStillComingUp();

            await service.ResumeAsync(_dirs, epoch);

            Assert.Empty(resumed);
        }

        [Fact]
        public async Task ThumbsAsync_TheCaptureEndedAndReleasedItsClaim_RepairsAgain()
        {
            // The OPPOSITE failure the new signal could introduce: a guard that reads a claim yields
            // for as long as that claim exists, so a claim that outlived its capture would stop
            // repair forever. It does not - the claim is released at stop, and the next pass (with
            // its own epoch, exactly as RunAsync takes one) repairs normally.
            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };

            StartACaptureWhoseWritersAreStillComingUp();
            RecordingWorkset.ReleaseForTests(_capturing);          // RecordingService.Stop's finally

            await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ThumbsAsync_AFailedStartRolledTheCaptureBack_RepairsAgain()
        {
            // The same question for the path where a capture never gets going: issue #155's
            // RecordingStartSequence rolls a failed start back, and the rollback is what releases the
            // claim. If it did not, this guard would have turned one failed recording into a
            // permanently silent repair service. Driven through the REAL start sequence, with the
            // real Discard, rather than by releasing the claim by hand.
            string dir = Path.Combine(_root, "2026-08-12_110098_video");
            Directory.CreateDirectory(dir);

            RecordingClaimTicket claim = default;
            Assert.Throws<InvalidOperationException>(() => RecordingStartSequence.Run(
                dir,
                publish: () =>
                {
                    Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Capture, "capture session", out claim));
                    CaptureSignal.CaptureStarted();
                },
                steps: new[] { new RecordingStartStep("video", () => throw new InvalidOperationException("ffmpeg would not start")) },
                startedWriters: () => Array.Empty<RecordingStopStep>(),
                releaseSession: () => RecordingStartSequence.Discard(dir, claim)));

            Assert.False(RecordingWorkset.CaptureInProgress, "the rollback must release the capture claim");

            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };

            await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ThumbsAsync_ARepairStageClaimIsHeldElsewhere_StillGeneratesThumbnails()
        {
            // The other way the new signal could go wrong: yielding to work that is not a capture at
            // all. Only a Capture-kind claim counts - a repair pass must not stand down for another
            // repair's stage claim, least of all for the Stage claims its own loops take.
            Assert.True(RecordingWorkset.TryClaim(_capturing, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                using var service = NotRecording();
                int calls = 0;
                RepairService.ThumbStep = _ => { calls++; return true; };

                await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

                Assert.Equal(2, calls);
            }
            finally
            {
                RecordingWorkset.ReleaseForTests(_capturing);
            }
        }

        // ---- the signal itself, and its one production caller ------------------

        [Fact]
        public void CaptureStarted_ChangesTheEpoch_SoAPassCanSeeACaptureItNeverOverlapped()
        {
            int before = CaptureSignal.Epoch;
            Assert.False(CaptureSignal.ChangedSince(before));

            CaptureSignal.CaptureStarted();

            Assert.True(CaptureSignal.ChangedSince(before));
        }

        [Fact]
        public void StartingACaptureAnnouncesItself_InTheCompiledCode()
        {
            // Read from the IL, not the source text: this is a WIRING fact ("recording start
            // interacts with the repair gate"), and the source-text form of this guard has been
            // defeated before (issue #155). It proves the call exists in BeginSession - not that the
            // guard downstream behaves, which is what the tests above are for.
            var sites = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                callee => callee == "AgentEyes.CaptureSignal::CaptureStarted");

            Assert.Contains(sites, s => s.Method.Contains("RecordingService::BeginSession", StringComparison.Ordinal));
        }

        [Fact]
        public void StartingACaptureClaimsItBeforeItAnnouncesIt_InTheCompiledCode()
        {
            // The ordering half of the fix, and the half no behavioral test can reach: a test writes
            // the interleaving itself, so it can only show what the guard does with a given order,
            // never which order BeginSession actually uses.
            //
            // Claim first, bump second, and the three signals in CaptureYielded leave no instant
            // uncovered. Announce first - the shape this PR shipped in round 1 - and the reverse
            // window opens: the epoch already counts a capture that has claimed nothing, so a pass
            // reading the epoch there sees an unchanged epoch, an idle flag AND no claim. That window
            // is not theoretical; CaptureStarted writes a log line to disk before it returns.
            //
            // From the IL, like the assertion above, because this is a wiring fact and the
            // source-text form of exactly this kind of guard has been defeated before (issue #155).
            var calls = CompiledCode.CallsIn(CompiledCode.CoreAssembly, "RecordingService::BeginSession").ToList();

            int claim = calls.IndexOf("AgentEyes.RecordingWorkset::TryClaim");
            int announce = calls.IndexOf("AgentEyes.CaptureSignal::CaptureStarted");

            Assert.True(claim >= 0, "BeginSession must claim the recording directory for the capture");
            Assert.True(announce >= 0, "BeginSession must announce the capture to the repair gate");
            Assert.True(claim < announce,
                "BeginSession must take the capture claim BEFORE it bumps the capture epoch, or a repair "
                + "pass reading the epoch between the two sees no capture at all. Order was: "
                + string.Join(" -> ", calls));
        }

        [Fact]
        public void ReadingTheCallOrderOfAMethodThatIsNotThere_Throws_SoTheOrderingCheckCannotPassByReadingNothing()
        {
            // The instrument above is only worth anything while it fails closed: if BeginSession were
            // renamed, or the scan pointed at the wrong assembly, a helper that returned an empty
            // list would make the ordering assertion fail loudly - but one that returned an empty
            // list AND an assertion written as "no call is out of order" would pass forever. This
            // pins the fail-closed half.
            var missing = Assert.Throws<InvalidOperationException>(
                () => CompiledCode.CallsIn(CompiledCode.CoreAssembly, "RecordingService::NoSuchMethodExists"));
            Assert.Contains("No method matching", missing.Message, StringComparison.Ordinal);

            // And a name that matches SEVERAL bodies is refused too - order across separate method
            // definitions is not order at all.
            var several = Assert.Throws<InvalidOperationException>(
                () => CompiledCode.CallsIn(CompiledCode.CoreAssembly, "RecordingService::"));
            Assert.Contains("method definitions", several.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TitleAsync_YieldsPartWayThrough_StillTellsTheLibraryAboutTheTitlesItWrote()
        {
            // Yielding is not a reason to hide work already done: the first recording's title is on
            // disk, and an open Library would otherwise not show it until some other trigger
            // happened to refresh the view.
            using var service = NotRecording();
            int libraryChanged = 0;
            service.LibraryChanged = () => libraryChanged++;

            int epoch = CaptureSignal.Epoch;
            RepairService.TitleStep = _ =>
            {
                CaptureSignal.CaptureStarted();   // the user hits record right after the first title
                return Task.FromResult(true);
            };

            await service.TitleAsync(_dirs, epoch);

            Assert.Equal(1, libraryChanged);
        }

        [Fact]
        public void TheQueueDrainIsGuardedToo_ItRunsAfterTheGateButStillYieldsToCapture()
        {
            // The loops return from THEMSELVES when they yield, and control still reaches the drain
            // at the end of RunAsync - so without a check there a pass that had just correctly stood
            // down would start a full mux anyway. Order matters, so this asserts position, not
            // mere presence. (PostRecordingQueue.Drain has its own capture guard for the
            // release-triggered path; this is the one that makes the pass's own decision stick.)
            string runAsync = RepoSource.MethodBody(
                RepoSource.Read(@"src\AgentEyes.Core\RepairService.cs"),
                "public async Task RunAsync(string trigger)");

            int gateExit = runAsync.IndexOf("_gate.Exit();", StringComparison.Ordinal);
            int drain = runAsync.IndexOf("PostRecordingQueue.Drain()", StringComparison.Ordinal);
            Assert.True(gateExit > 0, "RunAsync must release the gate");
            Assert.True(drain > gateExit, "RunAsync must drain the queue after the gate is released");

            // The guard must live in the window BETWEEN the gate release and the drain. Looking
            // anywhere earlier would be satisfied by the check at the top of the pass, which the
            // yielding loops have already walked past by the time control arrives here.
            int guard = runAsync.IndexOf("CaptureYielded(", gateExit, StringComparison.Ordinal);
            Assert.True(guard > 0 && guard < drain,
                "the queue drain that runs after the gate must itself be preceded by a capture check");
        }

        [Fact]
        public void TheCostlyRepairStagesAreGuarded_EveryLoopAsksBeforeItSpends()
        {
            // A structural check on top of the behavioral ones: each of the three loops must consult
            // the capture guard, so a fourth loop added later cannot quietly skip it.
            string repair = RepoSource.Read(@"src\AgentEyes.Core\RepairService.cs");
            foreach (string method in new[]
                     {
                         "internal async Task ResumeAsync(IReadOnlyList<string> unfinished, int captureEpoch)",
                         "internal async Task TitleAsync(IReadOnlyList<string> untitled, int captureEpoch)",
                         "internal async Task ThumbsAsync(IReadOnlyList<string> missing, int captureEpoch)",
                     })
            {
                Assert.Contains("CaptureYielded(", RepoSource.MethodBody(repair, method), StringComparison.Ordinal);
            }
        }
    }
}
