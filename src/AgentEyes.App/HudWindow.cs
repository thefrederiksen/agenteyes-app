using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentEyes;
using AgentEyes.Preview;

namespace AgentEyes.App
{
    /// <summary>
    /// Floating recording HUD (issue #20): small always-on-top pill shown only while
    /// recording. Pulsing red dot, elapsed timer, live mic/system meters, Stop and
    /// Discard. Draggable; position persists in Config. Never steals focus from the
    /// app being recorded (WS_EX_NOACTIVATE) and asks Windows to exclude it from
    /// screen capture (WDA_EXCLUDEFROMCAPTURE, Win10 2004+).
    ///
    /// Issue #33 adds a LIVE PREVIEW panel: the screen, the camera, or both with the camera inset in
    /// a chosen corner, shown inside the HUD while the recording runs. Two properties of this window
    /// are what make that possible at all, and neither may be given up:
    ///
    ///  - THE HUD IS EXCLUDED FROM SCREEN CAPTURE. That exclusion is why a picture of the screen
    ///    drawn inside this window does not recurse into an infinite mirror tunnel, and why neither
    ///    the HUD nor its preview appears in recording.mp4 (AC6, assumption C5).
    ///  - THE PREVIEW NEVER TOUCHES THE CAMERA. Its frames are read from files the recording's own
    ///    ffmpeg pipeline publishes (<see cref="PreviewTap"/>); this window opens no capture device,
    ///    starts no process, and can therefore not take the webcam away from the recording
    ///    (assumption C1). Everything it does is read a file and draw it.
    /// </summary>
    internal sealed class HudWindow : Window
    {
        /// <summary>Default size of the HUD once the preview panel is showing, used until the person
        /// resizes it. Wide enough for a 16:9 preview at the tap's own 480x270.</summary>
        private const double DefaultPreviewWidth = 520;
        private const double DefaultPreviewHeight = 400;

        /// <summary>Fraction of the panel width the camera occupies when it is inset into a corner.</summary>
        private const double InsetWidthFraction = 0.30;

        private readonly RecordingService _svc;
        private readonly Config _cfg;
        private readonly Func<Task> _stop;
        private readonly Func<Task> _discard;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

        private readonly Ellipse _dot;
        private readonly TextBlock _elapsed;
        private readonly ProgressBar _mic;
        private readonly ProgressBar _sys;
        private readonly Button _stopButton;
        private readonly Button _discardButton;
        private bool _busy;
        private bool _finishing;   // showing a "Saving..."/processing label; the stop flow owns the close

        // ---- live preview (issue #33) --------------------------------------
        private readonly HudPreviewState _preview;
        private readonly PreviewFrameFeed _feed;
        private readonly Button _previewToggle;
        private readonly Grid _previewPanel;
        private readonly Grid _previewSurface;
        private readonly Image _screenImage;
        private readonly Border _cameraHost;
        private readonly Image _cameraImage;
        private readonly TextBlock _previewMessage;
        private readonly TextBlock _previewStatus;
        private readonly Button[] _modeButtons;
        private readonly Button[] _cornerButtons;

        /// <summary>
        /// The size this HUD was last given while it was manually sized, i.e. while the preview
        /// panel was up (issue #33, AC7). It is remembered as it happens rather than read back when
        /// the window closes, because the window auto-sizes back to the pill BEFORE Closed fires -
        /// <see cref="SetStatus"/> does it on every ordinary stop and <see cref="ApplyPreviewState"/>
        /// does it whenever the panel is hidden. See <see cref="HudSizeMemory"/>.
        /// </summary>
        private readonly HudSizeMemory _size;

        /// <summary>
        /// The only thing that ever writes to <see cref="_size"/> (issue #33, AC7). It watches for
        /// the three gestures by which a PERSON can resize this window - a drag of the sizing
        /// border, the preview panel's grip, and a UI Automation TransformPattern command - and
        /// nothing else. Nothing in this window is subscribed to SizeChanged or LayoutUpdated, so a
        /// size the layout produced cannot be mistaken for a size somebody chose.
        /// </summary>
        private readonly HudUserResize _userResize;

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal HudWindow(RecordingService svc, Config cfg, Func<Task> stop, Func<Task> discard)
        {
            _svc = svc; _cfg = cfg; _stop = stop; _discard = discard;
            _size = new HudSizeMemory(cfg.HudWidth, cfg.HudHeight);
            _userResize = new HudUserResize(this, _size);

            Title = "Recording HUD";   // no visible chrome; the name serves UI Automation
            WindowStyle = WindowStyle.None;
            // Issue #33, AC7: the HUD is resizable so the preview can be made big enough to be
            // useful. With no chrome the resize border is invisible, which is why the panel also
            // carries an explicit grip - and why UI Automation's Transform pattern (which this
            // ResizeMode is what enables) is the focus-free way to drive it.
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 260;
            MinHeight = 52;
            FontFamily = new FontFamily("Segoe UI");

            // ---- layout: [dot] 04:27 [meters] | [preview] [stop] [discard] ----
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 12, 10) };

            _dot = new Ellipse { Width = 10, Height = 10, Fill = Res<Brush>("RdRecord"), VerticalAlignment = VerticalAlignment.Center };
            var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _dot.BeginAnimation(OpacityProperty, pulse);
            row.Children.Add(_dot);

            _elapsed = new TextBlock
            {
                Text = "00:00",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 17, FontWeight = FontWeights.Bold,
                Foreground = Res<Brush>("RdText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 12, 0),
            };
            row.Children.Add(_elapsed);

            var meters = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _mic = MiniMeter();
            _sys = MiniMeter();
            _sys.Margin = new Thickness(0, 3, 0, 0);
            meters.Children.Add(_mic);
            meters.Children.Add(_sys);
            row.Children.Add(meters);

            _previewToggle = new Button
            {
                Style = Res<Style>("DkMini"),
                Height = 30, FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                Content = "Show preview",
                ToolTip = "Show what is being recorded",
                VerticalAlignment = VerticalAlignment.Center,
            };
            // The UI Automation NAME is fixed at "Show preview" in both states so it can be found by
            // name whichever way the panel is currently set (AC1); the visible label and the help
            // text are what change.
            AutomationName(_previewToggle, "Show preview");
            _previewToggle.Click += (_, _) => TogglePreview();
            row.Children.Add(_previewToggle);

            _stopButton = new Button
            {
                Style = Res<Style>("DkRecord"),
                Width = 64, Height = 30, FontSize = 12,
                Content = "STOP",
                ToolTip = "Stop recording",
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationName(_stopButton, "HUD stop");
            _stopButton.Click += async (_, _) => await RunOnce(_stop, "Saving...");
            row.Children.Add(_stopButton);

            _discardButton = new Button
            {
                Style = Res<Style>("DkIcon"),
                Height = 30,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Discard recording (stop and delete)",
                VerticalAlignment = VerticalAlignment.Center,
                Content = new Path
                {
                    // Trash can
                    Data = Geometry.Parse("M3,5 h12 M7,5 v-2 h4 v2 M5,5 l1,11 h6 l1,-11"),
                    Stroke = Res<Brush>("DkText"), StrokeThickness = 1.4,
                    Width = 14, Height = 14, Stretch = Stretch.Uniform,
                },
            };
            AutomationName(_discardButton, "HUD discard");
            _discardButton.Click += async (_, _) => await ConfirmDiscard();
            row.Children.Add(_discardButton);

            // ---- the preview panel ------------------------------------------
            // A recording only has a camera to preview when it is actually recording one. The state
            // is told that once, here, and every camera control follows from it.
            _preview = new HudPreviewState(
                _cfg.HudPreviewVisible,
                PreviewNames.Mode(_cfg.HudPreviewMode),
                PreviewNames.Corner(_cfg.HudPreviewCorner),
                feedAvailable: _svc.PreviewAvailable,
                cameraAvailable: _svc.PreviewCameraFrame != null);

            _screenImage = new Image { Stretch = Stretch.Uniform };
            _cameraImage = new Image { Stretch = Stretch.Uniform };
            _cameraHost = new Border
            {
                BorderBrush = Res<Brush>("RdStroke"),
                BorderThickness = new Thickness(1),
                Background = Brushes.Black,
                Margin = new Thickness(8),
                Child = _cameraImage,
                Visibility = Visibility.Collapsed,
            };
            _previewMessage = new TextBlock
            {
                Foreground = Res<Brush>("RdDim"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16),
            };
            AutomationName(_previewMessage, "HUD preview message");

            _previewSurface = new Grid { Background = Brushes.Black, ClipToBounds = true };
            _previewSurface.Children.Add(_screenImage);
            _previewSurface.Children.Add(_cameraHost);
            _previewSurface.Children.Add(_previewMessage);
            _previewSurface.SizeChanged += (_, e) => LayOutInset(e.NewSize.Width);

            _modeButtons = new[]
            {
                PreviewChip("Screen", "Preview mode screen", () => ChooseMode(PreviewMode.Screen)),
                PreviewChip("Camera", "Preview mode camera", () => ChooseMode(PreviewMode.Camera)),
                PreviewChip("Both", "Preview mode both", () => ChooseMode(PreviewMode.Both)),
            };
            _cornerButtons = new[]
            {
                PreviewChip("TL", "Preview corner top-left", () => ChooseCorner(PreviewCorner.TopLeft)),
                PreviewChip("TR", "Preview corner top-right", () => ChooseCorner(PreviewCorner.TopRight)),
                PreviewChip("BL", "Preview corner bottom-left", () => ChooseCorner(PreviewCorner.BottomLeft)),
                PreviewChip("BR", "Preview corner bottom-right", () => ChooseCorner(PreviewCorner.BottomRight)),
            };

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(14, 0, 12, 8),
            };
            foreach (var b in _modeButtons) controls.Children.Add(b);
            controls.Children.Add(new TextBlock
            {
                Text = "Corner",
                Foreground = Res<Brush>("RdDim"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0),
            });
            foreach (var b in _cornerButtons) controls.Children.Add(b);

            _previewStatus = new TextBlock
            {
                Foreground = Res<Brush>("RdDim"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            // Read by UI Automation: it is how the preview's mode, corner and liveness can be
            // asserted without a screenshot, which matters because this window is deliberately
            // invisible to screen capture.
            AutomationName(_previewStatus, "HUD preview status");
            controls.Children.Add(_previewStatus);

            // The visible resize affordance. The window is resizable from its (invisible) borders as
            // well, but a grip is what a person can actually find on a chromeless window.
            var grip = new Thumb
            {
                Width = 14, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = System.Windows.Input.Cursors.SizeNWSE,
                Opacity = 0.6,
                Template = GripTemplate(),
            };
            AutomationName(grip, "HUD resize");
            grip.DragDelta += (_, e) => _userResize.ByGrip(e.HorizontalChange, e.VerticalChange);

            var surfaceHost = new Grid { Margin = new Thickness(14, 0, 12, 10) };
            surfaceHost.Children.Add(new Border
            {
                BorderBrush = Res<Brush>("RdStroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = _previewSurface,
            });
            surfaceHost.Children.Add(grip);

            _previewPanel = new Grid { Visibility = Visibility.Collapsed };
            _previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _previewPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(controls, 0);
            Grid.SetRow(surfaceHost, 1);
            _previewPanel.Children.Add(controls);
            _previewPanel.Children.Add(surfaceHost);

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(row, 0);
            Grid.SetRow(_previewPanel, 1);
            body.Children.Add(row);
            body.Children.Add(_previewPanel);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x26, 0x2B)),
                BorderBrush = Res<Brush>("RdStroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = body,
            };

            _feed = new PreviewFrameFeed(Dispatcher, ShowFrames);

            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* click without drag */ } };
            // A size is remembered when a PERSON resizes this window, and at no other time
            // (issue #33, AC7). It cannot be read back at save time - by the time Closed runs, the
            // stop has already auto-sized the HUD to the pill and the panel's size is gone - and it
            // must not be taken from the window's own size reports either: three defects were
            // shipped trying to sort those into the person's and the layout's. See HudUserResize.
            _userResize.Watch();
            Loaded += (_, _) => Position();
            SourceInitialized += (_, _) => ApplyWindowStyles();
            Closed += (_, _) => { _timer.Stop(); SavePosition(); ClosePreview(); };

            // Constructing a HUD is not a person choosing anything, and the comment in
            // ApplyPreviewState always said so - but the call passed a `fromUser: true` flag, so
            // every HUD ever built rewrote config.json while it was being put on screen (Review Gate
            // round 1 on PR #34). The flag is gone: there are two methods now, and which one the
            // constructor calls is a fact readable from the compiled call graph rather than from an
            // argument. The in-memory half still runs here - arming the next recording, telling the
            // taps whether to publish; only remembering the choice belongs to a person.
            ApplyPreviewState();

            _timer.Tick += (_, _) => OnTick();
            _timer.Start();
        }

        private static ProgressBar MiniMeter() => new()
        {
            Width = 46, Height = 4, Minimum = 0, Maximum = 100,
            BorderThickness = new Thickness(0),
            Background = Res<Brush>("RdStroke"),
            Foreground = Res<Brush>("RdAccent"),
        };

        private static void AutomationName(UIElement e, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(e, name);

        private void OnTick()
        {
            // Once we show a Saving.../processing label, the stop flow owns the close - don't
            // overwrite the label or auto-close from here.
            if (_finishing) return;
            if (!_svc.IsRecording) { Close(); return; }   // ended elsewhere (main window, tray, REST)
            var t = _svc.Elapsed;
            _elapsed.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
            _mic.Value = Math.Min(100, _svc.MicLevel * 180);
            _sys.Value = Math.Min(100, _svc.SystemLevel * 180);
        }

        private async Task RunOnce(Func<Task> action, string busyLabel)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(busyLabel);   // immediate feedback - never a silent disabled button
            try { await action(); }
            finally { _busy = false; }
        }

        /// <summary>
        /// Switch the HUD to a processing state (a staged "Saving video..."/"Saving audio..."
        /// label while the raw files flush). Recording is over: stop the pulse, drop the
        /// meters and buttons, show the label. Once shown the HUD stays put until the stop
        /// flow closes it. Safe to call repeatedly and from the stop path (not just the button).
        /// </summary>
        public void SetStatus(string label)
        {
            Log.Info($"hud: status -> {label}");   // staged save sequence is visible in FileLog (issue #77 AC3)
            _finishing = true;
            _dot.BeginAnimation(OpacityProperty, null);   // stop the recording pulse
            _dot.Opacity = 1.0;
            _dot.Fill = Res<Brush>("RdAccent");           // blue = processing, not recording
            _mic.Visibility = Visibility.Collapsed;
            _sys.Visibility = Visibility.Collapsed;
            _stopButton.Visibility = Visibility.Collapsed;
            _discardButton.Visibility = Visibility.Collapsed;
            _previewToggle.Visibility = Visibility.Collapsed;
            // The recording is over, so there is nothing live left to preview. Taking the panel down
            // here also means the HUD is back to its pill size for the save, and the person is not
            // watching a picture that has stopped moving (AC10 in miniature).
            ClosePreview();
            _previewPanel.Visibility = Visibility.Collapsed;
            HudPreviewSizing.HidePanel(this, _size);
            _elapsed.Margin = new Thickness(9, 0, 4, 0);
            _elapsed.Text = label;
        }

        private async Task ConfirmDiscard()
        {
            Log.Info("hud: discard clicked");
            if (_busy) return;
            if (MessageBox.Show("Discard this recording? Its files will be deleted.",
                    "AgentEyes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await RunOnce(_discard, "Discarding...");
        }

        // ---- live preview (issue #33) --------------------------------------

        /// <summary>A small selectable button in the preview control strip.</summary>
        private static Button PreviewChip(string label, string automationName, Action onClick)
        {
            var b = new Button
            {
                Style = Res<Style>("DkMini"),
                Content = label,
                FontSize = 11,
                Height = 22,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationName(b, automationName);
            b.Click += (_, _) => onClick();
            return b;
        }

        private static ControlTemplate GripTemplate()
        {
            // The grid carries a transparent background on purpose: a stroke-only Path is only
            // hit-testable ON the stroke, which would make the grip a pixel-hunt to grab.
            var host = new FrameworkElementFactory(typeof(Grid));
            host.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var path = new FrameworkElementFactory(typeof(Path));
            path.SetValue(Path.DataProperty, Geometry.Parse("M10,0 L10,10 L0,10 M10,5 L5,10"));
            path.SetValue(Shape.StrokeProperty, Res<Brush>("RdDim"));
            path.SetValue(Shape.StrokeThicknessProperty, 1.2);
            path.SetValue(Shape.StretchProperty, Stretch.Uniform);
            host.AppendChild(path);
            return new ControlTemplate(typeof(Thumb)) { VisualTree = host };
        }

        /// <summary>
        /// Show or hide the preview. Deliberately trivial work on the UI thread: flipping a flag,
        /// setting visibilities, and telling the service to start or stop publishing frames. Nothing
        /// here opens a device, starts a process or touches the recording (AC8), so it returns well
        /// inside the responsive-UI budget.
        /// </summary>
        private void TogglePreview()
        {
            bool visible = _preview.ToggleVisible();
            Log.Info($"hud: preview toggled -> {(visible ? "shown" : "hidden")}");
            ApplyAndRememberPreviewChoice();
        }

        private void ChooseMode(PreviewMode mode)
        {
            if (!_preview.TrySetMode(mode))
            {
                Log.Warn($"hud: preview mode {PreviewNames.Text(mode)} refused - this recording has no camera track");
                _previewMessage.Text = "This recording has no camera track.";
                return;
            }
            Log.Info($"hud: preview mode -> {PreviewNames.Text(mode)}");
            ApplyAndRememberPreviewChoice();
        }

        private void ChooseCorner(PreviewCorner corner)
        {
            _preview.SetCorner(corner);
            Log.Info($"hud: preview corner -> {PreviewNames.Text(corner)}");
            ApplyAndRememberPreviewChoice();
        }

        /// <summary>
        /// Apply the current preview decisions AND remember them - what a person's click does.
        ///
        /// It is a separate method rather than a boolean argument on purpose (Review Gate round 1 on
        /// PR #34): the constructor used to pass "this is a person's choice" and rewrite config.json
        /// while the HUD was being put on screen, and no call-graph guard could see that, because an
        /// argument is invisible to one. Two methods make it visible - the constructor does not
        /// reach <see cref="SavePreviewChoices"/>, and <c>HudResponsivenessTests</c> asserts exactly
        /// that against the IL.
        /// </summary>
        private void ApplyAndRememberPreviewChoice()
        {
            ApplyPreviewState();
            SavePreviewChoices();
        }

        /// <summary>
        /// Push the current preview decisions into the window, the frame feed and the service. One
        /// place, so a mode change, a corner change and the toggle cannot drift apart.
        ///
        /// It PERSISTS NOTHING. Everything here is in memory: visibilities, the frame feed's wants,
        /// two flags on the recording service. That is what makes it safe to call while the HUD is
        /// being constructed, and <c>HudResponsivenessTests</c> asserts it against the IL.
        /// </summary>
        private void ApplyPreviewState()
        {
            _previewToggle.Content = _preview.ToggleLabel;
            System.Windows.Automation.AutomationProperties.SetHelpText(
                _previewToggle, _preview.Visible ? "showing" : "hidden");

            foreach (var b in _modeButtons) b.IsEnabled = true;
            _modeButtons[1].IsEnabled = _preview.CameraModesEnabled;   // Camera
            _modeButtons[2].IsEnabled = _preview.CameraModesEnabled;   // Both
            Select(_modeButtons, ModeIndex(_preview.Mode));

            foreach (var b in _cornerButtons) b.IsEnabled = _preview.CornerControlsEnabled;
            Select(_cornerButtons, CornerIndex(_preview.Corner));

            _previewPanel.Visibility = _preview.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (_preview.Visible)
            {
                // Manual sizing is what makes the window resizable at all: SizeToContent overrides
                // any width and height set on it, so the pill can only auto-size and the preview can
                // only be sized by hand. The size comes from the remembered one, not the config's:
                // within one recording the panel can be hidden and shown again, and it must come
                // back at the size it was left at even though nothing has been written to disk in
                // between (issue #33, AC7).
                HudPreviewSizing.ShowPanel(this, _size, DefaultPreviewWidth, DefaultPreviewHeight);
                _feed.Want(_svc.PreviewScreenFrame, _preview.ShowScreenLayer,
                           _svc.PreviewCameraFrame, _preview.ShowCameraLayer);
                _feed.Start();
            }
            else
            {
                // The completeness canary (issue #33, AC7): the last instant at which a size the HUD
                // ended up at, that no gesture ever claimed, can still be seen. It is reported, never
                // acted on - a size nobody was shown to have chosen is not a size to remember.
                string? unattributed = HudPreviewSizing.HidePanel(this, _size);
                if (unattributed != null) Log.Warn(unattributed);
                _feed.Want(null, false, null, false);
                _screenImage.Source = null;
                _cameraImage.Source = null;
            }

            _screenImage.Visibility = _preview.ShowScreenLayer ? Visibility.Visible : Visibility.Collapsed;
            _cameraHost.Visibility = _preview.ShowCameraLayer ? Visibility.Visible : Visibility.Collapsed;
            LayOutInset(_previewSurface.ActualWidth);
            UpdatePreviewStatus(screenStale: true, cameraStale: true);

            // The tap only writes frames out while something is looking at them (AC9): hiding the
            // panel stops the file writes and leaves the recording exactly as it was. Both of these
            // are in-memory flag sets on the service - no I/O, no process, nothing the recording can
            // feel - so they run on every apply, including the first one at construction.
            _svc.SetPreviewPublishing(_preview.Visible);
            // The framing hint for manifest.json (AC5) - null whenever no overlay was framed.
            _svc.SetPreviewOverlayCorner(_preview.ManifestCorner);
            // Arming is a choice about the NEXT recording, not this one: ffmpeg's outputs are fixed
            // when the process starts, so a feed can only be created at a recording's start. An
            // in-memory flag on the service, so it runs on EVERY apply including the construction
            // one - which is what keeps the service's arming and the HUD's state from drifting apart
            // now that construction no longer writes config.
            _svc.PreviewArmed = _preview.ArmNextRecording;
        }

        private static void Select(Button[] buttons, int index)
        {
            for (int i = 0; i < buttons.Length; i++)
                buttons[i].Background = i == index
                    ? Res<Brush>("RdAccent")
                    : new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3D));
        }

        /// <summary>Which control strip button stands for a mode. Written out rather than cast from
        /// the enum: the button order is a layout decision and the enum order is not, and a silent
        /// cast between them would mis-highlight the moment either changed.</summary>
        private static int ModeIndex(PreviewMode mode) => mode switch
        {
            PreviewMode.Screen => 0,
            PreviewMode.Camera => 1,
            PreviewMode.Both => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown preview mode"),
        };

        private static int CornerIndex(PreviewCorner corner) => corner switch
        {
            PreviewCorner.TopLeft => 0,
            PreviewCorner.TopRight => 1,
            PreviewCorner.BottomLeft => 2,
            PreviewCorner.BottomRight => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(corner), corner, "unknown preview corner"),
        };

        /// <summary>Place and size the camera layer: inset into the chosen corner in "both" mode,
        /// filling the panel when the camera is the only thing being shown.</summary>
        private void LayOutInset(double surfaceWidth)
        {
            if (_preview.CameraIsInset)
            {
                _cameraHost.Width = Math.Max(96, surfaceWidth * InsetWidthFraction);
                _cameraHost.Height = double.NaN;
                _cameraHost.Margin = new Thickness(8);
                (_cameraHost.HorizontalAlignment, _cameraHost.VerticalAlignment) = _preview.Corner switch
                {
                    PreviewCorner.TopLeft => (HorizontalAlignment.Left, VerticalAlignment.Top),
                    PreviewCorner.TopRight => (HorizontalAlignment.Right, VerticalAlignment.Top),
                    PreviewCorner.BottomLeft => (HorizontalAlignment.Left, VerticalAlignment.Bottom),
                    PreviewCorner.BottomRight => (HorizontalAlignment.Right, VerticalAlignment.Bottom),
                    _ => throw new ArgumentOutOfRangeException(nameof(_preview.Corner)),
                };
            }
            else
            {
                _cameraHost.Width = double.NaN;
                _cameraHost.Height = double.NaN;
                _cameraHost.Margin = new Thickness(0);
                _cameraHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                _cameraHost.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }

        /// <summary>
        /// Draw one publish from the frame feed. Runs on the UI thread and does nothing but assign
        /// already-decoded, already-frozen bitmaps - the file reading and JPEG decoding happened on
        /// the feed's own thread (repo coding standard 1).
        /// </summary>
        private void ShowFrames(PreviewSnapshot frames)
        {
            if (_finishing || !_preview.Visible) return;
            _screenImage.Source = _preview.ShowScreenLayer ? frames.Screen : null;
            _cameraImage.Source = _preview.ShowCameraLayer ? frames.Camera : null;
            UpdatePreviewStatus(frames.ScreenStale, frames.CameraStale);
        }

        /// <summary>
        /// The panel's own account of itself, in the message line and in the UI Automation status
        /// text. A track that is being shown and has no fresh frame SAYS SO - it never leaves the
        /// last picture it managed to read on screen, which would present a dead preview as a live
        /// one (AC10).
        /// </summary>
        private void UpdatePreviewStatus(bool screenStale, bool cameraStale)
        {
            if (!_preview.Visible)
            {
                _previewStatus.Text = "hidden";
                _previewMessage.Visibility = Visibility.Collapsed;
                return;
            }

            string what = PreviewNames.Text(_preview.Mode)
                + (_preview.CameraIsInset ? " " + PreviewNames.Text(_preview.Corner) : "");

            bool waiting = (_preview.ShowScreenLayer && screenStale) || (_preview.ShowCameraLayer && cameraStale);
            bool nothingToShow = !_preview.ShowScreenLayer && !_preview.ShowCameraLayer;

            string? unavailable = _preview.UnavailableMessage;
            if (nothingToShow)
            {
                _previewMessage.Text = unavailable ?? "Nothing to preview.";
                _previewMessage.Visibility = Visibility.Visible;
                _previewStatus.Text = $"{what} | unavailable";
            }
            else if (waiting)
            {
                _previewMessage.Text = "Preview unavailable - no frames from the recorder. "
                                     + "The recording is unaffected.";
                _previewMessage.Visibility = Visibility.Visible;
                _previewStatus.Text = $"{what} | no frames";
            }
            else
            {
                _previewMessage.Text = unavailable ?? "";
                _previewMessage.Visibility = unavailable != null ? Visibility.Visible : Visibility.Collapsed;
                _previewStatus.Text = $"{what} | live";
            }
        }

        /// <summary>
        /// Serve UI Automation's TransformPattern from WPF rather than from the default HWND
        /// provider, so an accessibility tool's (or QA's) resize arrives as a typed COMMAND that
        /// this window can attribute to a person - see <see cref="HudUserResize"/>. Everything else
        /// about the HUD's automation surface is the base peer's, unchanged.
        /// </summary>
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
            _userResize.CreatePeer();

        /// <summary>
        /// Persist what the person just chose. Runs on the UI thread and must therefore not touch a
        /// disk: the config is serialised here and WRITTEN on a background thread, because the
        /// dispatcher this returns to is the one that serves the Stop button (Review Gate round 1 on
        /// PR #34).
        /// </summary>
        private void SavePreviewChoices()
        {
            _cfg.HudPreviewVisible = _preview.Visible;
            _cfg.HudPreviewMode = PreviewNames.Text(_preview.Mode);
            _cfg.HudPreviewCorner = PreviewNames.Text(_preview.Corner);
            _cfg.SaveWithoutBlockingTheUiThread();
        }

        /// <summary>Stop reading frames and stop the taps writing them. Safe to call twice.</summary>
        private void ClosePreview()
        {
            _feed.Want(null, false, null, false);
            _feed.Dispose();
            _svc.SetPreviewPublishing(false);
        }

        // ---- placement ------------------------------------------------------

        private void Position()
        {
            double w = ActualWidth, h = ActualHeight;
            var area = SystemParameters.WorkArea;
            double left = _cfg.HudLeft ?? area.Right - w - 16;
            double top = _cfg.HudTop ?? area.Top + 16;
            // Clamp into the virtual screen so a stale saved position cannot strand it.
            left = Math.Max(SystemParameters.VirtualScreenLeft,
                Math.Min(left, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - w));
            top = Math.Max(SystemParameters.VirtualScreenTop,
                Math.Min(top, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - h));
            Left = left; Top = top;
        }

        private void SavePosition()
        {
            _cfg.HudLeft = Left;
            _cfg.HudTop = Top;
            // Size comes from the memory, NOT from the window (issue #33, AC7). This runs from the
            // Closed handler, and by then the stop has already put SizeToContent back to
            // WidthAndHeight - so the window's live size is the pill's, and a guard on the sizing
            // mode would simply write nothing. The memory holds the last size the HUD had while it
            // was manually sized, which is the size the person actually left the panel at. It stays
            // null when the panel was never shown, so an unresized HUD still saves no size at all.
            if (_size.HasSize)
            {
                _cfg.HudWidth = _size.Width;
                _cfg.HudHeight = _size.Height;
            }
            Log.Info($"hud: saving position left={_cfg.HudLeft} top={_cfg.HudTop} "
                   + $"width={_cfg.HudWidth?.ToString() ?? "none"} height={_cfg.HudHeight?.ToString() ?? "none"}");
            // Closed is a UI-thread lifecycle handler, and the app carries on running after the HUD
            // goes: a synchronous write here stalls the dispatcher for every other window too. The
            // write is flushed at application exit (App.OnExit).
            _cfg.SaveWithoutBlockingTheUiThread();
        }

        // ---- window styles ----------------------------------------------------

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern long GetWindowLongPtr(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private void ApplyWindowStyles()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                // No focus stealing from the app being recorded; no Alt-Tab entry.
                SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                    GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                // Leave the HUD out of screen captures (verified working with the
                // ffmpeg recorder). MQS_HUD_CAPTURABLE=1 opts out - for demos and
                // docs that deliberately want the HUD visible in the capture.
                //
                // Issue #33 leans on this and must not relax it (assumption C5): it is what stops a
                // screen preview drawn inside this window recursing into a mirror tunnel, and what
                // keeps the HUD and its preview out of recording.mp4 (AC6).
                if (Environment.GetEnvironmentVariable("MQS_HUD_CAPTURABLE") != "1")
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
            catch (Exception ex) { Log.Error("hud window styles", ex); }
        }
    }
}
