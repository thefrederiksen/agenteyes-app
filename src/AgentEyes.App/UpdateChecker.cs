using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentEyes.Setup.Engine;

namespace AgentEyes.App
{
    /// <summary>
    /// Background updater (cc-director style). When a newer release exists it downloads,
    /// SHA-256-verifies, and swaps the on-disk exes SILENTLY in the background.
    ///
    /// Issue #107: AgentEyes is a single-file self-contained host that reads its managed assemblies
    /// out of AgentEyesApp.exe lazily, on first use. Once that exe is replaced on disk, the still-
    /// running pre-update process can no longer load any assembly it had not already loaded, so every
    /// not-yet-exercised feature dies on first use with FileNotFoundException while the app appears to
    /// keep running. The old "no forced restart, the new version just runs next launch" behaviour left
    /// exactly that silent-degraded process. So once an update has been APPLIED on disk the running
    /// process is never left serving from the stale bundle: it RESTARTS into the new exe (strict
    /// shut-down-old-then-start-new ordering, see <see cref="App.OnExit"/> +
    /// <see cref="StartPendingRestart"/>), and only DEFERS that restart while a recording
    /// session is active (then restarts when the session ends, or on the next clean launch). The
    /// restart-vs-defer choice is the pure <see cref="UpdateRestartPolicy"/>.
    ///
    /// There are NO modal dialogs. An "up to date" result is silent on auto-checks so it never nags;
    /// the manual "Check for updates" menu item reports the outcome via a balloon. When a restart is
    /// deferred the tray shows a single non-blocking balloon so the user can also restart manually.
    /// </summary>
    internal static class UpdateChecker
    {
        private static int _busy;
        private static string? _restartExe;

        // A restart that was deferred because a session was active when the update applied. Held so
        // the app can complete it when the session ends (issue #107).
        private static string? _deferredExe;
        private static string? _deferredVersion;

        /// <summary>Set by App once the tray exists: non-blocking notifications.</summary>
        public static Action<string, string>? StagedUpdate;   // (version, exePath)
        public static Action<string, string>? InfoNotice;     // (title, text)

        /// <summary>Set by App: true while a recording session is in progress, so an
        /// applied update defers its restart instead of truncating in-flight capture (issue #107).
        /// Read from a background thread, so App keeps it to thread-safe primitives.</summary>
        public static Func<bool>? SessionActive;

        /// <summary>Manual check from the tray menu: reports the outcome (incl. "up to date") via a balloon.</summary>
        public static void CheckAndPrompt()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _ = RunAsync(userInitiated: true);
        }

        /// <summary>Automatic check on startup (Config.AutoUpdate): downloads and stages a newer release in
        /// the background, silent when up to date or offline. Runs after a short delay so it does not
        /// compete with launch.</summary>
        public static void AutoCheckOnStartup()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _ = RunAsync(userInitiated: false);
        }

        private static async Task RunAsync(bool userInitiated)
        {
            try
            {
                if (!userInitiated) await Task.Delay(TimeSpan.FromSeconds(4));
                EngineLog.Sink ??= line => AgentEyes.Log.Info($"[setup-engine] {line}");
                var layout = InstallLayout.Default();

                // The Inno v0.1 install must migrate through the new setup (it wipes the old multi-file
                // layout); an in-place exe swap would leave stale DLLs behind.
                if (InnoMigration.IsInnoInstall(layout))
                {
                    AgentEyes.Log.Info("update: old v0.1 (Inno) install - in-app update skipped; run the setup once to migrate");
                    if (userInitiated)
                        Notify(() => InfoNotice?.Invoke("AgentEyes",
                            "This copy needs a one-time setup re-run to migrate before in-app updates work."));
                    return;
                }

                var release = await new ReleaseSource().FetchLatestAsync(CancellationToken.None);
                string version = $"{release.Manifest.Version}";
                var reader = new InstalledStateReader(layout);
                var installed = reader.ReadAll(ComponentRegistry.All);
                var plan = UpdatePlanner.Plan(ComponentRegistry.All, installed, release.Manifest);

                if (!plan.HasWork)
                {
                    AgentEyes.Log.Info($"update: already up to date (v{version})");
                    if (userInitiated)
                        Notify(() => InfoNotice?.Invoke("AgentEyes", $"You are on the latest version (v{version})."));
                    return;
                }

                // Download + verify + swap silently. No "install now?" gate - AutoUpdate being on IS the consent.
                AgentEyes.Log.Info($"update: v{version} available - downloading and staging in the background");
                var source = new ReleaseSource();
                var orchestrator = new Orchestrator(layout, reader);
                var result = await orchestrator.RunAsync(ComponentRegistry.All, release.Manifest,
                    (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));

                var run = result.Run;
                if (run is null || run.Failed > 0)
                {
                    var errors = run?.Results.Where(r => r.Error != null).Select(r => $"{r.ComponentId}: {r.Error}")
                        ?? Enumerable.Empty<string>();
                    AgentEyes.Log.Error("update did not fully apply: " + string.Join("; ", errors));
                    Notify(() => InfoNotice?.Invoke("AgentEyes update failed",
                        "The update could not be applied. See the log: " + AgentEyes.Log.CurrentFile));
                    return;
                }

                // Keep the Add/Remove Programs version current. Best-effort.
                try
                {
                    var appVersion = release.Manifest.TryGetAsset(ComponentRegistry.App.Asset)?.Version
                        ?? release.Manifest.Version;
                    InstallFinalizer.RegisterUninstallEntry(layout, appVersion);
                }
                catch (Exception ex) { AgentEyes.Log.Error("ARP version refresh failed", ex); }

                var exe = layout.PathFor(ComponentRegistry.App);
                AgentEyes.Log.Info($"update: applied v{version} on disk ({run.Installed + run.Updated} component(s))");
                ApplyDecision(version, exe);
            }
            catch (Exception ex)
            {
                AgentEyes.Log.Error("update check failed", ex);
                if (userInitiated)
                    Notify(() => InfoNotice?.Invoke("AgentEyes", "Could not check for updates: " + ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>
        /// Decide what to do now that the update has been applied on disk (issue #107): restart into
        /// the new exe immediately, or defer the restart while a recording session is
        /// active. The pre-update process is NEVER left serving from the replaced single-file bundle.
        /// Every branch logs an explicit decision line. Runs on the background update thread; the
        /// actual shutdown is marshalled to the UI thread by <see cref="RequestRestart"/> via
        /// <see cref="Notify"/>.
        /// </summary>
        private static void ApplyDecision(string version, string exePath)
        {
            bool sessionActive = SessionActive?.Invoke() ?? false;
            var decision = UpdateRestartPolicy.Decide(sessionActive);
            if (decision == UpdateApplyDecision.DeferSessionActive)
            {
                _deferredExe = exePath;
                _deferredVersion = version;
                AgentEyes.Log.Info($"update staged v{version}; deferred - recording in progress. Will restart when the session ends (or on next launch).");
                // Surface the tray balloon so the user knows an update is pending and can restart manually.
                Notify(() => StagedUpdate?.Invoke(version, exePath));
                return;
            }

            AgentEyes.Log.Info($"update applied v{version}; restarting into the new exe now (no active session).");
            Notify(() => RequestRestart(exePath));
        }

        /// <summary>
        /// Called by App when a recording session ends (issue #107). If an update
        /// restart was deferred while the session was active, complete it now - unless another
        /// session is still in progress, in
        /// which case it stays deferred until that one ends too. No-op when nothing was deferred.
        /// </summary>
        public static void OnSessionEnded()
        {
            if (_deferredExe == null) return;                       // nothing was deferred
            if (SessionActive?.Invoke() ?? false)                  // still busy with another session
            {
                AgentEyes.Log.Info("update: session ended but another session is still active; keeping the restart deferred.");
                return;
            }
            var exe = _deferredExe;
            var version = _deferredVersion;
            _deferredExe = null;
            _deferredVersion = null;
            AgentEyes.Log.Info($"update applied v{version}; session ended - restarting into the new exe now (deferred restart).");
            Notify(() => RequestRestart(exe));
        }

        /// <summary>Triggered from the tray (balloon or menu) or a completed defer to restart into the
        /// freshly applied exe now. Sets the pending exe and shuts the current process down; the new
        /// exe is started from <see cref="StartPendingRestart"/> only after the mutex + port are freed
        /// (strict shut-down-old-then-start-new ordering). Must be called on the UI thread.</summary>
        public static void RequestRestart(string exePath)
        {
            _restartExe = exePath;
            AgentEyes.Log.Info("update: shutting down the old instance before starting the new exe");
            Application.Current.Shutdown();
        }

        /// <summary>
        /// Called from App.OnExit AFTER the single-instance mutex is released, so the freshly
        /// updated exe can take the lock. No-op unless a restart was requested.
        /// </summary>
        public static void StartPendingRestart()
        {
            if (_restartExe == null) return;
            try
            {
                AgentEyes.Log.Info($"update: old instance torn down (mutex + port released); starting new exe {_restartExe}");
                Process.Start(new ProcessStartInfo(_restartExe) { UseShellExecute = true });
            }
            catch (Exception ex) { AgentEyes.Log.Error("restart after update failed", ex); }
        }

        private static void Notify(Action a) =>
            Application.Current?.Dispatcher.BeginInvoke(a);
    }
}
