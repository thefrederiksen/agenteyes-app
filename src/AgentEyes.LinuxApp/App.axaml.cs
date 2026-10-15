using System;
using System.Linq;
using AgentEyes.LinuxApp.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AgentEyes.LinuxApp
{
    public partial class App : Application
    {
        private MainWindow? _window;
        private TrayController? _tray;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            Log.Info("[App] OnFrameworkInitializationCompleted");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _tray = new TrayController(ShowWindow, () => Quit(desktop));
                AppState.Current.StartPolling();
                Program.Instance!.CommandReceived += cmd => Dispatcher.UIThread.Post(() => Run(cmd));
                if (!Program.StartInTray) Run(Program.StartCommand);
                desktop.Exit += (_, _) => _tray?.Dispose();
            }
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Show the window, creating it on first use; bring it forward when it exists.</summary>
        public void ShowWindow()
        {
            Log.Info("[App] ShowWindow");
            if (_window == null)
            {
                _window = new MainWindow();
                // Closing the window hides it to the tray, like the Windows app; Quit is in the tray menu.
                _window.Closing += (_, e) =>
                {
                    if (_quitting) return;
                    e.Cancel = true;
                    _window.Hide();
                    Log.Info("[App] window closed to the tray");
                };
            }
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        /// <summary>Carry out a launch command (see Program): show the window, then the view or window asked for.</summary>
        private void Run(string command)
        {
            Log.Info($"[App] Run: {command}");
            if (command == "tray") return;
            if (command.StartsWith("snap:", StringComparison.Ordinal))
            {
                SnapWindows(command[5..]);
                return;
            }
            ShowWindow();
            int colon = command.IndexOf(':');
            if (colon < 0) return;
            string verb = command[..colon], arg = command[(colon + 1)..];
            _window!.RunCommand(verb, arg);
        }

        /// <summary>
        /// Proof without stealing focus (issue #107): render every open AgentEyes window to
        /// DIR/&lt;title&gt;.png from the app itself, whether or not GNOME has it on top.
        /// </summary>
        private void SnapWindows(string dir)
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
            System.IO.Directory.CreateDirectory(dir);
            foreach (var w in desktop.Windows)
            {
                if (!w.IsVisible) continue;
                var size = new PixelSize((int)w.ClientSize.Width, (int)w.ClientSize.Height);
                if (size.Width <= 0 || size.Height <= 0) continue;
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bmp.Render(w);
                string name = string.Concat((w.Title ?? "window").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                string file = System.IO.Path.Combine(dir, name + ".png");
                bmp.Save(file);
                Log.Info($"[App] SnapWindows: {file}");
            }
        }

        private bool _quitting;

        private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Info("[App] Quit (the recorder service keeps running)");
            _quitting = true;
            _window?.Close();
            desktop.Shutdown();
        }
    }
}
