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
    /// Issue #156 pass 3: the ownership predicate <see cref="FfmpegChildRegistry.IsSameProcess"/> is
    /// TOTAL - it answers, it never throws - and every answer is pinned here with the REAL probe (no
    /// seam on it anywhere in this class).
    ///
    /// The defect it pins: on macOS, a recorded pid now held by ANOTHER USER's process (the normal
    /// state after a crash and a reboot - low pids go to root daemons) made Process.StartTime throw
    /// Win32Exception. That threw out of CaptureLock.Inspect / ClearStaleClaim / ThrowIfLive, so
    /// PostRecordingPlan.FindUnfinished skipped the crashed recording on every pass, and
    /// Thumbnails.FindMissing threw, which stopped thumbnail backfill, housekeeping and the queue
    /// drain for every recording on the machine. Linux reads any process's start time, so the hole
    /// was invisible there.
    ///
    /// "Another user's process" is pid 1 on macOS and Linux: root-owned, always present, with a
    /// start time a record can never match. On Windows it is pid 4 (System), the nearest analogue;
    /// there the same tests assert the same answers - they are not skipped.
    ///
    /// In the capture-ownership collection: the blast-radius tests read
    /// <see cref="CaptureLock.IsSameProcess"/> and <see cref="CaptureLock.ThisHostId"/>, which other
    /// tests in that collection swap.
    /// </summary>
    [Collection(CaptureOwnershipCollection.Name)]
    public sealed class ProcessIdentityTests : IDisposable
    {
        private static readonly DateTime NeverStarted = new(2026, 11, 15, 12, 0, 1, DateTimeKind.Utc);

        private readonly string _root;
        private readonly Func<int, DateTime, bool> _seamAtStart = CaptureLock.IsSameProcess;
        private readonly Func<string> _realHostId = CaptureLock.ThisHostId;
        private readonly List<string> _muxCalls = new();

        public ProcessIdentityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-pid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            // The production probe, whatever an earlier test in the collection left behind.
            CaptureLock.IsSameProcess = FfmpegChildRegistry.IsSameProcess;
            PostRecording.MuxStep = MuxStub;
            PostRecording.ThumbnailStep = _ => { };
            PostRecording.ComposeStep = _ => { };
        }

        public void Dispose()
        {
            CaptureLock.IsSameProcess = _seamAtStart;
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
        /// FREE capture.lock, and a same-host capture.owner.json whose owner is gone and whose one
        /// writer is <paramref name="writerPid"/>. The gate's reproduction, unchanged in shape.
        /// </summary>
        private string Crashed(string name, int writerPid, DateTime? writerStart = null)
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
            File.WriteAllText(Path.Combine(dir, CaptureLock.OwnerFileName), JsonSerializer.Serialize(owner));
            return dir;
        }

        // ---- the predicate's mapping, one test per outcome -----------------------

        /// <summary>THE PIN: another user's process is answered "not the recorded process" - no
        /// exception. A predicate made to throw again (or to answer true) fails here.</summary>
        [Fact]
        public void IsSameProcess_PidHeldByAnotherUsersProcess_ReturnsFalseWithoutThrowing()
        {
            Assert.False(FfmpegChildRegistry.IsSameProcess(ForeignPid, NeverStarted));
        }

        /// <summary>The precondition that makes the test above mean something: on macOS and Linux,
        /// pid 1 IS another user's process to this test. Not where it is not: as root (which may
        /// signal anything), or in a container whose pid 1 runs as the test's own user. On Linux the
        /// test above is answered by the start-time comparison either way; only macOS reaches the
        /// unreadable-start-time mapping through pid 1.</summary>
        [Fact]
        public void DescribeUnreadable_PidOne_SaysAnotherUser()
        {
            if (OperatingSystem.IsWindows() || RunningAsRoot) return;   // no kill(2); root may signal pid 1
            if (PsUser(1) == Environment.UserName) return;               // pid 1 is ours here (a container)
            Assert.Contains("ANOTHER USER", FfmpegChildRegistry.DescribeUnreadable(1));
        }

        [Fact]
        public void IsSameProcess_NoSuchProcess_ReturnsFalse()
        {
            Assert.False(FfmpegChildRegistry.IsSameProcess(int.MaxValue - 7, NeverStarted));
        }

        [Fact]
        public void IsSameProcess_OwnProcessDifferentStartTime_ReturnsFalse()
        {
            Assert.False(FfmpegChildRegistry.IsSameProcess(Environment.ProcessId, NeverStarted));
        }

        /// <summary>The positive control: without it, a predicate that answers false for everything
        /// passes every test above - and declares every live capture stale.</summary>
        [Fact]
        public void IsSameProcess_OwnProcessRecordedStartTime_ReturnsTrue()
        {
            using var me = Process.GetCurrentProcess();
            Assert.True(FfmpegChildRegistry.IsSameProcess(me.Id, me.StartTime.ToUniversalTime()));
        }

        /// <summary>
        /// The one case of "this user's process, start time unreadable", measured on macOS: a ZOMBIE
        /// (kill(pid, 0) succeeds, HasExited is false, proc_pidinfo refuses). It has exited, so it is
        /// not a running writer. Made by a shell that backgrounds a short sleep and then execs a long
        /// one, which never waits for the first. macOS only: Windows has no zombies, and Linux READS a
        /// zombie's start time from /proc, so there the answer comes from the start-time comparison
        /// (the tests above), not from this mapping.
        /// </summary>
        [Fact]
        public void IsSameProcess_ThisUsersZombie_ReturnsFalse()
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

                Assert.False(FfmpegChildRegistry.IsSameProcess(zombie, DateTime.UtcNow));
            }
            finally
            {
                parent.Kill(entireProcessTree: true);
            }
        }

        /// <summary>kill(2) reads 0 and below as a process GROUP (0: the caller's own group, -1:
        /// every process). The classifier must never send to one.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void DescribeUnreadable_PidZeroOrNegative_IsCorruptNotAGroupSignal(int pid)
        {
            Assert.Contains("not a process id", FfmpegChildRegistry.DescribeUnreadable(pid));
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
}
