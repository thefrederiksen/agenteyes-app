using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Floating recording HUD (issue #20): small always-on-top pill shown only while
    /// recording. Pulsing red dot, elapsed timer, live mic/system meters, Stop and
    /// Discard. Draggable; position persists in Config. Never steals focus from the
    /// app being recorded (WS_EX_NOACTIVATE) and asks Windows to exclude it from
    /// screen capture (WDA_EXCLUDEFROMCAPTURE, Win10 2004+).
    /// </summary>
    internal sealed class HudWindow : Window
    {
        private readonly RecordingService _svc;
        private readonly Config _cfg;
        private readonly Func<Task> _stop;
        private readonly Func<Task> _discard;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

        private readonly Ellipse _dot;
        private readonly TextBlock _elapsed;
        private readonly ProgressBar _mic;
        private readonly ProgressBar _sys;
        private readonly Button _stopButton;
        private readonly Button _discardButton;
        private bool _busy;
        private bool _finishing;   // showing a "Saving..."/processing label; the stop flow owns the close

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal HudWindow(RecordingService svc, Config cfg, Func<Task> stop, Func<Task> discard)
        {
            _svc = svc; _cfg = cfg; _stop = stop; _discard = discard;

            Title = "Recording HUD";   // no visible chrome; the name serves UI Automation
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");

            // ---- layout: [dot] 04:27 [meters] | [stop] [discard] ----
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 12, 10) };

            _dot = new Ellipse { Width = 10, Height = 10, Fill = Res<Brush>("RdRecord"), VerticalAlignment = VerticalAlignment.Center };
            var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _dot.BeginAnimation(OpacityProperty, pulse);
            row.Children.Add(_dot);

            _elapsed = new TextBlock
            {
                Text = "00:00",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 17, FontWeight = FontWeights.Bold,
                Foreground = Res<Brush>("RdText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 12, 0),
            };
            row.Children.Add(_elapsed);

            var meters = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _mic = MiniMeter();
            _sys = MiniMeter();
            _sys.Margin = new Thickness(0, 3, 0, 0);
            meters.Children.Add(_mic);
            meters.Children.Add(_sys);
            row.Children.Add(meters);

            _stopButton = new Button
            {
                Style = Res<Style>("DkRecord"),
                Width = 64, Height = 30, FontSize = 12,
                Content = "STOP",
                ToolTip = "Stop recording",
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationName(_stopButton, "HUD stop");
            _stopButton.Click += async (_, _) => await RunOnce(_stop, "Saving...");
            row.Children.Add(_stopButton);

            _discardButton = new Button
            {
                Style = Res<Style>("DkIcon"),
                Height = 30,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Discard recording (stop and delete)",
                VerticalAlignment = VerticalAlignment.Center,
                Content = new Path
                {
                    // Trash can
                    Data = Geometry.Parse("M3,5 h12 M7,5 v-2 h4 v2 M5,5 l1,11 h6 l1,-11"),
                    Stroke = Res<Brush>("DkText"), StrokeThickness = 1.4,
                    Width = 14, Height = 14, Stretch = Stretch.Uniform,
                },
            };
            AutomationName(_discardButton, "HUD discard");
            _discardButton.Click += async (_, _) => await ConfirmDiscard();
            row.Children.Add(_discardButton);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x26, 0x2B)),
                BorderBrush = Res<Brush>("RdStroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = row,
            };

            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* click without drag */ } };
            Loaded += (_, _) => Position();
            SourceInitialized += (_, _) => ApplyWindowStyles();
            Closed += (_, _) => { _timer.Stop(); SavePosition(); };

            _timer.Tick += (_, _) => OnTick();
            _timer.Start();
        }

        private static ProgressBar MiniMeter() => new()
        {
            Width = 46, Height = 4, Minimum = 0, Maximum = 100,
            BorderThickness = new Thickness(0),
            Background = Res<Brush>("RdStroke"),
            Foreground = Res<Brush>("RdAccent"),
        };

        private static void AutomationName(UIElement e, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(e, name);

        private void OnTick()
        {
            // Once we show a Saving.../processing label, the stop flow owns the close - don't
            // overwrite the label or auto-close from here.
            if (_finishing) return;
            if (!_svc.IsRecording) { Close(); return; }   // ended elsewhere (main window, tray, REST)
            var t = _svc.Elapsed;
            _elapsed.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
            _mic.Value = Math.Min(100, _svc.MicLevel * 180);
            _sys.Value = Math.Min(100, _svc.SystemLevel * 180);
        }

        private async Task RunOnce(Func<Task> action, string busyLabel)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(busyLabel);   // immediate feedback - never a silent disabled button
            try { await action(); }
            finally { _busy = false; }
        }

        /// <summary>
        /// Switch the HUD to a processing state (a staged "Saving video..."/"Saving audio..."
        /// label while the raw files flush). Recording is over: stop the pulse, drop the
        /// meters and buttons, show the label. Once shown the HUD stays put until the stop
        /// flow closes it. Safe to call repeatedly and from the stop path (not just the button).
        /// </summary>
        public void SetStatus(string label)
        {
            Log.Info($"hud: status -> {label}");   // staged save sequence is visible in FileLog (issue #77 AC3)
            _finishing = true;
            _dot.BeginAnimation(OpacityProperty, null);   // stop the recording pulse
            _dot.Opacity = 1.0;
            _dot.Fill = Res<Brush>("RdAccent");           // blue = processing, not recording
            _mic.Visibility = Visibility.Collapsed;
            _sys.Visibility = Visibility.Collapsed;
            _stopButton.Visibility = Visibility.Collapsed;
            _discardButton.Visibility = Visibility.Collapsed;
            _elapsed.Margin = new Thickness(9, 0, 4, 0);
            _elapsed.Text = label;
        }

        private async Task ConfirmDiscard()
        {
            Log.Info("hud: discard clicked");
            if (_busy) return;
            if (MessageBox.Show("Discard this recording? Its files will be deleted.",
                    "AgentEyes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await RunOnce(_discard, "Discarding...");
        }

        // ---- placement ------------------------------------------------------

        private void Position()
        {
            double w = ActualWidth, h = ActualHeight;
            var area = SystemParameters.WorkArea;
            double left = _cfg.HudLeft ?? area.Right - w - 16;
            double top = _cfg.HudTop ?? area.Top + 16;
            // Clamp into the virtual screen so a stale saved position cannot strand it.
            left = Math.Max(SystemParameters.VirtualScreenLeft,
                Math.Min(left, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - w));
            top = Math.Max(SystemParameters.VirtualScreenTop,
                Math.Min(top, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - h));
            Left = left; Top = top;
        }

        private void SavePosition()
        {
            _cfg.HudLeft = Left;
            _cfg.HudTop = Top;
            _cfg.Save();
        }

        // ---- window styles ----------------------------------------------------

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern long GetWindowLongPtr(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private void ApplyWindowStyles()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                // No focus stealing from the app being recorded; no Alt-Tab entry.
                SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                    GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                // Leave the HUD out of screen captures (verified working with the
                // ffmpeg recorder). MQS_HUD_CAPTURABLE=1 opts out - for demos and
                // docs that deliberately want the HUD visible in the capture.
                if (Environment.GetEnvironmentVariable("MQS_HUD_CAPTURABLE") != "1")
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
            catch (Exception ex) { Log.Error("hud window styles", ex); }
        }
    }
}
