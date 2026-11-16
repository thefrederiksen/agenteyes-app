// QA #113 macOS evidence host - NOT product code. Production RecordingService + HeadlessControlServer,
// both AgentEyes roots redirected into scratch BEFORE any path resolves. No RepairService.
// Args: <scratch-root> <port>. Stops when <scratch-root>/harness.stop exists.
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
        string version = typeof(RecordingService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)";
        Console.WriteLine($"[harness] pid={Environment.ProcessId} core={version} coreLocation={typeof(RecordingService).Assembly.Location}");
        Console.WriteLine($"[harness] data={AppDataPaths.Root}");
        Console.WriteLine($"[harness] recordings={RecordingPaths.Root}");
        var svc = new RecordingService();
        using var server = new HeadlessControlServer(svc, port);
        server.Start();
        Console.WriteLine($"[harness] listening {server.Url}");
        Console.Out.Flush();
        string stopFile = Path.Combine(scratch, "harness.stop");
        while (!File.Exists(stopFile)) Thread.Sleep(500);
        Console.WriteLine("[harness] stop file seen");
        if (svc.IsRecording) { try { svc.Stop(); } catch (Exception ex) { Console.WriteLine("[harness] stop failed: " + ex.Message); } }
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Console.WriteLine("[harness] exiting");
        return 0;
    }
}
