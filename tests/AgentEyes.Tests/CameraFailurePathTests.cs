using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #28, Review Gate round 2 - the camera track's FAILURE paths.
    ///
    /// WHY THIS FILE EXISTS. The gate's own words about the merged code: "a derived search of every
    /// test reference to FfmpegCameraRecorder found only five calls to DiagnoseOpenFailure; no test
    /// exercises Start, Stop, process ownership, termination failure, or the exit/stop race." Every
    /// one of the five blocking defects lived on a path nothing could reach, and the successful
    /// runtime runs the QA report leaned on never touched any of them. So each of the five is pinned
    /// here by a check that was RUN AGAINST THE DEFECTIVE BEHAVIOUR FIRST - see
    /// docs/cencon/proof/issue-28/mutation-evidence.txt for the failing output of every one.
    ///
    /// WHAT THESE CAN AND CANNOT SEE.
    ///  - Defects 2, 3 and 4 are behavioural, driven through <see cref="ICameraProcess"/>. They
    ///    exercise the REAL <see cref="FfmpegCameraRecorder"/> logic over a process whose exits,
    ///    timeouts and callbacks the test controls. They do NOT prove anything about ffmpeg itself:
    ///    that a real webcam records is the running-app proof's job, not this file's.
    ///  - Defects 1 and 5 are STRUCTURAL - the presence or absence of a failure boundary around a
    ///    call. They are read out of the compiled IL, because a `using` declaration writes no
    ///    "finally" in the source and a text scan cannot say which call a boundary covers.
    ///  - Each test below names the bad result it fires on. An EMPTY or unfindable target is a broken
    ///    instrument, and <see cref="CompiledCode.GuardedCalls"/> throws rather than passing on one.
    ///
    /// ROUND 3 (gate REJECT of PR #32). The gate rejected this file's own coverage as well as the
    /// code, and it was right on both counts. Every round-3 defect was one of two assumptions:
    /// "we asked the process to die" taken as "the process died", and "the device opened" taken as
    /// "the device is producing video". What changed here:
    ///
    ///  - THE FAILED-START PATHS ARE COVERED AT LAST. There was no startup test for a kill that is
    ///    REFUSED (KillEndsIt = false) or one that THROWS (KillThrows = true), which is precisely
    ///    where the code marked itself terminated and released the handle to a live ffmpeg.
    ///  - ONE TEST HERE WAS ITSELF THE DEFECT.
    ///    <see cref="Stop_AfterAFailedTermination_DisposeKeepsTheProcessReachableInsteadOfAbandoningIt"/>
    ///    used to assert only that a second kill had been ATTEMPTED and that the wrapper had been
    ///    disposed - both true whether or not ffmpeg died. It certified a lifetime guarantee it never
    ///    checked. It is rewritten, not deleted, and it now fails against the code it once passed.
    ///  - THE ZERO-FRAME CAMERA IS TESTED ALIVE. The old test for it called End(1) FIRST, so it only
    ///    ever proved the already-exited path - not the live, silent camera the header-based open
    ///    probe actually creates.
    ///
    /// Every check added or rewritten in round 3 was RUN AGAINST THE ROUND-3 CODE FIRST; the failing
    /// output of each is in docs/cencon/proof/issue-28/mutation-evidence-round4.txt.
    /// </summary>
    public sealed class CameraFailurePathTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "AgentEyesCameraFailure_" + Guid.NewGuid().ToString("N"));

        public CameraFailurePathTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private string Out => Path.Combine(_dir, "camera.mp4");
        private string LogPath => Path.Combine(_dir, "camera.mp4.ffmpeg.log");

        /// <summary>
        /// ffmpeg's report that it OPENED the DirectShow camera - HALF of what the open probe waits
        /// for. Verbatim from the shipped ffmpeg against the eMeet C960.
        /// </summary>
        private const string InputOpenReport =
            "Input #0, dshow, from 'video=HD Webcam eMeet C960':";

        /// <summary>ffmpeg's report that camera.mp4 is open for writing - the other half.</summary>
        private const string OutputOpenReport =
            "Output #0, mp4, to 'C:\\Users\\soren\\Videos\\AgentEyes\\2026-08-28_104509_video\\camera.mp4':";

        /// <summary>
        /// An ffmpeg progress tick carrying a real output position. NOT what the open probe waits
        /// for - it is what round 2 waited for, and libx264 frame-threading holds it back by seconds
        /// while the camera is already filming (issue #28, AC3 regression).
        /// </summary>
        private const string ProgressTick =
            "frame=   15 fps= 30 q=28.0 size=      64KiB time=00:00:00.50 bitrate=1048.6kbits/s speed=1x";

        /// <summary>
        /// A camera process the test drives: it exits when the test says so, quits (or refuses to)
        /// when asked, dies under a kill (or survives one), and delivers its exit callback only if
        /// the test wants it delivered. Every one of those is a real ffmpeg behaviour that
        /// System.Diagnostics.Process cannot be asked to perform.
        /// </summary>
        private sealed class FakeCameraProcess : ICameraProcess
        {
            private Action<string>? _onStderr;
            private Action? _onExited;

            /// <summary>Report the DirectShow input open the moment the process starts.</summary>
            public bool ReportsInputOpenOnStart = true;

            /// <summary>Report camera.mp4 open for writing the moment the process starts.</summary>
            public bool ReportsOutputOpenOnStart = true;

            /// <summary>
            /// Emit a progress tick on start. Deliberately SEPARATE from the two open reports: a real
            /// ffmpeg emits the headers seconds before its first encoded frame, and the start must not
            /// wait for the frame (issue #28, AC3).
            /// </summary>
            public bool ReportsProgressOnStart = true;

            /// <summary>Exit during Start with this code, i.e. a camera that fails immediately.</summary>
            public int? ExitsOnStartWith;

            /// <summary>ffmpeg's stderr, delivered during Start before the exit above - the order a
            /// real failing ffmpeg produces them in.</summary>
            public string[]? StderrOnStart;

            /// <summary>False = ffmpeg ignores "q" (a stuck dshow device).</summary>
            public bool QuitEndsIt = true;

            /// <summary>
            /// What happens as the write of "q" to ffmpeg's stdin FAILS - the pipe is gone because
            /// the process is dying underneath it. Set it and SendQuit runs this and then throws,
            /// which is the real shape of the gate's FAILED_QUIT_THEN_ERROR_EXIT case: the quit
            /// never reached ffmpeg, and ffmpeg ended for a reason nothing here observed.
            /// </summary>
            public Action? QuitFailsWith;

            /// <summary>What happens when "q" arrives, when a test needs more than "it ends" or "it
            /// does not" - a real ffmpeg FLUSHES on the way out, and whether that late output may
            /// certify a track is its own question (AC13). Takes precedence over
            /// <see cref="QuitEndsIt"/>.</summary>
            public Action? QuitEndsItWith;

            /// <summary>False = the process survives Kill. Deliberately a mutable field, so a test
            /// can let the FIRST kill be refused and the second succeed - that is how the recovery
            /// this class exists to keep alive is proved to still be reachable.</summary>
            public bool KillEndsIt = true;

            /// <summary>True = Kill itself throws (access denied, already-exiting race).</summary>
            public bool KillThrows;

            /// <summary>False = the stderr reader never reports end of stream, i.e. ffmpeg's output
            /// is INCOMPLETE when the stop reads it.</summary>
            public bool StderrReachesEof = true;

            /// <summary>How many times the stop asked for the stderr to be drained.</summary>
            public int Drains;

            /// <summary>False = the process ends but its Exited callback is never delivered.</summary>
            public bool DeliversExitCallback = true;

            public bool Started;
            public int Quits;
            public int Kills;
            public int Disposes;

            /// <summary>The OS process id this fake reports once started - what AC16 requires
            /// /status to carry so a stuck ffmpeg can actually be dealt with.</summary>
            public const int Pid = 24512;

            public bool HasExited { get; private set; }
            public int ExitCode { get; private set; }

            public int? ProcessId { get; private set; }

            public void Start(Action<string> onStderrLine, Action onExited)
            {
                Started = true;
                ProcessId = Pid;
                _onStderr = onStderrLine;
                _onExited = onExited;
                // The order a real ffmpeg produces them in: input header, output header, then ticks.
                if (ReportsInputOpenOnStart) Emit(InputOpenReport);
                if (ReportsOutputOpenOnStart) Emit(OutputOpenReport);
                if (ReportsProgressOnStart) Emit(ProgressTick);
                if (StderrOnStart != null)
                    foreach (string line in StderrOnStart) Emit(line);
                if (ExitsOnStartWith.HasValue) End(ExitsOnStartWith.Value);
            }

            public void Emit(string line) => _onStderr!(line);

            /// <summary>End the process, delivering the exit callback only when configured to.</summary>
            public void End(int exitCode)
            {
                ExitCode = exitCode;
                HasExited = true;
                if (DeliversExitCallback) _onExited!();
            }

            public void SendQuit()
            {
                Quits++;
                if (QuitFailsWith != null)
                {
                    QuitFailsWith();
                    throw new IOException("the pipe has been ended.");
                }
                if (QuitEndsItWith != null) { QuitEndsItWith(); return; }
                if (QuitEndsIt) End(0);
            }

            public bool WaitForExit(int milliseconds) => HasExited;

            public bool DrainStderr(int milliseconds)
            {
                Drains++;
                return StderrReachesEof;
            }

            public void Kill()
            {
                Kills++;
                if (KillThrows) throw new InvalidOperationException("access is denied");
                if (KillEndsIt) End(-1);
            }

            public void Dispose() => Disposes++;
        }

        /// <summary>
        /// A clock the test moves by hand.
        ///
        /// AC13's subject is a camera that emitted one progress tick and then STALLED FOR THE REST OF
        /// A THIRTY-SECOND SESSION. Reaching that with the real clock means either sleeping for
        /// thirty seconds or shrinking the window until the test is a race; moving time by hand is
        /// neither. Every assertion about staleness in this file is deterministic because of this.
        /// </summary>
        private sealed class TestClock
        {
            private DateTime _utc = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

            public DateTime Now() => _utc;

            public void Advance(TimeSpan by) => _utc += by;
        }

        /// <summary>An ffmpeg progress tick at a given output position - the ONLY evidence this
        /// recorder ever has that camera.mp4 contains anything.</summary>
        private static string TickAt(TimeSpan position) =>
            $"frame=  {(int)(position.TotalSeconds * 30),4} fps= 30 q=28.0 size=      64KiB "
            + $"time={position:hh\\:mm\\:ss\\.ff} bitrate=1048.6kbits/s speed=1x";

        /// <summary>Build the recorder without starting anything - the first half of the two-phase
        /// ownership the callers use (issue #28, gate round 3, defect 1).</summary>
        private FfmpegCameraRecorder Create(FakeCameraProcess proc, double openTimeoutSeconds = 5.0,
            TestClock? clock = null) =>
            FfmpegCameraRecorder.CreateOver(proc, "HD Webcam eMeet C960", Out, LogPath,
                TimeSpan.FromSeconds(openTimeoutSeconds), clock == null ? null : clock.Now);

        /// <summary>Create and open in one go, for the tests whose subject is not the failed open.
        /// The tests that ARE about a failed open keep the two halves apart on purpose, because the
        /// recorder the caller still owns after Open() threw is the whole point.</summary>
        private FfmpegCameraRecorder StartOver(FakeCameraProcess proc, double openTimeoutSeconds = 5.0,
            TestClock? clock = null)
        {
            var rec = Create(proc, openTimeoutSeconds, clock);
            rec.Open();
            return rec;
        }

        // ---- gate defect 3: the open probe must establish that the camera OPENED ----------------

        [Fact]
        public void Start_WhenTheCameraNeverProducesVideo_FailsTheStartInsteadOfCallingItOpen()
        {
            // THE DEFECT. The probe slept 400 ms and rejected only a process that had ALREADY exited
            // with a non-zero code. A busy, unplugged or unsupported DirectShow device that takes
            // longer than that to fail was marked opened, the screen recorder started, and the
            // camera's later exit was filed as a harmless mid-run loss - so a camera that never
            // recorded a frame silently became a screen-only recording. AC9 says that start FAILS.
            //
            // Bad result this fires on: StartOver returns a recorder. Empty result: impossible -
            // either it throws or it returns, and both are asserted.
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
            };

            var ex = Assert.Throws<UsageException>(() => StartOver(proc, openTimeoutSeconds: 0.2));

            Assert.Contains("HD Webcam eMeet C960", ex.Message);
            Assert.Contains("could not be opened", ex.Message);
        }

        [Fact]
        public void Start_WhenTheCameraNeverProducesVideo_KillsTheStalledFfmpegAndReleasesIt()
        {
            // The other half of the same fix, and the one that would turn the fix into a NEW leak if
            // it were missing: a probe that times out is looking at a LIVE process holding the
            // webcam. Failing the start without killing it would strand ffmpeg exactly as defect 1
            // did. Bad result: Kills == 0 (walked away from a running process).
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
            };

            Assert.Throws<UsageException>(() => StartOver(proc, openTimeoutSeconds: 0.2));

            Assert.Equal(1, proc.Kills);
            Assert.True(proc.HasExited, "the stalled camera ffmpeg must be dead before the start fails");
            Assert.Equal(1, proc.Disposes);
        }

        // ---- gate ROUND 3 defect 1: a failed START may not strand ffmpeg either -----------------

        [Fact]
        public void Open_WhenTheStalledFfmpegSurvivesTheKill_KeepsTheProcessHandleForARetry()
        {
            // THE DEFECT (gate round 3, defect 1). The probe timed out, killed, waited, saw the
            // process STILL ALIVE - and then LOGGED that and marked itself terminated and disposed
            // the wrapper anyway. Disposing an ICameraProcess closes a handle; it does not end an OS
            // process. And because Open() throws, no caller ever received the recorder, so that
            // surviving ffmpeg - holding the webcam and writing camera.mp4 - was unreachable for the
            // life of the process.
            //
            // The guarantee asserted here is a PRESENCE, not the absence of a complaint: after the
            // failed open the process handle is still held and the recorder is still usable, so its
            // owner has something left to try. Bad result this fires on: Disposes == 1, i.e. the
            // handle to a live ffmpeg was thrown away. EMPTY result is impossible - Open either
            // throws or returns, and both are asserted.
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
                KillEndsIt = false,
            };
            var rec = Create(proc, openTimeoutSeconds: 0.2);

            var ex = Assert.Throws<CameraStopFailedException>(() => rec.Open());

            Assert.Equal("HD Webcam eMeet C960", ex.DeviceName);
            Assert.Contains("could not be opened", ex.Message);
            Assert.Contains("STILL RUNNING", ex.Message);
            Assert.Equal(1, proc.Kills);
            Assert.False(proc.HasExited, "the fake ffmpeg is meant to survive this kill - otherwise the test proves nothing");
            Assert.Equal(0, proc.Disposes);   // the handle to a LIVE process is the only way back to it

            // ... and the owner really can try again, which is what keeping the handle is FOR.
            rec.Dispose();
            Assert.Equal(2, proc.Kills);
            Assert.Equal(0, proc.Disposes);
        }

        [Fact]
        public void Open_WhenTheKillItselfThrows_KeepsTheProcessHandleForARetry()
        {
            // The other arm the gate named: Kill throwing (access denied, or an already-exiting
            // race) must be judged by the WAIT that follows, not by the fact that a kill was issued.
            // Bad result: a UsageException that reads like an ordinary failed open, or a disposed
            // wrapper.
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
                KillEndsIt = false,
                KillThrows = true,
            };
            var rec = Create(proc, openTimeoutSeconds: 0.2);

            var ex = Assert.Throws<CameraStopFailedException>(() => rec.Open());

            Assert.Contains("STILL RUNNING", ex.Message);
            Assert.Equal(1, proc.Kills);
            Assert.False(proc.HasExited);
            Assert.Equal(0, proc.Disposes);
        }

        [Fact]
        public void Open_WhenTheStalledFfmpegDiesOnTheRetry_ReleasesTheCameraAndTheHandle()
        {
            // POSITIVE CONTROL for the two above, and the reason they cannot be satisfied by a
            // recorder that simply never disposes anything. The first kill is refused; the owner
            // retries through Dispose, the process dies, and only THEN is the handle released.
            //
            // Bad result: Disposes == 0 after the process is confirmed gone (a handle leaked on the
            // recovery path), or HasExited == false (the retry never terminated anything).
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
                KillEndsIt = false,
            };
            var rec = Create(proc, openTimeoutSeconds: 0.2);
            Assert.Throws<CameraStopFailedException>(() => rec.Open());

            proc.KillEndsIt = true;      // the second kill lands
            rec.Dispose();

            Assert.Equal(2, proc.Kills);
            Assert.True(proc.HasExited, "the retry must actually terminate the process");
            Assert.Equal(1, proc.Disposes);
        }

        [Fact]
        public void Open_CalledTwice_RefusesToStartASecondFfmpeg()
        {
            // The two-phase split hands the caller an object between Create and Open, so "open it
            // again" becomes expressible for the first time. A second Start on the same recorder
            // would put a second ffmpeg on the camera with only one handle to reach it.
            var proc = new FakeCameraProcess();
            var rec = StartOver(proc);

            Assert.Throws<InvalidOperationException>(() => rec.Open());
            rec.Dispose();
        }

        [Fact]
        public void Start_WhenFfmpegExitsWithCodeZeroDuringTheProbe_FailsTheStart()
        {
            // The gate named this case explicitly: the old probe rejected only a NON-ZERO exit, so an
            // ffmpeg that exited 0 during the probe was marked opened, and the Exited handler ignored
            // it because _opened was still false - a camera that recorded nothing, reported by
            // nobody. A process that has ended is not recording, whatever it exited with.
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
                ExitsOnStartWith = 0,
            };

            var ex = Assert.Throws<UsageException>(() => StartOver(proc));

            Assert.Contains("exited with code 0", ex.Message);
        }

        [Fact]
        public void Start_WhenFfmpegReportsTheCameraAndTheFileOpen_OpensTheCamera()
        {
            // The POSITIVE control for the three above. Without it a probe that rejected everything
            // would pass all of them - the tests would prove only that starting a camera is
            // impossible.
            var proc = new FakeCameraProcess();

            using var rec = StartOver(proc);

            Assert.True(proc.Started);
            Assert.Equal("HD Webcam eMeet C960", rec.DeviceName);
            Assert.Equal(0.5, rec.CapturedSeconds, 3);   // the tick's own time=, not wall time
            Assert.False(rec.LostMidRun);
            Assert.Equal(0, proc.Kills);
        }

        [Fact]
        public void Start_DoesNotHoldTheRecordingStartWaitingForTheFirstEncodedFrame()
        {
            // THE AC3 REGRESSION (QA round 2). Round 2 fixed the probe by waiting for ffmpeg's first
            // progress tick. That tick reports ENCODED output, and libx264 frame-threading holds the
            // first encoded frame back by SECONDS - 2.6s on the eMeet C960 - while the camera is
            // already filming. The screen recorder is started only after the probe returns, so every
            // millisecond the probe spends is head footage camera.mp4 carries and recording.mp4 does
            // not: the two files came out 2.37s apart against AC3's hard 1.0s limit.
            //
            // So the criterion lives here, in the only place a unit test can hold it: the start must
            // complete on ffmpeg's OPEN report and must not wait for encoded output. The budget is
            // AC3's own 1.0s.
            //
            // Bad result this fires on: StartOver blocks (and then throws UsageException when the
            // deadline passes) because no progress tick is ever emitted - exactly what the round-2
            // code does with this fake.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false };

            var sw = Stopwatch.StartNew();
            using var rec = StartOver(proc, openTimeoutSeconds: 10.0);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.0),
                $"the start blocked for {sw.Elapsed.TotalSeconds:F2}s after ffmpeg reported the camera open - "
                + "every second of that is head footage on camera.mp4 that recording.mp4 does not have, and "
                + "AC3 allows the two durations to differ by at most 1.0s");
            Assert.Equal(0.0, rec.CapturedSeconds, 3);   // no tick yet: opened is not the same as encoded
            Assert.False(rec.LostMidRun);
            Assert.Equal(0, proc.Kills);
        }

        [Fact]
        public void Start_WhenOnlyAProgressTickArrives_DoesNotCountThatAsAnOpenCamera()
        {
            // The other side of the same line, and the guard against "fixing" the regression by
            // accepting EITHER signal. The open probe's presence is ffmpeg's two headers; a progress
            // tick on its own is not a report that this camera and this file were opened.
            //
            // Bad result: StartOver returns.
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = true,
            };

            var ex = Assert.Throws<UsageException>(() => StartOver(proc, openTimeoutSeconds: 0.2));

            Assert.Contains("never reported the camera open", ex.Message);
        }

        [Fact]
        public void Start_WhenFfmpegOpensTheCameraButNeverOpensTheOutputFile_FailsTheStart()
        {
            // Both headers are required: a camera ffmpeg that opened the device and then could not
            // open camera.mp4 is holding the webcam and writing nothing. Half a presence is not one.
            //
            // Bad result: StartOver returns.
            var proc = new FakeCameraProcess { ReportsOutputOpenOnStart = false, ReportsProgressOnStart = false };

            var ex = Assert.Throws<UsageException>(() => StartOver(proc, openTimeoutSeconds: 0.2));

            Assert.Contains("opened the camera but never opened camera.mp4", ex.Message);
            Assert.Equal(1, proc.Kills);
        }

        [Fact]
        public void TheOpenReport_IsFfmpegsTwoHeadersAndNothingElse()
        {
            // The instrument behind the probe. If either predicate answered true for a line ffmpeg
            // prints on the way to FAILING, the probe would open a camera that never opened; if it
            // answered false for the real header, it would fail a working one. Both directions are
            // asserted, on verbatim ffmpeg output.
            Assert.True(FfmpegCameraRecorder.IsInputOpenReport(InputOpenReport));
            Assert.True(FfmpegCameraRecorder.IsOutputOpenReport(OutputOpenReport));

            Assert.False(FfmpegCameraRecorder.IsInputOpenReport(OutputOpenReport));
            Assert.False(FfmpegCameraRecorder.IsOutputOpenReport(InputOpenReport));

            foreach (string notAnOpenReport in new[]
            {
                ProgressTick,
                null!,
                "",
                "[dshow @ 000001c99aa571c0] Could not run graph (sometimes caused by a device already in use by other application)",
                "[in#0 @ 000001c99aa56f80] Error opening input: I/O error",
                "Error opening input file video=HD Webcam eMeet C960.",
                // a NON-dshow input: this recorder only ever captures a camera, so an input header
                // for anything else is not the camera reporting itself open.
                "Input #0, lavfi, from 'testsrc':",
                "  Stream #0:0: Video: mjpeg (Baseline), yuvj422p, 1920x1080, 30 fps",
            })
            {
                Assert.False(FfmpegCameraRecorder.IsInputOpenReport(notAnOpenReport),
                    $"\"{notAnOpenReport}\" was read as ffmpeg reporting the camera open");
                Assert.False(FfmpegCameraRecorder.IsOutputOpenReport(notAnOpenReport),
                    $"\"{notAnOpenReport}\" was read as ffmpeg reporting camera.mp4 open");
            }
        }

        [Fact]
        public void Start_WhenTheCameraIsHeldByAnotherApplication_StillNamesTheRealCause()
        {
            // The failure a user actually hits first - a webcam held by a browser or OBS - must keep
            // its one actionable sentence through the rewritten probe. Verbatim ffmpeg 9.0 stderr.
            var proc = new FakeCameraProcess
            {
                ReportsInputOpenOnStart = false,
                ReportsOutputOpenOnStart = false,
                ReportsProgressOnStart = false,
                StderrOnStart = new[]
                {
                    "[dshow @ 000001c99aa571c0] Could not run graph (sometimes caused by a device already in use by other application)",
                    "[in#0 @ 000001c99aa56f80] Error opening input: I/O error",
                },
                ExitsOnStartWith = 1,
            };

            var ex = Assert.Throws<UsageException>(() => StartOver(proc));

            Assert.Contains("exited with code 1", ex.Message);
            Assert.Contains("already in use by another application", ex.Message);
        }

        // ---- gate defect 2: a stop that could not terminate ffmpeg is NOT a clean stop ----------

        [Fact]
        public void Stop_WhenFfmpegSurvivesTheQuitAndTheKill_ThrowsInsteadOfReportingSuccess()
        {
            // THE DEFECT. The timeout was a warning, the failed kill was swallowed, and the second
            // wait's result was ignored - so Stop returned normally while ffmpeg was still running.
            // RecordingService then set the state to idle and released the capture claim with the
            // webcam and camera.mp4 still owned by that process.
            //
            // Bad result this fires on: Stop returns.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            using var rec = StartOver(proc);

            var ex = Assert.Throws<CameraStopFailedException>(() => rec.Stop());

            Assert.Equal("HD Webcam eMeet C960", ex.DeviceName);
            Assert.Contains("STILL RUNNING", ex.Message);
            Assert.Equal(1, proc.Quits);
            Assert.Equal(1, proc.Kills);
        }

        [Fact]
        public void Stop_WhenTheKillItselfThrows_StillReportsTheStopAsFailed()
        {
            // The swallowed-kill arm on its own: Kill throwing must not become a clean stop either.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false, KillThrows = true };
            using var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
        }

        [Fact]
        public void Stop_AfterAFailedTermination_DisposeKeepsTheProcessReachableInsteadOfAbandoningIt()
        {
            // THIS TEST WAS ITSELF A DEFECT, and the Review Gate said so in round 3: it set
            // KillEndsIt = false, then asserted only that a second kill had been ATTEMPTED and that
            // the wrapper had been disposed. Both of those are true whether or not ffmpeg died, so
            // it certified a lifetime guarantee it never checked - a check that FAILS OPEN. It is
            // rewritten here to assert the guarantee itself, and it fails against the code it was
            // written for.
            //
            // THE GUARANTEE: while the camera ffmpeg is still alive, the recorder keeps the ONLY
            // thing that can still reach it. Dispose retries the stop and, when that retry also
            // cannot confirm the process gone, it KEEPS the process handle - because
            // ICameraProcess.Dispose closes a handle and terminates nothing, so releasing it there
            // converts a reported failure into an invisible one while RecordingService goes back to
            // idle and releases the recording claim.
            //
            // Bad results this fires on: Kills == 1 (no retry at all), or Disposes == 1 (the handle
            // to a LIVE process was released - which is exactly what the round-2 code did, and what
            // the old assertion demanded).
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            rec.Dispose();

            Assert.False(proc.HasExited,
                "this test only means anything while the fake ffmpeg is still alive after both kills");
            Assert.Equal(2, proc.Kills);
            Assert.Equal(0, proc.Disposes);

            // And the object is still a working handle on that process, not a husk: a third attempt
            // is available to whoever reads the failure off /status.
            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            Assert.Equal(3, proc.Kills);
        }

        [Fact]
        public void Stop_WhenTheRetryFinallyTerminatesFfmpeg_DisposeReleasesTheHandle()
        {
            // POSITIVE CONTROL for the test above - without it, a Dispose that NEVER released the
            // handle would pass, and the recorder would leak a Process object on every clean
            // recording. Here the second kill lands, so the process is confirmed gone and the handle
            // is released exactly once.
            //
            // Bad result: Disposes == 0 (a handle leaked once the process really is dead).
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            proc.KillEndsIt = true;        // the second kill lands
            rec.Dispose();

            Assert.Equal(2, proc.Kills);
            Assert.True(proc.HasExited);
            Assert.Equal(1, proc.Disposes);

            rec.Dispose();                 // and disposing again is a no-op, not a second release
            Assert.Equal(1, proc.Disposes);
        }

        [Fact]
        public void Stop_WhenFfmpegQuitsCleanly_ReportsACleanStopAndNeverKills()
        {
            // POSITIVE control for the two above: an ffmpeg that answers "q" must not be killed and
            // must not throw. Without this, a Stop that always threw would pass them both.
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.False(rec.LostMidRun);
            Assert.True(File.Exists(LogPath), "a clean stop writes the ffmpeg log beside camera.mp4");
        }

        [Fact]
        public void Stop_CalledTwiceAfterACleanStop_DoesNothingTheSecondTime()
        {
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc);

            rec.Stop();
            rec.Stop();

            Assert.Equal(1, proc.Quits);
        }

        // ---- gate defect 4: a real mid-run loss must never be recorded as a clean track ---------

        [Fact]
        public void Stop_WhenTheCameraDiedWithoutItsExitCallbackDelivered_RecordsTheTrackAsLost()
        {
            // THE DEFECT. Stop set _stopped BEFORE observing the process, which suppressed the Exited
            // handler; it then computed a local `lost` that was only ever used in a log line and
            // never assigned to _lostMidRun. So a camera that died a moment before the user stopped -
            // exit callback not yet delivered - produced LostMidRun == false, the manifest wrote
            // CameraTruncated: false, and the required warning was omitted for a camera file that
            // ends early. An editor is told the take is complete when it is not.
            //
            // Bad result this fires on: LostMidRun == false.
            var proc = new FakeCameraProcess { DeliversExitCallback = false };
            using var rec = StartOver(proc);
            proc.Emit("frame=  300 fps= 30 q=28.0 size=    2048KiB time=00:00:10.00 bitrate=1677.7kbits/s speed=1x");

            proc.End(1);          // the camera dies; nothing tells the recorder
            rec.Stop();

            Assert.True(rec.LostMidRun, "a camera that ended before the stop is a TRUNCATED track");
            Assert.Equal(10.0, rec.CapturedSeconds, 3);
        }

        [Fact]
        public void Stop_WhenTheCameraDiedAfterOpeningWithoutDeliveringAFrame_StillReportsTheLoss()
        {
            // The case the open probe hands to decision 4 instead of failing the start, on the arm
            // that was already covered: ffmpeg reported the camera and the file open, produced no
            // encoded output at all, and then DIED. The track is reported LOST, camera.mp4 is marked
            // truncated in the manifest, and it carries the honest 0.0s.
            //
            // Bad result: LostMidRun == false, i.e. the manifest tells an editor the take is complete.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false };
            using var rec = StartOver(proc);

            proc.End(1);
            rec.Stop();

            Assert.True(rec.LostMidRun, "a camera that opened and then produced nothing is a TRUNCATED track");
            Assert.Equal(0.0, rec.CapturedSeconds, 3);
        }

        [Fact]
        public void Stop_WhenALIVECameraOpenedAndNeverDeliveredAFrame_StillReportsTheLoss()
        {
            // THE DEFECT (gate round 3, defect 3). The test above calls End(1) FIRST, so it only
            // ever walked the already-exited path - and that is the ONE path the code checked. The
            // path the header-based open probe actually created was never covered: ffmpeg prints
            // both headers, the camera or its driver then stalls WITHOUT producing any "time="
            // progress while the process stays perfectly alive, and later answers "q" like a healthy
            // recorder. That stop recorded CapturedSeconds == 0 and LostMidRun == false, so the
            // manifest wrote "cameraTruncated": false over an EMPTY camera.mp4 and emitted no loss
            // warning. The user finds out in an editor.
            //
            // "The device opened" is not "the device produced video". The only evidence camera.mp4
            // contains anything is ffmpeg's own report that it wrote output, so its ABSENCE is a
            // lost track whether the process is alive or dead.
            //
            // Bad result this fires on: LostMidRun == false. Empty result: impossible - LostMidRun
            // is a bool read after a Stop that is asserted not to throw.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false };
            using var rec = StartOver(proc);

            Assert.False(proc.HasExited, "the subject of this test is a camera that is STILL RUNNING at the stop");

            rec.Stop();                    // answers "q" normally, exits 0, like a healthy camera

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);   // nothing was wrong with the PROCESS; the FILE is empty
            Assert.True(rec.LostMidRun,
                "a camera that opened and then never reported writing a frame leaves an EMPTY camera.mp4 - "
                + "recording it as a complete take tells an editor the file is good");
            Assert.Equal(0.0, rec.CapturedSeconds, 3);
        }

        [Fact]
        public void Stop_WhenTheCameraReportedOnlyAZeroOutputPosition_StillReportsTheLoss()
        {
            // The same rule one notch finer. ffmpeg prints progress ticks before it has encoded
            // anything - "time=N/A" first, then "time=00:00:00.00" - so counting ANY tick as
            // "it wrote output" would re-open the hole with an extra step. Only a POSITIVE output
            // position is evidence that camera.mp4 has content.
            //
            // Bad result: LostMidRun == false.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false };
            using var rec = StartOver(proc);
            proc.Emit("frame=    0 fps=0.0 q=0.0 size=       0KiB time=N/A bitrate=N/A speed=N/A");
            proc.Emit("frame=    0 fps=0.0 q=0.0 size=       0KiB time=00:00:00.00 bitrate=N/A speed=0x");

            rec.Stop();

            Assert.True(rec.LostMidRun, "a tick at position zero is not a frame");
            Assert.Equal(0.0, rec.CapturedSeconds, 3);
        }

        [Fact]
        public void Stop_ReadsTheZeroFrameVerdictFromCOMPLETEStderr()
        {
            // The instrument behind the rule above, and the reason it is not itself a check that
            // fails open. "No progress tick arrived" is only an absence once ffmpeg's stderr is
            // finished: Process.WaitForExit(int) does not flush the asynchronous readers, so a real
            // camera's LAST tick can still be in flight at the moment the process is already gone,
            // and a stop that judged the track right then would call a good short recording empty.
            //
            // Bad result: Drains == 0, i.e. the verdict was reached without waiting for the stream
            // to end.
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.Equal(1, proc.Drains);
            Assert.False(rec.LostMidRun);   // and the drained stderr DID carry a tick, so it is a good take
        }

        [Fact]
        public void Stop_WhenTheStderrNeverReachesEndOfStream_DrawsNoConclusionFromTheUnfinishedRead()
        {
            // The third arm of the drain, CORRECTED (gate round 4, defect 3). This test used to
            // require LostMidRun == true here, and the gate was right that it codified an overclaim:
            // "we could not read it" must not become "it was fine", and it must not become "the
            // camera was lost" either. Both are claims, and the recorder holds evidence for neither.
            //
            // The honest result is the absence of a diagnosis: nothing is asserted about the file,
            // the incomplete read IS recorded (StderrComplete false), and the verdict is "unknown".
            //
            // Bad result: LostMidRun == true (the old overclaim), or a Completeness of yes or no.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false, StderrReachesEof = false };
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.Equal(1, proc.Drains);
            Assert.False(rec.StderrComplete);
            Assert.False(rec.LostMidRun);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenTheExitCallbackWasDelivered_StillRecordsTheTrackAsLost()
        {
            // The same loss on the path that already worked, so the fix cannot be "the callback is
            // now the only route".
            var proc = new FakeCameraProcess { DeliversExitCallback = true };
            using var rec = StartOver(proc);

            proc.End(1);
            rec.Stop();

            Assert.True(rec.LostMidRun);
        }

        [Fact]
        public void Stop_AfterANormalQuit_DoesNotMarkTheTrackAsLost()
        {
            // POSITIVE control: the deliberate stop ends the process too, and that must NOT be
            // reported as a mid-run loss. Without this a recorder that always said "lost" would pass
            // both tests above and every clean recording would carry CameraTruncated: true.
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.False(rec.LostMidRun);
        }

        // ---- the 2026-08-28 spec amendment: OBSERVE, do not claim (AC10, AC13 - AC17) ----------
        //
        // These are the three cases the Review Gate REPRODUCED with its own probe against this exact
        // seam, plus the positive control that stops the fix degenerating into "always unknown".
        // Every one of them used to come out of Stop() as a clean, complete take:
        //
        //   ONE_TICK_STALL             alive_before_stop=True quits=1 kills=0 drains=1 captured=0.5
        //   ONE_TICK_INCOMPLETE_STDERR alive_before_stop=True quits=1 kills=0 drains=1 captured=0.5
        //   FORCED_KILL_AFTER_OUTPUT   alive_before_stop=True quits=1 kills=1 drains=1 captured=0.5
        //
        // The counts in each test below are asserted to MATCH the gate's, so these are the gate's
        // cases and not three easier ones wearing their names.

        [Fact]
        public void Stop_WhenTheCameraTickedOnceAndThenStalledForTheSession_IsNeverRecordedAsComplete()
        {
            // GATE CASE ONE_TICK_STALL (AC13). ffmpeg reported both headers and ONE progress tick at
            // 0.5s, the device then stalled for the remaining thirty seconds while the process
            // stayed perfectly alive, and it answered "q" at the stop like a healthy recorder.
            //
            // The old rule latched _wroteOutput on any positive tick and asked no further questions,
            // so this produced CapturedSeconds == 0.5, LostMidRun == false and a manifest reading
            // "cameraTruncated": false - a thirty-second session recorded as a complete half-second
            // take. Nothing about the process was wrong; the FILE is the thing that was wrong, and
            // the two had been conflated.
            //
            // What is asserted is the ABSENCE OF A CLAIM, not the presence of a diagnosis: the
            // recorder does not know whether those missing frames are buffered or gone, so "unknown"
            // is the honest answer and "yes" is the forbidden one.
            //
            // Bad result this fires on: Completeness == Yes. Empty result: impossible - Completeness
            // is a three-state enum read after a Stop asserted not to throw.
            var clock = new TestClock();
            var proc = new FakeCameraProcess();          // headers + one tick at 0.5s on start
            using var rec = StartOver(proc, clock: clock);

            clock.Advance(TimeSpan.FromSeconds(30));     // ... and then nothing, for the whole session
            rec.Stop();

            // The gate's own counts, so this is provably its case.
            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(1, proc.Drains);
            Assert.Equal(0.5, rec.CapturedSeconds, 3);
            Assert.False(rec.LostMidRun);

            // Everything the recorder DID observe is recorded, and it is all true.
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.True(rec.StderrComplete);

            // And the one judgement it makes refuses to go past the evidence.
            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenTheOutputKeptAdvancingUntilTheStop_RecordsTheTakeAsComplete()
        {
            // AC17, AT THE UNIT LEVEL - THE CONTROL THAT MAKES THE TEST ABOVE MEAN SOMETHING. A
            // recorder that answered "unknown" to everything would satisfy AC13, AC14, AC15 and
            // AC16 while telling the user precisely nothing, and that is a fail-open fix. So a
            // healthy camera - ticks advancing right up to the stop, a clean quit, stderr to end of
            // stream - must still come out as "yes", in this same build.
            //
            // Bad result this fires on: Completeness != Yes, i.e. the fix degenerated into a
            // recorder that can no longer vouch for anything.
            var clock = new TestClock();
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc, clock: clock);

            // Thirty seconds of ordinary recording: half a second of wall time per tick, and the
            // output position moves with it.
            for (int i = 1; i <= 60; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(0.5));
                proc.Emit(TickAt(TimeSpan.FromSeconds(0.5 * i)));
            }

            rec.Stop();

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(30.0, rec.CapturedSeconds, 3);
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.True(rec.StderrComplete);
            Assert.Equal(CameraCompleteness.Yes, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenAStalledCameraFlushesOutputOnlyAfterTheStop_IsStillNeverRecordedAsComplete()
        {
            // THE SECOND TRAP, and the reason the freshness clause reads a SNAPSHOT taken when the
            // stop was requested rather than the live value. ffmpeg flushes what it is holding when
            // it is told to quit, so a camera that stalled for the whole session can still push its
            // position forward on the way OUT. Judging freshness after that flush would let the
            // parting tick certify the stall - the same false-clean verdict, one step further along.
            //
            // The question is whether this camera was still recording WHEN THE USER STOPPED IT, and
            // that can only be answered from evidence that existed at that moment.
            //
            // Bad result: Completeness == Yes. The seconds it did flush are still recorded honestly.
            var clock = new TestClock();
            var proc = new FakeCameraProcess { QuitEndsIt = false };   // headers + one tick at 0.5s
            var rec = StartOver(proc, clock: clock);

            clock.Advance(TimeSpan.FromSeconds(30));    // stalled for the whole session
            proc.QuitEndsItWith = () =>
            {
                clock.Advance(TimeSpan.FromSeconds(0.2));
                proc.Emit(TickAt(TimeSpan.FromSeconds(0.9)));   // ... and flushes a little on the way out
                proc.End(0);
            };

            rec.Stop();

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(0.9, rec.CapturedSeconds, 3);   // what it flushed IS reported - honestly
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenAStalledCameraRepeatsItsPositionOnTheWayOut_IsStillNeverRecordedAsComplete()
        {
            // THE TRAP THE FRESHNESS RULE HAD TO AVOID, pinned so a later "simplification" cannot
            // walk back into it. ffmpeg prints a FINAL summary line when it quits, and a stalled
            // camera's summary repeats the position it stalled at. A freshness check written against
            // tick ARRIVAL would read that repeat as "output was still arriving at the stop" and
            // certify the stall - the same false-clean result with one extra step.
            //
            // Only an ADVANCE counts. Bad result: Completeness == Yes.
            var clock = new TestClock();
            var proc = new FakeCameraProcess();          // one tick at 0.5s
            using var rec = StartOver(proc, clock: clock);

            clock.Advance(TimeSpan.FromSeconds(30));
            proc.Emit(TickAt(TimeSpan.FromSeconds(0.5)));   // ffmpeg's parting summary: same position

            rec.Stop();

            Assert.Equal(0.5, rec.CapturedSeconds, 3);
            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenFfmpegIgnoredTheQuitAndWasForceKilled_ReportsItInsteadOfReturningCleanly()
        {
            // GATE CASE FORCED_KILL_AFTER_OUTPUT (AC14). ffmpeg produced video, ignored "q", and
            // died under the kill. The PROCESS question was answered correctly - it really is gone -
            // but no path recorded that camera.mp4 had been SHOT rather than asked, so ffmpeg never
            // wrote the MP4 trailer while the durable manifest said the take was complete. The
            // code's own warning line said the file may be truncated at the same moment.
            //
            // Two things are required now and both are asserted: the stop SURFACES the condition to
            // its caller instead of returning as a clean success, and the file is never claimed
            // complete.
            //
            // Bad result this fires on: Stop returns, or Completeness == Yes.
            var proc = new FakeCameraProcess { QuitEndsIt = false };   // ... but the kill lands
            using var rec = StartOver(proc);

            var ex = Assert.Throws<CameraForceKilledException>(() => rec.Stop());

            Assert.Equal("HD Webcam eMeet C960", ex.DeviceName);
            Assert.Contains("force-killed", ex.Message);
            Assert.Contains("may be truncated", ex.Message);

            // The gate's own counts for this case.
            Assert.Equal(1, proc.Quits);
            Assert.Equal(1, proc.Kills);
            Assert.Equal(1, proc.Drains);
            Assert.True(proc.HasExited);
            Assert.Equal(0.5, rec.CapturedSeconds, 3);

            Assert.Equal(CameraStopKind.ForceKilled, rec.StopKind);
            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.No, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenTheStderrNeverReachedEndOfStreamAfterAPositiveTick_IsNeverRecordedAsComplete()
        {
            // GATE CASE ONE_TICK_INCOMPLETE_STDERR (AC15). The drain was already being called and
            // its failure was already being LOGGED - but the result was thrown away, and the only
            // conclusion that consulted it was the zero-frame one, which does not run once any
            // positive tick exists. So a camera whose evidence was explicitly INCOMPLETE was still
            // accepted as a complete take.
            //
            // An unreadable instrument is not a clean run. Bad result: Completeness == Yes, or
            // StderrComplete reported as true.
            var proc = new FakeCameraProcess { StderrReachesEof = false };   // headers + one tick
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(1, proc.Drains);
            Assert.Equal(0.5, rec.CapturedSeconds, 3);
            Assert.False(rec.LostMidRun);          // the old rule has nothing to say here

            Assert.False(rec.StderrComplete);
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        // ---- gate round 4: the four cases its probe reproduced against this exact seam ---------
        //
        // Round 5 was rejected because three of these still reached the manifest as claims the
        // recorder had not established, and one asserted a liveness it no longer had. The gate's own
        // probe output, verbatim, is the specification of each test below:
        //
        //   ONE_TICK_STALL_2_9S         captured=0.5 stopKind=clean-quit stderrComplete=True  complete=yes
        //   ZERO_TICK_INCOMPLETE_STDERR captured=0   stopKind=clean-quit stderrComplete=False complete=no
        //   FAILED_QUIT_THEN_ERROR_EXIT captured=1   stopKind=clean-quit stderrComplete=True  complete=yes
        //   RETAINED_PROCESS_DIED       hasExited=True isAbandoned=True cameraStuck=True pid=4242
        //
        // Each of the first three is asserted with the gate's own counts, so it is provably its case
        // and not an easier one wearing its name.

        [Fact]
        public void Stop_WhenTheQuitCouldNotEvenBeDelivered_IsNeverRecordedAsACleanQuit()
        {
            // GATE CASE FAILED_QUIT_THEN_ERROR_EXIT (round 4, defect 1). ffmpeg is alive at the
            // pre-stop check, then dies while the write of "q" to its stdin fails, and its exit
            // callback is suppressed because the stop had already been requested. The stop caught
            // the write failure, LOGGED it, and then read WaitForExit() == true as proof that ffmpeg
            // had ANSWERED a quit it never received - so a process that crashed was recorded as
            // clean-quit and the manifest said the take was complete.
            //
            // A quit that was never delivered cannot have been answered. The recorder did watch this
            // process end, but it did not observe HOW, and the amended contract sends every
            // unanticipated case to "unknown" rather than to the friendliest of the four kinds.
            //
            // Bad result this fires on: StopKind == CleanQuit, or Completeness == Yes. Empty result:
            // impossible - both are read after a Stop that is asserted not to throw.
            var proc = new FakeCameraProcess { DeliversExitCallback = false };
            using var rec = StartOver(proc);
            proc.Emit(TickAt(TimeSpan.FromSeconds(1)));
            proc.QuitFailsWith = () => proc.End(-5);      // it crashes as the pipe goes

            rec.Stop();

            // The gate's own counts for this case.
            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(1, proc.Drains);
            Assert.Equal(1.0, rec.CapturedSeconds, 3);
            Assert.True(rec.StderrComplete);
            Assert.True(proc.HasExited);

            Assert.NotEqual(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenFfmpegWasTerminatedAbnormallyUnderTheQuit_IsNeverRecordedAsACleanQuit()
        {
            // The same defect through its other signal, which the gate named explicitly: the stop
            // "never reads the exit code on the stop path". A NEGATIVE exit code is the operating
            // system reporting an abnormal termination - an NTSTATUS such as 0xC0000005 surfacing as
            // a negative int - so that process did not run its own exit path and cannot have written
            // the MP4 trailer, whatever the timing of the quit looked like.
            //
            // Bad result: StopKind == CleanQuit, or Completeness == Yes.
            var proc = new FakeCameraProcess { QuitEndsIt = false, DeliversExitCallback = false };
            using var rec = StartOver(proc);
            proc.Emit(TickAt(TimeSpan.FromSeconds(1)));
            proc.QuitEndsItWith = () => proc.End(-1073741819);   // 0xC0000005

            rec.Stop();

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.True(proc.HasExited);
            Assert.NotEqual(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenADeliveredQuitEndedTheProcessNormally_IsStillRecordedAsACleanQuit()
        {
            // THE CONTROL FOR THE TWO ABOVE, and it is AC17's clause in miniature. A rule that
            // refused "clean-quit" whenever anything at all was unusual would satisfy both tests
            // above and destroy the positive control - every healthy recording would become
            // "unknown", which is the fail-open fix wearing the opposite mask.
            //
            // ffmpeg is NOT pinned to one build here (FfmpegLocator will take a bundled, PATH or
            // winget ffmpeg), and different builds answer "q" with 0 or with 255, so a NON-NEGATIVE
            // code is deliberately not held against the take: what is required is that the quit was
            // delivered and that the process was not terminated abnormally.
            //
            // Bad result: StopKind != CleanQuit for a quit that was delivered and answered.
            var proc = new FakeCameraProcess { QuitEndsIt = false };
            using var rec = StartOver(proc);
            proc.Emit(TickAt(TimeSpan.FromSeconds(1)));
            proc.QuitEndsItWith = () => proc.End(255);   // ffmpeg's own "interrupted, but I exited"

            rec.Stop();

            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
        }

        [Fact]
        public void Stop_WhenTheCameraTickedOnceAndStalledInsideTheFreshnessWindow_IsNeverRecordedAsComplete()
        {
            // GATE CASE ONE_TICK_STALL_2_9S (round 4, defect 2). The freshness rule asked only
            // whether the LAST advance was recent, so a camera that advanced ONCE, at 0.5s, and then
            // stalled for 2.9 seconds until the stop still walked through the three-second window
            // and earned "yes". The gate's words: it "never establishes that ticks CONTINUED after
            // the first one".
            //
            // One advance is a camera that started. It is not a camera that was recording. The rule
            // now needs both halves of the same presence: the output moved forward MORE THAN ONCE,
            // and it was still moving when the user asked to stop.
            //
            // Bad result this fires on: Completeness == Yes. Empty result: impossible - Completeness
            // is a three-state enum read after a Stop asserted not to throw.
            var clock = new TestClock();
            var proc = new FakeCameraProcess();          // headers + ONE tick at 0.5s on start
            using var rec = StartOver(proc, clock: clock);

            clock.Advance(TimeSpan.FromSeconds(2.9));    // inside the window, and nothing more arrives
            rec.Stop();

            // The gate's own observations for this case.
            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(1, proc.Drains);
            Assert.Equal(0.5, rec.CapturedSeconds, 3);
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.True(rec.StderrComplete);

            Assert.NotEqual(CameraCompleteness.Yes, rec.Completeness);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenTheOutputAdvancedTwiceAndWasStillFresh_IsRecordedAsComplete()
        {
            // THE CONTROL FOR THE TEST ABOVE. "Ticks continued after the first one" must mean
            // exactly that and nothing stricter: a rule that demanded a large number of advances
            // would pass the stall test above while quietly making every SHORT healthy recording
            // "unknown" - AC17 failing by degrees instead of all at once.
            //
            // Two advances inside the window is the smallest recording that is still a recording,
            // and it says "yes".
            //
            // Bad result: Completeness != Yes.
            var clock = new TestClock();
            var proc = new FakeCameraProcess();          // one advance at 0.5s
            using var rec = StartOver(proc, clock: clock);

            clock.Advance(TimeSpan.FromSeconds(0.5));
            proc.Emit(TickAt(TimeSpan.FromSeconds(1.0)));   // ... and a second one
            clock.Advance(TimeSpan.FromSeconds(0.5));

            rec.Stop();

            Assert.Equal(1.0, rec.CapturedSeconds, 3);
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.True(rec.StderrComplete);
            Assert.Equal(CameraCompleteness.Yes, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenNoTickArrivedOnAnINCOMPLETEStderr_DoesNotClaimTheFileIsEmpty()
        {
            // GATE CASE ZERO_TICK_INCOMPLETE_STDERR (round 4, defect 3), and the shape the committed
            // test used to REQUIRE. The evidence: ffmpeg opened both ends, wrote a progress tick that
            // is still sitting in an undrained reader, answered "q", and the drain TIMED OUT.
            //
            // Reading "no tick arrived" off a stream that never reached end of stream is not an
            // absence - it is an unfinished read - so "camera.mp4 is EMPTY" and "the camera was lost"
            // are both claims this recorder is not entitled to make. Shortness is not INDEPENDENTLY
            // known here, and the only honest answer is "unknown".
            //
            // Bad results this fires on: LostMidRun == true (the loss overclaim), Completeness == No
            // (the emptiness overclaim), or Completeness == Yes (the original defect).
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false, StderrReachesEof = false };
            using var rec = StartOver(proc);

            rec.Stop();

            // The gate's own observations for this case.
            Assert.Equal(1, proc.Quits);
            Assert.Equal(0, proc.Kills);
            Assert.Equal(1, proc.Drains);
            Assert.Equal(0.0, rec.CapturedSeconds, 3);
            Assert.False(rec.StderrComplete);

            Assert.False(rec.LostMidRun,
                "an incomplete read of ffmpeg's stderr is not evidence that the camera was lost");
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenNoTickArrivedOnACOMPLETEStderr_StillRecordsTheEmptyFileAsKnownBroken()
        {
            // THE CONTROL FOR THE TEST ABOVE, and the round-3 fix it must not walk back. Once the
            // stderr HAS reached end of stream, "ffmpeg never reported writing a frame" is a real
            // absence: camera.mp4 is empty, that is KNOWN, and it is "no" rather than "unknown".
            //
            // Bad result: Completeness == Unknown, i.e. the incomplete-evidence rule was applied to
            // complete evidence and the empty-file diagnosis was lost.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false };   // StderrReachesEof stays true
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.True(rec.StderrComplete);
            Assert.True(rec.LostMidRun);
            Assert.Equal(CameraCompleteness.No, rec.Completeness);
        }

        [Fact]
        public void IsAbandoned_WhenTheStrandedProcessLaterDiesOnItsOwn_StopsClaimingItIsAlive()
        {
            // GATE CASE RETAINED_PROCESS_DIED (round 4, defect 4), the recorder's half. IsAbandoned
            // read only the stored stop kind and the _terminated flag - never the process - so once
            // a stranded ffmpeg exited by itself, every consumer of that flag went on being told a
            // dead PID was live: /status kept a stuck row and the recording's claim was kept with
            // it, blocking packaging and transcription until some LATER recording happened to run
            // the recovery.
            //
            // "Still running" is a fact about a process, and the process is the only thing that can
            // answer it.
            //
            // Bad result this fires on: IsAbandoned == true while the process has exited. Empty
            // result: impossible - HasExited is asserted first, so the test cannot pass by never
            // reaching the state it is about.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            rec.Dispose();
            Assert.True(rec.IsAbandoned, "the fixture only means anything once the process really is stranded");

            proc.End(0);            // whatever it was waiting on lets go, and it exits by itself

            Assert.True(proc.HasExited);
            Assert.False(rec.IsAbandoned,
                "a process that has exited is not abandoned - reporting it as live keeps a dead PID "
                + "on /status and holds that recording's claim for ever");
        }

        [Fact]
        public void StopKind_WhenTheRetryFindsAnAbandonedProcessGone_StaysAbandonedRatherThanBeingRelabelled()
        {
            // The record has to survive the retry that reads it. Once a stop has observed
            // "abandoned", a later Dispose that finds the process finally gone must not overwrite
            // that with "exited-early": the process did not end before it was asked to, it ignored
            // everything it was asked and outlived the stop. Relabelling it would make the durable
            // observation depend on WHEN somebody next looked.
            //
            // Bad result: StopKind == ExitedEarly, or LostMidRun == true, after the retry.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            Assert.Equal(CameraStopKind.Abandoned, rec.StopKind);

            proc.End(0);            // it dies between the failed stop and the retry
            rec.Dispose();          // the retry, which now finds it gone

            Assert.Equal(CameraStopKind.Abandoned, rec.StopKind);
            Assert.False(rec.LostMidRun);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
            Assert.Equal(1, proc.Disposes);   // and the handle IS released now that it is confirmed gone
        }

        [Fact]
        public void Stop_WhenTheCameraDiedDuringTheRecording_RecordsExitedEarlyAndNotComplete()
        {
            // AC10 as amended. A camera lost mid-run is the one failure that must NOT fail the
            // recording (decision 4) - and it must still be written down as what it was: a track
            // that ended before the user asked, carrying only the seconds ffmpeg reported.
            //
            // Bad result: any stop kind but exited-early, or Completeness != No.
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc);
            proc.Emit(TickAt(TimeSpan.FromSeconds(10)));

            proc.End(1);          // the USB cable moved
            rec.Stop();

            Assert.True(rec.LostMidRun);
            Assert.Equal(10.0, rec.CapturedSeconds, 3);
            Assert.Equal(CameraStopKind.ExitedEarly, rec.StopKind);
            Assert.Equal(CameraCompleteness.No, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenTheCameraNeverReportedWritingAFrame_RecordsCompleteNo()
        {
            // "Never produced a frame" is one of the three things the amendment lists as KNOWN
            // broken, so this is the one stall-shaped case that is "no" rather than "unknown":
            // there is nothing in camera.mp4 to be uncertain about.
            //
            // Bad result: Completeness == Yes or Unknown.
            var proc = new FakeCameraProcess { ReportsProgressOnStart = false };
            using var rec = StartOver(proc);

            rec.Stop();

            Assert.Equal(0.0, rec.CapturedSeconds, 3);
            Assert.Equal(CameraStopKind.CleanQuit, rec.StopKind);
            Assert.Equal(CameraCompleteness.No, rec.Completeness);
        }

        [Fact]
        public void Completeness_BeforeAnyStop_IsUnknownRatherThanAGuess()
        {
            // The default matters as much as the rules. While the recording is running nothing has
            // been observed about how it ends, and the answer to "is this file complete" is not
            // "yes" and not "no".
            var proc = new FakeCameraProcess();
            using var rec = StartOver(proc);

            Assert.Null(rec.StopKind);
            Assert.False(rec.StderrComplete);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
        }

        [Fact]
        public void Stop_WhenFfmpegSurvivesEverything_MarksItselfAbandonedAndKeepsItsProcessId()
        {
            // AC16, the recorder's half. A process that survived the quit, the kill AND the Dispose
            // retry is not a stop that failed once - it is a LIVE ffmpeg on the webcam with no
            // owner. The recorder has to say so in a way something else can act on: a flag that
            // means "still running", and the PID, which is the only field that makes the failure
            // actionable for the person holding the machine.
            //
            // Bad result: IsAbandoned false (the failure is invisible to any owner), a stop kind
            // that reads like a finished stop, or a Completeness of yes/no about a file a live
            // process is still writing.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            rec.Dispose();

            Assert.False(proc.HasExited, "this test only means anything while the fake ffmpeg is still alive");
            Assert.True(rec.IsAbandoned);
            Assert.Equal(CameraStopKind.Abandoned, rec.StopKind);
            Assert.Equal(CameraCompleteness.Unknown, rec.Completeness);
            Assert.Equal(FakeCameraProcess.Pid, rec.ProcessId);
        }

        [Fact]
        public void IsAbandoned_OnceTheProcessIsFinallyGone_IsFalseAgain()
        {
            // POSITIVE CONTROL for the flag above: a recorder that reported itself abandoned forever
            // would keep a claim and a /status row for a process that died minutes ago, and the
            // retry loop would never let go. It is a statement about the process RIGHT NOW.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            Assert.True(rec.IsAbandoned);

            proc.KillEndsIt = true;      // the retry finally lands
            rec.Dispose();

            Assert.False(rec.IsAbandoned);
            Assert.True(proc.HasExited);
        }

        [Fact]
        public void TheManifestSpellings_AreTheFourStopKindsAndTheThreeVerdicts()
        {
            // The wire contract itself (assumption A7). These strings go into manifest.json and are
            // read by people and by tools; a rename here is a breaking change and has to be a
            // deliberate one.
            Assert.Equal("clean-quit", CameraObservation.Text(CameraStopKind.CleanQuit));
            Assert.Equal("force-killed", CameraObservation.Text(CameraStopKind.ForceKilled));
            Assert.Equal("exited-early", CameraObservation.Text(CameraStopKind.ExitedEarly));
            Assert.Equal("abandoned", CameraObservation.Text(CameraStopKind.Abandoned));

            Assert.Equal("yes", CameraObservation.Text(CameraCompleteness.Yes));
            Assert.Equal("no", CameraObservation.Text(CameraCompleteness.No));
            Assert.Equal("unknown", CameraObservation.Text(CameraCompleteness.Unknown));

            // A stop that was never observed is an ABSENT field, not a fifth kind invented here.
            Assert.Null(CameraObservation.Text((CameraStopKind?)null));
            Assert.Equal("clean-quit", CameraObservation.Text((CameraStopKind?)CameraStopKind.CleanQuit));
        }

        // ---- gate defect 1: the CLI owns the camera through a failure boundary ------------------

        [Fact]
        public void TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary()
        {
            // THE DEFECT. `agenteyes video --camera ...` opened the webcam into a nullable local and
            // then ran a hundred lines that can throw - gdigrab opening the screen, the loopback
            // start, sysCap.Start, recorder.Stop, the audio mux, the duration probe, the manifest
            // save - with no finally and no using anywhere on the path. Program.Main reported the
            // error and the command exited, leaving that ffmpeg writing camera.mp4 with the webcam
            // held for the life of the process.
            //
            // Read from IL because a `using` declaration writes no "finally" in the source and a text
            // scan cannot say which call a boundary covers. Bad result: no Finally among the handlers
            // (which is exactly what the merged code produced - a Catch region and nothing else).
            foreach (string opensTheCamera in new[]
            {
                // BOTH halves, because the boundary has to cover the whole of the camera's life in
                // this command: Create is where the local starts pointing at a recorder, Open is
                // where an OS process appears behind it.
                "AgentEyes.Video.FfmpegCameraRecorder::Create",
                "AgentEyes.Video.FfmpegCameraRecorder::Open",
            })
            {
                var sites = CompiledCode.GuardedCalls(
                    CompiledCode.CoreAssembly, "AgentEyes.Commands::Video", opensTheCamera);

                Assert.NotEmpty(sites);
                foreach (var site in sites)
                {
                    Assert.True(site.Handlers.Contains("Finally") || site.Handlers.Contains("Fault"),
                        $"{opensTheCamera} is called at IL offset " + site.Offset + " of AgentEyes.Commands::Video "
                        + $"with only [{string.Join(", ", site.Handlers)}] protecting it - a throw after the camera "
                        + "opened leaves ffmpeg writing camera.mp4 and the webcam held");
                    Assert.Contains("AgentEyes.Video.FfmpegCameraRecorder::Dispose", site.CleanupCalls);
                }
            }
        }

        [Fact]
        public void EveryCallerThatOpensACamera_ConstructsTheRecorderInTheSameMethod()
        {
            // GATE ROUND 3, DEFECT 1, AT THE CALLERS. The stranded ffmpeg was not only a bug inside
            // the recorder: it was unreachable because `Start` both created the recorder AND started
            // the process, so a failure threw before ANY caller held the object -
            // `_camera = ...` in RecordingService and `cameraRec = ...` in Commands both never
            // completed, and the rollback each of them has stopped a null. Splitting the call in two
            // is what fixes that, and it is only a fix while every caller keeps both halves.
            //
            // Bad result this fires on: a method that calls Open without calling Create - i.e. an
            // opener that got its recorder from somewhere else and may not own it. An EMPTY result
            // is a broken instrument: Assert.NotEmpty below fails if nothing opens a camera at all.
            //
            // WHAT IT CANNOT SEE, stated rather than implied: it proves the two calls are in the
            // same method body, not their order and not that the result was stored. Order is
            // enforced by C# itself (Open is an instance method - it cannot run before something
            // produced the instance), and the STORAGE is what
            // TheRecordingService_StoresTheCameraBeforeStartingIt and the Commands boundary test
            // above cover.
            var opens = new List<CompiledCode.CallSite>();
            var creates = new List<CompiledCode.CallSite>();
            foreach (string assembly in CompiledCode.ProductAssemblies())
            {
                opens.AddRange(CompiledCode.CallSites(assembly,
                    c => c == "AgentEyes.Video.FfmpegCameraRecorder::Open"));
                creates.AddRange(CompiledCode.CallSites(assembly,
                    c => c == "AgentEyes.Video.FfmpegCameraRecorder::Create"));
            }

            Assert.NotEmpty(opens);
            var constructors = new HashSet<string>(creates.Select(c => c.Method), StringComparer.Ordinal);
            foreach (var open in opens)
                Assert.True(constructors.Contains(open.Method),
                    $"{open.Assembly}!{open.Method} starts a camera ffmpeg but never constructs the recorder - "
                    + "it cannot be the owner that stops one whose open failed");
        }

        [Fact]
        public void TheRecordingService_StoresTheCameraBeforeStartingIt()
        {
            // The service's half of the same ownership. Its rollback is LiveWriters, which reads the
            // _camera FIELD, so the field must hold the recorder BEFORE the process exists - the
            // rule issue #155 already states for every other writer ("a field is set the moment its
            // writer is constructed, so a writer whose Start threw is still in here").
            //
            // Read from the SOURCE, and that limit is the point of saying so: the ordering of a field
            // store against a call is not something CompiledCode can report today (CallsIn refuses a
            // method whose body the compiler split into lambdas, which this one is). A source scan is
            // defeated by an alias or a helper - so it is paired with the IL check above, which sees
            // the calls wherever they are spelled, and with the behavioural tests that prove the
            // recorder survives a failed Open.
            //
            // Bad result: the store does not appear before the Open, e.g. because the two were merged
            // back into one expression. EMPTY result: RepoSource.Read and MethodBody both throw when
            // the file or the method is gone, so this cannot pass by reading nothing.
            string startVideo = RepoSource.MethodBody(
                RepoSource.Read(Path.Combine("src", "AgentEyes.Core", "RecordingService.cs")),
                "public void StartVideo(");

            int stored = startVideo.IndexOf("_camera = FfmpegCameraRecorder.Create(", StringComparison.Ordinal);
            int opened = startVideo.IndexOf("_camera.Open();", StringComparison.Ordinal);

            Assert.True(stored >= 0,
                "StartVideo no longer stores the camera recorder in _camera as it constructs it - LiveWriters "
                + "reads that field, and a start failure would have nothing to stop");
            Assert.True(opened >= 0,
                "StartVideo no longer opens the camera through the recorder it stored in _camera");
            Assert.True(stored < opened,
                "StartVideo starts the camera ffmpeg BEFORE _camera holds the recorder - a failed open would "
                + "again leave a live ffmpeg on the webcam that the rollback cannot reach");
        }

        [Fact]
        public void TheBoundaryScan_FiresWhenItsTargetIsNotThere()
        {
            // The instrument check for the two structural tests. If GuardedCalls answered "no
            // offenders" for a method or a callee that does not exist, both of them would pass
            // forever after any rename. It throws instead - proven here, not assumed.
            Assert.Throws<InvalidOperationException>(() => CompiledCode.GuardedCalls(
                CompiledCode.CoreAssembly, "AgentEyes.Commands::NoSuchCommand",
                "AgentEyes.Video.FfmpegCameraRecorder::Open"));

            Assert.Throws<InvalidOperationException>(() => CompiledCode.GuardedCalls(
                CompiledCode.CoreAssembly, "AgentEyes.Commands::Video",
                "AgentEyes.Video.FfmpegCameraRecorder::NoSuchMethod"));
        }

        // ---- AC16: the service's two exits go through the lifetime owner, not around it ---------

        [Fact]
        public void TheRecordingService_ReleasesItsClaimOnlyThroughTheStrandedCameraOwner()
        {
            // GATE ROUND 3, DEFECT 1, AT THE SERVICE. The recorder had already been fixed to KEEP
            // its process handle when a stop could not confirm ffmpeg dead - and it changed nothing,
            // because Stop() cleared _camera, dropped the local, went idle and released the claim
            // one line later. The gate's words: "keeping a handle inside an object that immediately
            // becomes unreachable does not keep the process recoverable."
            //
            // The fix is that the decision no longer lives at the call site at all. There is ONE
            // call, to a method that either retains the recorder AND its claim or releases the
            // claim - so "the claim is not released as though the stop were clean" cannot be right
            // on one path and wrong on the other. This pins that: Stop must not reach
            // RecordingWorkset::Release itself.
            //
            // Bad result this fires on: a direct Release in RecordingService::Stop, i.e. the branch
            // was reintroduced at the call site. EMPTY result is impossible - the positive control
            // below proves the scan finds Release calls at all.
            var releases = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c == "AgentEyes.RecordingWorkset::Release");
            var throughTheOwner = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c == "AgentEyes.StrandedCameraOwner::ReleaseClaimUnlessStranded");

            Assert.Contains(throughTheOwner, s => s.Method == "AgentEyes.RecordingService::Stop");
            Assert.DoesNotContain(releases, s => s.Method == "AgentEyes.RecordingService::Stop");

            // The instrument, proven rather than assumed: the scan DOES see Release calls, and the
            // one place that still makes them is the owner itself.
            Assert.NotEmpty(releases);
            Assert.Contains(releases, s => s.Method == "AgentEyes.StrandedCameraOwner::ReleaseClaimUnlessStranded");
        }

        [Fact]
        public void TheRecordingService_DiscardsAFailedStartsDirectoryOnlyThroughTheStrandedCameraOwner()
        {
            // The same rule on the START path, and it is not a duplicate: a failed OPEN can strand
            // ffmpeg just as a failed stop can, and the rollback there does something worse than
            // release a claim - it DELETES the directory. Deleting a directory around a live ffmpeg
            // does not stop the ffmpeg; it fails on the file the process holds open and replaces
            // "the camera is already in use" with an IO error about camera.mp4.
            //
            // Bad result: ReleaseSession calls Discard directly again.
            var discards = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c == "AgentEyes.RecordingStartSequence::Discard");
            var throughTheOwner = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c == "AgentEyes.StrandedCameraOwner::DiscardDirectoryUnlessStranded");

            Assert.Contains(throughTheOwner, s => s.Method == "AgentEyes.RecordingService::ReleaseSession");
            Assert.DoesNotContain(discards, s => s.Method == "AgentEyes.RecordingService::ReleaseSession");

            Assert.NotEmpty(discards);
            Assert.Contains(discards, s => s.Method == "AgentEyes.StrandedCameraOwner::DiscardDirectoryUnlessStranded");
        }

        [Fact]
        public void TheCameraTrackRecord_IsTheOnlyPlaceTheManifestVerdictIsAssigned()
        {
            // The wiring between the recorder's honesty and the durable record. Everything above
            // proves the RECORDER refuses to claim what it did not observe; this proves the manifest
            // the user keeps says what the recorder said.
            //
            // AN EARLIER VERSION OF THIS TEST FAILED OPEN, AND IT IS RECORDED HERE RATHER THAN
            // QUIETLY REPLACED. It asserted that each writer method READ the four observations
            // somewhere in its body - and the mutation that assigns the verdict as a literal left it
            // GREEN, because the same method still read Completeness for a log line two statements
            // later (round-5 mutation M12, first run). A check that survives the defect it names is
            // a defect, not weak coverage.
            //
            // What is asserted now cannot be satisfied that way:
            //
            //  1. the four manifest properties are assigned in EXACTLY ONE method in the whole
            //     product - a literal at either call site means a setter call from a second method,
            //     and that is what fires;
            //  2. that one method reads all four observations off the recorder; and
            //  3. both writers reach it.
            //
            // LIMIT, stated rather than papered over: this proves WHERE the assignment happens and
            // what that method reads, not that each right-hand side is the matching left-hand side.
            // Clause 1 is what makes the remaining surface eight lines long, in a file whose only
            // job is those five assignments.
            const string writer = "AgentEyes.CameraTrackRecord::Write";

            // The two methods allowed to touch these fields: the one that OBSERVES them off the
            // recorder, and the one that COPIES that record onto the manifest read back off disk
            // (the stop is a read-modify-write of the record the start wrote - issue #155). Nothing
            // else, in either assembly.
            var allowed = new HashSet<string>(new[] { writer, "AgentEyes.CameraTrackRecord::CopyTo" },
                StringComparer.Ordinal);

            foreach (string property in new[]
            {
                "AgentEyes.Manifest::set_CameraComplete",
                "AgentEyes.Manifest::set_CameraStopKind",
                "AgentEyes.Manifest::set_CameraStderrComplete",
                "AgentEyes.Manifest::set_CameraCapturedSeconds",
            })
            {
                var assignments = new List<CompiledCode.CallSite>();
                foreach (string assembly in CompiledCode.ProductAssemblies())
                    assignments.AddRange(CompiledCode.CallSites(assembly, c => c == property));

                // An EMPTY result is a broken instrument, never a clean scan: if nothing assigns the
                // verdict, the manifest carries no camera record at all.
                Assert.NotEmpty(assignments);
                foreach (var site in assignments)
                    Assert.True(allowed.Contains(site.Method),
                        $"{site.Assembly}!{site.Method} assigns {property} directly. That field is the "
                        + "recorder's verdict, and every place it can be written is a place it can be "
                        + $"STATED instead of reported - it belongs in {writer} alone");
            }

            // ... and the copy really is a copy: it reads each field off the source manifest rather
            // than deciding anything of its own.
            var copied = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c.StartsWith("AgentEyes.Manifest::get_Camera", StringComparison.Ordinal));
            foreach (string property in new[]
            {
                "AgentEyes.Manifest::get_CameraComplete",
                "AgentEyes.Manifest::get_CameraStopKind",
                "AgentEyes.Manifest::get_CameraStderrComplete",
                "AgentEyes.Manifest::get_CameraCapturedSeconds",
            })
                Assert.Contains(copied, s => s.Method == "AgentEyes.CameraTrackRecord::CopyTo" && s.Callee == property);

            var reads = CompiledCode.CallSites(CompiledCode.CoreAssembly,
                c => c.StartsWith("AgentEyes.Video.FfmpegCameraRecorder::get_", StringComparison.Ordinal));

            foreach (string observation in new[]
            {
                "AgentEyes.Video.FfmpegCameraRecorder::get_CapturedSeconds",
                "AgentEyes.Video.FfmpegCameraRecorder::get_StopKind",
                "AgentEyes.Video.FfmpegCameraRecorder::get_StderrComplete",
                "AgentEyes.Video.FfmpegCameraRecorder::get_Completeness",
            })
                Assert.True(reads.Any(s => s.Method == writer && s.Callee == observation),
                    $"{writer} writes the camera track's manifest record without ever reading "
                    + $"{observation} from the recorder - it is stating something rather than reporting it");

            var callers = CompiledCode.CallSites(CompiledCode.CoreAssembly, c => c == writer);
            Assert.Contains(callers, s => s.Method == "AgentEyes.RecordingService::Stop");
            Assert.Contains(callers, s => s.Method == "AgentEyes.Commands::Video");
        }

        // ---- gate defect 5: no forbidden fallback in the Devices API ----------------------------

        [Fact]
        public void TheDevicesEndpoint_DoesNotSwallowACameraEnumerationFailure()
        {
            // THE DEFECT. GET /devices caught every camera-enumeration exception and answered
            // cameras = [] with HTTP 200, so ffmpeg missing, unable to start, or throwing was
            // indistinguishable from a laptop with no webcam. That is the fallback programming
            // CLAUDE.md forbids, and it made AC1's "an empty array means this machine has no camera"
            // false. The failure now reaches the request handler, which answers 500 with the message.
            //
            // Bad result: any handler region covering the call (the merged code had a Catch).
            var sites = CompiledCode.GuardedCalls(
                CompiledCode.AppAssembly, "AgentEyes.App.RestServer::Devices",
                "AgentEyes.Video.FfmpegDevices::ListVideo");

            Assert.NotEmpty(sites);
            foreach (var site in sites)
                Assert.True(site.Handlers.Count == 0,
                    "the camera enumeration at IL offset " + site.Offset + " of RestServer::Devices is wrapped in "
                    + $"[{string.Join(", ", site.Handlers)}] - a broken enumerator must not be reported as "
                    + "'this machine has no camera'");
        }

        [Fact]
        public void TheDevicesEndpoint_StillEnumeratesCameras()
        {
            // The presence half, so "no handler covers that call" can never be satisfied by deleting
            // the call. An empty result here is a broken instrument, not a clean endpoint.
            var sites = CompiledCode.CallSites(CompiledCode.AppAssembly,
                c => c == "AgentEyes.Video.FfmpegDevices::ListVideo");

            Assert.Contains(sites, s => s.Method == "AgentEyes.App.RestServer::Devices");
        }
    }
}
