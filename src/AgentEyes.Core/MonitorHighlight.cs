using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfControls = System.Windows.Controls;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// Transient, click-through highlight that flashes a colored frame over the monitor (or region)
    /// about to be recorded, so the user can see which screen is the capture target. It auto-closes
    /// after a short delay, never steals focus, and passes mouse/keyboard input straight through to
    /// whatever is underneath. Single global DPI scale is assumed (same as RegionOverlay).
    /// </summary>
    internal sealed class MonitorHighlight : Window
    {
        private readonly Drawing.Rectangle _deviceRect;
        private readonly DispatcherTimer _timer;

        private MonitorHighlight(Drawing.Rectangle deviceRect, string label, int milliseconds)
        {
            _deviceRect = deviceRect;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;                 // do not steal focus from the main window
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = deviceRect.X; Top = deviceRect.Y; Width = 1; Height = 1;

            var accent = Color.FromRgb(0xD6, 0x9E, 0x2E);   // gold (matches the app theme)
            var frame = new WpfControls.Border
            {
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(6),
                Background = new SolidColorBrush(Color.FromArgb(28, 0xD6, 0x9E, 0x2E)),
                Child = new WpfControls.Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(220, 0x1A, 0x36, 0x5D)),  // navy pill
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(18, 9, 18, 9),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new WpfControls.TextBlock
                    {
                        Text = label,
                        Foreground = Brushes.White,
                        FontSize = 22,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI"),
                    },
                },
            };
            Content = frame;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            _timer.Tick += (_, _) => Close();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Make the window click-through and non-activating so it is purely a visual indicator.
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            // Monitor bounds are device pixels; WPF positions in DIPs. Convert with the window scale.
            var source = PresentationSource.FromVisual(this);
            double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            Left = _deviceRect.X / sx;
            Top = _deviceRect.Y / sy;
            Width = _deviceRect.Width / sx;
            Height = _deviceRect.Height / sy;

            _timer.Start();
        }

        protected override void OnClosed(EventArgs e) { _timer.Stop(); base.OnClosed(e); }

        /// <summary>
        /// Flash a highlight over the given device-pixel rectangle. Returns the window so the caller
        /// can close it early (e.g. when recording starts). Auto-closes after <paramref name="milliseconds"/>.
        /// </summary>
        public static MonitorHighlight Flash(Drawing.Rectangle deviceRect, string label, int milliseconds = 2500)
        {
            var w = new MonitorHighlight(deviceRect, label, milliseconds);
            w.Show();
            return w;
        }

        // ---- interop ------------------------------------------------------
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
