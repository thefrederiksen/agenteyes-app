using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes.App
{
    /// <summary>
    /// The one place the detailed capture controls live. Loads a CapturePreset, lets the user tweak every
    /// field, and saves it back (Save) or as a new named preset (Save as). The launcher reads the result.
    /// </summary>
    public partial class PresetEditor : Window
    {
        /// <summary>First MicBox item: persisted as Mic = null, resolved to the Windows
        /// default input device at record time (PresetCapture.Start).</summary>
        private const string DefaultMicItem = "(System default)";

        /// <summary>First CameraBox item: persisted as Camera = null, i.e. no camera track.</summary>
        private const string NoCameraItem = "(None)";

        /// <summary>What CameraBox shows while the camera list is still being enumerated.</summary>
        private const string LoadingCamerasItem = "Loading cameras...";

        private readonly CapturePreset _preset;

        /// <summary>
        /// The app config, so the editor can remember where it was and which tab was open
        /// (issue #35, AC10). The caller owns this instance - the editor writes its own four keys
        /// into it and saves; it never replaces it.
        /// </summary>
        private readonly Config _cfg;
        private readonly List<MonitorInfo> _monitors = new();
        private Drawing.Rectangle? _region;
        private MonitorHighlight? _highlight;

        /// <summary>
        /// The camera saved on the preset, held until the (asynchronous) camera enumeration finishes
        /// so the picker can select it. Enumerating DirectShow video devices means launching ffmpeg,
        /// which is far too slow to do on the UI thread while the dialog is opening.
        /// </summary>
        private readonly string? _savedCamera;

        /// <summary>
        /// False until the camera list has actually loaded. It is what stops a Save made in the first
        /// moments of the dialog from writing "no camera" over a camera the user never touched: with
        /// no list there is no selection to read, and an empty picker must not be mistaken for the
        /// user choosing None.
        /// </summary>
        private bool _camerasLoaded;

        /// <summary>
        /// The live camera preview (issue #29). It owns the camera device; this dialog only tells it
        /// what is selected and paints what it sends. Disposed from Window.Closed, which is the one
        /// point every close route passes through (Save, Save as, Cancel, the X, Esc).
        /// </summary>
        private readonly CameraPreviewController _cameraPreview;

        /// <summary>The bitmap the preview frames are written into, allocated on the first frame.</summary>
        private WriteableBitmap? _previewBitmap;

        /// <summary>
        /// 1 while a frame is already queued for the UI thread. Frames arrive at ~10/s from a
        /// background thread and the newest one is the only one worth drawing, so a frame that
        /// arrives while one is pending is DROPPED rather than queued behind it - that is what keeps
        /// the dialog responsive while the preview runs (issue #29, AC8).
        /// </summary>
        private int _previewFramePending;

        /// <summary>The preset to persist once the dialog returns true (the edited instance, or a new one for Save as).</summary>
        internal CapturePreset? SavedPreset { get; private set; }

        internal PresetEditor(CapturePreset preset, Config cfg)
        {
            _preset = preset;
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _savedCamera = preset.Camera;
            InitializeComponent();
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            RestoreWindowState();
            try { PopulateDevices(); LoadFrom(preset); UpdateModeUi(); }
            catch (Exception ex) { ErrorText.Text = "Init error: " + ex.Message; }

            // Issue #29. The preview is created up front but opens nothing until a camera is
            // selected, so a preset with no camera never spawns a camera process (AC9).
            _cameraPreview = new CameraPreviewController();
            _cameraPreview.StateChanged += OnCameraPreviewStateChanged;
            _cameraPreview.FrameReceived += OnCameraPreviewFrame;

            // A preset that already has a camera says so IMMEDIATELY - before the (slow) camera
            // enumeration that has to finish before the picker can select it. The dialog is fully
            // interactive at this point and the pane says what it is doing (AC2).
            if (!string.IsNullOrWhiteSpace(_savedCamera))
                CameraPreviewStatus.Text = CameraPreviewController.StartingStatus;

            // Releasing the camera on the way out is the whole point of this feature: Save, Save as,
            // Cancel, the window close button and Esc all end here (AC4).
            Closed += (_, _) =>
            {
                try { _cameraPreview.Dispose(); }
                catch (Exception ex) { Log.Error("[PresetEditor] Closed: releasing the camera preview FAILED", ex); }
                try { RememberWindowState(); }
                catch (Exception ex) { Log.Error("[PresetEditor] Closed: remembering the window state FAILED", ex); }
            };

            // The camera list is the one expensive lookup in this dialog (it launches ffmpeg), so it
            // loads on a background thread AFTER the window is up - the dialog appears instantly with
            // the picker showing "Loading cameras..." and fills itself in.
            Loaded += async (_, _) => await LoadCamerasAsync();

            RegionRadio.Checked += (_, _) => { SelectAreaButton.IsEnabled = true; RegionOptions.IsEnabled = true; };
            RegionRadio.Unchecked += (_, _) =>
            {
                SelectAreaButton.IsEnabled = false;
                RegionOptions.IsEnabled = false;
                _region = null;
                RegionLabel.Text = "";
                RegionWarn.Visibility = Visibility.Collapsed;
            };
        }

        /// <summary>The aspect lock currently chosen in the picker (Free when nothing constrains the drag).</summary>
        private RegionMath.AspectLock SelectedAspect() => AspectBox.SelectedIndex switch
        {
            1 => RegionMath.AspectLock.Square,
            2 => RegionMath.AspectLock.Landscape16x9,
            3 => RegionMath.AspectLock.Vertical9x16,
            _ => RegionMath.AspectLock.Free,
        };

        /// <summary>The monitor the picker is targeting, in device pixels; falls back to the primary.</summary>
        private MonitorInfo SelectedMonitor()
        {
            if (MonitorBox.SelectedIndex >= 0 && MonitorBox.SelectedIndex < _monitors.Count)
                return _monitors[MonitorBox.SelectedIndex];
            return _monitors.FirstOrDefault(m => m.Primary) ?? _monitors[0];
        }

        // ---- populate -----------------------------------------------------

        private void PopulateDevices()
        {
            _monitors.Clear();
            _monitors.AddRange(Monitors.All());
            MonitorBox.Items.Clear();
            foreach (var m in _monitors)
                MonitorBox.Items.Add($"Monitor {m.Index}  -  {m.Width}x{m.Height}{(m.Primary ? "  (primary)" : "")}");

            MicBox.Items.Clear();
            MicBox.Items.Add(DefaultMicItem);
            foreach (var (_, name) in AudioCapture.Devices()) MicBox.Items.Add(name);

            // Placeholder only - the real list arrives from LoadCamerasAsync.
            CameraBox.Items.Clear();
            CameraBox.Items.Add(NoCameraItem);
            CameraBox.Items.Add(LoadingCamerasItem);
            CameraBox.SelectedIndex = 0;
            CameraBox.IsEnabled = false;
        }

        /// <summary>
        /// Enumerate the DirectShow cameras off the UI thread and fill the picker, then select the
        /// preset's saved camera. A camera that is no longer attached is NOT silently dropped: its
        /// name is appended to the list, stays selected, and the hint under the picker says it is not
        /// connected - because a preset quietly losing its camera is exactly the kind of invisible
        /// change this app must not make. Record-time resolution still fails loudly if it really is
        /// gone.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCamerasAsync()
        {
            List<string> cameras;
            try
            {
                cameras = await System.Threading.Tasks.Task.Run(
                    () => new List<string>(AgentEyes.Video.FfmpegDevices.ListVideo()));
            }
            catch (Exception ex)
            {
                Log.Error("[PresetEditor] LoadCamerasAsync: enumerating cameras failed", ex);
                CameraBox.Items.Clear();
                CameraBox.Items.Add(NoCameraItem);
                CameraBox.SelectedIndex = 0;
                CameraBox.IsEnabled = false;
                CameraHint.Text = "Cameras could not be listed: " + ex.Message;
                CameraPreviewStatus.Text = "Cameras could not be listed, so there is nothing to preview.";
                return;
            }

            CameraBox.Items.Clear();
            CameraBox.Items.Add(NoCameraItem);
            foreach (string name in cameras) CameraBox.Items.Add(name);

            int select = 0;
            if (!string.IsNullOrWhiteSpace(_savedCamera))
            {
                int found = cameras.FindIndex(n => n.Equals(_savedCamera, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    select = found + 1;   // +1 for the "(None)" entry
                }
                else
                {
                    CameraBox.Items.Add(_savedCamera);
                    select = CameraBox.Items.Count - 1;
                    CameraHint.Text = $"The saved camera \"{_savedCamera}\" is not connected right now. " +
                                      "It stays on the preset; recording will fail until it is back.";
                }
            }
            CameraBox.SelectedIndex = select;

            _camerasLoaded = true;
            CameraBox.IsEnabled = ModeVideo.IsChecked == true;

            // Issue #29, assumption B4: a machine with no camera at all is not an error - there is
            // simply nothing to preview, so the pane is not shown. A saved-but-absent camera still
            // gets a pane, because its failure to open is exactly what the user needs to see.
            bool anythingToPreview = cameras.Count > 0 || !string.IsNullOrWhiteSpace(_savedCamera);
            CameraPreviewPanel.Visibility = anythingToPreview ? Visibility.Visible : Visibility.Collapsed;

            Log.Info($"[PresetEditor] LoadCamerasAsync: {cameras.Count} camera(s) listed, selected index {select}, " +
                     $"preview pane {(anythingToPreview ? "shown" : "hidden")}");

            // Now that the picker really holds the saved camera, start previewing it.
            UpdateCameraPreview();
        }

        // ---- live camera preview (issue #29) --------------------------------

        /// <summary>
        /// Point the preview at whatever the picker now says, or stop it. This is the ONLY place the
        /// preview is told what to show, so "what is selected" and "what is being held" cannot drift.
        /// </summary>
        private void UpdateCameraPreview()
        {
            if (!_camerasLoaded) return;   // the picker still holds the placeholder, not a camera

            // Issue #28, assumption A1: the camera is a Video-mode setting. A preset that is not
            // going to record the camera has no business holding the device open to show it.
            if (ModeVideo.IsChecked != true)
            {
                _cameraPreview.Stop("The camera is only recorded in Video mode.");
                return;
            }

            // Issue #35, AC9: a preview nobody can see must not hold an exclusive device. Leaving
            // the Camera tab hands the camera back immediately, exactly as closing the dialog does.
            if (!ReferenceEquals(EditorTabs.SelectedItem, CameraTab))
            {
                _cameraPreview.Stop("The preview runs while the Camera tab is open.");
                return;
            }

            string? camera = CameraBox.SelectedIndex <= 0 ? null : CameraBox.SelectedItem as string;
            _cameraPreview.Select(camera);
        }

        /// <summary>
        /// The visible tab changed. Two consequences: the camera preview starts when the Camera tab
        /// comes forward and stops the instant it goes away (issue #35, AC9), and the tab is
        /// remembered so the editor reopens where it was left (AC10).
        /// </summary>
        private void EditorTabs_Changed(object sender, SelectionChangedEventArgs e)
        {
            // A TabControl re-raises its children's SelectionChanged (the combo boxes inside a tab
            // bubble one up); only the TabControl's own change is a tab change.
            if (!ReferenceEquals(e.OriginalSource, EditorTabs)) return;
            if (!IsInitialized) return;
            try
            {
                Log.Info($"[PresetEditor] EditorTabs_Changed: tab={EditorTabs.SelectedIndex}");
                UpdateCameraPreview();
            }
            catch (Exception ex)
            {
                Log.Error("[PresetEditor] EditorTabs_Changed FAILED", ex);
                CameraPreviewStatus.Text = "The preview could not be started: " + ex.Message;
            }
        }

        // ---- window state, remembered across restarts (issue #35, AC10) ------

        /// <summary>
        /// Put the editor back where it was: same tab, same size, same place on screen. A size or
        /// position that is not on any monitor now (a screen was unplugged) is not restored - it
        /// would open the dialog where nobody can see it - and the window centres on its owner
        /// instead, which is what it does the first time too.
        /// </summary>
        private void RestoreWindowState()
        {
            if (_cfg.PresetEditorWidth is double w && _cfg.PresetEditorHeight is double h
                && w >= MinWidth && h >= MinHeight)
            {
                Width = w;
                Height = h;
            }

            if (_cfg.PresetEditorLeft is double l && _cfg.PresetEditorTop is double t
                && IsOnAScreen(l, t, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }

            int tab = _cfg.PresetEditorTab;
            if (tab >= 0 && tab < EditorTabs.Items.Count) EditorTabs.SelectedIndex = tab;

            Log.Info($"[PresetEditor] RestoreWindowState: size={Width:F0}x{Height:F0} " +
                     $"pos={(WindowStartupLocation == WindowStartupLocation.Manual ? $"{Left:F0},{Top:F0}" : "center-owner")} " +
                     $"tab={EditorTabs.SelectedIndex}");
        }

        /// <summary>Write the editor's tab, size and position back to the config.</summary>
        private void RememberWindowState()
        {
            _cfg.PresetEditorTab = EditorTabs.SelectedIndex;
            if (WindowState == WindowState.Normal)
            {
                _cfg.PresetEditorWidth = ActualWidth;
                _cfg.PresetEditorHeight = ActualHeight;
                _cfg.PresetEditorLeft = Left;
                _cfg.PresetEditorTop = Top;
            }
            _cfg.Save();
            Log.Info($"[PresetEditor] RememberWindowState: tab={_cfg.PresetEditorTab} " +
                     $"size={_cfg.PresetEditorWidth:F0}x{_cfg.PresetEditorHeight:F0} " +
                     $"pos={_cfg.PresetEditorLeft:F0},{_cfg.PresetEditorTop:F0}");
        }

        /// <summary>
        /// True when the given window rectangle still lands on the desktop. Measured against
        /// SystemParameters.VirtualScreen, which is in the same device-independent units as
        /// Window.Left/Top - the monitor list is in device pixels and would compare wrongly under
        /// display scaling. At least 120 x 40 of the window must be inside, so its title bar is
        /// always grabbable.
        /// </summary>
        private static bool IsOnAScreen(double left, double top, double width, double height)
        {
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                   SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var wanted = new Rect(left, top, width, height);
            wanted.Intersect(desktop);
            return wanted.Width >= 120 && wanted.Height >= 40;
        }

        private void Camera_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;
            try { UpdateCameraPreview(); }
            catch (Exception ex)
            {
                Log.Error("[PresetEditor] Camera_Changed: updating the preview FAILED", ex);
                CameraPreviewStatus.Text = "The preview could not be started: " + ex.Message;
            }
        }

        /// <summary>
        /// The preview changed state. Raised on a BACKGROUND thread (a recording start calls into the
        /// controller and must never wait on the UI thread), so everything visual is marshalled.
        /// </summary>
        private void OnCameraPreviewStateChanged(CameraPreviewState state, string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    CameraPreviewStatus.Text = status;
                    CameraPreviewStatus.Foreground = (Brush)FindResource(
                        state == CameraPreviewState.Failed ? "DkRed" : "DkDim");

                    // Anything that is not "running" must not leave the last frame on screen
                    // pretending to be live.
                    if (state != CameraPreviewState.Running)
                    {
                        CameraPreviewImage.Source = null;
                        _previewBitmap = null;
                    }
                }
                catch (Exception ex) { Log.Error("[PresetEditor] OnCameraPreviewStateChanged FAILED", ex); }
            }));
        }

        /// <summary>One preview frame, from the reader thread. Dropped if the UI thread has not drawn
        /// the previous one yet - the newest frame is the only one worth having.</summary>
        private void OnCameraPreviewFrame(byte[] bgr24)
        {
            if (Interlocked.CompareExchange(ref _previewFramePending, 1, 0) != 0) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { DrawPreviewFrame(bgr24); }
                catch (Exception ex) { Log.Error("[PresetEditor] drawing a camera preview frame FAILED", ex); }
                finally { Interlocked.Exchange(ref _previewFramePending, 0); }
            }));
        }

        private void DrawPreviewFrame(byte[] bgr24)
        {
            if (bgr24.Length != FfmpegCameraPreview.FrameBytes)
                throw new InvalidOperationException(
                    $"a preview frame must be {FfmpegCameraPreview.FrameBytes} bytes, got {bgr24.Length}");

            if (_previewBitmap == null)
            {
                _previewBitmap = new WriteableBitmap(
                    FfmpegCameraPreview.FrameWidth, FfmpegCameraPreview.FrameHeight, 96, 96, PixelFormats.Bgr24, null);
                CameraPreviewImage.Source = _previewBitmap;
            }

            _previewBitmap.WritePixels(
                new Int32Rect(0, 0, FfmpegCameraPreview.FrameWidth, FfmpegCameraPreview.FrameHeight),
                bgr24, FfmpegCameraPreview.FrameWidth * 3, 0);
        }

        private void LoadFrom(CapturePreset p)
        {
            NameBox.Text = p.Name;
            NoteBox.Text = p.Note ?? "";

            int monIdx = _monitors.FindIndex(m => m.Index == p.MonitorIndex);
            MonitorBox.SelectedIndex = monIdx >= 0 ? monIdx : (_monitors.Count > 0 ? 0 : -1);

            if (p.UseRegion && p.Region is { Length: 4 })
            {
                RegionRadio.IsChecked = true;
                RegionOptions.IsEnabled = true;
                _region = new Drawing.Rectangle(p.Region[0], p.Region[1], p.Region[2], p.Region[3]);
                RegionLabel.Text = $"{p.Region[2]} x {p.Region[3]}";
                ExactWidthBox.Text = p.Region[2].ToString();
                ExactHeightBox.Text = p.Region[3].ToString();
            }
            else FullRadio.IsChecked = true;

            if (!string.IsNullOrWhiteSpace(p.Mic))
            {
                // Search concrete devices only (item 0 is the "(System default)" sentinel);
                // a saved mic that is no longer present falls back - visibly - to default.
                int mi = MicBox.Items.Cast<string>().ToList().FindIndex(1, n => n.Contains(p.Mic!, StringComparison.OrdinalIgnoreCase));
                MicBox.SelectedIndex = mi >= 1 ? mi : 0;
            }
            else MicBox.SelectedIndex = 0;   // (System default)

            SrcMic.IsChecked = p.Source == "mic";
            SrcSystem.IsChecked = p.Source == "system";
            SrcMixed.IsChecked = p.Source is not ("mic" or "system");

            DenoiseCheck.IsChecked = p.Denoise;
            // Issue #83: null Gate = follow the source default (mic-only OFF, mixed/system ON).
            GateCheck.IsChecked = p.Gate ?? GateDefaults.For(p.Source);
            LevelCheck.IsChecked = p.Level;
            MicVol.Value = p.MicVol;
            SysVol.Value = p.SysVol;
            MicVolText.Text = $"{p.MicVol:F0}%";
            SysVolText.Text = $"{p.SysVol:F0}%";

            ModeShot.IsChecked = p.Mode == "shot";
            ModeAudio.IsChecked = p.Mode == "audio";
            ModeVideo.IsChecked = p.Mode is not ("shot" or "audio");
            SelectFps(p.Fps);
        }

        private void SelectFps(int fps)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in FpsBox.Items)
                if (item.Content?.ToString() == fps.ToString()) { item.IsSelected = true; return; }
            FpsBox.SelectedIndex = 2; // 30
        }

        // ---- read controls into a preset ----------------------------------

        private void ReadInto(CapturePreset p)
        {
            p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Untitled" : NameBox.Text.Trim();
            p.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
            p.MonitorIndex = MonitorBox.SelectedIndex >= 0 ? _monitors[MonitorBox.SelectedIndex].Index : 1;

            p.UseRegion = RegionRadio.IsChecked == true && _region != null;
            p.Region = p.UseRegion
                ? new[] { _region!.Value.X, _region.Value.Y, _region.Value.Width, _region.Value.Height }
                : null;

            p.Source = SrcMic.IsChecked == true ? "mic" : SrcSystem.IsChecked == true ? "system" : "mixed";
            p.Mic = MicBox.SelectedIndex <= 0 ? null : MicBox.SelectedItem as string;
            p.Denoise = DenoiseCheck.IsChecked == true;
            p.Gate = GateCheck.IsChecked == true;
            p.Level = LevelCheck.IsChecked == true;
            p.MicVol = MicVol.Value;
            p.SysVol = SysVol.Value;

            p.Mode = ModeShot.IsChecked == true ? "shot" : ModeAudio.IsChecked == true ? "audio" : "video";
            p.Fps = int.TryParse((FpsBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), out int f) ? f : 30;

            // Only read the camera picker once it actually holds the camera list. Reading it earlier
            // would read the placeholder and silently clear a saved camera (see _camerasLoaded).
            if (_camerasLoaded)
                p.Camera = CameraBox.SelectedIndex <= 0 ? null : CameraBox.SelectedItem as string;
        }

        // ---- ui state -----------------------------------------------------

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            try
            {
                UpdateModeUi();
                // Leaving Video mode releases the camera; coming back to it starts the preview again.
                UpdateCameraPreview();
            }
            catch (Exception ex) { Log.Error("[PresetEditor] Mode_Changed FAILED", ex); }
        }

        private void AudioSrc_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            // Issue #83: the gate only tames speaker bleed, which a mic-only source does not have,
            // so default the gate OFF when the user switches to mic-only and ON for mixed/system.
            // This is a default suggestion the user can immediately re-check.
            string src = SrcMic.IsChecked == true ? "mic" : SrcSystem.IsChecked == true ? "system" : "mixed";
            GateCheck.IsChecked = GateDefaults.For(src);
            UpdateModeUi();
        }

        private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            MicVolText.Text = $"{MicVol.Value:F0}%";
            SysVolText.Text = $"{SysVol.Value:F0}%";
        }

        private void UpdateModeUi()
        {
            string mode = ModeShot.IsChecked == true ? "shot" : ModeAudio.IsChecked == true ? "audio" : "video";
            bool wantsAudio = mode is "audio" or "video";
            bool isMic = SrcMic.IsChecked == true, isSys = SrcSystem.IsChecked == true;
            bool usesMic = wantsAudio && !isSys;
            bool usesSys = wantsAudio && !isMic;

            SrcMic.IsEnabled = SrcSystem.IsEnabled = SrcMixed.IsEnabled = wantsAudio;
            MicBox.IsEnabled = usesMic;
            DenoiseCheck.IsEnabled = usesMic;
            GateCheck.IsEnabled = usesMic;
            LevelCheck.IsEnabled = usesMic;
            MicVol.IsEnabled = usesMic;
            SysVol.IsEnabled = usesSys;
            FpsBox.IsEnabled = mode == "video";
            // Issue #28, assumption A1: the camera is a video-mode setting, so the picker is disabled
            // (but keeps its value) for shot/audio presets. It also stays disabled until the camera
            // list has loaded.
            CameraBox.IsEnabled = mode == "video" && _camerasLoaded;
        }

        // ---- region + show area -------------------------------------------

        private void SelectArea_Click(object sender, RoutedEventArgs e)
        {
            RegionRadio.IsChecked = true;
            var saved = WindowState;
            WindowState = WindowState.Minimized;
            try
            {
                var rect = RegionOverlay.Select(SelectedAspect());
                if (rect != null) ApplyRegion(rect.Value, warnIfExceeds: false);
            }
            finally { WindowState = saved; }
        }

        /// <summary>
        /// Set the exact-pixel region typed into the W x H boxes, centered on the selected monitor.
        /// The requested size is preserved exactly; only the origin is clamped (RegionMath). A size
        /// that overflows the monitor still applies but shows a warning.
        /// </summary>
        private void SetExact_Click(object sender, RoutedEventArgs e)
        {
            RegionWarn.Visibility = Visibility.Collapsed;
            if (!int.TryParse(ExactWidthBox.Text?.Trim(), out int w) || w < 2 ||
                !int.TryParse(ExactHeightBox.Text?.Trim(), out int h) || h < 2)
            {
                RegionWarn.Text = "Enter whole-number width and height (>= 2 px).";
                RegionWarn.Visibility = Visibility.Visible;
                return;
            }
            ApplyExactSize(w, h);
        }

        private void QuickSquare_Click(object sender, RoutedEventArgs e) => ApplyQuickPreset("Square 1080x1080", 1080, 1080);
        private void QuickVertical_Click(object sender, RoutedEventArgs e) => ApplyQuickPreset("Vertical 1080x1920", 1080, 1920);
        private void QuickLandscape_Click(object sender, RoutedEventArgs e) => ApplyQuickPreset("Landscape 1920x1080", 1920, 1080);

        /// <summary>
        /// One-click social-format preset: switches to a region video preset of exactly WxH centered
        /// on the selected monitor, names it (if the name box is empty or a prior quick name), and
        /// mirrors the size into the exact-size boxes. Save (or Save as) then persists it.
        /// </summary>
        private void ApplyQuickPreset(string name, int w, int h)
        {
            RegionRadio.IsChecked = true;
            ModeVideo.IsChecked = true;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || IsQuickPresetName(NameBox.Text.Trim()))
                NameBox.Text = name;
            ExactWidthBox.Text = w.ToString();
            ExactHeightBox.Text = h.ToString();
            ApplyExactSize(w, h);
        }

        private static bool IsQuickPresetName(string n) =>
            n is "Untitled" or "Default" or "Square 1080x1080" or "Vertical 1080x1920" or "Landscape 1920x1080";

        /// <summary>Center an exact WxH region on the selected monitor and apply it (warns on overflow).</summary>
        private void ApplyExactSize(int w, int h)
        {
            var mon = SelectedMonitor();
            var rect = RegionMath.CenteredExactSize(mon.Bounds, w, h);
            bool exceeds = RegionMath.ExceedsMonitor(mon.Bounds, w, h);
            ApplyRegion(rect, warnIfExceeds: exceeds, monitorIndex: mon.Index);
        }

        /// <summary>Store the region, echo its size in the labels, and optionally warn on overflow.</summary>
        private void ApplyRegion(Drawing.Rectangle rect, bool warnIfExceeds, int? monitorIndex = null)
        {
            _region = rect;
            RegionLabel.Text = $"{rect.Width} x {rect.Height}";
            if (warnIfExceeds && monitorIndex is int mi)
            {
                RegionWarn.Text = $"Region {rect.Width}x{rect.Height} is larger than monitor {mi} - the area " +
                                  "extends past the screen edge (off-screen pixels record black).";
                RegionWarn.Visibility = Visibility.Visible;
            }
            else RegionWarn.Visibility = Visibility.Collapsed;
        }

        private void ShowArea_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorBox.SelectedIndex < 0 || MonitorBox.SelectedIndex >= _monitors.Count) return;
            var mon = _monitors[MonitorBox.SelectedIndex];
            _highlight?.Close();
            if (RegionRadio.IsChecked == true && _region != null)
                _highlight = MonitorHighlight.Flash(_region.Value, $"Recording area   {_region.Value.Width} x {_region.Value.Height}");
            else
                _highlight = MonitorHighlight.Flash(mon.Bounds, $"Monitor {mon.Index}   {mon.Width} x {mon.Height}");
        }

        // ---- save / cancel ------------------------------------------------

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;
            ReadInto(_preset);
            SavedPreset = _preset;
            CloseHighlight();
            DialogResult = true;
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;
            string? name = PromptDialog.Ask(this, "Save preset as", "Name for the new preset:", NameBox.Text.Trim());
            if (name == null) return;
            var np = new CapturePreset();
            ReadInto(np);
            np.Name = name;
            SavedPreset = np;
            CloseHighlight();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { CloseHighlight(); DialogResult = false; }

        private bool Validate()
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) { ErrorText.Text = "Name is required."; return false; }
            if (RegionRadio.IsChecked == true && _region == null) { ErrorText.Text = "Click 'Select area...' to set the region."; return false; }
            ErrorText.Text = "";
            return true;
        }

        private void CloseHighlight() { _highlight?.Close(); _highlight = null; }
    }
}
