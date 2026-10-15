using System;
using System.IO;
using AgentEyes.LinuxApp.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AgentEyes.LinuxApp
{
    /// <summary>
    /// The top-bar icon (issue #107), owned by the app like the Windows TrayHost: a moon when idle, a
    /// red dot while recording, crossed out when the recorder service is not answering. GNOME shows it
    /// through Ubuntu's AppIndicator extension (Avalonia speaks StatusNotifierItem over D-Bus).
    /// </summary>
    internal sealed class TrayController : IDisposable
    {
        private readonly TrayIcon _tray;
        private readonly NativeMenuItem _status = new("Connecting to the recorder ...") { IsEnabled = false };
        private readonly NativeMenuItem _startStop = new("Start recording");
        private readonly WindowIcon _idle = Draw(DrawMoon(Color.Parse("#ECECF2")));
        private readonly WindowIcon _recording = Draw(DrawDot());
        private readonly WindowIcon _offline = Draw(DrawMoon(Color.Parse("#8A8A93"), crossed: true));
        private string _shown = "";

        public TrayController(Action showWindow, Action quit)
        {
            Log.Info("[TrayController] ctor");
            var menu = new NativeMenu();
            menu.Add(_status);
            menu.Add(new NativeMenuItemSeparator());
            _startStop.Click += async (_, _) =>
            {
                var st = AppState.Current;
                if (st.IsRecording) await st.StopRecordingAsync();
                else await st.StartRecordingAsync();
            };
            menu.Add(_startStop);
            var show = new NativeMenuItem("Show AgentEyes");
            show.Click += (_, _) => showWindow();
            menu.Add(show);
            var folder = new NativeMenuItem("Open recordings folder");
            folder.Click += (_, _) => OpenRecordingsFolder();
            menu.Add(folder);
            menu.Add(new NativeMenuItemSeparator());
            var quitItem = new NativeMenuItem("Quit AgentEyes (the recorder keeps running)");
            quitItem.Click += (_, _) => quit();
            menu.Add(quitItem);

            _tray = new TrayIcon { Icon = _offline, ToolTipText = "AgentEyes", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => showWindow();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
            AppState.Current.Changed += Render;
        }

        internal static void OpenRecordingsFolder()
        {
            try
            {
                Directory.CreateDirectory(RecordingPaths.Root);
                Shell.Open(RecordingPaths.Root);
            }
            catch (Exception ex)
            {
                Log.Error("[TrayController] OpenRecordingsFolder FAILED", ex);
            }
        }

        private void Render()
        {
            var st = AppState.Current;
            string state = st.Status == null ? "offline" : st.IsRecording ? "recording" : "idle";
            _status.Header = st.Status == null ? "Recorder service is not running"
                : st.IsRecording ? $"Recording  {Elapsed(st.Status.ElapsedSeconds)}"
                : "Ready to record";
            _startStop.Header = st.IsRecording ? "Stop recording" : "Start recording";
            _startStop.IsEnabled = st.Status != null && !st.Busy;
            if (state == _shown) return;
            Log.Info($"[TrayController] Render: {_shown} -> {state}");
            _shown = state;
            _tray.Icon = state switch { "recording" => _recording, "idle" => _idle, _ => _offline };
            _tray.ToolTipText = state == "recording" ? "AgentEyes - recording" : "AgentEyes";
        }

        internal static string Elapsed(double seconds)
        {
            int s = (int)seconds;
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}" : $"{s / 60:D2}:{s % 60:D2}";
        }

        // ---- icons, drawn once at start (no image files to keep in step with the SVG sources) ----

        private static Action<DrawingContext> DrawMoon(Color color, bool crossed = false) => dc =>
        {
            var brush = new SolidColorBrush(color);
            var moon = Geometry.Parse("M21 7 A15 15 0 1 0 37 26 A12 12 0 1 1 21 7 Z");
            dc.DrawGeometry(brush, null, moon);
            dc.DrawEllipse(brush, null, new Point(34, 10), 3.2, 3.2);
            if (crossed) dc.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#E5A52D")), 4, lineCap: PenLineCap.Round), new Point(8, 36), new Point(36, 8));
        };

        private static Action<DrawingContext> DrawDot() => dc =>
        {
            var red = Color.Parse("#E5322D");
            dc.DrawEllipse(new SolidColorBrush(red), null, new Point(22, 22), 15, 15);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(red, 0.55), 2.4), new Point(22, 22), 19, 19);
        };

        private static WindowIcon Draw(Action<DrawingContext> paint)
        {
            using var bmp = new RenderTargetBitmap(new PixelSize(44, 44));
            using (var dc = bmp.CreateDrawingContext()) paint(dc);
            var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }

        public void Dispose()
        {
            AppState.Current.Changed -= Render;
            _tray.Dispose();
        }
    }
}
