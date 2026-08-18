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
            string summaryText = "";
            try { summaryText = Manifest.Load(item.Dir).Description ?? ""; } catch { }
            bool aiConfigured = AgentEyes.DevThrottle.DevThrottleAccount.IsSignedIn;

            if (!aiConfigured && summaryText.Length == 0)
            {
                var banner = new Border
                {
                    Background = Res<Brush>("DkCard"),
                    BorderBrush = Res<Brush>("DkAccent"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 14, 0, 0),
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
                Grid.SetRow(banner, 2);
                banner.Child = bannerRow;
                root.Children.Add(banner);
            }
            else if (summaryText.Length > 0)
            {
                var summary = new TextBlock
                {
                    Text = summaryText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 20,
                    Foreground = Res<Brush>("DkText"),
                    Margin = new Thickness(2, 14, 0, 0),
                };
                Grid.SetRow(summary, 3);
                root.Children.Add(summary);
            }

            // ---- transcript ----
            var transcriptHost = new Border
            {
                Background = Res<Brush>("DkCard"),
                BorderBrush = Res<Brush>("DkBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 14, 0, 0),
            };
            string transcriptPath = Path.Combine(item.Dir, "transcript.txt");
            string transcript = "";
            try { if (File.Exists(transcriptPath)) transcript = File.ReadAllText(transcriptPath).Trim(); } catch { }
            transcriptHost.Child = new TextBox
            {
                Text = transcript.Length > 0 ? transcript
                    : (item.Status.Length > 0 ? "Transcribing..." : "No transcript for this recording."),
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = transcript.Length > 0 ? Res<Brush>("DkText") : Res<Brush>("DkDim"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(transcriptHost, 4);
            root.Children.Add(transcriptHost);
            _hasTranscript = transcript.Length > 0;

            // ---- actions ----
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            if (item.MediaPath.Length > 0) actions.Children.Add(Btn("Play", (_, _) => Play()));
            if (item.WalkthroughVisibility == Visibility.Visible)
                actions.Children.Add(Btn(File.Exists(Path.Combine(item.Dir, "walkthrough.html")) ? "Open walkthrough" : "Build walkthrough",
                    async (_, _) => { await _rebuildWalkthrough(); }));
            if (_hasTranscript) actions.Children.Add(Btn("Copy transcript", (_, _) => CopyTranscript(transcript)));
            actions.Children.Add(Btn("Open folder", (_, _) =>
            {
                if (Directory.Exists(item.Dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{item.Dir}\"") { UseShellExecute = true });
            }));
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
        }

        private readonly bool _hasTranscript;

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
                Log.Error($"[RecordingDetailWindow] CommitRename FAILED: dir={_item.Dir}", ex);
                _status.Text = "Rename error: " + ex.Message;
            }
        }
    }
}
