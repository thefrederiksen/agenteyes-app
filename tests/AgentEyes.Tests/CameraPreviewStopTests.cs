using System;
using System.IO;
using System.Threading;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #35, Review Gate round 1, DEFECT 4 - the real ffmpeg stop path.
    ///
    /// The gate called this "issue #28's original bug in a different file", and it was: the preview
    /// caught a failed <c>Process.Kill</c>, logged a process still alive after the kill wait, then
    /// announced unconditionally that the camera had been released and DISPOSED THE PROCESS WRAPPER.
    /// Disposing a wrapper does not terminate an operating-system process; it only throws away the
    /// last handle able to reach a live ffmpeg sitting on the webcam. On a close route that is an
    /// orphan with no remaining handle in AgentEyes - which is exactly what happened on this machine
    /// on 2026-08-28, where an orphaned capture ran for 3.6 hours unseen by the app.
    ///
    /// WHAT THESE CAN AND CANNOT SEE. "ffmpeg ignored the kill" and "Kill threw" are not states a
    /// real ffmpeg can be asked to enter, so the process is a fake behind
    /// <see cref="ICameraPreviewProcess"/> - the same seam issue #28 introduced for the recorder and
    /// for the same reason. Everything else is production code. They prove the OWNERSHIP decisions:
    /// what is claimed, what is retained, and when the handle is released. They do NOT prove that a
    /// real webcam comes back from Windows - that needs a camera and the running app.
    /// </summary>
    public sealed class CameraPreviewStopTests
    {
        private const string Camera = "HD Webcam eMeet C960";

        [Fact]
        public void Stop_WhenFfmpegIgnoresTheKill_DoesNotReportTheCameraAsFree()
        {
            // The bad result: IsAbandoned false - i.e. the session telling its owner the camera is
            // free while the process that holds it is demonstrably still running.
            var proc = new FakeProcess { KillEndsIt = false };
            var preview = Preview(proc);

            preview.Stop();

            Assert.Equal(1, proc.Kills);
            Assert.True(proc.IsRunning);
            Assert.True(preview.IsAbandoned);
        }

        [Fact]
        public void Stop_WhenFfmpegAnswersTheKill_ReportsTheCameraAsFree()
        {
            // NEGATIVE CONTROL. The same instrument on the good case must report the camera free -
            // otherwise "IsAbandoned == true" above would be a constant rather than an observation.
            var proc = new FakeProcess { KillEndsIt = true };
            var preview = Preview(proc);

            preview.Stop();

            Assert.Equal(1, proc.Kills);
            Assert.False(proc.IsRunning);
            Assert.False(preview.IsAbandoned);
        }

        [Fact]
        public void IsAbandoned_BeforeAnyStop_IsFalse()
        {
            // NEGATIVE CONTROL for the state machine: a running preview nobody has asked to stop is
            // not stranded, it is previewing. Without this, IsAbandoned could be "true whenever the
            // process is alive" and every assertion above would still pass.
            var proc = new FakeProcess { KillEndsIt = false };
            var preview = Preview(proc);

            Assert.True(proc.IsRunning);
            Assert.False(preview.IsAbandoned);
        }

        [Fact]
        public void Stop_WhenTheKillItselfThrows_DoesNotReportTheCameraAsFree()
        {
            // A Kill that throws is a FAILED attempt. The old code caught it, logged it, and carried
            // straight on to "camera released".
            var proc = new FakeProcess { KillEndsIt = false, KillThrows = true };
            var preview = Preview(proc);

            preview.Stop();

            Assert.Equal(1, proc.Kills);
            Assert.True(preview.IsAbandoned);
        }

        [Fact]
        public void Dispose_WhenFfmpegIgnoresTheKill_KEEPS_TheProcessHandle()
        {
            // THE DEFECT ITSELF. Dispose used to release the handle unconditionally. The bad result:
            // proc.Disposed true while proc.IsRunning is also true - a live ffmpeg on the webcam that
            // nothing in AgentEyes can reach any more.
            var proc = new FakeProcess { KillEndsIt = false };
            var preview = Preview(proc);

            preview.Dispose();

            Assert.True(proc.IsRunning);
            Assert.False(proc.Disposed);
            Assert.True(preview.IsAbandoned);
        }

        [Fact]
        public void Dispose_WhenTheProcessIsConfirmedGone_ReleasesTheProcessHandle()
        {
            // NEGATIVE CONTROL for the test above: the handle MUST be released once the process is
            // really gone, or "Disposed == false" would prove only that Dispose never worked.
            var proc = new FakeProcess { KillEndsIt = true };
            var preview = Preview(proc);

            preview.Dispose();

            Assert.False(proc.IsRunning);
            Assert.True(proc.Disposed);
            Assert.False(preview.IsAbandoned);
        }

        [Fact]
        public void Stop_CalledAgainOnASurvivingProcess_PerformsAnotherTerminationAttempt()
        {
            // What makes a retained session worth retaining: StrandedCameraOwner.Recover() gets the
            // camera back by stopping it AGAIN. The bad result: a latched _stopped flag that turns
            // every later attempt into a no-op, so a retained session is a museum piece.
            var proc = new FakeProcess { KillEndsIt = false };
            var preview = Preview(proc);

            preview.Stop();
            Assert.Equal(1, proc.Kills);
            Assert.True(preview.IsAbandoned);

            proc.KillEndsIt = true;      // the process finally becomes killable
            preview.Stop();

            Assert.Equal(2, proc.Kills);
            Assert.False(proc.IsRunning);
            Assert.False(preview.IsAbandoned);
        }

        [Fact]
        public void Stop_AfterTheHandleWasReleased_TouchesNothing()
        {
            // Reading a released handle throws. The bad result: an owner sweep over a reaped session
            // taking the process down with an ObjectDisposedException.
            var proc = new FakeProcess { KillEndsIt = true };
            var preview = Preview(proc);

            preview.Dispose();
            Assert.True(proc.Disposed);

            preview.Stop();      // must not throw and must not touch the released handle
            preview.Dispose();

            Assert.Equal(1, proc.Kills);
            Assert.Equal(1, proc.Disposes);
        }

        [Fact]
        public void APreviewSession_IsOwnedByTheSameStrandedOwnerAsARecording()
        {
            // Issue #28's StrandedCameraOwner is REUSED rather than a second one written to the same
            // description: a surviving preview is an IStrandedCameraProcess and goes into the same
            // list, with its PID, and is reaped by the same rule when the process finally goes.
            var proc = new FakeProcess { KillEndsIt = false, Pid = 31337 };
            var preview = Preview(proc);
            var owner = new StrandedCameraOwner();

            preview.Stop();
            bool retained = owner.RetainIfStranded(preview, dir: null);

            Assert.True(retained);
            Assert.Contains(owner.Report(), r => r.Device == Camera && r.Pid == 31337 && r.Output == null);

            proc.KillEndsIt = true;
            owner.Recover();

            Assert.Empty(owner.Report());
            Assert.True(proc.Disposed);
        }

        [Fact]
        public void APreviewSessionThatDied_IsNotRetainedAtAll()
        {
            // NEGATIVE CONTROL for the test above. A normal preview must NOT end up on the stranded
            // list, or the list would report a stuck camera on every close.
            var proc = new FakeProcess { KillEndsIt = true };
            var preview = Preview(proc);
            var owner = new StrandedCameraOwner();

            preview.Stop();

            Assert.False(owner.RetainIfStranded(preview, dir: null));
            Assert.Empty(owner.Report());
        }

        // ---- helpers -----------------------------------------------------------

        private static FfmpegCameraPreview Preview(FakeProcess proc) =>
            FfmpegCameraPreview.Start(proc, Camera, _ => { }, _ => { });

        /// <summary>
        /// The two ffmpeg behaviours that matter and that a real ffmpeg cannot be asked for: a
        /// process that survives every kill, and a Kill that throws. Its stdout is an empty stream,
        /// so the preview's reader thread reaches end-of-stream immediately and gets out of the way.
        /// </summary>
        private sealed class FakeProcess : ICameraPreviewProcess
        {
            private int _kills;
            private int _disposes;

            /// <summary>False = ffmpeg ignores the kill and goes on holding the camera.</summary>
            public bool KillEndsIt = true;

            /// <summary>True = the Kill call itself throws, which is a FAILED attempt.</summary>
            public bool KillThrows;

            public int Pid = 24512;

            public volatile bool IsRunning = true;

            public int Kills => Volatile.Read(ref _kills);
            public int Disposes => Volatile.Read(ref _disposes);
            public bool Disposed => Disposes > 0;

            public void Start(Action<string> onStderrLine) { }

            public Stream StandardOutput { get; } = new MemoryStream(Array.Empty<byte>());

            public bool HasExited
            {
                get
                {
                    if (Disposed) throw new InvalidOperationException(
                        "the process handle was released - reading it is the defect this fake exists to catch");
                    return !IsRunning;
                }
            }

            public int ExitCode => 0;

            public int? ProcessId => Pid;

            public bool WaitForExit(int milliseconds) => !IsRunning;

            public void Kill()
            {
                Interlocked.Increment(ref _kills);
                if (KillThrows) throw new InvalidOperationException("Access is denied");
                if (KillEndsIt) IsRunning = false;
            }

            public void Dispose() => Interlocked.Increment(ref _disposes);
        }
    }
}
