using System;
using AgentEyes.LinuxApp.Model;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace AgentEyes.LinuxApp.Views
{
    /// <summary>
    /// Plays a recording inside the app (issue #107) - the Windows PreviewWindow: Play/Pause, Restart,
    /// a seek bar and "m:ss / m:ss". Space plays / pauses, Esc closes. Video and audio go through
    /// libvlc; a screenshot is shown as an image with no transport bar.
    /// </summary>
    public partial class PlayerWindow : Window
    {
        private static LibVLC? _vlc;
        private static readonly Avalonia.Media.Geometry PlayGlyph = Avalonia.Media.Geometry.Parse("M0,0 L10,6 L0,12 Z");
        private static readonly Avalonia.Media.Geometry PauseGlyph = Avalonia.Media.Geometry.Parse("M0,0 H3.5 V12 H0 Z M6.5,0 H10 V12 H6.5 Z");
        private readonly LibraryEntry _entry;
        private MediaPlayer? _player;
        private Media? _media;
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _dragging;

        // Parameterless constructor for the XAML loader only.
        public PlayerWindow() : this(new LibraryEntry()) { }

        internal PlayerWindow(LibraryEntry entry)
        {
            InitializeComponent();
            _entry = entry;
            Title = entry.Title;
            Log.Info($"[PlayerWindow] ctor: {entry.MediaPath} ({entry.Media})");
            KeyDown += OnKeyDown;
            Seek.AddHandler(PointerPressedEvent, (_, _) => _dragging = true, RoutingStrategies.Tunnel);
            Seek.AddHandler(PointerReleasedEvent, (_, _) => { _dragging = false; SeekTo(Seek.Value); }, RoutingStrategies.Tunnel, handledEventsToo: true);
            _tick.Tick += (_, _) => UpdateTime();
            Opened += (_, _) => Load();
        }

        private void Load()
        {
            if (_entry.MediaPath == null) { LoadingText.Text = "Nothing to play."; return; }
            if (_entry.Media == MediaKind.Image)
            {
                Transport.IsVisible = false;
                Picture.Source = new Bitmap(_entry.MediaPath);
                Picture.IsVisible = true;
                LoadingText.IsVisible = false;
                return;
            }
            try
            {
                _vlc ??= new LibVLC("--no-video-title-show");
                _player = new MediaPlayer(_vlc) { Volume = 85 };
                _player.Playing += (_, _) => Dispatcher.UIThread.Post(() => { PlayIcon.Data = PauseGlyph; LoadingText.IsVisible = false; });
                _player.Paused += (_, _) => Dispatcher.UIThread.Post(() => PlayIcon.Data = PlayGlyph);
                _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => { PlayIcon.Data = PlayGlyph; Seek.Value = Seek.Maximum; });
                _player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    Log.Error($"[PlayerWindow] libvlc could not play {_entry.MediaPath}");
                    LoadingText.Text = "This recording could not be played.";
                    LoadingText.IsVisible = true;
                });
                if (_entry.Media == MediaKind.Video)
                {
                    Video.IsVisible = true;
                    Video.MediaPlayer = _player;
                }
                else
                {
                    AudioGlyph.IsVisible = true;
                    AudioTitle.Text = _entry.Title;
                }
                _media = new Media(_vlc, new Uri(_entry.MediaPath));
                _player.Play(_media);
                _tick.Start();
                Log.Info($"[PlayerWindow] Load: playing {_entry.MediaPath}");
            }
            catch (Exception ex)
            {
                // Entry point (window opened): say so in the window instead of failing silently.
                Log.Error($"[PlayerWindow] Load FAILED: {_entry.MediaPath}", ex);
                LoadingText.Text = "The player could not start: " + ex.Message;
            }
        }

        private void UpdateTime()
        {
            if (_player == null) return;
            long len = _player.Length;
            long pos = _player.Time;
            if (len > 0)
            {
                Seek.Maximum = len / 1000.0;
                if (!_dragging) Seek.Value = Math.Max(0, pos) / 1000.0;
            }
            TimeText.Text = $"{Fmt(Math.Max(0, pos))} / {Fmt(Math.Max(0, len))}";
        }

        internal static string Fmt(long ms)
        {
            long s = ms / 1000;
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}" : $"{s / 60}:{s % 60:D2}";
        }

        private void SeekTo(double seconds)
        {
            if (_player == null) return;
            Log.Info($"[PlayerWindow] SeekTo: {seconds:0.0}s");
            if (_player.State == VLCState.Ended) { _player.Stop(); _player.Play(); }
            _player.Time = (long)(seconds * 1000);
        }

        private void PlayPause_Click(object? sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause();
            else if (_player.State == VLCState.Ended) { _player.Stop(); _player.Play(); }
            else _player.Play();
        }

        private void Restart_Click(object? sender, RoutedEventArgs e)
        {
            if (_player == null) return;
            Log.Info("[PlayerWindow] Restart");
            _player.Stop();
            _player.Play();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        protected override void OnClosed(EventArgs e)
        {
            Log.Info("[PlayerWindow] OnClosed");
            _tick.Stop();
            var p = _player;
            var m = _media;
            _player = null;
            Video.MediaPlayer = null;
            // libvlc's Stop waits for its threads; never on the UI thread.
            if (p != null) System.Threading.Tasks.Task.Run(() => { p.Stop(); p.Dispose(); m?.Dispose(); });
            base.OnClosed(e);
        }
    }
}
