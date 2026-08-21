using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Ai;
using AgentEyes.DevThrottle;
using Drawing = System.Drawing;

namespace AgentEyes.App
{
    /// <summary>
    /// The launcher. Day to day this is just "pick a preset and record" - all the detailed capture knobs
    /// live in the PresetEditor, reached from the File menu. The selected preset drives the recording.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly RecordingService _svc;
        private readonly Config _cfg;
        private readonly Action _showTests;
        private readonly List<MonitorInfo> _monitors = new();
        private readonly ObservableCollection<CapturePreset> _presets = new();
        // Issue #3: the Library's rows and the ORDERING between the reloads in flight and the changes
        // the user makes while they are. Every route that reads or changes the Library goes through
        // it; the collection underneath refuses a change made any other way.
        private readonly LibraryCoherence _library = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };

        // Issue #142: the repair passes (missing titles, missing thumbnails) are owned by the app,
        // not by this window - the app normally runs with --tray and no window at all, and
        // recordings are driven through the REST Control API just as often as through this UI.
        private readonly RepairService _repair;

        private MonitorHighlight? _highlight;
        private CapturePreset? _selectedPreset;   // active preset (issue #21: dropdown, not a list)

        private const long LowCreditWarningThresholdMicros = 1_000_000;

        internal MainWindow(RecordingService svc, Config cfg, Action showTests, RepairService repair)
        {
            _svc = svc;
            _cfg = cfg;
            _showTests = showTests;
            _repair = repair;
            InitializeComponent();
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            RecentList.ItemsSource = _library.Rows;
            _library.SortKeyChanged = ResortLibrary;
            _timer.Tick += OnTick;

            // Library view (issue #19, flattened by issue #178): ONE FLAT LIST, newest first, plus
            // title search over the same collection. The order is an EXPLICIT sort on the view, by
            // the recording start time that came out of manifest.json - never the directory name,
            // never a filesystem date, never "now". There is deliberately no GroupDescription: day
            // grouping is what rendered each group's cards under another group's header, and it is
            // not coming back.
            if (System.Windows.Data.CollectionViewSource.GetDefaultView(_library.Rows)
                is not System.Windows.Data.ListCollectionView recentView)
                throw new InvalidOperationException(
                    "The library's default view is not a ListCollectionView, so the newest-first sort "
                    + "cannot be applied. The library collection must stay an ObservableCollection<RecentItem>.");
            recentView.CustomSort = RecentItem.NewestFirst;
            recentView.Filter = o => _searchText.Length == 0
                || (o as RecentItem)?.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true;
            _library.Rows.CollectionChanged += (_, _) => UpdateEmptyState();

            // Issue #175: the first paint goes through the same single configurator as every later
            // mode switch, so the panel and its virtualization can never disagree - not even once.
            ApplyLibraryMode();

            try
            {
                _monitors.AddRange(Monitors.All());
                LoadPresets();
                LoadRecent();
            }
            catch (Exception ex) { StatusText.Text = "Init error: " + ex.Message; }

            // Issue #178: there is no midnight rebuild any more, and no minute timer asking whether
            // the date rolled over. It existed only because the library spoke in labels relative to
            // "now" - the "Today"/"Yesterday" group headers and a "today 8:03 AM" card label - which
            // went stale at midnight. Every date the library shows is now the recording's own start
            // time, stated absolutely, so nothing about it changes when the clock does.

            // Capture gallery (issue #64): refresh live when a snip is saved (shortcut or API).
            CaptureList.ItemsSource = _captures;
            if (Application.Current is App app)
                app.CaptureSaved += OnCaptureSaved;

            // DevThrottle sign-in indicator (issue #129). Paint it now, then follow every later
            // change - a 401 from any call, or a sign-in/sign-out from Settings.
            RefreshAccountIndicator();
            AccountState.Changed += OnAccountStateChanged;

            // Issue #142: the repair service runs with or without this window. While the window IS
            // open it borrows the window for feedback - status line, library reload, the credits
            // toast - and hands all of it back when the window closes. Every callback arrives on a
            // background thread, so each one marshals to the UI thread (CLAUDE.md: UI thread safety).
            _repair.Status = text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
            _repair.LibraryChanged = () => Dispatcher.BeginInvoke(() =>
            {
                LoadRecent();
                UpdateLibraryTotal();   // a repaired recording may have brought its AI cost with it
            });
            _repair.CreditsExhausted = () => Dispatcher.BeginInvoke(ShowDevThrottleCreditsWarning);

            // Issue #151: the post-recording sequence runs on whichever path stopped the recording
            // (window, HUD, tray, tray Quit, REST) and lives in PostRecording. While this window is
            // open it lends the sequence a voice for failures - the "out of credits" toast used to
            // exist only on the window's own private copy of the pipeline.
            PostRecording.Failed += OnPostRecordingFailed;

            Closed += (_, _) =>
            {
                AccountState.Changed -= OnAccountStateChanged;
                PostRecording.Failed -= OnPostRecordingFailed;
                _repair.Status = null;
                _repair.LibraryChanged = null;
                _repair.CreditsExhausted = null;
            };

            // Repair anything a previous session left unfinished (issues #132/#152). Opening the
            // window is a nudge, NOT the trigger: the pass belongs to the app-level RepairService,
            // which runs it 20 seconds after every launch, on every tick and on sign-in - including
            // in --tray mode, where this window is never constructed and the old window-owned
            // backfill therefore never ran at all. Deferred to Loaded so the window paints first; the
            // pass talks to the network and must never hold up the UI (CLAUDE.md: responsive UI).
            Loaded += (_, _) => _ = _repair.RunAsync("main window opened");
        }

        // ---- DevThrottle sign-in indicator (issue #129) ---------------------

        /// <summary>
        /// AccountState raises on whatever thread saw the change (a 401 arrives on a background
        /// transcription thread), so marshal to the UI thread before touching the rail.
        /// </summary>
        private void OnAccountStateChanged()
        {
            try
            {
                // Signing back in is exactly when the backlog can finally be cleared, so the
                // recordings a dead key cost you repair themselves (issues #132/#152). The
                // app-level RepairService listens to this same event and runs that pass - the
                // window only has to repaint the indicator.
                Dispatcher.BeginInvoke(new Action(RefreshAccountIndicator));
            }
            catch (Exception ex)
            {
                Log.Info($"[MainWindow] OnAccountStateChanged FAILED: {ex.Message}");
            }
        }

        /// <summary>Paints the rail account item for the current state. UI thread only.</summary>
        private void RefreshAccountIndicator()
        {
            bool signedIn = AccountState.IsSignedIn;
            var d = AccountState.Describe(signedIn, AccountState.Email);

            RailAccount.Tag = d.Label;
            RailAccount.ToolTip = d.ToolTip;
            System.Windows.Automation.AutomationProperties.SetName(RailAccount, d.AutomationName);
            RailAccountBadge.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            RailAccountGlyph.Stroke = signedIn
                ? (Brush)FindResource("RdDim")
                : new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));

            Log.Info($"[MainWindow] RefreshAccountIndicator: signedIn={signedIn}");
        }

        /// <summary>The account item opens Settings on the Account tab - one click to reconnect.</summary>
        private void RailAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                new SettingsDialog(_cfg, "Account") { Owner = this }.ShowDialog();
                AccountState.Refresh();
            }
            catch (Exception ex)
            {
                Log.Info($"[MainWindow] RailAccount_Click FAILED: {ex.Message}");
                StatusText.Text = "Could not open account settings: " + ex.Message;
            }
        }

        private CapturePreset? Selected => _selectedPreset;

        // ---- shell: rail + top bar (issue #18) -----------------------------

        /// <summary>Rail navigation: show the checked view, retitle the top bar.</summary>
        private void Rail_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent (RailRecord starts checked) - panels not built yet.
            if (RecordPanel == null || LibraryPanel == null
                || DictionaryPanel == null || CaptureViewPanel == null) return;

            bool record = ReferenceEquals(sender, RailRecord);
            bool library = ReferenceEquals(sender, RailLibrary);
            bool dictionary = ReferenceEquals(sender, RailDictionary);
            bool capture = ReferenceEquals(sender, RailCapture);
            RecordPanel.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
            LibraryPanel.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
            DictionaryPanel.Visibility = dictionary ? Visibility.Visible : Visibility.Collapsed;
            CaptureViewPanel.Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
            LibraryControls.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
            ViewTitle.Text = record ? "Record" : library ? "Library"
                : dictionary ? "Dictionary" : "Capture";
            // Issue #4 round 2: re-derive every card's artifact chips from disk each time the
            // Library is shown. transcript.json can be deleted or created outside the app while
            // the user is on another view (the card's own Open-folder action invites it), and the
            // cards must agree with the canonical predicate - and so with the Control API, which
            // re-reads the disk on every request - the moment they are visible again.
            if (library) _library.RefreshArtifactChips();
            if (dictionary) LoadDictionary();
            if (capture)
            {
                UpdateCaptureShortcutLabels();
                UpdateCaptureSaveFolderLabel();
                BuildCaptureMonitorPicker();
                LoadCaptures();
            }
        }

        /// <summary>The dictionary is its own rail view (it serves both recording
        /// recording transcription).</summary>
        private void ManageDictionary_Click(object sender, RoutedEventArgs e) =>
            RailDictionary.IsChecked = true;

        // ---- dictionary view (issue #37 follow-up) ---------------------------

        /// <summary>One row of the dictionary list: a term and its misheard forms.</summary>
        private sealed class DictRow
        {
            public string Term { get; set; } = "";
            public List<string> Variants { get; } = new();
            public string VariantsLabel => Variants.Count == 0
                ? "no misheard forms recorded yet"
                : "misheard as: " + string.Join(", ", Variants);
        }

        private readonly List<DictRow> _dictRows = new();
        private string _dictFilter = "";

        /// <summary>Read dictionary.json into the list. Terms = vocabulary plus any
        /// mistranscription keys (hand-edited files may have either).</summary>
        private async void LoadDictionary()
        {
            // Read + parse dictionary.json off the UI thread (this runs on the Dictionary
            // tab switch); building the rows below is cheap and stays on the UI thread.
            var dict = await Ui.Run(() => AgentEyes.Transcription.DictionaryStore.Load());
            _dictRows.Clear();
            var terms = dict.Vocabulary
                .Concat(dict.CommonMistranscriptions.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
            foreach (var term in terms)
            {
                var row = new DictRow { Term = term };
                if (dict.CommonMistranscriptions.TryGetValue(term, out var variants))
                    row.Variants.AddRange(variants);
                _dictRows.Add(row);
            }
            ApplyDictFilter();
        }

        /// <summary>Write-through: every edit lands in dictionary.json immediately.
        /// Transcript correction Load()s per use, so the very next transcription
        /// picks the change up.</summary>
        private void SaveDictionary()
        {
            var dict = new AgentEyes.Transcription.TranscriptionDictionary(
                _dictRows.Select(r => r.Term).ToList(),
                _dictRows.Where(r => r.Variants.Count > 0)
                    .ToDictionary(r => r.Term, r => (IReadOnlyList<string>)r.Variants.ToList()));
            AgentEyes.Transcription.DictionaryStore.Save(
                AgentEyes.Transcription.DictionaryStore.DefaultPath, dict);
            ApplyDictFilter();
        }

        private void ApplyDictFilter()
        {
            var rows = _dictFilter.Length == 0
                ? _dictRows
                : _dictRows.Where(r => r.Term.Contains(_dictFilter, StringComparison.OrdinalIgnoreCase)
                    || r.Variants.Any(v => v.Contains(_dictFilter, StringComparison.OrdinalIgnoreCase))).ToList();
            DictList.ItemsSource = null;
            DictList.ItemsSource = rows;
            DictEmptyText.Text = _dictRows.Count == 0
                ? "No terms yet. Add the names and jargon transcription keeps getting wrong."
                : rows.Count == 0 ? "No terms match the search." : "";
            DictEmptyText.Visibility = DictEmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DictSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _dictFilter = DictSearch.Text.Trim();
            DictSearchHint.Visibility = DictSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyDictFilter();
        }

        private void DictNewTerm_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            DictNewTermHint.Visibility = Visibility.Collapsed;
            if (e.Key == System.Windows.Input.Key.Enter) DictAdd_Click(sender, e);
        }

        private void DictAdd_Click(object sender, RoutedEventArgs e)
        {
            string term = DictNewTerm.Text.Trim();
            if (term.Length == 0) return;
            var existing = _dictRows.FirstOrDefault(r => string.Equals(r.Term, term, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                StatusText.Text = $"\"{existing.Term}\" is already in the dictionary.";
                return;
            }
            _dictRows.Add(new DictRow { Term = term });
            _dictRows.Sort((a, b) => string.Compare(a.Term, b.Term, StringComparison.OrdinalIgnoreCase));
            SaveDictionary();
            DictNewTerm.Text = "";
            DictNewTermHint.Visibility = Visibility.Visible;
            StatusText.Text = $"Added \"{term}\" to the dictionary.";
        }

        private static DictRow? DictRowFrom(object sender) =>
            (sender as FrameworkElement)?.DataContext as DictRow;

        private void DictAddVariant_Click(object sender, RoutedEventArgs e)
        {
            if (DictRowFrom(sender) is not { } row) return;
            string? variant = PromptDialog.Ask(this, "Add misheard form", $"\"{row.Term}\" gets misheard as:", "");
            if (string.IsNullOrWhiteSpace(variant)) return;
            variant = variant.Trim();
            // Ordinal: casing matters - distinct casings are distinct wrong forms.
            if (!row.Variants.Contains(variant, StringComparer.Ordinal)) row.Variants.Add(variant);
            SaveDictionary();
        }

        private void DictEdit_Click(object sender, RoutedEventArgs e)
        {
            if (DictRowFrom(sender) is not { } row) return;
            var dlg = new DictTermDialog(row.Term, row.Variants) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            row.Term = dlg.TermText;
            row.Variants.Clear();
            row.Variants.AddRange(dlg.Variants);
            _dictRows.Sort((a, b) => string.Compare(a.Term, b.Term, StringComparison.OrdinalIgnoreCase));
            SaveDictionary();
        }

        private void DictDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DictRowFrom(sender) is not { } row) return;
            if (MessageBox.Show($"Remove \"{row.Term}\" and its misheard forms from the dictionary?",
                    "AgentEyes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _dictRows.Remove(row);
            SaveDictionary();
        }

        private static string TriggerLabel(string trigger)
        {
            string k = (trigger ?? "").ToLowerInvariant();
            if (k.Contains("shift")) return "Shift";
            if (k.Contains("alt")) return "Alt";
            return "Ctrl";
        }

        // ---- capture view (issue #64) ---------------------------------------

        /// <summary>One saved snip in the Capture gallery: its PNG, a thumbnail, and a label.</summary>
        private sealed class CaptureRow : System.ComponentModel.INotifyPropertyChanged
        {
            private System.Windows.Media.ImageSource? _thumb;
            public string File { get; init; } = "";
            public string Title { get; set; } = "";
            public System.Windows.Media.ImageSource? Thumb
            {
                get => _thumb;
                set { _thumb = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb))); }
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            // List items expose this as their UI Automation name (matches RecentItem).
            public override string ToString() => Title;
        }

        private readonly ObservableCollection<CaptureRow> _captures = new();

        /// <summary>The monitor the Capture tab's "New capture" / full-screen snip targets (AC11).
        /// 1-based, defaults to the primary; the picker updates it. Single-monitor setups keep 1.</summary>
        private int _selectedCaptureMonitor = 1;

        /// <summary>Show the configured shortcuts (issue #64). Reads the real config, not a guess.</summary>
        private void UpdateCaptureShortcutLabels()
        {
            RegionShortcutText.Text = TriggerSpec.Parse(_cfg.CaptureRegionTrigger).Label();
            FullShortcutText.Text = TriggerSpec.Parse(_cfg.CaptureFullTrigger).Label();
            CaptureEmptyHint.Text = $"Press {RegionShortcutText.Text} to snip a region, "
                + $"or {FullShortcutText.Text} for the full screen.";
        }

        // ---- capture: save folder (AC9/AC10) -------------------------------

        /// <summary>Show the active save folder in the Capture-tab Settings field. Blank config =
        /// the Windows Screenshots known folder (AC9); a set value is the override (AC10). The
        /// resolve runs off the UI thread (it P/Invokes the shell) so the tab opens instantly.</summary>
        private async void UpdateCaptureSaveFolderLabel()
        {
            string? configured = _cfg.CaptureSaveFolder;
            string resolved;
            try { resolved = await Task.Run(() => CaptureService.ResolveSaveFolder(configured)); }
            catch (Exception ex) { Log.Error("resolve capture folder", ex); resolved = configured ?? ""; }
            CaptureSaveFolderText.Text = resolved;
            CaptureSaveFolderDefaultHint.Visibility = string.IsNullOrWhiteSpace(configured)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Browse for a new save folder (AC10). The choice persists to config immediately and
        /// the very next capture lands in it; "Use default" clears the override back to Screenshots.</summary>
        private void CaptureBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Choose where captures are saved",
                    InitialDirectory = CaptureSaveFolderText.Text,
                };
                if (dlg.ShowDialog(this) != true) return;
                _cfg.CaptureSaveFolder = dlg.FolderName;
                _cfg.Save();
                UpdateCaptureSaveFolderLabel();
                LoadCaptures();
                StatusText.Text = "Captures will be saved to " + dlg.FolderName;
            }
            catch (Exception ex)
            {
                Log.Error("change capture save folder", ex);
                StatusText.Text = "Could not change the save folder: " + ex.Message;
            }
        }

        private void CaptureDefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cfg.CaptureSaveFolder = null;
                _cfg.Save();
                UpdateCaptureSaveFolderLabel();
                LoadCaptures();
                StatusText.Text = "Captures will be saved to the Windows Screenshots folder.";
            }
            catch (Exception ex)
            {
                Log.Error("reset capture save folder", ex);
                StatusText.Text = "Could not reset the save folder: " + ex.Message;
            }
        }

        // ---- capture: monitor picker (AC11) --------------------------------

        /// <summary>Draw a clickable monitor layout in the Capture tab (AC11), reusing the recording
        /// home screen's to-scale arrangement (UpdateMonitorStrip). One target per monitor; clicking
        /// one selects it and snaps a full-screen capture of exactly that monitor's bounds. With a
        /// single monitor the picker is collapsed - there is nothing to pick (CaptureService
        /// .ShouldShowMonitorPicker, unit-tested).</summary>
        private void BuildCaptureMonitorPicker()
        {
            CaptureMonitorCanvas.Children.Clear();
            if (!CaptureService.ShouldShowMonitorPicker(_monitors.Count))
            {
                CaptureMonitorSection.Visibility = Visibility.Collapsed;
                _selectedCaptureMonitor = _monitors.Count >= 1 ? _monitors[0].Index : 1;
                return;
            }
            CaptureMonitorSection.Visibility = Visibility.Visible;

            // Keep the selection valid if monitors changed since last shown.
            if (_monitors.All(m => m.Index != _selectedCaptureMonitor))
                _selectedCaptureMonitor = _monitors[0].Index;

            int minX = _monitors.Min(m => m.X), minY = _monitors.Min(m => m.Y);
            int maxX = _monitors.Max(m => m.X + m.Width), maxY = _monitors.Max(m => m.Y + m.Height);
            double unionW = Math.Max(1, maxX - minX), unionH = Math.Max(1, maxY - minY);
            double scale = Math.Min(CaptureMonitorCanvas.Width / unionW, CaptureMonitorCanvas.Height / unionH);
            double offX = (CaptureMonitorCanvas.Width - unionW * scale) / 2;
            double offY = (CaptureMonitorCanvas.Height - unionH * scale) / 2;

            var accent = (SolidColorBrush)FindResource("RdAccent");
            var surface = (Brush)FindResource("RdSurface");
            var stroke = (Brush)FindResource("RdStroke");
            var muted = (Brush)FindResource("RdMuted");

            foreach (var m in _monitors)
            {
                bool sel = m.Index == _selectedCaptureMonitor;
                var cellVisual = new Border
                {
                    Background = sel ? Translucent(accent, 34) : surface,
                    BorderBrush = sel ? accent : stroke,
                    BorderThickness = new Thickness(sel ? 1.8 : 1),
                    CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        Text = m.Index.ToString(),
                        FontSize = 12, FontWeight = sel ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = sel ? accent : muted,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                // A real Button (not a bare Border) so the target is a proper UIA click element:
                // it carries an InvokePattern and surfaces by name in the automation tree (AC11
                // requires QA to enumerate the per-monitor targets). The template makes it look
                // like the to-scale monitor cell.
                var cell = new Button
                {
                    Width = Math.Max(14, m.Width * scale - 3),
                    Height = Math.Max(14, m.Height * scale - 3),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = m.Index,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    ToolTip = $"Monitor {m.Index}  -  {m.Width} x {m.Height}"
                        + (m.Primary ? " (primary)" : "") + "   Click to capture",
                    Template = CellButtonTemplate(),
                    Content = cellVisual,
                };
                System.Windows.Automation.AutomationProperties.SetName(cell, $"Capture monitor {m.Index}");
                cell.Click += CaptureMonitor_Click;
                Canvas.SetLeft(cell, offX + (m.X - minX) * scale);
                Canvas.SetTop(cell, offY + (m.Y - minY) * scale);
                CaptureMonitorCanvas.Children.Add(cell);
            }
            CaptureMonitorHint.Text = $"Click a monitor to capture it. Selected: Monitor {_selectedCaptureMonitor}.";
        }

        /// <summary>A bare ControlTemplate so the monitor-picker Buttons render exactly as the
        /// bordered cell content with no default Button chrome.</summary>
        private static ControlTemplate CellButtonTemplate()
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
            return tpl;
        }

        /// <summary>A monitor cell was clicked: select it and snap a full-screen capture of that
        /// monitor (AC11). The same Core path as the shortcut + Control API.</summary>
        private void CaptureMonitor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if ((sender as FrameworkElement)?.Tag is not int index) return;
                _selectedCaptureMonitor = index;
                BuildCaptureMonitorPicker();   // repaint the selection highlight
                if (Application.Current is App app) app.CaptureFullScreenAndNotify(index);
                LoadCaptures();
            }
            catch (Exception ex)
            {
                Log.Error("capture monitor click", ex);
                StatusText.Text = "Capture error: " + ex.Message;
            }
        }

        /// <summary>Build the gallery from disk. Files + thumbnails are read and decoded on a
        /// worker thread; only the collection swap touches the UI thread (responsive-UI rule).</summary>
        private async void LoadCaptures()
        {
            List<CaptureRow> rows = new();
            try
            {
                rows = await Task.Run(() =>
                {
                    var list = new List<CaptureRow>();
                    foreach (var info in CaptureService.List(_cfg.CaptureSaveFolder))
                    {
                        var row = new CaptureRow
                        {
                            File = info.File,
                            Title = (info.Width > 0 ? $"{info.Width} x {info.Height}   " : "")
                                + info.CreatedLocal.ToString("MMM d  h:mm tt"),
                        };
                        row.Thumb = LoadCaptureThumb(info.File);
                        list.Add(row);
                    }
                    return list;
                });
            }
            catch (Exception ex) { Log.Error("load captures", ex); }

            _captures.Clear();
            foreach (var row in rows) _captures.Add(row);
            UpdateCaptureEmptyState();
        }

        /// <summary>Decode a capture PNG to a frozen thumbnail (safe to hand to the UI thread).</summary>
        private static System.Windows.Media.ImageSource? LoadCaptureThumb(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(file);
                bmp.DecodePixelWidth = 480;
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) { Log.Error("capture thumb " + file, ex); return null; }
        }

        private void UpdateCaptureEmptyState() =>
            CaptureEmptyState.Visibility = _captures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>A snip was just saved (shortcut, New-capture button, or the Control API).
        /// Re-read the gallery so the new row appears live. The App raises this on the UI thread;
        /// BeginInvoke keeps us safe even if a caller ever raises it from another thread.</summary>
        private void OnCaptureSaved(string file)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Log.Info("[MainWindow] OnCaptureSaved: refreshing gallery for " + file);
                LoadCaptures();
                StatusText.Text = "Capture saved + copied to clipboard.";
            });
        }

        private void NewCapture_Click(object sender, RoutedEventArgs e)
        {
            // Full-screen snip of the monitor chosen in the picker (AC11); single-monitor setups
            // always target monitor 1. Reuses the same path as the shortcut + Control API.
            if (Application.Current is App app) app.CaptureFullScreenAndNotify(_selectedCaptureMonitor);
            LoadCaptures();
        }

        private void RegionSnip_Click(object sender, RoutedEventArgs e)
        {
            // The overlay needs the screen clear; hide the window while the user drags.
            if (Application.Current is not App app) return;
            var saved = WindowState;
            WindowState = WindowState.Minimized;
            try { app.CaptureRegionInteractive(); }
            finally { WindowState = saved; }
            LoadCaptures();
        }

        private static CaptureRow? CaptureRowFrom(object sender) =>
            (sender as FrameworkElement)?.DataContext as CaptureRow;

        private void CaptureOpen_Click(object sender, RoutedEventArgs e)
        {
            if (CaptureRowFrom(sender) is not { } row || !File.Exists(row.File)) return;
            Process.Start(new ProcessStartInfo(row.File) { UseShellExecute = true });
        }

        private void CaptureCopy_Click(object sender, RoutedEventArgs e)
        {
            if (CaptureRowFrom(sender) is not { } row || !File.Exists(row.File)) return;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(row.File);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                Clipboard.SetImage(bmp);
                StatusText.Text = "Copied to clipboard.";
            }
            catch (Exception ex) { StatusText.Text = "Copy error: " + ex.Message; }
        }

        private void CaptureReveal_Click(object sender, RoutedEventArgs e)
        {
            if (CaptureRowFrom(sender) is not { } row || !File.Exists(row.File)) return;
            // Select the file in Explorer so the user lands on it, not just the folder.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.File}\"") { UseShellExecute = true });
        }

        private void CaptureDelete_Click(object sender, RoutedEventArgs e)
        {
            if (CaptureRowFrom(sender) is not { } row) return;
            try
            {
                CaptureService.Delete(row.File);
                _captures.Remove(row);
                UpdateCaptureEmptyState();
                StatusText.Text = "Capture deleted.";
            }
            catch (Exception ex) { StatusText.Text = "Delete error: " + ex.Message; }
        }

        private void RebindRegion_Click(object sender, RoutedEventArgs e) => RebindCapture(region: true);
        private void RebindFull_Click(object sender, RoutedEventArgs e) => RebindCapture(region: false);

        /// <summary>Rebind a capture shortcut. The new combo is validated by TriggerSpec (no silent
        /// accept of an unusable key), persisted, and the live hooks are re-armed immediately.</summary>
        private void RebindCapture(bool region)
        {
            string current = region ? _cfg.CaptureRegionTrigger : _cfg.CaptureFullTrigger;
            string label = region ? "region" : "full-screen";
            string? entry = PromptDialog.Ask(this, $"Rebind {label} capture",
                "Shortcut (e.g. ctrl+printscreen, alt+s, f9, printscreen):",
                TriggerSpec.Parse(current).Label());
            if (string.IsNullOrWhiteSpace(entry)) return;

            var spec = TriggerSpec.TryParseHotkey(entry.Trim(), out string? error);
            if (spec == null)
            {
                StatusText.Text = "Invalid shortcut: " + error;
                return;
            }
            string serialized = spec.Serialize();
            if (region) _cfg.CaptureRegionTrigger = serialized; else _cfg.CaptureFullTrigger = serialized;
            _cfg.Save();
            if (Application.Current is App app) app.ReArmCaptureHooks();
            UpdateCaptureShortcutLabels();
            StatusText.Text = $"{(region ? "Region" : "Full-screen")} capture rebound to {spec.Label()}.";
        }

        /// <summary>Top-bar overflow: low-frequency commands live in its menu.</summary>
        private void Overflow_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

        private void CheckUpdates_Click(object sender, RoutedEventArgs e) => UpdateChecker.CheckAndPrompt();

        // ---- presets (issue #21: split-dropdown + Manage Presets dialog) ----

        private void LoadPresets()
        {
            _presets.Clear();
            foreach (var p in PresetStore.Load()) _presets.Add(p);
            SelectPreset(_cfg.LastUsedPresetId);
        }

        private void SelectPreset(string? id)
        {
            _selectedPreset = _presets.FirstOrDefault(p => p.Id == id) ?? _presets.FirstOrDefault();
            UpdateSummary();
        }

        /// <summary>Refresh the split control (name + one-line summary) and the hero label.</summary>
        private void UpdateSummary()
        {
            var p = Selected;
            PresetNameText.Text = p?.Name ?? "No preset";
            PresetSummaryText.Text = p == null
                ? "Create one under Manage presets"
                : p.Summary().Replace("\r", "").Replace('\n', ' ').Replace("  ", " ");
            if (!_svc.IsRecording)
                RecordButton.Content = p?.Mode == "shot" ? "CAPTURE" : "REC";
            UpdateCaptureChips();
        }

        // ---- capture summary chips (what the active preset records) -------

        // Outline icons are stroked; the cleanup star is filled (see BuildChip).
        private const string ChipVideoIcon = "M2,4 H18 V14 H2 Z M7,17 H13 M6,17.5 H14";
        private const string ChipAudioIcon = "M10,2 a3,3 0 0 1 3,3 v4 a3,3 0 0 1 -6,0 v-4 a3,3 0 0 1 3,-3 Z M5,9 a5,5 0 0 0 10,0 M10,14 v3 M7,17 h6";
        private const string ChipShotIcon = "M2,5 H6 L8,3 H12 L14,5 H18 V16 H2 Z M10,7 a3.2,3.2 0 1 0 0.01,0 Z";
        private const string ChipMicIcon = "M10,2 a3,3 0 0 1 3,3 v4 a3,3 0 0 1 -6,0 v-4 a3,3 0 0 1 3,-3 Z M5,9 a5,5 0 0 0 10,0 M10,14 v3 M7,17 h6";
        private const string ChipSpeakerIcon = "M2,7 H5 L9,3 V17 L5,13 H2 Z M12,7 a4,4 0 0 1 0,6 M14,5 a7,7 0 0 1 0,10";
        private const string ChipFxIcon = "M5,0 L6.2,3.8 L10,5 L6.2,6.2 L5,10 L3.8,6.2 L0,5 L3.8,3.8 Z";

        /// <summary>Rebuild the four state chips + monitor strip from the active preset.</summary>
        private void UpdateCaptureChips()
        {
            CaptureChipsPanel.Children.Clear();
            var p = Selected;
            if (p == null)
            {
                CaptureScreenText.Text = "";
                MonitorStripCanvas.Children.Clear();
                return;
            }

            var accent = (SolidColorBrush)FindResource("RdAccent");
            var green = (SolidColorBrush)FindResource("DkGreen");

            bool isShot = p.Mode == "shot";
            bool isAudioOnly = p.Mode == "audio";
            bool micOn = !isShot && (p.Source == "mic" || p.Source == "mixed");
            bool sysOn = !isShot && (p.Source == "system" || p.Source == "mixed");

            // Mode chip - always on; accent-coloured since it is the "what".
            string modeTitle = isShot ? "SCREENSHOT" : isAudioOnly ? "AUDIO" : "VIDEO";
            string modeL1 = isShot ? "still image" : isAudioOnly ? "+ shots" : $"{p.Fps} fps";
            string modeIcon = isShot ? ChipShotIcon : isAudioOnly ? ChipAudioIcon : ChipVideoIcon;
            CaptureChipsPanel.Children.Add(BuildChip(
                modeIcon, modeTitle, modeL1, $"Monitor {p.MonitorIndex}" + (p.UseRegion ? " region" : ""),
                on: true, lit: accent, filledIcon: false));

            // Mic chip - green when captured (an audio source is live).
            string micDev = string.IsNullOrWhiteSpace(p.Mic) ? "Default mic" : Truncate(p.Mic!, 12);
            CaptureChipsPanel.Children.Add(BuildChip(
                ChipMicIcon, "MIC",
                micOn ? micDev : "not captured", micOn ? $"{p.MicVol:F0}%" : "",
                on: micOn, lit: green, filledIcon: false));

            // System chip - the one whose being off gutted the meeting recording.
            CaptureChipsPanel.Children.Add(BuildChip(
                ChipSpeakerIcon, "SYSTEM",
                sysOn ? "loopback" : "not captured", sysOn ? $"{p.SysVol:F0}%" : "",
                on: sysOn, lit: green, filledIcon: false));

            // Cleanup chip - mic noise processing; accent.
            var fx = new List<string>();
            if (p.Denoise) fx.Add("denoise");
            if (p.Gate ?? GateDefaults.For(p.Source)) fx.Add("gate");   // issue #83: null = source default
            if (p.Level) fx.Add("level");
            bool fxOn = !isShot && micOn && fx.Count > 0;
            CaptureChipsPanel.Children.Add(BuildChip(
                ChipFxIcon, "CLEANUP",
                fxOn ? fx[0] : (isShot ? "n/a" : "off"),
                fxOn && fx.Count > 1 ? string.Join("+", fx.Skip(1)) : "",
                on: fxOn, lit: accent, filledIcon: true));

            UpdateMonitorStrip(p);
        }

        /// <summary>One capture chip: icon, title, two detail lines, ON/OFF pill. Lit = captured.</summary>
        private Border BuildChip(string iconData, string title, string l1, string l2, bool on, SolidColorBrush lit, bool filledIcon)
        {
            var dim = (Brush)FindResource("RdDim");
            var muted = (Brush)FindResource("RdMuted");
            var stroke = (Brush)FindResource("RdStroke");
            var surface = (Brush)FindResource("RdSurface");

            Brush edge = on ? lit : stroke;
            Brush head = on ? lit : muted;
            Brush body = on ? dim : muted;

            var stack = new StackPanel();

            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(iconData),
                Stretch = Stretch.Uniform,
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 9),
            };
            if (filledIcon) icon.Fill = head;
            else { icon.Stroke = head; icon.StrokeThickness = 1.5; }
            stack.Children.Add(icon);

            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = head, Margin = new Thickness(0, 0, 0, 4),
            });
            stack.Children.Add(new TextBlock
            {
                Text = l1, FontSize = 11, Foreground = body, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(l2))
                stack.Children.Add(new TextBlock
                {
                    Text = l2, FontSize = 11, Foreground = body, TextTrimming = TextTrimming.CharacterEllipsis,
                });

            stack.Children.Add(new Border
            {
                Background = on ? Translucent(lit, 38) : Brushes.Transparent,
                BorderBrush = edge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0),
                Child = new TextBlock { Text = on ? "ON" : "OFF", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = head },
            });

            return new Border
            {
                Width = 124, MinHeight = 120,
                Background = surface, BorderBrush = edge, BorderThickness = new Thickness(on ? 1.5 : 1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 11, 12, 11),
                Margin = new Thickness(5, 0, 5, 0),
                Child = stack,
            };
        }

        /// <summary>Draw the monitors to scale in their real arrangement; the captured one is filled.</summary>
        private void UpdateMonitorStrip(CapturePreset p)
        {
            MonitorStripCanvas.Children.Clear();
            if (_monitors.Count == 0) { CaptureScreenText.Text = ""; return; }

            var mon = _monitors.FirstOrDefault(m => m.Index == p.MonitorIndex) ?? _monitors[0];
            string area = p.UseRegion && p.Region is { Length: 4 }
                ? $"region {p.Region[2]} x {p.Region[3]}"
                : "full screen";
            CaptureScreenText.Text = $"Capturing Monitor {mon.Index}  -  {mon.Width} x {mon.Height}  ({area})";

            int minX = _monitors.Min(m => m.X), minY = _monitors.Min(m => m.Y);
            int maxX = _monitors.Max(m => m.X + m.Width), maxY = _monitors.Max(m => m.Y + m.Height);
            double unionW = Math.Max(1, maxX - minX), unionH = Math.Max(1, maxY - minY);
            double scale = Math.Min(MonitorStripCanvas.Width / unionW, MonitorStripCanvas.Height / unionH);
            double offX = (MonitorStripCanvas.Width - unionW * scale) / 2;
            double offY = (MonitorStripCanvas.Height - unionH * scale) / 2;

            var accent = (SolidColorBrush)FindResource("RdAccent");
            var surface = (Brush)FindResource("RdSurface");
            var stroke = (Brush)FindResource("RdStroke");
            var muted = (Brush)FindResource("RdMuted");

            foreach (var m in _monitors)
            {
                bool sel = m.Index == mon.Index;
                var cell = new Border
                {
                    Width = Math.Max(10, m.Width * scale - 3),
                    Height = Math.Max(10, m.Height * scale - 3),
                    Background = sel ? Translucent(accent, 34) : surface,
                    BorderBrush = sel ? accent : stroke,
                    BorderThickness = new Thickness(sel ? 1.6 : 1),
                    CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        Text = m.Index.ToString(),
                        FontSize = 11, FontWeight = sel ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = sel ? accent : muted,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Canvas.SetLeft(cell, offX + (m.X - minX) * scale);
                Canvas.SetTop(cell, offY + (m.Y - minY) * scale);
                MonitorStripCanvas.Children.Add(cell);
            }
        }

        private static SolidColorBrush Translucent(SolidColorBrush b, byte alpha)
            => new(Color.FromArgb(alpha, b.Color.R, b.Color.G, b.Color.B));

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max - 2) + "..";

        /// <summary>Both halves of the split control open the preset menu.</summary>
        private void PresetMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = PresetMenuButton.ContextMenu!;
            menu.Items.Clear();
            foreach (var p in _presets)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = p.Name,
                    ToolTip = p.Summary().Replace("\r", "").Replace('\n', ' '),
                    IsChecked = ReferenceEquals(p, _selectedPreset),
                };
                var preset = p;
                item.Click += (_, _) => SwitchPreset(preset);
                menu.Items.Add(item);
            }
            menu.Items.Add(new System.Windows.Controls.Separator());
            var newItem = new System.Windows.Controls.MenuItem { Header = "New preset..." };
            newItem.Click += (_, _) => OpenManagePresets(createNew: true);
            menu.Items.Add(newItem);
            var manageItem = new System.Windows.Controls.MenuItem { Header = "Manage presets..." };
            manageItem.Click += (_, _) => OpenManagePresets();
            menu.Items.Add(manageItem);

            menu.PlacementTarget = PresetMenuButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void SwitchPreset(CapturePreset p)
        {
            _selectedPreset = p;
            UpdateSummary();
            if (!_svc.IsRecording) FlashArea(1500);
        }

        private void OpenManagePresets(bool createNew = false)
        {
            if (_svc.IsRecording) { StatusText.Text = "Stop recording before editing presets."; return; }
            var dlg = new ManagePresetsDialog(_presets, _cfg, _selectedPreset) { Owner = this };
            if (createNew) dlg.Loaded += (_, _) => dlg.Dispatcher.BeginInvoke(dlg.New);
            dlg.ShowDialog();
            // The dialog may have added/removed/renamed/set-active - re-resolve.
            _selectedPreset = dlg.ActivePreset != null && _presets.Contains(dlg.ActivePreset)
                ? dlg.ActivePreset
                : _presets.FirstOrDefault();
            UpdateSummary();
        }

        private void EditCurrentPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_svc.IsRecording) { StatusText.Text = "Stop recording before editing the active preset."; return; }
            var p = Selected;
            if (p == null) { OpenManagePresets(createNew: true); return; }

            var dlg = new PresetEditor(p) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.SavedPreset == null) return;

            var saved = dlg.SavedPreset;
            if (!_presets.Contains(saved)) _presets.Add(saved);   // Save as
            _selectedPreset = saved;
            PresetStore.Save(_presets.ToList());
            RememberUsed(saved);
            UpdateSummary();
            FlashArea(1500);
            StatusText.Text = $"Updated \"{saved.Name}\".";
        }

        // ---- menu: other --------------------------------------------------

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsDialog(_cfg) { Owner = this }.ShowDialog();
            AccountState.Refresh();   // issue #129: sign-in/out may have happened in there
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }

        private void OpenRecordings_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(RecordingPaths.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RecordingPaths.Root}\"") { UseShellExecute = true });
        }

        /// <summary>Left-click opens the help menu (About / Diagnostics) next to the rail button.</summary>
        private void Help_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

        /// <summary>Open a button's ContextMenu on left-click (rail/help/overflow pattern).</summary>
        private static void OpenButtonMenu(object sender)
        {
            var b = (System.Windows.Controls.Button)sender;
            b.ContextMenu!.PlacementTarget = b;
            b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            b.ContextMenu.IsOpen = true;
        }

        private void Tests_Click(object sender, RoutedEventArgs e) => _showTests();

        private void About_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("AgentEyes v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?")
                + "\nScreen + audio recorder with saved presets.\n\nPick a preset and press REC.",
                "About AgentEyes");

        // ---- show area ----------------------------------------------------

        private void ShowArea_Click(object sender, RoutedEventArgs e)
        {
            if (_svc.IsRecording) return;
            FlashArea(2500);
        }

        private void FlashArea(int milliseconds)
        {
            var p = Selected;
            if (p == null) return;
            var mon = _monitors.FirstOrDefault(m => m.Index == p.MonitorIndex) ?? _monitors.FirstOrDefault();
            if (mon == null) return;
            _highlight?.Close();
            if (p.UseRegion && p.Region is { Length: 4 })
                _highlight = MonitorHighlight.Flash(new Drawing.Rectangle(p.Region[0], p.Region[1], p.Region[2], p.Region[3]),
                    $"Recording area   {p.Region[2]} x {p.Region[3]}", milliseconds);
            else
                _highlight = MonitorHighlight.Flash(mon.Bounds,
                    $"Monitor {mon.Index}   {mon.Width} x {mon.Height}", milliseconds);
        }

        // ---- record / stop ------------------------------------------------

        private async void Record_Click(object sender, RoutedEventArgs e)
        {
            if (_svc.IsRecording) { await StopAsync(); return; }
            var p = Selected;
            if (p == null) { StatusText.Text = "No preset selected."; return; }
            _highlight?.Close();   // the indicator goes away as soon as we start
            bool minimizedForCapture = false;

            try
            {
                if (p.Mode == "shot")
                {
                    minimizedForCapture = await MinimizeBeforeCaptureAsync();
                    string? file = PresetCapture.Start(_svc, p);
                    RememberUsed(p);
                    StatusText.Text = "Screenshot saved + copied to clipboard.";
                    if (file != null)
                    {
                        string dir = Path.GetDirectoryName(Path.GetDirectoryName(file))!;
                        var shot = _library.Insert(dir);
                        EnsureThumbInBackground(shot);
                    }
                    if (minimizedForCapture) RestoreAfterCaptureStartFailureOrScreenshot();
                    return;
                }

                RecordButton.IsEnabled = false;
                StatusText.Text = "Starting...";
                minimizedForCapture = p.Mode == "video" && await MinimizeBeforeCaptureAsync();
                await Task.Run(() => PresetCapture.Start(_svc, p));
                RememberUsed(p);

                RecordButton.IsEnabled = true;
                RecordButton.Content = "STOP";
                SetRecordingUi(true);
                StatusText.Text = p.Mode == "video" ? "Recording video..." : "Recording audio...";
                _timer.Start();
                ShowHud();
            }
            catch (Exception ex)
            {
                Log.Error($"record start failed (preset \"{p.Name}\")", ex);
                if (minimizedForCapture) RestoreAfterCaptureStartFailureOrScreenshot();
                RecordButton.IsEnabled = true;
                SetRecordingUi(false);
                UpdateSummary();
                StatusText.Text = "Error: " + ex.Message;
            }
        }

        private async Task<bool> MinimizeBeforeCaptureAsync()
        {
            if (WindowState == WindowState.Minimized) return false;
            WindowState = WindowState.Minimized;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);
            return true;
        }

        private void RestoreAfterCaptureStartFailureOrScreenshot()
        {
            try
            {
                WindowState = WindowState.Normal;
                Activate();
            }
            catch { /* best effort after a failed capture start */ }
        }

        /// <summary>
        /// The window's Stop button and the HUD's Stop. Issue #151: this used to run a PRIVATE
        /// staged copy of the post-recording sequence, which is how the tray, tray Quit and the REST
        /// API each ended up with a different (or missing) pipeline. The sequence now lives in one
        /// place - <see cref="RecordingStop.Keep"/> -> <see cref="PostRecording.Run"/> - and this
        /// window only DISPLAYS it, through the progress sinks below.
        /// </summary>
        private async Task StopAsync()
        {
            _timer.Stop();
            RecordButton.IsEnabled = false;

            // The Recent row is created by the Saved sink the moment the raw files land, so the
            // Processing sink always has a row to report on (issues #7/#8).
            RecentItem? item = null;
            var progress = new StopProgress
            {
                // Issue #77: the stop only flushes the raw files (near-instant); the audio mux is
                // deferred. These are the descriptive staged "Saving ..." labels that replaced the
                // old opaque "Finishing...". Every sink arrives on a background thread.
                Saving = text => Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = text;
                    _hud?.SetStatus(text);
                }),
                Saved = result => Dispatcher.Invoke(() => item = _library.Insert(result.Dir)),
                Processing = stage =>
                {
                    var row = item;
                    if (row == null) return;
                    // Decoding the poster is file I/O and stays on this background thread; the
                    // thumbnail exists by the time packaging starts (issue #141 ordering).
                    if (stage == PostRecording.StageTranscribing) row.LoadThumb();
                    Dispatcher.BeginInvoke(() =>
                    {
                        // Both go through the model: this row was captured before the stop's awaits,
                        // so it is exactly the "held across an await" case (issue #3, failure mode 5).
                        _library.SetStatus(row, stage);
                        _library.Refresh(row);
                    });
                },
            };

            StoppedRecording? stopped = null;
            string? error = null;
            try { stopped = await Ui.Run(() => RecordingStop.Keep(_svc, progress)); }
            catch (Exception ex) { error = LogStopFailure("stopping the recording", ex); }

            // The recording file is finalized (or stop errored): the HUD has done its job.
            // Close it now so the overlay disappears immediately and the app is ready to
            // record again. Transcription runs in the background and reports on the Recent
            // row (issues #7/#8) - the HUD is never coupled to the transcription pass, so a
            // slow or stalled Whisper run can no longer strand the overlay (issue #62).
            CloseHud();

            RecordButton.IsEnabled = true;
            SetRecordingUi(false);
            UpdateSummary();

            if (error != null) { StatusText.Text = "Stop error (logged): " + error; return; }
            StatusText.Text = "Saved: " + (stopped!.Result.File ?? stopped.Result.Dir);

            // Post-processing is already running in the background; wait for it here only to update
            // this window when it finishes. It never faults - PostRecording.Run logs its own
            // failures and reports them through PostRecording.Failed (subscribed in the ctor).
            await stopped.PostProcessing;
            var row = item;
            if (row != null)
            {
                await Ui.Run(() => row.LoadThumb());   // decoding the poster stays off the UI thread
                _library.Refresh(row);
            }
            UpdateLibraryTotal();   // the cost tag was just filled in by packaging
            await RefreshDevThrottleCreditsAfterHostedWorkAsync();
        }

        /// <summary>
        /// Log a failure on a stop path and return the text to show the user (issue #153).
        ///
        /// The catches around <see cref="RecordingStop"/> in this window used to keep only
        /// <c>ex.Message</c> and then display the word "(logged)" - a claim about a log entry that
        /// this path never wrote. The catch that stops the exception is the entry point, so it is
        /// where the entry is written, and a failed stop names the recording directory so the
        /// recording can be found and recovered.
        /// </summary>
        private static string LogStopFailure(string what, Exception ex)
        {
            string dir = (ex as RecordingStopFailedException)?.Dir ?? "(unknown)";
            Log.Error($"[MainWindow] {what} FAILED: dir={dir}", ex);
            return ex.Message;
        }

        /// <summary>
        /// The post-recording sequence for <paramref name="dir"/> threw. Raised on a background
        /// thread by <see cref="PostRecording.Failed"/>, on whichever path stopped the recording -
        /// this window reports it whenever it happens to be open (issue #151).
        /// </summary>
        private void OnPostRecordingFailed(string dir, Exception ex)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = "Transcribe error: " + ex.Message;
                if (IsDevThrottleCreditsFailure(ex)) ShowDevThrottleCreditsWarning();
            });
        }

        // ---- automatic recovery of unfinished recordings (issues #132/#152) ----
        //
        // The transcription backfill USED TO LIVE HERE, as a private pass this window owned and
        // triggered from its own Loaded event. That is why it did not exist in the app's normal
        // shape: AgentEyes runs with --tray and never constructs MainWindow, so a recording left
        // half-processed by a crash, an update restart or one transient failure was stranded
        // forever. The pass now belongs to the app-level RepairService (PostRecording.Resume), which
        // runs it after every launch, on every tick and on sign-in with no window involved; this
        // window only lends it a status line and a library refresh while it happens to be open.

        private void RememberUsed(CapturePreset p)
        {
            _cfg.LastUsedPresetId = p.Id;
            _cfg.Save();
        }

        // ---- recording HUD (issue #20) --------------------------------------

        private HudWindow? _hud;

        private void ShowHud()
        {
            CloseHud();
            _hud = new HudWindow(_svc, _cfg,
                stop: () => Dispatcher.Invoke(StopAsync),
                discard: () => Dispatcher.Invoke(DiscardAsync));
            _hud.Closed += (_, _) => _hud = null;
            _hud.Show();
        }

        private void CloseHud()
        {
            var hud = _hud;
            _hud = null;
            hud?.Close();
        }

        /// <summary>HUD Discard: stop the engine, then delete the recording instead of
        /// keeping it - no library entry, no transcription. Issue #151: the stop-and-delete itself
        /// is the explicitly named <see cref="RecordingStop.Discard"/> operation, so "no
        /// post-processing here" is a decision the log records rather than an omission.</summary>
        private async Task DiscardAsync()
        {
            _timer.Stop();
            RecordButton.IsEnabled = false;
            StatusText.Text = "Discarding...";

            string? error = null;
            try { await Task.Run(() => RecordingStop.Discard(_svc)); }
            catch (Exception ex) { error = LogStopFailure("discarding the recording", ex); }

            RecordButton.IsEnabled = true;
            SetRecordingUi(false);
            UpdateSummary();
            CloseHud();

            StatusText.Text = error != null ? "Discard error (logged): " + error : "Recording discarded.";
        }

        private void SetRecordingUi(bool recording)
        {
            // Preset switching is locked while recording; the split control dims.
            PresetMainButton.IsEnabled = !recording;
            PresetMenuButton.IsEnabled = !recording;
            PresetMainButton.Opacity = recording ? 0.55 : 1.0;
            PresetMenuButton.Opacity = recording ? 0.55 : 1.0;
            ShowAreaButton.IsEnabled = !recording;
            EditPresetButton.IsEnabled = !recording;

            // Before REC the capture summary explains the preset; while recording the live
            // level meters take its place (proof the chosen sources are actually being heard).
            CapturePanel.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
            LevelPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            if (recording)
            {
                string? src = _svc.Status().Source;
                var mic = src is "mic" or "mixed" ? Visibility.Visible : Visibility.Collapsed;
                var sys = src is "system" or "mixed" ? Visibility.Visible : Visibility.Collapsed;
                MicLevelLabel.Visibility = mic; MicLevelBar.Visibility = mic;
                SysLevelLabel.Visibility = sys; SysLevelBar.Visibility = sys;
            }
            else { MicLevelBar.Value = 0; SysLevelBar.Value = 0; ElapsedText.Text = ""; }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!_svc.IsRecording) return;
            ElapsedText.Text = "REC " + Timecodes.Label(_svc.Elapsed);
            MicLevelBar.Value = Math.Min(100, _svc.MicLevel * 180);
            SysLevelBar.Value = Math.Min(100, _svc.SystemLevel * 180);
        }

        // ---- library (issue #19) -------------------------------------------

        private string _searchText = "";
        private bool _libraryGrid = true;

        /// <summary>Items + thumbnails load and decode on a worker thread; the UI thread only merges
        /// the finished snapshot into the library.
        ///
        /// The ORDER comes from <see cref="LibrarySnapshot.NewestFirst"/> - the recording start out
        /// of manifest.json, newest first (issue #178).
        ///
        /// Several of these overlap by design - the repair service asks for a reload after its
        /// resume, title and thumbnail stages, an import asks for one, and the window asks for one at
        /// startup. Their COMPLETIONS are ordered by <see cref="LibraryCoherence"/> (issue #3): the
        /// epoch is claimed BEFORE the worker reads the disk, and the snapshot is then merged one
        /// recording at a time so an older reload can no longer reinstall its stale answer over a
        /// newer one, and a live insert, rename or delete can no longer be undone by a reload that
        /// started before it. Nothing is dropped, so there is nothing to retry.
        ///
        /// A worker that THROWS is reported as a failed read and changes nothing. It used to leave
        /// its list empty and install that - a blank library produced by a broken instrument.</summary>
        private async void LoadRecent()
        {
            long epoch = _library.BeginSnapshot();
            List<RecentItem> items;
            try
            {
                items = await Task.Run(() => LibrarySnapshot.NewestFirst(RecordingPaths.Root));
            }
            catch (Exception ex)
            {
                _library.AbandonSnapshot(epoch, ex);
                StatusText.Text = "Could not read the library (logged): " + ex.Message;
                return;
            }

            // Entry point (CLAUDE.md rule 4): this is an async void method reached from the
            // constructor, from UI events and from a Dispatcher callback, so an exception escaping
            // it goes nowhere but the top of the UI thread and takes the window with it. QA round 1
            // reached exactly that (finding N9). Merging cannot throw on a diverged model any more -
            // it repairs and logs - but the catch is here so that no future throw on this path can
            // ever be fatal.
            try
            {
                _library.ApplySnapshot(epoch, items);
            }
            catch (Exception ex)
            {
                Log.Error($"[MainWindow] LoadRecent FAILED to merge snapshot epoch={epoch}", ex);
                StatusText.Text = "Library refresh error (logged): " + ex.Message;
            }
            UpdateEmptyState();

            // Issue #142: loading the list no longer generates thumbnails. The old backfill here
            // (issue #19) called Thumbnails.Ensure without counting the attempt, so a recording
            // ffmpeg could never read was retried on every list load forever - and, once the repair
            // pass started reloading the list to show repaired cards, it ran a SECOND uncounted
            // ffmpeg for every counted one. RepairService is now the single automatic thumbnail
            // generator, and every attempt it makes is counted and bounded.
        }

        /// <summary>Thumbnail for a recording whose media file is on disk, generated off the UI
        /// thread; the finished image pops onto the card via binding. Must not be called before
        /// the deferred mux has run for a recording (issue #141) - there is no file to read yet.
        /// Isolated on purpose: a failed thumbnail never costs a recording its transcript.</summary>
        private static void EnsureThumbInBackground(RecentItem item) =>
            _ = Task.Run(() =>
            {
                try { Thumbnails.Ensure(item.Dir); item.LoadThumb(); }
                catch (Exception ex) { Log.Error("thumb " + item.Dir, ex); }
            });

        /// <summary>
        /// Re-applies the library's newest-first order after a row's START TIME changed (issue #178,
        /// review finding 3).
        ///
        /// A ListCollectionView sorts when items arrive and when it is refreshed - it is not watching
        /// the field the CustomSort reads, so an item whose sort key changes keeps its old position.
        /// That is a real case, not a hypothetical: a recording first loaded from a manifest with no
        /// usable CreatedUtc is undated and therefore sorted LAST, and the repair pass that fills the
        /// timestamp in later would otherwise leave it pinned at the bottom showing a date that says
        /// it belongs at the top. UI thread only.
        /// </summary>
        private void ResortLibrary()
        {
            Log.Info("[MainWindow] ResortLibrary: a recording's start time changed; re-applying the "
                     + "newest-first order.");
            System.Windows.Data.CollectionViewSource.GetDefaultView(_library.Rows).Refresh();
        }

        private void UpdateEmptyState()
        {
            EmptyState.Visibility = _library.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateLibraryTotal();
        }

        /// <summary>Running AI-cost total across the loaded recordings, shown in the Library header.
        /// Marked "(incl. estimates)" when any contributing figure was estimated rather than measured.</summary>
        private void UpdateLibraryTotal()
        {
            double total = 0;
            bool anyEstimate = false, anyCost = false;
            foreach (var item in _library.Rows)
            {
                if (item.CostUsd <= 0 && item.Cost.Length == 0) continue;
                total += item.CostUsd;
                anyCost = true;
                if (item.Cost.Contains("est")) anyEstimate = true;
            }
            LibraryTotalText.Text = anyCost
                ? $"AI cost: ${total:0.0000}" + (anyEstimate ? " (incl. estimates)" : "")
                : "";
        }

        private void GoRecord_Click(object sender, RoutedEventArgs e) => RailRecord.IsChecked = true;

        private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _searchText = SearchBox.Text.Trim();
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Data.CollectionViewSource.GetDefaultView(_library.Rows).Refresh();
        }

        private void LibraryGrid_Click(object sender, RoutedEventArgs e) { _libraryGrid = true; ApplyLibraryMode(); }
        private void LibraryList_Click(object sender, RoutedEventArgs e) { _libraryGrid = false; ApplyLibraryMode(); }

        /// <summary>
        /// The ONE place the library list is configured for its view mode - template, items panel,
        /// container style, and the virtualization that panel actually supports, all set together.
        ///
        /// Issue #175: virtualization used to be pinned on RecentList in XAML (IsVirtualizing,
        /// VirtualizationMode=Recycling, IsVirtualizingWhenGrouping, ScrollUnit=Pixel) while this
        /// method swapped the panel underneath it. Those attached properties only take effect on a
        /// VirtualizingPanel, and grid mode's panel is a plain WrapPanel - so with day grouping on,
        /// WPF built the group-virtualization/recycling machinery over a panel that cannot support
        /// it and rendered each group's cards under another group's header. Grid mode therefore
        /// declares virtualization OFF; list mode keeps its VirtualizingStackPanel and stays
        /// virtualized. Changing the panel without changing these flags is what broke it, so they
        /// are set here or nowhere.
        ///
        /// Issue #178 then removed the day grouping altogether, so there are no group headers left
        /// to mis-map. IsVirtualizingWhenGrouping is set anyway, with the rest of the stack: the
        /// invariant this method exists for is that the panel and EVERY flag that depends on which
        /// panel it is are decided in one place, and a flag left behind here is exactly how the set
        /// drifted apart the first time.
        /// </summary>
        private void ApplyLibraryMode()
        {
            Log.Info($"[MainWindow] ApplyLibraryMode: grid={_libraryGrid}");

            RecentList.ItemTemplate = (System.Windows.DataTemplate)FindResource(_libraryGrid ? "RecentCardTemplate" : "RecentRowTemplate");
            RecentList.ItemsPanel = (System.Windows.Controls.ItemsPanelTemplate)FindResource(_libraryGrid ? "LibraryWrapPanel" : "LibraryStackPanel");
            RecentList.ItemContainerStyle = (Style)FindResource(_libraryGrid ? "LibraryCardItem" : "LibraryRowItem");

            // Grid = WrapPanel (not a VirtualizingPanel) -> virtualization off, no recycling.
            // List = VirtualizingStackPanel -> virtualized, recycling, pixel scrolling, and
            // virtualizing while grouped is safe because the panel is a VirtualizingPanel.
            bool virtualizing = !_libraryGrid;
            VirtualizingPanel.SetIsVirtualizing(RecentList, virtualizing);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(RecentList, virtualizing);
            VirtualizingPanel.SetVirtualizationMode(RecentList, virtualizing ? VirtualizationMode.Recycling : VirtualizationMode.Standard);
            VirtualizingPanel.SetScrollUnit(RecentList, virtualizing ? ScrollUnit.Pixel : ScrollUnit.Item);

            GridModeButton.Opacity = _libraryGrid ? 1.0 : 0.45;
            ListModeButton.Opacity = _libraryGrid ? 0.45 : 1.0;
        }

        /// <summary>Plain left-click on a card previews it (the 90 percent action).
        /// Modified clicks stay pure selection so multi-select still works.</summary>
        private void Card_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
            if (ItemFrom(sender) is not { } item || item.MediaPath.Length == 0) return;
            OpenPreview(item);
        }

        // ---- AI artifacts (issue #22) ---------------------------------------

        /// <summary>Transcript chip: open the detail view (title/summary/transcript).</summary>
        private void TranscriptChip_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenDetail(item);
        }

        /// <summary>Walkthrough chip: open the built walkthrough itself.</summary>
        private void WalkthroughChip_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item) return;
            string wt = Path.Combine(item.Dir, "walkthrough.html");
            if (File.Exists(wt)) Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true });
            else StatusText.Text = "Walkthrough not built yet - use Build walkthrough.";
        }

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenDetail(item);
        }

        private void OpenDetail(RecentItem item)
        {
            var dlg = new RecordingDetailWindow(item, _cfg,
                rebuildWalkthrough: () => PackageDirAsync(item.Dir),
                delete: () => DeleteRecordings(new List<RecentItem> { item }),
                rename: name => _library.Rename(item, name))
            { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>The card's hover "..." opens the same context menu as right-click.</summary>
        private void CardMore_Click(object sender, RoutedEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            FrameworkElement? owner = fe;
            while (owner != null && owner.ContextMenu == null) owner = owner.Parent as FrameworkElement;
            if (owner?.ContextMenu == null) return;
            owner.ContextMenu.PlacementTarget = fe;
            owner.ContextMenu.IsOpen = true;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string dir) OpenDir(dir);
        }

        private PreviewWindow? _preview;

        /// <summary>Play/view a recording in-app. One preview at a time - opening a new one
        /// closes the old (two videos playing at once is never what anyone wants).</summary>
        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenPreview(item);
        }

        private void OpenPreview(RecentItem item)
        {
            if (!File.Exists(item.MediaPath)) { StatusText.Text = "File not found: " + item.MediaPath; return; }
            try
            {
                _preview?.Close();
                _preview = new PreviewWindow(item.Title, item.MediaPath, item.MediaKind) { Owner = this };
                _preview.Closed += (_, _) => _preview = null;
                _preview.Show();
            }
            catch (Exception ex)
            {
                Log.Error("preview", ex);
                StatusText.Text = "Preview error: " + ex.Message;
            }
        }

        private async void Package_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string dir) await PackageDirAsync(dir);
        }

        private static void OpenDir(string dir)
        {
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }

        private async Task PackageDirAsync(string dir, bool rebuild = false)
        {
            if (!Directory.Exists(dir)) return;

            // Recordings auto-package after Stop, so the walkthrough usually already
            // exists - the button just opens it. "Build walkthrough" forces a rebuild.
            string wt = Path.Combine(dir, "walkthrough.html");
            if (!rebuild && !RecordingWorkset.IsClaimed(dir) && File.Exists(wt))
            {
                Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true });
                return;
            }

            // One build per recording at a time. A second click while Whisper holds
            // audio_16k.wav open would make ffmpeg fail with "Permission denied".
            //
            // Claimed as a STAGE (issue #154): a walkthrough rebuild is one piece of work, not the
            // whole post-recording sequence, so a full pipeline that arrives while this is running
            // must be queued and retried rather than dropped. Unlike that background sequence, THIS
            // caller is a button the user just pressed, so being refused is reported to them and the
            // click ends there - a queued second walkthrough build is not what the click asked for.
            if (!RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "walkthrough build", out var claim))
            {
                StatusText.Text = "Walkthrough is already building for this recording.";
                return;
            }

            var item = _library.Find(dir);
            if (item != null) _library.SetStatus(item, "Transcribing...");
            StatusText.Text = "Building walkthrough (first run downloads the Whisper model)...";
            try
            {
                await DevThrottleClient.EnsureCreditsForHostedWorkAsync();
                await Task.Run(() => Package.Run(dir, 5.0, null));
                await RefreshDevThrottleCreditsAfterHostedWorkAsync();
                // The row was resolved before the awaits above, so it goes back through the model
                // rather than being written on directly (issue #3, failure mode 5).
                if (item != null) _library.Refresh(item);
                UpdateLibraryTotal();   // a rebuild may have (re)recorded the AI cost
                StatusText.Text = "Walkthrough built.";
                if (File.Exists(wt)) Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = "Package error: " + ex.Message;
                if (IsDevThrottleCreditsFailure(ex)) ShowDevThrottleCreditsWarning();
            }
            finally
            {
                if (item != null) _library.SetStatus(item, "");
                RecordingWorkset.Release(claim);
            }
        }

        /// <summary>The predicate itself lives in Core (issue #142) because the automatic repair
        /// passes run with no window at all and must stop on an empty wallet the same way.</summary>
        private static bool IsDevThrottleCreditsFailure(Exception ex) =>
            DevThrottleClient.IsCreditsFailure(ex);

        private static void ShowDevThrottleCreditsWarning()
        {
            NoticeToast.ShowAction(
                "DevThrottle credits are empty. Hosted transcription and AI are paused until you add credits.",
                "Add credits",
                OpenDevThrottleCredits,
                null);
        }

        private static void OpenDevThrottleCredits()
        {
            Process.Start(new ProcessStartInfo(DevThrottleAccount.CreditsUrl) { UseShellExecute = true });
        }

        private async Task RefreshDevThrottleCreditsAfterHostedWorkAsync()
        {
            DevThrottleCredits credits;
            try
            {
                credits = await DevThrottleClient.GetCreditsAsync();
            }
            catch (DevThrottleException ex) when (ex.Status == 401)
            {
                Log.Info("[MainWindow] credit refresh skipped: reconnect needed");
                return;
            }

            foreach (var settings in Application.Current.Windows.OfType<SettingsDialog>())
                settings.RefreshDevThrottleCredits(credits);

            if (credits.BalanceMicros <= 0)
            {
                ShowDevThrottleCreditsWarning();
                return;
            }

            if (credits.BalanceMicros < LowCreditWarningThresholdMicros)
            {
                NoticeToast.ShowActionBottomRight(
                    "DevThrottle credits are below $1. Add credits to avoid hosted transcription and AI pausing.",
                    "Add credits",
                    OpenDevThrottleCredits,
                    null);
            }
        }

        // ---- recent: rename / delete (context menu) ------------------------

        private static RecentItem? ItemFrom(object sender) =>
            (sender as FrameworkElement)?.DataContext as RecentItem;

        private async void RenameRecording_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item || !Directory.Exists(item.Dir)) return;
            string? name = PromptDialog.Ask(this, "Rename recording", "New name:", item.Title);
            if (name == null) return;
            try
            {
                StatusText.Text = "Renaming...";
                // The name lives in the recording's own manifest, so it survives restarts. Off the
                // UI thread: the write serializes on the recording's manifest lock and then flushes
                // to physical disk, so it can wait on a packaging pass that holds the same lock.
                await Task.Run(() => ManifestStore.Update(item.Dir, m => m.DisplayName = name));
                // Through the model: the row was captured before that await, and the new name is
                // newer information than any reload still in flight, which must not revert it
                // (issue #3, failure modes 4 and 5).
                _library.Rename(item, name);
                StatusText.Text = $"Renamed to \"{name}\".";
            }
            catch (Exception ex)
            {
                Log.Error($"[MainWindow] RenameRecording FAILED: dir={item.Dir}", ex);
                StatusText.Text = "Rename error: " + ex.Message;
            }
        }

        /// <summary>Context-menu Delete: the clicked row, or the whole selection when the
        /// clicked row is part of it - Explorer semantics (issue #11).</summary>
        private void DeleteRecording_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item) return;
            var selected = RecentList.SelectedItems.Cast<RecentItem>().ToList();
            DeleteRecordings(selected.Contains(item) && selected.Count > 1
                ? selected
                : new List<RecentItem> { item });
        }

        /// <summary>The Delete key removes the selected recordings (issue #11).</summary>
        private void RecentList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Delete) return;
            var selected = RecentList.SelectedItems.Cast<RecentItem>().ToList();
            if (selected.Count == 0) return;
            e.Handled = true;
            DeleteRecordings(selected);
        }

        /// <summary>One confirmation, then delete every given recording and its files.</summary>
        private void DeleteRecordings(List<RecentItem> items)
        {
            // A recording that is still being captured or processed holds open file handles (the
            // capture writers, Whisper, ffmpeg); deleting it now would half-fail. Let it finish,
            // then delete. Since issue #155 a live capture holds the claim too, so this also covers
            // the recording that is being made right now.
            var busy = items.Find(i => RecordingWorkset.IsClaimed(i.Dir));
            if (busy != null)
            {
                StatusText.Text = $"\"{busy.Title}\" is still being worked on - try again when it is done.";
                return;
            }

            string prompt = items.Count == 1
                ? $"Delete \"{items[0].Title}\" and all its files?"
                : $"Delete {items.Count} recordings and all their files?";
            if (MessageBox.Show(prompt, "AgentEyes", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            _preview?.Close();   // release any playback handle into these folders first

            // Drop the rows immediately so the list feels instant, then delete the (often
            // large, video) folders off the UI thread and report when done. Recursive delete
            // of an mp4 folder can take a noticeable beat - never on the UI thread.
            //
            // The gap between the two is the race in issue #3, failure mode 6: for as long as the
            // recursive delete is running the manifest is STILL on disk, so any reload - however
            // recently it started - honestly reports the recording as present. The model is told the
            // deletion has begun here and is told its OUTCOME below, and it refuses to re-add the
            // rows for the whole span between. Bounding that span on an epoch instead is what let
            // the rows come back in the first round: a reload begun after the delete always carries
            // the higher epoch.
            int count = items.Count;
            string title = items[0].Title;
            var deletion = _library.Delete(items);
            UpdateEmptyState();
            StatusText.Text = count == 1 ? $"Deleting \"{title}\"..." : $"Deleting {count} recordings...";

            int deleted = 0;
            string? firstError = null;
            var failed = new List<string>();

            // Ui.RunThenPost, NOT a bare Ui.Run: the settle below runs from that helper's FINALLY, so
            // it happens even if this loop ever throws. Settling is not optional - a deletion left
            // unsettled hides its recordings from every later reload, even ones that can plainly see
            // the folder is still there - and until this was structural the comment saying so was
            // only true by inspection of a fire-and-forget lambda whose exceptions went nowhere.
            Ui.RunThenPost(
                work: () =>
                {
                    foreach (var dir in deletion.Directories)
                    {
                        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); deleted++; }
                        catch (Exception ex)
                        {
                            Log.Error("delete " + dir, ex);
                            firstError ??= ex.Message;
                            failed.Add(dir);
                        }
                    }
                },
                onDone: () =>
                {
                    _library.CompleteDelete(deletion, failed);
                    StatusText.Text = firstError != null
                        ? $"Deleted {deleted} of {count} - {firstError}"
                        : count == 1 ? $"Deleted \"{title}\"." : $"Deleted {deleted} recordings.";
                });
        }

        private void OpenRecordingMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) OpenDir(item.Dir);
        }

        private async void PackageRecordingMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is { } item) await PackageDirAsync(item.Dir, rebuild: true);
        }

        // ---- import / export-with-subtitles (issue #103) -------------------

        /// <summary>
        /// Import an external video file into the library (issue #103), wired to the #100 VideoImport
        /// engine - no reimplementation. Picking the file (the OpenFileDialog) is the immediate
        /// feedback; the import itself (copy + ffmpeg audio extract + hosted transcription, which can
        /// take minutes) runs OFF the UI thread via Task.Run so the window never blocks (responsive-UI
        /// rule). The new recording lands in the library when it finishes.
        /// </summary>
        private async void ImportVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import a video into the library",
                Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.wmv;*.flv;*.ts;*.m2ts;*.mpg;*.mpeg|All files|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            string path = dlg.FileName;

            // Immediate visual feedback (<100ms), then the heavy work runs off the UI thread.
            ImportVideoButton.IsEnabled = false;
            StatusText.Text = $"Importing \"{Path.GetFileName(path)}\"...";
            try
            {
                await DevThrottleClient.EnsureCreditsForHostedWorkAsync();
                var result = await Task.Run(() => VideoImport.Run(path));
                await RefreshDevThrottleCreditsAfterHostedWorkAsync();
                StatusText.Text = $"Imported \"{Path.GetFileName(path)}\".";
                RailLibrary.IsChecked = true;
                LoadRecent();   // show the newly imported recording
            }
            catch (Exception ex)
            {
                Log.Error("import video " + path, ex);
                StatusText.Text = "Import error: " + ex.Message;
                if (IsDevThrottleCreditsFailure(ex)) ShowDevThrottleCreditsWarning();
            }
            finally { ImportVideoButton.IsEnabled = true; }
        }

        /// <summary>
        /// Export a recording as a NEW subtitled MP4 (issue #103), wired to the #102 SubtitleBurner
        /// engine - no reimplementation. Reads the recording's available languages, asks which to burn
        /// (the prompt is the immediate feedback), then runs the ffmpeg burn OFF the UI thread via
        /// Task.Run so the window never blocks (responsive-UI rule).
        /// </summary>
        private async void ExportSubtitles_Click(object sender, RoutedEventArgs e)
        {
            if (ItemFrom(sender) is not { } item || !Directory.Exists(item.Dir)) return;
            string id = Path.GetFileName(item.Dir);

            // Which languages does this recording carry a subtitle-ready transcript for? Read off the
            // UI thread (small file IO), then decide what to offer.
            IReadOnlyList<string>? langs;
            try { langs = await Task.Run(() => RecordingLibrary.TranscriptLanguages(id)); }
            catch (Exception ex)
            {
                Log.Error("read languages " + item.Dir, ex);
                StatusText.Text = "Could not read transcript languages: " + ex.Message;
                return;
            }

            if (langs == null || langs.Count == 0)
            {
                StatusText.Text = "This recording has no transcript to burn - transcribe or translate it first.";
                return;
            }

            string? lang = PromptDialog.Ask(this, "Export with subtitles",
                "Language to burn (available: " + string.Join(", ", langs) + "):", langs[0]);
            if (string.IsNullOrWhiteSpace(lang)) return;
            lang = lang.Trim();

            // Immediate visual feedback, then the ffmpeg burn runs off the UI thread.
            StatusText.Text = $"Exporting \"{item.Title}\" with {lang} subtitles...";
            try
            {
                var result = await Task.Run(() => SubtitleBurner.Run(item.Dir, lang));
                StatusText.Text = $"Subtitled video ready: {result.OutputFile}";
                OpenDir(item.Dir);   // reveal the new recording.<lang>.subtitled.mp4
            }
            catch (Exception ex)
            {
                Log.Error("export subtitles " + item.Dir, ex);
                StatusText.Text = "Subtitle export error: " + ex.Message;
            }
        }
    }

    /// <summary>Win11 dark title bar so the window chrome matches the dark theme.</summary>
    internal static class DarkTitleBar
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window window)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int on = 1;
                DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            }
            catch { /* cosmetic only */ }
        }
    }

    internal sealed class RecentItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _title = "";
        private string _detail = "";
        private string _status = "";
        private System.Windows.Media.ImageSource? _thumb;

        /// <summary>
        /// Raises PropertyChanged for a bound value that actually changed.
        ///
        /// Every value a Library card BINDS notifies now (issue #3). It did not have to before,
        /// because a reload replaced the whole row object and the new object arrived with the new
        /// values already on it. Rows are updated IN PLACE now - that is what keeps a row held across
        /// an await attached, and what keeps thumbnails and selection alive across a reload - so a
        /// value that changed silently would leave the card rendering the old one.
        /// </summary>
        private bool Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            return true;
        }

        private string _badge = "";
        private System.Windows.Media.Brush _badgeBrush = System.Windows.Media.Brushes.Gray;

        public string Badge
        {
            get => _badge;
            set => Set(ref _badge, value, nameof(Badge));
        }

        public System.Windows.Media.Brush BadgeBrush
        {
            get => _badgeBrush;
            set => Set(ref _badgeBrush, value, nameof(BadgeBrush));
        }

        // ---- library cards (issue #19) ----

        /// <summary>
        /// When the recording STARTED, as a UTC INSTANT - read only from manifest.json's CreatedUtc,
        /// and the single date this library knows about (issue #178). This is the library's SORT KEY.
        ///
        /// It is UTC and not local on purpose (issue #178, review finding 1). Local wall-clock time
        /// is not monotonic: when the clocks go back in the autumn, 06:15 UTC reads as 1:15 AM and
        /// 05:30 UTC reads as 1:30 AM, so ordering by the local reading puts the LATER recording
        /// first. An instant has no such ambiguity. Local time is derived from this for DISPLAY only.
        ///
        /// <c>null</c> means the manifest carried no usable CreatedUtc. That recording is shown as
        /// undated and sorted last; it is never given the folder's filesystem date and never given
        /// today's, because a recording silently dated "now" is indistinguishable from one that
        /// really was made now - which is precisely the confusion this issue removed.
        ///
        /// The setter is private: the sort key changes only through <see cref="From"/> (building the
        /// card) and <see cref="RefreshNaming"/> (re-reading the manifest), and RefreshNaming REPORTS
        /// the change so the view can be re-sorted. Nothing else may move a card silently.
        /// </summary>
        public DateTime? StartedUtc { get; private set; }

        /// <summary>The recording start in LOCAL time - derived from <see cref="StartedUtc"/> for
        /// display, and never used to order the library.</summary>
        public DateTime? StartedLocal => StartedUtc?.ToLocalTime();

        /// <summary>The card's date label: the recording start in local time, stated absolutely so
        /// it never depends on what day it is read. "Undated" when there is no usable start time.
        /// This is the ONLY date or time the library shows for a recording.</summary>
        public string DateText => DateLabel(StartedLocal);

        /// <summary>Newest first, by recording start; undated last. Used BOTH by the collection view
        /// (CustomSort) and by the loader, so the library has exactly one ordering rule.</summary>
        public static readonly NewestFirstComparer NewestFirst = new();

        /// <summary>Card image (poster frame / waveform / the screenshot). Loaded and
        /// frozen off the UI thread; null until then (placeholder shows).</summary>
        public System.Windows.Media.ImageSource? Thumb
        {
            get => _thumb;
            set
            {
                _thumb = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PlaceholderVisibility)));
            }
        }

        public Visibility PlaceholderVisibility => _thumb == null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Spinner overlay on the card while Status is non-empty.</summary>
        public Visibility StatusVisibility => _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Artifact chips (transcript / walkthrough exist on disk).
        private Visibility _transcriptChip = Visibility.Collapsed;
        private Visibility _flatTextChip = Visibility.Collapsed;
        private Visibility _walkthroughChip = Visibility.Collapsed;
        /// <summary>The "Transcript" chip: TRANSCRIPTION COMPLETE per the canonical predicate
        /// (<see cref="TranscriptStatus"/>, issue #4) - never mere transcript.txt existence.</summary>
        public Visibility TranscriptChipVisibility
        {
            get => _transcriptChip;
            set { _transcriptChip = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TranscriptChipVisibility))); }
        }
        /// <summary>The quieter "Text file" chip (issue #4): a legacy flat transcript.txt exists but
        /// the recording is NOT transcribed. Mutually exclusive with the Transcript chip; the text
        /// stays readable through the same detail view.</summary>
        public Visibility FlatTextChipVisibility
        {
            get => _flatTextChip;
            set { _flatTextChip = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FlatTextChipVisibility))); }
        }
        public Visibility WalkthroughChipVisibility
        {
            get => _walkthroughChip;
            set { _walkthroughChip = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(WalkthroughChipVisibility))); }
        }

        /// <summary>Resolve + decode the thumbnail at card size, frozen so it can cross
        /// from a worker thread. Safe to call repeatedly.</summary>
        public void LoadThumb()
        {
            try
            {
                string? path = Thumbnails.PathFor(Dir);
                if (path == null && MediaKind == "image") path = MediaPath;   // screenshots
                if (path == null || !File.Exists(path)) return;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 480;
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Thumb = bmp;
            }
            catch (Exception ex) { Log.Error("thumb load " + Dir, ex); }
        }

        /// <summary>Notifies so an in-place Rename updates the row immediately.</summary>
        public string Title
        {
            get => _title;
            set { _title = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title))); }
        }

        /// <summary>Notifies so the generated description lands on the row when packaging finishes.</summary>
        public string Detail
        {
            get => _detail;
            set { _detail = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Detail))); }
        }

        /// <summary>Live progress on the row ("Transcribing..."); empty when idle (issue #7).</summary>
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusVisibility)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private string _duration = "-";
        private Visibility _walkthroughVisibility = Visibility.Visible;

        public string Duration
        {
            get => _duration;
            set => Set(ref _duration, value, nameof(Duration));
        }

        public Visibility WalkthroughVisibility
        {
            get => _walkthroughVisibility;
            set => Set(ref _walkthroughVisibility, value, nameof(WalkthroughVisibility));
        }

        /// <summary>The recording folder - the row's IDENTITY in the library. It is set when the card
        /// is built and never changes: the coherence model matches a snapshot's rows to the visible
        /// rows by this value (issue #3).</summary>
        public string Dir { get; set; } = "";

        /// <summary>
        /// The recording's identity for UI Automation: the recording folder's NAME only
        /// (<c>2026-08-17_080332_video</c>), never the absolute path (issue #178).
        ///
        /// Every Library row publishes this as its AutomationId so an automated check can tell one
        /// rendered row from another - the rendered title plus a minute-rounded date is not unique,
        /// but the folder name is unique within the library by construction.
        ///
        /// It is the LEAF and not <see cref="Dir"/> on purpose. The UI Automation tree is readable
        /// by any process on the desktop, so publishing the full path would put the user's home
        /// directory on that public surface - a gratuitous leak from a privacy-sensitive recorder,
        /// buying nothing, because every recording lives under the same root and the leaf already
        /// identifies it.
        /// </summary>
        public string DirName => Path.GetFileName(Dir);

        // ---- AI cost (per recording) ----
        private string _cost = "";
        /// <summary>Spend tag for this recording's AI calls ("$0.0018", "~$0.0018 est"); empty when
        /// none was recorded. Notifies so it appears on the row when packaging finishes.</summary>
        public string Cost
        {
            get => _cost;
            set
            {
                _cost = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Cost)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CostVisibility)));
            }
        }
        public Visibility CostVisibility => _cost.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>Raw USD, for the library running total. 0 when unknown.</summary>
        public double CostUsd { get; set; }

        private string _costTip = "";
        /// <summary>Tooltip explaining how the figure was derived (API usage vs estimate).</summary>
        public string CostTip
        {
            get => _costTip;
            set => Set(ref _costTip, value, nameof(CostTip));
        }

        /// <summary>Cost label for a recording's AI spend. DevThrottle bills server-side, so a
        /// recording carries token usage rather than a client-known dollar figure - show tokens
        /// then. Legacy recordings with a stored USD amount still render as dollars.</summary>
        internal static string FormatCost(Ai.AiCostInfo c)
        {
            if (c.CostUsd > 0)
            {
                string n = "$" + c.CostUsd.ToString("0.0000");
                return c.IsEstimate ? "~" + n + " est" : n;
            }
            int tokens = c.PromptTokens + c.CompletionTokens;
            return tokens > 0 ? tokens + " tokens" : "";
        }

        // In-app preview: the playable/viewable file and the row's vector icon for it.
        private string _mediaPath = "";
        private string _mediaKind = "";
        private System.Windows.Media.Geometry? _iconGeometry;
        private Visibility _previewVisibility = Visibility.Collapsed;
        private string _previewTip = "Preview";

        public string MediaPath
        {
            get => _mediaPath;
            set => Set(ref _mediaPath, value, nameof(MediaPath));
        }

        public string MediaKind                              // video | audio | image
        {
            get => _mediaKind;
            set => Set(ref _mediaKind, value, nameof(MediaKind));
        }

        public System.Windows.Media.Geometry? IconGeometry
        {
            get => _iconGeometry;
            set => Set(ref _iconGeometry, value, nameof(IconGeometry));
        }

        public Visibility PreviewVisibility
        {
            get => _previewVisibility;
            set => Set(ref _previewVisibility, value, nameof(PreviewVisibility));
        }

        public string PreviewTip
        {
            get => _previewTip;
            set => Set(ref _previewTip, value, nameof(PreviewTip));
        }

        private static System.Windows.Media.Geometry Frozen(string data)
        {
            var g = System.Windows.Media.Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        private static readonly System.Windows.Media.Geometry PlayIcon =
            Frozen("M 4 2 L 20 12 L 4 22 Z");
        private static readonly System.Windows.Media.Geometry SpeakerIcon =
            Frozen("M 3 9 L 7 9 L 12 4 L 12 20 L 7 15 L 3 15 Z " +
                   "M 15.5 7 C 18.5 9 18.5 15 15.5 17 L 14.4 15.8 C 16.6 14.2 16.6 9.8 14.4 8.2 Z");
        private static readonly System.Windows.Media.Geometry EyeIcon =
            Frozen("M 12 5 C 6 5 2 12 2 12 C 2 12 6 19 12 19 C 18 19 22 12 22 12 C 22 12 18 5 12 5 Z " +
                   "M 12 15.5 C 10.1 15.5 8.5 13.9 8.5 12 C 8.5 10.1 10.1 8.5 12 8.5 " +
                   "C 13.9 8.5 15.5 10.1 15.5 12 C 15.5 13.9 13.9 15.5 12 15.5 Z");

        // See CapturePreset.ToString - list items expose this as their UI Automation name.
        public override string ToString() => Title;

        private static System.Windows.Media.Brush Rgb(byte r, byte g, byte b)
        {
            // Frozen: items are now built on a worker thread (issue #19 async load).
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The recording's start time as a UTC INSTANT, taken ONLY from manifest.json's CreatedUtc
        /// (issue #178). Returns null - and says so in the log - when the manifest carries no usable
        /// timestamp; the caller shows that recording as undated and sorts it last.
        ///
        /// There is deliberately no second source. The old code fell back to the folder's filesystem
        /// creation time and then to DateTime.Now, which is how recordings from July were filed under
        /// "Today": three different notions of "when" on one screen, only one of them the truth.
        ///
        /// The result is normalized to UTC rather than to local time (review finding 1) because the
        /// library ORDERS by it, and local wall-clock time runs backwards for an hour every autumn.
        /// RoundtripKind gives back Utc for a "Z" instant, Local for an explicit offset, and
        /// Unspecified for a bare date-time - the field's contract is UTC, so an unspecified value is
        /// read as UTC and an offset is converted.
        /// </summary>
        internal static DateTime? StartUtc(string dir, string? createdUtc)
        {
            if (!string.IsNullOrWhiteSpace(createdUtc)
                && DateTime.TryParse(createdUtc, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var started))
                return started.Kind == DateTimeKind.Local
                    ? started.ToUniversalTime()
                    : DateTime.SpecifyKind(started, DateTimeKind.Utc);

            Log.Warn($"[RecentItem] StartUtc: manifest for {dir} has no usable CreatedUtc "
                     + $"(value=\"{createdUtc}\") - the recording is shown undated and sorted last.");
            return null;
        }

        /// <summary>The library's date label for a recording start: absolute, local, with the year,
        /// so it reads the same on any day. "Undated" when there is no start time.</summary>
        internal static string DateLabel(DateTime? startedLocal) =>
            startedLocal.HasValue ? $"{startedLocal.Value:MMM d, yyyy}  {startedLocal.Value:h:mm tt}" : "Undated";

        public static RecentItem From(string dir)
        {
            var item = new RecentItem { Title = Path.GetFileName(dir), Detail = "", Dir = dir };
            Manifest? manifest = null;   // survives the catch - the artifact chips below still classify
            try
            {
                var m = Manifest.Load(dir);
                manifest = m;
                string mic = ShortMic(m.Microphone);

                switch (m.Mode)
                {
                    case "video":
                        item.Badge = "VID";
                        item.BadgeBrush = Rgb(0x00, 0x7A, 0xCC);
                        item.Title = $"Monitor {m.MonitorIndex}" + (m.Region != null ? " (region)" : "");
                        break;
                    case "audio":
                        item.Badge = "AUD";
                        item.BadgeBrush = Rgb(0x22, 0xC5, 0x5E);
                        item.Title = mic.Length > 0 ? mic : "Audio";
                        mic = "";   // already the title; don't repeat it below
                        break;
                    case "shot":
                        item.Badge = "SHOT";
                        item.BadgeBrush = Rgb(0xD6, 0x9E, 0x2E);
                        item.Title = $"Screenshot - Monitor {m.MonitorIndex}";
                        item.WalkthroughVisibility = Visibility.Hidden;   // Hidden (not Collapsed) keeps Open aligned
                        break;
                    default:
                        item.Badge = m.Mode.ToUpperInvariant();
                        break;
                }

                // Name priority: user Rename > title generated from the transcript > derived.
                if (!string.IsNullOrWhiteSpace(m.Title)) item.Title = m.Title;
                if (!string.IsNullOrWhiteSpace(m.DisplayName)) item.Title = m.DisplayName;

                // Resolve the previewable file for this row (button hides if it is missing).
                switch (m.Mode)
                {
                    case "video":
                        item.SetMedia(Path.Combine(dir, m.VideoFile ?? "recording.mp4"),
                            "video", PlayIcon, "Play video");
                        break;
                    case "audio":
                        item.SetMedia(Path.Combine(dir, m.AudioFile ?? "audio.wav"),
                            "audio", SpeakerIcon, "Play audio");
                        break;
                    case "shot":
                        string shotsDir = Path.Combine(dir, "shots");
                        string? png = Directory.Exists(shotsDir)
                            ? Directory.GetFiles(shotsDir, "*.png").OrderBy(f => f).FirstOrDefault()
                            : null;
                        if (png != null) item.SetMedia(png, "image", EyeIcon, "View screenshot");
                        break;
                }

                // Issue #178: the recording start out of the manifest, and nothing else. It sorts the
                // card and it labels it - one value, read once, used for both.
                item.StartedUtc = StartUtc(dir, m.CreatedUtc);
                string when = item.DateText;

                // The generated description (issue #8) says more than the mic name does.
                item.Detail = !string.IsNullOrWhiteSpace(m.Description) ? $"{when}   -   {m.Description}"
                    : mic.Length > 0 ? $"{when}   -   {mic}" : when;
                item.Duration = m.Mode == "shot" || m.DurationSeconds <= 0
                    ? "-"
                    : $"{(int)m.DurationSeconds / 60}:{(int)m.DurationSeconds % 60:D2}";

                // AI spend for this recording (issue: cost tracking). Measured from API token
                // usage when present, otherwise estimated from transcript length (flagged "est").
                if (m.AiCost != null)
                {
                    item.CostUsd = m.AiCost.CostUsd;
                    item.Cost = FormatCost(m.AiCost);
                    item.CostTip = m.AiCost.Basis ?? "";
                }
                // Legacy recordings titled before token capture carry no usable cost figure
                // (the old client-side dollar estimate was removed) - leave it blank.

            }
            catch (Exception ex)
            {
                // Entry point for one card: a manifest this window cannot read must not take the
                // whole library down with it. Issue #178: it is LOGGED now rather than swallowed -
                // the card that comes out of here is undated, and an undated card has to be
                // explainable from the log.
                Log.Error($"[RecentItem] From: cannot build the library card for {dir}", ex);
            }

            // Artifact chips: what the recording already produced on disk. Deliberately OUTSIDE the
            // manifest try (issue #4 review): the chips are file facts, and an unreadable manifest
            // must not hide artifacts that are plainly there - RefreshArtifactChips takes a null
            // manifest and falls back to the default artifact names, the same thing the detail
            // window does. Transcript presence comes from the canonical predicate the Control API
            // uses (issue #4) - a legacy flat transcript.txt is NOT "transcribed", it gets its own
            // quieter chip. The SAME method re-derives the chips whenever the Library is shown
            // again (issue #4 round 2), so a card can never keep claiming a transcript that was
            // deleted outside the app.
            item.RefreshArtifactChips(manifest);

            if (item.Detail.Length == 0) item.Detail = item.DateText;
            return item;
        }

        /// <summary>The manifest this card was built from, null when it could not be read. Kept so
        /// <see cref="RefreshArtifactChips()"/> can re-consult the canonical transcript predicate
        /// without re-reading the manifest on the UI thread: the only thing the predicate takes
        /// from it is the manifest-NAMED transcript artifact file name, and that name changes only
        /// through routes that rebuild the card anyway (<see cref="AdoptFrom"/> carries it over).
        /// </summary>
        private Manifest? _manifest;

        /// <summary>
        /// (Re-)derives the artifact chips from the disk, through the canonical transcript
        /// predicate (<see cref="TranscriptStatus"/>, issue #4), and remembers
        /// <paramref name="manifest"/> for the parameterless refresh below.
        /// </summary>
        public void RefreshArtifactChips(Manifest? manifest)
        {
            _manifest = manifest;
            RefreshArtifactChips();
        }

        /// <summary>
        /// Re-derives the artifact chips from the disk using the manifest the card already holds -
        /// the answer to issue #4 round 2 (review gate defect 1): transcript.json can be deleted
        /// or created OUTSIDE the app (the card's own Open-folder action invites exactly that),
        /// and a chip cached at build time left the visible card contradicting the Control API,
        /// which re-reads the disk on every request. The library re-runs this on every card each
        /// time the Library becomes visible (<see cref="LibraryCoherence.RefreshArtifactChips"/>),
        /// so the card and the canonical predicate agree whenever the card is shown.
        ///
        /// Cheap by design - two or three File.Exists per card, no manifest re-read, safe on the
        /// UI thread - and silent per card like the predicate itself (the bulk route logs once).
        /// </summary>
        public void RefreshArtifactChips()
        {
            var kind = TranscriptStatus.Classify(Dir, _manifest);
            TranscriptChipVisibility = kind == TranscriptKind.Transcribed
                ? Visibility.Visible : Visibility.Collapsed;
            FlatTextChipVisibility = kind == TranscriptKind.FlatTextOnly
                ? Visibility.Visible : Visibility.Collapsed;
            WalkthroughChipVisibility = File.Exists(Path.Combine(Dir, "walkthrough.html"))
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Re-reads this recording's manifest into this card, in place - packaging just
        /// filled in the generated Title/Description (issue #8), the artifact chips and the cost.
        /// The same in-place update a reload does, from a fresh read of the card's own recording.
        /// </summary>
        /// <returns>true when the recording's START TIME changed - see <see cref="AdoptFrom"/>.
        /// Callers reach this through <c>LibraryCoherence.Refresh</c>, which is where that answer is
        /// acted on.</returns>
        public bool RefreshNaming() => AdoptFrom(From(Dir));

        /// <summary>
        /// Takes on everything a freshly-built card for the SAME recording knows, in place.
        ///
        /// This is how the library updates a row instead of replacing it (issue #3). Replacing the
        /// object is what detached a row a caller was holding across an await, threw away the
        /// thumbnail that had already been decoded for it, and reset the user's selection on every
        /// reload. Every value that a card BINDS is adopted here, so nothing is lost by keeping the
        /// object - the two exceptions are deliberate:
        ///
        /// * <see cref="Status"/> is live progress the running app is writing on this row
        ///   ("Transcribing..."), not something the manifest knows. A snapshot must not wipe it.
        /// * <see cref="Thumb"/> is only taken when the fresh card HAS one. A snapshot built before
        ///   the poster frame existed would otherwise blank a thumbnail this row already decoded.
        /// </summary>
        /// <returns>
        /// true when the recording's START TIME changed, i.e. the library's SORT KEY moved and the
        /// view has to be re-sorted (issue #178, review finding 3). The card cannot do that itself -
        /// it does not know which view it is in - and a collection view does NOT re-sort merely
        /// because a field it sorts on changed. Saying so is how the caller knows to re-sort, and
        /// how an item that arrived undated stops being pinned to the bottom once its manifest can
        /// be read.
        /// </returns>
        public bool AdoptFrom(RecentItem fresh)
        {
            if (fresh is null) throw new ArgumentNullException(nameof(fresh));
            if (!string.Equals(Dir, fresh.Dir, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"A library row for {Dir} cannot adopt the card for {fresh.Dir}. The recording "
                    + "directory is the row's identity.", nameof(fresh));

            Title = fresh.Title;
            Detail = fresh.Detail;
            // The start time comes back with the fresh read so the sort key and the date inside
            // Detail can never disagree - a repair that fills in a missing CreatedUtc must not leave
            // a card showing a date while still sorted as undated (issue #178).
            bool startChanged = StartedUtc != fresh.StartedUtc;
            StartedUtc = fresh.StartedUtc;
            CostUsd = fresh.CostUsd;
            CostTip = fresh.CostTip;
            Cost = fresh.Cost;   // notifies; the cost tag lands on the row when packaging finishes
            TranscriptChipVisibility = fresh.TranscriptChipVisibility;
            FlatTextChipVisibility = fresh.FlatTextChipVisibility;
            WalkthroughChipVisibility = fresh.WalkthroughChipVisibility;
            // The manifest travels with the chips it names (issue #4 round 2): a later
            // RefreshArtifactChips on this row must classify with the manifest of the FRESH read,
            // not one from before the reload.
            _manifest = fresh._manifest;
            Badge = fresh.Badge;
            BadgeBrush = fresh.BadgeBrush;
            Duration = fresh.Duration;
            WalkthroughVisibility = fresh.WalkthroughVisibility;
            MediaPath = fresh.MediaPath;
            MediaKind = fresh.MediaKind;
            IconGeometry = fresh.IconGeometry;
            PreviewTip = fresh.PreviewTip;
            PreviewVisibility = fresh.PreviewVisibility;
            if (fresh.Thumb != null) Thumb = fresh.Thumb;

            if (startChanged)
                Log.Info($"[RecentItem] AdoptFrom: the start time of {Dir} changed to "
                         + $"{(StartedUtc.HasValue ? StartedUtc.Value.ToString("O") : "unknown")} - "
                         + "the library has to be re-sorted.");
            return startChanged;
        }

        private void SetMedia(string path, string kind, System.Windows.Media.Geometry icon, string tip)
        {
            if (!File.Exists(path)) return;
            MediaPath = path;
            MediaKind = kind;
            IconGeometry = icon;
            PreviewTip = tip;
            PreviewVisibility = Visibility.Visible;
        }

        /// <summary>"Microphone (FDUCE SL40 Audio Device)" -> "FDUCE SL40 Audio Device".</summary>
        private static string ShortMic(string? mic)
        {
            if (string.IsNullOrWhiteSpace(mic)) return "";
            int open = mic.IndexOf('(');
            int close = mic.LastIndexOf(')');
            return open >= 0 && close > open ? mic[(open + 1)..close] : mic;
        }
    }

    /// <summary>
    /// The library's one ordering rule (issue #178): newest first by RECORDING START, with any
    /// recording that has no usable start time sorted LAST.
    ///
    /// Undated last is the deliberate half. Sorting an unknown date as "very old" is the only
    /// reading that cannot mislead - the alternative, treating it as new, puts a recording at the
    /// top of the library on the strength of a date nobody knows.
    ///
    /// Ties break on the directory name so that two recordings sharing a start time still come out
    /// in a fixed order. That is a TIEBREAK and nothing more: the directory name is not a date and
    /// is never the sort key (it is the string ordering that used to pass for one).
    ///
    /// It compares the UTC INSTANT, never the local wall clock (review finding 1). Wall-clock time
    /// repeats an hour when the clocks go back: a recording made at 06:15 UTC reads 1:15 AM and one
    /// made 45 minutes EARLIER at 05:30 UTC reads 1:30 AM, so comparing the readings puts the older
    /// recording first. Instants are ordered; readings of them are not.
    /// </summary>
    internal sealed class NewestFirstComparer : IComparer<RecentItem>, System.Collections.IComparer
    {
        public int Compare(RecentItem? x, RecentItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            if (x.StartedUtc is null && y.StartedUtc is null) return TieBreak(x, y);
            if (x.StartedUtc is null) return 1;    // undated sorts last
            if (y.StartedUtc is null) return -1;

            int byStart = y.StartedUtc.Value.CompareTo(x.StartedUtc.Value);   // descending = newest first
            return byStart != 0 ? byStart : TieBreak(x, y);
        }

        private static int TieBreak(RecentItem x, RecentItem y) =>
            string.Compare(y.Dir, x.Dir, StringComparison.OrdinalIgnoreCase);

        int System.Collections.IComparer.Compare(object? x, object? y) =>
            Compare(x as RecentItem, y as RecentItem);
    }
}
