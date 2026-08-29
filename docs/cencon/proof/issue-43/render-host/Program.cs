using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shot
{
    // Issue #43 proof host. Builds the PresetEditor dialog OFFSCREEN - it is never Show()n, never
    // brought to the foreground, and the camera list is never loaded (that happens on Window.Loaded,
    // which an unshown window does not raise), so no camera device and no ffmpeg is ever opened.
    //
    // ROUND 2: the dialog is laid out at its CLIENT size, not at its window size. Round 1 rendered
    // the content at the full 1000x760 - which is 16x39 px more room than the window really has -
    // and still produced a vertical scrollbar down the Camera tab. The frame is measured here the
    // same way tests\AgentEyes.Tests\PresetEditorFitsWithoutScrollingTests.cs measures it, so a shot
    // and the suite cannot disagree about what "the default size" means. Each shot also prints what
    // the Camera tab's ScrollViewer decided, so the absence of a scrollbar is a stated measurement
    // rather than something a reader has to spot in a picture.
    internal static class Program
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(outDir);

            var app = new AgentEyes.App.App();
            app.InitializeComponent();          // App.xaml resources only - OnStartup never runs
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var cfg = new AgentEyes.App.Config();
            var preset = new AgentEyes.App.CapturePreset { Name = "Issue 43 proof", Mode = "video" };
            var win = new AgentEyes.App.PresetEditor(preset, cfg);

            var tabs = (TabControl)win.FindName("EditorTabs");
            tabs.SelectedItem = win.FindName("CameraTab");

            var root = (FrameworkElement)win.Content;
            var scroll = (ScrollViewer)win.FindName("CameraScroll");
            var inset = (Slider)win.FindName("InsetSizeSlider");
            var diameter = (Slider)win.FindName("CircleSizeSlider");
            var corner = (ComboBox)win.FindName("OverlayCornerBox");
            var caption = (TextBlock)win.FindName("InsetSchematicCaption");
            var schematic = (FrameworkElement)win.FindName("InsetSchematicBorder");
            var path = (System.Windows.Shapes.Path)win.FindName("InsetSchematicInsetPath");

            Size chrome = WindowChrome();
            double w = win.Width - chrome.Width, h = win.Height - chrome.Height;
            Console.WriteLine($"window {win.Width}x{win.Height}, frame {chrome.Width}x{chrome.Height}, "
                              + $"client {w}x{h}");

            Layout(root, w, h);

            void Shot(string name)
            {
                Layout(root, w, h);
                Render(root, w, h, Path.Combine(outDir, name + ".png"));
                var b = path.Data.Bounds;
                Console.WriteLine($"{name}: inset x={b.X:F1} y={b.Y:F1} w={b.Width:F1} h={b.Height:F1} "
                                  + $"area={b.Width * b.Height:F0} | box {schematic.ActualWidth}x{schematic.ActualHeight} "
                                  + $"| scrollbar={scroll.ComputedVerticalScrollBarVisibility} "
                                  + $"(content {scroll.ExtentHeight:F0} px in a {scroll.ViewportHeight:F0} px viewport) "
                                  + $"| \"{caption.Text}\"");
            }

            // AC1 - the two ends of "Size on screen".
            inset.Value = 0.15; Shot("ac1-size-15");
            inset.Value = 0.60; Shot("ac1-size-60");

            // AC2 - each corner, at one size.
            inset.Value = 0.30;
            for (int i = 0; i < 4; i++)
            {
                corner.SelectedIndex = i;
                Shot("ac2-corner-" + ((ComboBoxItem)corner.SelectedItem).Content.ToString()
                        .ToLowerInvariant().Replace(' ', '-'));
            }

            // AC3 - a small crop of the camera picture, sitting large on the recording, and the
            // reverse. Two sliders, two visibly different things.
            corner.SelectedIndex = 0;
            diameter.Value = 0.20; inset.Value = 0.55; Shot("ac3-small-crop-large-on-screen");
            diameter.Value = 0.95; inset.Value = 0.15; Shot("ac3-large-crop-small-on-screen");

            // Issue #35 AC3 - no scrollbar on ANY tab, not only the one this issue changed. The
            // other two are wider now too, so they are shown rather than assumed.
            foreach (var (tab, scroller, name) in new[]
                     {
                         ("CaptureTab", "CaptureScroll", "layout-capture-tab"),
                         ("AudioTab", "AudioScroll", "layout-audio-tab"),
                     })
            {
                tabs.SelectedItem = win.FindName(tab);
                var s = (ScrollViewer)win.FindName(scroller);
                Layout(root, w, h);
                Render(root, w, h, Path.Combine(outDir, name + ".png"));
                Console.WriteLine($"{name}: scrollbar={s.ComputedVerticalScrollBarVisibility} "
                                  + $"(content {s.ExtentHeight:F0} px in a {s.ViewportHeight:F0} px viewport)");
            }

            return 0;
        }

        /// <summary>
        /// How much of a window is frame rather than content on this machine - measured, not named,
        /// because a title bar and a resize border differ with DPI and with the Windows version. A
        /// plain window in the editor's own shape is shown far off-screen and asked how big its
        /// content ended up.
        /// </summary>
        private static Size WindowChrome()
        {
            var content = new Grid();
            var probe = new Window
            {
                Title = "chrome probe",
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -8000,
                Top = -8000,
                Width = 1000,
                Height = 760,
                Content = content,
            };
            try
            {
                probe.Show();
                probe.UpdateLayout();
                return new Size(probe.ActualWidth - content.ActualWidth,
                                probe.ActualHeight - content.ActualHeight);
            }
            finally { probe.Close(); }
        }

        private static void Layout(FrameworkElement root, double w, double h)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
        }

        private static void Render(FrameworkElement root, double w, double h, string file)
        {
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x22)), null, new Rect(0, 0, w, h));

            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(background);
            bmp.Render(root);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(file);
            png.Save(fs);
        }
    }
}
