using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Video;

namespace AgentEyes.Headless
{
    /// <summary>
    /// <c>agenteyes serve</c> (issue #102): the recorder with no window, for the systemd user
    /// service. It hosts the same pieces the WPF app does at start-up - one
    /// <see cref="RecordingService"/>, the Control API on 127.0.0.1 (here the
    /// <see cref="HeadlessControlServer"/>), and the <see cref="RepairService"/> that finishes
    /// interrupted recordings and backfills transcripts and titles - and runs until SIGTERM / SIGINT.
    ///
    /// A stop signal never loses a take: a recording that is running is stopped and KEPT through
    /// <see cref="RecordingStop.Keep"/>, and the post-recording work is given
    /// <see cref="PostProcessingGrace"/> to finish. Anything it does not finish is on disk as an
    /// unfinished stage that the repair pass resumes at the next start.
    /// </summary>
    internal static class HeadlessHost
    {
        /// <summary>How long a shutdown waits for post-recording work. The systemd unit's
        /// TimeoutStopSec is set above this.</summary>
        public static readonly TimeSpan PostProcessingGrace = TimeSpan.FromSeconds(60);

        public static int Run(CliArgs opts)
        {
            int port = opts.Has("port") ? opts.RequireInt("port", "agenteyes serve [--port N]") : LocalAppConfig.Port;
            Log.Info($"[HeadlessHost] Run: starting, platform={HeadlessControlServer.PlatformName()} port={port} "
                     + $"pid={Environment.ProcessId} data={AppDataPaths.Root} recordings={RecordingPaths.Root}");

            // Fail at start, with the fix, rather than at the first recording: a service that is
            // "active" but cannot record is the worst state to be in.
            string ffmpeg = FfmpegLocator.Ffmpeg();
            Log.Info($"[HeadlessHost] Run: ffmpeg={ffmpeg}");
#if MACOS
            // macOS has no DISPLAY variable: the window server is always there. What CAN be missing
            // is the Screen Recording permission, and enumerating the displays is what surfaces that -
            // so the service still refuses to start when it could not record, which is the point.
            var monitors = Monitors.All();
            Log.Info($"[HeadlessHost] Run: {monitors.Count} display(s): "
                     + string.Join(", ", monitors.Select(m => $"{m.Name} {m.Width}x{m.Height}")));
#elif !WINDOWS
            string display = Platform.Linux.LinuxDisplay.Require();
            var monitors = Monitors.All();
            Log.Info($"[HeadlessHost] Run: DISPLAY={display}, {monitors.Count} monitor(s): "
                     + string.Join(", ", monitors.Select(m => $"{m.Name} {m.Width}x{m.Height}+{m.X}+{m.Y}")));
#endif

            var svc = new RecordingService();
            using var server = new HeadlessControlServer(svc, port);
            server.Start();

            using var repair = new RepairService(() => svc.IsRecording);
            repair.Start();

            using var stop = new ManualResetEventSlim(false);
            string? signalName = null;
            void OnSignal(PosixSignalContext ctx)
            {
                ctx.Cancel = true;   // we shut down ourselves, after the recording is kept
                signalName = ctx.Signal.ToString();
                stop.Set();
            }
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

            Console.WriteLine($"[ok] AgentEyes headless service listening on {server.Url}");
            Console.WriteLine($"     recordings -> {RecordingPaths.Root}");
            Console.WriteLine($"     signed in to DevThrottle: {(DevThrottle.DevThrottleAccount.IsSignedIn ? "yes" : "NO - run 'agenteyes signin' (recordings will not be transcribed)")}");
            Log.Info($"[HeadlessHost] Run: ready on {server.Url}");

            stop.Wait();
            Log.Info($"[HeadlessHost] Run: {signalName} received - shutting down");
            Shutdown(svc, server);
            Log.Info("[HeadlessHost] Run: stopped");
            return 0;
        }

        private static void Shutdown(RecordingService svc, HeadlessControlServer server)
        {
            if (svc.IsRecording)
            {
                Log.Warn("[HeadlessHost] Shutdown: a recording is running - stopping and keeping it before exit");
                try
                {
                    RecordingStop.Keep(svc);
                }
                catch (Exception ex)
                {
                    Log.Error("[HeadlessHost] Shutdown: stopping the running recording FAILED - the repair pass "
                              + "recovers what reached disk at the next start", ex);
                }
            }

            var pending = server.PendingPostProcessing();
            if (pending.Length == 0) return;
            Log.Info($"[HeadlessHost] Shutdown: waiting up to {PostProcessingGrace.TotalSeconds:0}s for {pending.Length} post-recording job(s)");
            if (Task.WaitAll(pending, PostProcessingGrace))
                Log.Info("[HeadlessHost] Shutdown: post-recording work finished");
            else
                Log.Warn("[HeadlessHost] Shutdown: post-recording work did not finish in time - its unfinished stages "
                         + "are on disk and the repair pass resumes them at the next start");
        }
    }
}
