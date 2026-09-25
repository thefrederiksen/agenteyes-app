using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #86, review of PR #92 (B1): the update runner in two steps. <see cref="UpdateRunner.StageAsync"/>
    /// downloads and verifies EVERY component while no installed file is touched (B1a), and
    /// <see cref="UpdateRunner.Swap"/> places them all or nothing, rolling back what it had placed when
    /// one fails (B1b). Also the failed-attempt marker (B1c) the app reads after a relaunch. Real files
    /// in a temp root, a downloader that copies from a local release dir - no network, no app.
    /// </summary>
    public sealed class UpdateStageSwapTests : IDisposable
    {
        private readonly string _temp;
        private readonly string _releaseDir;
        private readonly InstallLayout _layout;
        private readonly List<string> _downloaded = new();

        public UpdateStageSwapTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-stage-" + Guid.NewGuid().ToString("N"));
            _releaseDir = Path.Combine(_temp, "release");
            Directory.CreateDirectory(_releaseDir);
            _layout = new InstallLayout(Path.Combine(_temp, "root"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }

        // ---- B1a: stage everything first, touch nothing installed ----------------------------------

        [Fact]
        public async Task StageAsync_DownloadsAndVerifiesEveryItem_AndTouchesNoInstalledFile()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteAsset(ComponentRegistry.Cli.Asset, "cli 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)), (ComponentRegistry.Cli.Asset, Sha(ComponentRegistry.Cli.Asset)));
            InstallOld(ComponentRegistry.App, "app 1.11.2");
            InstallOld(ComponentRegistry.Cli, "cli 1.11.2");
            var runner = Runner();

            using var staged = await runner.StageAsync(Plan(ComponentRegistry.App, ComponentRegistry.Cli));

            Assert.Equal(new[] { "app", "cli" }, staged.Items.Select(s => s.Item.ComponentId));
            Assert.All(staged.Items, s => Assert.True(File.Exists(s.StagedPath), s.StagedPath));
            Assert.Equal("app 9.9.9", File.ReadAllText(staged.Items[0].StagedPath));
            Assert.Equal("app 1.11.2", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));   // untouched
            Assert.Equal("cli 1.11.2", File.ReadAllText(_layout.PathFor(ComponentRegistry.Cli)));
            Assert.False(File.Exists(InstallSwapper.BackupPathFor(_layout.PathFor(ComponentRegistry.App))));
        }

        [Fact]
        public async Task StageAsync_SecondItemFailsItsHash_ThrowsNamingIt_DeletesTheFirstStagedFile_AndTouchesNothing()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteAsset(ComponentRegistry.Cli.Asset, "cli 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)), (ComponentRegistry.Cli.Asset, "DEADBEEF"));
            InstallOld(ComponentRegistry.App, "app 1.11.2");
            var runner = Runner();

            var ex = await Assert.ThrowsAsync<UpdateStageException>(() => runner.StageAsync(Plan(ComponentRegistry.App, ComponentRegistry.Cli)));

            Assert.Equal("cli", ex.ComponentId);
            Assert.Contains("SHA-256 mismatch", ex.Reason);
            Assert.Contains("was not stopped", ex.Message);
            Assert.Contains("Nothing was replaced", ex.Message);
            Assert.Equal(2, _downloaded.Count);
            Assert.All(_downloaded, p => Assert.False(File.Exists(p), "a staged temp file was left behind: " + p));
            Assert.Equal("app 1.11.2", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));
            Assert.False(File.Exists(_layout.PathFor(ComponentRegistry.Cli)));
        }

        [Fact]
        public async Task StageAsync_DownloadThrows_IsAStageFailureWithTheReason()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)));
            var runner = new UpdateRunner(_layout, ComponentRegistry.All, (_, _) => throw new HttpRequestExceptionStandIn("connection reset"));

            var ex = await Assert.ThrowsAsync<UpdateStageException>(() => runner.StageAsync(Plan(ComponentRegistry.App)));

            Assert.Equal("app", ex.ComponentId);
            Assert.Contains("download failed: connection reset", ex.Reason);
        }

        [Fact]
        public async Task StageAsync_ComponentNotInScope_IsAStageFailure()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)));
            var runner = new UpdateRunner(_layout, new[] { ComponentRegistry.Cli }, Download);

            var ex = await Assert.ThrowsAsync<UpdateStageException>(() => runner.StageAsync(Plan(ComponentRegistry.App)));

            Assert.Equal("component not in scope", ex.Reason);
            Assert.Empty(_downloaded);
        }

        // ---- B1b: swap all or nothing ------------------------------------------------------------

        [Fact]
        public async Task Swap_EveryItemPlaced_RecordsTheVersions_AndConsumesTheStagedFiles()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteAsset(ComponentRegistry.Cli.Asset, "cli 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)), (ComponentRegistry.Cli.Asset, Sha(ComponentRegistry.Cli.Asset)));
            InstallOld(ComponentRegistry.App, "app 1.11.2");
            var runner = Runner();
            using var staged = await runner.StageAsync(Plan(ComponentRegistry.App, ComponentRegistry.Cli));

            var run = runner.Swap(staged);

            Assert.Equal(0, run.Failed);
            Assert.Equal(1, run.Updated);
            Assert.Equal(1, run.Installed);
            Assert.Equal("app 9.9.9", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));
            Assert.Equal("app 1.11.2", File.ReadAllText(InstallSwapper.BackupPathFor(_layout.PathFor(ComponentRegistry.App))));
            Assert.Equal("cli 9.9.9", File.ReadAllText(_layout.PathFor(ComponentRegistry.Cli)));
            Assert.Equal("9.9.9", InstalledManifest.Load(_layout).Get("app"));
            Assert.Equal("9.9.9", InstalledManifest.Load(_layout).Get("cli"));
            Assert.All(staged.Items, s => Assert.False(File.Exists(s.StagedPath)));
        }

        [Fact]
        public async Task Swap_SecondItemCannotBePlaced_TheFirstIsRolledBack_AndTheFailureSaysSo()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteAsset(ComponentRegistry.Cli.Asset, "cli 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)), (ComponentRegistry.Cli.Asset, Sha(ComponentRegistry.Cli.Asset)));
            InstallOld(ComponentRegistry.App, "app 1.11.2");
            InstalledManifest.Load(_layout).Set("app", "1.11.2");
            var bookkeeping = InstalledManifest.Load(_layout); bookkeeping.Set("app", "1.11.2"); bookkeeping.Save(_layout);
            // The cli's target is a DIRECTORY, so its placement fails after the app was already swapped.
            Directory.CreateDirectory(_layout.PathFor(ComponentRegistry.Cli));
            var runner = Runner();
            using var staged = await runner.StageAsync(Plan(ComponentRegistry.App, ComponentRegistry.Cli));

            var ex = Assert.Throws<UpdateSwapException>(() => runner.Swap(staged));

            Assert.Equal("cli", ex.ComponentId);
            Assert.Equal(new[] { "app" }, ex.RolledBack);
            Assert.False(ex.InstallIsMixed);
            Assert.Contains("rolled back to the previous build", ex.Message);
            Assert.Contains("on the previous version throughout", ex.Message);
            Assert.Equal("app 1.11.2", File.ReadAllText(_layout.PathFor(ComponentRegistry.App)));                 // back to the old build
            Assert.False(File.Exists(InstallSwapper.BackupPathFor(_layout.PathFor(ComponentRegistry.App))));      // the backup was consumed by the rollback
            Assert.Equal("1.11.2", InstalledManifest.Load(_layout).Get("app"));                                   // bookkeeping not advanced
        }

        [Fact]
        public async Task Swap_SecondItemCannotBePlaced_AFreshlyInstalledFirstItemIsRemovedAgain()
        {
            WriteAsset(ComponentRegistry.App.Asset, "app 9.9.9");
            WriteAsset(ComponentRegistry.Cli.Asset, "cli 9.9.9");
            WriteManifest("9.9.9", (ComponentRegistry.App.Asset, Sha(ComponentRegistry.App.Asset)), (ComponentRegistry.Cli.Asset, Sha(ComponentRegistry.Cli.Asset)));
            Directory.CreateDirectory(_layout.PathFor(ComponentRegistry.Cli));
            var runner = Runner();
            using var staged = await runner.StageAsync(Plan(ComponentRegistry.App, ComponentRegistry.Cli));

            Assert.Throws<UpdateSwapException>(() => runner.Swap(staged));

            Assert.False(File.Exists(_layout.PathFor(ComponentRegistry.App)), "a fresh file placed before the failure must be removed by the rollback");
        }

        [Fact]
        public async Task Swap_ArchiveComponent_ReportsEveryFileItPlaced_SoTheyCanBeRolledBack()
        {
            string zip = Path.Combine(_releaseDir, ComponentRegistry.Ffmpeg.Asset);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    using var w = new StreamWriter(z.CreateEntry(exe).Open());
                    w.Write("ffmpeg 7.1.1");
                }
            }
            WriteManifest("9.9.9", (ComponentRegistry.Ffmpeg.Asset, Sha(ComponentRegistry.Ffmpeg.Asset)));
            Directory.CreateDirectory(_layout.AppDir);
            File.WriteAllText(Path.Combine(_layout.AppDir, "ffmpeg.exe"), "ffmpeg 7.1.0");

            var placed = ArchiveInstaller.Place(_layout.AppDir, zip);

            Assert.Equal(2, placed.Count);
            var ffmpeg = placed.Single(p => p.Target.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            var ffprobe = placed.Single(p => p.Target.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(ffmpeg.Backup);                                       // it existed: a .old backup
            Assert.Equal("ffmpeg 7.1.0", File.ReadAllText(ffmpeg.Backup!));
            Assert.Null(ffprobe.Backup);                                         // it was new: nothing to back up
        }

        [Fact]
        public void SwapException_RollbackFailed_NamesTheMixedStateAndTheFix()
        {
            // The mixed state cannot be produced with real files here without locking a file from another
            // process mid-swap; the message is pure, so it is pinned as such. What this cannot see: that the
            // runner really reaches this branch when a rollback fails - RollBack's catch is read, not run.
            var ex = new UpdateSwapException("cli", "access denied", new[] { "setup-cli" },
                new[] { @"app (C:\x\app\AgentEyesApp.exe): the .old backup to restore is gone" });

            Assert.True(ex.InstallIsMixed);
            Assert.Contains("ROLLBACK FAILED", ex.Message);
            Assert.Contains("MIXED", ex.Message);
            Assert.Contains(@"C:\x\app\AgentEyesApp.exe", ex.Message);
            Assert.Contains("agenteyes-setup install", ex.Message);
        }

        // ---- B1c: the failed-attempt marker -------------------------------------------------------

        [Fact]
        public void Marker_Absent_ReadsAsNull_AndClearIsFalse()
        {
            Assert.Null(UpdateAttemptMarker.Read(_layout));
            Assert.False(UpdateAttemptMarker.Clear(_layout));
        }

        [Fact]
        public void Marker_WriteFailed_ThenRead_RoundTrips_UnderTheInstallRoot()
        {
            var written = UpdateAttemptMarker.WriteFailed(_layout, "9.9.9", UpdateAttemptMarker.StageDownload, "SHA-256 mismatch", "cli");

            string path = _layout.UpdateAttemptMarkerPath;
            Assert.StartsWith(_layout.LocalRoot, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_layout.SetupStateDir, "last-update-attempt.json"), path);
            Assert.True(File.Exists(path));
            var read = UpdateAttemptMarker.Read(_layout)!;
            Assert.Equal("9.9.9", read.TargetVersion);
            Assert.Equal("failed", read.Outcome);
            Assert.Equal("download", read.Stage);
            Assert.Equal("SHA-256 mismatch", read.Reason);
            Assert.Equal("cli", read.By);
            Assert.Equal(written.AttemptedUtc, read.AttemptedUtc);
            Assert.Contains("v9.9.9", read.Describe());
            Assert.Contains("download: SHA-256 mismatch", read.Describe());
        }

        [Fact]
        public void Marker_Clear_RemovesIt()
        {
            UpdateAttemptMarker.WriteFailed(_layout, "9.9.9", UpdateAttemptMarker.StageStop, "could not be stopped", "cli");

            Assert.True(UpdateAttemptMarker.Clear(_layout));
            Assert.Null(UpdateAttemptMarker.Read(_layout));
            Assert.False(File.Exists(_layout.UpdateAttemptMarkerPath));
        }

        [Fact]
        public void Marker_Unreadable_ThrowsNamingThePath_NeverReadsAsNoFailure()
        {
            Directory.CreateDirectory(_layout.SetupStateDir);
            File.WriteAllText(_layout.UpdateAttemptMarkerPath, "{ not a record");

            var ex = Assert.Throws<InvalidDataException>(() => UpdateAttemptMarker.Read(_layout));

            Assert.Contains(_layout.UpdateAttemptMarkerPath, ex.Message);
            Assert.Contains("Delete it", ex.Message);
        }

        [Fact]
        public void Marker_StageOf_MapsEachCycleFailure()
        {
            var instance = new RunningAppInstance(1, @"C:\x\AgentEyesApp.exe", Array.Empty<string>());
            Assert.Equal("download", UpdateAttemptMarker.StageOf(new UpdateStageException("app", "x")));
            Assert.Equal("stop", UpdateAttemptMarker.StageOf(new AppStopFailedException(instance)));
            Assert.Equal("swap", UpdateAttemptMarker.StageOf(new UpdateSwapException("app", "x", Array.Empty<string>(), Array.Empty<string>())));
            Assert.Equal("relaunch", UpdateAttemptMarker.StageOf(new AppRelaunchFailedException(@"C:\x\AgentEyesApp.exe", new IOException("x"))));
            Assert.Equal("relaunch", UpdateAttemptMarker.StageOf(new AggregateException(new IOException("x"),
                new AppRelaunchFailedException(@"C:\x\AgentEyesApp.exe", new IOException("y")))));
            Assert.Equal("unknown", UpdateAttemptMarker.StageOf(new InvalidOperationException("command line unreadable")));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private UpdateRunner Runner() => new(_layout, ComponentRegistry.All, Download);

        private Task<string> Download(PlanItem item, CancellationToken ct)
        {
            string dest = Path.Combine(_temp, $"staged-{Guid.NewGuid():N}-{item.AssetName}");
            File.Copy(Path.Combine(_releaseDir, item.AssetName), dest);
            _downloaded.Add(dest);
            return Task.FromResult(dest);
        }

        private UpdatePlan Plan(params Component[] components)
        {
            var release = ReleaseSource.LoadLocalReleaseDir(_releaseDir);
            var installed = new InstalledStateReader(_layout).ReadAll(components);
            return UpdatePlanner.Plan(components, installed, release.Manifest);
        }

        private void InstallOld(Component c, string content)
        {
            string path = _layout.PathFor(c);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            // A text stand-in carries no file version; the bookkeeping says an old version so the
            // planner sees an Update rather than an unreadable install it must not touch.
            var m = InstalledManifest.Load(_layout);
            m.Set(c.Id, "1.11.2");
            m.Save(_layout);
        }

        private void WriteAsset(string name, string content) => File.WriteAllText(Path.Combine(_releaseDir, name), content);

        private string Sha(string assetName) => Hashing.Sha256OfFile(Path.Combine(_releaseDir, assetName));

        private void WriteManifest(string version, params (string Asset, string Sha)[] assets)
        {
            string entries = string.Join(",\n", assets.Select(a => $"    \"{a.Asset}\": {{ \"version\": \"{version}\", \"sha256\": \"{a.Sha}\" }}"));
            File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), $"{{\n  \"version\": \"{version}\",\n  \"assets\": {{\n{entries}\n  }}\n}}");
        }

        private sealed class HttpRequestExceptionStandIn : Exception
        {
            public HttpRequestExceptionStandIn(string message) : base(message) { }
        }
    }
}
