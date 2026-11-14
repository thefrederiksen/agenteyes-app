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
        }

        /// <summary>Where the records live.</summary>
        public static string Folder => Path.Combine(AppDataPaths.Root, "run", "ffmpeg");

        private static string PathFor(int pid) => Path.Combine(Folder, pid + ".json");

        /// <summary>Record a running child. Call right after it starts.</summary>
        public static void Register(Process child, string label)
        {
            using var me = Process.GetCurrentProcess();
            var e = new Entry
            {
                Pid = child.Id,
                StartUtc = child.StartTime.ToUniversalTime(),
                OwnerPid = me.Id,
                OwnerStartUtc = me.StartTime.ToUniversalTime(),
                Label = label,
            };
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(e.Pid), JsonSerializer.Serialize(e));
            Log.Info($"[FfmpegChildRegistry] Register: pid {e.Pid} [{label}] owner pid {e.OwnerPid}");
        }

        /// <summary>Forget a child that has exited (or been killed by its owner).</summary>
        public static void Unregister(int pid)
        {
            string p = PathFor(pid);
            if (File.Exists(p)) File.Delete(p);
            Log.Info($"[FfmpegChildRegistry] Unregister: pid {pid}");
        }

        /// <summary>
        /// Kill every recorded child whose owner is provably gone and which is provably still the
        /// same process. Returns one line per record examined, saying what was done and why.
        /// </summary>
        public static List<string> ReapOrphans() => ReapOrphansIn(Folder);

        /// <summary><see cref="ReapOrphans()"/> over an explicit folder, with the real probe and kill.</summary>
        internal static List<string> ReapOrphansIn(string folder) => ReapOrphans(folder, IsSameProcess, KillTree);

        /// <summary>Testable core: the probe and the kill are injected.</summary>
        internal static List<string> ReapOrphans(string folder, Func<int, DateTime, bool> isSameProcess, Action<int> kill)
        {
            var report = new List<string>();
            Log.Info($"[FfmpegChildRegistry] ReapOrphans: folder={folder}");
            if (!Directory.Exists(folder)) { Log.Info("[FfmpegChildRegistry] ReapOrphans: no records"); return report; }

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

                if (isSameProcess(e.Pid, e.StartUtc))
                {
                    kill(e.Pid);
                    report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN (owner pid {e.OwnerPid} gone) - killed");
                }
                else
                {
                    report.Add($"pid {e.Pid} [{e.Label}]: already exited (or pid reused) - record removed");
                }
                File.Delete(file);
            }

            foreach (string line in report) Log.Info($"[FfmpegChildRegistry] ReapOrphans: {line}");
            return report;
        }

        /// <summary>True when a process with <paramref name="pid"/> exists and started at <paramref name="startUtc"/>.</summary>
        private static bool IsSameProcess(int pid, DateTime startUtc)
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
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
    }
}
