using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentEyes.Setup.Engine;

namespace AgentEyes.App
{
    /// <summary>
    /// Background updater. When a newer release exists it HANDS THE UPDATE OVER to the installed setup
    /// CLI (<c>agenteyes-setup update</c>), which takes the one stop -> replace -> relaunch path every
    /// update takes (<see cref="UpdateRestartCycle"/>, issue #86): it asks this process to quit through
    /// <see cref="QuitRequest"/>, waits for it to leave - always-on hands its open clip over on the way
    /// out - replaces the files, and starts the app again with this process's own arguments (a
    /// <c>--tray</c> app comes back as a <c>--tray</c> app, issue #61).
    ///
    /// This process therefore never replaces its own files. It used to: it downloaded and swapped the
    /// exes in place and then restarted itself. Issue #107 showed why that is unsafe - AgentEyes is a
    /// single-file self-contained host that reads its managed assemblies out of AgentEyesApp.exe lazily,
    /// so once that exe was replaced on disk every not-yet-exercised feature died on first use with
    /// FileNotFoundException - and issue #86 asked for the app's own update to go through the same
    /// stop/replace/relaunch decision as the CLI and the wizard. The only decision left to this class is
    /// the pure <see cref="UpdateRestartPolicy"/>: hand over NOW, or DEFER while a recording session is
    /// active (then hand over when the session ends, or when the person asks from the tray).
    ///
    /// There are NO modal dialogs. An "up to date" result is silent on auto-checks so it never nags;
    /// the manual "Check for updates" menu item reports the outcome via a balloon. When the handover is
    /// deferred the tray shows a single non-blocking balloon and an "Install update now" item.
    /// </summary>
    internal static class UpdateChecker
    {
        private static int _busy;

        // A handover that was deferred because a session was active when the update was found (issue
        // #107). Held so the app can complete it when the session ends.
        private static string? _deferredVersion;

        /// <summary>Set by App once the tray exists: a newer release is ready and waits for the
        /// recording session to end (version). Non-blocking notification.</summary>
        public static Action<string>? UpdateWaiting;

        /// <summary>Set by App once the tray exists: a non-blocking informational balloon (title, text).</summary>
        public static Action<string, string>? InfoNotice;

        /// <summary>Set by App: true while a recording session is in progress, so an available update
        /// defers its handover instead of truncating in-flight capture (issue #107). Read from a
        /// background thread, so App keeps it to thread-safe primitives.</summary>
        public static Func<bool>? SessionActive;

        /// <summary>
        /// Testable seam (issue #86): how the setup CLI is started - (exe, arguments) -> pid. Production
        /// starts the installed agenteyes-setup.exe; a test records the call and starts nothing.
        /// </summary>
        internal static Func<string, IReadOnlyList<string>, int> StartSetup { get; set; } = StartProcess;

        /// <summary>Manual check from the tray menu: reports the outcome (incl. "up to date") via a balloon.</summary>
        public static void CheckAndPrompt()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _ = RunAsync(userInitiated: true);
        }

        /// <summary>Automatic check on startup (Config.AutoUpdate): silent when up to date or offline.
        /// Runs after a short delay so it does not compete with launch.</summary>
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
                // layout); the setup CLI's update would leave stale DLLs behind.
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

                // No "install now?" gate - AutoUpdate being on IS the consent. Nothing is downloaded or
                // replaced by this process: the setup CLI does that, with this process stopped.
                AgentEyes.Log.Info($"update: v{version} available ({plan.Actionable.Count} component(s) behind)");
                ApplyDecision(version, layout);
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
        /// Decide what to do about the available update (issues #107, #86): hand over to the setup CLI
        /// now, or defer while a recording session is active. Files are never replaced under this
        /// process on either branch. Every branch logs an explicit decision line. Runs on the background
        /// update thread.
        /// </summary>
        private static void ApplyDecision(string version, InstallLayout layout)
        {
            bool sessionActive = SessionActive?.Invoke() ?? false;
            var decision = UpdateRestartPolicy.Decide(sessionActive);
            if (decision == UpdateApplyDecision.DeferSessionActive)
            {
                _deferredVersion = version;
                AgentEyes.Log.Info($"update: v{version} waits - recording in progress. It is installed when the session ends (or from the tray).");
                // Surface the tray balloon so the user knows an update is pending and can install it now.
                Notify(() => UpdateWaiting?.Invoke(version));
                return;
            }

            AgentEyes.Log.Info($"update: v{version} available and no session is active - handing over to the setup engine now.");
            HandOverToSetup(layout, version);
        }

        /// <summary>
        /// Called by App when a recording session ends (issue #107). If an update was deferred while the
        /// session was active, hand it over now - unless another session is still in progress, in which
        /// case it stays deferred until that one ends too. No-op when nothing was deferred.
        /// </summary>
        public static void OnSessionEnded()
        {
            if (_deferredVersion == null) return;                   // nothing was deferred
            if (SessionActive?.Invoke() ?? false)                  // still busy with another session
            {
                AgentEyes.Log.Info("update: session ended but another session is still active; keeping the update deferred.");
                return;
            }
            var version = _deferredVersion;
            _deferredVersion = null;
            AgentEyes.Log.Info($"update: session ended - handing v{version} over to the setup engine now (deferred update).");
            // An event handler is an entry point: a failure here is logged and told, never thrown into the recorder.
            try { HandOverToSetup(InstallLayout.Default(), version); }
            catch (Exception ex) { ReportHandoverFailure(version, ex); }
        }

        /// <summary>The tray's "Install update now" (balloon or menu item): hand the deferred update over
        /// at once, even while a session is active - the person asked. No-op when nothing waits.</summary>
        public static void InstallNow()
        {
            var version = _deferredVersion;
            if (version == null)
            {
                AgentEyes.Log.Info("update: install now asked, but no update is waiting");
                return;
            }
            _deferredVersion = null;
            AgentEyes.Log.Info($"update: install now asked from the tray - handing v{version} over to the setup engine.");
            try { HandOverToSetup(InstallLayout.Default(), version); }
            catch (Exception ex) { ReportHandoverFailure(version, ex); }
        }

        /// <summary>
        /// Hand the update to the installed setup CLI (issue #86): <c>agenteyes-setup update</c> stops this
        /// app (asking first through <see cref="QuitRequest"/>, force after the bound), replaces the
        /// files, and starts the app again with the arguments this process was started with. Returns the
        /// setup CLI's pid. Throws when the CLI is not installed - there is no in-process swap to fall
        /// back to (issue #107 is why), and the message says what to do.
        /// </summary>
        internal static int HandOverToSetup(InstallLayout layout, string version)
        {
            ArgumentNullException.ThrowIfNull(layout);
            var setupExe = layout.PathFor(ComponentRegistry.SetupCli);
            if (!File.Exists(setupExe))
                throw new FileNotFoundException(
                    "the in-app update needs the setup CLI (agenteyes-setup.exe), which is not installed. "
                    + "Run the AgentEyes setup once to install it, then check for updates again.", setupExe);
            int pid = StartSetup(setupExe, new[] { "update" });
            AgentEyes.Log.Info($"update: v{version} handed over to \"{setupExe}\" update (pid {pid}); it stops this app, "
                               + "replaces the files and starts it again with the same arguments");
            return pid;
        }

        private static void ReportHandoverFailure(string version, Exception ex)
        {
            AgentEyes.Log.Error($"update: handing v{version} over to the setup engine FAILED", ex);
            Notify(() => InfoNotice?.Invoke("AgentEyes update failed", ex.Message));
        }

        private static int StartProcess(string exe, IReadOnlyList<string> arguments)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException($"Process.Start returned no process for {exe}");
            return p.Id;
        }

        private static void Notify(Action a) =>
            Application.Current?.Dispatcher.BeginInvoke(a);
    }
}
