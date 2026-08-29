using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WinForms = System.Windows.Forms;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// System-tray presence: a NotifyIcon with quick actions that drive the shared RecordingService.
    /// The app lives here even when the window is hidden.
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ContextMenuStrip _menu;
        private readonly RecordingService _svc;
        private readonly Config _cfg;
        private readonly Action _showWindow;
        private readonly Action _showTests;

        private WinForms.ToolStripMenuItem _statusItem = null!;
        private WinForms.ToolStripMenuItem _recordItem = null!;
        private WinForms.ToolStripMenuItem _restartItem = null!;
        private string? _stagedExe;

        /// <summary>Quit was clicked while a recording was in progress and the post-recording
        /// sequence is still finishing. UI thread only.</summary>
        private bool _quitting;

        /// <summary>Shutdown has already been requested. UI thread only.</summary>
        private bool _shuttingDown;

        public TrayHost(RecordingService svc, Config cfg, Action showWindow, Action showTests)
        {
            _svc = svc; _cfg = cfg; _showWindow = showWindow; _showTests = showTests;

            _menu = BuildMenu();
            _icon = new WinForms.NotifyIcon
            {
                Icon = AppIcon(),
                Text = "AgentEyes",
                Visible = true,
                ContextMenuStrip = _menu,
            };
            // Single left-click opens the window (issue #12); DoubleClick kept for users who still double-click.
            _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _showWindow(); };
            _icon.DoubleClick += (_, _) => _showWindow();
            _icon.BalloonTipClicked += (_, _) => RestartForUpdate();
            _menu.Opening += (_, _) => RefreshMenu();
        }

        /// <summary>A background update has been downloaded and applied to disk. Show a single
        /// non-blocking balloon and reveal a tray item; the new exe runs on the next restart
        /// (click the balloon or the item to restart now). Must run on the UI thread.</summary>
        public void NotifyUpdateStaged(string version, string exePath)
        {
            _stagedExe = exePath;
            _restartItem.Text = $"Restart to finish update (v{version})";
            _restartItem.Visible = true;
            _icon.ShowBalloonTip(8000, "AgentEyes updated",
                $"v{version} is ready and will run the next time you open AgentEyes. Click here to restart now.",
                WinForms.ToolTipIcon.Info);
        }

        /// <summary>A non-blocking informational balloon (e.g. "you are up to date"). UI thread.</summary>
        public void ShowInfo(string title, string text) =>
            _icon.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.Info);

        private void RestartForUpdate()
        {
            if (_stagedExe == null) return;
            UpdateChecker.RequestRestart(_stagedExe);
        }

        /// <summary>The exe's embedded icon (the product icon); generic app icon if extraction fails.</summary>
        private static System.Drawing.Icon AppIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe != null) return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application;
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private WinForms.ContextMenuStrip BuildMenu()
        {
            var menu = new WinForms.ContextMenuStrip();
            _statusItem = new WinForms.ToolStripMenuItem("Status: idle") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add(new WinForms.ToolStripMenuItem("Show window", null, (_, _) => _showWindow()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Tests...", null, (_, _) => _showTests()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Quick screenshot", null, (_, _) => Safe(() => _svc.Screenshot(PrimaryScreen(), null))));

            _recordItem = new WinForms.ToolStripMenuItem("Start recording", null, (_, _) => ToggleRecord());
            menu.Items.Add(_recordItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenFolder()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Check for updates", null, (_, _) => UpdateChecker.CheckAndPrompt()));

            _restartItem = new WinForms.ToolStripMenuItem("Restart to finish update", null, (_, _) => RestartForUpdate()) { Visible = false };
            menu.Items.Add(_restartItem);

            var login = new WinForms.ToolStripMenuItem("Run at login") { Checked = Autostart.IsEnabled(), CheckOnClick = true };
            login.CheckedChanged += (_, _) => Safe(() => { Autostart.Set(login.Checked); _cfg.RunAtLogin = login.Checked; _cfg.Save(); });
            menu.Items.Add(login);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("Quit", null, (_, _) => Quit()));
            return menu;
        }

        private void RefreshMenu()
        {
            if (_quitting)
            {
                // Quit is waiting on the post-recording sequence (issue #151). Say so rather than
                // reporting "idle", which reads as "nothing is happening, it has hung".
                _statusItem.Text = "Finishing the recording before closing...";
                _recordItem.Enabled = false;
                return;
            }

            var s = _svc.Status();
            _statusItem.Text = s.State == "recording"
                ? $"Recording {(int)s.ElapsedSeconds / 60:D2}:{(int)s.ElapsedSeconds % 60:D2}  ({s.Mode})"
                : "Status: idle";
            _recordItem.Text = _svc.IsRecording ? "Stop recording" : $"Start recording ({LastPreset()?.Name ?? "no preset"})";
        }

        // Quick-record from the tray uses the last-used preset (or the first one).
        private void ToggleRecord()
        {
            if (_svc.IsRecording) { StopInBackground(); return; }
            var preset = LastPreset();
            if (preset == null) { Safe(() => _svc.Screenshot(PrimaryScreen(), null)); return; }
            Safe(() =>
            {
                PresetCapture.Start(_svc, preset, _cfg);
                _cfg.LastUsedPresetId = preset.Id;
                _cfg.Save();
            });
        }

        private CapturePreset? LastPreset()
        {
            var presets = PresetStore.Load();
            return presets.FirstOrDefault(p => p.Id == _cfg.LastUsedPresetId) ?? presets.FirstOrDefault();
        }

        /// <summary>
        /// Stop a recording from the tray. The tray is the PRIMARY stop control - the app normally
        /// runs with --tray and never builds MainWindow - so this must produce exactly what a
        /// window-stopped or API-stopped recording produces. It used to call a bare
        /// <c>_svc.Stop()</c> and leave the recording as raw media with no mixed audio, no
        /// thumbnail, no transcript and no title (issue #151); it now goes through the one shared
        /// stop operation like every other keeping caller.
        /// </summary>
        private void StopInBackground()
        {
            // The directory is read BEFORE the stop: a failed stop leaves the service with no
            // session, and this is the only way left to name the recording (issue #153). The generic
            // Safe() wrapper is deliberately not used here - a lost recording must be logged as a
            // lost recording, not as "tray action".
            string? dir = _svc.Status().Dir;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { RecordingStop.Keep(_svc); }
                catch (Exception ex) { Log.Error($"[TrayHost] stopping the recording FAILED (dir={dir ?? "(unknown)"})", ex); }
            });
        }

        private static int PrimaryScreen()
        {
            foreach (var m in Monitors.All()) if (m.Primary) return m.Index;
            return 1;
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(RecordingPaths.Root);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RecordingPaths.Root}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("open folder", ex); }
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception ex) { Log.Error("tray action", ex); }
        }

        /// <summary>
        /// Quit with a recording in progress used to call a bare <c>_svc.Stop()</c> inside an EMPTY
        /// catch: the recording was left as raw media with no post-processing, and if the stop threw
        /// nothing was written anywhere (issue #151). Now it stops through the one shared operation,
        /// finishes the post-recording sequence BEFORE the process exits - killing it mid-mux is
        /// exactly how a recording ends up as raw media - and logs any failure with the recording
        /// directory instead of swallowing it.
        ///
        /// UI thread. Feedback is immediate (a balloon and the tray status line); the waiting is
        /// done on a background task so the menu click never blocks.
        /// </summary>
        private void Quit()
        {
            if (_quitting)
            {
                // Clicked again while the sequence is running: the user is asking to go NOW.
                Log.Info("[TrayHost] Quit: clicked again while finishing - shutting down immediately");
                ShutdownNow();
                return;
            }
            if (!_svc.IsRecording) { ShutdownNow(); return; }

            _quitting = true;
            string? dir = _svc.Status().Dir;
            Log.Info($"[TrayHost] Quit: a recording is in progress (dir={dir ?? "(unknown)"}); finishing it before exit");
            _statusItem.Text = "Finishing the recording...";
            _icon.Text = "AgentEyes - finishing the recording";
            ShowInfo("AgentEyes is closing",
                "Finishing the recording (mixing, transcript, title) before it exits.");

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var stopped = RecordingStop.Keep(_svc);
                    stopped.PostProcessing.GetAwaiter().GetResult();
                    Log.Info($"[TrayHost] Quit: post-recording finished for {stopped.Result.Dir}");
                }
                catch (Exception ex)
                {
                    Log.Error($"[TrayHost] Quit: stopping the recording FAILED (dir={dir ?? "(unknown)"})", ex);
                }
                finally
                {
                    // The user can click Quit a second time and shut the app down before this
                    // finishes, so the application may already be gone.
                    var app = System.Windows.Application.Current;
                    if (app != null) app.Dispatcher.BeginInvoke(new Action(ShutdownNow));
                    else Log.Info("[TrayHost] Quit: the app had already shut down");
                }
            });
        }

        /// <summary>Take the tray icon down and end the process. UI thread. Idempotent: a forced
        /// second Quit and the finishing task's completion can both reach it.</summary>
        private void ShutdownNow()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            Log.Info("[TrayHost] ShutdownNow: quitting");
            _icon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }
}
