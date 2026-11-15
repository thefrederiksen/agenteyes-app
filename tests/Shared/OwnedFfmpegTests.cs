using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #151: a test's failure path kills only the ffmpeg processes that test started.
    /// Compiled into all three suites. The pure tests run in the default gate; the one that runs a
    /// real ffmpeg is opt-in (AGENTEYES_FFMPEG_TESTS=1) like every other test that launches one.
    /// </summary>
    public sealed class OwnedFfmpegTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public OwnedFfmpegTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        private static readonly DateTime T1 = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime T2 = T1.AddSeconds(3);

        // ---- the decision (pure, default gate) ---------------------------------------------------

        [Fact]
        public void KillOwned_TwoRecordedChildren_KillsExactlyThoseTwo()
        {
            var children = new[] { new OwnedFfmpeg.Child(101, T1), new OwnedFfmpeg.Child(102, T2) };
            var probed = new List<int>();
            var killed = new List<int>();

            var report = OwnedFfmpeg.KillOwned(children, (pid, _) => { probed.Add(pid); return true; }, killed.Add);

            // Exactly the recorded pids were probed and killed - nothing else on the machine was even looked at.
            Assert.Equal(new[] { 101, 102 }, killed);
            Assert.Equal(new[] { 101, 102 }, probed);
            Assert.Equal(new[] { "pid 101: killed", "pid 102: killed" }, report);
        }

        [Fact]
        public void KillOwned_PidReusedSinceItWasRecorded_LeavesItAlone()
        {
            // The pid now names a different process (its start time no longer matches).
            var children = new[] { new OwnedFfmpeg.Child(101, T1) };
            var killed = new List<int>();

            var report = OwnedFfmpeg.KillOwned(children, (_, start) => start == T2, killed.Add);

            Assert.Empty(killed);
            Assert.Equal("pid 101: already exited (or the pid was reused) - left alone", Assert.Single(report));
        }

        [Fact]
        public void KillOwned_StartTimeNeverRead_LeavesItAloneWithoutProbing()
        {
            var children = new[] { new OwnedFfmpeg.Child(101, null) };
            var probed = new List<int>();
            var killed = new List<int>();

            var report = OwnedFfmpeg.KillOwned(children, (pid, _) => { probed.Add(pid); return true; }, killed.Add);

            Assert.Empty(probed);
            Assert.Empty(killed);
            Assert.Equal("pid 101: start time never read - not provably ours, left alone", Assert.Single(report));
        }

        [Fact]
        public void OwnedFfmpeg_Dispose_RestoresThePreviousObserver()
        {
            Action<Process>? before = Ffmpeg.ChildStartedObserver.Value;
            using (var outer = new OwnedFfmpeg())
            {
                Action<Process>? outerObserver = Ffmpeg.ChildStartedObserver.Value;
                Assert.NotNull(outerObserver);
                using (new OwnedFfmpeg())
                    Assert.NotSame(outerObserver, Ffmpeg.ChildStartedObserver.Value);
                Assert.Same(outerObserver, Ffmpeg.ChildStartedObserver.Value);
            }
            Assert.Same(before, Ffmpeg.ChildStartedObserver.Value);
        }

        /// <summary>
        /// The defect was a sweep over every ffmpeg on the machine. No test may bring one back. The
        /// scan is checked for PRESENCE first - a scan that read no files, or did not read the file
        /// the old sweep lived in, would pass by finding nothing.
        /// </summary>
        [Fact]
        public void TestSources_NoTestSweepsEveryFfmpegOnTheMachine()
        {
            string root = RepoRoot();
            var files = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.True(files.Count >= 50, $"only {files.Count} test sources found under {root} - broken instrument");
            Assert.Contains(files, f => Path.GetFileName(f) == "MicChainStallTests.cs");

            // Built from pieces so this file does not match itself.
            string sweep = "GetProcessesBy" + "Name(";
            var offenders = files
                .Where(f => File.ReadAllLines(f).Any(l => l.Contains(sweep) && l.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)))
                .Select(f => Path.GetRelativePath(root, f))
                .ToList();
            Assert.True(offenders.Count == 0,
                "a test sweeps ffmpeg by name - kill only the pids the test started (OwnedFfmpeg): " + string.Join(", ", offenders));
        }

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "AgentEyes.sln"))) return d.FullName;
            throw new InvalidOperationException($"AgentEyes.sln not found above {AppContext.BaseDirectory}");
        }

        // ---- the hazard (real ffmpeg, opt-in) ------------------------------------------------------

        /// <summary>
        /// Criteria 4 and 5. Three ffmpeg processes, all started AFTER the test began, so the old
        /// start-time sweep would have killed all three:
        ///  - OWNED: started by the product runner in this test's flow, and stuck - the failure path;
        ///  - RECORDER: a plain ffmpeg this flow did not start through the runner - the stand-in for a
        ///    live recording on the same machine;
        ///  - PARALLEL: started by the product runner in ANOTHER flow, under its own OwnedFfmpeg - a
        ///    parallel test.
        /// The failure path must kill OWNED, leave it no way to keep spinning, and leave the other two running.
        /// </summary>
        [FfmpegOptInFact]
        public void KillOwned_FailurePath_KillsTheStuckRunAndLeavesUnrelatedFfmpegRunning()
        {
            DateTime testBegan = DateTime.Now.AddSeconds(-1);
            string exe = FfmpegLocator.Ffmpeg();
            Process? recorder = null;
            OwnedFfmpeg? parallelScope = null;
            Task? parallelRun = null;
            try
            {
                // RECORDER: not started through the runner, so nothing records it.
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "sine=f=440:d=120", "-f", "null", "-" })
                    psi.ArgumentList.Add(a);
                recorder = Process.Start(psi)!;

                // PARALLEL: a separate flow (the observer must not flow into it), with its own scope.
                var parallelReady = new ManualResetEventSlim();
                using (ExecutionContext.SuppressFlow())
                {
                    parallelRun = OnOwnThread(() =>
                    {
                        parallelScope = new OwnedFfmpeg();
                        parallelReady.Set();
                        Ffmpeg.Run(LongRun(), "test parallel");
                    });
                }
                Assert.True(parallelReady.Wait(TimeSpan.FromSeconds(60)), "the parallel flow never started");

                // OWNED: stuck from this test's point of view (a 120 s run, a 5 minute stall bound).
                using var owned = new OwnedFfmpeg();
                var run = OnOwnThread(() => Ffmpeg.RunWatched(LongRun(), "test owned", TimeSpan.FromMinutes(5)));

                Assert.True(SpinWait.SpinUntil(() => owned.Children.Count == 1 && parallelScope!.Children.Count == 1, TimeSpan.FromSeconds(60)),
                    $"children not recorded: owned {owned.Children.Count}, parallel {parallelScope!.Children.Count} - broken instrument");
                var mine = owned.Children[0];
                var theirs = parallelScope!.Children[0];
                Assert.NotNull(mine.StartUtc);
                Assert.NotNull(theirs.StartUtc);
                Assert.NotEqual(mine.Pid, theirs.Pid);
                Assert.NotEqual(recorder.Id, mine.Pid);
                Assert.False(Finishes(run, TimeSpan.FromSeconds(2)), "the owned run returned on its own - this is not the failure path");

                // All three are what the old sweep selected: an ffmpeg that started after the test began.
                foreach (var (name, pid) in new[] { ("owned", mine.Pid), ("recorder", recorder.Id), ("parallel", theirs.Pid) })
                {
                    using var p = Process.GetProcessById(pid);
                    Assert.True(p.ProcessName.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) && p.StartTime >= testBegan,
                        $"{name} pid {pid} ({p.ProcessName}, started {p.StartTime:O}) is not in the old sweep's selection - the test would prove nothing");
                }

                // ---- the failure path ----
                var report = owned.KillOwned();
                foreach (var line in report) _out.WriteLine("KillOwned: " + line);
                Assert.Equal($"pid {mine.Pid}: killed", Assert.Single(report));

                // Criterion 5: the owned ffmpeg is gone - nothing left spinning (KillTree waited for it).
                Assert.False(FfmpegChildRegistry.IsSameProcess(mine.Pid, mine.StartUtc!.Value), $"owned pid {mine.Pid} is still running after KillOwned");

                // And the stuck run is over, because its ffmpeg was killed. The bound is generous on
                // purpose, like the #128 tests' 60-90 s: after the exit the runner still drains
                // ffmpeg's stdout/stderr on thread-pool callbacks, and on a loaded 2-core CI runner
                // (the whole suite in parallel) a 15 s bound once expired before they ran. It is still
                // well short of the 120 s the unrelated processes run for, so a run held open by them
                // fails here by name.
                var drain = Stopwatch.StartNew();
                Assert.True(Finishes(run, TimeSpan.FromSeconds(60)), "the owned run is still blocked 60 s after its ffmpeg was killed");
                _out.WriteLine($"the owned run returned {drain.Elapsed.TotalSeconds:F1}s after the kill");
                var ended = Assert.IsType<UsageException>(run.Exception?.InnerException);
                Assert.True(ended.Message.StartsWith("ffmpeg failed during test owned (exit ", StringComparison.Ordinal),
                    "the owned run did not end because its ffmpeg was killed: " + ended.Message);

                // Criterion 4: the unrelated processes are still running.
                Assert.False(recorder.HasExited, $"the RECORDER ffmpeg (pid {recorder.Id}) was killed by a test that did not start it");
                Assert.True(FfmpegChildRegistry.IsSameProcess(theirs.Pid, theirs.StartUtc!.Value),
                    $"the PARALLEL test's ffmpeg (pid {theirs.Pid}) was killed by a test that did not start it");
                _out.WriteLine($"owned pid {mine.Pid} killed; recorder pid {recorder.Id} alive; parallel pid {theirs.Pid} alive");
            }
            finally
            {
                // Clean up only what this test started, by handle and by its own scope.
                if (recorder != null && !recorder.HasExited) { recorder.Kill(entireProcessTree: true); recorder.WaitForExit(5000); }
                recorder?.Dispose();
                parallelScope?.KillOwned();
                if (parallelRun != null) Finishes(parallelRun, TimeSpan.FromSeconds(15));
            }
        }

        /// <summary>
        /// Run <paramref name="body"/> on a dedicated thread, not a thread-pool one. On a 2-core
        /// Windows runner with the whole suite running in parallel the pool starves: a Task.Run here
        /// once waited more than 15 s for a thread before it could even start its ffmpeg. A dedicated
        /// thread still carries this flow's ExecutionContext, so the OwnedFfmpeg observer flows into it
        /// exactly as it would into Task.Run (and SuppressFlow still keeps it out).
        /// </summary>
        private static Task OnOwnThread(Action body) =>
            Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>Wait for <paramref name="t"/> without rethrowing its fault.</summary>
        private static bool Finishes(Task t, TimeSpan within) => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(within);

        /// <summary>A real-time 120 s encode to nowhere: it runs, it never finishes inside a test.</summary>
        private static string[] LongRun() =>
            new[] { "-nostdin", "-y", "-re", "-f", "lavfi", "-i", "sine=f=300:r=48000:d=120", "-f", "null", "-" };
    }
}
