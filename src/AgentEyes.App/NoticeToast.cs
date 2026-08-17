using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentEyes.App
{
    /// <summary>
    /// A plain dark notice that does not steal focus and dismisses itself. Used when
    /// the app has something the user must KNOW but should not be interrupted by - e.g.
    /// remote-desktop software intercepting keystrokes so the text could not be typed
    /// (issue #46). Bottom-right of the relevant monitor, click or hover-then-leave aware.
    /// </summary>
    internal sealed class NoticeToast : Window
    {
        private const int AutoCloseSeconds = 12;

        private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(AutoCloseSeconds) };
        private readonly ToastCorner _corner;

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        private enum ToastCorner { BottomRight, BottomLeft }

        internal static void Show(string message, System.Drawing.Rectangle? nearBounds)
            => new NoticeToast(message, nearBounds).Show();

        internal static void ShowAction(
            string message, string actionText, Action action, System.Drawing.Rectangle? nearBounds)
            => new NoticeToast(message, nearBounds, actionText, action, ToastCorner.BottomLeft).Show();

        internal static void ShowActionBottomRight(
            string message, string actionText, Action action, System.Drawing.Rectangle? nearBounds)
            => new NoticeToast(message, nearBounds, actionText, action, ToastCorner.BottomRight).Show();

        private NoticeToast(
            string message,
            System.Drawing.Rectangle? nearBounds,
            string? actionText = null,
            Action? action = null,
            ToastCorner corner = ToastCorner.BottomRight)
        {
            _corner = corner;
            Title = "AgentEyes";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI");

            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12), MaxWidth = 360 };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = Res<Brush>("RdText"),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(actionText) && action != null)
            {
                var button = new Button
                {
                    Content = actionText,
                    MinWidth = 100,
                    Height = 28,
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Style = Res<Style>("DkPrimary"),
                };
                button.Click += (_, _) => { action(); Close(); };
                panel.Children.Add(button);
            }

            Content = new Border
            {
                Background = Res<Brush>("RdSurface"),
                BorderBrush = Res<Brush>("RdRecord"),   // red edge: this is a problem, not a tip
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = panel,
            };

            MouseLeftButtonUp += (_, _) => Close();
            _autoClose.Tick += (_, _) => Close();
            MouseEnter += (_, _) => _autoClose.Stop();
            MouseLeave += (_, _) => _autoClose.Start();
            Loaded += (_, _) => { Position(nearBounds); _autoClose.Start(); };
        }

        private void Position(System.Drawing.Rectangle? nearBounds)
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            var toDevice = source!.CompositionTarget!.TransformToDevice;

            var area = nearBounds is { } b
                ? System.Windows.Forms.Screen.FromRectangle(b).WorkingArea
                : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;

            Left = _corner == ToastCorner.BottomLeft
                ? (area.Left + 16) / toDevice.M11
                : (area.Right - ActualWidth * toDevice.M11 - 16) / toDevice.M11;
            Top = (area.Bottom - ActualHeight * toDevice.M22 - 16) / toDevice.M22;
        }

        protected override void OnClosed(EventArgs e)
        {
            _autoClose.Stop();
            base.OnClosed(e);
        }
    }
}
