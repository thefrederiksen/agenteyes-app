using System;
using System.IO;
using System.Threading.Tasks;
using AgentEyes.App;
using AgentEyes.LinuxApp.Model;
using AgentEyes.LinuxApp.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AgentEyes.LinuxApp.Views
{
    /// <summary>
    /// One recording's details (issue #107) - the Windows RecordingDetailWindow: the editable title
    /// (rename on Enter or when the box loses focus), date and duration, the AI summary, the transcript,
    /// and Play / Walkthrough / Copy transcript / Open folder / Delete. Everything loads after the
    /// window shows.
    /// </summary>
    public partial class DetailWindow : Window
    {
        private readonly LibraryEntry _entry;
        private readonly MainWindow? _main;
        private string _savedTitle;
        private string _transcript = "";

        public DetailWindow() : this(new LibraryEntry(), null) { }

        internal DetailWindow(LibraryEntry entry, MainWindow? main)
        {
            InitializeComponent();
            _entry = entry;
            _main = main;
            _savedTitle = entry.Title;
            Log.Info($"[DetailWindow] ctor: {entry.Id}");
            Title = "Details - " + entry.Title;
            TitleBox.Text = entry.Title;
            MetaText.Text = entry.Detail;
            ToolTip.SetTip(MetaText, entry.Detail + "\n" + entry.Id + (entry.DurationSeconds > 0 ? "   -   " + entry.Duration : ""));
            PlayButton.IsVisible = entry.CanPreview;
            WalkthroughButton.Content = entry.HasWalkthrough ? "Open walkthrough" : "Build walkthrough";
            WalkthroughButton.IsVisible = !entry.IsShot;
            TitleBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SaveTitleAsync(); } };
            TitleBox.LostFocus += async (_, _) => await SaveTitleAsync();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Opened += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                var (manifest, transcript) = await Task.Run(() =>
                {
                    var m = Manifest.Load(_entry.Dir);
                    return (m, TranscriptPresentation.For(_entry.Dir, m));
                });
                bool signedIn = AppState.Current.Status?.SignedIn == true;
                string summary = manifest.Description ?? "";
                SummaryText.Text = summary;
                SummaryText.IsVisible = summary.Length > 0;
                AiOffBanner.IsVisible = summary.Length == 0 && !signedIn;

                _transcript = transcript.Text;
                TranscriptBox.Text = transcript.Kind == TranscriptKind.None
                    ? (signedIn ? "Not transcribed yet - it is on its way." : "Not transcribed.")
                    : transcript.Text;
                TranscriptCaption.Text = "Not transcribed - showing the text file";
                TranscriptCaption.IsVisible = transcript.Kind == TranscriptKind.FlatTextOnly;
                CopyButton.IsEnabled = _transcript.Length > 0;
                Log.Info($"[DetailWindow] LoadAsync: {_entry.Id} transcript={transcript.Kind} chars={_transcript.Length}");
            }
            catch (Exception ex)
            {
                Log.Error($"[DetailWindow] LoadAsync FAILED: {_entry.Dir}", ex);
                TranscriptBox.Text = "Could not read this recording: " + ex.Message;
            }
            finally
            {
                LoadingText.IsVisible = false;
            }
        }

        private async Task SaveTitleAsync()
        {
            string name = (TitleBox.Text ?? "").Trim();
            if (name.Length == 0) { TitleBox.Text = _savedTitle; return; }
            if (name == _savedTitle || _main == null) return;
            StatusText.Text = "Renaming...";
            if (await _main.RenameAsync(_entry, name))
            {
                _savedTitle = name;
                Title = "Details - " + name;
                StatusText.Text = $"Renamed to \"{name}\".";
            }
            else
            {
                StatusText.Text = "Rename failed - see the status line in the main window.";
            }
        }

        private void Play_Click(object? sender, RoutedEventArgs e) => _main?.OpenPreview(new LibraryItem(_entry));

        private async void Walkthrough_Click(object? sender, RoutedEventArgs e)
        {
            if (_main == null) return;
            string file = Path.Combine(_entry.Dir, "walkthrough.html");
            if (File.Exists(file)) { _main.OpenPath(file); return; }
            StatusText.Text = "Building the walkthrough...";
            WalkthroughButton.IsEnabled = false;
            await _main.BuildWalkthroughAsync(_entry);
            WalkthroughButton.IsEnabled = true;
            bool built = File.Exists(file);
            WalkthroughButton.Content = built ? "Open walkthrough" : "Build walkthrough";
            StatusText.Text = built ? "Walkthrough built." : "The walkthrough was not built - see the status line in the main window.";
        }

        private async void Copy_Click(object? sender, RoutedEventArgs e)
        {
            if (_transcript.Length == 0 || Clipboard == null) return;
            await Clipboard.SetTextAsync(_transcript);
            Log.Info($"[DetailWindow] Copy_Click: {_transcript.Length} chars");
            StatusText.Text = "Transcript copied to the clipboard.";
        }

        private void OpenFolder_Click(object? sender, RoutedEventArgs e) => _main?.OpenPath(_entry.Dir);

        private async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (_main == null) return;
            if (await _main.DeleteAsync(new[] { new LibraryItem(_entry) })) Close();
        }

        private void SignIn_Click(object? sender, RoutedEventArgs e) => new SettingsWindow("Account").ShowDialog(this);
    }
}
