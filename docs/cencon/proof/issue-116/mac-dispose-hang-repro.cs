// Issue #116, macOS: a standalone repro of the Socket.Dispose hang that SingleInstance.Dispose hit.
// It references NOTHING from AgentEyes - it is the bare pattern of SingleInstance.cs:90-94 (bind,
// listen), :135-154 (Accept on a background thread) and :205 (Dispose the listener).
//
// To run it:   mkdir repro && cd repro && dotnet new console && (replace Program.cs with this) && dotnet run -c Release
// On devthrottle-mac-mini, 2026-11-15, .NET SDK 10.0.403 / net8.0, macOS 26.5.2:
//   path=<TMPDIR>/sockrepro-9ef092b1/app.sock bytes=76
//   accept thread is blocked in accept(); calling listener.Dispose()
//   RESULT: Dispose DID NOT RETURN within 20 s - HUNG
// Expected on Linux: 'RESULT: Dispose RETURNED after <n> ms'. Bad result: the HUNG line.
// An empty result (no line at all) is a broken instrument, not a pass.
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

// Minimal repro of the SingleInstance socket pattern (SingleInstance.cs:90-94, 135-154, 205):
// bind a Unix socket, run Accept() on a background thread, then Dispose the listener while that
// thread is blocked in Accept(). Nothing from AgentEyes is referenced; this is the bare pattern.
class P
{
    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sockrepro-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.sock");
        Console.WriteLine($"path={path} bytes={System.Text.Encoding.UTF8.GetByteCount(path)}");

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(4);
        bool running = true;
        var accepting = new ManualResetEventSlim();
        new Thread(() =>
        {
            while (running)
            {
                try { accepting.Set(); using var s = listener.Accept(); }
                catch (Exception ex) { Console.WriteLine("accept threw: " + ex.GetType().Name); if (!running) return; }
            }
        }) { IsBackground = true, Name = "accept" }.Start();
        accepting.Wait();
        Thread.Sleep(300);
        Console.WriteLine("accept thread is blocked in accept(); calling listener.Dispose()");

        var sw = Stopwatch.StartNew();
        running = false;
        var done = new ManualResetEventSlim();
        new Thread(() => { listener.Dispose(); done.Set(); }) { IsBackground = true }.Start();
        bool ok = done.Wait(TimeSpan.FromSeconds(20));
        Console.WriteLine(ok
            ? $"RESULT: Dispose RETURNED after {sw.ElapsedMilliseconds} ms"
            : $"RESULT: Dispose DID NOT RETURN within {sw.Elapsed.TotalSeconds:F0} s - HUNG");
        try { File.Delete(path); Directory.Delete(dir); } catch { }
        Environment.Exit(ok ? 0 : 3);
    }
}
