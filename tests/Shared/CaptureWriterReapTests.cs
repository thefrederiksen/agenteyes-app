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
    /// Issue #156 pass 2 (D1): the start-up reaper must end an orphaned CAPTURE ffmpeg gracefully -
    /// SIGTERM, a bounded wait, and SIGKILL only if it is still running - because a plain MP4 killed
    /// outright has no moov atom and the crashed recording is lost. QA measured exactly that: the
    /// SIGKILLed orphan's recording failed its mux, the SIGTERMed one was a complete 142.5 s file.
    ///
    /// Compiled into all three suites. The ordering tests use injected operations, so they pin the
    /// ORDER independently of any platform; the real-process tests run the real signal and kill.
    /// On Windows the real-process tests assert that platform's contract instead (no graceful
    /// request exists, so the writer is killed at once and the report says so) - not skipped.
    /// </summary>
    public sealed class CaptureWriterReapTests : IDisposable
    {
        private readonly string _folder;
        private static readonly DateTime OwnerStart = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime WriterStart = new(2026, 11, 16, 10, 0, 5, DateTimeKind.Utc);

        public CaptureWriterReapTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "agenteyes-capreap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }

        private void Record(int pid, bool captureWriter, DateTime? start = null, int ownerPid = 100)
        {
            var e = new FfmpegChildRegistry.Entry
            {
                Pid = pid, StartUtc = start ?? WriterStart, OwnerPid = ownerPid, OwnerStartUtc = OwnerStart,
                Label = captureWriter ? $"capture video (2026-11-16_100000_video)" : "process mic audio",
                CaptureWriter = captureWriter,
            };
            File.WriteAllText(Path.Combine(_folder, pid + ".json"), JsonSerializer.Serialize(e));
        }

        // ---- the order, with injected operations ---------------------------------

        /// <summary>THE PIN: a capture writer that ignores SIGTERM gets SIGTERM FIRST, the bounded
        /// wait, and only then SIGKILL. A refactor that kills first, or never kills, fails here.</summary>
        [Fact]
        public void ReapOrphans_CaptureWriterIgnoresSigterm_SigtermThenWaitThenSigkill()
        {
            Record(pid: 500, captureWriter: true);
            var calls = new List<string>();
            var grace = TimeSpan.FromMilliseconds(300);
            var clock = Stopwatch.StartNew();
            TimeSpan killedAt = TimeSpan.Zero;

            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                TestIdentity.From((pid, _) => pid == 500),                       // owner gone; the writer never exits by itself
                pid => { calls.Add($"TERM {pid}"); return true; },
                pid => { calls.Add($"KILL {pid}"); killedAt = clock.Elapsed; },
                grace);

            Assert.Equal(new[] { "TERM 500", "KILL 500" }, calls);
            Assert.True(killedAt >= grace, $"SIGKILL came {killedAt.TotalMilliseconds:F0} ms after the start - before the {grace.TotalMilliseconds:F0} ms grace ran out");
            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("SIGTERM, did NOT exit") && l.Contains("SIGKILLED"));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        /// <summary>The graceful path: a capture writer that exits on SIGTERM is never killed, and the
        /// report says it finished its file.</summary>
        [Fact]
        public void ReapOrphans_CaptureWriterExitsOnSigterm_IsNeverKilled()
        {
            Record(pid: 500, captureWriter: true);
            bool termed = false;
            var killed = new List<int>();

            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                TestIdentity.From((pid, _) => pid == 500 && !termed),
                pid => { termed = true; return true; },
                killed.Add,
                TimeSpan.FromSeconds(30));

            Assert.True(termed, "the capture writer was never sent SIGTERM");
            Assert.Empty(killed);
            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("SIGTERM, exited after") && l.Contains("graceful"));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        /// <summary>Several orphaned writers share ONE deadline: all are signalled before the wait,
        /// so a reap of two stubborn writers takes one grace, not two.</summary>
        [Fact]
        public void ReapOrphans_TwoStubbornCaptureWriters_BothSignalledFirstAndOneSharedDeadline()
        {
            Record(pid: 500, captureWriter: true);
            Record(pid: 501, captureWriter: true);
            var calls = new List<string>();
            var grace = TimeSpan.FromMilliseconds(400);
            var clock = Stopwatch.StartNew();

            FfmpegChildRegistry.ReapOrphans(_folder,
                TestIdentity.From((pid, _) => pid is 500 or 501),
                pid => { calls.Add($"TERM {pid}"); return true; },
                pid => calls.Add($"KILL {pid}"),
                grace);
            var took = clock.Elapsed;

            Assert.Equal(4, calls.Count);
            Assert.All(calls.Take(2), c => Assert.StartsWith("TERM", c));
            Assert.All(calls.Skip(2), c => Assert.StartsWith("KILL", c));
            Assert.True(took < grace + grace, $"two writers took {took.TotalMilliseconds:F0} ms - the deadline is per writer, not shared");
        }

        /// <summary>Control: a post-processing child is still killed at once - its output is
        /// disposable - and is never sent a graceful request.</summary>
        [Fact]
        public void ReapOrphans_PostProcessingChild_KilledAtOnceWithoutAGracefulRequest()
        {
            Record(pid: 600, captureWriter: false);
            var calls = new List<string>();

            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                TestIdentity.From((pid, _) => pid == 600),
                pid => { calls.Add($"TERM {pid}"); return true; },
                pid => calls.Add($"KILL {pid}"),
                TimeSpan.FromSeconds(30));

            Assert.Equal(new[] { "KILL 600" }, calls);
            Assert.Contains(report, l => l.Contains("pid 600") && l.Contains("ORPHAN") && l.Contains("killed"));
        }

        /// <summary>A platform with no graceful request kills the writer at once and SAYS so,
        /// rather than waiting on nothing.</summary>
        [Fact]
        public void ReapOrphans_NoGracefulRequestOnThisPlatform_KillsAtOnceAndSaysSo()
        {
            Record(pid: 500, captureWriter: true);
            var killed = new List<int>();

            var report = FfmpegChildRegistry.ReapOrphans(_folder, TestIdentity.From((pid, _) => pid == 500), _ => false, killed.Add, TimeSpan.FromSeconds(30));

            Assert.Equal(new[] { 500 }, killed);
            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("no graceful stop") && l.Contains("KILLED at once"));
        }

        /// <summary>Control: an owner that is alive means its writer is left alone - no signal at all.</summary>
        [Fact]
        public void ReapOrphans_CaptureWriterOwnerAlive_NoSignalSent()
        {
            Record(pid: 500, captureWriter: true);

            var report = FfmpegChildRegistry.ReapOrphans(_folder, TestIdentity.From((_, _) => true),
                _ => throw new Exception("must not signal a live owner's writer"),
                _ => throw new Exception("must not kill a live owner's writer"),
                TimeSpan.FromSeconds(30));

            Assert.Contains(report, l => l.Contains("owner pid 100 is alive"));
            Assert.Single(Directory.GetFiles(_folder));
        }

        // ---- the third answer (issue #156 pass 4): Unknown is never acted on -----------

        private static ProcessAnswer Unknown => ProcessAnswer.Unknown("test: start time refused, process alive");

        /// <summary>An owner whose liveness is Unknown is not proved gone: no signal, no kill, record kept.</summary>
        [Fact]
        public void ReapOrphans_OwnerIdentityUnknown_LeftAloneRecordKept()
        {
            Record(pid: 500, captureWriter: true);

            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                (pid, _) => pid == 100 ? Unknown : ProcessAnswer.Mine("writer"),
                _ => throw new Exception("must not signal when the owner may be alive"),
                _ => throw new Exception("must not kill when the owner may be alive"),
                TimeSpan.FromSeconds(30));

            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("UNKNOWN whether owner pid 100") && l.Contains("record kept"));
            Assert.Single(Directory.GetFiles(_folder));
        }

        /// <summary>The owner is proved gone but the writer pid's identity is Unknown: it is not
        /// signalled (it may not be our ffmpeg), and its record is NOT removed as "already exited".</summary>
        [Fact]
        public void ReapOrphans_ChildIdentityUnknown_NotSignalledRecordKept()
        {
            Record(pid: 500, captureWriter: true);
            Record(pid: 600, captureWriter: false);

            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                (pid, _) => pid == 100 ? ProcessAnswer.NotMine("owner gone") : Unknown,
                _ => throw new Exception("must not signal a process whose identity is unproved"),
                _ => throw new Exception("must not kill a process whose identity is unproved"),
                TimeSpan.FromSeconds(30));

            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("not signalled") && l.Contains("record kept"));
            Assert.Contains(report, l => l.Contains("pid 600") && l.Contains("not signalled") && l.Contains("record kept"));
            Assert.Equal(2, Directory.GetFiles(_folder).Length);
        }

        /// <summary>Signalled while proved ours, then Unknown at the deadline: a kill needs proof, so it
        /// is NOT killed and the record stays for the next reap.</summary>
        [Fact]
        public void ReapOrphans_WriterUnknownAfterSigterm_NotKilledAtTheDeadline()
        {
            Record(pid: 500, captureWriter: true);
            bool termed = false;
            var calls = new List<string>();

            var report = FfmpegChildRegistry.ReapOrphans(_folder,
                (pid, _) => pid == 100 ? ProcessAnswer.NotMine("owner gone") : termed ? Unknown : ProcessAnswer.Mine("writer"),
                pid => { calls.Add($"TERM {pid}"); termed = true; return true; },
                pid => calls.Add($"KILL {pid}"),
                TimeSpan.FromMilliseconds(200));

            Assert.Equal(new[] { "TERM 500" }, calls);
            Assert.Contains(report, l => l.Contains("pid 500") && l.Contains("UNKNOWN") && l.Contains("NOT killed"));
            Assert.Single(Directory.GetFiles(_folder));
        }

        [Fact]
        public void CaptureWriterGrace_IsTheMeasuredBound()
        {
            // Changing the bound must be a deliberate act backed by a new measurement (see its
            // comment in FfmpegChildRegistry and docs/cencon/proof/issue-156/handoff.md, pass 2).
            Assert.Equal(TimeSpan.FromSeconds(MeasuredBoundSeconds), FfmpegChildRegistry.CaptureWriterGrace);
        }

        private const int MeasuredBoundSeconds = 10;

        // ---- the real signal and kill --------------------------------------------

        /// <summary>A real orphaned writer that finishes on SIGTERM exits by ITSELF (exit code 0), not
        /// by the kill. On Windows, the platform contract: killed at once, and the report says so.</summary>
        [Fact]
        public void ReapOrphansIn_RealWriterThatHandlesSigterm_ExitsGracefully()
        {
            using var child = StartChild(handlesSigterm: true);
            try
            {
                RecordReal(child);

                var report = FfmpegChildRegistry.ReapOrphansIn(_folder, TimeSpan.FromSeconds(10));

                Assert.True(child.WaitForExit(10_000), "the orphan was still running after the reap");
                if (OperatingSystem.IsWindows())
                {
                    Assert.Contains(report, l => l.Contains($"pid {child.Id}") && l.Contains("no graceful stop"));
                }
                else
                {
                    Assert.Contains(report, l => l.Contains($"pid {child.Id}") && l.Contains("graceful"));
                    Assert.Equal(0, child.ExitCode);   // its own trap ran: SIGTERM, not SIGKILL
                }
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }

        /// <summary>A real orphaned writer that IGNORES SIGTERM is still ended: SIGKILL after the
        /// bound. A graceful stop that can hang for ever is a new way to strand the service.</summary>
        [Fact]
        public void ReapOrphansIn_RealWriterThatIgnoresSigterm_IsSigkilledAfterTheBound()
        {
            using var child = StartChild(handlesSigterm: false);
            try
            {
                RecordReal(child);
                var grace = TimeSpan.FromMilliseconds(700);
                var clock = Stopwatch.StartNew();

                var report = FfmpegChildRegistry.ReapOrphansIn(_folder, grace);

                Assert.True(child.WaitForExit(10_000), "a writer that ignores SIGTERM was left running");
                if (!OperatingSystem.IsWindows())
                {
                    Assert.True(clock.Elapsed >= grace, "SIGKILL came before the bound ran out");
                    Assert.Contains(report, l => l.Contains($"pid {child.Id}") && l.Contains("did NOT exit") && l.Contains("SIGKILLED"));
                    Assert.NotEqual(0, child.ExitCode);
                }
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }

        private void RecordReal(Process child)
        {
            var e = new FfmpegChildRegistry.Entry
            {
                Pid = child.Id, StartUtc = child.StartTime.ToUniversalTime(),
                OwnerPid = int.MaxValue - 7, OwnerStartUtc = DateTime.MinValue,   // an owner that cannot exist
                Label = "capture video (test)", CaptureWriter = true,
            };
            File.WriteAllText(Path.Combine(_folder, child.Id + ".json"), JsonSerializer.Serialize(e));
            Assert.False(child.HasExited, "precondition: the writer is running before the reap");
        }

        /// <summary>A stand-in writer: a shell loop that either exits 0 on SIGTERM or ignores it.
        /// Polls in short sleeps so a trap runs within ~50 ms of the signal.</summary>
        private static Process StartChild(bool handlesSigterm)
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1") { CreateNoWindow = true, RedirectStandardOutput = true }
                : new ProcessStartInfo("/bin/sh", new[] { "-c", (handlesSigterm ? "trap 'exit 0' TERM; " : "trap '' TERM; ") + "while :; do sleep 0.05; done" });
            psi.UseShellExecute = false;
            var p = Process.Start(psi)!;
            if (!OperatingSystem.IsWindows()) System.Threading.Thread.Sleep(200);   // let the shell install its trap
            return p;
        }
    }
}
