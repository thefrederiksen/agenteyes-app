using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

// #156 pass 3 Windows QA: the PRODUCT's predicate and its callers, loaded by reflection from the
// agenteyes.dll under test. Usage: predprobe <agenteyes.dll> <scratch-dir> <elevatedPid,...>
// Signals and kills NOTHING. Its own AgentEyes data is redirected into <scratch-dir>.
static class Q
{
    const uint PROCESS_TERMINATE = 0x0001;
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    static Assembly A;
    static Type T(string n) => A.GetType(n, true);
    static object Call(string type, string method, params object[] args)
    {
        var m = T(type).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .First(x => x.Name == method && x.GetParameters().Length == args.Length
                && x.GetParameters().Select(p => p.ParameterType).Zip(args, (pt, a) => a == null || pt.IsInstanceOfType(a)).All(b => b));
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static string Try(Func<object> f)
    {
        try { var r = f(); return r is System.Collections.IEnumerable e && r is not string ? "[" + string.Join(" | ", e.Cast<object>()) + "]" : Convert.ToString(r); }
        catch (Exception ex) { return "THREW " + ex.GetType().FullName + ": " + ex.Message; }
    }
    static string Liveness(object l) => l == null ? "null" : $"Live={l.GetType().GetProperty("Live").GetValue(l)} Reason=\"{l.GetType().GetProperty("Reason").GetValue(l)}\"";
    static DateTime Start(int pid) { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
    static int DeadPid() { for (int pid = 99996; ; pid -= 4) { try { Process.GetProcessById(pid).Dispose(); } catch (ArgumentException) { return pid; } } }
    static string Term(int pid) { IntPtr h = OpenProcess(PROCESS_TERMINATE, false, pid); if (h == IntPtr.Zero) return "ERR" + Marshal.GetLastWin32Error(); CloseHandle(h); return "ok (handle closed, nothing sent)"; }

    const string Manifest = "{ \"Tool\": \"AgentEyes\", \"Mode\": \"video\", \"Label\": \"video\", \"CreatedUtc\": \"2026-11-15T12:00:00.0000000Z\", \"MonitorIndex\": 1, \"MonitorName\": \"stand-in\", " +
        "\"PendingMux\": { \"Mode\": \"video\", \"Source\": \"mic\", \"RawVideo\": \"raw.mp4\", \"FinalFile\": \"recording.mp4\", \"RawDurationSeconds\": 0 } }";

    static void MakeCrash(string dir, int ownerPid, int writerPid, DateTime writerStart)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest);
        File.WriteAllBytes(Path.Combine(dir, "raw.mp4"), new byte[] { 0 });
        File.WriteAllText(Path.Combine(dir, "capture.lock"), "");
        File.WriteAllText(Path.Combine(dir, "capture.owner.json"),
            $"{{ \"Host\": \"{Environment.MachineName}\", \"Pid\": {ownerPid}, \"ProcessStartUtc\": \"2026-11-15T12:00:00Z\", \"What\": \"capture session\", " +
            $"\"StartedUtc\": \"2026-11-15T12:00:00Z\", \"Writers\": [ {{ \"Label\": \"video\", \"Pid\": {writerPid}, \"StartUtc\": \"{writerStart:o}\" }} ] }}");
    }

    static int Main(string[] args)
    {
        A = Assembly.LoadFrom(args[0]);
        string scratch = Path.GetFullPath(args[1]);
        int[] elevated = args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : new int[0];
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        string lad = Path.Combine(scratch, "LocalAppData"), vid = Path.Combine(scratch, "Videos");
        Directory.CreateDirectory(lad); Directory.CreateDirectory(vid);
        Call("AgentEyes.AppDataPaths", "RedirectForThisProcess", lad, vid);
        string root = (string)T("AgentEyes.AppDataPaths").GetProperty("RecordingsRoot").GetValue(null);
        string logDir = (string)T("AgentEyes.Log").GetProperty("Dir").GetValue(null);
        Console.WriteLine($"assembly: {A.Location}");
        Console.WriteLine($"version:  {A.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        Console.WriteLine($"redirected: recordings root={root}  log dir={logDir}");
        if (!root.StartsWith(scratch, StringComparison.OrdinalIgnoreCase) || !logDir.StartsWith(scratch, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("ABORT: redirect did not take"); return 3; }
        using (var me = Process.GetCurrentProcess())
            Console.WriteLine($"probe: pid {me.Id} user {Environment.UserName} admin-token={new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)} runtime {RuntimeInformation.FrameworkDescription}");

        const string Reg = "AgentEyes.Video.FfmpegChildRegistry";
        int dead = DeadPid();
        var foreign = new List<(int pid, string what)> { (4, "System") };
        foreach (var n in new[] { "services", "lsass", "wininit", "winlogon", "spoolsv", "svchost" })
            foreach (var p in Process.GetProcessesByName(n).Take(n == "svchost" ? 3 : 1)) { foreign.Add((p.Id, n + ".exe")); p.Dispose(); }

        Console.WriteLine("\n== (1) ANOTHER USER'S PROCESS: IsSameProcess(pid, 2026-11-15T12:00:01Z) - expected False, no throw");
        foreach (var (pid, what) in foreign)
            Console.WriteLine($"  {what,-14} pid {pid,-6} IsSameProcess -> {Try(() => Call(Reg, "IsSameProcess", pid, new DateTime(2026, 11, 15, 12, 0, 1, DateTimeKind.Utc)))}");

        Console.WriteLine("\n== (2) ELEVATED, SAME USER: IsSameProcess with its REAL start time (expected True = live) and a WRONG one (expected False)");
        foreach (int pid in elevated)
        {
            DateTime s = Start(pid);
            string name; using (var p = Process.GetProcessById(pid)) name = p.ProcessName;
            Console.WriteLine($"  {name,-26} pid {pid,-6} start {s:o}  real -> {Try(() => Call(Reg, "IsSameProcess", pid, s))}   wrong(-10min) -> {Try(() => Call(Reg, "IsSameProcess", pid, s.AddMinutes(-10)))}   OpenProcess(PROCESS_TERMINATE) -> {Term(pid)}");
        }

        Console.WriteLine("\n== controls: this process (medium, real start -> True), a dead pid (False), pid 0");
        using (var me = Process.GetCurrentProcess()) Console.WriteLine($"  self pid {me.Id} -> {Try(() => Call(Reg, "IsSameProcess", me.Id, me.StartTime.ToUniversalTime()))}");
        Console.WriteLine($"  dead pid {dead} -> {Try(() => Call(Reg, "IsSameProcess", dead, DateTime.UtcNow))}");
        Console.WriteLine($"  pid 0 (Idle) -> {Try(() => Call(Reg, "IsSameProcess", 0, DateTime.UtcNow))}");

        Console.WriteLine("\n== crashed recordings (free lock, owner pid " + dead + " gone, one writer) under the redirected root");
        var recs = new List<(string name, int writer, DateTime start)> {
            ("2026-11-15_120000_writer-is-system-pid4", 4, new DateTime(2026, 11, 15, 12, 0, 1, DateTimeKind.Utc)) };
        int k = 1;
        foreach (int pid in elevated)
        {
            var s = Start(pid);
            recs.Add(($"2026-11-15_12{k:00}00_elevated-writer-live", pid, s)); k++;
            recs.Add(($"2026-11-15_12{k:00}00_elevated-pid-reused", pid, s.AddMinutes(-10))); k++;
        }
        recs.Add(("2026-11-15_125900_healthy-no-claim", 0, default));
        foreach (var r in recs)
        {
            string dir = Path.Combine(root, r.name);
            if (r.writer == 0) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest); File.WriteAllBytes(Path.Combine(dir, "raw.mp4"), new byte[] { 0 }); }
            else MakeCrash(dir, dead, r.writer, r.start);
        }
        foreach (var r in recs)
            Console.WriteLine($"  {r.name,-40} writer {r.writer,-6} Inspect -> {Try(() => Liveness(Call("AgentEyes.CaptureLock", "Inspect", Path.Combine(root, r.name))))}");
        Console.WriteLine($"  FindUnfinished(root) -> {Try(() => ((IEnumerable<string>)Call("AgentEyes.PostRecordingPlan", "FindUnfinished", root)).Select(Path.GetFileName).ToList())}");

        Console.WriteLine("\n== reaper: record a = OWNER pid is SYSTEM's pid 4, child gone (the gate's item 3); record b = owner and child gone");
        string regDir = Path.Combine(scratch, "reap");
        Directory.CreateDirectory(regDir);
        File.WriteAllText(Path.Combine(regDir, "a.json"), $"{{ \"Pid\": {dead}, \"StartUtc\": \"2026-11-15T12:00:02Z\", \"OwnerPid\": 4, \"OwnerStartUtc\": \"2026-11-15T12:00:00Z\", \"Label\": \"video\", \"CaptureWriter\": true }}");
        File.WriteAllText(Path.Combine(regDir, "b.json"), $"{{ \"Pid\": {dead}, \"StartUtc\": \"2026-11-15T12:00:02Z\", \"OwnerPid\": {dead}, \"OwnerStartUtc\": \"2026-11-15T12:00:00Z\", \"Label\": \"mux\", \"CaptureWriter\": false }}");
        Console.WriteLine($"  ReapOrphansIn -> {Try(() => Call(Reg, "ReapOrphansIn", regDir))}");
        Console.WriteLine($"  records left: {Directory.GetFiles(regDir).Length}");

        Console.WriteLine("\n== IsSameProcess / CaptureLock / ReapOrphans / FindUnfinished lines from the redirected product log:");
        foreach (var f in Directory.GetFiles(logDir))
            foreach (var line in File.ReadAllLines(f).Where(l => l.Contains("IsSameProcess") || l.Contains("[CaptureLock]") || l.Contains("ReapOrphans") || l.Contains("FindUnfinished")))
                Console.WriteLine("  " + (line.Length > 460 ? line.Substring(0, 460) + "..." : line));
        return 0;
    }
}
