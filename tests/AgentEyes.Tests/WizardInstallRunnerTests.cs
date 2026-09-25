using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using AgentEyesSetup.Services;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #86, review of PR #92 (N1): the wizard's <see cref="EngineInstallRunner"/> caught only
    /// <see cref="AppStopFailedException"/>, and its caller is fire-and-forget - so a running app whose
    /// command line could not be read (Find throws) would have left the wizard at "Installing..." for
    /// ever. Every failure of the update cycle must come out of <see cref="EngineInstallRunner.ApplyAsync"/>
    /// as ONE error state: <see cref="EngineInstallRunner.LastError"/> set, the items marked, an
    /// "ERROR:" status line, the failed attempt on record - and it must return.
    ///
    /// Only FAILURE paths run here: they return before the wizard's finalize step, which writes the real
    /// Start Menu shortcut, the Run key and the Add/Remove Programs entry. The success path of the same
    /// cycle is proven in UpdateRestartCycleTests and, at the CLI's entry point, SetupCliRestartTests.
    /// The WPF wizard itself is never started.
    /// </summary>
    public sealed class WizardInstallRunnerTests : IDisposable
    {
        private readonly string _temp;
        private readonly string _releaseDir;
        private readonly InstallLayout _layout;
        private readonly FakeApp _app = new();
        private readonly FakeLauncher _launcher = new();
        private readonly List<string> _status = new();

        public WizardInstallRunnerTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-wizard-" + Guid.NewGuid().ToString("N"));
            _releaseDir = Path.Combine(_temp, "release");
            Directory.CreateDirectory(_releaseDir);
            _layout = new InstallLayout(Path.Combine(_temp, "root"));
            WriteRelease("9.9.9", "app build 9.9.9", goodHash: true);
        }

        public void Dispose()
        {
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public async Task ApplyAsync_FindThrows_IsOneErrorState_NotAHang()
        {
            // The real handle throws when the running app's command line cannot be read (nothing is
            // guessed). Before the fix this exception escaped the runner and the wizard hung.
            _app.FindThrows = new InvalidOperationException("the command line of the running AgentEyes (pid 5) could not be read, so it cannot be started again the way it was running.");
            var runner = Runner();
            var prep = runner.Prepare(ReleaseSource.LoadLocalReleaseDir(_releaseDir));

            var (installed, skipped) = await runner.ApplyAsync(prep, new EngineInstallRunner.Options(false, false, false));

            Assert.Equal(0, installed);
            Assert.Equal(prep.Items.Count, skipped);
            Assert.NotNull(runner.LastError);
            Assert.Contains("pid 5", runner.LastError!);
            Assert.StartsWith("ERROR: ", _status.Last());
            Assert.Contains("pid 5", _status.Last());
            var appItem = prep.ItemsByComponentId["app"];
            Assert.Equal("Skipped", appItem.Status);
            Assert.Equal("Not installed - see the error", appItem.StatusDetail);
            Assert.False(File.Exists(_layout.PathFor(ComponentRegistry.App)));
            Assert.Empty(_launcher.Launches);
            Assert.Equal(0, _app.StopCalls);
            var marker = UpdateAttemptMarker.Read(_layout)!;
            Assert.Equal("9.9.9", marker.TargetVersion);
            Assert.Equal("unknown", marker.Stage);
            Assert.Equal("wizard", marker.By);
        }

        [Fact]
        public async Task ApplyAsync_AppCannotBeStopped_IsTheSameErrorState_AndNothingIsReplaced()
        {
            InstallOldApp("old build 1.11.2");
            var before = File.ReadAllBytes(_layout.PathFor(ComponentRegistry.App));
            _app.Instance = new RunningAppInstance(13680, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            _app.StopSucceeds = false;
            var runner = Runner();
            var prep = runner.Prepare(ReleaseSource.LoadLocalReleaseDir(_releaseDir));

            var (installed, _) = await runner.ApplyAsync(prep, new EngineInstallRunner.Options(false, false, false));

            Assert.Equal(0, installed);
            Assert.Contains("could not be stopped", runner.LastError!);
            Assert.Contains("Nothing was replaced", runner.LastError!);
            Assert.Equal("Could not stop the running AgentEyes", prep.ItemsByComponentId["app"].StatusDetail);
            Assert.Equal(before, File.ReadAllBytes(_layout.PathFor(ComponentRegistry.App)));
            Assert.Equal(1, _app.StopCalls);                                                        // the download came first, then the stop
            Assert.Contains(_status, s => s.Contains("closing the running AgentEyes"));
            Assert.Equal("stop", UpdateAttemptMarker.Read(_layout)!.Stage);
        }

        [Fact]
        public async Task ApplyAsync_DownloadFailsVerification_TheAppIsNeverStopped_AndTheErrorSaysSo()
        {
            WriteRelease("9.9.9", "app build 9.9.9", goodHash: false);
            InstallOldApp("old build 1.11.2");
            _app.Instance = new RunningAppInstance(13680, _layout.PathFor(ComponentRegistry.App), new[] { "--tray" });
            _app.StopSucceeds = true;
            var runner = Runner();
            var prep = runner.Prepare(ReleaseSource.LoadLocalReleaseDir(_releaseDir));

            var (installed, _) = await runner.ApplyAsync(prep, new EngineInstallRunner.Options(false, false, false));

            Assert.Equal(0, installed);
            Assert.Contains("could not be downloaded and verified", runner.LastError!);
            Assert.Contains("was not stopped", runner.LastError!);
            Assert.Equal(0, _app.StopCalls);
            Assert.Empty(_launcher.Launches);
            Assert.Equal("old build 1.11.2", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));
            Assert.Equal("download", UpdateAttemptMarker.Read(_layout)!.Stage);
        }

        [Fact]
        public void Constructor_NullLayoutOrSource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new EngineInstallRunner(null!, new ReleaseSource(), _app, _launcher));
            Assert.Throws<ArgumentNullException>(() => new EngineInstallRunner(_layout, null!, _app, _launcher));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private EngineInstallRunner Runner() =>
            new(_layout, new ReleaseSource(), _app, _launcher) { OnStatus = s => _status.Add(s) };

        private void InstallOldApp(string content)
        {
            string exe = _layout.PathFor(ComponentRegistry.App);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, content);
        }

        private void WriteRelease(string version, string appContent, bool goodHash)
        {
            string asset = Path.Combine(_releaseDir, ComponentRegistry.App.Asset);
            File.WriteAllText(asset, appContent);
            string sha = goodHash ? Hashing.Sha256OfFile(asset) : "DEADBEEF";
            File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), $$"""
                {
                  "version": "{{version}}",
                  "assets": {
                    "{{ComponentRegistry.App.Asset}}": { "version": "{{version}}", "sha256": "{{sha}}", "size": 16 }
                  }
                }
                """);
        }

        private sealed class FakeApp : IRunningAppHandle
        {
            public RunningAppInstance? Instance { get; set; }
            public bool StopSucceeds { get; set; }
            public Exception? FindThrows { get; set; }
            public int StopCalls { get; private set; }

            public RunningAppInstance? Find()
            {
                if (FindThrows != null) throw FindThrows;
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
            public List<(string Exe, IReadOnlyList<string> Arguments)> Launches { get; } = new();

            public int Launch(string exePath, IReadOnlyList<string> arguments)
            {
                Launches.Add((exePath, arguments.ToList()));
                return 1;
            }
        }
    }
}
