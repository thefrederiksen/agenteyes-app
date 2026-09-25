using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #86: an update replaces the files but leaves the running app on the old build. The ONE
    /// stop -> replace -> relaunch decision every update path takes is <see cref="UpdateRestartCycle"/>,
    /// proven here against doubles - a fake running-app handle, a fake launcher and a fake (or real,
    /// on a temp file) replacer - so no AgentEyes is ever found, stopped or started by these tests.
    /// </summary>
    public sealed class UpdateRestartCycleTests
    {
        private static readonly RunningAppInstance TrayApp =
            new(4242, @"C:\Users\x\AppData\Local\AgentEyes\app\AgentEyesApp.exe", new[] { "--tray" });

        // ---- the acceptance criteria, one by one ---------------------------------------------

        [Fact]
        public async Task RunAsync_AppRunning_StopsIt_ThenReplaces_ThenStartsItAgainWithTheSameArguments()
        {
            var journal = new List<string>();
            var app = new FakeApp(journal) { Instance = TrayApp, StopSucceeds = true };
            var launcher = new FakeLauncher(journal) { NewPid = 4343 };
            var cycle = new UpdateRestartCycle(app, launcher);

            var outcome = await cycle.RunAsync(_ => { journal.Add("replace"); return Task.FromResult("replaced"); });

            // The order IS the criterion: nothing is replaced until the stop has succeeded, and the
            // relaunch comes after the replace.
            Assert.Equal(new[] { "find", "stop 4242", "replace", "launch" }, journal);
            Assert.Equal("replaced", outcome.Result);
            var launch = Assert.Single(launcher.Launches);
            Assert.Equal(TrayApp.ExePath, launch.Exe);
            Assert.Equal(new[] { "--tray" }, launch.Arguments);          // a --tray app comes back as a --tray app
            Assert.True(outcome.Restart.Restarted);
            Assert.Equal("restarted the running app (pid 4242 -> pid 4343)", outcome.Restart.Describe());
        }

        [Fact]
        public async Task RunAsync_AppNotRunning_ReplacesAndStartsNothing()
        {
            var journal = new List<string>();
            var app = new FakeApp(journal) { Instance = null };
            var launcher = new FakeLauncher(journal);
            var cycle = new UpdateRestartCycle(app, launcher);

            var outcome = await cycle.RunAsync(_ => { journal.Add("replace"); return Task.FromResult(1); });

            Assert.Equal(new[] { "find", "replace" }, journal);
            Assert.Empty(launcher.Launches);
            Assert.False(outcome.Restart.Restarted);
            Assert.Equal("AgentEyes was not running - it was not started", outcome.Restart.Describe());
        }

        [Fact]
        public async Task RunAsync_AppCannotBeStopped_ThrowsBeforeTheReplaceStep_AndStartsNothing()
        {
            var journal = new List<string>();
            var app = new FakeApp(journal) { Instance = TrayApp, StopSucceeds = false };
            var launcher = new FakeLauncher(journal);
            var cycle = new UpdateRestartCycle(app, launcher);

            var ex = await Assert.ThrowsAsync<AppStopFailedException>(() =>
                cycle.RunAsync(_ => { journal.Add("replace"); return Task.FromResult(1); }));

            // The replace step never ran: no half state, the installed version is what it was.
            Assert.Equal(new[] { "find", "stop 4242" }, journal);
            Assert.Empty(launcher.Launches);
            Assert.Equal(4242, ex.Instance.Pid);
            Assert.Contains("pid 4242", ex.Message);
            Assert.Contains("Nothing was replaced", ex.Message);
            Assert.Contains("tray icon -> Quit", ex.Message);
        }

        [Fact]
        public async Task RunAsync_AppCannotBeStopped_TheInstalledFileIsByteForByteUnchanged()
        {
            // The same criterion on a REAL file with the real swapper as the replace step: the stop fails,
            // so the swapper is never reached and the old build stays, with no .old backup either.
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-cycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string target = Path.Combine(dir, "AgentEyesApp.exe");
                string staged = Path.Combine(dir, "staged.exe");
                File.WriteAllText(target, "old build 1.11.2");
                File.WriteAllText(staged, "new build 1.11.5");
                var before = File.ReadAllBytes(target);

                var app = new FakeApp(new List<string>()) { Instance = TrayApp, StopSucceeds = false };
                var cycle = new UpdateRestartCycle(app, new FakeLauncher(new List<string>()));

                await Assert.ThrowsAsync<AppStopFailedException>(() =>
                    cycle.RunAsync(_ => Task.FromResult(InstallSwapper.Place(target, staged))));

                Assert.Equal(before, File.ReadAllBytes(target));
                Assert.False(File.Exists(InstallSwapper.BackupPathFor(target)));
                Assert.True(File.Exists(staged));                        // not consumed either
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task RunAsync_StopSucceeds_TheRealSwapperReplacesTheFile_AndTheAppIsStartedFromTheSamePath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-cycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string target = Path.Combine(dir, "AgentEyesApp.exe");
                string staged = Path.Combine(dir, "staged.exe");
                File.WriteAllText(target, "old build");
                File.WriteAllText(staged, "new build");
                var running = new RunningAppInstance(100, target, new[] { "--tray" });

                var journal = new List<string>();
                var launcher = new FakeLauncher(journal) { NewPid = 200 };
                var cycle = new UpdateRestartCycle(new FakeApp(journal) { Instance = running, StopSucceeds = true }, launcher);

                var outcome = await cycle.RunAsync(_ => Task.FromResult(InstallSwapper.Place(target, staged)));

                Assert.Equal("new build", File.ReadAllText(target));
                Assert.Equal("old build", File.ReadAllText(InstallSwapper.BackupPathFor(target)));
                Assert.Equal(target, Assert.Single(launcher.Launches).Exe);
                Assert.Equal("restarted the running app (pid 100 -> pid 200)", outcome.Restart.Describe());
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task RunAsync_ReplaceStepThrows_TheStoppedAppIsStartedAgain_AndTheFailurePropagates()
        {
            var journal = new List<string>();
            var launcher = new FakeLauncher(journal) { NewPid = 7 };
            var cycle = new UpdateRestartCycle(new FakeApp(journal) { Instance = TrayApp, StopSucceeds = true }, launcher);

            var ex = await Assert.ThrowsAsync<IOException>(() =>
                cycle.RunAsync<int>(_ => { journal.Add("replace"); throw new IOException("disk full"); }));

            // An app this cycle stopped is never left stopped, whatever the replace step did.
            Assert.Equal("disk full", ex.Message);
            Assert.Equal(new[] { "find", "stop 4242", "replace", "launch" }, journal);
            Assert.Equal(new[] { "--tray" }, Assert.Single(launcher.Launches).Arguments);
        }

        [Fact]
        public async Task RunAsync_LaunchThrows_TheFailurePropagates_AfterTheReplaceStepRan()
        {
            var journal = new List<string>();
            var launcher = new FakeLauncher(journal) { Throws = new FileNotFoundException("exe gone") };
            var cycle = new UpdateRestartCycle(new FakeApp(journal) { Instance = TrayApp, StopSucceeds = true }, launcher);

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                cycle.RunAsync(_ => { journal.Add("replace"); return Task.FromResult(1); }));

            Assert.Equal(new[] { "find", "stop 4242", "replace", "launch" }, journal);
        }

        [Fact]
        public async Task RunAsync_EveryArgumentIsCarriedVerbatim_NotJustTheTrayFlag()
        {
            var running = new RunningAppInstance(1, @"D:\app\AgentEyesApp.exe", new[] { "--tray", "--extra", "with space" });
            var journal = new List<string>();
            var launcher = new FakeLauncher(journal) { NewPid = 2 };
            var cycle = new UpdateRestartCycle(new FakeApp(journal) { Instance = running, StopSucceeds = true }, launcher);

            await cycle.RunAsync(_ => Task.FromResult(0));

            Assert.Equal(new[] { "--tray", "--extra", "with space" }, Assert.Single(launcher.Launches).Arguments);
        }

        [Fact]
        public void Report_NotRestarted_WhenOnlyAStopIsOnRecord()
        {
            // A stopped app with no new pid is not "restarted" - the report never claims more than happened.
            Assert.False(new AppRestartReport(TrayApp, null).Restarted);
            Assert.False(new AppRestartReport(null, 5).Restarted);
        }

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new UpdateRestartCycle(null!, new FakeLauncher(new List<string>())));
            Assert.Throws<ArgumentNullException>(() => new UpdateRestartCycle(new FakeApp(new List<string>()), null!));
        }

        // ---- the grace period: a build that took the quit request gets the whole wait -----------

        [Fact]
        public void GracePeriod_QuitRequestDelivered_IsTheWholeGracefulWait()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), RunningApp.GracePeriod(quitRequestDelivered: true, TimeSpan.FromSeconds(30)));
        }

        [Fact]
        public void GracePeriod_NoListener_IsCappedAtTheLegacyGrace()
        {
            // Nobody honours CloseMainWindow for long (a tray app ignores it), so the force stop follows
            // after the legacy 3 s instead of the person waiting 30 s for nothing.
            Assert.Equal(RunningApp.LegacyGrace, RunningApp.GracePeriod(quitRequestDelivered: false, TimeSpan.FromSeconds(30)));
            Assert.Equal(TimeSpan.FromMilliseconds(200), RunningApp.GracePeriod(quitRequestDelivered: false, TimeSpan.FromMilliseconds(200)));
        }

        [Fact]
        public void GracePeriod_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RunningApp.GracePeriod(true, TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void DefaultBounds_GiveAListeningBuildTimeToFinishItsPieceAndHandOver()
        {
            // ffmpeg is given up to 15 s to finish its piece (ContinuousRecorder.StopProcess), then the
            // handover and the rest of App.OnExit run; 30 s covers that, and the force phase has the rest.
            Assert.True(RunningApp.DefaultGraceful >= TimeSpan.FromSeconds(20), "the graceful wait must outlast ffmpeg's 15 s stop");
            Assert.True(RunningApp.DefaultTimeout > RunningApp.DefaultGraceful, "the overall bound must leave time for the force phase");
        }

        [Fact]
        public void StopAndWait_QuitRequestDeliveredButIgnored_StillForceStopsAndConfirms()
        {
            // A disposable child that ignores every request (cmd waiting on pause) stands in for a build
            // that took the quit request and then hung: the graceful wait passes, the force phase ends it,
            // and the result is still an honest "gone".
            var psi = new ProcessStartInfo("cmd.exe", "/c pause") { CreateNoWindow = true, UseShellExecute = false };
            using var child = Process.Start(psi)!;
            int pid = child.Id;
            var requested = new List<int>();

            Process[] Provider(string _)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    return p.HasExited ? Array.Empty<Process>() : new[] { p };
                }
                catch (ArgumentException) { return Array.Empty<Process>(); }
            }

            var sw = Stopwatch.StartNew();
            bool gone = RunningApp.StopAndWait("cmd", TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10), Provider,
                requestQuit: p => { requested.Add(p); return true; });

            Assert.True(gone);
            Assert.True(child.HasExited);
            Assert.Equal(new[] { pid }, requested);                                  // the request went to exactly that pid
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350), $"the graceful wait was skipped ({sw.Elapsed})");
        }

        // ---- the quit request channel ---------------------------------------------------------

        [Fact]
        public void QuitRequest_Listen_ThenSignal_RunsTheCallbackOnce()
        {
            // A pid no real process has, so the event name cannot collide with a running AgentEyes.
            const int pid = 987_654_321;
            int calls = 0;
            using var fired = new ManualResetEventSlim(false);
            using (QuitRequest.Listen(pid, () => { Interlocked.Increment(ref calls); fired.Set(); }))
            {
                Assert.True(QuitRequest.TrySignal(pid));
                Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "the listener did not run within 5 s");
            }
            Assert.Equal(1, calls);
        }

        [Fact]
        public void QuitRequest_NoListener_TrySignalIsFalse()
        {
            // An older build (or no such process) does not listen: the engine learns that honestly and
            // falls through to the bounded force stop instead of waiting the whole grace period.
            Assert.False(QuitRequest.TrySignal(987_654_322));
        }

        [Fact]
        public void QuitRequest_AfterDispose_TrySignalIsFalse()
        {
            const int pid = 987_654_323;
            var listener = QuitRequest.Listen(pid, () => { });
            Assert.True(QuitRequest.TrySignal(pid));
            listener.Dispose();
            Assert.False(QuitRequest.TrySignal(pid));
        }

        [Fact]
        public void QuitRequest_EventName_IsPerPid()
        {
            Assert.Equal("AgentEyes-quit-13680", QuitRequest.EventName(13680));
            Assert.NotEqual(QuitRequest.EventName(1), QuitRequest.EventName(2));
        }

        // ---- the real handle's pure parts: the command line -> the relaunch arguments -------------

        [Theory]
        [InlineData("\"C:\\Program Files\\AgentEyes\\AgentEyesApp.exe\" --tray", new[] { "C:\\Program Files\\AgentEyes\\AgentEyesApp.exe", "--tray" })]
        [InlineData("AgentEyesApp.exe", new[] { "AgentEyesApp.exe" })]
        [InlineData("D:\\app\\AgentEyesApp.exe --tray \"--name=with space\"", new[] { "D:\\app\\AgentEyesApp.exe", "--tray", "--name=with space" })]
        public void SplitCommandLine_FollowsTheWindowsRules(string commandLine, string[] expected)
        {
            Assert.Equal(expected, RunningAppHandle.SplitCommandLine(commandLine));
        }

        [Fact]
        public void SplitCommandLine_Blank_IsEmpty()
        {
            Assert.Empty(RunningAppHandle.SplitCommandLine("   "));
        }

        [Fact]
        public void Describe_TrayApp_CarriesTheTrayFlagAndDropsTheExePath()
        {
            var instance = RunningAppHandle.Describe(13680, @"C:\Users\x\AppData\Local\AgentEyes\app\AgentEyesApp.exe",
                "\"C:\\Users\\x\\AppData\\Local\\AgentEyes\\app\\AgentEyesApp.exe\" --tray");

            Assert.Equal(13680, instance.Pid);
            Assert.Equal(@"C:\Users\x\AppData\Local\AgentEyes\app\AgentEyesApp.exe", instance.ExePath);
            Assert.Equal(new[] { "--tray" }, instance.Arguments);
            Assert.Equal("--tray", instance.ArgumentsText);
        }

        [Fact]
        public void Describe_WindowedApp_CarriesNoArguments()
        {
            var instance = RunningAppHandle.Describe(1, @"D:\app\AgentEyesApp.exe", "D:\\app\\AgentEyesApp.exe");
            Assert.Empty(instance.Arguments);
            Assert.Equal("(none)", instance.ArgumentsText);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Describe_CommandLineUnreadable_ThrowsNamingThePid_SoNothingIsGuessed(string? commandLine)
        {
            // No fallback to "--tray" or to nothing: a relaunch with guessed arguments is not a relaunch
            // of what was running, and the message says what to do instead.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                RunningAppHandle.Describe(555, @"D:\app\AgentEyesApp.exe", commandLine));
            Assert.Contains("pid 555", ex.Message);
            Assert.Contains("tray icon -> Quit", ex.Message);
        }

        [Fact]
        public void WmiCommandLine_ThisProcess_ReturnsItsOwnCommandLine()
        {
            // The live WMI path, against the one process this test may read: the test host itself. It
            // proves the query answers and that the answer splits to an argv whose first entry is the
            // host exe - the same read the update makes of a running AgentEyesApp.
            string? line = RunningAppHandle.WmiCommandLine(Environment.ProcessId);

            Assert.False(string.IsNullOrWhiteSpace(line), "WMI returned no command line for this process");
            var argv = RunningAppHandle.SplitCommandLine(line!);
            Assert.NotEmpty(argv);
            Assert.EndsWith(".exe", argv[0], StringComparison.OrdinalIgnoreCase);
        }

        // ---- doubles ----------------------------------------------------------------------------

        private sealed class FakeApp : IRunningAppHandle
        {
            private readonly List<string> _journal;
            public FakeApp(List<string> journal) { _journal = journal; }
            public RunningAppInstance? Instance { get; set; }
            public bool StopSucceeds { get; set; }
            public int FindCalls { get; private set; }

            public RunningAppInstance? Find()
            {
                FindCalls++;
                _journal.Add("find");
                return Instance;
            }

            public Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct)
            {
                _journal.Add($"stop {instance.Pid}");
                return Task.FromResult(StopSucceeds);
            }
        }

        private sealed class FakeLauncher : IAppLauncher
        {
            private readonly List<string> _journal;
            public FakeLauncher(List<string> journal) { _journal = journal; }
            public int NewPid { get; set; } = 1;
            public Exception? Throws { get; set; }
            public List<(string Exe, IReadOnlyList<string> Arguments)> Launches { get; } = new();

            public int Launch(string exePath, IReadOnlyList<string> arguments)
            {
                _journal.Add("launch");
                if (Throws != null) throw Throws;
                Launches.Add((exePath, arguments.ToList()));
                return NewPid;
            }
        }
    }
}
