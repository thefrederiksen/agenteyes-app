using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            ReapOrphans(folder, IdentifyProcess, RequestGracefulExit, KillTree, captureWriterGrace);

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
        ///
        /// Issue #156 pass 4: an <see cref="ProcessIdentity.Unknown"/> answer - for the owner or for the
        /// child, before the signal or after it - is never acted on. The process is not signalled or
        /// killed and its record is KEPT, so a later reap that can answer still can; the report line
        /// says Unknown and why. Only a proved NotMine owner lets the reap go on, and only a proved Mine
        /// child is signalled or killed.
        /// </summary>
        internal static List<string> ReapOrphans(string folder, Func<int, DateTime, ProcessAnswer> identify,
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

                var owner = identify(e.OwnerPid, e.OwnerStartUtc);
                if (owner.IsMine)
                {
                    // A live AgentEyes still owns it (e.g. the app while a CLI starts) - not ours to touch.
                    report.Add($"pid {e.Pid} [{e.Label}]: owner pid {e.OwnerPid} is alive - left alone");
                    continue;
                }
                if (owner.IsUnknown)
                {
                    report.Add($"pid {e.Pid} [{e.Label}]: UNKNOWN whether owner pid {e.OwnerPid} is alive ({owner.Why}) - " +
                               "left alone, record kept");
                    continue;
                }

                var child = identify(e.Pid, e.StartUtc);
                if (child.IsNotMine)
                {
                    report.Add($"pid {e.Pid} [{e.Label}]: already exited (or pid reused) - record removed");
                    File.Delete(file);
                    continue;
                }
                if (child.IsUnknown)
                {
                    report.Add($"pid {e.Pid} [{e.Label}]: owner pid {e.OwnerPid} gone, but UNKNOWN whether pid {e.Pid} is still " +
                               $"this ffmpeg ({child.Why}) - not signalled, left alone, record kept");
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
                        // Not proved gone - Mine, or Unknown - is still waiting: the deadline decides.
                        if (!identify(e.Pid, e.StartUtc).IsNotMine) continue;
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
                    var last = identify(e.Pid, e.StartUtc);
                    if (last.IsUnknown)
                    {
                        // Signalled while proved ours; now its identity cannot be checked. A kill needs
                        // proof, so it is not killed, and its record stays for the next reap.
                        report.Add($"pid {e.Pid} [{e.Label}]: ORPHAN capture writer (owner pid {e.OwnerPid} gone) - SIGTERM sent, " +
                                   $"still not proved exited at the {captureWriterGrace.TotalSeconds:F0}s deadline and now UNKNOWN " +
                                   $"({last.Why}) - NOT killed, record kept");
                        continue;
                    }
                    if (last.IsNotMine)
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

        /// <summary>
        /// Does <paramref name="pid"/> still name the process that was recorded as starting at
        /// <paramref name="startUtc"/>? Answers <see cref="ProcessIdentity.Mine"/>,
        /// <see cref="ProcessIdentity.NotMine"/> or <see cref="ProcessIdentity.Unknown"/>, always with
        /// the reason. It never throws.
        ///
        /// Issue #156 pass 4 - THE RULE: liveness is PROVED or reported unknown. It is never inferred
        /// from readability. Pass 3 answered "not the recorded process" for a pid of this user whose
        /// start time it could not read, on the strength of "the only cause measured is a zombie".
        /// The Review Gate measured a second cause: a prober inside a Seatbelt sandbox that allows
        /// process-info only within its own sandbox is refused the start time of a LIVE process of
        /// this same user, while kill(pid, 0) still succeeds. That prober admitted a recording whose
        /// writer was still running, spent an attempt on it, and deleted its capture claim.
        ///
        /// THE TRADEOFF, decided and not to be re-derived: Unknown delays a recovery, which can be
        /// recovered from - the recording stays on disk and is finalized by the next prober that CAN
        /// answer, or after its capture ends. Answering NotMine wrongly finalizes a file that is still
        /// being written and destroys the claim that protects it, which cannot be recovered from.
        /// Answering Mine wrongly would let the reaper signal a process it never identified. So every
        /// case not proved one way or the other is Unknown, and every caller treats Unknown as "do not
        /// touch this, and say so": <see cref="CaptureLock"/> reports it as live (ThrowIfLive refuses,
        /// ClearStaleClaim keeps the record, Inspect carries <see cref="CaptureLiveness.Undetermined"/>,
        /// the repair sweep warns), and the reaper leaves the process and its record alone.
        ///
        /// Each outcome, and the fact behind it:
        ///
        /// * pid 0 or below: NotMine. No recorded process has one; a record holding it is corrupt.
        /// * No such process (GetProcessById throws ArgumentException): NotMine - it has exited.
        /// * HasExited, or InvalidOperationException (it exited between two reads): NotMine.
        /// * Start time read and different: NotMine - the pid was reused.
        /// * Start time read and equal (within <see cref="StartTimeTolerance"/>): Mine.
        /// * The start time CANNOT BE READ (Win32Exception): decided by <see cref="ClassifyUnreadable"/>
        ///   from a second, positive fact - see there. Only facts map to NotMine; the rest is Unknown.
        ///
        /// PREMISE NOT HANDLED, stated so nobody assumes it away: this predicate assumes the prober sees
        /// the SAME pid namespace the recorder did. A Linux prober in its own pid namespace (a container)
        /// with the recordings directory mounted in would get "no such process" for a live writer of
        /// the host and answer NotMine. Unmeasured as of pass 4 (no Linux machine in that seat); see
        /// docs/cencon/proof/issue-156/handoff.md, pass 4.
        /// </summary>
        internal static ProcessAnswer IdentifyProcess(int pid, DateTime startUtc)
        {
            if (pid <= 0) return ProcessAnswer.NotMine($"pid {pid} is not a process id (a record holding it is corrupt)");

            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return ProcessAnswer.NotMine($"no process {pid} exists"); }
            using (p)
            {
                try
                {
                    if (p.HasExited) return ProcessAnswer.NotMine($"pid {pid} has exited");
                    DateTime actual = p.StartTime.ToUniversalTime();
                    return (actual - startUtc).Duration() <= StartTimeTolerance
                        ? ProcessAnswer.Mine($"pid {pid} is running and started at the recorded time {startUtc:o}")
                        : ProcessAnswer.NotMine($"pid {pid} started at {actual:o}, not the recorded {startUtc:o} (the pid was reused)");
                }
                catch (InvalidOperationException) { return ProcessAnswer.NotMine($"pid {pid} exited while being read"); }
                catch (Win32Exception ex)
                {
                    var answer = ClassifyUnreadable(pid, ex.NativeErrorCode, $"{ex.NativeErrorCode}: {ex.Message}");
                    Log.Info($"[FfmpegChildRegistry] IdentifyProcess: pid {pid} - start time cannot be read " +
                             $"({ex.NativeErrorCode}: {ex.Message}) - answered {answer.Identity}: {answer.Why}");
                    return answer;
                }
            }
        }

        /// <summary>The start time of <paramref name="pid"/> was refused: ask the second question with
        /// the real platform probes.</summary>
        internal static ProcessAnswer ClassifyUnreadable(int pid, int nativeError, string refusal) =>
            ClassifyUnreadable(pid, nativeError, refusal, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), ProbeSignal, ReadMacProcessState);

        /// <summary>
        /// Testable core of the unreadable-start-time decision. The probes are injected so EVERY row is
        /// pinned by a test, including the ones a test machine cannot produce on demand. The rows:
        ///
        /// WINDOWS, ERROR_ACCESS_DENIED (5) - NotMine, a MEASURED fact (Windows QA, issue #156 pass 3,
        /// docs/cencon/proof/issue-156/qa-report-windows.md): .NET 8 reads StartTime and HasExited
        /// through OpenProcess with PROCESS_QUERY_LIMITED_INFORMATION (read from the IL of
        /// System.Diagnostics.Process 8.0.21), and Windows grants that right to the same user ACROSS
        /// integrity levels - measured against four High-integrity processes of the user from a
        /// Medium-integrity probe, all readable. So a refusal (error 5) means the process is another
        /// user's (SYSTEM services, pid 4 - measured) or a protected process; our writer is neither.
        /// WINDOWS, any other error - Unknown: it is not a refusal, so it is not that fact.
        /// NOT MEASURED, and stated so nobody assumes it away: a prober inside an AppContainer or under a
        /// restricted token may be refused (5) for a LIVE process of the same user - the Windows
        /// counterpart of the macOS sandbox row below. The pass 3 measurement covered integrity levels
        /// only. Left as the lead ruled (5 is NotMine) and raised with the lead in the pass 4 handoff.
        ///
        /// POSIX, kill(pid, 0) - which can fail only with ESRCH or EPERM, since signal 0 is valid:
        /// * ESRCH: NotMine - the process is gone.
        /// * EPERM: NotMine - it belongs to ANOTHER USER (after a crash and a reboot, low pids are reused
        ///   by root daemons; pid 1 is the measured stand-in). The writer ran as this user, so a process
        ///   this user may not signal is not it. (A writer started under sudo is root's, and is read the
        ///   same way: see the pass 4 handoff for what that case does and does not reach.)
        /// * any other errno: Unknown - the probe itself failed.
        /// * 0 - the process EXISTS AND IS THIS USER'S. This is the row pass 3 got wrong. A refused start
        ///   time here is not evidence of anything. On macOS the kernel state is read with sysctl
        ///   KERN_PROC_PID (the call ps uses), which the measured sandbox still answers:
        ///     - p_stat == SZOMB: NotMine, PROVED - a zombie has exited and writes nothing.
        ///     - no such process in the reply: NotMine - it exited between the two calls.
        ///     - any other state (running, sleeping, stopped): Unknown - a live process of this user
        ///       whose identity cannot be checked from here (measured: the sandboxed prober).
        ///     - the sysctl itself refused: Unknown.
        ///   On Linux there is no second probe to ask (a refused /proc read refuses the state too), and
        ///   Linux reads a zombie's start time anyway: Unknown.
        /// </summary>
        internal static ProcessAnswer ClassifyUnreadable(int pid, int nativeError, string refusal, bool isWindows, bool isMac,
            Func<int, (int Rc, int Errno)> signalProbe, Func<int, MacProcessState> macState)
        {
            if (pid <= 0) return ProcessAnswer.NotMine($"pid {pid} is not a process id (a record holding it is corrupt)");

            if (isWindows && nativeError != ErrorAccessDenied)
                return ProcessAnswer.Unknown(
                    $"Windows could not read pid {pid} ({refusal}); that is not an access refusal, so it says nothing about whose process it is");
            if (isWindows)
                return ProcessAnswer.NotMine(
                    $"Windows refused to open pid {pid} ({refusal}). .NET asks only for PROCESS_QUERY_LIMITED_INFORMATION, " +
                    "which Windows grants to the same user across elevation (measured, issue #156 pass 3), so this is another " +
                    "user's or a protected process - the recorded process ran as this user, so this is not it");

            var (rc, errno) = signalProbe(pid);
            if (rc != 0)
            {
                if (errno == Esrch) return ProcessAnswer.NotMine($"pid {pid} exited while being read");
                if (errno == Eperm)
                    return ProcessAnswer.NotMine(
                        $"pid {pid} belongs to ANOTHER USER (kill(pid, 0) errno {errno}); the recorded process ran as this user, so this is not it");
                return ProcessAnswer.Unknown(
                    $"pid {pid}: its start time is refused ({refusal}) and kill(pid, 0) failed with errno {errno}, which says nothing about it");
            }

            if (!isMac)
                return ProcessAnswer.Unknown(
                    $"pid {pid} exists and is this user's, but its start time is refused ({refusal}) and this platform has no " +
                    "second probe - this process (a restricted or sandboxed one?) cannot tell whether it is the recorded process");

            var state = macState(pid);
            switch (state.Kind)
            {
                case MacProcessStateKind.Zombie:
                    return ProcessAnswer.NotMine($"pid {pid} is a ZOMBIE (sysctl KERN_PROC_PID p_stat {state.PStat} = SZOMB): it has exited and writes nothing");
                case MacProcessStateKind.Gone:
                    return ProcessAnswer.NotMine($"pid {pid} exited while being read (sysctl KERN_PROC_PID returned no process)");
                case MacProcessStateKind.Alive:
                    return ProcessAnswer.Unknown(
                        $"pid {pid} is ALIVE (p_stat {state.PStat}) and this user's, but this process is refused its start time ({refusal}) - " +
                        "typically a sandboxed prober - so whether it is the recorded process cannot be proved from here");
                default:
                    return ProcessAnswer.Unknown(
                        $"pid {pid} is this user's, its start time is refused ({refusal}) and its state could not be read ({state.Detail})");
            }
        }

        /// <summary>kill(pid, 0): rc and errno. Never called with pid 0 or below (a process GROUP to kill).</summary>
        private static (int Rc, int Errno) ProbeSignal(int pid)
        {
            int rc = PosixKill(pid, 0);
            return (rc, rc == 0 ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        }

        /// <summary>
        /// macOS: the kernel's state for <paramref name="pid"/>, from sysctl {CTL_KERN, KERN_PROC,
        /// KERN_PROC_PID, pid} - the call ps uses. The reply is one struct kinfo_proc (648 bytes on every
        /// 64-bit macOS); p_stat is the char at offset 36 of its extern_proc (two pointers' union of 16,
        /// p_vmspace, p_sigacts, p_flag). Measured 2026-11-16 on this layout: a running process reads 2
        /// (SRUN), a zombie 5 (SZOMB), a missing pid an empty reply - from a plain prober and from the
        /// gate's sandbox alike. Any other reply size is reported, never guessed at.
        /// </summary>
        internal static MacProcessState ReadMacProcessState(int pid)
        {
            int[] mib = { CtlKern, KernProc, KernProcPid, pid };
            var buf = new byte[KinfoProcSize];
            var len = (UIntPtr)KinfoProcSize;
            if (Sysctl(mib, (uint)mib.Length, buf, ref len, IntPtr.Zero, UIntPtr.Zero) != 0)
            {
                int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                return new MacProcessState(MacProcessStateKind.Unreadable, 0, $"sysctl KERN_PROC_PID refused, errno {errno}");
            }
            ulong n = len.ToUInt64();
            if (n == 0) return new MacProcessState(MacProcessStateKind.Gone, 0, "no such process");
            if (n != KinfoProcSize)
                return new MacProcessState(MacProcessStateKind.Unreadable, 0, $"sysctl KERN_PROC_PID returned {n} bytes, not the {KinfoProcSize} of a kinfo_proc");
            int stat = buf[PStatOffset];
            return stat == SZomb
                ? new MacProcessState(MacProcessStateKind.Zombie, stat, "zombie")
                : new MacProcessState(MacProcessStateKind.Alive, stat, "alive");
        }

        private const int ErrorAccessDenied = 5;   // Windows ERROR_ACCESS_DENIED
        private const int Eperm = 1;       // "operation not permitted", the same number on Linux and macOS
        private const int CtlKern = 1, KernProc = 14, KernProcPid = 1;
        private const int KinfoProcSize = 648, PStatOffset = 36, SZomb = 5;

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
        private static extern int Sysctl(int[] name, uint namelen, byte[] oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);

        internal static void KillTree(int pid)
        {
            Log.Info($"[FfmpegChildRegistry] KillTree: SIGKILL -> pid {pid} and its tree");
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
    }
}
