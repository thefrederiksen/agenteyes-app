using System;
using WpfApp = System.Windows.Application;
using Wpf = System.Windows;
using WpfMedia = System.Windows.Media;
using WpfShapes = System.Windows.Shapes;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

namespace AgentEyes
{
    /// <summary>
    /// Always-on-top transparent overlay spanning the virtual desktop. The user drags a
    /// rectangle; the selected region is returned in virtual-desktop DEVICE pixels (ready
    /// for Screenshot.CaptureRect / the video engine).
    ///
    /// SystemParameters.VirtualScreen* are in DIPs; we convert the final rectangle to device
    /// pixels using the window's device transform.
    /// TODO Phase 1: validate on mixed-DPI monitors (single global scale is assumed here).
    /// </summary>
    internal sealed class RegionOverlay : Wpf.Window
    {
        private readonly WpfControls.Canvas _canvas;
        private readonly WpfShapes.Rectangle _selection;
        private readonly WpfControls.TextBlock _readout;
        private readonly RegionMath.AspectLock _aspect;
        private Wpf.Point _startDip;
        private bool _dragging;

        private RegionOverlay(RegionMath.AspectLock aspect)
        {
            _aspect = aspect;

            WindowStyle = Wpf.WindowStyle.None;
            ResizeMode = Wpf.ResizeMode.NoResize;
            AllowsTransparency = true;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(60, 0, 0, 0));
            Cursor = WpfInput.Cursors.Cross;

            // Cover the whole virtual desktop (all monitors), in DIPs.
            Left = Wpf.SystemParameters.VirtualScreenLeft;
            Top = Wpf.SystemParameters.VirtualScreenTop;
            Width = Wpf.SystemParameters.VirtualScreenWidth;
            Height = Wpf.SystemParameters.VirtualScreenHeight;

            _canvas = new WpfControls.Canvas();
            _selection = new WpfShapes.Rectangle
            {
                Stroke = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xD6, 0x9E, 0x2E)),
                StrokeThickness = 2,
                Fill = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(40, 0xD6, 0x9E, 0x2E)),
                Visibility = Wpf.Visibility.Collapsed,
            };
            _canvas.Children.Add(_selection);

            // Live dimension readout in DEVICE pixels, following the selection while dragging.
            _readout = new WpfControls.TextBlock
            {
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Colors.White),
                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(180, 0, 0, 0)),
                FontFamily = new WpfMedia.FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Wpf.Thickness(6, 3, 6, 3),
                Visibility = Wpf.Visibility.Collapsed,
            };
            _canvas.Children.Add(_readout);
            Content = _canvas;

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            KeyDown += (_, e) => { if (e.Key == WpfInput.Key.Escape) { Result = null; Close(); } };
        }

        /// <summary>The selected rectangle in device pixels, or null if cancelled.</summary>
        public System.Drawing.Rectangle? Result { get; private set; }

        /// <summary>
        /// Show the overlay modally on its own STA message loop and return the selection.
        /// Safe to call from the console main thread. Freehand drag (no aspect lock).
        /// </summary>
        public static System.Drawing.Rectangle? Select() => Select(RegionMath.AspectLock.Free);

        /// <summary>
        /// Show the overlay modally and return the selection, constraining the drag to
        /// <paramref name="aspect"/> (use <see cref="RegionMath.AspectLock.Free"/> for freehand).
        /// </summary>
        public static System.Drawing.Rectangle? Select(RegionMath.AspectLock aspect)
        {
            System.Drawing.Rectangle? result = null;

            // Ensure a WPF Application exists for this process.
            if (WpfApp.Current == null)
            {
                _ = new WpfApp { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
            }

            var overlay = new RegionOverlay(aspect);
            overlay.ShowDialog();
            result = overlay.Result;
            return result;
        }

        private void OnDown(object sender, WpfInput.MouseButtonEventArgs e)
        {
            _dragging = true;
            _startDip = e.GetPosition(_canvas);
            WpfControls.Canvas.SetLeft(_selection, _startDip.X);
            WpfControls.Canvas.SetTop(_selection, _startDip.Y);
            _selection.Width = 0;
            _selection.Height = 0;
            _selection.Visibility = Wpf.Visibility.Visible;
        }

        private void OnMove(object sender, WpfInput.MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(_canvas);

            // Constrain the drag to the aspect lock (Free returns the raw drag). The anchor is the
            // mouse-down point; the snapped rectangle is expressed in the same canvas-local DIPs.
            var snapped = RegionMath.SnapDragToAspect(_startDip.X, _startDip.Y, p.X, p.Y, _aspect);

            WpfControls.Canvas.SetLeft(_selection, snapped.X);
            WpfControls.Canvas.SetTop(_selection, snapped.Y);
            _selection.Width = snapped.Width;
            _selection.Height = snapped.Height;

            (double scaleX, double scaleY) = DeviceScale();
            int pw = (int)Math.Round(snapped.Width * scaleX);
            int ph = (int)Math.Round(snapped.Height * scaleY);
            _readout.Text = $"{pw} x {ph}";
            WpfControls.Canvas.SetLeft(_readout, snapped.X);
            WpfControls.Canvas.SetTop(_readout, Math.Max(0, snapped.Y - 24));
            _readout.Visibility = Wpf.Visibility.Visible;
        }

        private void OnUp(object sender, WpfInput.MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;

            var end = e.GetPosition(_canvas);
            var snapped = RegionMath.SnapDragToAspect(_startDip.X, _startDip.Y, end.X, end.Y, _aspect);

            // DIP (canvas-local) -> absolute DIP -> device pixels.
            double absX = Left + snapped.X;
            double absY = Top + snapped.Y;

            (double scaleX, double scaleY) = DeviceScale();
            int px = (int)Math.Round(absX * scaleX);
            int py = (int)Math.Round(absY * scaleY);
            int pw = (int)Math.Round(snapped.Width * scaleX);
            int ph = (int)Math.Round(snapped.Height * scaleY);

            Result = (pw > 0 && ph > 0) ? new System.Drawing.Rectangle(px, py, pw, ph) : null;
            Close();
        }

        private (double scaleX, double scaleY) DeviceScale()
        {
            var source = Wpf.PresentationSource.FromVisual(this);
            double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            return (scaleX, scaleY);
        }
    }
}
