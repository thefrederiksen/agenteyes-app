// QA #142 macOS row 5 discriminator - a .NET startup hook loaded into the INSTALLED agenteyes apphost
// by a one-shot launchd job, so the ffmpeg it starts is attributed (TCC responsible process) to the
// granted binary. It runs ffmpeg capturing AVFoundation audio input :0 STRAIGHT TO A FILE (no pipe, no
// product reader) for a fixed wall-clock time, asks it to quit with 'q', and reports wall time vs the
// samples written. It never reaches the recorder's Main. It changes no file of the install and no TCC entry.
using System; using System.Diagnostics; using System.IO; using System.Threading;
internal class StartupHook
{
    public static void Initialize()
    {
        string outDir = Environment.GetEnvironmentVariable("QA_OUT") ?? "/tmp";
        int secs = int.Parse(Environment.GetEnvironmentVariable("QA_SECS") ?? "30");
        using var w = new StreamWriter(Path.Combine(outDir, "mic-hook-report.txt"), false) { AutoFlush = true };
        void Say(string s) => w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
        try
        {
            string wav = Path.Combine(outDir, Environment.GetEnvironmentVariable("QA_NAME") ?? "direct.wav");
            var psi = new ProcessStartInfo("/opt/homebrew/bin/ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-nostats", "-y", "-f", "avfoundation", "-thread_queue_size", "1024", "-i", ":0", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", wav }) psi.ArgumentList.Add(a);
            Say($"[mic-hook] pid={Environment.ProcessId} exe={Environment.ProcessPath} ffmpeg {string.Join(' ', psi.ArgumentList)}");
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            var err = new System.Text.StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            Thread.Sleep(secs * 1000);
            double asked = sw.Elapsed.TotalSeconds;
            p.StandardInput.Write("q"); p.StandardInput.Flush();
            if (!p.WaitForExit(15000)) { p.Kill(true); Say("[mic-hook] ffmpeg did not exit on q - killed (it is MY ffmpeg, pid " + p.Id + ")"); }
            long bytes = File.Exists(wav) ? new FileInfo(wav).Length : -1;
            Say($"[mic-hook] ffmpeg pid={p.Id} exit={p.ExitCode} wall-clock until 'q' {asked:F3} s, total {sw.Elapsed.TotalSeconds:F3} s, wav bytes {bytes} -> {(bytes - 44) / 96000.0:F3} s of 48 kHz mono s16");
            lock (err) Say("[mic-hook] ffmpeg stderr:\n" + err);
        }
        catch (Exception ex) { Say("[mic-hook] FAILED: " + ex); }
        Say("[mic-hook] Environment.Exit(0)"); w.Flush(); Environment.Exit(0);
    }
}
