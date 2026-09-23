using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.App;
using AgentEyes.Video;

namespace AgentEyes.Probe
{
    /// <summary>
    /// COMPILED-HEAD PROBES for issue #35, Review Gate round 1, defects 1, 2 and 3.
    ///
    /// It drives the REAL, COMPILED CameraPreviewController out of whichever AgentEyesApp.dll it was
    /// built against, with a fake camera session, and prints what it observed as key=value lines.
    /// It infers nothing from source text.
    ///
    /// Every probe is paired with a NEGATIVE CONTROL that exercises the same instrument against the
    /// good case, so a bad number is known to be an observation rather than a constant or an empty
    /// run. A probe that cannot reach the state it is testing prints PROBE_BROKEN and exits non-zero
    /// rather than printing a clean-looking absence.
    ///
    /// WHAT IT CANNOT SEE: it runs the LIFECYCLE with a fake session, so it says nothing about
    /// whether a real ffmpeg hands a real webcam back to Windows. Defect 4 lives in
    /// FfmpegCameraPreview's own stop path, which had no seam at the reviewed head and therefore
    /// cannot be driven by a probe built against it; that one is covered by unit tests over the new
    /// process seam plus committed mutation evidence, and is called out as such in the handoff.
    /// </summary>
    internal static class CameraReleaseProbe
    {
        private const string CameraA = "HD Webcam eMeet C960";
        private const string CameraB = "OBS Virtual Camera";

        private static int _failures;

        private static int Main()
        {
            Console.WriteLine("PROBE_BUILD=" + typeof(CameraPreviewController).Assembly.Location);

            Run("P1", Probe1_ClosingDuringEnumeration);
            Run("P1N", Probe1Negative_NotClosed);
            Run("P2", Probe2_DisposeGap);
            Run("P2N", Probe2Negative_NoBlockingStop);
            Run("P3", Probe3_BlockedOpen);
            Run("P3N", Probe3Negative_NormalOpen);

            Console.WriteLine("PROBE_FAILURES=" + _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"{name}_PROBE_BROKEN={ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- defect 1: a close during camera enumeration ----------------------

        /// <summary>
        /// The editor is closed while DirectShow is still being enumerated; the enumeration's
        /// continuation then selects the saved camera. Does the CLOSED editor's controller open a
        /// camera anyway?
        /// </summary>
        private static void Probe1_ClosingDuringEnumeration()
        {
            int baseHolders = CameraDeviceArbiter.HolderCount;
            var factory = new FakeFactory();
            var c = new CameraPreviewController(factory.Create);

            c.Select(CameraA);
            WaitForSessions(factory, 1);
            c.Dispose();

            Console.WriteLine("P1_POST_DISPOSE_HOLDER_COUNT=" + (CameraDeviceArbiter.HolderCount - baseHolders));

            // This is the enumeration continuation landing after Window.Closed.
            c.Select(CameraB);
            Thread.Sleep(600);

            var made = factory.Created;
            Console.WriteLine("P1_SESSIONS_CREATED_AFTER_DISPOSE=" + (made.Count - 1));
            Console.WriteLine("P1_POST_DISPOSE_SESSION_HELD=" + made.Any(s => s.StopCalls == 0));
            Console.WriteLine("P1_POST_DISPOSE_CONTROLLER_HOLDS=" + c.HoldsCamera);

            Cleanup(c, factory);
        }

        /// <summary>
        /// NEGATIVE CONTROL for P1. The same instrument, without the close: a second Select MUST
        /// create a second session and MUST leave it held. Without this, "0 sessions created" and
        /// "nothing held" would be indistinguishable from a probe that never ran.
        /// </summary>
        private static void Probe1Negative_NotClosed()
        {
            var factory = new FakeFactory();
            var c = new CameraPreviewController(factory.Create);

            c.Select(CameraA);
            WaitForSessions(factory, 1);
            c.Select(CameraB);
            WaitForSessions(factory, 2);

            var made = factory.Created;
            Console.WriteLine("P1N_SESSIONS_CREATED_AFTER_SECOND_SELECT=" + (made.Count - 1));
            Console.WriteLine("P1N_SESSION_HELD=" + made.Any(s => s.StopCalls == 0));
            Console.WriteLine("P1N_CONTROLLER_HOLDS=" + c.HoldsCamera);

            Cleanup(c, factory);
        }

        // ---- defect 2: unregistering before the camera is free ----------------

        /// <summary>
        /// A recording start arrives WHILE Window.Closed is waiting for the preview to stop. Is the
        /// holder still registered, and does the arbiter's release wait for the device?
        /// </summary>
        private static void Probe2_DisposeGap()
        {
            int baseHolders = CameraDeviceArbiter.HolderCount;
            var factory = new FakeFactory();
            var c = new CameraPreviewController(factory.Create);

            c.Select(CameraA);
            var session = WaitForSessions(factory, 1)[0];

            using var stopGate = new ManualResetEventSlim(false);
            session.StopBlocksOn = stopGate;

            var closing = Task.Run(() => c.Dispose());
            if (!SpinUntil(() => session.StopCalls > 0, 5000))
                throw new InvalidOperationException("the close never reached the session's Stop - nothing was observed");

            Console.WriteLine("P2_DISPOSE_GAP_HOLDER_COUNT=" + (CameraDeviceArbiter.HolderCount - baseHolders));
            Console.WriteLine("P2_DISPOSE_GAP_SESSION_HELD=" + (session.StopCalls > 0 && !session.StopReturned));

            int released = -1;
            var recording = Task.Run(() => released = CameraDeviceArbiter.ReleaseForRecording(CameraA));
            bool finishedDuringGap = recording.Wait(1500);

            Console.WriteLine("P2_DISPOSE_GAP_RELEASE_RETURNED_WHILE_HELD=" + finishedDuringGap);
            Console.WriteLine("P2_DISPOSE_GAP_RELEASED_COUNT=" + (finishedDuringGap ? released.ToString() : "(still waiting)"));

            stopGate.Set();
            closing.Wait(15000);
            recording.Wait(15000);
            Console.WriteLine("P2_AFTER_GAP_RELEASED_COUNT=" + released);

            Cleanup(c, factory);
        }

        /// <summary>NEGATIVE CONTROL for P2: with nothing blocking, a close leaves no holder and the
        /// arbiter reports nothing to release. Proves the holder count moves.</summary>
        private static void Probe2Negative_NoBlockingStop()
        {
            int baseHolders = CameraDeviceArbiter.HolderCount;
            var factory = new FakeFactory();
            var c = new CameraPreviewController(factory.Create);

            c.Select(CameraA);
            WaitForSessions(factory, 1);
            Console.WriteLine("P2N_HOLDER_COUNT_WHILE_OPEN=" + (CameraDeviceArbiter.HolderCount - baseHolders));
            c.Dispose();
            Console.WriteLine("P2N_HOLDER_COUNT_AFTER_CLEAN_CLOSE=" + (CameraDeviceArbiter.HolderCount - baseHolders));
            Console.WriteLine("P2N_RELEASED_COUNT=" + CameraDeviceArbiter.ReleaseForRecording(CameraA));

            Cleanup(c, factory);
        }

        // ---- defect 3: an in-flight open that times out ------------------------

        /// <summary>
        /// A recording starts while a preview open is still acquiring the camera. Does the release
        /// report success, and does the controller then claim to hold nothing?
        /// </summary>
        private static void Probe3_BlockedOpen()
        {
            var factory = new FakeFactory();
            using var openGate = new ManualResetEventSlim(false);
            factory.OpenBlocksOn = openGate;

            var c = new CameraPreviewController(factory.Create);
            c.Select(CameraA);

            if (!SpinUntil(() => factory.OpensEntered > 0, 5000))
                throw new InvalidOperationException("the camera open never started - nothing was observed");

            var clock = Stopwatch.StartNew();
            int released = CameraDeviceArbiter.ReleaseForRecording(CameraA);
            clock.Stop();

            Console.WriteLine("P3_BLOCKED_OPEN_IN_PROGRESS_AT_RETURN=" + (factory.OpensEntered > factory.OpensLeft));
            Console.WriteLine("P3_BLOCKED_OPEN_RELEASED_COUNT=" + released);
            Console.WriteLine("P3_BLOCKED_OPEN_RELEASE_MS=" + clock.ElapsedMilliseconds);
            Console.WriteLine("P3_BLOCKED_OPEN_CONTROLLER_HOLDS_AFTER_RETURN=" + c.HoldsCamera);
            Console.WriteLine("P3_BLOCKED_OPEN_STATE_AFTER_RETURN=" + c.State);

            openGate.Set();
            SpinUntil(() => factory.Created.Count > 0 && factory.Created.All(s => s.StopCalls > 0), 8000);
            Console.WriteLine("P3_BLOCKED_OPEN_SESSION_STOPPED_EVENTUALLY="
                              + (factory.Created.Count > 0 && factory.Created.All(s => s.StopCalls > 0)));

            Cleanup(c, factory);
        }

        /// <summary>NEGATIVE CONTROL for P3: the same instrument with an open that returns normally
        /// must report one release, promptly, and a controller holding nothing.</summary>
        private static void Probe3Negative_NormalOpen()
        {
            var factory = new FakeFactory();
            var c = new CameraPreviewController(factory.Create);
            c.Select(CameraA);
            WaitForSessions(factory, 1);

            var clock = Stopwatch.StartNew();
            int released = CameraDeviceArbiter.ReleaseForRecording(CameraA);
            clock.Stop();

            Console.WriteLine("P3N_RELEASED_COUNT=" + released);
            Console.WriteLine("P3N_RELEASE_MS=" + clock.ElapsedMilliseconds);
            Console.WriteLine("P3N_CONTROLLER_HOLDS_AFTER_RETURN=" + c.HoldsCamera);
            Console.WriteLine("P3N_SESSION_STOPPED=" + factory.Created.All(s => s.StopCalls > 0));

            Cleanup(c, factory);
        }

        // ---- helpers ----------------------------------------------------------

        private static void Cleanup(CameraPreviewController c, FakeFactory factory)
        {
            factory.OpenBlocksOn = null;
            foreach (var s in factory.Created) s.StopBlocksOn = null;
            try { c.Dispose(); } catch { }
        }

        private static IReadOnlyList<FakeSession> WaitForSessions(FakeFactory factory, int count)
        {
            if (!SpinUntil(() => factory.Created.Count >= count, 8000))
                throw new InvalidOperationException(
                    $"only {factory.Created.Count} preview session(s) appeared, wanted {count}");
            return factory.Created;
        }

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

        /// <summary>
        /// A preview session that needs no camera. The three extra members (ProcessId, OutputPath,
        /// IsAbandoned) are ordinary public members on the reviewed head and interface members after
        /// the fix, which is what lets ONE probe source compile against both builds.
        /// </summary>
        private sealed class FakeSession : ICameraPreviewSession
        {
            private int _stops;

            public FakeSession(string deviceName) => DeviceName = deviceName;

            public string DeviceName { get; }
            public int? ProcessId => 24512;
            public string OutputPath => null;
            public bool IsAbandoned => false;   // this fake always dies when asked

            public ManualResetEventSlim StopBlocksOn;
            public volatile bool StopReturned;
            public int StopCalls => Volatile.Read(ref _stops);

            public void Stop()
            {
                Interlocked.Increment(ref _stops);
                StopBlocksOn?.Wait(30000);
                StopReturned = true;
            }

            public void Dispose() { }
        }

        private sealed class FakeFactory
        {
            private readonly List<FakeSession> _created = new List<FakeSession>();
            private int _entered;
            private int _left;

            public ManualResetEventSlim OpenBlocksOn;
            public int OpensEntered => Volatile.Read(ref _entered);
            public int OpensLeft => Volatile.Read(ref _left);

            public IReadOnlyList<FakeSession> Created
            {
                get { lock (_created) { return _created.ToArray(); } }
            }

            public ICameraPreviewSession Create(string deviceName, Action<byte[]> onFrame, Action<string> onFailed)
            {
                Interlocked.Increment(ref _entered);
                OpenBlocksOn?.Wait(30000);
                var s = new FakeSession(deviceName);
                lock (_created) { _created.Add(s); }
                Interlocked.Increment(ref _left);
                return s;
            }
        }
    }
}
