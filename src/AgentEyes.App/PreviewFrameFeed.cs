using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentEyes;
using AgentEyes.Preview;

namespace AgentEyes.App
{
    /// <summary>One publish of the live preview: the newest picture for each track, and whether that
    /// picture is too old to be shown as live.</summary>
    internal sealed class PreviewSnapshot
    {
        public BitmapSource? Screen { get; init; }
        public BitmapSource? Camera { get; init; }

        /// <summary>True when no fresh screen frame has arrived - INCLUDING when none ever has. The
        /// panel shows a message rather than the last picture it managed to get (issue #33, AC10).</summary>
        public bool ScreenStale { get; init; } = true;

        public bool CameraStale { get; init; } = true;
    }

    /// <summary>
    /// Reads published preview frames off disk and hands them to the HUD (issue #33).
    ///
    /// EVERY BYTE OF FILE I/O AND EVERY JPEG DECODE HAPPENS ON THIS THREAD, never on the UI thread
    /// (repo coding standard 1). The window is handed finished, FROZEN bitmaps through the dispatcher
    /// and does nothing but assign them, so showing the preview cannot make the Stop button late.
    ///
    /// It is a READER and only a reader. It never talks to ffmpeg, never touches the recording, and
    /// its failure - a missing file, a directory that was deleted, an image that will not decode -
    /// is reported as a stale picture and a WARNING. That is the whole of what a preview failure is
    /// allowed to cost (AC10).
    /// </summary>
    internal sealed class PreviewFrameFeed : IDisposable
    {
        /// <summary>Poll interval. Matched to the tap's publish rate
        /// (<see cref="AgentEyes.Video.FfmpegArgs.PreviewFps"/>) - polling faster would only re-read
        /// bytes that have not changed.</summary>
        private const int PollMs = 100;

        private readonly Dispatcher _ui;
        private readonly Action<PreviewSnapshot> _publish;
        private readonly CancellationTokenSource _cancel = new();

        private Thread? _thread;
        private volatile string? _screenPath;
        private volatile string? _cameraPath;
        private volatile bool _wantScreen;
        private volatile bool _wantCamera;
        private volatile bool _disposed;

        private BitmapSource? _screen;
        private BitmapSource? _camera;
        private DateTime? _screenAtUtc;
        private DateTime? _cameraAtUtc;
        private bool _decodeFailureLogged;

        public PreviewFrameFeed(Dispatcher ui, Action<PreviewSnapshot> publish)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        }

        /// <summary>Which files to read, and which of them the panel currently wants. Set from the UI
        /// thread whenever the mode changes; the reader picks them up on its next tick.</summary>
        public void Want(string? screenPath, bool wantScreen, string? cameraPath, bool wantCamera)
        {
            _screenPath = screenPath;
            _cameraPath = cameraPath;
            _wantScreen = wantScreen;
            _wantCamera = wantCamera;
            Log.Info($"[PreviewFrameFeed] Want: screen={wantScreen} camera={wantCamera}");
        }

        /// <summary>Start reading. Idempotent.</summary>
        public void Start()
        {
            if (_thread != null || _disposed) return;
            Log.Info("[PreviewFrameFeed] Start");
            _thread = new Thread(Loop) { IsBackground = true, Name = "AgentEyes HUD preview feed" };
            _thread.Start();
        }

        /// <summary>
        /// The reader loop. A THREAD ENTRY POINT, so it carries the try/catch: nothing it reads is
        /// under its control - a file another process is replacing ten times a second, inside a
        /// directory that may be deleted while a person is testing exactly that - and none of it may
        /// escape into an unhandled exception.
        /// </summary>
        private void Loop()
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    Tick();
                    if (_cancel.Token.WaitHandle.WaitOne(PollMs)) break;
                }
                Log.Info("[PreviewFrameFeed] Loop: ended");
            }
            catch (Exception ex)
            {
                Log.Error("[PreviewFrameFeed] Loop FAILED - the preview stops updating; "
                          + "the recording is unaffected", ex);
            }
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;

            if (_wantScreen && TryLoad(_screenPath) is { } screen) { _screen = screen; _screenAtUtc = now; }
            if (!_wantScreen) { _screen = null; _screenAtUtc = null; }

            if (_wantCamera && TryLoad(_cameraPath) is { } camera) { _camera = camera; _cameraAtUtc = now; }
            if (!_wantCamera) { _camera = null; _cameraAtUtc = null; }

            bool screenStale = HudPreviewState.IsStale(_screenAtUtc, now);
            bool cameraStale = HudPreviewState.IsStale(_cameraAtUtc, now);

            // A stale track publishes NO picture. Handing the window the last frame it managed to
            // read - with a flag beside it saying not to trust it - is exactly the frozen-last-frame
            // failure AC10 forbids, one careless binding away.
            var snapshot = new PreviewSnapshot
            {
                Screen = screenStale ? null : _screen,
                Camera = cameraStale ? null : _camera,
                ScreenStale = screenStale,
                CameraStale = cameraStale,
            };

            if (!_cancel.IsCancellationRequested)
                _ui.BeginInvoke(DispatcherPriority.Background, _publish, snapshot);
        }

        /// <summary>
        /// The newest WHOLE frame at <paramref name="path"/> as a frozen bitmap, or null when there
        /// is not one right now. Frozen because it crosses to the UI thread, and a bitmap that is not
        /// frozen cannot.
        /// </summary>
        private BitmapSource? TryLoad(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var bytes = PreviewFrameFile.TryRead(path, out string? readError);
            if (bytes == null)
            {
                if (readError != null && !_decodeFailureLogged)
                {
                    _decodeFailureLogged = true;
                    Log.Warn($"[PreviewFrameFeed] TryLoad: cannot read the preview frame {path} - {readError}. "
                             + "The panel will say the preview is unavailable; the recording is unaffected.");
                }
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(bytes);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                _decodeFailureLogged = false;
                return image;
            }
            catch (Exception ex)
            {
                // The bytes carried both JPEG markers and still would not decode. That is worth
                // saying once - and worth saying only once, at ten reads a second.
                if (!_decodeFailureLogged)
                {
                    _decodeFailureLogged = true;
                    Log.Warn($"[PreviewFrameFeed] TryLoad: a complete-looking preview frame from {path} "
                             + $"would not decode - {ex.Message}. The recording is unaffected.");
                }
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Log.Info("[PreviewFrameFeed] Dispose");
            _cancel.Cancel();
            var thread = _thread;
            if (thread != null && thread.IsAlive) thread.Join(PollMs * 10);
            _cancel.Dispose();
        }
    }
}
