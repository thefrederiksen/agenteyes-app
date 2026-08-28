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

            /// <summary>False = the process survives Kill.</summary>
            public bool KillEndsIt = true;

            /// <summary>True = Kill itself throws (access denied, already-exiting race).</summary>
            public bool KillThrows;

            /// <summary>False = the process ends but its Exited callback is never delivered.</summary>
            public bool DeliversExitCallback = true;

            public bool Started;
            public int Quits;
            public int Kills;
            public int Disposes;

            public bool HasExited { get; private set; }
            public int ExitCode { get; private set; }

            public void Start(Action<string> onStderrLine, Action onExited)
            {
                Started = true;
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
                if (QuitEndsIt) End(0);
            }

            public bool WaitForExit(int milliseconds) => HasExited;

            public void Kill()
            {
                Kills++;
                if (KillThrows) throw new InvalidOperationException("access is denied");
                if (KillEndsIt) End(-1);
            }

            public void Dispose() => Disposes++;
        }

        private FfmpegCameraRecorder StartOver(FakeCameraProcess proc, double openTimeoutSeconds = 5.0) =>
            FfmpegCameraRecorder.StartOver(proc, "HD Webcam eMeet C960", Out, LogPath,
                TimeSpan.FromSeconds(openTimeoutSeconds));

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
        public void Stop_AfterAFailedTermination_DisposeTriesToTerminateTheProcessAgain()
        {
            // The second half of defect 2: _stopped was set at the TOP of Stop, so after a failed
            // termination Dispose short-circuited and the object's last chance at the device was
            // gone. Disposing a Process does not terminate the OS process.
            //
            // Bad result: Kills stays at 1 - the retry never happened.
            var proc = new FakeCameraProcess { QuitEndsIt = false, KillEndsIt = false };
            var rec = StartOver(proc);

            Assert.Throws<CameraStopFailedException>(() => rec.Stop());
            rec.Dispose();

            Assert.Equal(2, proc.Kills);
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
        public void Stop_WhenTheCameraOpenedAndThenNeverDeliveredAFrame_StillReportsTheLoss()
        {
            // The case the open probe now hands to decision 4 instead of failing the start: ffmpeg
            // reported the camera and the file open, and then produced no encoded output at all. It
            // must NOT be a silent screen-only recording - the track is reported LOST, camera.mp4 is
            // marked truncated in the manifest, and it carries the honest 0.0s.
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
            var sites = CompiledCode.GuardedCalls(
                CompiledCode.CoreAssembly, "AgentEyes.Commands::Video",
                "AgentEyes.Video.FfmpegCameraRecorder::Start");

            Assert.NotEmpty(sites);
            foreach (var site in sites)
            {
                Assert.True(site.Handlers.Contains("Finally") || site.Handlers.Contains("Fault"),
                    "the camera is opened at IL offset " + site.Offset + " of AgentEyes.Commands::Video with only "
                    + $"[{string.Join(", ", site.Handlers)}] protecting it - a throw after the camera opened leaves "
                    + "ffmpeg writing camera.mp4 and the webcam held");
                Assert.Contains("AgentEyes.Video.FfmpegCameraRecorder::Dispose", site.CleanupCalls);
            }
        }

        [Fact]
        public void TheBoundaryScan_FiresWhenItsTargetIsNotThere()
        {
            // The instrument check for the two structural tests. If GuardedCalls answered "no
            // offenders" for a method or a callee that does not exist, both of them would pass
            // forever after any rename. It throws instead - proven here, not assumed.
            Assert.Throws<InvalidOperationException>(() => CompiledCode.GuardedCalls(
                CompiledCode.CoreAssembly, "AgentEyes.Commands::NoSuchCommand",
                "AgentEyes.Video.FfmpegCameraRecorder::Start"));

            Assert.Throws<InvalidOperationException>(() => CompiledCode.GuardedCalls(
                CompiledCode.CoreAssembly, "AgentEyes.Commands::Video",
                "AgentEyes.Video.FfmpegCameraRecorder::NoSuchMethod"));
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
