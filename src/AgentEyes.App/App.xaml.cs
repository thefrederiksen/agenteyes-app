using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AgentEyes;

namespace AgentEyes.App
{
    public partial class App : Application
    {
        private static readonly string CrashLog =
            Path.Combine(Path.GetTempPath(), "AgentEyes-crash.log");

        private Mutex? _mutex;
        private Config? _cfg;
        private RecordingService? _service;
        private RestServer? _rest;
        private RepairService? _repair;
        private TrayHost? _tray;
        private MainWindow? _window;
        private TestPanel? _tests;
        private KeyboardHook? _captureRegionHook;
        private KeyboardHook? _captureFullHook;

        /// <summary>Raised on the UI thread after a capture (shortcut or API) is saved, so the
        /// Capture gallery refreshes live (issue #64). Payload is the saved PNG path.</summary>
        internal event Action<string>? CaptureSaved;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single instance: only one process owns the tray + API port.
            _mutex = new Mutex(initiallyOwned: true, "AgentEyes-singleinstance", out bool created);
            if (!created)
            {
                MessageBox.Show("AgentEyes is already running (see the system tray).", "AgentEyes");
                Shutdown();
                return;
            }

            // Tray app: do not exit when the window closes.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log(ev.ExceptionObject as Exception, "AppDomain");
            DispatcherUnhandledException += OnDispatcherUnhandled;

            base.OnStartup(e);

            try
            {
                StorageMigration.Run();   // qa-record -> AgentEyes folders, one time
                _cfg = Config.Load();
                _service = new RecordingService();
                // Issue #33: a live preview feed is a second output on the recording's own ffmpeg, so
                // it has to be asked for BEFORE the recording starts. This carries the person's
                // persisted "show preview" choice into the first recording of the session; the HUD
                // updates it whenever they change their mind. Left false - the default - a recording
                // is byte-for-byte the recording it was before the feature existed (AC11).
                _service.PreviewArmed = _cfg.HudPreviewVisible;

                // Issue #151: the post-recording sequence is wired ONCE, here, so it is identical on
                // every stop path - including this process's normal shape, which is --tray with no
                // MainWindow at all. Every caller that stops a kept recording goes through
                // RecordingStop.Keep; nothing re-implements the sequence.
                RecordingStop.Configure(_cfg);

                if (_cfg.ApiEnabled)
                {
                    try { _rest = new RestServer(_service, _cfg.Port,
                            file => Dispatcher.BeginInvoke(() => CaptureSaved?.Invoke(file)),
                            () => _cfg!.CaptureSaveFolder); _rest.Start(); }
                    catch (Exception ex) { AgentEyes.Log.Error("REST start failed", ex); }
                }

                // Issue #142: the repair passes (missing titles, missing thumbnails) belong to the
                // APP, not to the window. The app is normally started with --tray and never builds
                // MainWindow, and recordings are driven through the REST API above just as often as
                // through the UI - a repair timer owned by the window did not exist in either case.
                _repair = new RepairService(() => _service!.IsRecording);
                _repair.Start();

                _tray = new TrayHost(_service, _cfg, ShowWindow, ShowTests);
                InstallCaptureHooks();

                // Auto-update (opt-out in Settings): on startup, quietly ask the public releases repo
                // for the latest version; if newer, download + swap in the BACKGROUND and surface a
                // single non-blocking tray balloon - it applies on the next restart. No modal nagging.
                UpdateChecker.StagedUpdate = _tray.NotifyUpdateStaged;
                UpdateChecker.InfoNotice = _tray.ShowInfo;
                // Issue #107: an applied in-place update must never leave this process serving from the
                // replaced single-file bundle. Tell UpdateChecker how to see an active session (so it
                // defers the restart) and complete a deferred restart when a recording session ends.
                UpdateChecker.SessionActive = IsSessionActive;
                _service.RecordingStopped += UpdateChecker.OnSessionEnded;
                // Issue #152: the capture ending is NOT the app going idle - the mux, the
                // transcription and the title run for minutes afterwards, and a restart fired into
                // that gap left the recording with raw media and no transcript. This is the honest
                // "nothing is in flight any more" signal, so a deferred restart waits for the WORK.
                PostRecording.WorkIdle += UpdateChecker.OnSessionEnded;
                if (_cfg.AutoUpdate) UpdateChecker.AutoCheckOnStartup();

                bool startHidden = e.Args.Any(a => a is "--tray" or "--minimized");
                AgentEyes.Log.Info($"app started (hidden={startHidden}, api={(_rest != null ? _rest.Url : "off")})");

                // First-run / signed-out gate (issue #87): AgentEyes runs only on DevThrottle. With no
                // stored credential, prompt sign-in up front so transcription and AI work. If dismissed,
                // the app still runs but hosted features fail explicitly until the user signs in.
                if (!startHidden && !AgentEyes.DevThrottle.DevThrottleAccount.IsSignedIn)
                {
                    try { DevThrottleSignInWindow.Prompt(null); }
                    catch (Exception ex) { AgentEyes.Log.Error("DevThrottle sign-in gate failed", ex); }
                }

                if (!startHidden) ShowWindow();
            }
            catch (Exception ex)
            {
                Log(ex, "startup");
                MessageBox.Show("AgentEyes could not start. See the log:\n" + AgentEyes.Log.CurrentFile, "AgentEyes");
                Shutdown(1);
            }
        }

        /// <summary>
        /// Issue #107: true while a session would be destroyed by a restart. Read by the background
        /// update thread to DEFER an applied update's restart rather than truncate work in flight.
        /// Uses only thread-safe primitives (a volatile state string via
        /// <see cref="RecordingService.IsRecording"/>, an interlocked count via
        /// <see cref="PostRecording.IsBusy"/>) so it is safe off the UI thread.
        ///
        /// Issue #152 widened it from "recording" to "recording OR post-processing"
        /// (<see cref="SessionReadiness.IsBusy"/>): capture stops in a second, but the mux,
        /// transcription and title that follow it run for minutes, and a restart in that window
        /// stranded the recording.
        /// </summary>
        private bool IsSessionActive() =>
            SessionReadiness.IsBusy(_service?.IsRecording ?? false, PostRecording.IsBusy);

        // ---- capture (issue #64) ------------------------------------------

        /// <summary>Arm both capture shortcuts (region + full-screen). The WH_KEYBOARD_LL
        /// callback must stay fast, so the real capture work is queued
        /// to the next dispatcher frame with BeginInvoke (issue #35).</summary>
        private void InstallCaptureHooks()
        {
            _captureRegionHook = ArmCaptureHook(_cfg!.CaptureRegionTrigger, "region",
                () => Dispatcher.BeginInvoke(CaptureRegionInteractive));
            _captureFullHook = ArmCaptureHook(_cfg.CaptureFullTrigger, "full-screen",
                () => Dispatcher.BeginInvoke(() => CaptureFullScreenAndNotify(1)));
        }

        private static KeyboardHook? ArmCaptureHook(string trigger, string label, Action onActivated)
        {
            try
            {
                var hook = new KeyboardHook(TriggerSpec.Parse(trigger));
                hook.Activated += onActivated;
                hook.Install();
                AgentEyes.Log.Info($"capture {label} shortcut armed: {trigger}");
                return hook;
            }
            catch (Exception ex)
            {
                // No silent degradation: the user must know the shortcut is dead.
                AgentEyes.Log.Error($"capture {label} hook install failed", ex);
                MessageBox.Show($"The {label} capture shortcut could not be installed.\n\n"
                    + ex.Message + "\n\nSee the log: " + AgentEyes.Log.CurrentFile,
                    "AgentEyes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        /// <summary>Live trigger change from the Capture view (issue #64): drop both hooks and
        /// re-arm from the saved config. No restart needed.</summary>
        internal void ReArmCaptureHooks()
        {
            try { _captureRegionHook?.Dispose(); } catch { }
            try { _captureFullHook?.Dispose(); } catch { }
            _captureRegionHook = null;
            _captureFullHook = null;
            InstallCaptureHooks();
        }

        /// <summary>Region snip: show the in-app overlay, and if the user completes a selection
        /// capture that rectangle (to clipboard + PNG). Esc cancels with no file written. Runs on
        /// the UI thread (overlay is WPF).</summary>
        internal void CaptureRegionInteractive()
        {
            try
            {
                AgentEyes.Log.Info("capture: region shortcut -> overlay");
                var rect = RegionOverlay.Select();
                if (rect == null)
                {
                    AgentEyes.Log.Info("capture: region cancelled (no file written)");
                    return;
                }
                var info = CaptureService.CaptureRegion(rect.Value, _cfg!.CaptureSaveFolder);
                CaptureSaved?.Invoke(info.File);
            }
            catch (Exception ex)
            {
                AgentEyes.Log.Error("capture region failed", ex);
                MessageBox.Show("The region capture failed.\n\n" + ex.Message, "AgentEyes");
            }
        }

        /// <summary>Full-screen snip of the given monitor (to clipboard + PNG).</summary>
        internal void CaptureFullScreenAndNotify(int screen)
        {
            try
            {
                var info = CaptureService.CaptureFullScreen(screen, _cfg!.CaptureSaveFolder);
                CaptureSaved?.Invoke(info.File);
            }
            catch (Exception ex)
            {
                AgentEyes.Log.Error("capture full-screen failed", ex);
                MessageBox.Show("The full-screen capture failed.\n\n" + ex.Message, "AgentEyes");
            }
        }

        private void ShowWindow()
        {
            if (_window == null)
            {
                _window = new MainWindow(_service!, _cfg!, ShowTests, _repair!);
                _window.Closing += (_, ev) => { ev.Cancel = true; _window!.Hide(); };  // close = hide to tray
            }
            _window.Show();
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private void ShowTests()
        {
            if (_tests == null)
            {
                _tests = new TestPanel(_service!);
                _tests.Closing += (_, ev) => { ev.Cancel = true; _tests!.Hide(); };  // keep state + any in-progress take
            }
            _tests.Show();
            _tests.WindowState = WindowState.Normal;
            _tests.Activate();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Issue #152: say so when the process is going away with work still in flight, so the
            // next log makes sense. It is not a data loss any more - the recovery pass finishes an
            // interrupted recording on the next launch - and exit is deliberately NOT blocked on a
            // transcription that can take minutes.
            if (PostRecording.IsBusy)
            {
                AgentEyes.Log.Info($"app exit: {PostRecording.WorkInFlight} post-recording job(s) still in flight; "
                    + "they will be resumed by the recovery pass on the next start");
            }
            PostRecording.WorkIdle -= UpdateChecker.OnSessionEnded;

            try { _captureRegionHook?.Dispose(); } catch { }
            try { _captureFullHook?.Dispose(); } catch { }
            try { _repair?.Dispose(); } catch { }
            try { _rest?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            // The recording HUD saves its preview choices and its position WITHOUT blocking the UI
            // thread (issue #33), so a save made moments before exit may still be in flight. Bounded
            // on purpose: the writer is allowed to be stuck in a filesystem call, and exit is not.
            try { Config.FlushPendingSave(2000); }
            catch (Exception ex)
            {
                AgentEyes.Log.Warn($"app exit: flushing the config failed - {ex.Message}");
            }
            UpdateChecker.StartPendingRestart();   // after the mutex is gone, so the new exe can take it
            AgentEyes.Log.Info("app exit");
            base.OnExit(e);
        }

        private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception, "Dispatcher");
            MessageBox.Show("Something went wrong - it has been logged and the app will keep running.\n\n"
                + e.Exception.Message + "\n\nLog: " + AgentEyes.Log.CurrentFile, "AgentEyes");
            e.Handled = true;
        }

        private static void Log(Exception? ex, string where)
        {
            AgentEyes.Log.Error($"unhandled ({where})", ex);
            try { File.WriteAllText(CrashLog, $"[{where}] {DateTime.Now:o}\n{ex}"); }
            catch { }
        }
    }
}
