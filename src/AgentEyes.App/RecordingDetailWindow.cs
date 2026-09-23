using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Recording detail (issue #22): the place where the AI output is visible -
    /// editable AI title (rename), summary paragraph, scrollable transcript, and
    /// the recording's actions. When AI is not configured it says so plainly with
    /// a path to Settings (no silent degradation).
    /// </summary>
    internal sealed class RecordingDetailWindow : Window
    {
        private readonly RecentItem _item;
        private readonly Config _cfg;
        private readonly Func<Task> _rebuildWalkthrough;
        private readonly Action _delete;

        /// <summary>
        /// Applies a rename to the library, ordered against the reloads in flight (issue #3).
        ///
        /// It is a callback for the same reason <see cref="_delete"/> is: this dialog is handed
        /// behaviour it does not own. It used to write RecentItem.Title straight onto the live
        /// library row, which claimed no epoch, so a reload whose worker had already read the old
        /// manifest landed afterwards and put the old name back - failure mode 4, on the one rename
        /// route that never went through the model.
        /// </summary>
        private readonly Action<string> _rename;
        private readonly TextBox _title;
        private readonly TextBlock _status;
        private PreviewWindow? _player;

        // The async-filled area (issue #4 rounds 2 and 3). The window opens IMMEDIATELY with a
        // loading line in _transcriptBody; LoadDetailsAsync reads the manifest, the account state
        // and the transcript on a background thread after Loaded and then only fills values into
        // these controls - the visual tree is never restructured after construction. The summary
        // and the sign-in banner are part of the layout from the start, collapsed until the load
        // says which of them applies, because deciding that needs the manifest (a disk read) and
        // the credential file (a read + decrypt) - neither is constructor work (round 3).
        private readonly TextBox _transcriptBody;
        private readonly TextBlock _legacyNotice;
        private readonly Button _copyButton;
        private readonly TextBlock _summary;
        private readonly Border _aiBanner;

        /// <summary>The transcript text the async load produced - what Copy copies.</summary>
        private string _transcriptText = "";

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal RecordingDetailWindow(RecentItem item, Config cfg, Func<Task> rebuildWalkthrough,
            Action delete, Action<string> rename)
        {
            _item = item;
            _cfg = cfg;
            _rebuildWalkthrough = rebuildWalkthrough;
            _delete = delete;
            _rename = rename ?? throw new ArgumentNullException(nameof(rename));

            Title = "Recording details";
            Width = 640; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // meta
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // AI banner
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // summary
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // transcript
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // actions

            // Editable AI title = rename (saved on focus loss or Enter).
            _title = new TextBox
            {
                Text = item.Title,
                FontSize = 17, FontWeight = FontWeights.SemiBold,
                Style = Res<Style>("DkTextBox"),
                ToolTip = "Recording name - edit to rename",
            };
            _title.LostFocus += (_, _) => CommitRename();
            _title.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitRename(); };
            root.Children.Add(_title);

            var meta = new TextBlock
            {
                Text = item.Detail + (item.Duration != "-" ? $"   -   {item.Duration}" : ""),
                FontSize = 12,
                Foreground = Res<Brush>("DkDim"),
                Margin = new Thickness(2, 8, 0, 0),
            };
            Grid.SetRow(meta, 1);
            root.Children.Add(meta);

            // ---- AI state: configured -> summary; not configured -> say so plainly ----
            // Which of the two applies needs the manifest (a disk read) and the credential file
            // (a read + decrypt) - so the DECISION belongs to LoadDetailsAsync on its worker
            // (round 3, review gate defect 2: nothing pre-show may wait on the disk). Both
            // controls are built collapsed here; the load shows at most one of them.
            _aiBanner = new Border
            {
                Background = Res<Brush>("DkCard"),
                BorderBrush = Res<Brush>("DkAccent"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 14, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            var bannerRow = new DockPanel();
            var setup = new Button
            {
                Content = "Sign in",
                Style = Res<Style>("DkPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            setup.Click += (_, _) => new SettingsDialog(_cfg) { Owner = this }.ShowDialog();
            DockPanel.SetDock(setup, Dock.Right);
            bannerRow.Children.Add(setup);
            bannerRow.Children.Add(new TextBlock
            {
                Text = "AI titles and summaries are off. Sign in to DevThrottle under Settings > Account "
                    + "and new recordings get named and summarized automatically.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Res<Brush>("DkText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });
            _aiBanner.Child = bannerRow;
            Grid.SetRow(_aiBanner, 2);
            root.Children.Add(_aiBanner);

            _summary = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20,
                Foreground = Res<Brush>("DkText"),
                Margin = new Thickness(2, 14, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Grid.SetRow(_summary, 3);
            root.Children.Add(_summary);

            // ---- transcript ----
            var transcriptHost = new Border
            {
                Background = Res<Brush>("DkCard"),
                BorderBrush = Res<Brush>("DkBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 14, 0, 0),
            };
            // Issue #4: every transcript decision (is it transcribed? what text shows? is Copy
            // offered?) comes from the testable presentation, driven by the canonical predicate -
            // never from flat-text existence or length.
            //
            // Issue #4 round 2 (review gate defect 2): the presentation is NOT built here.
            // Building it reads and deserializes the whole transcript, and doing that on the
            // constructor thread froze the window for the duration of a large JSON-only read -
            // the exact shape CLAUDE.md section 1 forbids. The window opens immediately showing a
            // loading line; LoadDetailsAsync (wired to Loaded below) does the read on a
            // background thread and fills these controls in on the UI thread.
            _transcriptBody = new TextBox
            {
                Text = "Loading transcript...",
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = Res<Brush>("DkDim"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            // A legacy flat-text-only recording: the text stays readable, but a quiet caption says
            // plainly that it is NOT a transcription (issue #4). The caption is part of the layout
            // from the start, collapsed until the load says it applies.
            _legacyNotice = new TextBlock
            {
                Text = "",
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = Res<Brush>("DkDim"),
                Margin = new Thickness(12, 10, 12, 0),
                Visibility = Visibility.Collapsed,
            };
            var transcriptPanel = new DockPanel();
            DockPanel.SetDock(_legacyNotice, Dock.Top);
            transcriptPanel.Children.Add(_legacyNotice);
            transcriptPanel.Children.Add(_transcriptBody);
            transcriptHost.Child = transcriptPanel;
            Grid.SetRow(transcriptHost, 4);
            root.Children.Add(transcriptHost);

            // ---- actions ----
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            if (item.MediaPath.Length > 0) actions.Children.Add(Btn("Play", (_, _) => Play()));
            // The button label used to probe walkthrough.html with File.Exists right here - a
            // synchronous disk read pre-show (round 3). The card's walkthrough CHIP already
            // answers the same question from the same probe, re-derived every time the Library
            // is shown, and this window opens from that card - so the label reads the chip.
            if (item.WalkthroughVisibility == Visibility.Visible)
                actions.Children.Add(Btn(item.WalkthroughChipVisibility == Visibility.Visible
                        ? "Open walkthrough" : "Build walkthrough",
                    async (_, _) => { await _rebuildWalkthrough(); }));
            // Copy follows CanCopy, not the transcribed claim - a legacy flat text is still the
            // user's content and must stay copyable (issue #4). The button exists from the start,
            // hidden at its place in the row, and appears when the async load finds text (round
            // 2), so showing it never reorders the actions.
            _copyButton = Btn("Copy transcript", (_, _) => CopyTranscript(_transcriptText));
            _copyButton.Visibility = Visibility.Collapsed;
            actions.Children.Add(_copyButton);
            actions.Children.Add(Btn("Open folder", (_, _) => OpenFolder()));
            actions.Children.Add(Btn("Delete", (_, _) => { _delete(); Close(); }));

            _status = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = Res<Brush>("DkDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            actions.Children.Add(_status);
            Grid.SetRow(actions, 5);
            root.Children.Add(actions);

            Content = root;
            Closed += (_, _) => _player?.Close();

            // Everything that touches the disk loads AFTER the window is visible (CLAUDE.md
            // section 1): show first, read on a worker, update on the UI thread.
            Loaded += async (_, _) => await LoadDetailsAsync();
        }

        // The former _hasTranscript flag (flat-text LENGTH) is gone (issue #4): the transcript
        // decisions live in the testable TranscriptPresentation, built by LoadDetailsAsync on
        // a background thread once the window is up (issue #4 round 2).

        /// <summary>
        /// Reads everything the constructor must not - the manifest (summary + transcript
        /// classification), the account state (banner) and the transcript itself - in ONE
        /// background pass, then fills the already-visible window in (issue #4 rounds 2 and 3).
        /// The manifest read predates this line of work but sat synchronously pre-show, so the
        /// immediate-show claim was not real on slow storage (round 3, review gate defect 2).
        /// Every log on this path is written INSIDE the worker body, before the UI update is
        /// dispatched, so the paint-critical dispatcher hop appends nothing to disk (round 3,
        /// defect 3) - full enterprise-logging coverage, off the UI thread. Entry point (wired to
        /// Loaded), so the try-catch lives here (CLAUDE.md rule 4): a failed load degrades to the
        /// empty state, loudly logged from a worker - never a frozen or dead window.
        /// </summary>
        private async Task LoadDetailsAsync()
        {
            try
            {
                var loaded = await Task.Run(() =>
                {
                    Log.Info($"[RecordingDetailWindow] LoadDetailsAsync: dir={_item.Dir}");

                    // A manifest failure is survivable (no summary; classification falls back to
                    // the default artifact names) but never silent.
                    Manifest? manifest = null;
                    try { manifest = Manifest.Load(_item.Dir); }
                    catch (Exception ex)
                    {
                        Log.Error($"[RecordingDetailWindow] LoadDetailsAsync: cannot read the manifest "
                            + $"for {_item.Dir} - no summary, transcript classified by default artifact names.", ex);
                    }

                    var presentation = TranscriptPresentation.For(_item.Dir, manifest);
                    // IsSignedIn reads and decrypts the stored credential - worker territory too.
                    bool aiConfigured = AgentEyes.DevThrottle.DevThrottleAccount.IsSignedIn;
                    Log.Info($"[RecordingDetailWindow] LoadDetailsAsync: dir={_item.Dir} "
                        + $"kind={presentation.Kind} chars={presentation.Text.Length} aiConfigured={aiConfigured}");
                    return (Summary: manifest?.Description ?? "", Presentation: presentation, AiConfigured: aiConfigured);
                });

                // Back on the UI thread (the awaiter marshals here): apply values only - no disk
                // reads, no log writes (round 3, defect 3).
                if (loaded.Summary.Length > 0)
                {
                    _summary.Text = loaded.Summary;
                    _summary.Visibility = Visibility.Visible;
                }
                else if (!loaded.AiConfigured)
                {
                    _aiBanner.Visibility = Visibility.Visible;
                }
                var presentation = loaded.Presentation;
                _transcriptText = presentation.Text;
                _transcriptBody.Text = presentation.Text.Length > 0 ? presentation.Text
                    : (_item.Status.Length > 0 ? "Transcribing..." : "No transcript for this recording.");
                _transcriptBody.Foreground = presentation.Text.Length > 0
                    ? Res<Brush>("DkText") : Res<Brush>("DkDim");
                if (presentation.LegacyNotice != null)
                {
                    _legacyNotice.Text = presentation.LegacyNotice;
                    _legacyNotice.Visibility = Visibility.Visible;
                }
                _copyButton.Visibility = presentation.CanCopy ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                // Logged from a worker as well: the window shows its degraded state without first
                // waiting on a disk append (round 3, defect 3).
                await Task.Run(() => Log.Error($"[RecordingDetailWindow] LoadDetailsAsync FAILED for {_item.Dir}", ex));
                _transcriptText = "";
                _transcriptBody.Text = "No transcript for this recording.";
                _transcriptBody.Foreground = Res<Brush>("DkDim");
            }
        }

        /// <summary>Opens the recording folder in Explorer. A click handler's single
        /// Directory.Exists probe (user-initiated, never paint-critical) - and a missing folder
        /// says so instead of silently doing nothing.</summary>
        private void OpenFolder()
        {
            if (!Directory.Exists(_item.Dir))
            {
                _status.Text = "Recording folder not found.";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{_item.Dir}\"") { UseShellExecute = true });
        }

        private Button Btn(string text, RoutedEventHandler onClick)
        {
            var b = new Button { Content = text, Style = Res<Style>("DkMini"), Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += onClick;
            return b;
        }

        private void Play()
        {
            try
            {
                _player?.Close();
                _player = new PreviewWindow(_item.Title, _item.MediaPath, _item.MediaKind) { Owner = this };
                _player.Closed += (_, _) => _player = null;
                _player.Show();
            }
            catch (Exception ex) { _status.Text = "Preview error: " + ex.Message; }
        }

        private void CopyTranscript(string transcript)
        {
            try { Clipboard.SetText(transcript); _status.Text = "Transcript copied."; }
            catch (Exception ex) { _status.Text = "Copy error: " + ex.Message; }
        }

        private async void CommitRename()
        {
            string name = _title.Text.Trim();
            if (name.Length == 0 || name == _item.Title) return;
            try
            {
                _status.Text = "Renaming...";
                // Off the UI thread: the write takes the recording's manifest lock and flushes to
                // physical disk, so it can wait on whatever else is writing that recording.
                await Task.Run(() => ManifestStore.Update(_item.Dir, m => m.DisplayName = name));
                // Through the library's coherence model, never onto the row directly (issue #3):
                // the row was captured before that await, and the new name has to be stamped as
                // newer than any reload still in flight or the next one to land reverts it.
                _rename(name);
                _status.Text = "Renamed.";
            }
            catch (Exception ex)
            {
                // Log from a worker (round 3 sweep): the error line must show without the UI
                // thread first taking the log lock and appending to disk.
                await Task.Run(() => Log.Error($"[RecordingDetailWindow] CommitRename FAILED: dir={_item.Dir}", ex));
                _status.Text = "Rename error: " + ex.Message;
            }
        }
    }
}
