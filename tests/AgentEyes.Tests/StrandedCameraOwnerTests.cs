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
