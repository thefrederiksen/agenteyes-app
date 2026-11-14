using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #128 criteria 9/10: a post-processing ffmpeg must not outlive the AgentEyes that started
    /// it. The reaper must kill a recorded child ONLY when the owner is provably gone AND the pid is
    /// provably the same process - every other case is left alone (a reused pid is never killed).
    /// </summary>
    public sealed class FfmpegChildRegistryTests : IDisposable
    {
        private readonly string _folder;
        private static readonly DateTime OwnerStart = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime ChildStart = new(2026, 11, 14, 10, 5, 0, DateTimeKind.Utc);

        public FfmpegChildRegistryTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "agenteyes-reap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }

        private void Record(int pid, int ownerPid)
        {
            var e = new FfmpegChildRegistry.Entry
            {
                Pid = pid, StartUtc = ChildStart, OwnerPid = ownerPid, OwnerStartUtc = OwnerStart, Label = "process mic audio",
            };
            File.WriteAllText(Path.Combine(_folder, pid + ".json"), JsonSerializer.Serialize(e));
        }

        [Fact]
        public void ReapOrphans_OwnerGoneChildAlive_KillsTheChildAndRemovesTheRecord()
        {
            Record(pid: 500, ownerPid: 100);
            var killed = new List<int>();
            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                (pid, start) => pid == 500 && start == ChildStart,   // owner 100 gone, child 500 alive
                killed.Add);

            Assert.Equal(new[] { 500 }, killed);
            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("ORPHAN") && l.Contains("killed"));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void ReapOrphans_OwnerAlive_LeavesTheChildAndItsRecord()
        {
            Record(pid: 500, ownerPid: 100);
            var killed = new List<int>();
            var report = FfmpegChildRegistry.ReapOrphans(_folder, (pid, start) => true, killed.Add);

            Assert.Empty(killed);
            Assert.Contains(report, l => l.Contains("owner pid 100 is alive"));
            Assert.Single(Directory.GetFiles(_folder));
        }

        [Fact]
        public void ReapOrphans_PidReusedByAnotherProcess_IsNeverKilled()
        {
            // The pid exists but started at a different time: not our ffmpeg. The probe says "not the
            // same process" for both the owner and the child.
            Record(pid: 500, ownerPid: 100);
            var killed = new List<int>();
            var report = FfmpegChildRegistry.ReapOrphans(_folder, (pid, start) => false, killed.Add);

            Assert.Empty(killed);
            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("record removed"));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void ReapOrphans_NoFolder_ReturnsEmptyReport()
        {
            var report = FfmpegChildRegistry.ReapOrphans(Path.Combine(_folder, "missing"), (p, s) => true, _ => throw new Exception("must not kill"));
            Assert.Empty(report);
        }

        [Fact]
        public void ReapOrphans_RealOrphanedProcess_IsKilled()
        {
            // A real long-running child recorded under an owner that does not exist (pid 0 start
            // MinValue can never match), reaped with the REAL probe and kill.
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }
                : new ProcessStartInfo("sleep", "120") { UseShellExecute = false };
            using var child = Process.Start(psi)!;
            try
            {
                var e = new FfmpegChildRegistry.Entry
                {
                    Pid = child.Id, StartUtc = child.StartTime.ToUniversalTime(),
                    OwnerPid = int.MaxValue - 7, OwnerStartUtc = DateTime.MinValue, Label = "test",
                };
                File.WriteAllText(Path.Combine(_folder, child.Id + ".json"), JsonSerializer.Serialize(e));
                Assert.False(child.HasExited, "precondition: the child is running before the reap");

                var report = FfmpegChildRegistry.ReapOrphansIn(_folder);

                Assert.True(child.WaitForExit(10_000), "the orphan was still running after the reap");
                Assert.Contains(report, l => l.Contains($"pid {child.Id}") && l.Contains("killed"));
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }
    }
}
