using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgentEyes.App
{
    /// <summary>
    /// In-app playback for a recording - no need to open the folder. Video and audio play through
    /// MediaElement (the Windows media stack, same codecs as Movies and TV); screenshots show in an
    /// Image. One window handles all three kinds; Esc closes, Space toggles play/pause.
    /// </summary>
    public partial class PreviewWindow : Window
    {
        private static readonly Geometry PlayGlyphData = Geometry.Parse("M 4 2 L 20 12 L 4 22 Z");
        private static readonly Geometry PauseGlyphData =
            Geometry.Parse("M 6 4 L 10 4 L 10 20 L 6 20 Z M 14 4 L 18 4 L 18 20 L 14 20 Z");

        private readonly bool _isMedia;          // video or audio (vs a still image)
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private bool _playing;
        private bool _syncingSlider;             // guard: timer-driven slider moves are not seeks

        internal PreviewWindow(string title, string path, string kind)
        {
            InitializeComponent();
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            Title = title;
            _isMedia = kind is "video" or "audio";

            if (!_isMedia)
            {
                // Screenshot: load fully into memory (OnLoad) so the file is not kept locked
                // and the recording can still be renamed/deleted while the preview is open.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                Still.Source = bmp;
                Still.Visibility = Visibility.Visible;
                ControlsBar.Visibility = Visibility.Collapsed;
                SizeToContentArea(bmp.PixelWidth, bmp.PixelHeight);
                return;
            }

            if (kind == "audio")
            {
                AudioGlyph.Visibility = Visibility.Visible;
                Width = 480;
                Height = 250;
            }

            Media.Volume = 0.85;
            Media.Source = new Uri(path);
            _timer.Tick += (_, _) => SyncUi();
            Loaded += (_, _) => Play();
            Closed += (_, _) => { _timer.Stop(); Media.Close(); };
        }

        // ---- transport ------------------------------------------------------

        private void Play()
        {
            Media.Play();
            _playing = true;
            PlayGlyph.Data = PauseGlyphData;
            _timer.Start();
        }

        private void Pause()
        {
            Media.Pause();
            _playing = false;
            PlayGlyph.Data = PlayGlyphData;
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_playing) Pause(); else Play();
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            Media.Position = TimeSpan.Zero;
            Play();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); return; }
            if (e.Key == Key.Space && _isMedia) { PlayPause_Click(sender, e); e.Handled = true; }
        }

        // ---- media events ---------------------------------------------------

        private void Media_Opened(object sender, RoutedEventArgs e)
        {
            if (Media.NaturalDuration.HasTimeSpan)
                SeekBar.Maximum = Media.NaturalDuration.TimeSpan.TotalSeconds;
            if (Media.NaturalVideoWidth > 0)
                SizeToContentArea(Media.NaturalVideoWidth, Media.NaturalVideoHeight);
            SyncUi();
        }

        private void Media_Ended(object sender, RoutedEventArgs e)
        {
            Pause();
            Media.Position = TimeSpan.Zero;
            SyncUi();
        }

        private void Media_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            _timer.Stop();
            ErrorText.Text = "Playback failed: " + (e.ErrorException?.Message ?? "unknown error");
            ErrorText.Visibility = Visibility.Visible;
        }

        // ---- seek bar / clock ----------------------------------------------

        private void SyncUi()
        {
            if (!Media.NaturalDuration.HasTimeSpan) return;
            var pos = Media.Position;
            var total = Media.NaturalDuration.TimeSpan;
            _syncingSlider = true;
            SeekBar.Value = pos.TotalSeconds;
            _syncingSlider = false;
            TimeText.Text = $"{Clock(pos)} / {Clock(total)}";
        }

        private void Seek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingSlider || !Media.NaturalDuration.HasTimeSpan) return;
            Media.Position = TimeSpan.FromSeconds(e.NewValue);
            if (!_playing) SyncUi();   // scrubbing while paused still updates the clock
        }

        private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

        // ---- sizing ----------------------------------------------------------

        /// <summary>Size the window to the media's pixels, clamped to 80% of the work area
        /// (small things stay small, a 4K capture does not swallow the desktop).</summary>
        private void SizeToContentArea(int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return;
            double maxW = SystemParameters.WorkArea.Width * 0.8;
            double maxH = SystemParameters.WorkArea.Height * 0.8;
            double chrome = _isMedia ? 92 : 48;   // title bar + transport bar
            double scale = Math.Min(1.0, Math.Min(maxW / pixelWidth, (maxH - chrome) / pixelHeight));
            Width = Math.Max(MinWidth, pixelWidth * scale);
            Height = Math.Max(MinHeight, pixelHeight * scale + chrome);
        }
    }
}
