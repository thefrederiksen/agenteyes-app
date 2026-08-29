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
    /// Issue #35 - measures the REAL WPF layout of the tabbed preset editor, captures every tab, and
    /// round-trips the remembered tab/size/position through the real config file.
    ///
    /// Why a harness and not the tray app: AgentEyesApp is single-instance (App.OnStartup takes the
    /// "AgentEyes-singleinstance" mutex), so a second copy cannot start while the installed app is
    /// running, and killing somebody's running recorder to look at a dialog is not acceptable. This
    /// harness loads the SAME freshly built AgentEyesApp, loads App.xaml so the dialog gets its real
    /// styles, constructs the real PresetEditor window, shows it WITHOUT stealing focus
    /// (ShowActivated = false), and reads the numbers the acceptance criteria are written against
    /// straight off the live ScrollViewer of each tab:
    ///
    ///   ComputedVerticalScrollBarVisibility, ExtentHeight, ViewportHeight, ScrollableHeight
    ///
    /// It then PrintWindow-captures the window (no foreground steal) on every tab.
    ///
    /// The user's %LOCALAPPDATA%\AgentEyes\config.json is BACKED UP before the run and restored in a
    /// finally block, because proving AC10 means actually writing the remembered state.
    ///
    /// WHAT THIS CANNOT SEE:
    ///  * The dialog is measured with no Owner, so WindowStartupLocation CenterOwner falls back to
    ///    the screen. Nothing else differs from the dialog the app opens.
    ///  * It does not open a camera. Whether the preview releases the device is CameraPreviewTests'
    ///    and the running-app check's job, not this harness's.
    ///
    /// Every step throws on failure: an empty or missing measurement is a broken instrument, never a
    /// clean run.
    /// </summary>
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>Every x:Name the dialog carried before issue #35, plus issue #29's preview parts.</summary>
        private static readonly string[] AllNames =
        {
            "AspectBox", "CameraBox", "CameraHint", "CancelButton", "DenoiseCheck", "ErrorText",
            "ExactHeightBox", "ExactWidthBox", "FpsBox", "FullRadio", "GateCheck", "LandscapeButton",
            "LevelCheck", "MicBox", "MicVol", "MicVolText", "ModeAudio", "ModeShot", "ModeVideo",
            "MonitorBox", "NameBox", "NoteBox", "RegionLabel", "RegionOptions", "RegionRadio",
            "RegionWarn", "SaveAsButton", "SaveButton", "SelectAreaButton", "SetExactButton",
            "ShowAreaButton", "SquareButton", "SrcMic", "SrcMixed", "SrcSystem", "SysVol",
            "SysVolText", "VerticalButton",
            "CameraPreviewPanel", "CameraPreviewImage", "CameraPreviewStatus",
        };

        /// <summary>Keeps the shown window alive: without Application.Run nothing else roots it.</summary>
        private static readonly List<Window> Rooted = new();

        private static Assembly _asm = null!;
        private static readonly StringBuilder Report = new();

        private static void Say(string line) { Console.WriteLine(line); Report.AppendLine(line); }

        [STAThread]
        private static int Main(string[] args)
        {
            string outDir = args.Length > 0
                ? args[0]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            Directory.CreateDirectory(outDir);

            _asm = Assembly.Load("AgentEyesApp");

            // A real Application carrying the real App.xaml resources, without running OnStartup
            // (so no tray icon, no REST port, no recording service).
            var appType = Need("AgentEyes.App.App");
            var app = (Application)Activator.CreateInstance(appType, nonPublic: true)!;
            appType.GetMethod("InitializeComponent")!.Invoke(app, null);
            if (app.Resources.Count == 0)
                throw new InvalidOperationException("App.xaml resources did not load - every StaticResource would be missing.");

            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentEyes", "config.json");
            string backup = configPath + ".issue35-probe-bak";
            bool hadConfig = File.Exists(configPath);
            if (hadConfig) File.Copy(configPath, backup, overwrite: true);

            try
            {
                Say("PRESET EDITOR TABBED LAYOUT MEASUREMENT (issue #35)");
                Say($"date         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Say($"assembly     : {_asm.Location}");
                Say($"built        : {File.GetLastWriteTime(_asm.Location):yyyy-MM-dd HH:mm:ss}");
                Say($"screen (DIP) : {SystemParameters.PrimaryScreenWidth:F0} x {SystemParameters.PrimaryScreenHeight:F0}");
                Say("");

                MeasureLayout(outDir);
                ProveLivePreviewAndRelease(outDir);
                ProveRememberedState(configPath);
                ProveSettingsRoundTrip();
            }
            finally
            {
                if (hadConfig) File.Copy(backup, configPath, overwrite: true);
                else File.Delete(configPath);
                if (File.Exists(backup)) File.Delete(backup);
                Console.WriteLine("[OK] config.json restored");
            }

            File.WriteAllText(Path.Combine(outDir, "layout-measurement.txt"), Report.ToString(), Encoding.ASCII);
            Console.WriteLine($"[OK] wrote {Path.Combine(outDir, "layout-measurement.txt")}");
            return 0;
        }

        // ---- AC1 / AC3 / AC6 / AC8: the layout of every tab -------------------

        private static void MeasureLayout(string outDir)
        {
            var win = NewEditor(NewConfig());
            double defaultWidth = win.Width, defaultHeight = win.Height;
            Show(win);
            Settle(TimeSpan.FromSeconds(5));   // the camera list loads in the background

            var tabs = (TabControl)win.FindName("EditorTabs")!;
            var root = (FrameworkElement)win.Content;
            Say($"XAML default : Width={defaultWidth:F0} Height={defaultHeight:F0}, {tabs.Items.Count} tabs");
            Say("");

            var everSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (int h in new[] { 0, 600 })
            {
                win.Height = h > 0 ? h : defaultHeight;
                Settle(TimeSpan.FromMilliseconds(600));
                string sizeLabel = h > 0 ? $"height-{h}" : $"default-{defaultHeight:F0}";
                Say($"=== window {win.ActualWidth:F0} x {win.ActualHeight:F0} ({sizeLabel}) ===");

                for (int i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    Settle(TimeSpan.FromMilliseconds(600));
                    var tab = (TabItem)tabs.Items[i]!;
                    string header = tab.Header?.ToString() ?? $"tab{i}";

                    // The tab's content is its ScrollViewer. It is NOT a visual child of the TabItem
                    // (TabControl hosts the selected content in its own PART_SelectedContentHost), so
                    // it is taken from the logical content rather than walked for.
                    var sv = tab.Content as ScrollViewer
                        ?? throw new InvalidOperationException($"The {header} tab has no ScrollViewer - the safety net is gone.");

                    Say($"  --- tab \"{header}\" ---");
                    Say($"    ComputedVerticalScrollBarVisibility : {sv.ComputedVerticalScrollBarVisibility}");
                    Say($"    ExtentHeight / ViewportHeight       : {sv.ExtentHeight:F1} / {sv.ViewportHeight:F1}");
                    Say($"    ScrollableHeight                    : {sv.ScrollableHeight:F1}");

                    sv.ScrollToTop();
                    Settle(TimeSpan.FromMilliseconds(200));
                    var hiddenAtTop = Hidden(win, sv, root, out var collapsed, out var visible);

                    // "Reachable by scrolling" is tested by actually scrolling TO each hidden control
                    // (BringIntoView), not by looking at the two extremes - a control taller than
                    // nothing but shorter than the viewport sits at neither end.
                    var stuck = new List<string>();
                    foreach (string name in hiddenAtTop.Keys.ToList())
                    {
                        var c = (FrameworkElement)win.FindName(name)!;
                        c.BringIntoView();
                        Settle(TimeSpan.FromMilliseconds(250));
                        var still = Hidden(win, sv, root, out _, out _);
                        if (still.ContainsKey(name)) stuck.Add(name + " " + still[name]);
                    }
                    sv.ScrollToTop();
                    Settle(TimeSpan.FromMilliseconds(200));

                    foreach (string v in visible) everSeen.Add(v);

                    Say($"    controls rendered on this tab       : {visible.Count}"
                        + (collapsed.Count == 0 ? "" : $" (+{collapsed.Count} collapsed by design: {string.Join(", ", collapsed)})"));
                    if (hiddenAtTop.Count == 0)
                        Say("    every rendered control fully visible WITHOUT scrolling: YES");
                    else
                    {
                        Say($"    needs scrolling to reach ({hiddenAtTop.Count}):");
                        foreach (var kv in hiddenAtTop) Say($"      - {kv.Key} {kv.Value}");
                        Say($"    still unreachable after scrolling to it: {(stuck.Count == 0 ? "none" : string.Join("; ", stuck))}");
                    }

                    string png = Path.Combine(outDir, $"tab-{header.ToLowerInvariant()}-{sizeLabel}.png");
                    Capture(win, png);
                    Say($"    screenshot: {Path.GetFileName(png)}");
                }
                Say("");
            }

            var never = AllNames.Where(n => !everSeen.Contains(n)).ToList();
            Say($"NAMED CONTROLS: {AllNames.Length} expected, {everSeen.Count} seen rendered across the tabs.");
            Say($"  never rendered on any tab: {(never.Count == 0 ? "none" : string.Join(", ", never))}");
            Say("  NOTE: RegionWarn is Visibility=Collapsed until a region overflows its monitor, so it");
            Say("  is expected in that list; every other name must be seen.");
            Say("");

            // Back to the default size before closing, so the state this run remembers (and the next
            // section's screenshot) is the dialog as it actually opens.
            win.Width = defaultWidth;
            win.Height = defaultHeight;
            Settle(TimeSpan.FromMilliseconds(400));
            win.Close();
            Pump();
        }

        // ---- AC8 / AC9: live frames, and the camera handed straight back ------

        /// <summary>
        /// Selects a real camera, waits for real frames, and then proves the device is handed back:
        /// once while the preview is still running (a recording must be able to take it anyway,
        /// through CameraDeviceArbiter) and once after leaving the Camera tab.
        ///
        /// A machine with no camera cannot observe this, and says so rather than passing quietly.
        /// </summary>
        private static void ProveLivePreviewAndRelease(string outDir)
        {
            Say("=== AC8 / AC9: live preview and camera release ===");

            var win = NewEditor(NewConfig());
            Show(win);
            Settle(TimeSpan.FromSeconds(6));   // camera enumeration
            var tabs = (TabControl)win.FindName("EditorTabs")!;
            var box = (ComboBox)win.FindName("CameraBox")!;
            var image = (System.Windows.Controls.Image)win.FindName("CameraPreviewImage")!;
            var status = (TextBlock)win.FindName("CameraPreviewStatus")!;

            tabs.SelectedIndex = 2;
            Settle(TimeSpan.FromMilliseconds(500));

            if (box.Items.Count < 2)
            {
                Say("  NO CAMERA is attached to this machine (the picker holds only \"(None)\").");
                Say("  AC8 and AC9 CANNOT be observed here - this is a missing observation, NOT a pass.");
                win.Close(); Pump(); Say("");
                return;
            }

            box.SelectedIndex = 1;
            string camera = (string)box.Items[1]!;
            Say($"  camera selected: \"{camera}\"");

            bool live = WaitFor(() => image.Source != null, TimeSpan.FromSeconds(20));
            Say($"  live frames arriving in the pane: {(live ? "YES" : "NO")} (status: \"{status.Text}\")");
            if (live)
            {
                var src = (System.Windows.Media.Imaging.BitmapSource)image.Source!;
                Say($"  rendered frame source: {src.PixelWidth}x{src.PixelHeight}, pane is "
                    + $"{((FrameworkElement)win.FindName("CameraPreviewPanel")!).ActualWidth:F0}x"
                    + $"{((FrameworkElement)win.FindName("CameraPreviewPanel")!).ActualHeight:F0}");
                Capture(win, Path.Combine(outDir, "tab-camera-live.png"));
                Say("  screenshot: tab-camera-live.png");
            }
            else
            {
                Say("  AC8 NOT OBSERVED: no frame reached the pane.");
            }

            // (a) the arbiter releases the device WHILE the preview is running.
            var core = Assembly.Load("agenteyes");
            var arbiter = core.GetType("AgentEyes.Video.CameraDeviceArbiter")!;
            var release = arbiter.GetMethod("ReleaseForRecording", BindingFlags.Public | BindingFlags.Static)!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            release.Invoke(null, new object[] { camera });
            sw.Stop();
            Say($"  CameraDeviceArbiter.ReleaseForRecording while previewing: {sw.ElapsedMilliseconds} ms");

            // (b) a REAL camera recording opens the same device end to end.
            Say($"  recording open after that release: {OpenCameraRecording(core, camera)}");

            // (c) leaving the Camera tab releases the device on its own.
            box.SelectedIndex = 0;
            Settle(TimeSpan.FromMilliseconds(300));
            box.SelectedIndex = 1;
            WaitFor(() => image.Source != null, TimeSpan.FromSeconds(20));
            tabs.SelectedIndex = 0;                     // leave the Camera tab
            Settle(TimeSpan.FromMilliseconds(500));

            var previewField = win.GetType().GetField("_cameraPreview", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object controller = previewField.GetValue(win)!;
            bool holds = (bool)controller.GetType().GetProperty("HoldsCamera")!.GetValue(controller)!;
            Say($"  after leaving the Camera tab, the preview still holds the camera: {(holds ? "YES - DEFECT" : "NO")}");
            Say($"  recording open after leaving the tab: {OpenCameraRecording(core, camera)}");

            win.Close();
            Pump();
            Say("");
        }

        /// <summary>Open a real camera recording and report how long it took, then stop it.</summary>
        private static string OpenCameraRecording(Assembly core, string camera)
        {
            var recType = core.GetType("AgentEyes.Video.FfmpegCameraRecorder")!;
            string outPath = Path.Combine(Path.GetTempPath(), $"issue35-probe-{Guid.NewGuid():N}.mp4");
            var create = recType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            object rec;
            try
            {
                // 30 fps, the preset default. NOT 15: this machine's webcam rejects a 15 fps dshow
                // open outright ("Could not set video options"), which is a camera capability, not a
                // device-contention failure - measuring the release with it would report a defect
                // that is not there.
                rec = create.Invoke(null, new object?[] { camera, 30, 23, outPath, null })!;
                recType.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance)!.Invoke(rec, null);
                sw.Stop();
            }
            catch (Exception ex)
            {
                sw.Stop();
                return $"FAILED after {sw.ElapsedMilliseconds} ms: {(ex.InnerException ?? ex).Message}";
            }

            string verdict = $"{sw.ElapsedMilliseconds} ms ({(sw.ElapsedMilliseconds <= 2000 ? "within" : "OVER")} the 2000 ms budget)";
            try { recType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance)!.Invoke(rec, null); }
            catch (Exception ex) { verdict += $" [stop said: {(ex.InnerException ?? ex).Message}]"; }
            try { ((IDisposable)rec).Dispose(); } catch { }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            try { if (File.Exists(outPath + ".ffmpeg.log")) File.Delete(outPath + ".ffmpeg.log"); } catch { }
            return verdict;
        }

        private static bool WaitFor(Func<bool> condition, TimeSpan limit)
        {
            var end = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < end)
            {
                if (condition()) return true;
                Settle(TimeSpan.FromMilliseconds(200));
            }
            return condition();
        }

        // ---- AC10: the tab, size and position survive a close and reopen ------

        private static void ProveRememberedState(string configPath)
        {
            Say("=== AC10: remembered tab / size / position ===");

            var win = NewEditor(NewConfig());
            Show(win);
            Settle(TimeSpan.FromSeconds(1));
            var tabs = (TabControl)win.FindName("EditorTabs")!;
            tabs.SelectedIndex = 2;                 // Camera
            win.Width = 1100; win.Height = 740;
            win.Left = 120; win.Top = 80;
            Settle(TimeSpan.FromMilliseconds(600));
            Say($"  set before closing: tab={tabs.SelectedIndex} size={win.Width:F0}x{win.Height:F0} pos={win.Left:F0},{win.Top:F0}");
            win.Close();
            Pump();

            string json = File.ReadAllText(configPath);
            foreach (string key in new[] { "PresetEditorTab", "PresetEditorWidth", "PresetEditorHeight",
                                           "PresetEditorLeft", "PresetEditorTop" })
            {
                int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (at < 0) throw new InvalidOperationException($"{key} was never written to config.json - AC10 is not met.");
                int end = json.IndexOf('\n', at);
                Say("  config.json: " + json.Substring(at, (end < 0 ? json.Length : end) - at).Trim());
            }

            var reopened = NewEditor(NewConfig());   // NewConfig reloads the file just written
            Show(reopened);
            Settle(TimeSpan.FromSeconds(1));
            var tabs2 = (TabControl)reopened.FindName("EditorTabs")!;
            Say($"  reopened: tab={tabs2.SelectedIndex} size={reopened.Width:F0}x{reopened.Height:F0} " +
                $"pos={reopened.Left:F0},{reopened.Top:F0}");
            bool ok = tabs2.SelectedIndex == 2
                      && Math.Abs(reopened.Width - 1100) < 2 && Math.Abs(reopened.Height - 740) < 2
                      && Math.Abs(reopened.Left - 120) < 2 && Math.Abs(reopened.Top - 80) < 2;
            Say($"  AC10 round trip: {(ok ? "PASS" : "FAIL")}");
            reopened.Close();
            Pump();
            Say("");
        }

        // ---- AC5: a preset survives a trip through the tabbed editor ----------

        /// <summary>
        /// Loads a fully-populated preset into the editor, reads it straight back out of the
        /// controls, writes it through PresetStore to the real presets.json and loads it again. If
        /// the tabs had dropped or mis-wired a control, the value it carries would come back changed.
        ///
        /// presets.json is backed up and restored around this.
        ///
        /// WHAT THIS CANNOT SEE: it exercises LoadFrom/ReadInto, which is what Save and Save as call;
        /// it does not click the buttons (DialogResult can only be set on a window opened with
        /// ShowDialog, which this harness deliberately does not do).
        /// </summary>
        private static void ProveSettingsRoundTrip()
        {
            Say("=== AC5: a fully-populated preset round-trips through the editor ===");

            var presetType = Need("AgentEyes.App.CapturePreset");
            object input = Activator.CreateInstance(presetType, nonPublic: true)!;
            void Set(string name, object? v) => presetType.GetProperty(name)!.SetValue(input, v);
            Set("Name", "round trip probe");
            Set("Note", "every field non-default");
            Set("MonitorIndex", 1);
            Set("UseRegion", true);
            Set("Region", new[] { 100, 120, 640, 480 });
            Set("Source", "system");
            Set("Denoise", false);
            Set("Gate", true);
            Set("Level", false);
            Set("MicVol", 123.0);
            Set("SysVol", 45.0);
            Set("Mode", "video");
            Set("Fps", 24);

            var editorType = Need("AgentEyes.App.PresetEditor");
            var ctor = editorType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                                                 null, new[] { presetType, Need("AgentEyes.App.Config") }, null)!;
            var win = (Window)ctor.Invoke(new[] { input, NewConfig() });
            Rooted.Add(win);
            Show(win);
            Settle(TimeSpan.FromSeconds(6));   // camera enumeration, so the camera field is live too

            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
            string before = System.Text.Json.JsonSerializer.Serialize(input, presetType, opts);

            var readInto = editorType.GetMethod("ReadInto", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // The Save path: the edited preset is written back into ITSELF, id and all.
            object saved = Activator.CreateInstance(presetType, nonPublic: true)!;
            foreach (var prop in presetType.GetProperties().Where(x => x.CanWrite))
                prop.SetValue(saved, prop.GetValue(input));
            readInto.Invoke(win, new[] { saved });

            // The Save-as path: a brand new preset, which gets its own fresh Id by construction -
            // so that one field is expected to differ and is compared separately.
            object output = Activator.CreateInstance(presetType, nonPublic: true)!;
            readInto.Invoke(win, new[] { output });
            win.Close();
            Pump();

            string savedJson = System.Text.Json.JsonSerializer.Serialize(saved, presetType, opts);
            string after = System.Text.Json.JsonSerializer.Serialize(output, presetType, opts);
            string WithoutId(string j) => System.Text.RegularExpressions.Regex.Replace(j, "\"Id\":\"[0-9a-f]*\",", "");
            Say($"  loaded    : {before}");
            Say($"  Save      : {savedJson}");
            Say($"  Save as   : {after}");
            Say($"  Save path round trip identical (all fields incl. Id): {(before == savedJson ? "YES" : "NO - DIFFERENT")}");
            Say($"  Save-as path identical apart from its new Id        : {(WithoutId(before) == WithoutId(after) ? "YES" : "NO - DIFFERENT")}");

            // ... and through the real presets.json.
            string presetsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentEyes", "presets.json");
            string backup = presetsPath + ".issue35-probe-bak";
            bool had = File.Exists(presetsPath);
            if (had) File.Copy(presetsPath, backup, overwrite: true);
            try
            {
                var storeType = Need("AgentEyes.App.PresetStore");
                var listType = typeof(List<>).MakeGenericType(presetType);
                var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                list.Add(output);
                storeType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { list });
                var reloaded = (System.Collections.IList)storeType
                    .GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                string back = System.Text.Json.JsonSerializer.Serialize(reloaded[0], presetType, opts);
                Say($"  presets.json reload: {back}");
                Say($"  presets.json round trip identical: {(back == after ? "YES" : "NO - DIFFERENT")}");
            }
            finally
            {
                if (had) File.Copy(backup, presetsPath, overwrite: true); else File.Delete(presetsPath);
                if (File.Exists(backup)) File.Delete(backup);
                Console.WriteLine("[OK] presets.json restored");
            }
            Say("");
        }

        // ---- plumbing ---------------------------------------------------------

        private static Type Need(string name) => _asm.GetType(name)
            ?? throw new InvalidOperationException($"Type {name} is not in AgentEyesApp - wrong build?");

        /// <summary>The real Config, loaded from the real config.json (backed up by Main).</summary>
        private static object NewConfig()
        {
            var cfgType = Need("AgentEyes.App.Config");
            return cfgType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        }

        private static Window NewEditor(object cfg)
        {
            var presetType = Need("AgentEyes.App.CapturePreset");
            object preset = Activator.CreateInstance(presetType, nonPublic: true)!;
            presetType.GetProperty("Name")!.SetValue(preset, "layout probe");

            var editorType = Need("AgentEyes.App.PresetEditor");
            var ctor = editorType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                                                 null, new[] { presetType, cfg.GetType() }, null)
                ?? throw new InvalidOperationException("PresetEditor(CapturePreset, Config) constructor not found.");
            var win = (Window)ctor.Invoke(new[] { preset, cfg });
            Rooted.Add(win);
            return win;
        }

        private static void Show(Window win)
        {
            win.ShowActivated = false;      // never steal focus from whatever the person is doing
            win.ShowInTaskbar = false;
            if (win.WindowStartupLocation != WindowStartupLocation.Manual)
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = 60;
                win.Top = 40;
            }
            win.Show();
        }

        /// <summary>
        /// The named controls that are NOT fully visible right now, with where they sit. Controls
        /// inside the tab's ScrollViewer are measured against its VIEWPORT (what you can see without
        /// scrolling); the Save/Cancel row and the name/note header live outside it and are measured
        /// against the client area. Judging everything against the client area would call a clipped
        /// control "on screen" and is exactly the check that proves nothing.
        /// </summary>
        private static Dictionary<string, string> Hidden(Window win, ScrollViewer sv, FrameworkElement root,
                                                         out List<string> collapsed, out List<string> visible)
        {
            var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
            collapsed = new List<string>();
            visible = new List<string>();
            foreach (string n in AllNames)
            {
                var c = win.FindName(n) as FrameworkElement
                    ?? throw new InvalidOperationException($"Control {n} is missing from the live window.");
                if (!c.IsVisible)
                {
                    // On a tabbed dialog, a control belonging to another tab is simply not rendered.
                    // Only a control that IS on this tab and still not rendered is interesting.
                    if (c.Visibility != Visibility.Visible) collapsed.Add(n);
                    continue;
                }
                visible.Add(n);
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
