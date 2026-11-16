// Issue #113 pass 5 evidence host - NOT product code.
//
// Runs the PRODUCTION RecordingService (production seams: real FfmpegRecorder, real CaptureWatchdog,
// real CaptureLock) behind the PRODUCTION HeadlessControlServer, with one difference from
// 'agenteyes serve': before anything resolves a path, both AgentEyes roots are redirected into a
// scratch folder (AppDataPaths.RedirectForThisProcess, the test host's own mechanism, fail-closed if
// any path was handed out first). So the recording, the claim files, the ffmpeg child registry and
// the log all land in scratch - never in the owner's library (issue #161: no supported way to do this
// from the shipped binaries). No RepairService is started, so nothing here sweeps any library.
//
// Usage: AgentEyes.Tests <scratch-root> <port>
// Stops when <scratch-root>\harness.stop exists; a recording still running then is stopped first.
using System.Reflection;
using AgentEyes;
using AgentEyes.Headless;

internal static class Program
{
    private static int Main(string[] args)
    {
        string scratch = Path.GetFullPath(args[0]);
        int port = int.Parse(args[1]);
        AppDataPaths.RedirectForThisProcess(Path.Combine(scratch, "LocalAppData"), Path.Combine(scratch, "Videos"));

        string version = typeof(RecordingService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)";
        Console.WriteLine($"[harness] pid={Environment.ProcessId} core={version}");
        Console.WriteLine($"[harness] data={AppDataPaths.Root}");
        Console.WriteLine($"[harness] recordings={RecordingPaths.Root}");

        var svc = new RecordingService();
        using var server = new HeadlessControlServer(svc, port);
        server.Start();
        Console.WriteLine($"[harness] listening {server.Url}");

        string stopFile = Path.Combine(scratch, "harness.stop");
        while (!File.Exists(stopFile)) Thread.Sleep(500);
        Console.WriteLine("[harness] stop file seen");
        if (svc.IsRecording)
        {
            Console.WriteLine("[harness] a recording is still running - stopping it");
            try { svc.Stop(); } catch (Exception ex) { Console.WriteLine("[harness] stop failed: " + ex.Message); }
        }
        // Give queued post-processing (the mux) time to finish before the process ends.
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Console.WriteLine("[harness] exiting");
        return 0;
    }
}
