using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AgentEyes.Video
{
    /// <summary>
    /// Issue #128: a post-processing ffmpeg must not outlive the AgentEyes process that started it.
    ///
    /// On macOS and Linux nothing ties a child to its parent: when the service dies or is restarted,
    /// a hung mux ffmpeg is reparented to pid 1 and keeps a core pinned forever (measured on a Mac:
    /// four of them, 95-98% CPU, 35-55 minutes each). On Windows the same is true for every
    /// <see cref="Ffmpeg"/> child that is not in <c>KillOnCloseJob</c>.
    ///
    /// So every watched child is RECORDED while it runs - one small file per child under
    /// <c>%LOCALAPPDATA%\AgentEyes\run\ffmpeg\</c> holding its pid and start time and its owner's pid
    /// and start time - and deleted when it exits. At service start, <see cref="ReapOrphans"/> kills
    /// a recorded child ONLY when it can positively prove both: the owner is gone (no process with
    /// that pid, or one with a different start time), and the pid still names the SAME ffmpeg (the
    /// start time matches). Anything it cannot prove is left alone - a reused pid is never killed.
    /// </summary>
    internal static class FfmpegChildRegistry
    {
        /// <summary>Two reads of one process's start time can differ by rounding on some platforms.</summary>
        private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

        /// <summary>One recorded child.</summary>
        internal sealed class Entry
        {
            public int Pid { get; set; }
            public DateTime StartUtc { get; set; }
            public int OwnerPid { get; set; }
            public DateTime OwnerStartUtc { get; set; }
            public string Label { get; set; } = "";

            /// <summary>Issue #156 pass 2 (D1): true for a capture ffmpeg writing a recording, which
            /// is ended with SIGTERM first so it can finish its file. False for a post-processing
            /// child, whose output is disposable (the mux re-runs) and which is killed at once.</summary>
            public bool CaptureWriter { get; set; }
        }

        /// <summary>Where the records live.</summary>
        public static string Folder => Path.Combine(AppDataPaths.Root, "run", "ffmpeg");

        private static string PathFor(int pid) => Path.Combine(Folder, pid + ".json");

        /// <summary>Record a running child. Call right after it starts.</summary>
        public static void Register(Process child, string label) =>
            Register(child.Id, child.StartTime.ToUniversalTime(), label);

        /// <summary>
        /// Record a running child by pid and start time. Issue #156: a CAPTURE ffmpeg is recorded too
        /// (by <see cref="CaptureLock.NoteWriter"/>) - measured on macOS, one whose owner is killed
        /// keeps writing into the recording, reparented to pid 1, and it is both an indicator-less
        /// recorder and what keeps that recording's capture claim live. Reaping it at the next
        /// service start is what lets the recording be recovered.
        /// </summary>
        public static void Register(int pid, DateTime startUtc, string label) =>
            WriteRecord(pid, startUtc, label, captureWriter: false);

        /// <summary>
        /// Issue #156 pass 2 (D1): record a CAPTURE ffmpeg - one writing a recording straight into
        /// its directory. Reaped gracefully (see <see cref="CaptureWriterGrace"/>), never killed
        /// outright first: a plain MP4 killed with SIGKILL has no moov atom and cannot be recovered.
        /// </summary>
        public static void RegisterCaptureWriter(int pid, DateTime startUtc, string label) =>
            WriteRecord(pid, startUtc, label, captureWriter: true);

        private static void WriteRecord(int pid, DateTime startUtc, string label, bool captureWriter)
        {
            using var me = Process.GetCurrentProcess();
            var e = new Entry
            {
                Pid = pid,
                StartUtc = startUtc,
                OwnerPid = me.Id,
                OwnerStartUtc = me.StartTime.ToUniversalTime(),
                Label = label,
                CaptureWriter = captureWriter,
            };
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(e.Pid), JsonSerializer.Serialize(e));
            Log.Info($"[FfmpegChildRegistry] Register: pid {e.Pid} [{label}] owner pid {e.OwnerPid}" + (captureWriter ? " (capture writer)" : ""));
        }

        /// <summary>Forget a child that has exited (or been killed by its owner).</summary>
        public static void Unregister(int pid)
        {
            string p = PathFor(pid);
            if (File.Exists(p)) File.Delete(p);
            Log.Info($"[FfmpegChildRegistry] Unregister: pid {pid}");
        }

        /// <summary>
        /// Issue #156 pass 2 (D1): how long an orphaned CAPTURE ffmpeg is given, after SIGTERM, to
        /// finish its file and exit before it is SIGKILLed.
        ///
        /// MEASURED, not guessed: real x11grab + pulse captures (the recorder's own arguments, full
        /// 1920x1080 at 30 fps, H.264 veryfast + AAC, plain MP4), orphaned exactly as the reaper finds
        /// them - parent gone, stdin at EOF, stderr a broken pipe, SIGPIPE ignored - then sent SIGTERM.
        /// Time from SIGTERM to exit, with a complete, decodable file every time:
        ///   10 s captures x5: 0.084-0.145 s;  60 s x3: 0.121-0.127 s;  300 s x2: 0.113-0.117 s;
        ///   1800 s (30 min, 298 MB) x1: 0.117 s;  60 s with all 32 cores busy x2: 0.252-0.269 s;
        ///   and in this reaper itself, on a real service SIGKILLed mid-capture: 0.13 s.
        /// The exit time does not grow with capture length (the moov of a plain MP4 is a small index
        /// written in one pass; the fixed cost is flushing x264's lookahead and the AAC tail).
        /// The bound is 10 s: about 37x the worst measured exit (0.269 s, under full CPU load) and
        /// about 69x the worst idle one (0.145 s), and the same order as the recorder's own clean
        /// stop, which gives ffmpeg 8 s after 'q'. Measured on Linux (devlinux, 2026-11-16, issue
        /// #156 pass 2 handoff); a writer still running at the bound is SIGKILLed. It is shared by every capture writer one reap
        /// ends (they are all signalled first, then waited for together), so a reap with several
        /// orphans still finishes inside one bound. It runs off the start path (RepairService
        /// starts the reap in the background and the first pass awaits it), so the wait never holds
        /// a host's start-up.
        /// </summary>
        internal static readonly TimeSpan CaptureWriterGrace = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Kill every recorded child whose owner is provably gone and which is provably still the
        /// same process. Returns one line per record examined, saying what was done and why.
        /// </summary>
        public static List<string> ReapOrphans() => ReapOrphansIn(Folder);

        /// <summary><see cref="ReapOrphans()"/> over an explicit folder, with the real probe, signal and kill.</summary>
        internal static List<string> ReapOrphansIn(string folder) => ReapOrphansIn(folder, CaptureWriterGrace);

        /// <summary>The real probe, signal and kill with an explicit capture-writer bound (tests).</summary>
        internal static List<string> ReapOrphansIn(string folder, TimeSpan captureWriterGrace) =>
            ReapOrphans(folder, IsSameProcess, RequestGracefulExit, KillTree, captureWriterGrace);

        /// <summary>
        /// Testable core: the probe, the graceful request and the kill are injected.
        ///
        /// A post-processing child is killed at once - its output is disposable and the mux re-runs.
        /// A CAPTURE writer (issue #156 pass 2, D1) is the only copy of a recording, so it is never
        /// killed first: every orphaned capture writer is sent the graceful request (SIGTERM, which
        /// ffmpeg handles by finishing its output - for a plain MP4, writing the moov), then all of
        /// them are waited for together for at most <paramref name="captureWriterGrace"/>, and only
        /// one that is STILL the same process after that is killed. The report says which path each
        /// took. <paramref name="requestGracefulExit"/> returns false when this platform has no such
        /// request; that writer is killed at once and the report says so.
        /// </summary>
        internal static List<string> ReapOrphans(string folder, Func<int, DateTime, bool> isSameProcess,
            Func<int, bool> requestGracefulExit, Action<int> kill, TimeSpan captureWriterGrace)
        {
            var report = new List<string>();
            Log.Info($"[FfmpegChildRegistry] ReapOrphans: folder={folder}");
            if (!Directory.Exists(folder)) { Log.Info("[FfmpegChildRegistry] ReapOrphans: no records"); return report; }

            var graceful = new List<(Entry Entry, string File)>();
            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                Entry? e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                if (e == null || e.Pid <= 0)
                {
                    report.Add($"{Path.GetFileName(file)}: unreadable record - removed");
                    File.Delete(file);
                    continue;
                }

                if (isSameProcess(e.OwnerPid, e.OwnerStartUtc))
                {
                    // A live AgentEyes still owns it (e.g. the app while a CLI starts) - not ours to touch.
                    report.Add($"pid {e.Pid} [{e.Label}]: owner pid {e.OwnerPid} is alive - left alone");
                    continue;
                }

                if (!isSameProcess(e.Pid, e.StartUtc))
                {
                    report.Add($"pid {e.Pid} [{e.Label}]: already exited (or pid reused) - record removed");
                    File.Delete(file);
                    continue;
                }

                if (e.CaptureWriter)
                {
                    if (requestGracefulExit(e.Pid))
                    {
                        graceful.Add((e, file));
                        continue;
                    }
                    kill(e.Pid);
                    report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN capture writer (owner pid {e.OwnerPid} gone) - this platform " +
                               "has no graceful stop for it, so it was KILLED at once; its recording may not be recoverable");
                    File.Delete(file);
                    continue;
                }

                kill(e.Pid);
                report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN (owner pid {e.OwnerPid} gone) - killed");
                File.Delete(file);
            }

            if (graceful.Count > 0)
            {
                // One shared deadline: every writer was signalled above, so they finish in parallel.
                var clock = Stopwatch.StartNew();
                var waiting = new List<(Entry Entry, string File)>(graceful);
                while (waiting.Count > 0)
                {
                    for (int i = waiting.Count - 1; i >= 0; i--)
                    {
                        var (e, file) = waiting[i];
                        if (isSameProcess(e.Pid, e.StartUtc)) continue;
                        // Not "its file is complete": a non-child's exit status cannot be read, so
                        // this says what is known - it ended on SIGTERM and was never killed. The
                        // recovery's mux is what proves the file.
                        report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN capture writer (owner pid {e.OwnerPid} gone) - SIGTERM, " +
                                   $"exited after {clock.Elapsed.TotalSeconds:F2}s (graceful: never killed)");
                        File.Delete(file);
                        waiting.RemoveAt(i);
                    }
                    if (waiting.Count == 0 || clock.Elapsed >= captureWriterGrace) break;
                    System.Threading.Thread.Sleep(GracePollInterval);
                }

                foreach (var (e, file) in waiting)
                {
                    if (!isSameProcess(e.Pid, e.StartUtc))
                    {
                        // It exited between the last poll and the deadline: graceful after all.
                        report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN capture writer (owner pid {e.OwnerPid} gone) - SIGTERM, " +
                                   $"exited at the {captureWriterGrace.TotalSeconds:F0}s deadline (graceful: never killed)");
                        File.Delete(file);
                        continue;
                    }
                    kill(e.Pid);
                    report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN capture writer (owner pid {e.OwnerPid} gone) - SIGTERM, did NOT " +
                               $"exit within {captureWriterGrace.TotalSeconds:F0}s - SIGKILLED; its recording may not be recoverable");
                    File.Delete(file);
                }
            }

            foreach (string line in report) Log.Info($"[FfmpegChildRegistry] ReapOrphans: {line}");
            return report;
        }

        /// <summary>How often a graceful reap re-checks whether its writers have exited.</summary>
        private static readonly TimeSpan GracePollInterval = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// The graceful request: SIGTERM on macOS and Linux, which ffmpeg handles by stopping its
        /// inputs and finishing its outputs. False on Windows, which has no signal that asks a
        /// process outside this one's console to finish - the reap then kills it and says so.
        /// Throws when the signal cannot be sent for any reason other than the process having just
        /// exited (which is reported as delivered: the wait then sees it gone).
        /// </summary>
        internal static bool RequestGracefulExit(int pid)
        {
            if (OperatingSystem.IsWindows())
            {
                Log.Warn($"[FfmpegChildRegistry] RequestGracefulExit: pid {pid} - no graceful stop exists on Windows for a process " +
                         "outside this console; it will be killed");
                return false;
            }
            Log.Info($"[FfmpegChildRegistry] RequestGracefulExit: SIGTERM -> pid {pid}");
            if (PosixKill(pid, SigTerm) == 0) return true;
            int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (errno == Esrch)
            {
                Log.Info($"[FfmpegChildRegistry] RequestGracefulExit: pid {pid} exited before the signal arrived");
                return true;
            }
            throw new InvalidOperationException(
                $"could not send SIGTERM to the orphaned capture ffmpeg pid {pid} (errno {errno}). It is still writing; " +
                $"stop it by hand with: kill -TERM {pid}");
        }

        private const int SigTerm = 15;   // the same number on Linux and macOS
        private const int Esrch = 3;      // "no such process", the same number on Linux and macOS

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int PosixKill(int pid, int sig);

        /// <summary>True when a process with <paramref name="pid"/> exists and started at <paramref name="startUtc"/>.</summary>
        internal static bool IsSameProcess(int pid, DateTime startUtc)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return false; }   // no such process: the documented signal
            using (p)
            {
                try
                {
                    if (p.HasExited) return false;
                    return (p.StartTime.ToUniversalTime() - startUtc).Duration() <= StartTimeTolerance;
                }
                catch (InvalidOperationException) { return false; }   // exited between the two calls
            }
        }

        private static void KillTree(int pid)
        {
            Log.Info($"[FfmpegChildRegistry] KillTree: SIGKILL -> pid {pid} and its tree");
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
    }
}
