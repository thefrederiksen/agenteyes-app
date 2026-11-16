// QA #156 pass 4 (Linux seat) - every caller of the identity predicate, on the REAL probe, in three
// states of knowledge. No test seam is replaced. Usage:
//   owner <dir> <writerPid>                         take a real claim on <dir>, record <writerPid>, then SIGKILL itself
//   probe <unknown|mine|notmine> <root> <dir> <relDir> <relWriterPid>
// In "unknown" mode the worker thread that runs every probe confines ITSELF with Landlock
// (libllprobe.so: reads of other processes' /proc/<pid> refused, kill(pid,0) still allowed) before
// the first probe. The release writer is recorded BEFORE that, unconfined, as a real capture does.
using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentEyes;
using AgentEyes.Video;

static class P
{
    [DllImport("libllprobe.so")] static extern int ll_restrict_thread();

    static void Say(string s) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
    static string Owner(string d) => File.Exists(Path.Combine(d, "capture.owner.json")) ? "PRESENT" : "ABSENT";
    static string Alive(int pid) { try { using var p = Process.GetProcessById(pid); return p.HasExited ? "exited" : "alive"; } catch { return "gone"; } }
    static string Und(object live) => live.GetType().GetProperty("Undetermined")?.GetValue(live)?.ToString() ?? "<no such property in this build>";

    static int Main(string[] a)
    {
        Say($"harness pid {Environment.ProcessId}, product {typeof(CaptureLock).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion}");
        if (a[0] == "paths") { Console.WriteLine($"PATHS recordings={AgentEyes.AppDataPaths.RecordingsRoot} state={AgentEyes.AppDataPaths.Root} registry={FfmpegChildRegistry.Folder}"); return 0; }
        if (a[0] == "total") return Qa5.Total(a);
        if (a[0] == "uidgid") return Qa5.UidGid(a);
        if (a[0] == "owner")
        {
            var l = CaptureLock.TryAcquire(a[1], "qa crashed owner", out string refusal) ?? throw new Exception("refused: " + refusal);
            l.NoteWriter("video", int.Parse(a[2]));
            if (a.Length > 3)   // the thumbnail fixture: the same crashed owner and the same live writer
            {
                var l2 = CaptureLock.TryAcquire(a[3], "qa crashed owner", out string r3) ?? throw new Exception("refused: " + r3);
                l2.NoteWriter("video", int.Parse(a[2]));
                Say($"OWNER also claimed thumbnail fixture {Path.GetFileName(a[3])} with writer {a[2]}; owner record {Owner(a[3])}");
            }
            Say($"OWNER pid {Environment.ProcessId} claimed {Path.GetFileName(a[1])} with writer {a[2]}; owner record {Owner(a[1])}; SIGKILLing myself now (a crash, no release)");
            Console.Out.Flush();
            Process.GetCurrentProcess().Kill();   // SIGKILL on Linux
            Thread.Sleep(5000);
            return 9;
        }
        if (a[0] == "inspect")   // read-only: the predicate and Inspect, nothing cleared or counted
        {
            int w = ReadWriter(a[1]);
            Say($"INSPECT from pid {Environment.ProcessId}, pid namespace {new FileInfo("/proc/self/ns/pid").LinkTarget}, host {Environment.MachineName}, host-id {CaptureLock.ThisHostId()}");
            Say($"  recorded writer pid {w}: {Identify(w, a[1])}");
            var lv = CaptureLock.Inspect(a[1]);
            Say($"  [Inspect] Live={lv.Live} Undetermined={Und(lv)} Reason={lv.Reason}");
            return 0;
        }
        string mode = a[1], root = a[2], dir = a[3], relDir = a[4];
        int relW = int.Parse(a[5]);
        var rel = CaptureLock.TryAcquire(relDir, "qa release", out string r2) ?? throw new Exception("release dir refused: " + r2);
        rel.NoteWriter("video", relW);
        Say($"RELEASE fixture: this process claims {Path.GetFileName(relDir)} with writer pid {relW} ({Alive(relW)}), recorded unconfined");
        if (mode == "notmine") { Process.GetProcessById(relW).Kill(); Thread.Sleep(500); Say($"  notmine: killed release writer {relW} -> {Alive(relW)}"); }

        int rc = 0;
        var t = new Thread(() =>
        {
            if (mode == "unknown")
            {
                if (ll_restrict_thread() != 0) { Say("ABORT: Landlock refused"); rc = 9; return; }
                bool plainRead = File.Exists($"/proc/{relW}/stat");
                string err = ""; try { File.ReadAllText($"/proc/{relW}/stat"); err = "READ OK (confinement NOT effective)"; } catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                Say($"  confinement check from this thread: read /proc/{relW}/stat -> {err}");
                if (err.StartsWith("READ OK")) { rc = 9; return; }
            }
            int w = ReadWriter(dir);
            Say($"--- probes, mode={mode}, thread {Environment.CurrentManagedThreadId}; crashed-owner dir {Path.GetFileName(dir)} writer pid {w} ({Alive(w)})");
            try { Say($"  IdentifyProcess(writer) = {Identify(w, dir)}"); } catch (Exception ex) { Say($"  IdentifyProcess: {ex.GetType().Name} {ex.Message}"); }

            var live = CaptureLock.Inspect(dir);
            Say($"  [Inspect]        Live={live.Live} Undetermined={Und(live)} Reason={live.Reason}");
            try { CaptureLock.ThrowIfLive(dir, "qa"); Say("  [ThrowIfLive]    did NOT throw (admitted)"); }
            catch (Exception ex) { Say($"  [ThrowIfLive]    REFUSED {ex.GetType().Name}: {ex.Message}"); }
            bool cleared = CaptureLock.ClearStaleClaim(dir, "qa");
            Say($"  [ClearStaleClaim] returned {cleared}; capture.owner.json {Owner(dir)}");
            var unf = PostRecordingPlan.FindUnfinished(root);
            Say($"  [FindUnfinished] admitted this recording: {unf.Any(d => Path.GetFileName(d) == Path.GetFileName(dir))} (list: {string.Join(",", unf.Select(Path.GetFileName))})");
            var thumbs = Thumbnails.FindMissing(root);
            string tdir = Path.Combine(root, "2026-11-16_170200_video");
            Say($"  [FindMissing]    would thumbnail the claimed thumbnail fixture: {thumbs.Any(d => Path.GetFileName(d) == Path.GetFileName(tdir))}; its capture.owner.json {Owner(tdir)} (list: {string.Join(",", thumbs.Select(Path.GetFileName))})");
            var rep = FfmpegChildRegistry.ReapOrphansIn(FfmpegChildRegistry.Folder, TimeSpan.FromSeconds(10));
            foreach (var line in rep) Say($"  [ReapOrphans]    {line}");
            Say($"  after reap: writer {w} {Alive(w)}; registry record {(File.Exists(Path.Combine(FfmpegChildRegistry.Folder, w + ".json")) ? "KEPT" : "REMOVED")}; capture.owner.json {Owner(dir)}");
            rel.Dispose();
            Say($"  [Release]        disposed the release claim; writer {relW} {Alive(relW)}; capture.owner.json {Owner(relDir)}");
        });
        t.Start(); t.Join();
        return rc;
    }

    static int ReadWriter(string dir)
    {
        var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "capture.owner.json")));
        return j.RootElement.GetProperty("Writers")[0].GetProperty("Pid").GetInt32();
    }

    // The predicate itself: IdentifyProcess on pass 4, IsSameProcess on pass 3 - whichever this build has.
    static string Identify(int pid, string dir)
    {
        var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "capture.owner.json")));
        var start = j.RootElement.GetProperty("Writers")[0].GetProperty("StartUtc").GetDateTime().ToUniversalTime();
        var t = typeof(FfmpegChildRegistry);
        var m = t.GetMethod("IdentifyProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
             ?? t.GetMethod("IsSameProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return $"{m.Name} -> {m.Invoke(null, new object[] { pid, start })}";
    }
}
