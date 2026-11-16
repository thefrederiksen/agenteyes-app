// QA #116 pass 3 - a .NET startup hook. Loaded into the INSTALLED agenteyes apphost by a one-shot
// launchd job via DOTNET_STARTUP_HOOKS, so whatever it starts is attributed (TCC "responsible
// process") to the agenteyes binary that holds the Screen Recording grant. It never reaches the
// recorder's Main: it does its captures and calls Environment.Exit. It changes no file of the
// install, no TCC entry, and no signature.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal class StartupHook
{
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern bool CGPreflightPostEventAccess();
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern bool AXIsProcessTrusted();

    public static void Initialize()
    {
        string outDir = Environment.GetEnvironmentVariable("QA_OUT") ?? "/tmp";
        string report = Path.Combine(outDir, "hook-report.txt");
        using var w = new StreamWriter(report, append: false) { AutoFlush = true };
        void Say(string s) => w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
        try
        {
            Say($"[hook] pid={Environment.ProcessId} exe={Environment.ProcessPath}");
            Say($"[hook] CGPreflightScreenCaptureAccess() = {CGPreflightScreenCaptureAccess()}");
            Say($"[hook] AXIsProcessTrusted() = {AXIsProcessTrusted()}  (Accessibility: needed to press a menu item)");
            Say($"[hook] CGPreflightPostEventAccess() = {CGPreflightPostEventAccess()}  (needed to synthesize a click)");
            foreach (var spec in (Environment.GetEnvironmentVariable("QA_CAPTURES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // spec = name|arg1 arg2 ...  -> /usr/sbin/screencapture <args> <outDir>/<name>.png
                var parts = spec.Split('|');
                string name = parts[0], target = Path.Combine(outDir, name + ".png");
                if (File.Exists(target)) File.Delete(target);
                var psi = new ProcessStartInfo("/usr/sbin/screencapture") { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add(target);
                Say($"[hook] {name}: screencapture {string.Join(' ', psi.ArgumentList)}");
                var sw = Stopwatch.StartNew();
                using var p = Process.Start(psi)!;
                bool done = p.WaitForExit(20000);
                if (!done) { p.Kill(true); p.WaitForExit(5000); Say($"[hook] {name}: TIMEOUT at 20 s - killed"); continue; }
                string err = p.StandardError.ReadToEnd().Trim();
                long size = File.Exists(target) ? new FileInfo(target).Length : -1;
                Say($"[hook] {name}: exit={p.ExitCode} ms={sw.ElapsedMilliseconds} file_bytes={size} stderr='{err}'");
            }
        }
        catch (Exception ex) { Say("[hook] FAILED: " + ex); }
        Say("[hook] Environment.Exit(0) - the recorder's Main is never reached");
        w.Flush();
        Environment.Exit(0);
    }
}
