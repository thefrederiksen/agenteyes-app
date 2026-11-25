using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentEyes.Audio;
using AgentEyes.Takes;

namespace AgentEyes.App
{
    /// <summary>
    /// Issue #230, screen 2 - the RECORDING window: full screen on the chosen screen, black, the
    /// words at the very top edge in a narrow column right under the camera, a small REC timer and
    /// "take N of M - attempt K" at the bottom. Nothing to change. Keys only: Space (mark the start
    /// / the end of a take), R (retake), Esc (finish).
    ///
    /// Round 3 - CONTINUOUS RECORDING. The second live take lost most of what was said: the camera
    /// only recorded from the moment it was up, three seconds after Space. Now ONE camera+mic
    /// recording starts when this screen opens and runs until it closes. Space and R only MARK where
    /// an attempt starts and ends; each attempt is cut out of the continuous file afterwards with
    /// <see cref="TakeCut.MarginSeconds"/> either side (<see cref="TakeCut"/>) into the session's
    /// takes\&lt;take id&gt;\ folder. The continuous file is always kept, so nothing said on this
    /// screen is lost even when a cut fails - and a failed cut is said on screen and in the log.
    ///
    /// Teleprompter (the default): the take's WHOLE text stays in the column under the camera - no
    /// phrase stepping. Read first: the text shows until Space and goes away while recording.
    ///
    /// Everything is logged: each key with the screen state and take/attempt, every change of the
    /// text on screen, each mark with its time in the continuous file, each cut, and the microphone
    /// level about once a second.
    ///
    /// Round 4 (the full review, REVIEW-agenteyes-takes.md beside the video's mandate): no mark before
    /// the camera is up; the continuous recording is encoded with zerolatency so its progress clock is
    /// honest, and the final cuts take their anchor from the file itself; a short live cut is cut
    /// again, never failed; the last take does not end the session; read first counts 3-2-1; a
    /// screen that lost the keyboard says so; a deferred update waits for the cuts.
    /// </summary>
    internal sealed class TakeRecordingWindow : Window
    {
        private enum Phase { Starting, Ready, Recording, Finishing, Finished, Failed }

        internal const string StartInstruction = "Press Space to start recording. Space again to stop.";
        internal const string ReadFirstInstruction = "Read the line. When you press Space the text is replaced by a 3-2-1 count, and you say it to the camera.";
        internal const string RecordingInstruction = "Recording - Space to stop, R to start again";
        /// <summary>Round 4 (review D2): nothing is recorded until the camera is up, and the screen says so instead of taking a mark.</summary>
        internal const string WaitInstruction = "The camera is starting - wait.";
        internal const string WaitKeys = "Wait for the camera - Esc finish";
        /// <summary>Round 4 (review I1): the last take does not end the session; he can record it again.</summary>
        internal const string LastTakeMessage = "All takes marked. Space records the last take again, Esc finishes.";
        /// <summary>Round 4 (review R4): keys go to the active window; this screen says when that is not it.</summary>
        internal const string NotListeningBanner = "This screen is not getting your keys - click it";
        /// <summary>Round 4 (review I3): read first shows a 3-2-1 before the text goes, as the approved mockup had.</summary>
        internal const int CountSeconds = 3;
        private const double LineFontSize = 40;
        private const double CountFontSize = 120;

        /// <summary>The continuous recording's keyframe interval in frames: one a second at 30 fps,
        /// so the live file is never more than a second behind what can be cut from it.</summary>
        private const int Fps = 30;

        /// <summary>
        /// PR 234 review, defect 1: in camera-only mode the service runs no capture watchdog, and a camera
        /// ffmpeg that dies leaves the service "recording". The one sign is a position that stops moving;
        /// ffmpeg reports it twice a second, and the first report comes about two seconds after the camera
        /// is up, so five seconds without movement is a dead camera and never a slow one.
        /// </summary>
        private const double StallSeconds = 5;

        private readonly RecordingService _svc;
        private readonly TakeStore _store;
        private readonly List<Take> _takes;
        private readonly string _camera;
        private readonly string _mic;
        private readonly bool _teleprompter;
        private readonly MonitorInfo _monitor;

        private readonly TextBlock _line = new() { FontSize = 40, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, LineHeight = 52 };
        private readonly TextBlock _hint = new() { FontSize = 24, Foreground = TakesWindow.Green, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        private readonly TextBlock _instruction = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 28, 0, 0), LineHeight = 40 };
        private readonly TextBlock _message = new() { FontSize = 18, Foreground = TakesWindow.Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        private readonly TextBlock _rec = new() { FontSize = 15, Foreground = TakesWindow.Red, Margin = new Thickness(0, 0, 18, 0) };
        private readonly TextBlock _where = new() { FontSize = 15, Foreground = TakesWindow.Dim };
        private readonly TextBlock _keys = new() { FontSize = 16, Foreground = TakesWindow.Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _banner = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = TakesWindow.Red, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly Stopwatch _clock = new();               // the screen's clock; marks are read off it
        private readonly ContinuousClock _fileClock = new();      // screen clock -> continuous file time

        private Phase _phase = Phase.Starting;
        private int _index;                    // which take in _takes
        private int _attempt;                  // the attempt number being / about to be recorded
        private double _attemptStart;          // screen-clock second of the current start mark
        private int _attemptTakeNumber;        // the take's "Take N" in the list, read at the start mark
        private readonly Dictionary<string, int> _nextNumber = new(StringComparer.OrdinalIgnoreCase);

        // The continuous recording.
        private volatile bool _recordingUp;     // StartCamera returned
        private volatile bool _stopDone;        // our stop has finished: the file is final
        private bool _stopping;                 // our stop has been started
        private bool _finishRequested;          // Esc / Alt+F4 / last take while the camera was starting
        private volatile bool _startFailed;     // the last StartCamera failed: nothing is coming
        private volatile string? _sessionDir;
        private volatile string? _sessionFile;
        private double _sessionStart;           // screen-clock second StartCamera returned
        private double _stopWall;               // screen-clock second the file is taken to end at; written BEFORE _stopDone, read once it is set
        private double _lastPosition;           // the camera position last read (seconds written): the REC clock
        private double _lastAdvanceWall;        // screen-clock second that position last moved; frozen means the camera died

        // The cuts: one at a time, in the order they were marked.
        private Task _cuts = Task.CompletedTask;
        private int _cutsPending;
        private int _saved;
        private readonly List<string> _cutFailures = new();

        // The microphone level, logged about once a second.
        private AudioCapture? _meter;
        private float _peak;
        private int _ticks;

        private bool _allowClose;
        private double _countEnd;               // read first: screen-clock second the 3-2-1 ends; 0 when no count runs
        private IDisposable? _work;             // round 4 (review R3): a deferred update restart waits for this screen

        public TakeRecordingWindow(RecordingService svc, TakeStore store, List<Take> takes, string camera, string mic,
            string readingMode, MonitorInfo monitor)
        {
            Log.Info($"[TakeRecordingWindow] ctor: takes={takes.Count} camera={camera} mic={mic} reading={readingMode} screen={monitor.Index}");
            _svc = svc; _store = store; _takes = takes; _camera = camera; _mic = mic;
            _teleprompter = readingMode == TakeStore.Teleprompter;
            _monitor = monitor;

            Title = "AgentEyes - Recording takes";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Black;
            Topmost = true;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.None;

            var top = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            top.Children.Add(_line);
            top.Children.Add(_hint);
            top.Children.Add(_instruction);
            top.Children.Add(_message);

            var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            bottomRow.Children.Add(_rec);
            bottomRow.Children.Add(_where);
            var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18) };
            bottom.Children.Add(_banner);
            bottom.Children.Add(bottomRow);
            bottom.Children.Add(_keys);

            var grid = new Grid();
            grid.Children.Add(top);
            grid.Children.Add(bottom);
            Content = grid;

            // The narrow column under the lens: about a third of the screen, never wider than 760.
            SizeChanged += (_, _) => top.Width = Math.Min(760, Math.Max(420, ActualWidth * 0.36));

            SourceInitialized += (_, _) => CoverMonitor();
            Loaded += (_, _) => OnLoaded();
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Closed += (_, _) => { StopMeter(); _work?.Dispose(); _work = null; };
            // Round 4 (review R4): a click anywhere else takes the keys with it. Say so, and log it.
            Activated += (_, _) =>
            {
                Log.Info($"[TakeRecordingWindow] activated in {_phase}: keys reach this screen");
                Show(_banner, "banner", "");
            };
            Deactivated += (_, _) =>
            {
                Log.Info($"[TakeRecordingWindow] DEACTIVATED in {_phase}: keys go elsewhere until this screen is clicked");
                if (!_allowClose) Show(_banner, "banner", NotListeningBanner);
            };
            _tick.Tick += (_, _) => OnTick();
        }

        // ---- placement --------------------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpShowWindow = 0x0040;

        /// <summary>Cover the chosen monitor exactly, in device pixels - WPF's own coordinates are
        /// per-DPI and do not place a window on a second monitor with a different scale reliably.</summary>
        private void CoverMonitor()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Log.Info($"[TakeRecordingWindow] CoverMonitor: screen {_monitor.Index} at {_monitor.X},{_monitor.Y} {_monitor.Width}x{_monitor.Height}");
            if (!SetWindowPos(hwnd, HwndTopmost, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, SwpShowWindow))
                Log.Error($"[TakeRecordingWindow] CoverMonitor: SetWindowPos failed, error {Marshal.GetLastWin32Error()}");
        }

        // ---- the text on screen (every change is logged) -----------------------------------------

        private void Show(TextBlock block, string what, string text)
        {
            if (block.Text == text) return;
            block.Text = text;
            Log.Info($"[TakeRecordingWindow] screen {what}: \"{OneLine(text)}\"");
        }

        private static string OneLine(string text) => text.Replace("\r", "").Replace("\n", " / ");

        // ---- the continuous recording -----------------------------------------------------------

        private void OnLoaded()
        {
            Activate();
            Focus();
            _clock.Start();
            _tick.Start();
            _work = PostRecording.TrackWork("takes session");
            StartContinuous();
        }

        /// <summary>
        /// Open the camera and the microphone for the whole session: 1080p MJPEG from the camera's own
        /// mode list (the largest at 30 fps), the chosen microphone, a keyframe every second.
        /// </summary>
        private void StartContinuous()
        {
            _phase = Phase.Starting;
            _startFailed = false;
            ShowTakeText(visible: true);
            Show(_hint, "hint", "Starting the camera...");
            _instruction.Foreground = Brushes.White;
            Show(_instruction, "instruction", WaitInstruction);
            _message.Foreground = TakesWindow.Dim;
            Show(_message, "message", "");
            Show(_keys, "keys", WaitKeys);
            UpdateFooter();

            var name = new TakeName("takes-session", "Takes session - " + (_takes.Count == 1 ? "1 take" : $"{_takes.Count} takes"));
            Log.Info($"[TakeRecordingWindow] StartContinuous: camera={_camera} mic={_mic} keyframes every {Fps} frames");
            Ui.Run(() => _svc.StartCamera(AudioSourceKind.Mic, _mic, _camera, Fps, label: "takes-session", take: name,
                    keyframeInterval: Fps))
              .ContinueWith(t => Ui.Post(() => OnContinuousStarted(t.Exception?.GetBaseException())));
        }

        private void OnContinuousStarted(Exception? error)
        {
            if (error != null)
            {
                Log.Error("[TakeRecordingWindow] OnContinuousStarted: the continuous recording did not start", error);
                _startFailed = true;
                if (_finishRequested) { CloseNow(); return; }
                _phase = Phase.Failed;
                Show(_hint, "hint", "");
                Show(_instruction, "instruction", "");
                _message.Foreground = TakesWindow.Red;
                Show(_message, "message", "The camera did not start: " + error.Message + "\nNothing is being recorded. Space to try again, Esc to finish.");
                Show(_keys, "keys", "Space try again - Esc finish");
                UpdateFooter();
                return;
            }

            // The folder before the flag: a cut thread reads the flag first and the folder second.
            _sessionStart = _clock.Elapsed.TotalSeconds;
            _lastPosition = 0;
            _lastAdvanceWall = _sessionStart;
            string? dir = _svc.Status().Dir;
            if (dir == null) Log.Error("[TakeRecordingWindow] OnContinuousStarted: the service is recording but reports no folder");
            _sessionDir = dir;
            _sessionFile = dir == null ? null : Path.Combine(dir, "recording.mp4");
            _recordingUp = true;
            Log.Info($"[TakeRecordingWindow] OnContinuousStarted: recording continuously into {_sessionFile ?? "(no folder reported)"} "
                     + $"at screen time {_sessionStart:F2}s");
            Show(_hint, "hint", "");

            // Round 4 (review R6): the meter opens the microphone only once the recording holds it, so
            // a device that will not be shared can cost the log line and never the take.
            if (_meter == null) StartMeter();

            if (_finishRequested) { Finish(); return; }
            ShowReady(null);
        }

        /// <summary>
        /// The continuous recording stopped on its own: the service ended it (a capture fault, issue #113)
        /// or - camera-only mode has no watchdog - the camera position froze (PR 234 review, defect 1).
        /// The attempt in progress ends here, the file is taken to end at <paramref name="fileEndWall"/>,
        /// the service is stopped when it does not know yet, and only Esc is left.
        /// </summary>
        private void OnContinuousLost(string why, double fileEndWall)
        {
            Log.Error($"[TakeRecordingWindow] OnContinuousLost: {why} in {_phase} (service state {_svc.Status().State}); "
                      + $"the file is taken to end at screen time {fileEndWall:F2}s; see the recording's manifest in {_sessionDir}");
            // The stop moment BEFORE any cut is queued or _stopDone is published (PR 234 review, defect 3).
            _stopWall = fileEndWall;
            bool serviceStillRecording = _svc.IsRecording;
            if (_phase == Phase.Recording) EndAttempt("the recording stopped by itself");
            _stopping = true;
            _phase = Phase.Failed;
            ShowTakeText(visible: false);
            Show(_hint, "hint", "");
            Show(_instruction, "instruction", "");
            _message.Foreground = TakesWindow.Red;
            Show(_message, "message", "The recording stopped by itself - the camera or microphone stopped sending. "
                                     + "What was recorded is kept. Esc to finish.");
            Show(_keys, "keys", "Esc finish");
            UpdateFooter();

            if (serviceStillRecording)
            {
                // The service does not know: stop it, so the file is final and the camera is let go.
                StopContinuous(error =>
                {
                    if (error == null) return;
                    _message.Foreground = TakesWindow.Red;
                    Show(_message, "message", "Stopping the recording FAILED: " + error.Message + "\nEsc to finish.");
                });
            }
            else
            {
                _stopDone = _svc.Status().State == "idle";
            }
        }

        /// <summary>
        /// Stop the continuous recording on a background thread. <see cref="_stopDone"/> is set once the
        /// stop returned - whatever is on disk after a failed stop is what the cuts get - and then
        /// <paramref name="onDone"/> runs on the UI thread with the stop's error, or null.
        /// </summary>
        private void StopContinuous(Action<Exception?> onDone)
        {
            Log.Info($"[TakeRecordingWindow] StopContinuous: stopping the continuous recording {_sessionFile}");
            Ui.Run(() =>
            {
                var stopped = RecordingStop.Keep(_svc);
                Log.Info($"[TakeRecordingWindow] StopContinuous: the continuous recording is saved, {stopped.Result.DurationSeconds:F1}s in {stopped.Result.Dir}");
            }).ContinueWith(stop =>
            {
                var error = stop.Exception?.GetBaseException();
                if (error != null) Log.Error("[TakeRecordingWindow] StopContinuous: stopping the continuous recording FAILED", error);
                _stopDone = true;
                Ui.Post(() => onDone(error));
            }, TaskScheduler.Default);
        }

        private void OnTick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double? position = _svc.CameraPositionSeconds;
            if (_recordingUp && !_stopping)
            {
                _fileClock.Observe(now, position);
                if (position is double p && p > _lastPosition) { _lastPosition = p; _lastAdvanceWall = now; }

                if (!_svc.IsRecording)
                {
                    // The service ended the recording without us (a capture fault): say so.
                    OnContinuousLost("the service ended the recording", fileEndWall: now);
                }
                else if (now - _lastAdvanceWall > StallSeconds)
                {
                    // The camera died under a service that cannot see it (PR 234 review, defect 1). The
                    // file ends where the position last moved, not where this was noticed.
                    OnContinuousLost($"the camera position has not moved for {now - _lastAdvanceWall:F1}s (stuck at {_lastPosition:F2}s)",
                        fileEndWall: _lastAdvanceWall);
                }
            }
            else if (_stopping && !_stopDone && _phase == Phase.Failed && _svc.Status().State == "idle")
            {
                _stopDone = true;
            }

            UpdateFooter();
            if (_countEnd > 0) ShowCount(now);

            // About once a second: the microphone level, and where the continuous file is.
            if (++_ticks % 4 == 0 && _meter != null)
            {
                float peak = Interlocked.Exchange(ref _peak, 0f);
                string db = peak > 0 ? (20 * Math.Log10(peak)).ToString("F1", CultureInfo.InvariantCulture) + " dBFS" : "silence";
                Log.Info($"[TakeRecordingWindow] mic level: peak {peak:F3} ({db}) phase={_phase} "
                         + $"camera at {(position?.ToString("F2", CultureInfo.InvariantCulture) ?? "-")}s");
            }
        }

        // ---- the microphone meter ---------------------------------------------------------------

        private void StartMeter()
        {
            try
            {
                int device = AudioCapture.ResolveDevice(_mic);
                _meter = new AudioCapture(device);
                _meter.LevelChanged += p =>
                {
                    // Keep the highest peak since the last log line.
                    float seen;
                    do { seen = _peak; if (p <= seen) return; }
                    while (Interlocked.CompareExchange(ref _peak, p, seen) != seen);
                };
                _meter.StartMonitor();
                Log.Info($"[TakeRecordingWindow] StartMeter: metering \"{_mic}\" (device {device}) for the log");
            }
            catch (Exception ex)
            {
                // Metering is for the log only; the recording does not depend on it. Said loudly.
                Log.Error($"[TakeRecordingWindow] StartMeter FAILED: the microphone level will not be logged for \"{_mic}\"", ex);
                _meter?.Dispose();
                _meter = null;
            }
        }

        private void StopMeter()
        {
            if (_meter == null) return;
            Log.Info("[TakeRecordingWindow] StopMeter");
            _meter.Dispose();
            _meter = null;
        }

        // ---- takes and marks --------------------------------------------------------------------

        private Take Current => _takes[_index];

        private void ShowReady(string? note)
        {
            _phase = Phase.Ready;
            Log.Info($"[TakeRecordingWindow] ShowReady: take {_index + 1}/{_takes.Count} id={Current.Id}");
            ShowTakeText(visible: true);
            Show(_hint, "hint", "");
            _instruction.Foreground = Brushes.White;
            Show(_instruction, "instruction", ReadyInstruction(_teleprompter));
            _message.Foreground = TakesWindow.Dim;
            Show(_message, "message", note ?? "");
            Show(_keys, "keys", _teleprompter ? "Space start - Esc finish" : "Space when you have it - Esc finish");
            UpdateFooter();
        }

        /// <summary>What the screen says, in large type, before a take starts (issue #230 round 2).</summary>
        internal static string ReadyInstruction(bool teleprompter) =>
            teleprompter ? StartInstruction : ReadFirstInstruction + "\n" + StartInstruction;

        /// <summary>The take's whole text, or nothing (read first, while recording). Ends any count.</summary>
        private void ShowTakeText(bool visible)
        {
            _countEnd = 0;
            _line.FontSize = LineFontSize;
            Show(_line, "text", visible ? Current.Text : "");
        }

        /// <summary>
        /// Read first (round 4, review I3): "3", "2", "1" in the text column, then nothing. The attempt
        /// was marked at Space and the recording has been running all along; the count is only on the
        /// screen, and the cut's margin covers it.
        /// </summary>
        private void ShowCount(double now)
        {
            int left = (int)Math.Ceiling(_countEnd - now);
            if (left > 0)
            {
                _line.FontSize = CountFontSize;
                Show(_line, "text", left.ToString(CultureInfo.InvariantCulture));
                return;
            }
            _countEnd = 0;
            _line.FontSize = LineFontSize;
            Show(_line, "text", "");
        }

        private void UpdateFooter()
        {
            string where = $"Take {_index + 1} of {_takes.Count} - attempt {_attempt}";
            double now = _clock.Elapsed.TotalSeconds;
            // What the camera has WRITTEN, not how long the screen has been open (PR 234 review, defect 1).
            string session = _recordingUp && !_stopping ? $"REC {TimeSpan.FromSeconds(_lastPosition):m\\:ss}" : "";
            switch (_phase)
            {
                case Phase.Recording:
                    _rec.Text = session;
                    _where.Text = $"{where} - {TimeSpan.FromSeconds(now - _attemptStart):m\\:ss}";
                    break;
                case Phase.Ready:
                    _rec.Text = session;
                    _where.Text = $"Take {_index + 1} of {_takes.Count}" + (_teleprompter ? "" : " - read it, then Space");
                    break;
                case Phase.Finishing:
                case Phase.Finished:
                    _rec.Text = "";
                    _where.Text = $"{_saved} attempt{(_saved == 1 ? "" : "s")} saved"
                                  + (_cutsPending > 0 ? $", {_cutsPending} still cutting" : "");
                    break;
                default:
                    _rec.Text = session;
                    _where.Text = $"Take {_index + 1} of {_takes.Count}";
                    break;
            }
        }

        /// <summary>The number the next attempt at <paramref name="takeId"/> gets: past the store's
        /// attempts and past any attempt marked on this screen that is still being cut.</summary>
        private int ClaimAttemptNumber(string takeId)
        {
            int n = _store.NextAttemptNumber(takeId);
            if (_nextNumber.TryGetValue(takeId, out int local) && local > n) n = local;
            _nextNumber[takeId] = n + 1;
            return n;
        }

        private string MarkDescription(double wallSeconds)
        {
            double? file = _fileClock.FileSeconds(wallSeconds);
            return file == null
                ? $"screen {wallSeconds:F2}s (no progress reading yet - placed in the file at cut time)"
                : $"screen {wallSeconds:F2}s = {file.Value:F2}s in the continuous file";
        }

        /// <summary>Space (or R): mark the start of an attempt. The recording is already running.</summary>
        private bool BeginAttempt()
        {
            string takeId = Current.Id;
            try
            {
                _attemptTakeNumber = _store.TakeNumber(takeId);
                _attempt = ClaimAttemptNumber(takeId);
            }
            catch (UsageException ex)
            {
                // The take list can be replaced through the Control API while this screen is open.
                Log.Error($"[TakeRecordingWindow] BeginAttempt: take {takeId} cannot be recorded", ex);
                _message.Foreground = TakesWindow.Red;
                Show(_message, "message", "This take is no longer in the take list: " + ex.Message + "\nEsc to finish, then open the recording screen again.");
                return false;
            }

            _attemptStart = _clock.Elapsed.TotalSeconds;
            Log.Info($"[TakeRecordingWindow] MARK start: take {_index + 1}/{_takes.Count} id={takeId} attempt={_attempt} at {MarkDescription(_attemptStart)}");
            _phase = Phase.Recording;
            // Teleprompter: the text stays. Read first: a 3-2-1 on screen, then the text goes away and
            // he says it to the lens (round 4, review I3). The recording has been running all along.
            if (_teleprompter) ShowTakeText(visible: true);
            else { _countEnd = _attemptStart + CountSeconds; ShowCount(_attemptStart); }
            Show(_hint, "hint", "");
            _instruction.Foreground = TakesWindow.Red;
            Show(_instruction, "instruction", RecordingInstruction);
            _message.Foreground = TakesWindow.Dim;
            Show(_message, "message", "");
            Show(_keys, "keys", "Space stop - R retake - Esc finish");
            UpdateFooter();
            return true;
        }

        /// <summary>Mark the end of the attempt in progress and queue its cut.</summary>
        private void EndAttempt(string why)
        {
            double end = _clock.Elapsed.TotalSeconds;
            var take = Current;
            int number = _attempt;
            double start = _attemptStart;
            Log.Info($"[TakeRecordingWindow] MARK end ({why}): take {_index + 1}/{_takes.Count} id={take.Id} attempt={number} at {MarkDescription(end)}, "
                     + $"{end - start:F2}s after its start mark");
            QueueCut(take, _attemptTakeNumber, number, start, end);
        }

        // ---- cutting ----------------------------------------------------------------------------

        private void QueueCut(Take take, int takeNumber, int number, double startWall, double endWall)
        {
            Interlocked.Increment(ref _cutsPending);
            UpdateFooter();
            _cuts = _cuts.ContinueWith(_ => CutOne(take, takeNumber, number, startWall, endWall), TaskScheduler.Default);
        }

        /// <summary>Background: wait until the span is in the continuous file, cut it, record it.</summary>
        private void CutOne(Take take, int takeNumber, int number, double startWall, double endWall)
        {
            string label = $"take {takeNumber} ({take.Id}) attempt {number}";
            try
            {
                string file = _sessionFile ?? throw new InvalidOperationException(
                    "there is no continuous file - an attempt was marked before the camera was up, which this screen no longer allows.");
                string folder = Path.Combine(_sessionDir!, "takes", SafeFolderName(take.Id));
                string output = Path.Combine(folder, $"attempt-{number}.mp4");
                double extra = 0;
                double duration;
                while (true)
                {
                    var span = WaitForSpan(startWall, endWall, label, extra, out bool final);
                    Log.Info($"[TakeRecordingWindow] CUT {label}: span {span} of {file} -> {output}{(final ? " (final file)" : "")}");
                    try { duration = TakeCut.Cut(file, span, output); break; }
                    catch (ShortCutException ex) when (!final)
                    {
                        // Round 4 (review R2): the live file was not as far along as its progress said.
                        // Wait longer and cut again; only a cut from the FINAL file is allowed to fail.
                        extra += TakeCut.WrittenBeyondSeconds;
                        Log.Warn($"[TakeRecordingWindow] CUT {label}: {ex.Message} - cutting again once the camera is "
                                 + $"{extra:F0}s further on, or the file is final");
                    }
                }
                _store.AddAttempt(take.Id, output, folder, duration, number, sessionFile: file);
                Log.Info($"[TakeRecordingWindow] CUT {label}: saved, {duration:F2}s");
                Ui.Post(() => OnCut(label, duration, null));
            }
            catch (Exception ex)
            {
                Log.Error($"[TakeRecordingWindow] CUT {label} FAILED - it is still in the continuous recording {_sessionFile ?? "(none)"}", ex);
                Ui.Post(() => OnCut(label, 0, ex));
            }
        }

        /// <summary>
        /// Block until the continuous file holds the attempt's span plus its margin, then return the
        /// span. While recording, that means the camera has written <paramref name="extraSeconds"/>
        /// beyond the usual allowance past it; once the recording is stopped, the file is final, its
        /// own length gives the exact anchor (round 4, review D1 and R1) and the span is clamped to it.
        /// <paramref name="final"/> says which: only a cut from the final file is allowed to fail.
        /// </summary>
        private CutSpan WaitForSpan(double startWall, double endWall, string label, double extraSeconds, out bool final)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                if (_stopDone)
                {
                    double length = AgentEyes.Video.MediaProbe.DurationSeconds(_sessionFile!);
                    if (length <= 0) throw new InvalidOperationException($"the continuous recording {_sessionFile} has no readable length.");
                    double anchor = ContinuousClock.FinalAnchor(_stopWall, length);
                    double? observed = _fileClock.AnchorSeconds;
                    // Exact only if the file ran until the stop (PR 234 review, defect 1): a file that ended
                    // early fails the cut loudly here, never cuts from the wrong place.
                    ContinuousClock.RequireFileRanToTheStop(anchor, observed, _sessionStart);
                    double startFile = ContinuousClock.FileSecondsAt(startWall, anchor);
                    double endFile = ContinuousClock.FileSecondsAt(endWall, anchor);
                    var finalSpan = TakeCut.Span(startFile, endFile, length);
                    // The observed anchor beside the exact one: the real progress lag of the real
                    // camera is in the log after every session.
                    Log.Info($"[TakeRecordingWindow] CUT {label}: the continuous file is final ({length:F2}s); anchor from the file {anchor:F2}s, "
                             + $"from progress readings {(observed?.ToString("F2", CultureInfo.InvariantCulture) ?? "-")}s "
                             + $"(progress lag {(observed is double o ? (o - anchor).ToString("F2", CultureInfo.InvariantCulture) : "-")}s); "
                             + $"marks {startFile:F2}s-{endFile:F2}s");
                    final = true;
                    return finalSpan;
                }

                double? liveStart = _fileClock.FileSeconds(startWall);
                double? liveEnd = _fileClock.FileSeconds(endWall);
                if (liveStart != null && liveEnd != null && _svc.CameraPositionSeconds is double position)
                {
                    var span = TakeCut.Span(liveStart.Value, liveEnd.Value, null);
                    double needed = TakeCut.PositionNeeded(span) + extraSeconds;
                    if (position >= needed)
                    {
                        Log.Info($"[TakeRecordingWindow] CUT {label}: the camera is at {position:F2}s, past {needed:F2}s; "
                                 + $"marks {liveStart:F2}s-{liveEnd:F2}s");
                        final = false;
                        return span;
                    }
                }

                if (waited.Elapsed > TimeSpan.FromMinutes(2))
                    throw new TimeoutException("the continuous recording did not reach the end of the attempt within two minutes.");
                Thread.Sleep(250);
            }
        }

        private static string SafeFolderName(string id)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string(id.Select(c => bad.Contains(c) ? '-' : c).ToArray());
        }

        private void OnCut(string label, double duration, Exception? error)
        {
            Interlocked.Decrement(ref _cutsPending);
            if (error != null)
            {
                _cutFailures.Add($"{label}: {error.Message}");
                _message.Foreground = TakesWindow.Red;
                Show(_message, "message", $"Cutting {label} FAILED: {error.Message}\nIt is still in the continuous recording: {_sessionFile}");
            }
            else
            {
                _saved++;
                if (_phase is Phase.Ready or Phase.Finishing)
                {
                    _message.Foreground = TakesWindow.Dim;
                    // On the last take the hint stays with the news (round 4, review I1) - only once that
                    // take has an attempt from this screen (PR 234 review, defect 2); before that the
                    // news is the previous take's and the last take is still to be recorded.
                    bool lastTake = _phase == Phase.Ready && _index + 1 == _takes.Count && _nextNumber.ContainsKey(Current.Id);
                    Show(_message, "message", $"Saved {label} ({duration:F1} s)." + (lastTake ? "\n" + LastTakeMessage : ""));
                }
            }
            UpdateFooter();
        }

        // ---- finishing --------------------------------------------------------------------------

        /// <summary>
        /// Esc, the last take ended, or a close: stop the continuous recording, let every cut finish,
        /// then close - or stay up showing what failed.
        /// </summary>
        private void Finish()
        {
            Log.Info($"[TakeRecordingWindow] Finish: phase={_phase} recordingUp={_recordingUp} cuts pending={_cutsPending}");
            if (!_recordingUp && !_startFailed && _phase == Phase.Starting)
            {
                // The camera is still opening on another thread: finish once it has.
                _finishRequested = true;
                Show(_message, "message", "Finishing...");
                return;
            }

            _finishRequested = false;
            _phase = Phase.Finishing;
            ShowTakeText(visible: false);
            Show(_hint, "hint", "");
            Show(_instruction, "instruction", "");
            _message.Foreground = TakesWindow.Dim;
            Show(_message, "message", "Saving the takes...");
            Show(_keys, "keys", "");
            UpdateFooter();

            bool stopHere = _recordingUp && !_stopping;
            if (stopHere) _stopWall = _clock.Elapsed.TotalSeconds;   // before _stopping, long before _stopDone
            _stopping = true;
            if (stopHere)
            {
                StopContinuous(error => _cuts.ContinueWith(c => Ui.Post(() => OnFinished(error)), TaskScheduler.Default));
                return;
            }

            // The recording was already stopped - by the service (a capture fault) or by OnContinuousLost:
            // wait for the service to be done writing before the cuts read the file as final.
            Ui.Run(() =>
            {
                var wait = Stopwatch.StartNew();
                while (_svc.Status().State != "idle" && wait.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(250);
                Log.Info($"[TakeRecordingWindow] Finish: the service is {_svc.Status().State} after {wait.Elapsed.TotalSeconds:F1}s");
                _stopDone = true;
            }).ContinueWith(t => _cuts.ContinueWith(c => Ui.Post(() => OnFinished(null)), TaskScheduler.Default), TaskScheduler.Default);
        }

        private void OnFinished(Exception? stopError)
        {
            Log.Info($"[TakeRecordingWindow] OnFinished: {_saved} attempt(s) saved, {_cutFailures.Count} cut(s) failed, "
                     + $"continuous recording {_sessionFile ?? "(none)"}");
            if (stopError == null && _cutFailures.Count == 0) { CloseNow(); return; }

            _phase = Phase.Finished;
            var lines = new List<string>();
            if (stopError != null) lines.Add("Stopping the recording FAILED: " + stopError.Message);
            lines.AddRange(_cutFailures.Select(f => "Cutting " + f));
            if (_sessionFile != null) lines.Add("Everything said is in the continuous recording: " + _sessionFile);
            _message.Foreground = TakesWindow.Red;
            Show(_message, "message", string.Join("\n", lines));
            Show(_keys, "keys", "Esc close");
            UpdateFooter();
        }

        // ---- keys -------------------------------------------------------------------------------

        private void OnKey(object sender, KeyEventArgs e)
        {
            // Only the keys this screen uses are taken; anything else (Alt+F4 included) passes on.
            bool ours = e.Key is Key.Space or Key.R or Key.Escape;
            double? file = _fileClock.FileSeconds(_clock.Elapsed.TotalSeconds);
            Log.Info($"[TakeRecordingWindow] key {e.Key} in {_phase}: take {_index + 1}/{_takes.Count} id={Current.Id} attempt={_attempt} "
                     + $"recording={(_recordingUp && !_stopping ? "on" : "off")} file time={(file?.ToString("F2", CultureInfo.InvariantCulture) ?? "-")}s "
                     + $"text=\"{OneLine(_line.Text)}\" instruction=\"{OneLine(_instruction.Text)}\"{(ours ? "" : " (not used here)")}");
            if (!ours) return;
            e.Handled = true;
            if (_finishRequested) return;   // finishing as soon as the camera is up - keys wait

            switch (_phase)
            {
                case Phase.Starting:
                    // Round 4 (review D2): no mark before the camera is up - the screen used to say
                    // "Recording" over three seconds that were never recorded.
                    if (e.Key == Key.Escape) Finish();
                    else Log.Info($"[TakeRecordingWindow] key {e.Key} ignored: the camera is still starting");
                    break;
                case Phase.Ready:
                    if (e.Key == Key.Space) _ = BeginAttempt();
                    else if (e.Key == Key.Escape) Finish();
                    break;
                case Phase.Recording:
                    if (e.Key == Key.Space)
                    {
                        EndAttempt("Space");
                        string marked = $"Take {_attemptTakeNumber} - attempt {_attempt} marked - saving it.";
                        if (_index + 1 < _takes.Count)
                        {
                            _index++;
                            ShowReady(marked);
                        }
                        else
                        {
                            // Round 4 (review I1): the last take does not end the session by itself.
                            ShowReady(marked + "\n" + LastTakeMessage);
                        }
                    }
                    else if (e.Key == Key.R)
                    {
                        EndAttempt("R - retake");
                        if (BeginAttempt()) Show(_message, "message", "Again - the last attempt is kept too.");
                        else ShowReady(_message.Text);
                    }
                    else if (e.Key == Key.Escape)
                    {
                        EndAttempt("Esc");
                        Finish();
                    }
                    break;
                case Phase.Failed:
                    if (e.Key == Key.Escape) Finish();
                    else if (e.Key == Key.Space && !_recordingUp && !_stopping) StartContinuous();
                    break;
                case Phase.Finished:
                    if (e.Key == Key.Escape) CloseNow();
                    break;
                default:
                    break; // Finishing: saving - keys wait.
            }
        }

        // ---- closing ----------------------------------------------------------------------------

        private void CloseNow()
        {
            Log.Info($"[TakeRecordingWindow] CloseNow: {_saved} attempt(s) saved");
            _allowClose = true;
            Close();
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) { _tick.Stop(); return; }

            // Alt+F4 or the taskbar: never walk away from a live recording - finish it the Esc way.
            e.Cancel = true;
            Log.Info($"[TakeRecordingWindow] OnClosing: close asked in {_phase} - finishing first");
            switch (_phase)
            {
                case Phase.Recording:
                    EndAttempt("window closed");
                    Finish();
                    break;
                case Phase.Starting:
                case Phase.Ready:
                    Finish();
                    break;
                case Phase.Failed:
                    if (_recordingUp || _stopping) { Finish(); break; }
                    // The camera never started: nothing to save, let THIS close go ahead. Calling
                    // Close() again from inside Closing throws in WPF.
                    e.Cancel = false;
                    _allowClose = true;
                    _tick.Stop();
                    Log.Info("[TakeRecordingWindow] OnClosing: closing, nothing was recording");
                    break;
                case Phase.Finished:
                    e.Cancel = false;
                    _allowClose = true;
                    _tick.Stop();
                    break;
                default:
                    break; // Finishing: it closes itself when the cuts are done.
            }
        }
    }
}
