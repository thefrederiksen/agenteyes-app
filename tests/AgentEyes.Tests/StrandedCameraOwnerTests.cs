using System;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #28, AC16 - a camera ffmpeg AgentEyes could not kill must stay REACHABLE, and its
    /// recording must stay CLAIMED.
    ///
    /// WHY THIS FILE EXISTS. Round 4 fixed the recorder to keep its process handle when a stop could
    /// not confirm ffmpeg dead, and the Review Gate rejected it anyway, correctly: "keeping a handle
    /// inside an object that immediately becomes unreachable does not keep the process recoverable."
    /// The test that was supposed to prove the lifetime guarantee proved only that its OWN local
    /// could make a third call - production had no such reference. So the guarantee is not about the
    /// recorder any more; it is about whether something in the app still HOLDS the recorder, and
    /// that is what is exercised here, through the real claim registry rather than a description of
    /// it.
    ///
    /// The camera process is a fake (<see cref="FakeStuckCamera"/>) because "an ffmpeg that survives
    /// a kill" is not a thing a test can ask a real ffmpeg to be. Everything else - the recorder, the
    /// owner, RecordingWorkset - is production code.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class StrandedCameraOwnerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dir;

        public StrandedCameraOwnerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-stranded-" + Guid.NewGuid().ToString("N"));
            _dir = Path.Combine(_root, "2026-08-28_120000_video");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            // A leaked capture claim would make every later test in this collection yield, so the
            // fixture drops whatever is on the directory whether or not it still owns it.
            RecordingWorkset.ReleaseForTests(_dir);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        /// <summary>The one ffmpeg behaviour that matters here: a process that answers nothing and
        /// survives every kill - until the test says it does not.</summary>
        private sealed class FakeStuckCamera : ICameraProcess
        {
            public const int Pid = 24512;

            private Action<string>? _onStderr;

            /// <summary>False = ffmpeg never reports the camera open, i.e. a start that FAILS -
            /// which is the shape the failed-start rollback actually sees.</summary>
            public bool ReportsOpen = true;

            public bool KillEndsIt;
            public int Kills;
            public int Disposes;

            public bool HasExited { get; private set; }
            public int ExitCode { get; private set; }
            public int? ProcessId { get; private set; }

            public void Start(Action<string> onStderrLine, Action onExited)
            {
                _onStderr = onStderrLine;
                ProcessId = Pid;
                if (!ReportsOpen) return;
                // The two headers the open probe waits for, then a progress tick, exactly as a
                // healthy ffmpeg reports them.
                _onStderr("Input #0, dshow, from 'video=HD Webcam eMeet C960':");
                _onStderr("Output #0, mp4, to 'camera.mp4':");
                _onStderr("frame=   15 fps= 30 q=28.0 size=      64KiB time=00:00:00.50 bitrate=1048.6kbits/s speed=1x");
            }

            public void SendQuit() { }

            public bool WaitForExit(int milliseconds) => HasExited;

            public bool DrainStderr(int milliseconds) => true;

            public void Kill()
            {
                Kills++;
                if (!KillEndsIt) return;
                HasExited = true;
                ExitCode = -1;
            }

            /// <summary>The process ends BY ITSELF, with nobody asking and nothing watching - the
            /// gate's RETAINED_PROCESS_DIED case. Whatever the stuck DirectShow device was waiting
            /// on lets go, ffmpeg finishes and exits, and no code in AgentEyes is running at that
            /// moment to notice.</summary>
            public void DiesOnItsOwn()
            {
                HasExited = true;
                ExitCode = 0;
            }

            public void Dispose() => Disposes++;
        }

        /// <summary>A recorder over a camera ffmpeg that has already survived the quit, the kill and
        /// the Dispose retry - i.e. exactly what a service stop hands the owner.</summary>
        private FfmpegCameraRecorder StrandedRecorder(FakeStuckCamera proc)
        {
            var rec = FfmpegCameraRecorder.CreateOver(proc, "HD Webcam eMeet C960",
                Path.Combine(_dir, "camera.mp4"), Path.Combine(_dir, "camera.mp4.ffmpeg.log"),
                TimeSpan.FromSeconds(5));
            rec.Open();
            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            rec.Dispose();
            Assert.True(rec.IsAbandoned, "this fixture only means anything while the fake ffmpeg is still alive");
            return rec;
        }

        /// <summary>A recorder whose camera stopped normally - the control every "it was retained"
        /// assertion below needs, or "retain everything" would satisfy them all.</summary>
        private FfmpegCameraRecorder CleanRecorder()
        {
            var proc = new FakeStuckCamera { KillEndsIt = true };
            var rec = FfmpegCameraRecorder.CreateOver(proc, "HD Webcam eMeet C960",
                Path.Combine(_dir, "camera.mp4"), Path.Combine(_dir, "camera.mp4.ffmpeg.log"),
                TimeSpan.FromSeconds(5));
            rec.Open();
            Assert.Throws<CameraForceKilledException>(() => rec.Stop());   // it ignored "q" but died
            rec.Dispose();
            Assert.False(rec.IsAbandoned);
            return rec;
        }

        /// <summary>
        /// A recorder whose camera FAILED TO OPEN and whose ffmpeg is confirmed gone - the ordinary
        /// AC8/AC9 failed start (a busy or absent camera; ffmpeg exits by itself). Nothing is written
        /// into the recording directory on this path, which is exactly the requirement that makes
        /// the directory safe to remove afterwards.
        /// </summary>
        private FfmpegCameraRecorder FailedOpenRecorder()
        {
            var proc = new FakeStuckCamera { ReportsOpen = false, KillEndsIt = true };
            var rec = FfmpegCameraRecorder.CreateOver(proc, "HD Webcam eMeet C960",
                Path.Combine(_dir, "camera.mp4"), Path.Combine(_dir, "camera.mp4.ffmpeg.log"),
                TimeSpan.FromSeconds(0.2));
            Assert.Throws<UsageException>(() => rec.Open());
            Assert.False(rec.IsAbandoned);
            return rec;
        }

        private RecordingClaimTicket Claim()
        {
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "capture session", out var ticket),
                "the fixture could not take the capture claim it is about to make assertions about");
            return ticket;
        }

        // ---- the stop path ------------------------------------------------------

        [Fact]
        public void ReleaseClaimUnlessStranded_WhenTheCameraIsStillRunning_KeepsTheClaimAndTheRecorder()
        {
            // AC16'S TWO CLAUSES, TOGETHER. The recorder is retained, so the live ffmpeg is still
            // reachable from inside the app; and the recording's claim is NOT released, because a
            // process AgentEyes could not kill is still writing camera.mp4 into that directory and
            // releasing the claim would publish it to every automatic repair, packaging and
            // transcription pass in the app.
            //
            // Bad results this fires on: the claim is gone (the stop was treated as clean), or
            // nothing is held (the only handle to a live process was dropped).
            var owner = new StrandedCameraOwner();
            var proc = new FakeStuckCamera();
            var rec = StrandedRecorder(proc);
            var claim = Claim();

            bool retained = owner.ReleaseClaimUnlessStranded(rec, claim, _dir);

            Assert.True(retained);
            Assert.True(owner.HoldsAny);
            Assert.True(RecordingWorkset.IsClaimed(_dir),
                "the recording claim was released while a live ffmpeg was still writing into that directory");
            Assert.Equal(RecordingWorkKind.Capture, RecordingWorkset.OwnerKind(_dir));
        }

        [Fact]
        public void ReleaseClaimUnlessStranded_WhenTheCameraStopped_ReleasesTheClaimAsBefore()
        {
            // THE POSITIVE CONTROL, and without it every assertion above is satisfied by an owner
            // that simply never releases anything - which would hold the claim on every normal
            // recording and stall the whole post-recording pipeline.
            //
            // Bad result: the claim survives a stop whose camera really did end.
            var owner = new StrandedCameraOwner();
            var rec = CleanRecorder();
            var claim = Claim();

            bool retained = owner.ReleaseClaimUnlessStranded(rec, claim, _dir);

            Assert.False(retained);
            Assert.False(owner.HoldsAny);
            Assert.False(RecordingWorkset.IsClaimed(_dir));
        }

        [Fact]
        public void ReleaseClaimUnlessStranded_WithNoCameraAtAll_ReleasesTheClaimAsBefore()
        {
            // The overwhelmingly common case - a recording with no camera track - must be completely
            // unaffected by any of this.
            var owner = new StrandedCameraOwner();
            var claim = Claim();

            Assert.False(owner.ReleaseClaimUnlessStranded(null, claim, _dir));
            Assert.False(owner.HoldsAny);
            Assert.False(RecordingWorkset.IsClaimed(_dir));
        }

        // ---- what /status is told -----------------------------------------------

        [Fact]
        public void Report_NamesTheStuckProcessAndItsPid()
        {
            // AC16's reporting clause. "A camera process is stuck" is a sentence; "PID 24512 is
            // stuck holding this file" is something a person can act on with Task Manager or
            // taskkill. The PID is the field that makes the difference, so it is asserted by value.
            //
            // Bad result: an empty report (the failure is invisible on /status) or a null PID.
            var owner = new StrandedCameraOwner();
            var rec = StrandedRecorder(new FakeStuckCamera());
            owner.ReleaseClaimUnlessStranded(rec, Claim(), _dir);

            var rows = owner.Report();

            Assert.Single(rows);
            Assert.Equal("HD Webcam eMeet C960", rows[0].Device);
            Assert.Equal(FakeStuckCamera.Pid, rows[0].Pid);
            Assert.Equal(Path.Combine(_dir, "camera.mp4"), rows[0].Output);
            Assert.Equal(_dir, rows[0].Dir);
        }

        [Fact]
        public void Report_WithNothingStranded_IsEmpty()
        {
            // The other direction, so a report that always named something could not pass the test
            // above. /status must say "no stuck camera" on every ordinary machine.
            var owner = new StrandedCameraOwner();

            Assert.False(owner.HoldsAny);
            Assert.Empty(owner.Report());
        }

        // ---- the retry, which is what makes retaining it worth anything ----------

        [Fact]
        public void Recover_WhenTheProcessFinallyDies_ReleasesTheClaimAndForgetsIt()
        {
            // Retaining a recorder that nothing ever retries is a museum piece. This is the other
            // end of the lifetime: the next recording start asks the owner to try again, the kill
            // lands this time, and the claim, the /status row and the process handle all go together.
            //
            // Bad result: the claim is still held (the directory is locked out of the pipeline
            // forever) or the row is still reported for a process that is gone.
            var owner = new StrandedCameraOwner();
            var proc = new FakeStuckCamera();
            var rec = StrandedRecorder(proc);
            owner.ReleaseClaimUnlessStranded(rec, Claim(), _dir);
            Assert.True(RecordingWorkset.IsClaimed(_dir));

            proc.KillEndsIt = true;      // whatever was holding it lets go
            owner.Recover();

            Assert.False(owner.HoldsAny);
            Assert.Empty(owner.Report());
            Assert.False(RecordingWorkset.IsClaimed(_dir));
            Assert.True(proc.HasExited);
            Assert.Equal(1, proc.Disposes);   // and only NOW is the handle released
        }

        [Fact]
        public void Recover_WhileTheProcessIsStillAlive_KeepsHoldingItAndItsClaim()
        {
            // The control for the retry: a recovery that forgot the process whether or not it died
            // would pass the test above and re-open the exact defect - an unreachable live ffmpeg,
            // with the directory published to the pipeline underneath it.
            //
            // Bad result: HoldsAny false, or the claim released, while the fake is still alive.
            var owner = new StrandedCameraOwner();
            var proc = new FakeStuckCamera();
            var rec = StrandedRecorder(proc);
            owner.ReleaseClaimUnlessStranded(rec, Claim(), _dir);

            owner.Recover();

            Assert.False(proc.HasExited, "this test only means anything while the fake ffmpeg survives");
            Assert.True(owner.HoldsAny);
            Assert.True(RecordingWorkset.IsClaimed(_dir));
            Assert.Equal(0, proc.Disposes);
            Assert.Single(owner.Report());
        }

        [Fact]
        public void Recover_DoesNotForgetOneStrandedCameraWhileAnotherIsStillAlive()
        {
            // Two stuck cameras is not a hypothetical: the service goes back to idle after a failed
            // stop, so a second recording can be started and its camera can be abandoned too.
            // Dropping either reference to keep the shape simple would throw away the only handle to
            // a live process, which is the whole defect this class closes.
            var owner = new StrandedCameraOwner();
            var first = new FakeStuckCamera();
            var second = new FakeStuckCamera();
            owner.ReleaseClaimUnlessStranded(StrandedRecorder(first), Claim(), _dir);
            owner.ReleaseClaimUnlessStranded(StrandedRecorder(second), default, _dir + "_2");

            first.KillEndsIt = true;
            owner.Recover();

            Assert.True(owner.HoldsAny);
            Assert.Single(owner.Report());
            Assert.True(first.HasExited);
            Assert.False(second.HasExited);
            Assert.False(RecordingWorkset.IsClaimed(_dir), "the recovered camera's claim must be released");
        }

        // ---- gate round 4, defect 4: a retained process that DIES ------------------
        //
        // The gate drove the real RecordingService.Status and the real RecordingWorkset and got:
        //
        //   RETAINED_PROCESS_DIED  hasExited=True isAbandoned=True cameraStuck=True statusRows=1
        //                          pid=4242 claimHeld=True
        //   AFTER_EXPLICIT_RECOVER cameraStuck=False holdsAny=False statusRows=0 claimHeld=False
        //
        // Every row on /status is an assertion that a process is alive RIGHT NOW, and the only
        // production caller of Recover() is the next StartVideo - so a stranded ffmpeg that exits on
        // its own left a dead PID reported as stuck, and that recording's claim held, until the user
        // happened to record again. Reading /status is exactly the moment somebody is asking, so it
        // is where the liveness is re-read.

        [Fact]
        public void Report_WhenTheStrandedProcessDiedOnItsOwn_StopsReportingItAndReleasesTheClaim()
        {
            // GATE CASE RETAINED_PROCESS_DIED. Nothing kills the process, nothing calls Recover, and
            // no later recording is started: the ffmpeg simply exits, and the very next look at
            // /status has to tell the truth about it - and let go of the directory it was holding
            // for a writer that no longer exists.
            //
            // Bad results this fires on: a row still reported for a dead PID, or the claim still
            // held. Empty result: impossible - the row and the claim are both asserted PRESENT
            // first, so the test cannot pass by never reaching the state it is about.
            var owner = new StrandedCameraOwner();
            var proc = new FakeStuckCamera();
            var rec = StrandedRecorder(proc);
            owner.ReleaseClaimUnlessStranded(rec, Claim(), _dir);

            Assert.Single(owner.Report());
            Assert.True(RecordingWorkset.IsClaimed(_dir));

            proc.DiesOnItsOwn();          // ... and nothing in AgentEyes is watching

            Assert.Empty(owner.Report());
            Assert.False(owner.HoldsAny);
            Assert.False(RecordingWorkset.IsClaimed(_dir),
                "a claim kept for a live writer must be released the moment that writer is gone - "
                + "packaging and transcription are blocked on it");
            Assert.Equal(1, proc.Disposes);   // and the handle is released, once it is safe to
        }

        [Fact]
        public void HoldsAny_WhenTheStrandedProcessDiedOnItsOwn_IsFalse()
        {
            // The same liveness through the other reader. HoldsAny is what /status turns into
            // CameraStuck, and a stale true there tells the user a webcam is still taken when it is
            // not.
            //
            // Bad result: HoldsAny == true for a process that has exited.
            var owner = new StrandedCameraOwner();
            var proc = new FakeStuckCamera();
            owner.ReleaseClaimUnlessStranded(StrandedRecorder(proc), Claim(), _dir);
            Assert.True(owner.HoldsAny);

            proc.DiesOnItsOwn();

            Assert.False(owner.HoldsAny);
        }

        [Fact]
        public void Report_WhileTheStrandedProcessIsStillAlive_KeepsReportingItAndItsClaim()
        {
            // THE POSITIVE CONTROL, without which "let go of dead ones" is satisfied by an owner
            // that lets go of everything - which would re-open the original defect completely: an
            // unreachable live ffmpeg with its recording directory published to every automatic pass
            // in the app.
            //
            // Bad result: an empty report, or a released claim, while the fake is still alive.
            var owner = new StrandedCameraOwner();
            var proc = new FakeStuckCamera();
            owner.ReleaseClaimUnlessStranded(StrandedRecorder(proc), Claim(), _dir);

            var rows = owner.Report();

            Assert.False(proc.HasExited, "this test only means anything while the fake ffmpeg survives");
            Assert.Single(rows);
            Assert.Equal(FakeStuckCamera.Pid, rows[0].Pid);
            Assert.True(owner.HoldsAny);
            Assert.True(RecordingWorkset.IsClaimed(_dir));
            Assert.Equal(0, proc.Disposes);
        }

        [Fact]
        public void Report_WhenOneStrandedProcessDiedAndAnotherDidNot_KeepsOnlyTheLiveOne()
        {
            // Two stuck cameras is not hypothetical - the service goes back to idle after a failed
            // stop - and the liveness re-read must be per process, not a whole-list decision.
            //
            // Bad result: both rows dropped, or both kept.
            var owner = new StrandedCameraOwner();
            var dead = new FakeStuckCamera();
            var alive = new FakeStuckCamera();
            owner.ReleaseClaimUnlessStranded(StrandedRecorder(dead), Claim(), _dir);
            owner.ReleaseClaimUnlessStranded(StrandedRecorder(alive), default, _dir + "_2");
            Assert.Equal(2, owner.Report().Count);

            dead.DiesOnItsOwn();

            Assert.Single(owner.Report());
            Assert.True(owner.HoldsAny);
            Assert.False(alive.HasExited);
            Assert.False(RecordingWorkset.IsClaimed(_dir), "the dead one's claim must be released");
        }

        // ---- gate round 4, defect 5: the CLI must hand its abandoned camera over ---------------

        [Fact]
        public void Video_HandsAnAbandonedCameraToAStrandedOwnerAtBothOfItsFailureBoundaries()
        {
            // GATE ROUND 4, DEFECT 5, and it is STRUCTURAL: "the only StrandedCameraOwner in product
            // code is the service field at RecordingService.cs:121; there is no transfer from the
            // CLI path." Commands.Video wrote the honest abandoned/unknown manifest and then let its
            // finally call Dispose() and the local leave scope, so the one handle able to reach a
            // live ffmpeg still holding the webcam and camera.mp4 was dropped on the floor.
            //
            // This is read out of the COMPILED IL rather than the source: a `using`, an alias or a
            // local would defeat a text scan, and what matters is which method actually contains the
            // call. It fails closed - CallSites throws if the assembly is missing, and the scanner
            // is proved able to see these calls at all before the absence is judged.
            //
            // Bad result this fires on: Commands::Video calls FfmpegCameraRecorder::Dispose (it
            // still must) but calls nothing on StrandedCameraOwner. Empty result: the two guard
            // assertions below turn "the scan found nothing anywhere" into a failure rather than a
            // pass.
            //
            // WHAT IT CANNOT SEE, stated rather than implied: it proves the transfers are COMPILED
            // INTO Commands::Video, not that they run on every path - the behaviour of the owner
            // itself is what the tests above exercise.
            //
            // AND IT IS PER SITE (gate round 5, QA mutation ruling). The assertion that used to
            // stand here was "Commands::Video calls SOMETHING on StrandedCameraOwner", which is
            // satisfied by EITHER boundary, so deleting either one alone left it green - the two
            // non-firing CLI mutations the gate reported. There are two transfers because there are
            // two ways out of this command that can strand a camera, and each one needs an assertion
            // that only it can satisfy. IL exception regions tell them apart exactly: the failed-open
            // transfer sits INSIDE the command's last-owner boundary (a try covered by a finally),
            // and the final transfer IS that boundary's handler, which no try of its own covers.
            const string Retain = "AgentEyes.StrandedCameraOwner::RetainIfStranded";
            const string DisposeRecorder = "AgentEyes.Video.FfmpegCameraRecorder::Dispose";

            // Fail-closed by construction: GuardedCalls throws if Commands::Video is gone, or if it
            // contains no call to the callee at all, so "found nothing" can never read as a pass.
            var transfers = CompiledCode.GuardedCalls(CompiledCode.CoreAssembly, "AgentEyes.Commands::Video", Retain)
                .OrderBy(c => c.Offset).ToList();
            var disposals = CompiledCode.GuardedCalls(CompiledCode.CoreAssembly, "AgentEyes.Commands::Video", DisposeRecorder)
                .OrderBy(c => c.Offset).ToList();

            // The per-site assertions come FIRST on purpose. A count of two would fail for either
            // deletion, but it would name neither, and a single assertion that both sites happen to
            // trip is not the same thing as an assertion per site. Deleting one boundary has to fail
            // on the line that is about THAT boundary.

            // SITE 1 - the failed-open boundary. Its distinguishing fact is that it runs while the
            // command's own finally is still owed: the open failed inside the outer try, so this
            // call is covered by a Finally region. Deleting it takes this line's Single() with it.
            var failedOpen = Assert.Single(transfers.Where(c => c.Handlers.Contains("Finally")));

            // SITE 2 - the final boundary, read from the OTHER side: it is the handler that covers
            // site 1, so site 1's own cleanup list names it. The order is part of the assertion -
            // the retry disposes first and hands over only what that could not end. Deleting this
            // boundary empties this list, whatever site 1 does.
            Assert.Equal(
                new[] { DisposeRecorder, Retain },
                failedOpen.CleanupCalls.Where(c => c == DisposeRecorder || c == Retain).ToArray());

            // ...and directly, as a call that no try region protects, because it IS the handler.
            var lastOwner = Assert.Single(transfers.Where(c => c.Handlers.Count == 0));
            Assert.True(lastOwner.Offset > failedOpen.Offset,
                "the last-owner boundary is the finally handler, which the compiler emits after the try");

            // Each transfer is preceded by ITS OWN disposal: getting ffmpeg off the camera is tried
            // first at both sites, and only what survives that is handed over.
            Assert.Equal(2, disposals.Count);
            Assert.True(disposals[0].Offset < failedOpen.Offset, "the failed-open path disposes before it hands over");
            Assert.True(disposals[1].Offset > failedOpen.Offset && disposals[1].Offset < lastOwner.Offset,
                "the finally disposes before it hands over");

            // And there is no THIRD transfer hiding behind the two the assertions above named.
            Assert.Equal(2, transfers.Count);
        }

        // ---- the failed-start path ----------------------------------------------

        [Fact]
        public void DiscardDirectoryUnlessStranded_WhenTheCameraIsStillRunning_LeavesTheDirectoryAlone()
        {
            // The failed-start half of AC16. A camera whose OPEN failed can strand ffmpeg exactly as
            // a failed stop can, and the rollback there does something worse than release a claim -
            // it deletes the directory. Deleting a directory around a live ffmpeg does not stop the
            // ffmpeg: it fails on the file that process holds open, and replaces the real, actionable
            // "the camera is already in use" with an IO error about camera.mp4.
            //
            // Bad result: the directory is gone, or the recorder was not retained.
            var owner = new StrandedCameraOwner();
            var rec = StrandedRecorder(new FakeStuckCamera());
            File.WriteAllText(Path.Combine(_dir, "camera.mp4"), "pretend ffmpeg is writing this");

            bool retained = owner.DiscardDirectoryUnlessStranded(rec, _dir, Claim());

            Assert.True(retained);
            Assert.True(Directory.Exists(_dir));
            Assert.True(owner.HoldsAny);
            Assert.True(RecordingWorkset.IsClaimed(_dir));
        }

        [Fact]
        public void DiscardDirectoryUnlessStranded_WhenTheCameraStopped_DiscardsTheDirectoryAsBefore()
        {
            // POSITIVE CONTROL, and it is AC8/AC9 in miniature: the ordinary failed start - a busy
            // or absent camera, where ffmpeg exits by itself - must still leave NOTHING behind. An
            // owner that retained everything would quietly turn every failed start into an orphaned
            // recording directory.
            //
            // Bad result: the directory survives a failed start whose camera really did end.
            var owner = new StrandedCameraOwner();
            var rec = FailedOpenRecorder();

            bool retained = owner.DiscardDirectoryUnlessStranded(rec, _dir, Claim());

            Assert.False(retained);
            Assert.False(owner.HoldsAny);
            Assert.False(Directory.Exists(_dir));
            Assert.False(RecordingWorkset.IsClaimed(_dir));
        }
    }
}
