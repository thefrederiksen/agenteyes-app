using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentEyes.LinuxApp.Model;
using AgentEyes.LinuxApp.Services;
using AgentEyes.LinuxApp.ViewModels;
using AgentEyes.Transcription;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AgentEyes.LinuxApp.Views
{
    /// <summary>
    /// The AgentEyes main window on Linux (issue #107) - the Windows MainWindow's rail, top bar and
    /// Record / Library / Dictionary views. Every disk read and every service call runs off the UI
    /// thread; the window shows at once and fills in.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppState _state = AppState.Current;

        // Library: every entry read from disk, and the filtered view the list shows.
        private readonly Dictionary<string, LibraryItem> _byId = new(StringComparer.Ordinal);
        private List<LibraryItem> _all = new();
        private readonly ObservableCollection<LibraryItem> _view = new();
        private HashSet<string> _pending = new(StringComparer.Ordinal);
        private bool _scanRunning, _scanAgain, _libraryLoaded;
        private FileSystemWatcher? _watcher;
        private DispatcherTimer? _scanDebounce;
        private int _lastPendingCount = -1;
        private string? _lastRecordingDir;

        // Record view
        private bool _populatingSetup;
        private DeviceList? _devices;
        private bool _devicesLoading;

        // Dictionary
        private List<DictRow> _dictRows = new();

        private PlayerWindow? _player;

        public MainWindow()
        {
            InitializeComponent();
            Log.Info("[MainWindow] ctor");
            RecentList.ItemsSource = _view;
            ApplyLibraryMode(_state.Settings.LibraryMode);
            SearchBox.TextChanged += (_, _) => ApplyFilter();
            DictSearchBox.TextChanged += (_, _) => RenderDictionary();
            RecentList.AddHandler(PointerReleasedEvent, RecentList_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

            _state.Changed += Render;
            _state.RecordingSaved += dir =>
            {
                StatusText.Text = $"Saved {Path.GetFileName(dir)} - transcribing and titling follow.";
                RequestScan();
            };
            _state.Failed += msg =>
            {
                StatusText.Text = msg;
                _ = Dialogs.InfoAsync(this, "AgentEyes", msg);
            };

            Opened += (_, _) =>
            {
                Log.Info("[MainWindow] Opened");
                Render();
                _ = LoadDevicesAsync();
                StartWatchingLibrary();
                RequestScan();
            };
        }

        // =============================================================================== shell

        private void Rail_Changed(object? sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { IsChecked: true } rb) return;
            string view = rb.Tag as string ?? "Record";
            Log.Info($"[MainWindow] Rail_Changed: {view}");
            ViewTitle.Text = view;
            RecordPanel.IsVisible = view == "Record";
            LibraryPanel.IsVisible = view == "Library";
            DictionaryPanel.IsVisible = view == "Dictionary";
            LibraryControls.IsVisible = view == "Library";
            if (view == "Library") RequestScan();
            if (view == "Dictionary") _ = LoadDictionaryAsync();
            if (view == "Record" && _devices == null) _ = LoadDevicesAsync();
        }

        private void GoRecord_Click(object? sender, RoutedEventArgs e) => RailRecord.IsChecked = true;

        /// <summary>A launch command (issue #107): open a view, a recording's details or preview, or settings.</summary>
        internal async void RunCommand(string verb, string arg)
        {
            Log.Info($"[MainWindow] RunCommand: {verb} {arg}");
            switch (verb)
            {
                case "view":
                    (arg switch { "library" => RailLibrary, "dictionary" => RailDictionary, _ => RailRecord }).IsChecked = true;
                    return;
                case "settings":
                    OpenSettings(arg == "account" ? "Account" : "General");
                    return;
                case "details":
                case "preview":
                {
                    RailLibrary.IsChecked = true;
                    for (int i = 0; i < 40 && (_scanRunning || !_libraryLoaded); i++) await Task.Delay(100);
                    if (!_byId.TryGetValue(arg, out var item)) { StatusText.Text = "No recording with id " + arg; return; }
                    if (verb == "details") OpenDetails(item); else OpenPreview(item);
                    return;
                }
            }
        }

        private void Render()
        {
            var st = _state.Status;
            bool recording = _state.IsRecording;

            RecordText.Text = recording ? "STOP" : "REC";
            RecordButton.Classes.Set("stop", recording);
            RecordButton.IsEnabled = st != null && !_state.Busy;
            ElapsedText.Text = recording && st != null ? "REC " + TrayController.Elapsed(st.ElapsedSeconds) : _state.Busy ? "..." : "";
            LevelPanel.IsVisible = recording;
            CaptureChips.IsVisible = !recording;
            CaptureScreenText.IsVisible = !recording;
            SetupCard.IsEnabled = !recording && !_state.Busy;
            if (recording && st != null)
            {
                // The Windows meters show the peak x180, clamped (MainWindow.UpdateLevels).
                MicLevelBar.Value = Math.Min(100, st.MicLevel * 180);
                SysLevelBar.Value = Math.Min(100, st.SystemLevel * 180);
            }

            OfflineBanner.IsVisible = st == null && _state.OfflineReason != null;
            OfflineText.Text = "The AgentEyes recorder service is not running, so nothing can be recorded. " + (_state.OfflineReason ?? "");
            RailAccountBadge.IsVisible = st != null && !st.SignedIn;
            RailAccountLabel.Text = st == null ? "Account" : st.SignedIn ? "Signed in" : "Sign in";
            ToolTip.SetTip(RailAccount, st == null ? "DevThrottle account" : st.SignedIn ? "Signed in as " + st.Email : "Not signed in - recordings are not transcribed");

            if (st != null)
            {
                if (_devices == null && !_devicesLoading) _ = LoadDevicesAsync();
                // A recording finished processing (or a new one is being made): the library changes.
                if (st.PendingTranscriptions != _lastPendingCount || st.Dir != _lastRecordingDir)
                {
                    _lastPendingCount = st.PendingTranscriptions;
                    _lastRecordingDir = st.Dir;
                    RequestScan();
                }
            }
        }

        // =============================================================================== record

        private async void Record_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (_state.IsRecording)
                {
                    StatusText.Text = "Stopping and saving...";
                    await _state.StopRecordingAsync();
                    return;
                }
                StatusText.Text = "Starting...";
                RecordButton.IsEnabled = false;
                await _state.StartRecordingAsync();
                if (_state.IsRecording)
                {
                    StatusText.Text = "Recording - stop from here or from the tray icon.";
                    // Out of the way of what is being recorded, like the Windows app.
                    WindowState = WindowState.Minimized;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] Record_Click FAILED", ex);
                StatusText.Text = "Record error: " + ex.Message;
            }
        }

        private async Task LoadDevicesAsync()
        {
            if (_devicesLoading) return;
            _devicesLoading = true;
            CaptureScreenText.Text = "Reading the screens and microphones...";
            try
            {
                var d = await _state.Client.GetDevicesAsync();
                _devices = d;
                PopulateSetup(d);
            }
            catch (ControlApiException ex)
            {
                Log.Warn($"[MainWindow] LoadDevicesAsync: {ex.Message}");
                CaptureScreenText.Text = "Devices unavailable: " + ex.Message;
            }
            finally
            {
                _devicesLoading = false;
            }
        }

        private void PopulateSetup(DeviceList d)
        {
            _populatingSetup = true;
            try
            {
                var s = _state.Settings;
                ScreenCombo.ItemsSource = d.Monitors.Select(m => m.Label).ToList();
                int screenIdx = d.Monitors.ToList().FindIndex(m => m.Index == s.Screen);
                ScreenCombo.SelectedIndex = screenIdx >= 0 ? screenIdx : d.Monitors.ToList().FindIndex(m => m.Primary) is int p && p >= 0 ? p : 0;

                var mics = new List<string> { "No microphone" };
                mics.AddRange(d.Mics);
                MicCombo.ItemsSource = mics;
                // First run: the first microphone the audio server lists (its default). Afterwards the remembered one.
                string? mic = s.Mic == null ? d.Mics.FirstOrDefault() : s.Mic;
                MicCombo.SelectedIndex = string.IsNullOrEmpty(mic) ? 0 : Math.Max(0, mics.IndexOf(mic));
                SystemToggle.IsChecked = s.SystemSound;
            }
            finally
            {
                _populatingSetup = false;
            }
            SaveSetup();
        }

        private void Setup_Changed(object? sender, RoutedEventArgs e)
        {
            if (_populatingSetup || _devices == null) return;
            SaveSetup();
        }

        private void SaveSetup()
        {
            var s = _state.Settings;
            if (_devices != null && ScreenCombo.SelectedIndex >= 0 && ScreenCombo.SelectedIndex < _devices.Monitors.Count)
                s.Screen = _devices.Monitors[ScreenCombo.SelectedIndex].Index;
            s.Mic = MicCombo.SelectedIndex <= 0 ? "" : MicCombo.SelectedItem as string;
            s.SystemSound = SystemToggle.IsChecked == true;
            try { s.Save(); }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] SaveSetup FAILED", ex);
                StatusText.Text = "Could not save the capture setup: " + ex.Message;
            }
            RenderCaptureSummary();
        }

        private void RenderCaptureSummary()
        {
            var s = _state.Settings;
            var mon = _devices?.Monitors.FirstOrDefault(m => m.Index == s.Screen);
            bool mic = !string.IsNullOrEmpty(s.Mic);
            CaptureChips.Children.Clear();
            CaptureChips.Children.Add(CaptureCard(MonitorIcon, "VIDEO", "#4CC2FF", "30 fps", $"Monitor {s.Screen}", true));
            CaptureChips.Children.Add(CaptureCard(MicIcon, "MIC", "#22C55E", mic ? Short(s.Mic!) : "no microphone", mic ? "100%" : "", mic));
            CaptureChips.Children.Add(CaptureCard(SpeakerIcon, "SYSTEM", "#22C55E", s.SystemSound ? "what you hear" : "not recorded", s.SystemSound ? "70%" : "", s.SystemSound));
            CaptureScreenText.Text = mon == null ? "" : $"Capturing Monitor {mon.Index}  -  {mon.Width} x {mon.Height}  (full screen)";
            RenderMonitorStrip();
        }

        private static readonly Geometry MonitorIcon = Geometry.Parse("M2,3 H18 V13 H2 Z M8,16 H12 M6,18 H14");
        private static readonly Geometry MicIcon = Geometry.Parse("M10,2 C11.7,2 13,3.3 13,5 V10 C13,11.7 11.7,13 10,13 C8.3,13 7,11.7 7,10 V5 C7,3.3 8.3,2 10,2 Z M4,10 C4,13.3 6.7,16 10,16 C13.3,16 16,13.3 16,10 M10,16 V19");
        private static readonly Geometry SpeakerIcon = Geometry.Parse("M2,7 H5 L10,3 V17 L5,13 H2 Z M13,7 C14.5,8.5 14.5,11.5 13,13 M15.5,4.5 C18.5,7.5 18.5,12.5 15.5,15.5");

        private static string Short(string s) => s.Length <= 18 ? s : s[..17] + "...";

        /// <summary>One card of the Windows capture summary: icon, coloured title, two detail lines, ON / OFF pill.</summary>
        private static Control CaptureCard(Geometry icon, string title, string color, string line1, string line2, bool on)
        {
            var accent = new SolidColorBrush(Color.Parse(on ? color : "#6B7178"));
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = icon, Stroke = accent, StrokeThickness = 1.6, Width = 18, Height = 18, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
            });
            stack.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = accent });
            stack.Children.Add(new TextBlock { Text = line1, FontSize = 11, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = line2, FontSize = 11, Classes = { "dim" } });
            stack.Children.Add(new Border
            {
                BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 0), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = on ? "ON" : "OFF", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = accent },
            });
            return new Border
            {
                Width = 124,
                Background = (IBrush)Application.Current!.FindResource("RdSurface")!,
                BorderBrush = on ? accent : (IBrush)Application.Current!.FindResource("RdStroke")!,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(13, 12),
                Child = stack,
            };
        }

        /// <summary>The monitors to scale, the one being recorded highlighted (the Windows monitor strip).</summary>
        private void RenderMonitorStrip()
        {
            MonitorStrip.Children.Clear();
            var mons = _devices?.Monitors;
            if (mons == null || mons.Count == 0) return;
            double left = mons.Min(m => (double)m.X), top = mons.Min(m => (double)m.Y);
            double right = mons.Max(m => (double)m.X + m.Width), bottom = mons.Max(m => (double)m.Y + m.Height);
            double scale = Math.Min(MonitorStrip.Width / (right - left), MonitorStrip.Height / (bottom - top));
            double offX = (MonitorStrip.Width - (right - left) * scale) / 2, offY = (MonitorStrip.Height - (bottom - top) * scale) / 2;
            foreach (var m in mons)
            {
                bool sel = m.Index == _state.Settings.Screen;
                var box = new Border
                {
                    Width = m.Width * scale - 4, Height = m.Height * scale - 4,
                    Background = new SolidColorBrush(Color.Parse(sel ? "#1E3442" : "#24262B")),
                    BorderBrush = new SolidColorBrush(Color.Parse(sel ? "#4CC2FF" : "#33363D")),
                    BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(2),
                    Child = new TextBlock
                    {
                        Text = m.Index.ToString(), FontSize = 11, FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse(sel ? "#4CC2FF" : "#6B7178")),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Canvas.SetLeft(box, offX + (m.X - left) * scale + 2);
                Canvas.SetTop(box, offY + (m.Y - top) * scale + 2);
                MonitorStrip.Children.Add(box);
            }
        }

        private void StartService_Click(object? sender, RoutedEventArgs e)
        {
            Log.Info("[MainWindow] StartService_Click");
            try
            {
                var psi = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardError = true };
                foreach (var a in new[] { "--user", "start", "agenteyes.service" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                StatusText.Text = "Starting the recorder service...";
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] StartService_Click FAILED", ex);
                StatusText.Text = "Could not start the service: " + ex.Message;
            }
        }

        // =============================================================================== library

        private void StartWatchingLibrary()
        {
            string root = RecordingPaths.Root;
            Directory.CreateDirectory(root);
            _scanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _scanDebounce.Tick += (_, _) => { _scanDebounce.Stop(); RequestScan(); };
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler onChange = (_, e) =>
            {
                string name = Path.GetFileName(e.FullPath);
                // Only the files the library shows from; ffmpeg writing the video is not a change.
                if (e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted && Path.GetDirectoryName(e.FullPath) == root
                    || name is "manifest.json" or "thumb.jpg" or "walkthrough.html" or "transcript.json" or "transcript.txt")
                    Dispatcher.UIThread.Post(() => { _scanDebounce.Stop(); _scanDebounce.Start(); });
            };
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Changed += onChange;
            _watcher.Renamed += (s, e) => onChange(s, e);
            _watcher.Error += (_, e) => Log.Error("[MainWindow] library watcher FAILED", e.GetException());
            _watcher.EnableRaisingEvents = true;
            Log.Info($"[MainWindow] StartWatchingLibrary: {root}");
        }

        private void RefreshLibrary_Click(object? sender, RoutedEventArgs e) => RequestScan();

        /// <summary>Re-read the library off the UI thread; a request during a scan runs one more after it.</summary>
        private async void RequestScan()
        {
            if (_scanRunning) { _scanAgain = true; return; }
            _scanRunning = true;
            try
            {
                do
                {
                    _scanAgain = false;
                    var sw = Stopwatch.StartNew();
                    var (entries, pending) = await Task.Run(() =>
                    {
                        var list = LibraryScanner.Scan(RecordingPaths.Root);
                        return (list, LibraryScanner.Pending(list));
                    });
                    Log.Info($"[MainWindow] RequestScan: {entries.Count} recording(s), {pending.Count} pending, {sw.ElapsedMilliseconds} ms");
                    ApplyScan(entries, pending);
                } while (_scanAgain);
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] RequestScan FAILED", ex);
                StatusText.Text = "Could not read the library: " + ex.Message;
            }
            finally
            {
                _scanRunning = false;
                _libraryLoaded = true;
                LibraryLoading.IsVisible = false;
            }
        }

        private void ApplyScan(List<LibraryEntry> entries, HashSet<string> pending)
        {
            _pending = pending;
            var next = new List<LibraryItem>(entries.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                seen.Add(e.Id);
                if (_byId.TryGetValue(e.Id, out var item))
                {
                    if (item.Entry.Signature != e.Signature) item.Update(e);
                }
                else
                {
                    item = new LibraryItem(e);
                    _byId[e.Id] = item;
                    _ = item.LoadThumbAsync();
                }
                next.Add(item);
            }
            foreach (var gone in _byId.Keys.Where(k => !seen.Contains(k)).ToList()) _byId.Remove(gone);
            _all = next;
            foreach (var item in _all) item.Status = StatusFor(item);
            ApplyFilter();
        }

        private string StatusFor(LibraryItem item)
        {
            var st = _state.Status;
            if (st != null && st.IsRecording && st.Dir != null && string.Equals(st.Dir.TrimEnd('/'), item.Dir.TrimEnd('/'), StringComparison.Ordinal))
                return "Recording...";
            if (_pending.Contains(item.Dir) && st?.SignedIn == true) return "Transcribing...";
            return "";
        }

        /// <summary>Show the entries matching the search, in library order, moving rows rather than
        /// rebuilding so selection and scroll position survive a refresh.</summary>
        private void ApplyFilter()
        {
            string q = (SearchBox.Text ?? "").Trim();
            var target = q.Length == 0 ? _all : _all.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            for (int i = 0; i < target.Count; i++)
            {
                if (i < _view.Count && ReferenceEquals(_view[i], target[i])) continue;
                int at = _view.IndexOf(target[i]);
                if (at >= 0) _view.Move(at, i);
                else _view.Insert(i, target[i]);
            }
            while (_view.Count > target.Count) _view.RemoveAt(_view.Count - 1);

            LibraryCountText.Text = q.Length == 0 ? $"{_all.Count} recording{(_all.Count == 1 ? "" : "s")}" : $"{target.Count} of {_all.Count}";
            bool empty = _libraryLoaded || _all.Count > 0 ? target.Count == 0 : false;
            EmptyState.IsVisible = empty;
            EmptyTitle.Text = q.Length > 0 ? "No recordings match." : "No recordings yet.";
            EmptyText.Text = q.Length > 0 ? $"Nothing has \"{q}\" in its title." : "Everything you capture lands here, transcribed and titled automatically.";
            EmptyButton.IsVisible = q.Length == 0;
        }

        private void GridMode_Click(object? sender, RoutedEventArgs e) => SetLibraryMode("grid");

        private void ListMode_Click(object? sender, RoutedEventArgs e) => SetLibraryMode("list");

        private void SetLibraryMode(string mode)
        {
            Log.Info($"[MainWindow] SetLibraryMode: {mode}");
            ApplyLibraryMode(mode);
            _state.Settings.LibraryMode = mode;
            try { _state.Settings.Save(); }
            catch (Exception ex) { Log.Error("[MainWindow] SetLibraryMode: save FAILED", ex); }
        }

        private void ApplyLibraryMode(string mode)
        {
            bool grid = mode != "list";
            RecentList.Classes.Set("grid", grid);
            RecentList.Classes.Set("list", !grid);
            RecentList.ItemTemplate = (IDataTemplate)Resources[grid ? "CardTemplate" : "RowTemplate"]!;
            RecentList.ItemsPanel = grid
                ? new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal })
                : new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
            GridModeButton.Opacity = grid ? 1 : 0.45;
            ListModeButton.Opacity = grid ? 0.45 : 1;
        }

        private void RecentList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            int n = RecentList.SelectedItems?.Count ?? 0;
            if (n > 1) StatusText.Text = $"{n} selected - Delete removes them";
        }

        /// <summary>A plain left click on a card opens Preview (the Windows behaviour); Ctrl/Shift clicks select.</summary>
        private void RecentList_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            if (e.Source is not Visual v) return;
            // Clicks on the card's own buttons (folder, chips, ...) are theirs.
            if (v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
            if (v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: LibraryItem item }) return;
            RecentList.SelectedItems?.Clear();
            RecentList.SelectedItem = item;
            if (item.CanPreview) OpenPreview(item);
            else OpenDetails(item);
        }

        private void Card_DoubleTapped(object? sender, TappedEventArgs e) { }

        private async void RecentList_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            var selected = RecentList.SelectedItems?.OfType<LibraryItem>().ToList() ?? new List<LibraryItem>();
            if (selected.Count == 0) return;
            e.Handled = true;
            await DeleteAsync(selected);
        }

        private static LibraryItem? ItemFrom(object? sender) => (sender as StyledElement)?.DataContext as LibraryItem;

        private void More_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c) return;
            var card = c.FindAncestorOfType<Grid>();
            while (card != null && card.ContextMenu == null) card = card.FindAncestorOfType<Grid>();
            card?.ContextMenu?.Open(c);
        }

        private void Preview_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenPreview(item);
        }

        private void Details_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenDetails(item);
        }

        internal void OpenPreview(LibraryItem item)
        {
            Log.Info($"[MainWindow] OpenPreview: {item.Id}");
            if (item.Entry.MediaPath == null) { StatusText.Text = "Nothing to play for this recording yet."; return; }
            try
            {
                _player?.Close();
                _player = new PlayerWindow(item.Entry);
                _player.Closed += (_, _) => _player = null;
                _player.Show(this);
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] OpenPreview FAILED", ex);
                StatusText.Text = "Could not open the player: " + ex.Message;
            }
        }

        internal void OpenDetails(LibraryItem item)
        {
            Log.Info($"[MainWindow] OpenDetails: {item.Id}");
            var w = new DetailWindow(item.Entry, this);
            w.Show(this);
        }

        private async void Rename_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item) return;
            string? name = await Dialogs.PromptAsync(this, "Rename recording", "New name:", item.Title);
            if (name == null || name == item.Title) return;
            await RenameAsync(item.Entry, name);
        }

        internal async Task<bool> RenameAsync(LibraryEntry entry, string name)
        {
            StatusText.Text = "Renaming...";
            try
            {
                await _state.Client.RenameAsync(entry.Id, name);
                StatusText.Text = $"Renamed to \"{name}\".";
                RequestScan();
                return true;
            }
            catch (ControlApiException ex)
            {
                Log.Error($"[MainWindow] RenameAsync FAILED: {entry.Id}", ex);
                StatusText.Text = "Rename error: " + ex.Message;
                return false;
            }
        }

        private async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item) return;
            var selected = RecentList.SelectedItems?.OfType<LibraryItem>().ToList() ?? new List<LibraryItem>();
            await DeleteAsync(selected.Contains(item) && selected.Count > 1 ? selected : new List<LibraryItem> { item });
        }

        internal async Task<bool> DeleteAsync(IReadOnlyList<LibraryItem> items)
        {
            string prompt = items.Count == 1
                ? $"Delete \"{items[0].Title}\" and all its files?"
                : $"Delete {items.Count} recordings and all their files?";
            if (!await Dialogs.ConfirmAsync(this, "Delete", prompt)) return false;
            _player?.Close();   // release the playback handle into these folders first

            int deleted = 0;
            string? firstError = null;
            StatusText.Text = items.Count == 1 ? $"Deleting \"{items[0].Title}\"..." : $"Deleting {items.Count} recordings...";
            foreach (var item in items)
            {
                try
                {
                    await _state.Client.DeleteAsync(item.Id);
                    deleted++;
                    _byId.Remove(item.Id);
                    _all.Remove(item);
                }
                catch (ControlApiException ex)
                {
                    Log.Error($"[MainWindow] DeleteAsync FAILED: {item.Id}", ex);
                    firstError ??= ex.StatusCode == 409 ? $"\"{item.Title}\" is still being worked on - try again when it is done." : ex.Message;
                }
            }
            ApplyFilter();
            StatusText.Text = firstError != null
                ? (items.Count == 1 ? firstError : $"Deleted {deleted} of {items.Count} - {firstError}")
                : items.Count == 1 ? $"Deleted \"{items[0].Title}\"." : $"Deleted {deleted} recordings.";
            RequestScan();
            return firstError == null;
        }

        private void OpenFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenPath(item.Dir);
        }

        private void OpenRecordings_Click(object? sender, RoutedEventArgs e) => TrayController.OpenRecordingsFolder();

        private void OpenWalkthrough_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenPath(Path.Combine(item.Dir, "walkthrough.html"));
        }

        internal void OpenPath(string path)
        {
            try { Shell.Open(path); }
            catch (Exception ex)
            {
                Log.Error($"[MainWindow] OpenPath FAILED: {path}", ex);
                StatusText.Text = "Could not open: " + ex.Message;
            }
        }

        private async void BuildWalkthrough_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) await BuildWalkthroughAsync(item.Entry);
        }

        internal async Task BuildWalkthroughAsync(LibraryEntry entry)
        {
            StatusText.Text = $"Building the walkthrough for \"{entry.Title}\"...";
            try
            {
                string file = await _state.Client.BuildWalkthroughAsync(entry.Id);
                StatusText.Text = "Walkthrough built.";
                OpenPath(file);
                RequestScan();
            }
            catch (ControlApiException ex)
            {
                Log.Error($"[MainWindow] BuildWalkthroughAsync FAILED: {entry.Id}", ex);
                StatusText.Text = "Walkthrough error: " + ex.Message;
            }
        }

        private async void ExportSubtitles_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item) return;
            try
            {
                var langs = await Task.Run(() => RecordingLibrary.TranscriptLanguages(item.Id) ?? Array.Empty<string>());
                if (langs.Count == 0) { StatusText.Text = "This recording has no transcript to burn in."; return; }
                string? lang = langs.Count == 1 ? langs[0]
                    : await Dialogs.ChooseAsync(this, "Export with subtitles", "Subtitle language:", langs.ToList());
                if (lang == null) return;
                StatusText.Text = $"Exporting \"{item.Title}\" with {lang} subtitles - this takes a while...";
                string file = await _state.Client.ExportSubtitlesAsync(item.Id, lang);
                StatusText.Text = $"Exported {Path.GetFileName(file)}.";
                OpenPath(item.Dir);
            }
            catch (ControlApiException ex)
            {
                Log.Error($"[MainWindow] ExportSubtitles FAILED: {item.Id}", ex);
                StatusText.Text = "Export error: " + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error($"[MainWindow] ExportSubtitles FAILED: {item.Id}", ex);
                StatusText.Text = "Export error: " + ex.Message;
            }
        }

        // =============================================================================== dictionary

        private sealed class DictRow
        {
            public string Term { get; set; } = "";
            public List<string> Variants { get; } = new();
        }

        private async Task LoadDictionaryAsync()
        {
            Log.Info("[MainWindow] LoadDictionaryAsync");
            try
            {
                var dict = await Task.Run(() => DictionaryStore.Load());
                _dictRows = dict.Vocabulary.Concat(dict.CommonMistranscriptions.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .Select(t =>
                    {
                        var row = new DictRow { Term = t };
                        if (dict.CommonMistranscriptions.TryGetValue(t, out var v)) row.Variants.AddRange(v);
                        return row;
                    }).ToList();
                RenderDictionary();
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] LoadDictionaryAsync FAILED", ex);
                StatusText.Text = "Could not read the dictionary: " + ex.Message;
            }
        }

        private async Task SaveDictionaryAsync()
        {
            var dict = new TranscriptionDictionary(
                _dictRows.Select(r => r.Term).ToList(),
                _dictRows.Where(r => r.Variants.Count > 0).ToDictionary(r => r.Term, r => (IReadOnlyList<string>)r.Variants.ToList()));
            try
            {
                await Task.Run(() => DictionaryStore.Save(DictionaryStore.DefaultPath, dict));
                Log.Info($"[MainWindow] SaveDictionaryAsync: {_dictRows.Count} term(s)");
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] SaveDictionaryAsync FAILED", ex);
                StatusText.Text = "Could not save the dictionary: " + ex.Message;
            }
            RenderDictionary();
        }

        private void RenderDictionary()
        {
            string q = (DictSearchBox.Text ?? "").Trim();
            var rows = q.Length == 0 ? _dictRows
                : _dictRows.Where(r => r.Term.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || r.Variants.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            var panel = new StackPanel { Spacing = 6 };
            foreach (var r in rows) panel.Children.Add(DictRowView(r));
            DictList.ItemsSource = null;
            DictList.Items.Clear();
            DictList.Items.Add(panel);
            DictEmpty.IsVisible = rows.Count == 0;
            DictEmpty.Text = q.Length > 0 ? "No terms match." : "No terms yet.";
        }

        private Control DictRowView(DictRow r)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = r.Term, FontSize = 13, FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = r.Variants.Count == 0 ? "no misheard variants" : "misheard as: " + string.Join(", ", r.Variants),
                FontSize = 11, Classes = { "dim" }, TextWrapping = TextWrapping.Wrap,
            });
            grid.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(buttons, 1);
            var add = new Button { Content = "+ misheard", Classes = { "mini" } };
            add.Click += async (_, _) =>
            {
                string? v = await Dialogs.PromptAsync(this, "Misheard as", $"How \"{r.Term}\" gets misheard:", "");
                if (v == null || r.Variants.Contains(v, StringComparer.Ordinal)) return;
                r.Variants.Add(v);
                await SaveDictionaryAsync();
            };
            var edit = new Button { Content = "Edit", Classes = { "mini" } };
            edit.Click += async (_, _) =>
            {
                string? t = await Dialogs.PromptAsync(this, "Edit term", "Term:", r.Term);
                if (t == null) return;
                string? v = await Dialogs.PromptAsync(this, "Edit term", "Misheard as (comma separated, empty for none):", string.Join(", ", r.Variants));
                r.Term = t;
                r.Variants.Clear();
                if (v != null) r.Variants.AddRange(v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));
                await SaveDictionaryAsync();
            };
            var del = new Button { Content = "Delete", Classes = { "mini" } };
            del.Click += async (_, _) =>
            {
                if (!await Dialogs.ConfirmAsync(this, "Delete term", $"Delete \"{r.Term}\" from the dictionary?")) return;
                _dictRows.Remove(r);
                await SaveDictionaryAsync();
            };
            buttons.Children.Add(add);
            buttons.Children.Add(edit);
            buttons.Children.Add(del);
            grid.Children.Add(buttons);
            return new Border
            {
                Background = (IBrush)Application.Current!.FindResource("RdSurface")!,
                BorderBrush = (IBrush)Application.Current!.FindResource("RdStroke")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 9),
                Child = grid,
            };
        }

        private async void AddTerm_Click(object? sender, RoutedEventArgs e) => await AddTermAsync();

        private async void NewTermBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await AddTermAsync();
        }

        private async Task AddTermAsync()
        {
            string term = (NewTermBox.Text ?? "").Trim();
            if (term.Length == 0) return;
            if (_dictRows.Any(r => string.Equals(r.Term, term, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = $"\"{term}\" is already in the dictionary.";
                return;
            }
            Log.Info($"[MainWindow] AddTermAsync: {term}");
            _dictRows.Add(new DictRow { Term = term });
            _dictRows = _dictRows.OrderBy(r => r.Term, StringComparer.OrdinalIgnoreCase).ToList();
            NewTermBox.Text = "";
            StatusText.Text = $"Added \"{term}\".";
            await SaveDictionaryAsync();
        }

        // =============================================================================== account / settings / about

        private void Account_Click(object? sender, RoutedEventArgs e) => OpenSettings("Account");

        private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings("General");

        private void OpenSettings(string tab)
        {
            Log.Info($"[MainWindow] OpenSettings: {tab}");
            new SettingsWindow(tab).ShowDialog(this);
        }

        private async void About_Click(object? sender, RoutedEventArgs e)
        {
            string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
            await Dialogs.InfoAsync(this, "About AgentEyes",
                $"AgentEyes {version} for Linux\n\nRecordings: {RecordingPaths.Root}\nData and logs: {AppDataPaths.Root}\n"
                + $"Recorder service: {ControlClient.BaseUrl}");
        }

        protected override void OnClosed(EventArgs e)
        {
            _watcher?.Dispose();
            _state.Changed -= Render;
            base.OnClosed(e);
        }
    }
}
