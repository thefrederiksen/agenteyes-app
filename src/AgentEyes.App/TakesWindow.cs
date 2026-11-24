using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AgentEyes.Takes;
using AgentEyes.Video;

namespace AgentEyes.App
{
    /// <summary>
    /// Issue #230, screen 1 - the SET-UP window. The take list with the full text of every take, its
    /// length, instructions, background and the attempts recorded so far (chosen one marked); tick
    /// what to record; a small live camera view; the camera and microphone; reading mode; which
    /// screen to record on; Start. Everything that can be changed is here and only here - the
    /// recording window (<see cref="TakeRecordingWindow"/>) has nothing to change.
    /// </summary>
    internal sealed class TakesWindow : Window
    {
        internal static readonly Brush Bg = Frozen(0x0D, 0x11, 0x17);
        internal static readonly Brush Panel = Frozen(0x16, 0x1B, 0x22);
        internal static readonly Brush Border = Frozen(0x30, 0x36, 0x3D);
        internal static readonly Brush Text = Frozen(0xE6, 0xED, 0xF3);
        internal static readonly Brush Dim = Frozen(0x8B, 0x94, 0x9E);
        internal static readonly Brush Green = Frozen(0x7E, 0xE7, 0x87);
        internal static readonly Brush Red = Frozen(0xFF, 0x5F, 0x5F);

        private readonly RecordingService _svc;
        private readonly TakeStore _store = TakeStore.Shared;
        private readonly CameraPreviewController _preview = new();
        private readonly Dictionary<string, CheckBox> _ticks = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unticked = new(StringComparer.OrdinalIgnoreCase);

        private readonly TextBlock _listTitle = new() { Foreground = Dim, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _cards = new() { Margin = new Thickness(16) };
        private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
        private readonly TextBlock _previewStatus = new() { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly ComboBox _camera = new() { Margin = new Thickness(0, 4, 0, 8) };
        private readonly ComboBox _mic = new() { Margin = new Thickness(0, 4, 0, 8) };
        private readonly RadioButton _readFirst = new() { Content = "Read first, then look at the camera", Foreground = Text, GroupName = "reading", Margin = new Thickness(0, 2, 0, 2) };
        private readonly RadioButton _prompter = new() { Content = "Teleprompter under the camera (default)", Foreground = Text, GroupName = "reading", Margin = new Thickness(0, 4, 0, 2) };
        private readonly TextBlock _readingHelp = new() { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        private readonly ComboBox _screen = new() { Margin = new Thickness(0, 4, 0, 12) };
        private readonly Button _start = new() { Height = 40, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = Red, BorderThickness = new Thickness(0) };
        private readonly TextBlock _status = new() { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        private WriteableBitmap? _bitmap;
        private int _framePending;
        private bool _devicesLoaded;
        private bool _recordingOpen;

        public TakesWindow(RecordingService svc)
        {
            Log.Info("[TakesWindow] ctor");
            _svc = svc;
            Title = "AgentEyes - Takes";
            Width = 1180; Height = 820;
            MinWidth = 900; MinHeight = 600;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI");
            Foreground = Text;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var root = new DockPanel();

            // ---- header ----
            var header = new DockPanel { Background = Panel, Height = 44 };
            var name = new TextBlock { Text = "AgentEyes - Takes", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var open = SmallButton("Open list...", (_, _) => OpenListFile());
            open.Margin = new Thickness(0, 0, 16, 0);
            DockPanel.SetDock(open, Dock.Right);
            header.Children.Add(open);
            var hp = new StackPanel { Orientation = Orientation.Horizontal };
            hp.Children.Add(name);
            hp.Children.Add(_listTitle);
            header.Children.Add(hp);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // ---- right: camera, devices, reading mode, screen, start ----
            var side = new StackPanel { Margin = new Thickness(16) };
            side.Children.Add(Label("YOU, LIVE"));
            var frame = new Grid { Height = 180, Background = Brushes.Black, Margin = new Thickness(0, 4, 0, 0) };
            frame.Children.Add(_previewImage);
            // The framing box: keep the head inside it. The final crop is chosen in the video pipeline.
            frame.Children.Add(new Rectangle
            {
                Stroke = Dim, StrokeDashArray = new DoubleCollection { 4, 3 }, StrokeThickness = 1,
                Width = 140, Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Center,
            });
            side.Children.Add(frame);
            side.Children.Add(_previewStatus);
            side.Children.Add(Label("CAMERA", 12));
            side.Children.Add(_camera);
            side.Children.Add(Label("MICROPHONE"));
            side.Children.Add(_mic);
            side.Children.Add(Label("HOW TO READ", 4));
            side.Children.Add(_prompter);
            side.Children.Add(_readFirst);
            side.Children.Add(_readingHelp);
            side.Children.Add(Label("RECORD ON"));
            side.Children.Add(_screen);
            side.Children.Add(_start);
            side.Children.Add(_status);
            var sideBorder = new Border { Width = 330, Background = Panel, BorderBrush = Border, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            DockPanel.SetDock(sideBorder, Dock.Right);
            root.Children.Add(sideBorder);

            // ---- left: the take list ----
            root.Children.Add(new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            _readFirst.Checked += (_, _) => ReadingChanged();
            _prompter.Checked += (_, _) => ReadingChanged();
            _camera.SelectionChanged += (_, _) => CameraChanged();
            _start.Click += (_, _) => StartClicked();

            _preview.StateChanged += (state, status) => Ui.Post(() =>
            {
                _previewStatus.Text = state == CameraPreviewState.Running ? "" : status;
                if (state != CameraPreviewState.Running) { _previewImage.Source = null; _bitmap = null; }
            });
            _preview.FrameReceived += OnFrame;

            _store.Changed += OnStoreChanged;
            Closed += (_, _) =>
            {
                Log.Info("[TakesWindow] closed");
                _store.Changed -= OnStoreChanged;
                _preview.Dispose();
            };

            var list = _store.Snapshot();
            // Issue #230 round 2: the teleprompter unless the list says read-first.
            (list.ReadingMode == TakeStore.ReadFirst ? _readFirst : _prompter).IsChecked = true;
            RenderList(list);
            FillScreens(list.Screen);
            _camera.Items.Add("Loading cameras...");
            _mic.Items.Add("Loading microphones...");
            _camera.SelectedIndex = 0; _mic.SelectedIndex = 0;
            _camera.IsEnabled = _mic.IsEnabled = false;
            Loaded += async (_, _) => await LoadDevicesAsync(list);
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static TextBlock Label(string text, double top = 0) => new()
        {
            Text = text, Foreground = Dim, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0),
        };

        private static Button SmallButton(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            b.Click += click;
            return b;
        }

        // ---- devices ----------------------------------------------------------------------------

        private async System.Threading.Tasks.Task LoadDevicesAsync(TakeList list)
        {
            Log.Info("[TakesWindow] LoadDevicesAsync: listing cameras and microphones");
            try
            {
                var (cams, mics) = await Ui.Run(() => (FfmpegDevices.ListVideo(), FfmpegDevices.ListAudio()));
                _camera.Items.Clear(); _mic.Items.Clear();
                foreach (var c in cams) _camera.Items.Add(c);
                foreach (var m in mics) _mic.Items.Add(m);
                _camera.SelectedItem = Pick(cams, list.Camera, "C920");
                _mic.SelectedItem = Pick(mics, list.Mic, "FDUCE");
                _camera.IsEnabled = _mic.IsEnabled = true;
                _devicesLoaded = true;
                if (cams.Count == 0) _previewStatus.Text = "No camera is attached.";
                Log.Info($"[TakesWindow] LoadDevicesAsync: {cams.Count} camera(s), {mics.Count} microphone(s)");
                CameraChanged();
            }
            catch (Exception ex)
            {
                Log.Error("[TakesWindow] LoadDevicesAsync FAILED", ex);
                _previewStatus.Text = "Cameras and microphones could not be listed: " + ex.Message;
            }
            UpdateStart();
        }

        /// <summary>The saved device if it is still attached, else the first whose name contains the
        /// preferred fragment, else the first that is not a virtual camera.</summary>
        private static string? Pick(IReadOnlyList<string> names, string? saved, string preferred)
        {
            if (saved != null && names.Contains(saved)) return saved;
            return names.FirstOrDefault(n => n.Contains(preferred, StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault(n => !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault();
        }

        private void CameraChanged()
        {
            if (!_devicesLoaded || _recordingOpen) return;
            string? cam = _camera.SelectedItem as string;
            Log.Info($"[TakesWindow] CameraChanged: {cam ?? "(none)"}");
            try { _preview.Select(cam); }
            catch (Exception ex)
            {
                Log.Error("[TakesWindow] CameraChanged: preview FAILED", ex);
                _previewStatus.Text = "The preview could not be started: " + ex.Message;
            }
        }

        private void OnFrame(byte[] bgr24)
        {
            if (Interlocked.CompareExchange(ref _framePending, 1, 0) != 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (bgr24.Length != FfmpegCameraPreview.FrameBytes) return;
                    if (_bitmap == null)
                    {
                        _bitmap = new WriteableBitmap(FfmpegCameraPreview.FrameWidth, FfmpegCameraPreview.FrameHeight, 96, 96, PixelFormats.Bgr24, null);
                        _previewImage.Source = _bitmap;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, FfmpegCameraPreview.FrameWidth, FfmpegCameraPreview.FrameHeight),
                        bgr24, FfmpegCameraPreview.FrameWidth * 3, 0);
                }
                catch (Exception ex) { Log.Error("[TakesWindow] OnFrame FAILED", ex); }
                finally { Interlocked.Exchange(ref _framePending, 0); }
            });
        }

        private void FillScreens(int? saved)
        {
            var monitors = Monitors.All();
            _screen.Items.Clear();
            foreach (var m in monitors)
                _screen.Items.Add(new ComboBoxItem
                {
                    Content = $"Screen {m.Index} - {m.Width}x{m.Height}{(m.Primary ? " (main)" : "")}",
                    Tag = m.Index,
                });
            int want = saved is int s && monitors.Any(m => m.Index == s)
                ? s
                : (monitors.FirstOrDefault(m => !m.Primary) ?? monitors.First()).Index;
            _screen.SelectedIndex = monitors.ToList().FindIndex(m => m.Index == want);
        }

        private void ReadingChanged()
        {
            _readingHelp.Text = _prompter.IsChecked == true
                ? "Teleprompter: the text stays on screen the whole take, one phrase at a time under the camera, the next one dimmed. Right arrow moves on."
                : "Read first: the line shows, you read it, press Space, a 3-2-1 count, the text is gone and you say it to the lens. Best for short takes.";
        }

        // ---- the list ---------------------------------------------------------------------------

        private void OnStoreChanged() => Ui.Post(() => RenderList(_store.Snapshot()));

        private void RenderList(TakeList list)
        {
            // Remember what the person unticked, so a refresh after a take does not re-tick it.
            foreach (var (id, box) in _ticks)
            {
                if (box.IsChecked == true) _unticked.Remove(id); else _unticked.Add(id);
            }
            _ticks.Clear();
            _cards.Children.Clear();
            _listTitle.Text = list.Title ?? "";

            if (list.Takes.Count == 0)
            {
                _cards.Children.Add(new TextBlock
                {
                    Text = "No take list is loaded. A video session loads one through the Control API "
                           + "(POST /takes), or open a list file with \"Open list...\".",
                    Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 14,
                });
                UpdateStart();
                return;
            }

            for (int i = 0; i < list.Takes.Count; i++) _cards.Children.Add(Card(i + 1, list.Takes[i]));
            UpdateStart();
        }

        private UIElement Card(int number, Take take)
        {
            var tick = new CheckBox { IsChecked = !_unticked.Contains(take.Id), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0) };
            tick.Checked += (_, _) => UpdateStart();
            tick.Unchecked += (_, _) => UpdateStart();
            _ticks[take.Id] = tick;

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"{number}. {take.Title ?? take.Id}", FontWeight = FontWeights.SemiBold, FontSize = 14 });
            body.Children.Add(new TextBlock { Text = take.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 6, 0, 6), LineHeight = 21 });
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(take.Instructions)) notes.Add(take.Instructions!.Trim());
            if (!string.IsNullOrWhiteSpace(take.Background)) notes.Add("Background: " + take.Background!.Trim());
            if (notes.Count > 0)
                body.Children.Add(new TextBlock { Text = string.Join(" ", notes), Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap });

            if (take.Attempts.Count > 0)
            {
                var attempts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var a in take.Attempts.OrderBy(a => a.Number)) attempts.Children.Add(AttemptChip(take, a));
                body.Children.Add(attempts);
            }

            var meta = new StackPanel { Margin = new Thickness(16, 0, 0, 0), MinWidth = 110 };
            if (take.TargetSeconds is double secs)
                meta.Children.Add(new TextBlock { Text = $"about {secs:0} s", Foreground = Dim, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            meta.Children.Add(new TextBlock
            {
                Text = take.Attempts.Count == 0 ? "not recorded" : take.Attempts.Count == 1 ? "1 attempt" : $"{take.Attempts.Count} attempts",
                Foreground = Dim, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right,
            });
            if (take.Chosen is int chosen)
                meta.Children.Add(new Border
                {
                    Background = Frozen(0x1F, 0x3B, 0x26), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock { Text = $"#{chosen} chosen", Foreground = Green, FontSize = 11 },
                });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(tick, 0); Grid.SetColumn(body, 1); Grid.SetColumn(meta, 2);
            grid.Children.Add(tick); grid.Children.Add(body); grid.Children.Add(meta);

            return new Border
            {
                Background = Panel, BorderBrush = tick.IsChecked == true ? Frozen(0x2E, 0x6B, 0x3A) : Border,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), Child = grid,
            };
        }

        private UIElement AttemptChip(Take take, TakeAttempt a)
        {
            bool chosen = take.Chosen == a.Number;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            row.Children.Add(new TextBlock
            {
                Text = $"#{a.Number} {TimeSpan.FromSeconds(a.DurationSeconds):m\\:ss}",
                Foreground = chosen ? Green : Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            row.Children.Add(SmallButton("Play", (_, _) => Play(a)));
            row.Children.Add(SmallButton(chosen ? "Chosen" : "Use this", (_, _) => ChooseAttempt(take.Id, chosen ? null : a.Number)));
            return row;
        }

        private void Play(TakeAttempt a)
        {
            Log.Info($"[TakesWindow] Play: {a.File}");
            try
            {
                if (!File.Exists(a.File)) { _status.Text = "That attempt's file is not there any more: " + a.File; return; }
                Process.Start(new ProcessStartInfo(a.File) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("[TakesWindow] Play FAILED", ex);
                _status.Text = "Could not play it: " + ex.Message;
            }
        }

        private void ChooseAttempt(string takeId, int? number)
        {
            try { _store.Choose(takeId, number); }
            catch (Exception ex)
            {
                Log.Error("[TakesWindow] ChooseAttempt FAILED", ex);
                _status.Text = ex.Message;
            }
        }

        private void OpenListFile()
        {
            Log.Info("[TakesWindow] OpenListFile");
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Take list (*.json)|*.json|All files|*.*", Title = "Open a take list" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _store.Load(TakeStore.Parse(File.ReadAllText(dlg.FileName)));
                _status.Text = "Loaded " + System.IO.Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Error("[TakesWindow] OpenListFile FAILED", ex);
                _status.Text = "That list could not be loaded: " + ex.Message;
            }
        }

        // ---- start ------------------------------------------------------------------------------

        private List<Take> SelectedTakes() =>
            _store.Snapshot().Takes.Where(t => _ticks.TryGetValue(t.Id, out var b) && b.IsChecked == true).ToList();

        private void UpdateStart()
        {
            int n = SelectedTakes().Count;
            // Issue #230 round 2: the button says what it does - it opens the recording screen;
            // recording itself starts there, with Space.
            _start.Content = n == 1 ? "Open the recording screen - 1 take" : $"Open the recording screen - {n} takes";
            _start.IsEnabled = n > 0 && _devicesLoaded && !_recordingOpen;
        }

        private void StartClicked()
        {
            var takes = SelectedTakes();
            string? cam = _camera.SelectedItem as string;
            string? mic = _mic.SelectedItem as string;
            string reading = _prompter.IsChecked == true ? TakeStore.Teleprompter : TakeStore.ReadFirst;
            int screen = (_screen.SelectedItem as ComboBoxItem)?.Tag is int s ? s : 1;
            Log.Info($"[TakesWindow] StartClicked: takes={takes.Count} camera={cam ?? "(none)"} mic={mic ?? "(none)"} reading={reading} screen={screen}");

            if (takes.Count == 0) { _status.Text = "Tick at least one take."; return; }
            if (cam == null) { _status.Text = "Choose a camera."; return; }
            if (mic == null) { _status.Text = "Choose a microphone."; return; }
            if (_svc.IsRecording) { _status.Text = "AgentEyes is already recording. Stop that recording first."; return; }

            try
            {
                _store.SaveSettings(cam, mic, reading, screen);
                var monitor = Monitors.Require(screen);

                // The recording opens the camera itself; the preview lets go of it now.
                _recordingOpen = true;
                _preview.Stop("The camera is recording.");
                UpdateStart();

                var rec = new TakeRecordingWindow(_svc, _store, takes, cam, mic, reading, monitor);
                rec.Closed += (_, _) =>
                {
                    Log.Info("[TakesWindow] the recording window closed");
                    _recordingOpen = false;
                    UpdateStart();
                    CameraChanged();
                    Activate();
                };
                rec.Show();
                _status.Text = "";
            }
            catch (Exception ex)
            {
                Log.Error("[TakesWindow] StartClicked FAILED", ex);
                _recordingOpen = false;
                UpdateStart();
                _status.Text = "Could not start: " + ex.Message;
            }
        }
    }
}
