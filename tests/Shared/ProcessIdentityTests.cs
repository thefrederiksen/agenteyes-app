using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEyes;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #156: the ownership predicate <see cref="FfmpegChildRegistry.IdentifyProcess"/> - every
    /// answer pinned, the real probe first, then each row of the unreadable-start-time decision with
    /// injected probes, then each caller's handling of the third answer.
    ///
    /// Pass 3 made the predicate TOTAL (it answers, never throws): on macOS a recorded pid now held by
    /// ANOTHER USER's process made Process.StartTime throw, which stranded the crashed recording and
    /// stopped thumbnail backfill for every recording. "Another user's process" is pid 1 on macOS and
    /// Linux and pid 4 (System) on Windows; the same tests assert the same answers on all three.
    ///
    /// Pass 4 gives it a THIRD answer. Pass 3 answered "not the recorded process" for a pid of this
    /// user whose start time was refused, assuming a zombie. The Review Gate measured a live writer in
    /// that state (a sandboxed prober), admitted, its attempt spent and its claim deleted. Now a zombie
    /// is PROVED (sysctl p_stat SZOMB) and anything unproved is Unknown, which every caller treats as
    /// "do not touch, and say so". Both arms are pinned: an Unknown is never touched, AND a proved
    /// stale claim is still recovered - a guard that refuses both is a wall, not a fix.
    ///
    /// In the capture-ownership collection: these tests read and swap
    /// <see cref="CaptureLock.IdentifyProcess"/> and <see cref="CaptureLock.ThisHostId"/>.
    /// </summary>
    [Collection(CaptureOwnershipCollection.Name)]
    public sealed class ProcessIdentityTests : IDisposable
    {
        private static readonly DateTime NeverStarted = new(2026, 11, 15, 12, 0, 1, DateTimeKind.Utc);

        /// <summary>The profile the Review Gate measured with: process-info allowed only for targets in
        /// the prober's own sandbox. Everything else is allowed.</summary>
        internal const string GateSandboxProfile =
            "(version 1)(allow default)(deny process-info*)(allow process-info* (target same-sandbox))";

        /// <summary>Pass 4's second measured case: the gate's profile plus signals denied outside the
        /// sandbox, so kill(pid, 0) on a live same-user writer fails with EPERM too.</summary>
        internal const string SignalDenyingSandboxProfile =
            "(version 1)(allow default)(deny process-info*)(allow process-info* (target same-sandbox))" +
            "(deny signal)(allow signal (target same-sandbox))";

        private readonly string _root;
        private readonly Func<int, DateTime, ProcessAnswer> _seamAtStart = CaptureLock.IdentifyProcess;
        private readonly Func<string> _realHostId = CaptureLock.ThisHostId;
        private readonly List<string> _muxCalls = new();

        public ProcessIdentityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-pid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            // The production probe, whatever an earlier test in the collection left behind.
            CaptureLock.IdentifyProcess = FfmpegChildRegistry.IdentifyProcess;
            PostRecording.MuxStep = MuxStub;
            PostRecording.ThumbnailStep = _ => { };
            PostRecording.ComposeStep = _ => { };
        }

        public void Dispose()
        {
            CaptureLock.IdentifyProcess = _seamAtStart;
            CaptureLock.ThisHostId = _realHostId;
            PostRecording.RestoreDefaultSteps();
            PostRecording.ComposeStep = dir => CameraCompose.Run(dir);
            foreach (string dir in Directory.GetDirectories(_root)) RecordingWorkset.ReleaseForTests(dir);
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>A process this test user cannot inspect, which is always present.</summary>
        private static int ForeignPid => OperatingSystem.IsWindows() ? 4 : 1;

        private static bool RunningAsRoot =>
            !OperatingSystem.IsWindows() && string.Equals(Environment.UserName, "root", StringComparison.Ordinal);

        private void MuxStub(string dir)
        {
            _muxCalls.Add(dir);
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "muxed");
            ManifestStore.Update(dir, m => { m.PendingMux = null; m.DurationSeconds = 12.0; });
        }

        /// <summary>
        /// What a crash leaves on this host: a start manifest with a pending mux, the raw file, a
        /// FREE capture.lock, and a same-host capture.owner.json whose owner is gone and whose
        /// writers are <paramref name="writerPids"/>. The gate's reproduction, unchanged in shape.
        /// </summary>
        private string Crashed(string name, int writerPid, DateTime? writerStart = null, params int[] moreWriters)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "raw.mp4"), "x");
            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video", Label = "video", CreatedUtc = "2026-11-16T12:00:00Z",
                PendingMux = new Manifest.PendingMuxInfo { Mode = "video", Source = "mic", RawVideo = "raw.mp4", FinalFile = "recording.mp4" },
            });
            File.WriteAllText(Path.Combine(dir, CaptureLock.FileName), "");
            var owner = new CaptureLock.OwnerRecord
            {
                Host = Environment.MachineName, Pid = int.MaxValue - 11, What = "capture session",
                StartedUtc = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc),
                Writers = new List<CaptureLock.WriterRecord>
                {
                    new() { Label = "video", Pid = writerPid, StartUtc = writerStart ?? NeverStarted },
                },
            };
            foreach (int pid in moreWriters)
                owner.Writers.Add(new CaptureLock.WriterRecord { Label = "audio", Pid = pid, StartUtc = NeverStarted });
            File.WriteAllText(Path.Combine(dir, CaptureLock.OwnerFileName), JsonSerializer.Serialize(owner));
            return dir;
        }

        // ---- the predicate, with the REAL probe -----------------------------------

        /// <summary>Pass 3's pin: another user's process is answered NotMine - no exception.</summary>
        [Fact]
        public void IdentifyProcess_PidHeldByAnotherUsersProcess_NotMineWithoutThrowing()
        {
            var answer = FfmpegChildRegistry.IdentifyProcess(ForeignPid, NeverStarted);
            Assert.True(answer.IsNotMine, $"{answer.Identity}: {answer.Why}");
        }

        /// <summary>The real probe reaches the EPERM row through pid 1 on macOS (Linux reads pid 1's
        /// start time, so there the classifier is asked directly). Not as root, which may signal
        /// anything, nor in a container whose pid 1 is the test's own user.</summary>
        [Fact]
        public void ClassifyUnreadable_RealProbePidOne_NotMineAnotherUser()
        {
            if (OperatingSystem.IsWindows() || RunningAsRoot) return;   // no kill(2); root may signal pid 1
            if (PsUser(1) == Environment.UserName) return;               // pid 1 is ours here (a container)
            var answer = FfmpegChildRegistry.ClassifyUnreadable(1, 1, "test refusal");
            Assert.True(answer.IsNotMine, $"{answer.Identity}: {answer.Why}");
            Assert.Contains("ANOTHER USER", answer.Why);
        }

        [Fact]
        public void IdentifyProcess_NoSuchProcess_NotMine()
        {
            var answer = FfmpegChildRegistry.IdentifyProcess(int.MaxValue - 7, NeverStarted);
            Assert.True(answer.IsNotMine, $"{answer.Identity}: {answer.Why}");
        }

        [Fact]
        public void IdentifyProcess_OwnProcessDifferentStartTime_NotMine()
        {
            var answer = FfmpegChildRegistry.IdentifyProcess(Environment.ProcessId, NeverStarted);
            Assert.True(answer.IsNotMine, $"{answer.Identity}: {answer.Why}");
            Assert.Contains("pid was reused", answer.Why);
        }

        /// <summary>The positive control: without it, a predicate that answers NotMine (or Unknown)
        /// for everything passes every test above - and declares every live capture stale (or none).</summary>
        [Fact]
        public void IdentifyProcess_OwnProcessRecordedStartTime_Mine()
        {
            using var me = Process.GetCurrentProcess();
            var answer = FfmpegChildRegistry.IdentifyProcess(me.Id, me.StartTime.ToUniversalTime());
            Assert.True(answer.IsMine, $"{answer.Identity}: {answer.Why}");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void IdentifyProcess_PidZeroOrNegative_NotMineCorruptNeverAGroupSignal(int pid)
        {
            var answer = FfmpegChildRegistry.IdentifyProcess(pid, NeverStarted);
            Assert.True(answer.IsNotMine, $"{answer.Identity}: {answer.Why}");
            Assert.Contains("not a process id", answer.Why);
            // The classifier refuses them before any probe: kill(2) reads 0 and below as a GROUP.
            var classified = FfmpegChildRegistry.ClassifyUnreadable(pid, 0, "test", false, true,
                _ => throw new Exception("kill(2) must never be sent to a process group"),
                _ => throw new Exception("no state read for a corrupt pid"));
            Assert.True(classified.IsNotMine);
        }

        /// <summary>
        /// Pass 4: the zombie is PROVED, not inferred. A shell backgrounds a short sleep and then execs a
        /// long one, which never waits for the first - a zombie of this user. Three assertions: the
        /// precondition (ps says Z), the kernel read the predicate now relies on (sysctl p_stat SZOMB),
        /// and the answer - NotMine, BECAUSE it is a zombie (the Why names it). macOS only: Windows has
        /// no zombies, and Linux reads a zombie's start time, so there the start-time comparison answers.
        /// </summary>
        [Fact]
        public void IdentifyProcess_ThisUsersZombie_NotMineProvedBySzomb()
        {
            if (!OperatingSystem.IsMacOS()) return;
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("sleep 0.05 & echo $!; exec sleep 30");
            using var parent = Process.Start(psi)!;
            try
            {
                int zombie = int.Parse(parent.StandardOutput.ReadLine()!.Trim());
                string state = "";
                for (int i = 0; i < 100 && !state.StartsWith("Z", StringComparison.Ordinal); i++)
                {
                    System.Threading.Thread.Sleep(20);
                    state = PsState(zombie);
                }
                Assert.True(state.StartsWith("Z", StringComparison.Ordinal), $"precondition: pid {zombie} is not a zombie (state '{state}')");

                var kernel = FfmpegChildRegistry.ReadMacProcessState(zombie);
                Assert.Equal(MacProcessStateKind.Zombie, kernel.Kind);
                Assert.Equal(5, kernel.PStat);

                var answer = FfmpegChildRegistry.IdentifyProcess(zombie, DateTime.UtcNow);
                Assert.True(answer.IsNotMine, $"{answer.Identity}: {answer.Why}");
                Assert.Contains("ZOMBIE", answer.Why);
            }
            finally
            {
                parent.Kill(entireProcessTree: true);
            }
        }

        /// <summary>The other two kernel reads the classifier relies on, against real processes: this
        /// (running) process is Alive, and a pid that does not exist is Gone.</summary>
        /// <summary>The uid offsets, read for real: this process carries this user's uid in all three,
        /// root's pid 1 carries 0. A layout change fails here on the first macOS run.</summary>
        [Fact]
        public void ReadMacProcessState_Uids_MatchThisUserAndRoot()
        {
            if (!OperatingSystem.IsMacOS()) return;
            uint me = uint.Parse(Run("/usr/bin/id", "-u"));
            Assert.NotEqual(0u, me);   // precondition: not root, or pid 1 would not be another user
            var self = FfmpegChildRegistry.ReadMacProcessState(Environment.ProcessId);
            Assert.Equal((uint?)me, self.RealUid);
            Assert.Equal((uint?)me, self.SavedUid);
            Assert.Equal((uint?)me, self.EffectiveUid);
            var init = FfmpegChildRegistry.ReadMacProcessState(1);
            Assert.Equal((uint?)0u, init.RealUid);
            Assert.Equal((uint?)0u, init.SavedUid);
        }

        private static string Run(string file, string arg)
        {
            var psi = new ProcessStartInfo(file, arg) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }

        [Fact]
        public void ReadMacProcessState_RunningAndMissingPids_AliveAndGone()
        {
            if (!OperatingSystem.IsMacOS()) return;
            var me = FfmpegChildRegistry.ReadMacProcessState(Environment.ProcessId);
            Assert.Equal(MacProcessStateKind.Alive, me.Kind);
            Assert.Equal(2, me.PStat);   // SRUN
            Assert.Equal(MacProcessStateKind.Gone, FfmpegChildRegistry.ReadMacProcessState(int.MaxValue - 7).Kind);
        }

        /// <summary>An answer nobody filled in must not read as proof of anything.</summary>
        [Fact]
        public void ProcessAnswer_Default_IsUnknown()
        {
            Assert.True(default(ProcessAnswer).IsUnknown);
        }

        // ---- the unreadable-start-time decision, one test per row -------------------

        private static Func<int, (int, int)> Signal(int rc, int errno) => _ => (rc, errno);
        private static Func<int, MacProcessState> State(MacProcessStateKind kind, int pstat = 0) => _ => new MacProcessState(kind, pstat, kind.ToString());
        private static readonly Func<int, MacProcessState> NoStateRead = _ => throw new Exception("the state must not be read on this row");

        [Fact]
        public void ClassifyUnreadable_Windows_NotMineWithoutAskingPosix()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 5, "5: Access is denied.", isWindows: true, isMac: false,
                _ => throw new Exception("no kill(2) on Windows"), NoStateRead);
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
            Assert.Contains("PROCESS_QUERY_LIMITED_INFORMATION", a.Why);
            Assert.DoesNotContain("unmeasured", a.Why);
        }

        /// <summary>Only an access REFUSAL is the measured fact. Any other Windows error reading a pid
        /// (87 is ERROR_INVALID_PARAMETER) says nothing about whose process it is.</summary>
        [Fact]
        public void ClassifyUnreadable_WindowsOtherError_Unknown()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 87, "87: The parameter is incorrect.", isWindows: true, isMac: false,
                _ => throw new Exception("no kill(2) on Windows"), NoStateRead);
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
        }

        /// <summary>A kernel state nobody read must not read as a proved zombie.</summary>
        [Fact]
        public void MacProcessState_Default_IsUnreadableNotAProof()
        {
            Assert.Equal(MacProcessStateKind.Unreadable, default(MacProcessState).Kind);
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(0, 0), _ => default);
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
        }

        [Fact]
        public void ClassifyUnreadable_KillEsrch_NotMineGone()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 3), NoStateRead);
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
            Assert.Contains("exited", a.Why);
        }

        // ---- Linux EPERM (issue #156 pass 5, D-L1): one row per branch ----------------

        private const uint LinuxMe = 1000;
        private static bool IsLinuxMe(uint uid) => uid == LinuxMe;
        private static Func<int, LinuxProcOwner> DirOwnedBy(uint uid) => _ => new LinuxProcOwner(LinuxProcOwnerKind.Owned, $"owned by uid {uid}", uid);
        private static Func<int, LinuxProcOwner> Dir(LinuxProcOwnerKind kind) => _ => new LinuxProcOwner(kind, kind.ToString());

        /// <summary>A pid reused by ANOTHER USER (root's daemon after a crash and reboot): /proc/&lt;pid&gt;
        /// is owned by another uid, which is a fact - our writer ran as us. The recording is recovered.</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnLinuxOwnedByAnotherUid_NotMine()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, IsLinuxMe, DirOwnedBy(Root));
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
            Assert.Contains("ANOTHER USER", a.Why);
            Assert.Contains("owned by uid 0", a.Why);
        }

        /// <summary>THE PASS 5 PIN - D-L1, measured on Linux: a prober refused /proc reads (Landlock) whose
        /// kill(2) answers EPERM (seccomp) probing a LIVE writer of THIS user. Pass 4 answered "ANOTHER
        /// USER" here, cleared the claim and spent an attempt. The owner is ours: Unknown.</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnLinuxOwnedByThisUser_Unknown()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, IsLinuxMe, DirOwnedBy(LinuxMe));
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
            Assert.Contains("THIS USER", a.Why);
            Assert.DoesNotContain("ANOTHER USER", a.Why);
        }

        /// <summary>/proc/&lt;pid&gt; is gone: the process exited between the reads - NotMine.</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnLinuxProcGone_NotMine()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, IsLinuxMe, Dir(LinuxProcOwnerKind.Gone));
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
            Assert.Contains("exited", a.Why);
        }

        /// <summary>/proc/&lt;pid&gt; cannot be examined at all, or nobody read it, or our own uids are
        /// unknown: whose process it is is not known, so the answer is Unknown - never a guess.</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnLinuxOwnerUnreadable_Unknown()
        {
            var refused = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, IsLinuxMe, Dir(LinuxProcOwnerKind.Unreadable));
            var neverRead = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, IsLinuxMe, _ => default);
            var noProbe = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, IsLinuxMe, linuxOwner: null);
            var noOwnUids = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead, isOurUid: null, DirOwnedBy(Root));
            Assert.True(refused.IsUnknown, $"refused: {refused.Identity}: {refused.Why}");
            Assert.True(neverRead.IsUnknown, $"never read: {neverRead.Identity}: {neverRead.Why}");
            Assert.True(noProbe.IsUnknown, $"no probe: {noProbe.Identity}: {noProbe.Why}");
            Assert.True(noOwnUids.IsUnknown, $"no own uids: {noOwnUids.Identity}: {noOwnUids.Why}");
        }

        /// <summary>The probe must not be asked on the rows that do not need it (ESRCH, kill succeeding).</summary>
        [Fact]
        public void ClassifyUnreadable_LinuxRowsWithoutEperm_DoNotReadTheOwner()
        {
            Func<int, LinuxProcOwner> mustNotRead = _ => throw new Exception("the /proc owner must not be read on this row");
            var esrch = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 3), NoStateRead, IsLinuxMe, mustNotRead);
            var ours = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(0, 0), NoStateRead, IsLinuxMe, mustNotRead);
            Assert.True(esrch.IsNotMine, $"{esrch.Identity}: {esrch.Why}");
            Assert.True(ours.IsUnknown, $"{ours.Identity}: {ours.Why}");
        }

        /// <summary>The real statx read, against real processes on every Linux run: this process's
        /// /proc entry is owned by this user, pid 1's by root, and a pid that does not exist is Gone.
        /// A layout or offset error fails here.</summary>
        [Fact]
        public void ReadLinuxProcOwner_ThisProcessRootAndMissingPid_OwnerAndGone()
        {
            if (!OperatingSystem.IsLinux()) return;
            uint me = uint.Parse(Run("/usr/bin/id", "-u"));
            var self = FfmpegChildRegistry.ReadLinuxProcOwner(Environment.ProcessId);
            Assert.Equal(LinuxProcOwnerKind.Owned, self.Kind);
            Assert.Equal((uint?)me, self.Uid);
            Assert.Equal(LinuxProcOwnerKind.Gone, FfmpegChildRegistry.ReadLinuxProcOwner(int.MaxValue - 7).Kind);
            if (PsUser(1) == "root")
            {
                var init = FfmpegChildRegistry.ReadLinuxProcOwner(1);
                Assert.Equal(LinuxProcOwnerKind.Owned, init.Kind);
                Assert.Equal((uint?)0u, init.Uid);
            }
        }

        /// <summary>The real path end to end for THIS user's live process on Linux: the real kill(2) probe
        /// is replaced by an EPERM (what a signal-scoping sandbox returns), the real /proc owner and the
        /// real own uids answer - Unknown. The pid-1 test above is the same real path answering NotMine.</summary>
        [Fact]
        public void ClassifyUnreadable_RealOwnerReadOwnLiveProcessWithEperm_Unknown()
        {
            if (!OperatingSystem.IsLinux()) return;
            uint ruid = uint.Parse(Run("/usr/bin/id", "-ru")), euid = uint.Parse(Run("/usr/bin/id", "-u"));
            var a = FfmpegChildRegistry.ClassifyUnreadable(Environment.ProcessId, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead,
                uid => uid == ruid || uid == euid, FfmpegChildRegistry.ReadLinuxProcOwner);
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
            Assert.Contains("THIS USER", a.Why);
        }

        // ---- Linux owner read without statx (issue #156 pass 5b): the predicate ANSWERS ----------

        private const int Enosys = 38, Einval = 22;
        private static (int Errno, uint? Uid) NoSuchSymbol(int pid) => throw new EntryPointNotFoundException("Unable to find an entry point named 'statx' in shared library 'libc'.");
        private static (int Errno, uint? Uid) NoSuchLibrary(int pid) => throw new DllNotFoundException("Unable to load shared library 'libSystem.Native'.");
        private static Func<int, (int Errno, uint? Uid)> Fails(int errno) => _ => (errno, null);
        private static Func<int, (int Errno, uint? Uid)> Answers(uint uid) => _ => (0, uid);

        /// <summary>THE PASS 5B PIN - TOTALITY. On the Linux EPERM row, with the owner read failing the way
        /// it does where statx does not exist (glibc before 2.28 or musl before 1.2.5: the import throws
        /// EntryPointNotFoundException; a kernel before 4.11: ENOSYS) and stat(2) unavailable too, or with
        /// statx refusing (EINVAL), the predicate answers Unknown and does not throw. A throw here is what
        /// the Review Gate rejected pass 2 for: it takes every sweep of the library down (issue #170).</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnLinuxWithoutStatxOrStat_UnknownAndNeverThrows()
        {
            var arms = new (string Name, Func<int, (int Errno, uint? Uid)> Statx, Func<int, (int Errno, uint? Uid)> Stat)[]
            {
                ("libc without statx, stat missing", NoSuchSymbol, NoSuchSymbol),
                ("libc without statx, stat library missing", NoSuchSymbol, NoSuchLibrary),
                ("kernel without statx (ENOSYS), stat ENOSYS", Fails(Enosys), Fails(Enosys)),
                ("kernel without statx (ENOSYS), stat missing", Fails(Enosys), NoSuchSymbol),
                ("statx EINVAL", Fails(Einval), NoSuchSymbol),
            };
            foreach (var arm in arms)
            {
                ProcessAnswer a = default;
                var thrown = Record.Exception(() => a = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead,
                    IsLinuxMe, pid => FfmpegChildRegistry.ReadLinuxProcOwner(pid, arm.Statx, arm.Stat)));
                Assert.True(thrown is null, $"{arm.Name}: the ownership predicate THREW instead of answering: {thrown}");
                Assert.True(a.IsUnknown, $"{arm.Name}: {a.Identity}: {a.Why}");
            }
        }

        /// <summary>Where statx is absent, stat(2) answers the same question, so the row still decides
        /// rather than degrading every probe to Unknown: another uid is NotMine, ours is Unknown, ENOENT is
        /// gone. stat is NOT asked when statx exists, even when statx refuses. And the real stat read,
        /// against real processes on every Linux run: this process is ours, pid 1 is root's, a missing pid
        /// is Gone - a wrong FileStatus offset fails here.</summary>
        [Fact]
        public void ReadLinuxProcOwner_StatxAbsent_StatAnswersTheSameOwner()
        {
            Func<int, (int Errno, uint? Uid)> mustNotStat = _ => throw new Exception("stat must not be asked while statx exists");
            var noSymbol = FfmpegChildRegistry.ReadLinuxProcOwner(4321, NoSuchSymbol, Answers(Root));
            var enosys = FfmpegChildRegistry.ReadLinuxProcOwner(4321, Fails(Enosys), Answers(LinuxMe));
            var gone = FfmpegChildRegistry.ReadLinuxProcOwner(4321, NoSuchSymbol, Fails(2));
            var refused = FfmpegChildRegistry.ReadLinuxProcOwner(4321, Fails(1), mustNotStat);
            Assert.Equal((LinuxProcOwnerKind.Owned, (uint?)Root), (noSymbol.Kind, noSymbol.Uid));
            Assert.Contains("(stat)", noSymbol.Detail);
            Assert.Equal((LinuxProcOwnerKind.Owned, (uint?)LinuxMe), (enosys.Kind, enosys.Uid));
            Assert.Equal(LinuxProcOwnerKind.Gone, gone.Kind);
            Assert.Equal(LinuxProcOwnerKind.Unreadable, refused.Kind);

            var other = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead,
                IsLinuxMe, pid => FfmpegChildRegistry.ReadLinuxProcOwner(pid, NoSuchSymbol, Answers(Root)));
            var ours = FfmpegChildRegistry.ClassifyUnreadable(4321, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead,
                IsLinuxMe, pid => FfmpegChildRegistry.ReadLinuxProcOwner(pid, NoSuchSymbol, Answers(LinuxMe)));
            Assert.True(other.IsNotMine, $"{other.Identity}: {other.Why}");
            Assert.True(ours.IsUnknown, $"{ours.Identity}: {ours.Why}");

            if (!OperatingSystem.IsLinux()) return;
            uint me = uint.Parse(Run("/usr/bin/id", "-u"));
            Assert.Equal((0, (uint?)me), FfmpegChildRegistry.StatOwnerOfPath($"/proc/{Environment.ProcessId}"));
            Assert.Equal(2, FfmpegChildRegistry.StatOwnerOfPath($"/proc/{int.MaxValue - 7}").Errno);
            if (PsUser(1) == "root") Assert.Equal((0, (uint?)0u), FfmpegChildRegistry.StatOwnerOfPath("/proc/1"));
            // These reads cannot tell Uid's offset from Gid's where this user's uid and gid are equal. The
            // pin that can - against a live subject whose uid and gid DIFFER - is the next test, for both
            // the statx read and this stat read. (Pass 5c removed a loop over system files here: every
            // branch of it was 'continue' unless a file happened to differ, so it could pass asserting nothing.)
        }

        /// <summary>
        /// THE PASS 5C PIN - the owner read returns the UID, not the GID. struct statx puts stx_uid at
        /// offset 20 and stx_gid at 24; a read at the wrong one returns the GROUP of /proc/&lt;pid&gt;. Where
        /// uid == gid the two are indistinguishable, which is how QA's mutant StxUidOffset 20 -> 24 passed
        /// the whole Linux suite (390/2) while answering NotMine for OUR OWN live process - admitting a
        /// live capture. So the subject here is a live process of this user whose /proc entry is owned by
        /// uid:gid with gid != uid (QA's subject: this process itself if its egid already differs, else
        /// 'sg &lt;a supplementary group&gt; -c "exec sleep"'), and the precondition that they differ is
        /// ASSERTED, so this test cannot pass on a subject that carries no information. Read through the
        /// real statx path, the real stat fallback, and the real predicate's Linux EPERM row.
        /// </summary>
        [LinuxUidGidSubjectFact]
        public void ReadLinuxProcOwner_LiveSubjectWhoseUidAndGidDiffer_ReadsUidNotGid()
        {
            if (!OperatingSystem.IsLinux()) return;
            uint ruid = uint.Parse(Run("/usr/bin/id", "-ru")), euid = uint.Parse(Run("/usr/bin/id", "-u"));
            using var subject = UidGidSubject.Start(euid);
            var ids = Run("/usr/bin/stat", $"-c %u:%g /proc/{subject.Pid}").Split(':');
            uint procUid = uint.Parse(ids[0]), procGid = uint.Parse(ids[1]);
            Assert.True(procUid == euid && procGid != procUid,
                $"precondition: the subject ({subject.How}) must be this user's with a different group, and /proc/{subject.Pid} is {procUid}:{procGid}");

            var owner = FfmpegChildRegistry.ReadLinuxProcOwner(subject.Pid);
            Assert.True(owner.Kind == LinuxProcOwnerKind.Owned && owner.Uid == euid && owner.Detail.Contains("(statx)"),
                $"statx owner read of /proc/{subject.Pid} ({procUid}:{procGid}, {subject.How}) gave {owner.Kind} '{owner.Detail}', " +
                $"expected owned by uid {euid} (statx)" + (owner.Uid == procGid ? " - it read the GID: the stx_uid offset is wrong" : ""));

            var viaStat = FfmpegChildRegistry.StatOwnerOfPath($"/proc/{subject.Pid}");
            Assert.True(viaStat == (0, (uint?)euid),
                $"stat owner read of /proc/{subject.Pid} ({procUid}:{procGid}) gave {viaStat}, expected (0, {euid})" +
                (viaStat.Uid == procGid ? " - it read the GID: the FileStatus Uid offset is wrong" : ""));

            var a = FfmpegChildRegistry.ClassifyUnreadable(subject.Pid, 13, "t", false, isMac: false, Signal(-1, 1), NoStateRead,
                uid => uid == ruid || uid == euid, FfmpegChildRegistry.ReadLinuxProcOwner);
            Assert.True(a.IsUnknown, $"our own live process under kill EPERM must be Unknown, never NotMine: {a.Identity}: {a.Why}");
        }

        /// <summary>A live process of this user whose /proc entry is owned by uid:gid with gid != uid.
        /// Kills only the process tree it started.</summary>
        private sealed class UidGidSubject : IDisposable
        {
            private readonly Process? _started;
            public int Pid { get; }
            public string How { get; }

            private UidGidSubject(int pid, string how, Process? started) { Pid = pid; How = how; _started = started; }

            public static UidGidSubject Start(uint euid)
            {
                if (uint.Parse(Run("/usr/bin/id", "-g")) != euid)
                    return new UidGidSubject(Environment.ProcessId, "this test process, whose egid differs", null);
                string group = LinuxUidGidSubject.SupplementaryGroupNotEqualTo(euid)
                    ?? throw new InvalidOperationException("no supplementary group differs from the uid - the attribute should have skipped");
                var psi = new ProcessStartInfo("/usr/bin/sg") { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
                foreach (string arg in new[] { group, "-c", "echo $$; exec sleep 120" }) psi.ArgumentList.Add(arg);
                var p = Process.Start(psi)!;
                string? line = p.StandardOutput.ReadLine();
                if (!int.TryParse(line, out int pid))
                {
                    p.Kill(entireProcessTree: true);
                    p.Dispose();
                    throw new InvalidOperationException($"sg {group} did not start the subject: first line '{line}'");
                }
                return new UidGidSubject(pid, $"'sg {group} -c \"exec sleep 120\"', pid {pid}", p);
            }

            public void Dispose()
            {
                if (_started is null) return;
                if (!_started.HasExited) _started.Kill(entireProcessTree: true);
                _started.WaitForExit(5000);
                _started.Dispose();
            }
        }

        private const uint Me = 501, Root = 0;
        private static Func<int, MacProcessState> Owned(uint ruid, uint svuid) =>
            _ => new MacProcessState(MacProcessStateKind.Alive, 2, "alive", ruid, svuid, ruid);
        private static bool IsMe(uint uid) => uid == Me;

        /// <summary>macOS EPERM, PROVED another user: the target's uids are not ours.</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnMacTargetOfAnotherUid_NotMine()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 1), Owned(Root, Root), IsMe);
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
            Assert.Contains("ANOTHER USER", a.Why);
        }

        /// <summary>THE SECOND PASS 4 PIN - measured: a sandbox that denies signals outside itself gets
        /// EPERM for a LIVE process of THIS user. The uids permit the signal, so EPERM is not the fact
        /// "another user" - it must be Unknown, not NotMine.</summary>
        [Theory]
        [InlineData(Me, Me)]
        [InlineData(Me, Root)]    // real uid ours
        [InlineData(Root, Me)]    // saved uid ours (kill(2) checks both)
        public void ClassifyUnreadable_KillEpermOnMacTargetIsThisUsers_Unknown(uint ruid, uint svuid)
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 1), Owned(ruid, svuid), IsMe);
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
            Assert.Contains("THIS USER", a.Why);
        }

        [Fact]
        public void ClassifyUnreadable_KillEpermOnMacTargetGone_NotMine()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 1), State(MacProcessStateKind.Gone), IsMe);
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
        }

        /// <summary>Whose process it is cannot be read (the state, or our own uids): Unknown.</summary>
        [Fact]
        public void ClassifyUnreadable_KillEpermOnMacUidsUnreadable_Unknown()
        {
            var noTargetUids = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 1), State(MacProcessStateKind.Unreadable), IsMe);
            var noOwnUids = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 1), Owned(Root, Root), isOurUid: null);
            Assert.True(noTargetUids.IsUnknown, $"{noTargetUids.Identity}: {noTargetUids.Why}");
            Assert.True(noOwnUids.IsUnknown, $"{noOwnUids.Identity}: {noOwnUids.Why}");
        }

        [Fact]
        public void ClassifyUnreadable_KillOtherErrno_Unknown()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(-1, 22), NoStateRead);
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
        }

        /// <summary>Linux: this user's process, start time refused, no second probe - Unknown, never a guess.</summary>
        [Fact]
        public void ClassifyUnreadable_OursOnLinux_Unknown()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, isMac: false, Signal(0, 0), NoStateRead);
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
        }

        [Fact]
        public void ClassifyUnreadable_OursOnMacZombie_NotMine()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(0, 0), State(MacProcessStateKind.Zombie, 5));
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
            Assert.Contains("ZOMBIE", a.Why);
        }

        [Fact]
        public void ClassifyUnreadable_OursOnMacGoneBetweenCalls_NotMine()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(0, 0), State(MacProcessStateKind.Gone));
            Assert.True(a.IsNotMine, $"{a.Identity}: {a.Why}");
        }

        /// <summary>THE PASS 4 PIN - the Review Gate's row: a LIVE process of this user whose start
        /// time is refused (the sandboxed prober). Pass 3 answered NotMine here and deleted a live
        /// capture's claim. It must be Unknown.</summary>
        [Fact]
        public void ClassifyUnreadable_OursOnMacAlive_Unknown()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(0, 0), State(MacProcessStateKind.Alive, 2));
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
            Assert.Contains("ALIVE", a.Why);
        }

        [Fact]
        public void ClassifyUnreadable_OursOnMacStateRefused_Unknown()
        {
            var a = FfmpegChildRegistry.ClassifyUnreadable(4321, 0, "t", false, true, Signal(0, 0), State(MacProcessStateKind.Unreadable));
            Assert.True(a.IsUnknown, $"{a.Identity}: {a.Why}");
        }

        // ---- the blast radius, with the real probe --------------------------------

        /// <summary>The gate's first row: Inspect answers "stale" for a writer pid now held by
        /// another user, and ClearStaleClaim / ThrowIfLive act on that answer.</summary>
        [Fact]
        public void Inspect_WriterPidHeldByAnotherUser_IsAStaleClaim()
        {
            string dir = Crashed("2026-11-15_120000_foreign", ForeignPid);

            var live = CaptureLock.Inspect(dir);
            Assert.False(live.Live, live.Reason);
            Assert.Contains("stale capture claim", live.Reason);

            CaptureLock.ThrowIfLive(dir, "finalize");   // must not throw
            Assert.True(CaptureLock.ClearStaleClaim(dir, "test"));
            Assert.False(File.Exists(Path.Combine(dir, CaptureLock.OwnerFileName)));
        }

        /// <summary>The live arm, so the row above is not a wall: a writer that IS running (this
        /// process, recorded with its real start time) keeps the claim live.</summary>
        [Fact]
        public void Inspect_WriterStillRunning_IsLive()
        {
            using var me = Process.GetCurrentProcess();
            string dir = Crashed("2026-11-15_120000_running", me.Id, me.StartTime.ToUniversalTime());

            var live = CaptureLock.Inspect(dir);

            Assert.True(live.Live, live.Reason);
            Assert.Contains("STILL RUNNING", live.Reason);
        }

        [Fact]
        public void FindUnfinished_WriterPidHeldByAnotherUser_AdmitsTheCrashedRecording()
        {
            string healthy = Crashed("2026-11-14_090000_gone", int.MaxValue - 7);
            string foreign = Crashed("2026-11-15_120000_foreign", ForeignPid);

            var unfinished = PostRecordingPlan.FindUnfinished(_root).Select(Path.GetFileName).ToList();

            Assert.Contains(Path.GetFileName(healthy), unfinished);
            Assert.Contains(Path.GetFileName(foreign), unfinished);
        }

        /// <summary>The half of the blast radius that stopped the repair pass for every recording:
        /// the thumbnail scan completes over a root that holds the foreign-pid recording.</summary>
        [Fact]
        public void FindMissing_RootHoldsAForeignPidRecording_CompletesForEveryRecording()
        {
            // Sorted on both sides of the crash, so neither an early stop nor a late one passes.
            string before = Finished("2026-11-14_090000_finished");
            Crashed("2026-11-15_120000_foreign", ForeignPid);
            string after = Finished("2026-11-16_090000_finished");

            var missing = Thumbnails.FindMissing(_root);

            Assert.Equal(new[] { before, after }, missing);   // the crash itself is still pending its mux
        }

        /// <summary>A finished video recording with no thumbnail: the thumbnail pass's backlog.</summary>
        private string Finished(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "v");
            ManifestStore.Replace(dir, new Manifest { Mode = "video", Label = "video", CreatedUtc = "2026-11-14T09:00:00Z" });
            return dir;
        }

        /// <summary>Criterion 4 on this path: the crashed recording is RECOVERED, not skipped.</summary>
        [Fact]
        public void Resume_WriterPidHeldByAnotherUser_RecoversTheRecording()
        {
            string dir = Crashed("2026-11-15_120000_foreign", ForeignPid);

            var outcome = PostRecording.Resume(dir, hostedWorkAllowed: false);

            Assert.False(outcome.Skipped, "the crashed recording was declined");
            Assert.Single(_muxCalls);
            Assert.Null(Manifest.Load(dir).PendingMux);
            Assert.False(File.Exists(Path.Combine(dir, CaptureLock.OwnerFileName)));
        }

        /// <summary>The start-up reap: a record whose OWNER pid is now another user's process is an
        /// orphan record (owner gone); its child is gone too, so the record is removed - the reap
        /// goes on to the next record instead of aborting.</summary>
        [Fact]
        public void ReapOrphansIn_OwnerPidHeldByAnotherUser_RemovesTheRecord()
        {
            string folder = Path.Combine(_root, "registry");
            Directory.CreateDirectory(folder);
            var e = new FfmpegChildRegistry.Entry
            {
                Pid = int.MaxValue - 9, StartUtc = NeverStarted, OwnerPid = ForeignPid, OwnerStartUtc = NeverStarted,
                Label = "capture video (test)", CaptureWriter = true,
            };
            string file = Path.Combine(folder, e.Pid + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(e));

            var report = FfmpegChildRegistry.ReapOrphansIn(folder, TimeSpan.FromSeconds(1));

            Assert.Contains(report, l => l.Contains($"pid {e.Pid}") && l.Contains("record removed"));
            Assert.False(File.Exists(file));
        }

        // ---- the callers learn the third answer (pass 4) ----------------------------

        private const int UnknownPid = 70001;

        /// <summary>The probe a sandboxed prober effectively has: <see cref="UnknownPid"/> is Unknown,
        /// every other pid is the real answer (so the stale neighbours are genuinely proved gone).</summary>
        private static ProcessAnswer UnknownForOnePid(int pid, DateTime start) =>
            pid == UnknownPid ? ProcessAnswer.Unknown("test: start time refused, process alive") : FfmpegChildRegistry.IdentifyProcess(pid, start);

        private static Manifest.PostStageRecord? MuxRecord(string dir) => PostRecordingState.Get(Manifest.Load(dir), PostStage.Mux);

        /// <summary>An Unknown writer: Inspect surfaces it as live AND undetermined, ThrowIfLive refuses
        /// naming it, ClearStaleClaim keeps the record. Mutation shown failing in the pass 4 handoff:
        /// FreeLockLiveness treating Unknown as gone.</summary>
        [Fact]
        public void Inspect_WriterIdentityUnknown_LiveUndeterminedAndNothingCleared()
        {
            CaptureLock.IdentifyProcess = UnknownForOnePid;
            string dir = Crashed("2026-11-15_120000_unknown", UnknownPid);

            var live = CaptureLock.Inspect(dir);
            Assert.True(live.Live, live.Reason);
            Assert.True(live.Undetermined, live.Reason);
            Assert.Contains("UNDETERMINED", live.Reason);
            Assert.Contains($"pid {UnknownPid}", live.Reason);

            var refused = Assert.Throws<UsageException>(() => CaptureLock.ThrowIfLive(dir, "package"));
            Assert.Contains("UNDETERMINED", refused.Message);

            Assert.False(CaptureLock.ClearStaleClaim(dir, "test"));
            Assert.True(File.Exists(Path.Combine(dir, CaptureLock.OwnerFileName)), "the claim of an undetermined capture was deleted");
        }

        /// <summary>A writer proved running beats an Unknown one: the reason is the actionable "STILL
        /// RUNNING", whichever order the writers were recorded in.</summary>
        [Fact]
        public void Inspect_OneWriterUnknownOneRunning_ReportsStillRunningNotUndetermined()
        {
            CaptureLock.IdentifyProcess = UnknownForOnePid;
            using var me = Process.GetCurrentProcess();
            string dir = Path.Combine(_root, "2026-11-15_120000_mixed");
            Crashed("2026-11-15_120000_mixed", UnknownPid);
            var owner = JsonSerializer.Deserialize<CaptureLock.OwnerRecord>(File.ReadAllText(Path.Combine(dir, CaptureLock.OwnerFileName)))!;
            owner.Writers.Add(new CaptureLock.WriterRecord { Label = "audio", Pid = me.Id, StartUtc = me.StartTime.ToUniversalTime() });
            File.WriteAllText(Path.Combine(dir, CaptureLock.OwnerFileName), JsonSerializer.Serialize(owner));

            var live = CaptureLock.Inspect(dir);

            Assert.True(live.Live, live.Reason);
            Assert.False(live.Undetermined, live.Reason);
            Assert.Contains("STILL RUNNING", live.Reason);
        }

        /// <summary>BOTH ARMS IN ONE PASS: the recovery declines the undetermined recording - no stage,
        /// no attempt, claim kept - AND still recovers its genuinely stale neighbour, whose claim is
        /// cleared. A guard that refused both would fail the second half.</summary>
        [Fact]
        public void Resume_WriterUnknownBesideAStaleClaim_DeclinesOneRecoversTheOther()
        {
            CaptureLock.IdentifyProcess = UnknownForOnePid;
            string unknown = Crashed("2026-11-15_120000_unknown", UnknownPid);
            string stale = Crashed("2026-11-14_090000_stale", int.MaxValue - 7);

            var declined = PostRecording.Resume(unknown, hostedWorkAllowed: false);
            var recovered = PostRecording.Resume(stale, hostedWorkAllowed: false);

            Assert.True(declined.Skipped, "the undetermined recording was admitted");
            Assert.NotNull(Manifest.Load(unknown).PendingMux);
            Assert.Null(MuxRecord(unknown));
            Assert.True(File.Exists(Path.Combine(unknown, CaptureLock.OwnerFileName)), "the undetermined claim was deleted");

            Assert.False(recovered.Skipped, "the stale recording was declined - the guard is a wall");
            Assert.Equal(new[] { stale }, _muxCalls);
            Assert.Null(Manifest.Load(stale).PendingMux);
            Assert.False(File.Exists(Path.Combine(stale, CaptureLock.OwnerFileName)), "the stale claim was not cleared");
        }

        /// <summary>The sweep does not admit the undetermined recording, and still admits the stale one.</summary>
        [Fact]
        public void FindUnfinished_WriterUnknownBesideAStaleClaim_AdmitsOnlyTheStaleOne()
        {
            CaptureLock.IdentifyProcess = UnknownForOnePid;
            string unknown = Crashed("2026-11-15_120000_unknown", UnknownPid);
            string stale = Crashed("2026-11-14_090000_stale", int.MaxValue - 7);

            var unfinished = PostRecordingPlan.FindUnfinished(_root).Select(Path.GetFileName).ToList();

            Assert.Contains(Path.GetFileName(stale), unfinished);
            Assert.DoesNotContain(Path.GetFileName(unknown), unfinished);
        }

        /// <summary>The owning capture's own release: a writer whose identity is Unknown is kept as
        /// running, so the owner record that protects its file is NOT deleted.</summary>
        [Fact]
        public void Release_WriterIdentityUnknown_OwnerRecordKept()
        {
            string dir = Path.Combine(_root, "2026-11-15_120000_release");
            Directory.CreateDirectory(dir);
            using var me = Process.GetCurrentProcess();
            var held = CaptureLock.TryAcquire(dir, "capture session", out string refusal);
            Assert.True(held != null, refusal);
            held!.NoteWriter("video", me.Id);
            CaptureLock.IdentifyProcess = (_, _) => ProcessAnswer.Unknown("test: start time refused, process alive");
            try
            {
                held.Dispose();
                Assert.True(File.Exists(Path.Combine(dir, CaptureLock.OwnerFileName)), "the owner record of an unproved writer was deleted");
            }
            finally
            {
                FfmpegChildRegistry.Unregister(me.Id);
            }
        }

        // ---- the Review Gate's reproduction, as a test: the REAL CLI in the REAL sandbox ----

        /// <summary>
        /// macOS: the gate's arm B, automated. A crashed recording whose owner is gone and whose writer
        /// is a LIVE process of this user (a sleep this test started), probed by the real
        /// <c>agenteyes package</c> from inside the gate's Seatbelt profile. Pass 3 answered "a zombie,
        /// gone" there, cleared the claim and spent an attempt. Now: exit 1, the decline says
        /// UNDETERMINED, no attempt, the claim intact. The control in the same test: the same command
        /// UNsandboxed declines with "STILL RUNNING" (its probe reads the start time). The child's HOME
        /// is this test's temp root, so its AgentEyes log and app data land there, not in the user's.
        /// It starts no ffmpeg: both arms decline before any stage.
        ///
        /// OPT-IN (<see cref="RealCliOptIn.Variable"/>=1; the macOS CI job sets it). On macOS the CLI's
        /// AgentEyes log is NOT moved by HOME (issue #161) - measured: the first run of this test
        /// appended four lines to the real per-user log - and a default-gate test must never write
        /// there. The row it proves end to end is pinned in the default gate by
        /// <see cref="ClassifyUnreadable_OursOnMacAlive_Unknown"/>.
        /// </summary>
        [RealCliOptInFact]
        public void PackageCli_SandboxedProberLiveWriter_DeclinesUndeterminedAndKeepsTheClaim()
        {
            Assert.True(File.Exists("/usr/bin/sandbox-exec"), "precondition: /usr/bin/sandbox-exec is missing on this Mac");
            string cli = Path.Combine(AppContext.BaseDirectory, "agenteyes");
            Assert.True(File.Exists(cli), $"precondition: the agenteyes CLI is not next to the tests ({cli})");

            using var writer = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
            try
            {
                DateTime writerStart = writer.StartTime.ToUniversalTime();
                string sandboxed = Crashed("2026-11-15_120000_sandboxed", writer.Id, writerStart);
                string signalDenied = Crashed("2026-11-15_123000_signal-denied", writer.Id, writerStart);
                string plain = Crashed("2026-11-15_130000_plain", writer.Id, writerStart);

                var (sbExit, sbOut) = RunCli(cli, GateSandboxProfile, "package", sandboxed, "--no-frames");
                var (sdExit, sdOut) = RunCli(cli, SignalDenyingSandboxProfile, "package", signalDenied, "--no-frames");
                var (plExit, plOut) = RunCli(cli, profile: null, "package", plain, "--no-frames");

                Assert.False(writer.HasExited, "precondition: the writer died during the test");
                Assert.True(sbExit == 1, $"sandboxed package exit {sbExit}, expected the decline (1):\n{sbOut}");
                Assert.Contains("UNDETERMINED", sbOut);
                Assert.True(File.Exists(Path.Combine(sandboxed, CaptureLock.OwnerFileName)), "the sandboxed prober deleted a live capture's claim:\n" + sbOut);
                Assert.Null(MuxRecord(sandboxed));
                Assert.NotNull(Manifest.Load(sandboxed).PendingMux);

                Assert.True(sdExit == 1, $"signal-denied package exit {sdExit}, expected the decline (1):\n{sdOut}");
                Assert.Contains("UNDETERMINED", sdOut);
                Assert.True(File.Exists(Path.Combine(signalDenied, CaptureLock.OwnerFileName)), "the signal-denied prober deleted a live capture's claim:\n" + sdOut);
                Assert.Null(MuxRecord(signalDenied));

                Assert.True(plExit == 1, $"plain package exit {plExit}, expected the decline (1):\n{plOut}");
                Assert.Contains("STILL RUNNING", plOut);
                Assert.True(File.Exists(Path.Combine(plain, CaptureLock.OwnerFileName)));
            }
            finally
            {
                if (!writer.HasExited) { writer.Kill(); writer.WaitForExit(5000); }
            }
        }

        private (int Exit, string Output) RunCli(string cli, string? profile, params string[] args)
        {
            bool sandbox = profile != null;
            var psi = new ProcessStartInfo(sandbox ? "/usr/bin/sandbox-exec" : cli)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (sandbox)
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(profile!);
                psi.ArgumentList.Add(cli);
            }
            foreach (string a in args) psi.ArgumentList.Add(a);
            string home = Path.Combine(_root, "home");
            Directory.CreateDirectory(home);
            psi.Environment["HOME"] = home;
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60000), "the CLI did not finish within 60 s");
            return (p.ExitCode, output + err.Result);
        }

        private static string PsState(int pid) => Ps("stat=", pid);

        private static string PsUser(int pid) => Ps("user=", pid);

        private static string Ps(string field, int pid)
        {
            var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string a in new[] { "-o", field, "-p", pid.ToString() }) psi.ArgumentList.Add(a);
            using var ps = Process.Start(psi)!;
            string s = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return s;
        }
    }

    /// <summary>
    /// Opt-in for tests that run the REAL agenteyes CLI as a child process (issue #156 pass 4). On
    /// macOS that child writes the user's REAL AgentEyes log - HOME does not redirect it (#161) and the
    /// CLI has no test redirect - so these are SKIPPED, visibly, unless AGENTEYES_REAL_CLI_TESTS=1.
    /// CI sets it: a hosted runner has no user log to protect.
    /// </summary>
    internal static class RealCliOptIn
    {
        public const string Variable = "AGENTEYES_REAL_CLI_TESTS";

        public static string? SkipReason =>
            !OperatingSystem.IsMacOS()
                ? "macOS only: it drives the CLI under sandbox-exec, which exists only on macOS"
                : Environment.GetEnvironmentVariable(Variable) == "1"
                    ? null
                    : $"runs the real agenteyes CLI, which writes this user's real AgentEyes log on macOS (#161) - opt in with {Variable}=1";
    }

    /// <summary>
    /// Issue #156 pass 5c: whether a live subject whose /proc owner uid and gid DIFFER can be built
    /// here - this process's egid already differs from its euid, or this user has a supplementary group
    /// whose gid differs and /usr/bin/sg exists. If not, the uid-vs-gid pin is SKIPPED, visibly, with
    /// the reason, rather than passing on a subject that cannot tell the two offsets apart. Off Linux
    /// it runs and returns, like the other Linux-only tests in this class.
    /// </summary>
    internal static class LinuxUidGidSubject
    {
        private static readonly Lazy<string?> Reason = new(() =>
        {
            if (!OperatingSystem.IsLinux()) return null;
            uint euid = uint.Parse(Id("-u"));
            if (uint.Parse(Id("-g")) != euid) return null;
            if (!File.Exists("/usr/bin/sg")) return "this user's uid and gid are equal and /usr/bin/sg is missing, so no subject can tell stx_uid from stx_gid";
            return SupplementaryGroupNotEqualTo(euid) is null
                ? $"this user's uid and gid are equal ({euid}) and no supplementary group differs, so no subject can tell stx_uid from stx_gid"
                : null;
        });

        public static string? SkipReason => Reason.Value;

        /// <summary>The name of one of this user's groups whose gid is not <paramref name="uid"/>, or null.</summary>
        public static string? SupplementaryGroupNotEqualTo(uint uid)
        {
            var gids = Id("-G").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var names = Id("-Gn").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (gids.Length != names.Length) return null;
            for (int i = 0; i < gids.Length; i++)
                if (uint.Parse(gids[i]) != uid) return names[i];
            return null;
        }

        private static string Id(string arg)
        {
            var psi = new ProcessStartInfo("/usr/bin/id", arg) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
    }

    /// <summary>A <see cref="FactAttribute"/> skipped, with the reason, where no uid != gid subject can be built.</summary>
    public sealed class LinuxUidGidSubjectFactAttribute : FactAttribute
    {
        public LinuxUidGidSubjectFactAttribute() { Skip = LinuxUidGidSubject.SkipReason; }
    }

    /// <summary>A <see cref="FactAttribute"/> that runs only when <see cref="RealCliOptIn.Variable"/> is 1.</summary>
    public sealed class RealCliOptInFactAttribute : FactAttribute
    {
        public RealCliOptInFactAttribute() { Skip = RealCliOptIn.SkipReason; }
    }
}
