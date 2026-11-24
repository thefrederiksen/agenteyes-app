using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentEyes.Takes;

namespace AgentEyes.App
{
    /// <summary>
    /// Issue #230, screen 2 - the RECORDING window: full screen on the chosen screen, black, the
    /// words at the very top edge in a narrow column right under the camera, a small REC timer and
    /// "take N of M - attempt K" at the bottom. Nothing to change. Keys only: Space (start / stop),
    /// R (retake), Esc (finish), and Right/Left to move the teleprompter's phrase.
    ///
    /// Read first, then look: the line shows until Space, a 3-2-1 count runs while the camera
    /// starts, the text goes away, he says it to the lens; Space stops and moves to the next take.
    /// Teleprompter: one phrase at a time with the next one dimmed below; Space starts the same
    /// count, then the phrases stay up and Right moves on.
    ///
    /// Every attempt is kept: Space and R both stop-and-keep, R just stays on the same take.
    /// </summary>
    internal sealed class TakeRecordingWindow : Window
    {
        private enum Phase { Ready, Countdown, Recording, Busy, Done }

        private readonly RecordingService _svc;
        private readonly TakeStore _store;
        private readonly List<Take> _takes;
        private readonly string _camera;
        private readonly string _mic;
        private readonly bool _teleprompter;
        private readonly MonitorInfo _monitor;

        private readonly TextBlock _line = new() { FontSize = 40, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, LineHeight = 52 };
        private readonly TextBlock _next = new() { FontSize = 32, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0), LineHeight = 42 };
        private readonly TextBlock _count = new() { FontSize = 110, FontWeight = FontWeights.Bold, Foreground = TakesWindow.Green, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        private readonly TextBlock _countHint = new() { FontSize = 24, Foreground = TakesWindow.Green, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _message = new() { FontSize = 18, Foreground = TakesWindow.Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        private readonly TextBlock _rec = new() { FontSize = 15, Foreground = TakesWindow.Red, Margin = new Thickness(0, 0, 18, 0) };
        private readonly TextBlock _where = new() { FontSize = 15, Foreground = TakesWindow.Dim };
        private readonly TextBlock _keys = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _recClock = new();

        private Phase _phase = Phase.Ready;
        private int _index;               // which take in _takes
        private int _attempt;             // the attempt number being / about to be recorded
        private int _phrase;              // teleprompter position
        private IReadOnlyList<string> _phrases = Array.Empty<string>();
        private int _countLeft;
        private bool _started;            // the recorder is running for this attempt
        private bool _startFailed;
        private bool _finishWhenStarted;  // Esc pressed while the camera was still starting
        private bool _allowClose;
        private int _saved;

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
            top.Children.Add(_next);
            top.Children.Add(_count);
            top.Children.Add(_countHint);
            top.Children.Add(_message);

            var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            bottomRow.Children.Add(_rec);
            bottomRow.Children.Add(_where);
            var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18) };
            bottom.Children.Add(bottomRow);
            bottom.Children.Add(_keys);

            var grid = new Grid();
            grid.Children.Add(top);
            grid.Children.Add(bottom);
            Content = grid;

            // The narrow column under the lens: about a third of the screen, never wider than 760.
            SizeChanged += (_, _) => top.Width = Math.Min(760, Math.Max(420, ActualWidth * 0.36));

            SourceInitialized += (_, _) => CoverMonitor();
            Loaded += (_, _) => { Activate(); Focus(); ShowReady(); };
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            _tick.Tick += (_, _) => UpdateFooter();
            _countdown.Tick += (_, _) => CountdownTick();
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

        // ---- states -----------------------------------------------------------------------------

        private Take Current => _takes[_index];

        private void ShowReady()
        {
            _phase = Phase.Ready;
            _started = false; _startFailed = false;
            _attempt = _store.NextAttemptNumber(Current.Id);
            _phrases = TakeStore.Phrases(Current.Text);
            _phrase = 0;
            Log.Info($"[TakeRecordingWindow] ShowReady: take {_index + 1}/{_takes.Count} id={Current.Id} attempt={_attempt}");
            ShowText(visible: true);
            _count.Text = ""; _countHint.Text = "";
            _message.Text = "";
            _keys.Text = _teleprompter ? "Space start - Right next phrase - Esc finish" : "Space when you have it - Esc finish";
            UpdateFooter();
        }

        private void ShowText(bool visible)
        {
            if (!visible) { _line.Text = ""; _next.Text = ""; return; }
            if (_teleprompter && _phrases.Count > 0)
            {
                _line.Text = _phrases[Math.Min(_phrase, _phrases.Count - 1)];
                _next.Text = _phrase + 1 < _phrases.Count ? _phrases[_phrase + 1] : "";
            }
            else
            {
                _line.Text = Current.Text;
                _next.Text = "";
            }
        }

        private void UpdateFooter()
        {
            string where = $"Take {_index + 1} of {_takes.Count} - attempt {_attempt}";
            switch (_phase)
            {
                case Phase.Recording:
                    _rec.Text = $"REC {_recClock.Elapsed:m\\:ss}";
                    _where.Text = where;
                    break;
                case Phase.Ready:
                    _rec.Text = "";
                    _where.Text = _teleprompter ? where : where + " - read it, then Space";
                    break;
                case Phase.Done:
                    _rec.Text = "";
                    _where.Text = $"Done - {_saved} attempt{(_saved == 1 ? "" : "s")} saved";
                    break;
                default:
                    _rec.Text = "";
                    _where.Text = where;
                    break;
            }
        }

        private void BeginCountdown()
        {
            Log.Info($"[TakeRecordingWindow] BeginCountdown: take={Current.Id} attempt={_attempt}");
            _phase = Phase.Countdown;
            _countLeft = 3;
            _count.Text = "3";
            _countHint.Text = "eyes on the camera";
            _message.Text = "";
            _keys.Text = "Esc finish";
            UpdateFooter();
            _countdown.Start();

            // The camera starts NOW, under the count, so it is surely recording when the count ends.
            // The take keeps those seconds as lead-in for the edit.
            string label = $"take-{Current.Id}-attempt-{_attempt}";
            var takeId = Current.Id;
            Ui.Run(() => _svc.StartCamera(AudioSourceKind.Mic, _mic, _camera, 30, label: label))
              .ContinueWith(t => Ui.Post(() => OnStarted(takeId, t.Exception?.GetBaseException())));
        }

        private void OnStarted(string takeId, Exception? error)
        {
            if (error != null)
            {
                Log.Error($"[TakeRecordingWindow] OnStarted: the recording did not start for take {takeId}", error);
                _countdown.Stop();
                _startFailed = true;
                if (_finishWhenStarted) { CloseNow(); return; }
                ShowReady();
                _message.Foreground = TakesWindow.Red;
                _message.Text = "The camera did not start: " + error.Message + "\nSpace to try again, Esc to finish.";
                return;
            }

            Log.Info($"[TakeRecordingWindow] OnStarted: recording take {takeId}");
            _started = true;
            _recClock.Restart();
            _tick.Start();
            if (_finishWhenStarted) { StopAttempt(then: Then.Close); return; }
            if (_countLeft <= 0) EnterRecording();
        }

        private void CountdownTick()
        {
            _countLeft--;
            if (_countLeft > 0) { _count.Text = _countLeft.ToString(); return; }
            _countdown.Stop();
            if (_started) EnterRecording();
            else if (!_startFailed)
            {
                _count.Text = "";
                _countHint.Text = "starting the camera...";
            }
        }

        private void EnterRecording()
        {
            _phase = Phase.Recording;
            _count.Text = ""; _countHint.Text = "";
            _message.Foreground = TakesWindow.Dim;
            _message.Text = "";
            // Read first: the text goes away and he says it to the lens. Teleprompter: it stays.
            ShowText(visible: _teleprompter);
            _keys.Text = _teleprompter
                ? "Space stop - R retake - Right next phrase - Esc finish"
                : "Space stop - R retake - Esc finish";
            UpdateFooter();
        }

        private enum Then { NextTake, SameTake, Close }

        private void StopAttempt(Then then)
        {
            Log.Info($"[TakeRecordingWindow] StopAttempt: take={Current.Id} attempt={_attempt} then={then}");
            _phase = Phase.Busy;
            _tick.Stop();
            _recClock.Stop();
            _count.Text = ""; _countHint.Text = "";
            ShowText(visible: false);
            _message.Foreground = TakesWindow.Dim;
            _message.Text = "Saving...";
            _keys.Text = "";
            UpdateFooter();

            var take = Current;
            Ui.Run(() =>
            {
                var stopped = RecordingStop.Keep(_svc);
                var r = stopped.Result;
                if (r.File == null) throw new InvalidOperationException($"the recording in {r.Dir} named no file");
                return _store.AddAttempt(take.Id, r.File, r.Dir, r.DurationSeconds);
            }).ContinueWith(t => Ui.Post(() => OnStopped(take, then, t.Exception?.GetBaseException(), t.IsCompletedSuccessfully ? t.Result : null)));
        }

        private void OnStopped(Take take, Then then, Exception? error, TakeAttempt? attempt)
        {
            _started = false;
            if (error != null)
            {
                Log.Error($"[TakeRecordingWindow] OnStopped: saving take {take.Id} FAILED", error);
                if (then == Then.Close) { CloseNow(); return; }
                ShowReady();
                _message.Foreground = TakesWindow.Red;
                _message.Text = "That attempt was not saved properly: " + error.Message
                                + "\nThe recording folder keeps whatever was captured. Space to record again, Esc to finish.";
                return;
            }

            _saved++;
            Log.Info($"[TakeRecordingWindow] OnStopped: take {take.Id} attempt {attempt!.Number} saved ({attempt.DurationSeconds:F1}s)");
            switch (then)
            {
                case Then.Close:
                    CloseNow();
                    return;
                case Then.SameTake:
                    ShowReady();
                    _message.Text = $"Attempt {attempt.Number} kept. Again when you are ready.";
                    return;
                default:
                    if (_index + 1 < _takes.Count)
                    {
                        _index++;
                        ShowReady();
                        _message.Text = $"Attempt {attempt.Number} of the last take kept.";
                        return;
                    }
                    _phase = Phase.Done;
                    _message.Text = "All takes recorded.\nR to retake the last one, Esc to close.";
                    _keys.Text = "R retake - Esc close";
                    UpdateFooter();
                    return;
            }
        }

        // ---- keys -------------------------------------------------------------------------------

        private void OnKey(object sender, KeyEventArgs e)
        {
            // Only the keys this screen uses are taken; anything else (Alt+F4 included) passes on.
            if (e.Key is not (Key.Space or Key.R or Key.Escape or Key.Left or Key.Right)) return;
            e.Handled = true;
            Log.Info($"[TakeRecordingWindow] key {e.Key} in {_phase}");
            switch (_phase)
            {
                case Phase.Ready:
                    if (e.Key == Key.Space) BeginCountdown();
                    else if (e.Key == Key.Escape) CloseNow();
                    else if (_teleprompter && e.Key is Key.Right or Key.Left) MovePhrase(e.Key == Key.Right ? 1 : -1);
                    break;
                case Phase.Countdown:
                    if (e.Key == Key.Escape)
                    {
                        // The camera is starting on another thread: finish once it has, keeping what it got.
                        _finishWhenStarted = true;
                        _countdown.Stop();
                        _message.Text = "Finishing...";
                        if (_started) StopAttempt(Then.Close);
                    }
                    break;
                case Phase.Recording:
                    if (e.Key == Key.Space) StopAttempt(Then.NextTake);
                    else if (e.Key == Key.R) StopAttempt(Then.SameTake);
                    else if (e.Key == Key.Escape) StopAttempt(Then.Close);
                    else if (_teleprompter && e.Key is Key.Right or Key.Left) MovePhrase(e.Key == Key.Right ? 1 : -1);
                    break;
                case Phase.Done:
                    if (e.Key == Key.Escape) CloseNow();
                    else if (e.Key == Key.R) ShowReady();
                    break;
                default:
                    break; // Busy: saving - keys wait.
            }
        }

        private void MovePhrase(int delta)
        {
            if (_phrases.Count == 0) return;
            _phrase = Math.Max(0, Math.Min(_phrases.Count - 1, _phrase + delta));
            ShowText(visible: true);
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
            if (_allowClose) { _tick.Stop(); _countdown.Stop(); return; }

            // Alt+F4 or the taskbar: never walk away from a live recording - finish it the Esc way.
            e.Cancel = true;
            Log.Info($"[TakeRecordingWindow] OnClosing: close asked in {_phase} - finishing first");
            switch (_phase)
            {
                case Phase.Recording: StopAttempt(Then.Close); break;
                case Phase.Countdown: _finishWhenStarted = true; _countdown.Stop(); if (_started) StopAttempt(Then.Close); break;
                case Phase.Busy: break;
                default: CloseNow(); break;
            }
        }
    }
}
