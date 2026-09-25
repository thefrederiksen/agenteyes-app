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
        private readonly InstallLayout _layout;
        private readonly Func<string, IReadOnlyList<string>, int> _startBefore;
        private readonly List<(string Exe, IReadOnlyList<string> Arguments)> _started = new();

        public UpdateHandoverTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-handover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
            _layout = new InstallLayout(Path.Combine(_temp, "root"));
            _startBefore = UpdateChecker.StartSetup;
            UpdateChecker.StartSetup = (exe, args) => { _started.Add((exe, args.ToList())); return 777; };
        }

        public void Dispose()
        {
            UpdateChecker.StartSetup = _startBefore;
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
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
