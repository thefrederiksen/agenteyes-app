using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;
using CliCommands = AgentEyes.Setup.Cli.Commands;
using CliProgram = AgentEyes.Setup.Cli.Program;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #86 at the CLI's real entry point: <c>agenteyes-setup update</c> run in-process through
    /// <see cref="CliProgram.RunAsync"/> against a LOCAL release dir and a temp root, with the running app
    /// and the launcher replaced by doubles through the CLI's seams - so the exact console line the issue
    /// asks for is read from what the command printed, and no real AgentEyes is found, stopped or started.
    ///
    /// The seams, the console and the engine log sink are process-wide, so this class shares the
    /// collection of the other CLI tests and runs serially with them.
    /// </summary>
    [Collection(ReleaseTransportSeamCollection.Name)]
    public sealed class SetupCliRestartTests : IDisposable
    {
        private readonly string _temp;
        private readonly string _releaseDir;
        private readonly InstallLayout _layout;
        private readonly FakeApp _app = new();
        private readonly FakeLauncher _launcher = new();
        private readonly Func<InstallLayout, IRunningAppHandle> _handleBefore;
        private readonly Func<InstallLayout, IAppLauncher> _launcherBefore;

        public SetupCliRestartTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-cli-restart-" + Guid.NewGuid().ToString("N"));
            _releaseDir = Path.Combine(_temp, "release");
            Directory.CreateDirectory(_releaseDir);
            _layout = new InstallLayout(Path.Combine(_temp, "root"));
            WriteRelease("9.9.9", "app build 9.9.9");

            _handleBefore = CliCommands.RunningAppHandleFactory;
            _launcherBefore = CliCommands.AppLauncherFactory;
            CliCommands.RunningAppHandleFactory = _ => _app;
            CliCommands.AppLauncherFactory = _ => _launcher;
        }

        public void Dispose()
        {
            CliCommands.RunningAppHandleFactory = _handleBefore;
            CliCommands.AppLauncherFactory = _launcherBefore;
            EngineLog.Sink = null;
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public async Task Update_AppRunning_PrintsRestartedTheRunningAppWithBothPids_AndRelaunchesWithTheSameArguments()
        {
            _app.Instance = new RunningAppInstance(13680, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            _app.StopSucceeds = true;
            _launcher.NewPid = 22104;

            var (exit, stdout, stderr) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Contains("Update complete:", stdout);
            Assert.Contains("restarted the running app (pid 13680 -> pid 22104)", stdout);   // THE line the issue asks for
            Assert.Equal("app build 9.9.9", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));
            var launch = Assert.Single(_launcher.Launches);
            Assert.Equal(_layout.PathFor(ComponentRegistry.App), launch.Exe);
            Assert.Equal(new[] { "--tray" }, launch.Arguments);
            Assert.Equal(1, _app.StopCalls);
        }

        [Fact]
        public async Task Update_AppRunning_JsonCarriesTheRestart()
        {
            _app.Instance = new RunningAppInstance(13680, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            _app.StopSucceeds = true;
            _launcher.NewPid = 22104;

            var (exit, stdout, _) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize", "--json");

            Assert.Equal(0, exit);
            Assert.True(stdout.Contains("\"restarted\": true", StringComparison.Ordinal), "stdout was:\n" + stdout);
            Assert.Contains("\"oldPid\": 13680", stdout);
            Assert.Contains("\"newPid\": 22104", stdout);
            Assert.Contains("restarted the running app (pid 13680 -\\u003E pid 22104)", stdout);   // JSON escapes '>'
        }

        [Fact]
        public async Task Update_AppNotRunning_SaysSoAndStartsNothing()
        {
            _app.Instance = null;

            var (exit, stdout, stderr) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Contains("AgentEyes was not running - it was not started", stdout);
            Assert.DoesNotContain("restarted the running app", stdout);
            Assert.Empty(_launcher.Launches);
            Assert.Equal(0, _app.StopCalls);
            Assert.Equal("app build 9.9.9", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));
        }

        [Fact]
        public async Task Update_AppCannotBeStopped_FailsWithTheReason_AndTheOldBuildIsUntouched()
        {
            // An old build is installed and running; the stop fails. The command must fail BEFORE any
            // file is replaced: the old exe is byte-for-byte what it was, no .old backup, nothing started.
            string exe = _layout.PathFor(ComponentRegistry.App);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "old build 1.11.2");
            // The installed-version bookkeeping says 1.11.2 (a text stand-in carries no file version, and
            // an unreadable installed version is never auto-updated), so the planner sees an update.
            var installed = InstalledManifest.Load(_layout);
            installed.Set(ComponentRegistry.App.Id, "1.11.2");
            installed.Save(_layout);
            var before = File.ReadAllBytes(exe);
            _app.Instance = new RunningAppInstance(13680, exe, new[] { "--tray" });
            _app.StopSucceeds = false;

            var (exit, stdout, stderr) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(1, exit);
            Assert.Contains("ERROR: AgentEyes (pid 13680) is running and could not be stopped.", stderr);
            Assert.Contains("Nothing was replaced", stderr);
            Assert.DoesNotContain("Update complete", stdout);
            Assert.Equal(before, File.ReadAllBytes(exe));
            Assert.False(File.Exists(InstallSwapper.BackupPathFor(exe)));
            Assert.Empty(_launcher.Launches);
            // Review B1c: the failed attempt is on record for the app, with where it failed.
            var marker = UpdateAttemptMarker.Read(_layout)!;
            Assert.Equal("9.9.9", marker.TargetVersion);
            Assert.Equal("stop", marker.Stage);
            Assert.Contains("could not be stopped", marker.Reason);
            Assert.Equal("cli", marker.By);
        }

        [Fact]
        public async Task Update_DownloadFailsVerification_AppRunning_NeverStopsIt_TouchesNoFile_ExitsOne_AndRecordsTheAttempt()
        {
            // Review B1a: the download and its SHA-256 check happen BEFORE the running app is stopped.
            // A bad release leaves the app running, the old build byte-for-byte intact, starts nothing,
            // fails with the reason - and records the attempt so the app does not retry it by itself.
            string exe = _layout.PathFor(ComponentRegistry.App);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "old build 1.11.2");
            var installed = InstalledManifest.Load(_layout);
            installed.Set(ComponentRegistry.App.Id, "1.11.2");
            installed.Save(_layout);
            var before = File.ReadAllBytes(exe);
            WriteReleaseWithBadHash("9.9.9", "app build 9.9.9");
            _app.Instance = new RunningAppInstance(13680, exe, new[] { "--tray" });
            _app.StopSucceeds = true;

            var (exit, stdout, stderr) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(1, exit);
            Assert.Contains("ERROR: app could not be downloaded and verified (SHA-256 mismatch", stderr);
            Assert.Contains("the running AgentEyes was not stopped", stderr);
            Assert.DoesNotContain("Update complete", stdout);
            Assert.Equal(1, _app.FindCalls);
            Assert.Equal(0, _app.StopCalls);                                    // never stopped
            Assert.Empty(_launcher.Launches);                                    // never started
            Assert.Equal(before, File.ReadAllBytes(exe));                        // never touched
            Assert.False(File.Exists(InstallSwapper.BackupPathFor(exe)));
            var marker = UpdateAttemptMarker.Read(_layout)!;
            Assert.Equal("9.9.9", marker.TargetVersion);
            Assert.Equal("download", marker.Stage);
            Assert.Contains("SHA-256 mismatch", marker.Reason);
        }

        [Fact]
        public async Task Update_Succeeds_ClearsTheFailedAttemptRecord()
        {
            UpdateAttemptMarker.WriteFailed(_layout, "9.9.9", UpdateAttemptMarker.StageDownload, "an earlier try", "cli");
            _app.Instance = null;

            var (exit, _, _) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.Null(UpdateAttemptMarker.Read(_layout));
            Assert.False(File.Exists(_layout.UpdateAttemptMarkerPath));
        }

        [Fact]
        public async Task Update_StoppedProcessRanFromElsewhere_StartsTheInstalledExe_AndPrintsTheNote()
        {
            // Review N2: a dev build running from a bin folder is stopped; the INSTALLED app is what is
            // started, and the output says the two differ.
            const string devExe = @"D:\dev\AgentEyes\bin\x64\Release\AgentEyesApp.exe";
            _app.Instance = new RunningAppInstance(13680, devExe, new[] { "--tray" });
            _app.StopSucceeds = true;
            _launcher.NewPid = 22104;

            var (exit, stdout, _) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.Contains("restarted the running app (pid 13680 -> pid 22104)", stdout);
            Assert.Contains("note: the stopped process (pid 13680) was running from " + devExe, stdout);
            Assert.Contains(_layout.PathFor(ComponentRegistry.App), stdout);
            Assert.Equal(_layout.PathFor(ComponentRegistry.App), Assert.Single(_launcher.Launches).Exe);
        }

        [Fact]
        public async Task Update_StoppedProcessRanFromTheInstalledExe_PrintsNoNote()
        {
            _app.Instance = new RunningAppInstance(13680, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            _app.StopSucceeds = true;

            var (exit, stdout, _) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.DoesNotContain("note: the stopped process", stdout);
        }

        [Fact]
        public async Task Update_DryRun_NeverLooksForTheRunningApp()
        {
            _app.Instance = new RunningAppInstance(1, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });

            var (exit, stdout, _) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--dry-run");

            Assert.Equal(0, exit);
            Assert.Contains("Plan:", stdout);
            Assert.Equal(0, _app.FindCalls);
            Assert.Equal(0, _app.StopCalls);
            Assert.Empty(_launcher.Launches);
        }

        [Fact]
        public async Task Update_NothingToDo_LeavesTheRunningAppAlone()
        {
            // First run installs 9.9.9 (the app "was not running"); the second finds nothing to do and
            // must not stop or start anything even though the app is now reported running.
            _app.Instance = null;
            var first = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");
            Assert.Equal(0, first.Exit);

            _app.Instance = new RunningAppInstance(13680, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            int findsBefore = _app.FindCalls;
            var (exit, stdout, _) = await Run("update", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.Contains("Nothing to do", stdout);
            Assert.Equal(findsBefore, _app.FindCalls);
            Assert.Equal(0, _app.StopCalls);
            Assert.Empty(_launcher.Launches);
        }

        [Fact]
        public async Task Install_AppRunning_TakesTheSamePath_AndPrintsTheSameLine()
        {
            _app.Instance = new RunningAppInstance(100, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            _app.StopSucceeds = true;
            _launcher.NewPid = 200;

            var (exit, stdout, stderr) = await Run("install", "--release-dir", _releaseDir, "--root", _layout.LocalRoot, "--no-finalize");

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Contains("Install complete:", stdout);
            Assert.Contains("restarted the running app (pid 100 -> pid 200)", stdout);
            Assert.Equal(new[] { "--tray" }, Assert.Single(_launcher.Launches).Arguments);
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>Run the CLI's real entry point in-process with the console captured.</summary>
        private static async Task<(int Exit, string Stdout, string Stderr)> Run(params string[] argv)
        {
            var outBefore = Console.Out;
            var errBefore = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var syncOut = TextWriter.Synchronized(stdout);
            var syncErr = TextWriter.Synchronized(stderr);
            Console.SetOut(syncOut);
            Console.SetError(syncErr);
            try
            {
                int exit = await CliProgram.RunAsync(argv, syncOut, syncErr);
                syncOut.Flush();
                syncErr.Flush();
                return (exit, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(outBefore);
                Console.SetError(errBefore);
                EngineLog.Sink = null;
            }
        }

        /// <summary>A local release dir with only the app asset: the other components are "not in the
        /// release" and so not actionable.</summary>
        private void WriteRelease(string version, string appContent)
        {
            string asset = Path.Combine(_releaseDir, ComponentRegistry.App.Asset);
            File.WriteAllText(asset, appContent);
            string sha = Hashing.Sha256OfFile(asset);
            File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), $$"""
                {
                  "version": "{{version}}",
                  "assets": {
                    "{{ComponentRegistry.App.Asset}}": { "version": "{{version}}", "sha256": "{{sha}}" }
                  }
                }
                """);
        }

        /// <summary>The same release with a manifest hash that cannot match: the download must be rejected.</summary>
        private void WriteReleaseWithBadHash(string version, string appContent)
        {
            string asset = Path.Combine(_releaseDir, ComponentRegistry.App.Asset);
            File.WriteAllText(asset, appContent);
            File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), $$"""
                {
                  "version": "{{version}}",
                  "assets": {
                    "{{ComponentRegistry.App.Asset}}": { "version": "{{version}}", "sha256": "DEADBEEF" }
                  }
                }
                """);
        }

        private sealed class FakeApp : IRunningAppHandle
        {
            public RunningAppInstance? Instance { get; set; }
            public bool StopSucceeds { get; set; }
            public int FindCalls { get; private set; }
            public int StopCalls { get; private set; }

            public RunningAppInstance? Find()
            {
                FindCalls++;
                return Instance;
            }

            public Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct)
            {
                StopCalls++;
                return Task.FromResult(StopSucceeds);
            }
        }

        private sealed class FakeLauncher : IAppLauncher
        {
            public int NewPid { get; set; } = 1;
            public List<(string Exe, IReadOnlyList<string> Arguments)> Launches { get; } = new();

            public int Launch(string exePath, IReadOnlyList<string> arguments)
            {
                Launches.Add((exePath, arguments.ToList()));
                return NewPid;
            }
        }
    }
}
