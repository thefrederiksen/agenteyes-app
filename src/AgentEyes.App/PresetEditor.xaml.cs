using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Preview;
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

        /// <summary>
        /// WHICH PANEL A REMEMBERED WINDOW SIZE BELONGS TO. Bump this whenever the editor's layout
        /// changes what size it needs, and every size remembered against an older panel is dropped -
        /// the editor opens at its XAML default instead. 1 was issue #35's two-column Camera tab at
        /// 1000x760; 2 is issue #43's three-column one at 1280x760, which does not fit in 1000.
        ///
        /// Without this, the scrollbar #35 removed would come back for every EXISTING installation
        /// and only for those - their config already holds a 1000x760 from the old panel, so the new
        /// default would never be applied and the defect would look fixed on a clean machine.
        /// </summary>
        internal const int LayoutVersion = 2;

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

        /// <summary>
        /// True once Window.Closed has run (issue #35, Review Gate round 1, defect 1).
        ///
        /// The camera enumeration is slow - it launches ffmpeg - and every close route (Save, Save
        /// as, Cancel, the X, Esc) can happen while it is still running. Its continuation then
        /// selected the saved camera and started a preview into a window that no longer existed. The
        /// controller REFUSES that now, which is the durable half of the fix; this is the dialog's
        /// half, so the closed editor does not go on writing into its own controls either.
        /// </summary>
        private bool _closed;

        /// <summary>The bitmap the preview frames are written into, allocated on the first frame.</summary>
        private WriteableBitmap? _previewBitmap;

        /// <summary>
        /// True once the constructor has finished. Every overlay handler checks it (issue #36): WPF
        /// raises SizeChanged and ValueChanged while the tree is being built, and the overlay drawing
        /// reads <see cref="_cameraPreview"/>, which is created late in the constructor.
        /// </summary>
        private bool _overlayReady;

        /// <summary>1 while the overlay controls are being FILLED IN from a preset, so the handlers
        /// they raise do not read half-loaded values back out again.</summary>
        private bool _loadingOverlay;

        /// <summary>True while the circle is being dragged across the live picture.</summary>
        private bool _draggingCircle;

        /// <summary>
        /// The camera frame size the adorner was last drawn for (issue #36). ffmpeg reports it a
        /// moment after the camera opens, so the adorner has to be redrawn when it arrives - and
        /// when it goes away again, because a preview that has stopped can no longer say where the
        /// picture is.
        /// </summary>
        private CameraFrameSize? _adornerFrameSize;

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
                _closed = true;
                try { _cameraPreview.Dispose(); }
                catch (Exception ex) { Log.Error("[PresetEditor] Closed: releasing the camera preview FAILED", ex); }
                try { RememberWindowState(); }
                catch (Exception ex) { Log.Error("[PresetEditor] Closed: remembering the window state FAILED", ex); }
            };

            // The camera list is the one expensive lookup in this dialog (it launches ffmpeg), so it
            // loads on a background thread AFTER the window is up - the dialog appears instantly with
            // the picker showing "Loading cameras..." and fills itself in.
            // Issue #36: the overlay adorner is drawn for the first time here, not in the
            // constructor body - it needs a laid-out canvas and a live controller, and it has
            // neither until the window is up.
            _overlayReady = true;
            Loaded += (_, _) => UpdateOverlayUi();
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
                if (_closed) return;
                CameraBox.Items.Clear();
                CameraBox.Items.Add(NoCameraItem);
                CameraBox.SelectedIndex = 0;
                CameraBox.IsEnabled = false;
                CameraHint.Text = "Cameras could not be listed: " + ex.Message;
                CameraPreviewStatus.Text = "Cameras could not be listed, so there is nothing to preview.";
                return;
            }

            // The dialog closed while DirectShow was being enumerated. There is nothing left to fill
            // in and - the part that mattered - nothing left to preview into.
            if (_closed)
            {
                Log.Info($"[PresetEditor] LoadCamerasAsync: the editor closed while {cameras.Count} camera(s) were "
                         + "being enumerated - no preview is started");
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
            if (_closed) return;           // the editor is gone; the controller refuses anyway

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
            if (_cfg.PresetEditorLayout == LayoutVersion
                && _cfg.PresetEditorWidth is double w && _cfg.PresetEditorHeight is double h
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
                _cfg.PresetEditorLayout = LayoutVersion;   // the panel this size was chosen for
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

                    // Issue #36: no live picture means nothing to place the circle against, and the
                    // adorner says so rather than hanging over a black pane.
                    UpdateOverlayUi();
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

            // Issue #36: ffmpeg reports the camera's own frame size a moment after the device opens,
            // and the circle cannot be placed until it has. Redraw on the frame where it CHANGES -
            // including the change back to "not known" - rather than on every frame.
            if (!Equals(_cameraPreview.SourceSize, _adornerFrameSize)) UpdateOverlayUi();
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

            // Issue #36: the overlay framing. A preset saved before this feature existed carries the
            // property initializer - the documented defaults, circle first.
            LoadOverlayFrom(p.Overlay);
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

            // Issue #36. Always read: unlike the camera picker there is no slow lookup behind these
            // controls, so they hold the real values from the moment the dialog opens.
            p.Overlay = ReadOverlay();
        }

        // ---- the camera overlay's framing (issue #36) -----------------------

        /// <summary>Fill the overlay controls from a preset, without letting the handlers they raise
        /// read half-filled values straight back out.</summary>
        private void LoadOverlayFrom(CameraOverlaySettings? overlay)
        {
            var o = (overlay ?? new CameraOverlaySettings()).Canonical();
            _loadingOverlay = true;
            try
            {
                OverlayShapeCircle.IsChecked = o.ShapeValue == CameraOverlayShape.Circle;
                OverlayShapeRectangle.IsChecked = o.ShapeValue == CameraOverlayShape.Rectangle;
                CircleXSlider.Value = o.Circle.CentreX;
                CircleYSlider.Value = o.Circle.CentreY;
                CircleSizeSlider.Value = o.Circle.Diameter;
                InsetSizeSlider.Value = o.InsetFraction;
                OverlayCornerBox.SelectedIndex = CornerIndex(o.CornerValue);
            }
            finally { _loadingOverlay = false; }

            Log.Info($"[PresetEditor] LoadOverlayFrom: {o}");
            UpdateOverlayUi();
        }

        /// <summary>The overlay framing exactly as the controls now read it.</summary>
        private CameraOverlaySettings ReadOverlay() => new CameraOverlaySettings
        {
            Shape = PreviewNames.Text(OverlayShapeRectangle.IsChecked == true
                ? CameraOverlayShape.Rectangle
                : CameraOverlayShape.Circle),
            Corner = PreviewNames.Text(SelectedOverlayCorner()),
            InsetFraction = InsetSizeSlider.Value,
            Circle = new CameraOverlayCircle
            {
                CentreX = CircleXSlider.Value,
                CentreY = CircleYSlider.Value,
                Diameter = CircleSizeSlider.Value,
            },
        }.Canonical();

        private PreviewCorner SelectedOverlayCorner() => OverlayCornerBox.SelectedIndex switch
        {
            1 => PreviewCorner.BottomLeft,
            2 => PreviewCorner.TopLeft,
            3 => PreviewCorner.TopRight,
            _ => PreviewCorner.BottomRight,
        };

        private static int CornerIndex(PreviewCorner corner) => corner switch
        {
            PreviewCorner.BottomLeft => 1,
            PreviewCorner.TopLeft => 2,
            PreviewCorner.TopRight => 3,
            _ => 0,
        };

        /// <summary>
        /// WHERE THE CAMERA'S PICTURE ACTUALLY IS inside the preview pane, in the pane's own pixels -
        /// or NULL when that cannot be known yet (issue #36).
        ///
        /// Two nested fits, and both are real: the 320x240 preview buffer is drawn into the pane with
        /// WPF's Uniform stretch, and inside that buffer the camera's picture is letterboxed by
        /// ffmpeg's own pad filter. Skipping the second fit would put the circle over the black bars
        /// on any camera that is not 4:3.
        ///
        /// NULL IS "NOT KNOWN" AND IS NEVER TREATED AS "IT FILLS THE PANE". Assuming a size would
        /// silently draw the circle over the wrong part of the face, and the drawing would look
        /// perfectly convincing while doing it - so the caller says so instead.
        /// </summary>
        private OverlayRect? PreviewContentRect()
        {
            if (!_overlayReady) return null;

            double paneWidth = CameraOverlayAdorner.ActualWidth;
            double paneHeight = CameraOverlayAdorner.ActualHeight;
            if (paneWidth <= 0 || paneHeight <= 0) return null;

            if (_cameraPreview.SourceSize is not { } camera) return null;

            var buffer = OverlayFit.Contain(paneWidth, paneHeight,
                                            FfmpegCameraPreview.FrameWidth, FfmpegCameraPreview.FrameHeight);
            var inner = OverlayFit.Contain(buffer.Width, buffer.Height, camera.Width, camera.Height);
            return new OverlayRect(buffer.X + inner.X, buffer.Y + inner.Y, inner.Width, inner.Height);
        }

        /// <summary>
        /// Push the overlay controls' current values into the labels, the enabled states and the
        /// circle drawn over the live picture. One place, so the numbers, the words and the drawing
        /// cannot disagree.
        /// </summary>
        private void UpdateOverlayUi()
        {
            if (!_overlayReady) return;

            bool circle = OverlayShapeCircle.IsChecked == true;
            CircleControls.IsEnabled = circle;

            CircleXText.Text = $"{CircleXSlider.Value * 100:F0}%";
            CircleYText.Text = $"{CircleYSlider.Value * 100:F0}%";
            CircleSizeText.Text = $"{CircleSizeSlider.Value * 100:F0}%";
            InsetSizeText.Text = $"{InsetSizeSlider.Value * 100:F0}%";

            RedrawOverlayAdorner(circle);
            RedrawInsetSchematic(circle);
        }

        /// <summary>
        /// Draw (or clear) the circle over the live camera image.
        ///
        /// The dimmed area is the part of the camera frame the circle leaves out - it is dimmed, not
        /// removed, because that is exactly what happens to it: camera.mp4 still records all of it
        /// (assumption E1), and this circle is a framing choice that can be moved later.
        /// </summary>
        private void RedrawOverlayAdorner(bool circle)
        {
            var content = circle ? PreviewContentRect() : null;
            _adornerFrameSize = _cameraPreview.SourceSize;

            if (content is not { } area)
            {
                OverlayMaskPath.Data = null;
                OverlayOutlinePath.Data = null;
                OverlayHint.Text = circle
                    ? "Waiting for the camera picture. The circle is drawn once ffmpeg reports the "
                      + "camera's own frame size - nothing is assumed, because an assumed size would "
                      + "put the circle over the wrong part of the picture."
                    : "Rectangle: the whole camera frame is inset, exactly as before. camera.mp4 "
                      + "records the full frame either way.";
                return;
            }

            var bounds = ReadOverlay().Circle.PixelBounds(area.Width, area.Height);
            var centre = new Point(area.X + bounds.CentreX, area.Y + bounds.CentreY);
            double radius = bounds.Width / 2.0;

            var ellipse = new EllipseGeometry(centre, radius, radius);
            var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
            outside.Children.Add(new RectangleGeometry(
                new Rect(0, 0, CameraOverlayAdorner.ActualWidth, CameraOverlayAdorner.ActualHeight)));
            outside.Children.Add(ellipse);

            OverlayMaskPath.Data = outside;
            OverlayOutlinePath.Data = ellipse;
            OverlayHint.Text =
                $"Circle shown over this camera's own {_adornerFrameSize} frame. It is a framing "
                + "choice, not a crop: camera.mp4 still records the whole rectangular frame, and the "
                + "circle is written into the recording's manifest so the framing can be reproduced - "
                + "or moved - later.";
        }

        /// <summary>
        /// THE FEEDBACK FOR "SIZE ON SCREEN" AND "CORNER" (issue #43).
        ///
        /// Draw the small schematic of the recording: a 16:9 box with the camera inset in the chosen
        /// corner at the chosen fraction of the recording's width. Before this, those two controls
        /// changed a stored number and a text label and NOTHING ELSE - the only drawing in the dialog
        /// was the circle over the live camera picture, which the inset fraction has no part in - so
        /// the slider was reported as broken. It was not broken; it was invisible.
        ///
        /// It is a SCHEMATIC, not a composite (assumption F1): no screen capture is started to draw
        /// it. The camera's own picture IS shown inside the inset, because the preview beside it is
        /// already receiving those frames - so the DIAMETER's crop and the INSET's size on the
        /// recording are visible in the same picture, which is what tells the two sliders apart.
        /// </summary>
        private void RedrawInsetSchematic(bool circle)
        {
            double boxWidth = InsetSchematicCanvas.ActualWidth;
            double boxHeight = InsetSchematicCanvas.ActualHeight;

            // Not laid out yet. SizeChanged draws it the moment it is - the canvas is never assumed
            // to be some size, because an assumed size would draw an inset that is to no scale at all.
            if (boxWidth <= 0 || boxHeight <= 0) return;

            var overlay = ReadOverlay();
            var camera = _cameraPreview.SourceSize;
            double aspect = camera is { } cam
                ? cam.Width / (double)cam.Height
                : InsetSchematic.DefaultFrameAspect;

            var placed = InsetSchematic.Place(boxWidth, boxHeight, overlay, aspect);
            var rect = new Rect(placed.X, placed.Y, placed.Width, placed.Height);

            InsetSchematicScreenPath.Data = ScreenMotif(boxWidth, boxHeight);
            InsetSchematicInsetPath.Data = circle ? new EllipseGeometry(rect) : new RectangleGeometry(rect);
            InsetSchematicInsetPath.Fill = InsetSchematicFill(circle, camera);

            string caption = $"The camera covers {overlay.ClampedInsetFraction * 100:F0}% of the recording's "
                             + $"width, {PreviewNames.Text(overlay.CornerValue)}.";
            if (placed.Y < 0 || placed.Bottom > boxHeight)
                caption += " At this size it is taller than the recording, so the top and bottom are cut off"
                           + " - exactly as they would be while recording.";
            InsetSchematicCaption.Text = caption;
        }

        /// <summary>
        /// A plain suggestion of screen content behind the inset - a title bar and a body - so the
        /// box reads as "the recording" rather than as an empty rectangle. It carries no information:
        /// everything this schematic has to say is said by where the inset sits and how large it is.
        /// </summary>
        private static Geometry ScreenMotif(double boxWidth, double boxHeight)
        {
            double margin = boxWidth * 0.06;
            double barHeight = boxHeight * 0.10;
            var motif = new GeometryGroup();
            motif.Children.Add(new RectangleGeometry(
                new Rect(margin, margin, boxWidth - 2 * margin, barHeight), 2, 2));
            motif.Children.Add(new RectangleGeometry(
                new Rect(margin, margin + barHeight * 1.4,
                         boxWidth - 2 * margin, boxHeight - 2 * margin - barHeight * 1.4), 2, 2));
            motif.Freeze();
            return motif;
        }

        /// <summary>
        /// What the inset is filled with: the live camera picture when there is one, cropped exactly
        /// as the HUD will crop it, and a flat panel colour when there is not.
        ///
        /// NOTHING IS ASSUMED ABOUT THE CAMERA'S SHAPE. The preview buffer is a fixed 320x240 with
        /// the camera's picture letterboxed inside it by ffmpeg's pad filter, so the circle's viewbox
        /// - which is expressed in the CAMERA's own frame - has to be mapped through that padding.
        /// Skipping that mapping would fill the circle with the black bars of any camera that is not
        /// 4:3, and it would look entirely convincing while doing it.
        /// </summary>
        private Brush InsetSchematicFill(bool circle, CameraFrameSize? camera)
        {
            if (_previewBitmap == null || camera is not { } cam) return (Brush)FindResource("DkBorder");

            var inner = OverlayFit.Contain(FfmpegCameraPreview.FrameWidth, FfmpegCameraPreview.FrameHeight,
                                           cam.Width, cam.Height);
            var crop = circle
                ? ReadOverlay().Circle.Viewbox(cam.Width, cam.Height)
                : new OverlayRect(0, 0, 1, 1);

            var viewbox = new Rect(
                (inner.X + crop.X * inner.Width) / FfmpegCameraPreview.FrameWidth,
                (inner.Y + crop.Y * inner.Height) / FfmpegCameraPreview.FrameHeight,
                crop.Width * inner.Width / FfmpegCameraPreview.FrameWidth,
                crop.Height * inner.Height / FfmpegCameraPreview.FrameHeight);

            // Not frozen: the source is the WriteableBitmap the preview writes into, so the picture
            // inside the schematic stays live without rebuilding the brush ten times a second.
            return new ImageBrush(_previewBitmap)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = viewbox,
            };
        }

        private void InsetSchematic_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_overlayReady) return;
            try { UpdateOverlayUi(); }
            catch (Exception ex) { Log.Error("[PresetEditor] InsetSchematic_SizeChanged FAILED", ex); }
        }

        private void Overlay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingOverlay) return;
            try { UpdateOverlayUi(); }
            catch (Exception ex) { Log.Error("[PresetEditor] Overlay_Changed FAILED", ex); }
        }

        private void OverlayShape_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingOverlay || !_overlayReady) return;
            try
            {
                Log.Info($"[PresetEditor] OverlayShape_Changed: circle={OverlayShapeCircle.IsChecked == true}");
                UpdateOverlayUi();
            }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayShape_Changed FAILED", ex); }
        }

        private void OverlayCorner_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingOverlay || !_overlayReady) return;
            // Issue #43: the corner moves the inset in the recording schematic. It used to redraw
            // nothing at all - the corner said where the inset sits on the RECORDING, and this dialog
            // drew only the camera frame - which made a real choice look like a dead control.
            try
            {
                Log.Info($"[PresetEditor] OverlayCorner_Changed: corner={PreviewNames.Text(SelectedOverlayCorner())}");
                UpdateOverlayUi();
            }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayCorner_Changed FAILED", ex); }
        }

        private void OverlayReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Info("[PresetEditor] OverlayReset_Click: back to the default framing");
                LoadOverlayFrom(new CameraOverlaySettings());
            }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayReset_Click FAILED", ex); }
        }

        private void OverlayAdorner_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_overlayReady) return;
            try { UpdateOverlayUi(); }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayAdorner_SizeChanged FAILED", ex); }
        }

        private void OverlayAdorner_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_overlayReady) return;
            try
            {
                if (OverlayShapeCircle.IsChecked != true || PreviewContentRect() == null) return;
                _draggingCircle = true;
                CameraOverlayAdorner.CaptureMouse();
                MoveCircleTo(e.GetPosition(CameraOverlayAdorner));
            }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayAdorner_MouseDown FAILED", ex); }
        }

        private void OverlayAdorner_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingCircle) return;
            try { MoveCircleTo(e.GetPosition(CameraOverlayAdorner)); }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayAdorner_MouseMove FAILED", ex); }
        }

        private void OverlayAdorner_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingCircle) return;
            try
            {
                _draggingCircle = false;
                CameraOverlayAdorner.ReleaseMouseCapture();
                Log.Info($"[PresetEditor] circle placed at {ReadOverlay().Circle}");
            }
            catch (Exception ex) { Log.Error("[PresetEditor] OverlayAdorner_MouseUp FAILED", ex); }
        }

        /// <summary>Put the circle's centre where the pointer is. The SLIDERS stay the authoritative
        /// value - this sets them, and their handler redraws - so clicking the picture and dragging a
        /// slider cannot end up meaning two different things.</summary>
        private void MoveCircleTo(Point pointInPane)
        {
            if (PreviewContentRect() is not { } area) return;
            CircleXSlider.Value = CameraOverlayCircle.Clamp((pointInPane.X - area.X) / area.Width, 0, 1);
            CircleYSlider.Value = CameraOverlayCircle.Clamp((pointInPane.Y - area.Y) / area.Height, 0, 1);
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
            // Issue #36: the overlay frames the camera, so it is a video-mode setting too. Disabled
            // (but KEPT) for a shot/audio preset, exactly like the camera picker above it.
            OverlayControls.IsEnabled = mode == "video";
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
