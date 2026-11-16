using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

// #156 pass 3 Windows QA - a REAL capture whose owner is then hard-killed.
//   crash start <agenteyes.dll> <scratch>   redirect data + recordings into <scratch>, start a real
//                                            screen capture (small region, no audio) through the
//                                            product's RecordingService, print the dir, then block
//                                            until the caller TerminateProcess-es this process.
//   crash ctrlc <pid>                        send CTRL_C to <pid>'s console (QA's own way to end an
//                                            orphaned ffmpeg gracefully; NOT a product path).
static class C
{
    [DllImport("kernel32", SetLastError = true)] static extern bool AttachConsole(int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
    [DllImport("kernel32", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(int ev, int group);

    static Assembly A;
    static Type T(string n) => A.GetType(n, true);

    static int Main(string[] args)
    {
        if (args[0] == "ctrlc")
        {
            int pid = int.Parse(args[1]);
            FreeConsole();
            if (!AttachConsole(pid)) { Console.Error.WriteLine("AttachConsole failed " + Marshal.GetLastWin32Error()); return 2; }
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            bool ok = GenerateConsoleCtrlEvent(0, 0);
            Thread.Sleep(200);
            FreeConsole();
            return ok ? 0 : 3;
        }

        A = Assembly.LoadFrom(args[1]);
        string scratch = Path.GetFullPath(args[2]);
        string lad = Path.Combine(scratch, "LocalAppData"), vid = Path.Combine(scratch, "Videos");
        Directory.CreateDirectory(lad); Directory.CreateDirectory(vid);
        T("AgentEyes.AppDataPaths").GetMethod("RedirectForThisProcess", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { lad, vid });
        string root = (string)T("AgentEyes.AppDataPaths").GetProperty("RecordingsRoot")!.GetValue(null)!;
        string dataRoot = (string)T("AgentEyes.AppDataPaths").GetProperty("Root")!.GetValue(null)!;
        if (!root.StartsWith(scratch, StringComparison.OrdinalIgnoreCase) || !dataRoot.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("ABORT: redirect did not take"); return 3; }
        Console.WriteLine($"owner pid {Environment.ProcessId}; version {A.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        Console.WriteLine($"recordings root {root}; data root {dataRoot}");

        var svcType = T("AgentEyes.RecordingService");
        object svc = Activator.CreateInstance(svcType, nonPublic: true)!;
        var srcType = T("AgentEyes.AudioSourceKind");
        object none = Enum.Parse(srcType, args.Length > 3 ? args[3] : "Mic");
        object opts = Activator.CreateInstance(T("AgentEyes.AudioMixOptions"), nonPublic: true)!;
        var start = svcType.GetMethod("StartVideo")!;
        start.Invoke(svc, new object[] { 1, none, (args.Length > 4 ? args[4] : "Realtek"), new[] { 0, 0, 320, 240 }, opts, 15, null!, 30, null! });
        string dir = Directory.GetDirectories(root).Single();
        Console.WriteLine($"capturing into {dir}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
