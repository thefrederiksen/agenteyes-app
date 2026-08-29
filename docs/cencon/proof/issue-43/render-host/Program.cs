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
    internal static class Program
    {
        private const double W = 1000, H = 760;

        [STAThread]
        internal static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(outDir);

            var app = new AgentEyes.App.App();
            app.InitializeComponent();          // App.xaml resources only - OnStartup never runs

            var cfg = new AgentEyes.App.Config();
            var preset = new AgentEyes.App.CapturePreset { Name = "Issue 43 proof", Mode = "video" };
            var win = new AgentEyes.App.PresetEditor(preset, cfg);

            var tabs = (TabControl)win.FindName("EditorTabs");
            tabs.SelectedItem = win.FindName("CameraTab");

            var root = (FrameworkElement)win.Content;
            var inset = (Slider)win.FindName("InsetSizeSlider");
            var diameter = (Slider)win.FindName("CircleSizeSlider");
            var corner = (ComboBox)win.FindName("OverlayCornerBox");
            var caption = (TextBlock)win.FindName("InsetSchematicCaption");
            var schematic = (FrameworkElement)win.FindName("InsetSchematicBorder");
            var path = (System.Windows.Shapes.Path)win.FindName("InsetSchematicInsetPath");

            Layout(root);

            void Shot(string name)
            {
                Layout(root);
                Render(root, Path.Combine(outDir, name + ".png"));
                var b = path.Data.Bounds;
                Console.WriteLine($"{name}: inset x={b.X:F1} y={b.Y:F1} w={b.Width:F1} h={b.Height:F1} "
                                  + $"area={b.Width * b.Height:F0} | box {schematic.ActualWidth}x{schematic.ActualHeight} "
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

            return 0;
        }

        private static void Layout(FrameworkElement root)
        {
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
        }

        private static void Render(FrameworkElement root, string file)
        {
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x22)), null, new Rect(0, 0, W, H));

            var bmp = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(background);
            bmp.Render(root);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(file);
            png.Save(fs);
        }
    }
}
