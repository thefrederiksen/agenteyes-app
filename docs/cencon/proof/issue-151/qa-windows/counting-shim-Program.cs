using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
// Counting shim (QA, issue #151). One file per process start under SHIM_LOG_DIR, then runs the real tool
// from SHIM_REAL_DIR with inherited std handles and forwards its exit code.
string self = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);   // ffmpeg or ffprobe
string logDir = Environment.GetEnvironmentVariable("SHIM_LOG_DIR") ?? throw new Exception("SHIM_LOG_DIR unset");
string realDir = Environment.GetEnvironmentVariable("SHIM_REAL_DIR") ?? throw new Exception("SHIM_REAL_DIR unset");
int pid = Environment.ProcessId;
string line = $"{DateTime.UtcNow:O}\t{self}\tpid={pid}\tcwd={Environment.CurrentDirectory}\t" +
              string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
File.WriteAllText(Path.Combine(logDir, $"{self}-{DateTime.UtcNow.Ticks}-{pid}.txt"), line + Environment.NewLine);
var psi = new ProcessStartInfo(Path.Combine(realDir, self + ".exe")) { UseShellExecute = false };
foreach (var a in args) psi.ArgumentList.Add(a);
using var p = Process.Start(psi)!;
p.WaitForExit();
return p.ExitCode;
