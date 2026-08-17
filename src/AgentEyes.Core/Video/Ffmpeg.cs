using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AgentEyes.Video
{
    /// <summary>Run ffmpeg synchronously to completion (for extraction/transcode steps).</summary>
    internal static class Ffmpeg
    {
        public static void Run(IReadOnlyList<string> args, string label)
        {
            string exe = FfmpegLocator.Ffmpeg();

            // Defensive: a null/empty arg means a path field upstream was not set. Fail clearly
            // (this is the class of bug that crashed Stop) rather than throwing deep inside Process.
            for (int i = 0; i < args.Count; i++)
            {
                if (string.IsNullOrEmpty(args[i]))
                {
                    string msg = $"internal error building ffmpeg command for '{label}': argument {i} is null/empty.";
                    Log.Error(msg);
                    throw new UsageException(msg);
                }
            }

            Log.Info($"ffmpeg [{label}]: {FfmpegArgs.ToCommandLine(exe, args)}");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                string tail = Tail(err.ToString(), 600);
                Log.Error($"ffmpeg [{label}] failed (exit {p.ExitCode}). {tail}");
                throw new UsageException($"ffmpeg failed during {label} (exit {p.ExitCode}). {tail}");
            }
            Log.Info($"ffmpeg [{label}] ok");
        }

        private static string Tail(string s, int n) =>
            s.Length <= n ? s : "..." + s.Substring(s.Length - n);
    }
}
