using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AgentEyes.Video
{
    /// <summary>Run ffmpeg synchronously to completion (for extraction/transcode steps).</summary>
    internal static class Ffmpeg
    {
        /// <summary>
        /// Issue #128: how long a post-processing run's output time may stay frozen before the run
        /// is declared hung, killed, and failed. Generous against a slow machine (a healthy audio
        /// pass advances its output many seconds per wall-clock second) and short against "forever".
        /// </summary>
        public static readonly TimeSpan PostProcessStallTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Issue #128 round two (D4): a test-only override of <see cref="PostProcessStallTimeout"/>
        /// for the current async flow, so a test can drive a real stall through the PRODUCT stage
        /// runner (<see cref="AgentEyes.Audio.AudioMix.RunStage"/>) in seconds rather than a minute.
        /// AsyncLocal, not a static field: it cannot leak into a test running in parallel.
        /// </summary>
        internal static readonly System.Threading.AsyncLocal<TimeSpan?> PostProcessStallTimeoutOverride = new();

        /// <summary>The stall bound a post-processing stage runs under right now.</summary>
        internal static TimeSpan CurrentPostProcessStallTimeout =>
            PostProcessStallTimeoutOverride.Value ?? PostProcessStallTimeout;

        /// <summary>Run ffmpeg to completion with no liveness guard.</summary>
        public static void Run(IReadOnlyList<string> args, string label) => RunCore(args, label, null);

        /// <summary>
        /// Issue #128: run ffmpeg to completion, but FAIL if its output time stops advancing for
        /// longer than <paramref name="stallTimeout"/>: the process tree is killed and a
        /// <see cref="UsageException"/> names the stage, the frozen output time and the bound.
        /// Use for stages whose output is continuous (audio/mux passes), where a frozen output time
        /// can only mean a hang - NOT for sparse-output stages such as scene extraction.
        /// </summary>
        public static void RunWatched(IReadOnlyList<string> args, string label, TimeSpan stallTimeout)
            => RunCore(args, label, stallTimeout);

        private static void RunCore(IReadOnlyList<string> args, string label, TimeSpan? stallTimeout)
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
            if (stallTimeout != null)
            {
                // Machine-readable progress on stdout, one block per second, for the watchdog.
                psi.ArgumentList.Add("-progress");
                psi.ArgumentList.Add("pipe:1");
                psi.ArgumentList.Add("-stats_period");
                psi.ArgumentList.Add("1");
            }
            foreach (var a in args) psi.ArgumentList.Add(a);

            var clock = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();

            if (stallTimeout == null)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
            }
            else
            {
                // Issue #128: a watched (post-processing) child must not outlive this process. On
                // Windows the kill-on-close job ends it the moment AgentEyes ends, however it ends;
                // on every platform the registry lets the next start reap it if that did not happen.
                if (OperatingSystem.IsWindows()) TieToLifetime(p, label);
                FfmpegChildRegistry.Register(p, label);
                try
                {
                    WaitWatched(p, label, stallTimeout.Value, clock, err);
                }
                finally
                {
                    FfmpegChildRegistry.Unregister(p.Id);
                }
            }

            if (p.ExitCode != 0)
            {
                string tail = Tail(err.ToString(), 600);
                Log.Error($"ffmpeg [{label}] failed (exit {p.ExitCode}). {tail}");
                throw new UsageException($"ffmpeg failed during {label} (exit {p.ExitCode}). {tail}");
            }
            Log.Info($"ffmpeg [{label}] ok");
        }

        /// <summary>
        /// Wait for <paramref name="p"/> while feeding its progress lines to a watchdog; on a stall,
        /// kill the whole process tree and throw. Never returns while ffmpeg is hung.
        /// </summary>
        private static void WaitWatched(Process p, string label, TimeSpan stallTimeout, Stopwatch clock, StringBuilder err)
        {
            var dog = new FfmpegProgressWatchdog(stallTimeout, clock.Elapsed);
            var gate = new object();
            p.OutputDataReceived += (_, e) => { lock (gate) dog.Observe(e.Data, clock.Elapsed); };
            p.BeginOutputReadLine();

            while (!p.WaitForExit(500))
            {
                bool stalled;
                double? outSec;
                TimeSpan frozen;
                lock (gate)
                {
                    stalled = dog.IsStalled(clock.Elapsed);
                    outSec = dog.OutputSeconds;
                    frozen = dog.FrozenFor(clock.Elapsed);
                }
                if (!stalled) continue;

                string at = outSec == null ? "before any output" : $"at {outSec.Value:F1}s of output";
                string msg = $"ffmpeg stalled during {label}: output stopped advancing {at} for " +
                             $"{frozen.TotalSeconds:F0}s (limit {stallTimeout.TotalSeconds:F0}s) after " +
                             $"{clock.Elapsed.TotalSeconds:F0}s - process killed";
                Log.Error($"ffmpeg [{label}] {msg}. {Tail(err.ToString(), 600)}");
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
                throw new UsageException(msg);
            }
            p.WaitForExit(); // drain the async stdout/stderr readers
        }

        /// <summary>
        /// Issue #128 round two (D6, found by the #136 seat on Windows CI): tie a watched child to
        /// AgentEyes' lifetime WITHOUT letting the plumbing replace ffmpeg's own error. Assigning an
        /// ALREADY-EXITED process to a job object fails, so an ffmpeg that failed within
        /// milliseconds used to surface as "could not tie ffmpeg (pid N) to AgentEyes' lifetime"
        /// instead of the error ffmpeg printed. If the child has exited there is nothing left that
        /// could outlive us: the tie is moot, and the caller goes on to report ffmpeg's exit code
        /// and stderr. If the child is still ALIVE and cannot be tied, that is a real failure: the
        /// child is killed (it must not run untied) and the tie error is thrown.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static void TieToLifetime(Process p, string label)
        {
            try
            {
                AgentEyes.AlwaysOn.KillOnCloseJob.Assign(p);
            }
            catch (System.ComponentModel.Win32Exception ex) when (p.HasExited)
            {
                Log.Info($"ffmpeg [{label}] exited (code {p.ExitCode}) before it could be tied to AgentEyes' lifetime " +
                         $"({ex.Message}) - nothing left to tie; its own result is reported");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error($"ffmpeg [{label}] could not be tied to AgentEyes' lifetime and is still running - killing it", ex);
                p.Kill(entireProcessTree: true);
                throw;
            }
        }

        private static string Tail(string s, int n) =>
            s.Length <= n ? s : "..." + s.Substring(s.Length - n);
    }
}
