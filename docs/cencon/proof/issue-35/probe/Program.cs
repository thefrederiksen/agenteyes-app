using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentEyes.Proof
{
    /// <summary>
    /// Issue #35 - measures the REAL WPF layout of the preset editor and captures it.
    ///
    /// Why a harness and not the tray app: AgentEyesApp is single-instance (App.OnStartup takes the
    /// "AgentEyes-singleinstance" mutex), so a second copy cannot start while the installed app is
    /// running, and killing somebody's running recorder to look at a dialog is not acceptable. This
    /// harness loads the SAME freshly built AgentEyesApp, loads App.xaml so the dialog gets its real
    /// styles, constructs the real PresetEditor window, shows it WITHOUT stealing focus
    /// (ShowActivated = false), and reads the numbers the acceptance criteria are written against
    /// straight off the live ScrollViewer:
    ///
    ///   ComputedVerticalScrollBarVisibility, ExtentHeight, ViewportHeight, ScrollableHeight
    ///
    /// It then PrintWindow-captures the window (no foreground steal) at each requested height.
    ///
    /// WHAT THIS CANNOT SEE: the dialog is measured with no Owner, so WindowStartupLocation
    /// CenterOwner falls back to the screen. Nothing else differs from the dialog the app opens -
    /// same window type, same XAML, same App.xaml resources, same build.
    ///
    /// Every step throws on failure: an empty or missing measurement is a broken instrument, never
    /// a clean run.
    /// </summary>
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>Every x:Name the dialog carried before issue #35 (AC4) - all must still be here.</summary>
        private static readonly string[] AllNames =
        {
            "AspectBox", "CameraBox", "CameraHint", "CancelButton", "DenoiseCheck", "ErrorText",
            "ExactHeightBox", "ExactWidthBox", "FpsBox", "FullRadio", "GateCheck", "LandscapeButton",
            "LevelCheck", "MicBox", "MicVol", "MicVolText", "ModeAudio", "ModeShot", "ModeVideo",
            "MonitorBox", "NameBox", "NoteBox", "RegionLabel", "RegionOptions", "RegionRadio",
            "RegionWarn", "SaveAsButton", "SaveButton", "SelectAreaButton", "SetExactButton",
            "ShowAreaButton", "SquareButton", "SrcMic", "SrcMixed", "SrcSystem", "SysVol",
            "SysVolText", "VerticalButton",
        };

        /// <summary>Keeps the shown window alive: without Application.Run nothing else roots it.</summary>
        private static Window? _rooted;

        /// <summary>The settings AC2 names explicitly - these must be on screen simultaneously.</summary>
        private static readonly string[] Ac2Names =
        {
            "NameBox", "MonitorBox", "CameraBox", "MicBox", "MicVol", "SysVol", "FpsBox",
            "ModeShot", "ModeAudio", "ModeVideo",
        };

        [STAThread]
        private static int Main(string[] args)
        {
            string outDir = args.Length > 0
                ? args[0]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            Directory.CreateDirectory(outDir);

            // 0 means "the height the XAML asks for"; the rest are explicit small-screen heights.
            var heights = args.Length > 1
                ? args.Skip(1).Select(int.Parse).ToArray()
                : new[] { 0, 600 };

            var report = new StringBuilder();
            void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }

            var asm = Assembly.Load("AgentEyesApp");
            Type Need(string name) => asm.GetType(name)
                ?? throw new InvalidOperationException($"Type {name} is not in AgentEyesApp - wrong build?");

            // A real Application carrying the real App.xaml resources, without running OnStartup
            // (so no tray icon, no REST port, no recording service).
            var appType = Need("AgentEyes.App.App");
            var app = (Application)Activator.CreateInstance(appType, nonPublic: true)!;
            appType.GetMethod("InitializeComponent")!.Invoke(app, null);
            if (app.Resources.Count == 0)
                throw new InvalidOperationException("App.xaml resources did not load - every StaticResource would be missing.");

            var presetType = Need("AgentEyes.App.CapturePreset");
            object preset = Activator.CreateInstance(presetType, nonPublic: true)!;
            presetType.GetProperty("Name")!.SetValue(preset, "layout probe");

            var editorType = Need("AgentEyes.App.PresetEditor");
            var ctor = editorType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                                                 null, new[] { presetType }, null)
                ?? throw new InvalidOperationException("PresetEditor(CapturePreset) constructor not found.");
            var win = (Window)ctor.Invoke(new[] { preset });

            double defaultWidth = win.Width, defaultHeight = win.Height;
            win.ShowActivated = false;
            win.ShowInTaskbar = false;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = 60;
            win.Top = 40;
            _rooted = win;   // Application.Run was never called, so keep the window rooted ourselves
            win.Show();
            Settle(TimeSpan.FromSeconds(5));   // the camera list loads in the background and moves the hint text

            var sv = FindScrollViewer(win)
                ?? throw new InvalidOperationException("No ScrollViewer in the preset editor - the safety net is gone.");
            var root = (FrameworkElement)win.Content;   // the dialog's outer Grid: the client area

            Say("PRESET EDITOR LAYOUT MEASUREMENT (issue #35)");
            Say($"date         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Say($"assembly     : {asm.Location}");
            Say($"built        : {File.GetLastWriteTime(asm.Location):yyyy-MM-dd HH:mm:ss}");
            Say($"screen (DIP) : {SystemParameters.PrimaryScreenWidth:F0} x {SystemParameters.PrimaryScreenHeight:F0}");
            Say($"XAML default : Width={defaultWidth:F0} Height={defaultHeight:F0}");
            Say("");

            foreach (int h in heights)
            {
                win.Height = h > 0 ? h : defaultHeight;
                Settle(TimeSpan.FromMilliseconds(1200));

                string label = h > 0 ? $"height-{h}" : $"default-{defaultHeight:F0}";
                Say($"--- window {win.ActualWidth:F0} x {win.ActualHeight:F0} ({label}) ---");
                Say($"  ComputedVerticalScrollBarVisibility : {sv.ComputedVerticalScrollBarVisibility}");
                Say($"  ExtentHeight / ViewportHeight       : {sv.ExtentHeight:F1} / {sv.ViewportHeight:F1}");
                Say($"  ScrollableHeight                    : {sv.ScrollableHeight:F1}");

                // Two DIFFERENT buckets, kept apart on purpose: a control the dialog deliberately
                // hides (RegionWarn only appears when a region overflows its monitor) is not the same
                // fact as a control pushed off the visible area, and merging them would let a real
                // defect hide behind a by-design Collapsed.
                //
                // A control inside the ScrollViewer is judged against the VIEWPORT (what you can see
                // without scrolling); the Save/Cancel row lives outside it and is judged against the
                // client area. Judging everything against the client area would call a clipped
                // control "on screen" and is exactly the check that proves nothing.
                sv.ScrollToTop();
                Settle(TimeSpan.FromMilliseconds(250));
                var collapsed = new List<string>();
                var hiddenAtTop = Offscreen(win, sv, root, collapsed);
                sv.ScrollToBottom();
                Settle(TimeSpan.FromMilliseconds(250));
                var hiddenAtBottom = Offscreen(win, sv, root, new List<string>());
                sv.ScrollToTop();
                Settle(TimeSpan.FromMilliseconds(250));

                Say($"  named controls found in the live window : {AllNames.Length} of {AllNames.Length}");
                Say($"  hidden BY DESIGN (Visibility set in XAML): {(collapsed.Count == 0 ? "none" : string.Join(", ", collapsed))}");
                if (hiddenAtTop.Count == 0)
                    Say("  every other named control visible WITHOUT scrolling: YES");
                else
                {
                    Say($"  needs scrolling to reach ({hiddenAtTop.Count}):");
                    foreach (var kv in hiddenAtTop) Say($"    - {kv.Key} {kv.Value}");
                }
                var unreachable = hiddenAtTop.Keys.Where(k => hiddenAtBottom.ContainsKey(k)).ToList();
                Say($"  unreachable even after scrolling to the bottom: {(unreachable.Count == 0 ? "none" : string.Join(", ", unreachable))}");
                var offscreen = hiddenAtTop;

                var missingAc2 = Ac2Names.Where(n => offscreen.ContainsKey(n)).ToList();
                Say($"  AC2 controls simultaneously on screen without scrolling: {(missingAc2.Count == 0 ? "YES" : "NO -> " + string.Join(", ", missingAc2))}");

                // Two-column geometry: one control from each column, with their x positions.
                var left = (FrameworkElement)win.FindName("NameBox")!;
                var right = (FrameworkElement)win.FindName("MicBox")!;
                var lp = left.TransformToAncestor(root).Transform(new System.Windows.Point(0, 0));
                var rp = right.TransformToAncestor(root).Transform(new System.Windows.Point(0, 0));
                Say($"  columns: NameBox x={lp.X:F0} w={left.ActualWidth:F0} | MicBox x={rp.X:F0} w={right.ActualWidth:F0}"
                    + $" -> {(rp.X > lp.X + left.ActualWidth - 1 ? "SIDE BY SIDE" : "NOT two columns")}");

                string png = Path.Combine(outDir, $"preset-editor-{label}.png");
                Capture(win, png);
                Say($"  screenshot: {Path.GetFileName(png)}");
                Say("");
            }

            win.Close();
            Pump();

            File.WriteAllText(Path.Combine(outDir, "layout-measurement.txt"), report.ToString(), Encoding.ASCII);
            Console.WriteLine($"[OK] wrote {Path.Combine(outDir, "layout-measurement.txt")}");
            return 0;
        }

        /// <summary>
        /// The named controls that are NOT fully visible right now, with where they sit. Controls
        /// inside the ScrollViewer are measured against its viewport; the rest against the client
        /// area. Collapsed controls are moved into <paramref name="collapsed"/> instead.
        /// </summary>
        private static Dictionary<string, string> Offscreen(Window win, ScrollViewer sv, FrameworkElement root,
                                                            List<string> collapsed)
        {
            var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string n in AllNames)
            {
                var c = win.FindName(n) as FrameworkElement
                    ?? throw new InvalidOperationException($"Control {n} is missing from the live window.");
                if (!c.IsVisible)
                {
                    if (c.Visibility == Visibility.Visible) hidden[n] = "(Visibility=Visible but not rendered)";
                    else collapsed.Add($"{n} ({c.Visibility})");
                    continue;
                }
                bool inScroller = IsDescendantOf(c, sv);
                FrameworkElement frame = inScroller ? sv : root;
                double limit = inScroller ? sv.ViewportHeight : root.ActualHeight;
                var p = c.TransformToAncestor(frame).Transform(new System.Windows.Point(0, 0));
                double bottom = p.Y + c.ActualHeight;
                if (p.Y < -0.5 || bottom > limit + 0.5)
                    hidden[n] = $"(y {p.Y:F0}..{bottom:F0} vs {(inScroller ? "viewport" : "client")} {limit:F0})";
            }
            return hidden;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
        {
            for (var p = VisualTreeHelper.GetParent(child); p != null; p = VisualTreeHelper.GetParent(p))
                if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject node)
        {
            if (node is ScrollViewer sv) return sv;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                var hit = FindScrollViewer(VisualTreeHelper.GetChild(node, i));
                if (hit != null) return hit;
            }
            return null;
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static void Settle(TimeSpan span)
        {
            var end = DateTime.UtcNow + span;
            while (DateTime.UtcNow < end) { Pump(); System.Threading.Thread.Sleep(40); }
        }

        private static void Capture(Window win, string path)
        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("The window has no HWND - nothing to capture.");
            if (!GetWindowRect(hwnd, out RECT r)) throw new InvalidOperationException("GetWindowRect failed.");
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) throw new InvalidOperationException($"Window rect is empty ({w}x{h}).");

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, 2);   // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
                if (!ok) throw new InvalidOperationException("PrintWindow failed.");
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
