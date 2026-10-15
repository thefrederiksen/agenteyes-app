using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AgentEyes.Platform.Linux
{
    /// <summary>The exit code and both output streams of one finished tool run.</summary>
    internal readonly record struct ToolResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Runs a short-lived command-line tool (xrandr, ffmpeg -sources) to completion and returns
    /// what it printed (issue #102). Both streams are drained concurrently so a chatty tool cannot
    /// fill one pipe and deadlock. A tool that does not finish inside the timeout is killed and the
    /// call fails - a device listing that hangs must not hang the recorder.
    /// </summary>
    internal static class ToolRunner
    {
        public static ToolResult Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
        {
            Log.Info($"[ToolRunner] Run: {exe} {string.Join(" ", args)}");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process p;
            try
            {
                p = Process.Start(psi) ?? throw new UsageException($"could not start {exe}.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error($"[ToolRunner] Run FAILED: {exe} could not be started", ex);
                throw new UsageException($"{exe} could not be started ({ex.Message}). Is it installed and on PATH?", ex);
            }

            using (p)
            {
                p.StandardInput.Close();
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { p.Kill(true); }
                    catch (Exception ex) { Log.Error($"[ToolRunner] Run: killing the hung {exe} FAILED", ex); }
                    Log.Error($"[ToolRunner] Run FAILED: {exe} did not finish within {timeout.TotalSeconds:0}s");
                    throw new UsageException($"{exe} did not finish within {timeout.TotalSeconds:0}s.");
                }
                p.WaitForExit(); // flush the async readers

                var result = new ToolResult(p.ExitCode, stdout.ToString(), stderr.ToString());
                Log.Info($"[ToolRunner] Run: {exe} exit={result.ExitCode} stdout={result.StdOut.Length}B stderr={result.StdErr.Length}B");
                return result;
            }
        }
    }
}
