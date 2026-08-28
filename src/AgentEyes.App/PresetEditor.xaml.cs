using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AgentEyes;
using AgentEyes.Audio;
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

        /// <summary>The preset to persist once the dialog returns true (the edited instance, or a new one for Save as).</summary>
        internal CapturePreset? SavedPreset { get; private set; }

        internal PresetEditor(CapturePreset preset)
        {
            _preset = preset;
            _savedCamera = preset.Camera;
            InitializeComponent();
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            try { PopulateDevices(); LoadFrom(preset); UpdateModeUi(); }
            catch (Exception ex) { ErrorText.Text = "Init error: " + ex.Message; }

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
            Log.Info($"[PresetEditor] LoadCamerasAsync: {cameras.Count} camera(s) listed, selected index {select}");
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

        private void Mode_Changed(object sender, RoutedEventArgs e) { if (IsInitialized) UpdateModeUi(); }

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
