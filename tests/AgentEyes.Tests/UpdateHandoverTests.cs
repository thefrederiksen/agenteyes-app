using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes.AlwaysOn;
using AgentEyes.App;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #86 on the app's side: the in-app AutoUpdate follows the same stop -> replace -> relaunch
    /// path as the CLI by handing over to the installed setup CLI, and never replaces its own files;
    /// and an app exit hands the always-on clip over while leaving always-on enabled, so the restore
    /// on start brings it back.
    /// </summary>
    public sealed class UpdateHandoverTests : IDisposable
    {
        private readonly string _temp;
        private readonly string _releaseDir;
        private readonly InstallLayout _layout;
        private readonly Func<string, IReadOnlyList<string>, int> _startBefore;
        private readonly Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<ResolvedRelease>> _fetchBefore;
        private readonly Func<InstallLayout> _layoutBefore;
        private readonly Action<Action> _dispatchBefore;
        private readonly List<(string Exe, IReadOnlyList<string> Arguments)> _started = new();
        private readonly List<string> _waiting = new();
        private readonly List<(string Title, string Text)> _notices = new();
        private bool _sessionActive;

        public UpdateHandoverTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-handover-" + Guid.NewGuid().ToString("N"));
            _releaseDir = Path.Combine(_temp, "release");
            Directory.CreateDirectory(_releaseDir);
            _layout = new InstallLayout(Path.Combine(_temp, "root"));
            _startBefore = UpdateChecker.StartSetup;
            _fetchBefore = UpdateChecker.FetchLatest;
            _layoutBefore = UpdateChecker.Layout;
            _dispatchBefore = UpdateChecker.Dispatch;
            UpdateChecker.ResetForTests();
            UpdateChecker.StartSetup = (exe, args) => { _started.Add((exe, args.ToList())); return 777; };
            // The whole check runs against a LOCAL release and this test's install root; UI callbacks run inline.
            UpdateChecker.FetchLatest = _ => System.Threading.Tasks.Task.FromResult(ReleaseSource.LoadLocalReleaseDir(_releaseDir));
            UpdateChecker.Layout = () => _layout;
            UpdateChecker.Dispatch = a => a();
            UpdateChecker.SessionActive = () => _sessionActive;
            UpdateChecker.UpdateWaiting = v => _waiting.Add(v);
            UpdateChecker.InfoNotice = (title, text) => _notices.Add((title, text));
        }

        public void Dispose()
        {
            UpdateChecker.StartSetup = _startBefore;
            UpdateChecker.FetchLatest = _fetchBefore;
            UpdateChecker.Layout = _layoutBefore;
            UpdateChecker.Dispatch = _dispatchBefore;
            UpdateChecker.SessionActive = null;
            UpdateChecker.UpdateWaiting = null;
            UpdateChecker.InfoNotice = null;
            UpdateChecker.ResetForTests();
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }

        // ---- the real check, end to end against a local release (review N6 and B1c) -------------------

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_UpdateAvailable_NoSession_HandsOverToTheSetupCliAtOnce()
        {
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();

            await UpdateChecker.CheckAsync(userInitiated: false);

            var start = Assert.Single(_started);
            Assert.Equal(_layout.PathFor(ComponentRegistry.SetupCli), start.Exe);
            Assert.Equal(new[] { "update" }, start.Arguments);
            Assert.Empty(_waiting);
            Assert.Null(UpdateChecker.DeferredVersion);
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_UpdateAvailable_SessionActive_DefersWithTheBalloon_ThenInstallNowHandsOver()
        {
            // Review N6: the defer arm THROUGH UpdateChecker, not only the pure policy. A recording is in
            // progress: no handover, the tray is told which version waits, and the person's "Install
            // update now" hands over even though the session is still active.
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();
            _sessionActive = true;

            await UpdateChecker.CheckAsync(userInitiated: false);

            Assert.Empty(_started);
            Assert.Equal(new[] { "9.9.9" }, _waiting);
            Assert.Equal("9.9.9", UpdateChecker.DeferredVersion);

            UpdateChecker.InstallNow();

            var start = Assert.Single(_started);
            Assert.Equal(new[] { "update" }, start.Arguments);
            Assert.Null(UpdateChecker.DeferredVersion);
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_UpdateAvailable_SessionActive_ThenTheSessionEnds_HandsOver()
        {
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();
            _sessionActive = true;
            await UpdateChecker.CheckAsync(userInitiated: false);
            Assert.Empty(_started);

            UpdateChecker.OnSessionEnded();                     // still active: stays deferred
            Assert.Empty(_started);
            Assert.Equal("9.9.9", UpdateChecker.DeferredVersion);

            _sessionActive = false;
            UpdateChecker.OnSessionEnded();

            Assert.Single(_started);
            Assert.Null(UpdateChecker.DeferredVersion);
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_LastAttemptFailedForTheSameVersion_AutomaticCheckDoesNotHandOverAgain_AndShowsTheReason()
        {
            // Review B1c: the CLI failed at 9.9.9 (the record it left says why). The relaunched app must
            // NOT hand over again for 9.9.9 - that would be the stop -> fail -> relaunch loop - but log
            // and show the reason where the update state is shown (the tray balloon).
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();
            UpdateAttemptMarker.WriteFailed(_layout, "9.9.9", UpdateAttemptMarker.StageDownload, "SHA-256 mismatch; download rejected", "cli");

            await UpdateChecker.CheckAsync(userInitiated: false);

            Assert.Empty(_started);
            Assert.Empty(_waiting);
            var notice = Assert.Single(_notices);
            Assert.Contains("9.9.9", notice.Title);
            Assert.Contains("download: SHA-256 mismatch; download rejected", notice.Text);
            Assert.Contains("not retried by itself", notice.Text);
            Assert.NotNull(UpdateAttemptMarker.Read(_layout));                                  // the record stays
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_LastAttemptFailedForTheSameVersion_ThePersonsOwnCheckTriesAgain()
        {
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();
            UpdateAttemptMarker.WriteFailed(_layout, "9.9.9", UpdateAttemptMarker.StageDownload, "SHA-256 mismatch", "cli");

            await UpdateChecker.CheckAsync(userInitiated: true);

            Assert.Single(_started);
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_LastAttemptFailedForAnOlderVersion_ANewTargetClearsTheBlock_AndHandsOver()
        {
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();
            UpdateAttemptMarker.WriteFailed(_layout, "9.9.8", UpdateAttemptMarker.StageSwap, "disk full", "cli");

            await UpdateChecker.CheckAsync(userInitiated: false);

            Assert.Single(_started);
            Assert.Null(UpdateAttemptMarker.Read(_layout));
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_UpToDate_ClearsAStaleFailedAttempt_AndHandsNothingOver()
        {
            // The relaunch failed but the files WERE swapped: the app now runs the target version. The
            // record is behind us and is cleared.
            ReleaseWithAppInstalledAt("9.9.9");
            InstallSetupCliStub();
            UpdateAttemptMarker.WriteFailed(_layout, "9.9.9", UpdateAttemptMarker.StageRelaunch, "exe gone", "cli");

            await UpdateChecker.CheckAsync(userInitiated: false);

            Assert.Empty(_started);
            Assert.Null(UpdateAttemptMarker.Read(_layout));
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckAsync_UnreadableAttemptRecord_Throws_NamingTheFile_AndHandsNothingOver()
        {
            // No fallback: a record that is there but cannot be read is never taken as "no failure".
            ReleaseWithAppBehind("9.9.9");
            InstallSetupCliStub();
            Directory.CreateDirectory(_layout.SetupStateDir);
            File.WriteAllText(_layout.UpdateAttemptMarkerPath, "{ not a record");

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.CheckAsync(userInitiated: false));

            Assert.Contains(_layout.UpdateAttemptMarkerPath, ex.Message);
            Assert.Empty(_started);
        }

        private void ReleaseWithAppBehind(string version)
        {
            string asset = Path.Combine(_releaseDir, ComponentRegistry.App.Asset);
            File.WriteAllText(asset, "app build " + version);
            File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), $$"""
                {
                  "version": "{{version}}",
                  "assets": {
                    "{{ComponentRegistry.App.Asset}}": { "version": "{{version}}", "sha256": "{{Hashing.Sha256OfFile(asset)}}" }
                  }
                }
                """);
        }

        private void ReleaseWithAppInstalledAt(string version)
        {
            ReleaseWithAppBehind(version);
            string exe = _layout.PathFor(ComponentRegistry.App);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "app build " + version);
            var m = InstalledManifest.Load(_layout);
            m.Set(ComponentRegistry.App.Id, version);
            m.Save(_layout);
        }

        private void InstallSetupCliStub()
        {
            string setupExe = _layout.PathFor(ComponentRegistry.SetupCli);
            Directory.CreateDirectory(Path.GetDirectoryName(setupExe)!);
            File.WriteAllText(setupExe, "setup cli");
        }

        // ---- AutoUpdate hands over to the setup CLI ---------------------------------------------

        [Fact]
        public void HandOverToSetup_StartsTheInstalledSetupCliWithUpdate()
        {
            string setupExe = _layout.PathFor(ComponentRegistry.SetupCli);
            Directory.CreateDirectory(Path.GetDirectoryName(setupExe)!);
            File.WriteAllText(setupExe, "setup cli");

            int pid = UpdateChecker.HandOverToSetup(_layout, "1.11.5");

            Assert.Equal(777, pid);
            var start = Assert.Single(_started);
            Assert.Equal(setupExe, start.Exe);
            Assert.Equal(new[] { "update" }, start.Arguments);       // the CLI's update IS the stop/replace/relaunch cycle
        }

        [Fact]
        public void HandOverToSetup_SetupCliNotInstalled_ThrowsAndStartsNothing()
        {
            // No in-process swap to fall back to (issue #107 is why): the failure names the missing exe
            // and what to do.
            var ex = Assert.Throws<FileNotFoundException>(() => UpdateChecker.HandOverToSetup(_layout, "1.11.5"));

            Assert.Empty(_started);
            Assert.Equal(_layout.PathFor(ComponentRegistry.SetupCli), ex.FileName);
            Assert.Contains("Run the AgentEyes setup once", ex.Message);
        }

        [Fact]
        public void HandOverToSetup_NullLayout_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => UpdateChecker.HandOverToSetup(null!, "1.0.0"));
        }

        [Fact]
        public void InstallNow_NothingDeferred_StartsNothing()
        {
            UpdateChecker.InstallNow();
            Assert.Empty(_started);
        }

        [Fact]
        public void OnSessionEnded_NothingDeferred_StartsNothing()
        {
            UpdateChecker.OnSessionEnded();
            Assert.Empty(_started);
        }

        /// <summary>
        /// The app never replaces its own files: compiled AgentEyesApp.dll has no call from the updater
        /// (or anywhere else) into the engine's file-replacing entry points. Fail-closed on both arms:
        /// the presence arm proves the scan sees the updater's engine calls at all, the absence arm is
        /// the guard. What this cannot see: a file replaced through System.IO directly - that class of
        /// call is inventoried by ManifestWriterIlTests for the whole assembly.
        /// </summary>
        [Fact]
        public void UpdateChecker_CallsTheEnginesPlannerAndLayout_ButNeverAFileReplacer()
        {
            var engineCalls = CompiledCode.CallSites(CompiledCode.AppAssembly,
                callee => callee.StartsWith("AgentEyes.Setup.Engine.", StringComparison.Ordinal));
            var fromUpdater = engineCalls.Where(s => s.Method.StartsWith("AgentEyes.App.UpdateChecker::", StringComparison.Ordinal)).ToList();

            // Presence: the instrument is live and looking at the updater.
            Assert.Contains(fromUpdater, s => s.Callee.StartsWith("AgentEyes.Setup.Engine.UpdatePlanner::Plan", StringComparison.Ordinal));
            Assert.Contains(fromUpdater, s => s.Callee.StartsWith("AgentEyes.Setup.Engine.UpdateRestartPolicy::Decide", StringComparison.Ordinal));
            Assert.Contains(fromUpdater, s => s.Callee.StartsWith("AgentEyes.Setup.Engine.InstallLayout::PathFor", StringComparison.Ordinal));

            // Absence: nothing in the app assembly reaches a replacer.
            string[] replacers = { "InstallSwapper::", "UpdateRunner::", "Orchestrator::", "ArchiveInstaller::" };
            var offenders = engineCalls.Where(s => replacers.Any(r => s.Callee.Contains(r, StringComparison.Ordinal))).ToList();
            Assert.True(offenders.Count == 0,
                "the app assembly replaces install files itself:\n" + string.Join("\n", offenders.Select(o => $"{o.Method} -> {o.Callee}")));
        }

        // ---- always-on: exit hands over, and stays enabled for the restore on start ---------------

        [Fact]
        public void ShutdownForExit_HandsTheClipOverAndLeavesAlwaysOnEnabled_SoRestoreOnStartupBringsItBack()
        {
            var o = new AlwaysOnOptions
            {
                SetupName = "test", Counts = SoundSource.Mic, ThresholdDb = -40,
                ClipsFolder = Path.Combine(_temp, "clips"), WorkFolder = Path.Combine(_temp, "work"),
            };
            var history = new AlwaysOnHistory(Path.Combine(_temp, "history.jsonl"));
            var engine = new AlwaysOnEngine(() => new FakeRecorder(), () => DateTime.UtcNow, ownTimer: false, history: history);
            engine.Start(o);
            var cfg = new Config { AlwaysOnEnabled = true };
            using var controller = new AlwaysOnController(new RecordingService(), cfg, engine);

            controller.ShutdownForExit();

            Assert.Equal(AlwaysOnState.Off, engine.State);
            Assert.True(File.Exists(o.HandoverFile), "an app exit must hand the always-on state over, not close it as a crash would");
            Assert.True(cfg.AlwaysOnEnabled, "an exit must leave always-on enabled - that flag is what RestoreOnStartup reads");
            Assert.Contains(history.Events(null, HistoryFilter.All), e => e.Text.Contains("stopped for a restart") && e.Text.Contains("app exit"));
        }

        [Fact]
        public void RestoreOnStartup_AlwaysOnWasOff_DoesNotStartIt()
        {
            var engine = new AlwaysOnEngine(() => throw new InvalidOperationException("no recorder may be started in this test"),
                () => DateTime.UtcNow, ownTimer: false);
            var cfg = new Config { AlwaysOnEnabled = false };
            using var controller = new AlwaysOnController(new RecordingService(), cfg, engine);

            controller.RestoreOnStartup();

            Assert.Equal(AlwaysOnState.Off, engine.State);
            Assert.False(controller.IsOn);
        }

        private sealed class FakeRecorder : IPieceRecorder
        {
            public void Start(AlwaysOnOptions options, SoundLog sound) { }
            public void Stop() { }
            public bool HasExited => false;
            public string Encoder => "fake";
            public string StderrTail => "fake ffmpeg";
            public string ProcessState => "still running (pid 1)";
            public void Dispose() { }
        }
    }
}
