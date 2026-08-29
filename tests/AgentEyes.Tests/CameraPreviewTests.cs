using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AgentEyes.App;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #29 - the preset editor's live camera preview, and the one thing about it that can break
    /// a recording.
    ///
    /// WHAT THESE CAN AND CANNOT SEE. A DirectShow camera is EXCLUSIVE, so the property that matters
    /// is not "frames appear" - it is "the device is not held any more" after every exit path. These
    /// tests drive the LIFECYCLE (<see cref="CameraPreviewController"/>) with a fake session, so they
    /// run on a machine with no camera and prove that every exit path reaches a released session and
    /// that a recording start preempts a running preview. They do NOT prove that ffmpeg produces
    /// frames or that a real camera is handed back to Windows - that needs a camera and the running
    /// app, and it is what AC1, AC3-AC7 of the running-app proof are for.
    ///
    /// Each test names the bad result it would show. Several are NEGATIVE CONTROLS: they assert that
    /// a session is NOT stopped, or that a scan does NOT report a method, so the positive assertions
    /// elsewhere are known to be capable of failing rather than passing over an empty result.
    /// </summary>
    public sealed class CameraPreviewTests
    {
        private const string Camera = "HD Webcam eMeet C960";
        private const string OtherCamera = "OBS Virtual Camera";

        // ---- the ffmpeg command line ---------------------------------------

        [Fact]
        public void CameraPreview_Args_StreamFixedSizeRawBgr24FramesOnStdout()
        {
            // The reader finds frame boundaries by COUNTING bytes, so the stream has to be raw,
            // un-containered and of a constant size. The bad result this catches is an encoded or
            // variable-size stream, which the reader would happily chop into garbage "frames" that
            // still render as a plausible-looking pane.
            var args = FfmpegArgs.CameraPreview(Camera, 320, 240, 10);
            string line = string.Join(" ", args);

            Assert.Equal(230400, FfmpegCameraPreview.FrameBytes);
            Assert.Equal(320 * 240 * 3, FfmpegCameraPreview.FrameBytes);
            Assert.Contains("-f dshow", line, StringComparison.Ordinal);
            Assert.Contains($"video={Camera}", args);
            Assert.Contains("-f rawvideo", line, StringComparison.Ordinal);
            Assert.Contains("-pix_fmt bgr24", line, StringComparison.Ordinal);
            Assert.Contains("-r 10", line, StringComparison.Ordinal);
            Assert.Equal("pipe:1", args[args.Count - 1]);

            // Aspect-preserving, padded to the exact box: the frame size must not depend on the
            // camera's native resolution, and a 16:9 webcam must not come out stretched.
            Assert.Contains(
                "scale=320:240:force_original_aspect_ratio=decrease,pad=320:240:(ow-iw)/2:(oh-ih)/2:black",
                args);
        }

        [Fact]
        public void CameraPreview_Args_NeverOpenAnAudioInput()
        {
            // The preview is a picture. It must not be able to take the camera's microphone with it -
            // that device belongs to the recording.
            var args = FfmpegArgs.CameraPreview(Camera, 320, 240, 10);

            Assert.Contains("-an", args);
            Assert.DoesNotContain(args, a => a.StartsWith("audio=", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CameraPreview_Args_DoNotRequestAFrameRateFromTheDevice()
        {
            // -framerate is an INPUT option: a dshow device that does not offer 10 fps refuses to
            // open at all, turning a working camera into a preview that never starts. The rate is
            // limited on the output instead. The bad result: "-framerate" reappearing before "-i".
            var args = FfmpegArgs.CameraPreview(Camera, 320, 240, 10);

            int input = args.IndexOf("-i");
            Assert.True(input > 0, "the preview args have no input");
            Assert.DoesNotContain("-framerate", args.Take(input));
        }

        [Fact]
        public void CameraPreview_Args_WithNoDeviceName_Throw()
        {
            Assert.Throws<UsageException>(() => FfmpegArgs.CameraPreview("", 320, 240, 10));
            Assert.Throws<UsageException>(() => FfmpegArgs.CameraPreview("   ", 320, 240, 10));
        }

        [Fact]
        public void CameraPreview_Args_WithAnUnusableFrameBox_Throw()
        {
            // Negative control for the validation above: odd/zero sizes and a zero frame rate must
            // FIRE, or the "it accepted 320x240" assertions prove nothing.
            Assert.Throws<UsageException>(() => FfmpegArgs.CameraPreview(Camera, 321, 240, 10));
            Assert.Throws<UsageException>(() => FfmpegArgs.CameraPreview(Camera, 320, 0, 10));
            Assert.Throws<UsageException>(() => FfmpegArgs.CameraPreview(Camera, 320, 240, 0));
        }

        // ---- the lifecycle: selecting ---------------------------------------

        [Fact]
        public void Select_ACamera_IsStartingAndSaysSoBeforeAnyFrame()
        {
            // AC2: the pane says what it is doing while the camera opens; it does not sit blank.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);

            preview.Select(Camera);

            Assert.Equal(CameraPreviewState.Starting, preview.State);
            Assert.Equal(CameraPreviewController.StartingStatus, preview.StatusText);
            Assert.Equal(Camera, preview.DeviceName);
        }

        [Fact]
        public void Select_TheFirstFrame_TurnsTheStartingPaneIntoARunningOne()
        {
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            var frames = new List<byte[]>();
            preview.FrameReceived += frames.Add;

            preview.Select(Camera);
            var session = WaitForSession(factory);
            session.RaiseFrame();

            Assert.Equal(CameraPreviewState.Running, preview.State);
            Assert.Single(frames);
            Assert.Equal(FfmpegCameraPreview.FrameBytes, frames[0].Length);
        }

        [Fact]
        public void Select_None_ReleasesTheCameraBeforeItReturns()
        {
            // AC3. The recording that follows starts within two seconds, so the release cannot be
            // something that happens "soon" on another thread - Select must not return holding it.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);

            preview.Select(Camera);
            var session = WaitForSession(factory);
            preview.Select(null);

            Assert.Equal(1, session.StopCalls);
            Assert.Equal(1, session.DisposeCalls);
            Assert.False(preview.HoldsCamera);
            Assert.Equal(CameraPreviewState.Stopped, preview.State);
            Assert.Equal(CameraPreviewController.NoCameraStatus, preview.StatusText);
            Assert.Null(preview.DeviceName);
        }

        [Fact]
        public void Select_ADifferentCamera_ReleasesThePreviousOne()
        {
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);

            preview.Select(Camera);
            var first = WaitForSession(factory);
            preview.Select(OtherCamera);
            var second = WaitForSession(factory, index: 1);

            Assert.Equal(1, first.StopCalls);
            Assert.Equal(0, second.StopCalls);
            Assert.Equal(OtherCamera, second.DeviceName);
            Assert.Equal(OtherCamera, preview.DeviceName);
        }

        [Fact]
        public void Select_TheCameraAlreadyShowing_DoesNotDropAndReopenTheDevice()
        {
            // NEGATIVE CONTROL. This is the one case where StopCalls must stay 0 - which is what
            // makes "StopCalls == 1" in the tests above a real observation rather than a constant.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);

            preview.Select(Camera);
            var session = WaitForSession(factory);
            session.RaiseFrame();
            preview.Select(Camera);

            Assert.Equal(0, session.StopCalls);
            Assert.Single(factory.Created);
            Assert.Equal(CameraPreviewState.Running, preview.State);
        }

        // ---- the lifecycle: every way out -----------------------------------

        [Fact]
        public void Dispose_ReleasesTheCamera_WhicheverWayTheEditorWasClosed()
        {
            // AC4. Save, Save as, Cancel, the window close button and Esc are five routes to ONE
            // event - Window.Closed - and that is what disposes this controller. The routes are not
            // five code paths to keep in step; there is one, and this is it.
            var factory = new FakeCameraFactory();
            var preview = new CameraPreviewController(factory.Create);

            preview.Select(Camera);
            var session = WaitForSession(factory);
            preview.Dispose();

            Assert.Equal(1, session.StopCalls);
            Assert.Equal(1, session.DisposeCalls);
            Assert.False(preview.HoldsCamera);
            Assert.Null(preview.DeviceName);
        }

        [Fact]
        public void Dispose_WithNoCameraSelected_ReleasesNothing()
        {
            // AC9: a preset on "(None)" must never have opened a camera at all, so there is nothing
            // to release. The bad result: a session created eagerly "just in case".
            var factory = new FakeCameraFactory();
            var preview = new CameraPreviewController(factory.Create);

            preview.Select(null);
            preview.Dispose();

            Assert.Empty(factory.Created);
        }

        [Fact]
        public void Dispose_Twice_IsHarmless()
        {
            var factory = new FakeCameraFactory();
            var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            preview.Dispose();
            preview.Dispose();

            Assert.Equal(1, session.StopCalls);
        }

        [Fact]
        public void Stop_LeavingVideoMode_ReleasesTheCameraAndSaysWhy()
        {
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            preview.Stop("The camera is only recorded in Video mode.");

            Assert.Equal(1, session.StopCalls);
            Assert.False(preview.HoldsCamera);
            Assert.Equal("The camera is only recorded in Video mode.", preview.StatusText);
        }

        // ---- failures -------------------------------------------------------

        [Fact]
        public void AFailedOpen_ShowsAMessageNamingTheDevice_AndHoldsNothing()
        {
            // AC6: a camera held by another application must produce a readable, device-named error
            // rather than a blank pane - and the device must not be left half-held.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            session.RaiseFailure($"The camera \"{Camera}\" could not be opened: "
                                 + $"the camera \"{Camera}\" is already in use by another application.");

            Assert.Equal(CameraPreviewState.Failed, preview.State);
            Assert.Contains(Camera, preview.StatusText, StringComparison.Ordinal);
            Assert.Contains("already in use", preview.StatusText, StringComparison.Ordinal);
            Assert.False(preview.HoldsCamera);
            Assert.Equal(1, session.StopCalls);
        }

        [Fact]
        public void TheOpenFailureText_ForACameraHeldElsewhere_NamesTheDevice()
        {
            // The message the pane shows comes from the recorder's diagnosis, shared rather than
            // written twice. This is the exact stderr ffmpeg 9.0 prints for a webcam held by a
            // browser (quoted in FfmpegCameraRecorder).
            const string stderr = "[dshow @ 000001] Could not run graph (sometimes caused by a device "
                                + "already in use by other application)\n[in#0 @ 000002] Error opening input: I/O error";

            string diagnosis = FfmpegCameraRecorder.DiagnoseOpenFailure(stderr, Camera);

            Assert.Contains(Camera, diagnosis, StringComparison.Ordinal);
            Assert.Contains("already in use by another application", diagnosis, StringComparison.Ordinal);
        }

        [Fact]
        public void FramesAndFailuresFromAReleasedSession_AreIgnored()
        {
            // A killed ffmpeg's reader thread can still be mid-callback when the user has already
            // moved on. The bad result: a dead camera's last frame re-animating the pane, or its
            // "stopped sending frames" error appearing over the camera the user just picked.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            var frames = new List<byte[]>();
            preview.FrameReceived += frames.Add;

            preview.Select(Camera);
            var session = WaitForSession(factory);
            preview.Select(null);

            session.RaiseFrame();
            session.RaiseFailure("this camera is long gone");

            Assert.Empty(frames);
            Assert.Equal(CameraPreviewState.Stopped, preview.State);
            Assert.Equal(CameraPreviewController.NoCameraStatus, preview.StatusText);
        }

        // ---- a recording preempts the preview (AC7) --------------------------

        [Fact]
        public void ARecordingOpeningTheCamera_ReleasesTheRunningPreviewFirst()
        {
            // AC7, the reason this feature is its own issue. The recording start calls the arbiter
            // and must be able to open the device the moment that call returns.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);
            session.RaiseFrame();
            Assert.Equal(CameraPreviewState.Running, preview.State);

            int released = CameraDeviceArbiter.ReleaseForRecording(Camera);

            Assert.Equal(1, released);
            Assert.Equal(1, session.StopCalls);
            Assert.False(preview.HoldsCamera);
            Assert.Equal(CameraPreviewState.Stopped, preview.State);
            Assert.Contains("recording", preview.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ARecordingOnADifferentCamera_StillReleasesThePreview()
        {
            // Deliberate: the two mistakes are not symmetric. Releasing a preview that did not need
            // releasing costs a preview the user was about to lose anyway; keeping one because two
            // device names were judged different costs them the recording.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            int released = CameraDeviceArbiter.ReleaseForRecording(OtherCamera);

            Assert.Equal(1, released);
            Assert.Equal(1, session.StopCalls);
        }

        [Fact]
        public void ARecordingWithNoPreviewOpen_ReleasesNothing()
        {
            // NEGATIVE CONTROL for the two tests above: with nothing held, the arbiter must report 0.
            // A ReleaseForRecording that always answered 1 would make them pass over no behaviour.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);

            int released = CameraDeviceArbiter.ReleaseForRecording(Camera);

            Assert.Equal(0, released);
            Assert.Empty(factory.Created);
        }

        [Fact]
        public void AClosedEditor_IsNoLongerAskedToReleaseAnything()
        {
            // A disposed controller must be off the arbiter's list: a stale holder would be asked to
            // release on every future recording, and its answer would be meaningless.
            var factory = new FakeCameraFactory();
            int before = CameraDeviceArbiter.HolderCount;
            var preview = new CameraPreviewController(factory.Create);
            Assert.Equal(before + 1, CameraDeviceArbiter.HolderCount);

            preview.Dispose();

            Assert.Equal(before, CameraDeviceArbiter.HolderCount);
        }

        // ---- the wiring, read out of the compiled product --------------------

        [Fact]
        public void OpeningACameraForRecording_AsksEveryHolderToReleaseIt()
        {
            // AC7's wiring, read from IL rather than from source text, so an alias, a helper or a
            // different spelling cannot hide it. The camera recorder opens in two steps on this code
            // line - Create builds the recorder, Open launches ffmpeg - so the release belongs in
            // Create, which every recording passes through first.
            //
            // WHAT THIS CANNOT SEE: IL presence proves the release is compiled into the one method
            // that prepares a camera for recording; it does not prove the release happens BEFORE the
            // process launch (bodies carry lambdas, and an ordered read across a split body has no
            // meaning). That ordering is what the running-app AC7 check observes.
            var sites = CompiledCode.CallSites(
                CompiledCode.CoreAssembly,
                callee => callee.Contains("CameraDeviceArbiter::ReleaseForRecording", StringComparison.Ordinal));

            Assert.NotEmpty(sites);
            Assert.Contains(sites, s => s.Method.Contains("FfmpegCameraRecorder::Create", StringComparison.Ordinal));
        }

        [Fact]
        public void TheScreenRecorder_DoesNotTouchTheCameraArbiter()
        {
            // NEGATIVE CONTROL for the scan above: it must discriminate between methods rather than
            // reporting every one it walks. The screen recorder opens no camera and must not appear.
            var sites = CompiledCode.CallSites(
                CompiledCode.CoreAssembly,
                callee => callee.Contains("CameraDeviceArbiter::ReleaseForRecording", StringComparison.Ordinal));

            Assert.DoesNotContain(sites, s => s.Method.Contains("FfmpegRecorder::Start", StringComparison.Ordinal));
        }

        [Fact]
        public void TheClosingPresetEditor_ReleasesTheCamera()
        {
            // AC4's wiring: the editor disposes the preview controller. Read from AgentEyesApp.dll's
            // IL, which is where the Window.Closed lambda actually lives after compilation.
            var sites = CompiledCode.CallSites(
                CompiledCode.AppAssembly,
                callee => callee.Contains("CameraPreviewController::Dispose", StringComparison.Ordinal));

            Assert.NotEmpty(sites);
            Assert.Contains(sites, s => s.Method.Contains("PresetEditor", StringComparison.Ordinal));
        }

        // ---- issue #35, Review Gate round 1: no claim without proof -----------
        //
        // The four blocking defects were one disease: a call site ANNOUNCING that the camera was
        // free without having established it, or discarding the handle to a process that survived.
        // Each test below names the bad result it would show, and each is paired with a NEGATIVE
        // CONTROL that makes the same instrument report the good case - so a passing assertion is
        // known to be an observation and not a constant.

        [Fact]
        public void Select_AfterTheEditorClosed_NeverStartsASession()
        {
            // DEFECT 1. The camera enumeration launches ffmpeg and can finish long after the dialog
            // was closed by Save / Save as / Cancel / Esc / the X. Its continuation then selected the
            // saved camera and started a preview - into a window that no longer existed, with the
            // holder already off the arbiter. The bad result: a session created after Dispose.
            var factory = new FakeCameraFactory();
            var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            WaitForSession(factory);

            preview.Dispose();
            preview.Select(OtherCamera);   // the enumeration continuation, landing after the close
            Thread.Sleep(200);             // an open is queued to a background thread; give it time

            Assert.True(preview.IsDisposed);
            Assert.Single(factory.Created);
            Assert.False(preview.HoldsCamera);
            Assert.Equal(Camera, factory.Created[0].DeviceName);
        }

        [Fact]
        public void Select_BeforeTheEditorCloses_DoesStartASession()
        {
            // NEGATIVE CONTROL for the test above. Without the close, the SAME second Select must
            // create a second session and hold it - otherwise "Single(factory.Created)" would pass
            // over a controller that had simply stopped working.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            WaitForSession(factory);

            preview.Select(OtherCamera);
            var second = WaitForSession(factory, index: 1);

            Assert.False(preview.IsDisposed);
            Assert.Equal(2, factory.Created.Count);
            Assert.Equal(OtherCamera, second.DeviceName);
            Assert.True(preview.HoldsCamera);
        }

        [Fact]
        public void Dispose_WhileTheCameraIsStillBeingReleased_KeepsTheHolderRegistered()
        {
            // DEFECT 2. Dispose used to unregister from the arbiter BEFORE stopping the preview, so
            // for the whole length of a real ffmpeg stop (up to three seconds) the camera was held
            // and a recording start snapshotting the holders found NONE. The bad result: the holder
            // count back at its baseline while the session's Stop has not returned.
            var factory = new FakeCameraFactory();
            var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            using var insideStop = new ManualResetEventSlim(false);
            session.StopBlocksOn = insideStop;

            int baseHolders = CameraDeviceArbiter.HolderCount - 1;   // this controller is one of them
            var closing = System.Threading.Tasks.Task.Run(() => preview.Dispose());
            Assert.True(SpinUntil(() => session.StopCalls > 0, 5000),
                "the close never reached the session's Stop - nothing was observed");

            Assert.Equal(baseHolders + 1, CameraDeviceArbiter.HolderCount);

            insideStop.Set();
            Assert.True(SpinUntil(() => closing.IsCompleted, 15000), "the close never finished");
            Assert.Equal(baseHolders, CameraDeviceArbiter.HolderCount);
        }

        [Fact]
        public void Dispose_WhoseCameraSurvivesTheStop_KeepsTheHolderAndRetainsTheSession()
        {
            // DEFECT 2 + DEFECT 4 together, which is the shape that actually strands a webcam: the
            // editor closes, ffmpeg ignores the kill, and the old code unregistered the holder and
            // forgot the session. The bad result: nothing registered, nothing retained, and a live
            // process on the camera that nothing in the app can reach.
            var factory = new FakeCameraFactory { SessionsSurviveTheStop = true };
            var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            int baseHolders = CameraDeviceArbiter.HolderCount - 1;
            preview.Dispose();

            Assert.Equal(1, session.StopCalls);
            Assert.Equal(0, session.DisposeCalls);                     // the handle was NOT discarded
            Assert.Equal(baseHolders + 1, CameraDeviceArbiter.HolderCount);
            Assert.Contains(CameraDeviceArbiter.StrandedPreviews.Report(),
                            r => r.Device == Camera && r.Pid == FakeCameraSession.Pid);

            // ...and it is recoverable: the next recording start retries it, and once the process is
            // really gone the holder, the retained session and the /status row all go.
            session.SurvivesTheStop = false;
            CameraDeviceArbiter.ReleaseForRecording(Camera);

            Assert.DoesNotContain(CameraDeviceArbiter.StrandedPreviews.Report(), r => r.Device == Camera);
            Assert.Equal(baseHolders, CameraDeviceArbiter.HolderCount);
        }

        [Fact]
        public void Dispose_WithACleanStop_RetainsNothingAndUnregisters()
        {
            // NEGATIVE CONTROL for the test above. A session that really does die must leave NO
            // stranded row and NO registration - otherwise "Contains(...)" above would be asserting
            // over a list everything lands in.
            var factory = new FakeCameraFactory();
            int baseHolders = CameraDeviceArbiter.HolderCount;
            var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            preview.Dispose();

            Assert.Equal(1, session.DisposeCalls);
            Assert.Equal(baseHolders, CameraDeviceArbiter.HolderCount);
            Assert.DoesNotContain(CameraDeviceArbiter.StrandedPreviews.Report(), r => r.Device == Camera);
        }

        [Fact]
        public void ARecordingStartingDuringAnUnfinishedOpen_IsNotToldTheCameraWasReleased()
        {
            // DEFECT 3. With no session published yet, the stop waited 5000ms for the in-flight open,
            // LOGGED the timeout, and then returned as though the wait had succeeded - the arbiter
            // was told one camera had been released while an open was still on its way to the device.
            // The bad result: a non-zero release count, and a controller reporting it holds nothing.
            var factory = new FakeCameraFactory();
            using var insideOpen = new ManualResetEventSlim(false);
            factory.OpenBlocksOn = insideOpen;

            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            Assert.True(SpinUntil(() => factory.OpensEntered > 0, 5000),
                "the camera open never started - nothing was observed");

            int released = CameraDeviceArbiter.ReleaseForRecording(Camera);

            Assert.Equal(0, released);                              // nothing was ESTABLISHED free
            Assert.True(preview.HoldsCamera);                       // ...and it says so
            Assert.Equal(CameraPreviewState.Failed, preview.State);
            Assert.Contains("may still be held", preview.StatusText, StringComparison.Ordinal);

            insideOpen.Set();
            Assert.True(SpinUntil(() => factory.Created.Count == 1 && factory.Created[0].StopCalls > 0, 8000),
                "the superseded open never released the session it created");
        }

        [Fact]
        public void ASessionThatSurvivesItsStop_IsNotCountedAsARelease()
        {
            // DEFECT 4 seen from the arbiter's side: a recording start must not be told a camera was
            // handed back when the process that holds it is still running.
            var factory = new FakeCameraFactory { SessionsSurviveTheStop = true };
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            int released = CameraDeviceArbiter.ReleaseForRecording(Camera);

            Assert.Equal(0, released);
            Assert.Equal(0, session.DisposeCalls);
            Assert.Equal(CameraPreviewState.Failed, preview.State);
            Assert.Contains(FakeCameraSession.Pid.ToString(), preview.StatusText, StringComparison.Ordinal);

            session.SurvivesTheStop = false;
            preview.Dispose();
        }

        [Fact]
        public void AStopThatThrows_IsNotAReleaseAndTheSessionIsNotDiscarded()
        {
            // A stop that threw has released nothing. The old code let the exception escape a Select
            // and let Dispose report success; either way the handle went and the claim of a release
            // stood.
            var factory = new FakeCameraFactory();
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);
            session.StopThrows = true;
            session.SurvivesTheStop = true;

            preview.Stop("leaving the Camera tab");

            Assert.Equal(CameraPreviewState.Failed, preview.State);
            Assert.Equal(0, session.DisposeCalls);
            Assert.True(preview.HoldsCamera);

            session.StopThrows = false;
            session.SurvivesTheStop = false;
            preview.Dispose();
        }

        [Fact]
        public void Select_WhoseCurrentCameraCannotBeReleased_DoesNotOpenTheNextOne()
        {
            // One stuck preview must not become two. The bad result: a second exclusive device
            // opened while the first is demonstrably still held.
            var factory = new FakeCameraFactory { SessionsSurviveTheStop = true };
            using var preview = new CameraPreviewController(factory.Create);
            preview.Select(Camera);
            var session = WaitForSession(factory);

            preview.Select(OtherCamera);
            Thread.Sleep(200);

            Assert.Single(factory.Created);
            Assert.Equal(CameraPreviewState.Failed, preview.State);

            session.SurvivesTheStop = false;
            preview.Dispose();
        }

        /// <summary>Spin until <paramref name="what"/> is true. Returns its final answer, so a caller
        /// asserts on a PRESENCE rather than on the loop having ended.</summary>
        private static bool SpinUntil(Func<bool> what, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                if (what()) return true;
                Thread.Sleep(5);
            }
            return what();
        }

        // ---- helpers ---------------------------------------------------------

        /// <summary>
        /// Wait for the controller's background open to have produced its session. A timeout THROWS -
        /// a test that carried on with no session would assert over nothing and pass.
        /// </summary>
        private static FakeCameraSession WaitForSession(FakeCameraFactory factory, int index = 0)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                var created = factory.Created;
                if (created.Count > index) return created[index];
                Thread.Sleep(5);
            }
            throw new InvalidOperationException(
                $"No camera preview session #{index} was created within 5s (created {factory.Created.Count}).");
        }

        /// <summary>A preview session that needs no camera: it records what was asked of it and lets
        /// the test push frames and failures back through the controller's own callbacks.</summary>
        internal sealed class FakeCameraSession : ICameraPreviewSession
        {
            private readonly Action<byte[]> _onFrame;
            private readonly Action<string> _onFailed;
            private int _stops;
            private int _disposes;

            /// <summary>The one ffmpeg behaviour that matters for issue #35's gate round 1: a camera
            /// process that survives every kill. Set it and the session goes on reporting that it
            /// still holds the device however many times it is stopped.</summary>
            public bool SurvivesTheStop;

            /// <summary>Blocks inside Stop until the test lets go, so a release can be observed
            /// WHILE it is in progress - which is where defect 2 lives.</summary>
            public ManualResetEventSlim? StopBlocksOn;

            /// <summary>Makes Stop throw. A stop that threw has released nothing.</summary>
            public bool StopThrows;

            public FakeCameraSession(string deviceName, Action<byte[]> onFrame, Action<string> onFailed)
            {
                DeviceName = deviceName;
                _onFrame = onFrame;
                _onFailed = onFailed;
            }

            public string DeviceName { get; }
            public const int Pid = 24512;

            public int? ProcessId => Pid;
            public string? OutputPath => null;

            /// <summary>Asked of the "process" every time, exactly as the real session asks the OS:
            /// it is abandoned once a stop has been attempted and it is still holding the device.</summary>
            public bool IsAbandoned => StopCalls > 0 && SurvivesTheStop;

            public int StopCalls => Volatile.Read(ref _stops);
            public int DisposeCalls => Volatile.Read(ref _disposes);

            public void RaiseFrame() => _onFrame(new byte[FfmpegCameraPreview.FrameBytes]);
            public void RaiseFailure(string message) => _onFailed(message);

            public void Stop()
            {
                Interlocked.Increment(ref _stops);
                StopBlocksOn?.Wait(30000);
                if (StopThrows) throw new InvalidOperationException("the preview stop failed");
            }

            public void Dispose() => Interlocked.Increment(ref _disposes);
        }

        /// <summary>Hands out fake sessions and remembers every one it made.</summary>
        internal sealed class FakeCameraFactory
        {
            private readonly List<FakeCameraSession> _created = new List<FakeCameraSession>();

            /// <summary>Every session this factory hands out survives its stop.</summary>
            public bool SessionsSurviveTheStop;

            /// <summary>Blocks inside the factory itself, i.e. while the camera is being OPENED and
            /// before any session has been published - the state defect 3 reported a release from.</summary>
            public ManualResetEventSlim? OpenBlocksOn;

            public IReadOnlyList<FakeCameraSession> Created
            {
                get { lock (_created) { return _created.ToArray(); } }
            }

            /// <summary>How many opens have ENTERED the factory. A test that blocks the open needs
            /// to know the open really started, or it would be observing a state it never reached.</summary>
            public int OpensEntered => Volatile.Read(ref _entered);

            private int _entered;

            public ICameraPreviewSession Create(string deviceName, Action<byte[]> onFrame, Action<string> onFailed)
            {
                Interlocked.Increment(ref _entered);
                OpenBlocksOn?.Wait(30000);
                var session = new FakeCameraSession(deviceName, onFrame, onFailed)
                {
                    SurvivesTheStop = SessionsSurviveTheStop,
                };
                lock (_created) { _created.Add(session); }
                return session;
            }
        }
    }
}
