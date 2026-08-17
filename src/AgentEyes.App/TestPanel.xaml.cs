using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Packaging;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes.App
{
    /// <summary>
    /// In-app test panel. The app gives the instructions and shows the numbers; the user controls all
    /// timing by clicking Start/Stop. The agent is never in the loop during a test. Automatic tests run
    /// instantly; guided tests walk the user through a recording and end with a playable artifact.
    /// </summary>
    public partial class TestPanel : Window
    {
        private readonly RecordingService _svc;
        private readonly ObservableCollection<TestRow> _tests = new();
        private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(50) };

        private TestRow? _current;

        // Current tuning (feeds the next capture; tunable tests expose these as sliders).
        private double _micVol = 100, _sysVol = 70;
        private bool _gate = true;

        private TestReportEntry? _lastEntry;

        internal TestPanel(RecordingService svc)
        {
            _svc = svc;
            InitializeComponent();
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            TestList.ItemsSource = _tests;
            _meter.Tick += OnMeterTick;
            BuildCatalog();
        }

        // ---- catalog ------------------------------------------------------

        private void BuildCatalog()
        {
            _tests.Clear();
            _tests.Add(new TestRow("devices", "Devices", "Monitors and microphones the app can see.", TestKind.Devices));
            _tests.Add(new TestRow("screenshot", "Screenshot", "Full-monitor and region capture.", TestKind.Screenshot));
            _tests.Add(new TestRow("selftest", "Injected self-test",
                "Full pipeline with injected tones + speech (12 checks, ~1 min).", TestKind.SelfTest));

            // One guided mic-check per microphone - this is the test that catches a dead/quiet mic.
            foreach (var (_, name) in AudioCapture.Devices())
                _tests.Add(new TestRow("mic:" + name, "Mic check - " + name,
                    "Confirm this mic captures your voice.", TestKind.MicCheck) { Mic = name });

            _tests.Add(new TestRow("vm-speakers", "Voice + music (speakers)",
                "Narration over program audio on speakers.", TestKind.VoiceMusicSpeakers) { Tunable = true });
            _tests.Add(new TestRow("vm-headphones", "Voice + music (headphones)",
                "Clean baseline with no speaker bleed.", TestKind.VoiceMusicHeadphones) { Tunable = true });
            _tests.Add(new TestRow("walkthrough", "Screen walkthrough",
                "Record the screen with narration, then replay.", TestKind.ScreenWalkthrough));
        }

        // ---- list interaction --------------------------------------------

        private void TestList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_svc.IsRecording) return;                       // do not disturb an in-progress take
            if (TestList.SelectedItem is TestRow row) Preview(row);
        }

        private void RunTest_Click(object sender, RoutedEventArgs e)
        {
            if (_svc.IsRecording) { Busy("A recording is already in progress - finish it first."); return; }
            if ((sender as FrameworkElement)?.Tag is not string id) return;
            var row = _tests.FirstOrDefault(t => t.Id == id);
            if (row == null) return;
            _current = row;

            if (row.IsAutomatic) _ = RunAutomatic(row);
            else ShowInstruction(row);
        }

        // ---- instruction card --------------------------------------------

        private void Preview(TestRow row)
        {
            _current = row;
            ShowInstruction(row);
        }

        private void ShowInstruction(TestRow row)
        {
            ApplyDefaultTuning(row);
            InstrTitle.Text = row.Name;
            InstrWhat.Text = WhatText(row);
            InstrSteps.Text = StepsText(row);
            StartButton.Visibility = row.IsAutomatic ? Visibility.Collapsed : Visibility.Visible;
            if (row.IsAutomatic)
                InstrSteps.Text = StepsText(row) + "\n\nClick Run on the test to execute.";
            ShowCard(InstructionCard);
        }

        // ---- guided capture ----------------------------------------------

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _svc.IsRecording) return;
            var row = _current;
            StartButton.IsEnabled = false;
            Busy("Starting...");
            string? error = null;
            try
            {
                int screen = PrimaryScreen();
                var opts = CurrentOpts();
                await Task.Run(() =>
                {
                    switch (row.Kind)
                    {
                        case TestKind.MicCheck:
                            _svc.StartAudio(screen, AudioSourceKind.Mic, row.Mic, opts); break;
                        case TestKind.VoiceMusicSpeakers:
                        case TestKind.VoiceMusicHeadphones:
                            _svc.StartAudio(screen, AudioSourceKind.Mixed, FirstMic(), opts); break;
                        case TestKind.ScreenWalkthrough:
                            _svc.StartVideo(screen, AudioSourceKind.Mixed, FirstMic(), null, opts, 30); break;
                    }
                });
            }
            catch (Exception ex) { error = ex.Message; }

            StartButton.IsEnabled = true;
            if (error != null) { Busy("Could not start: " + error); return; }

            ResetMeters();
            ShowCard(RecordingCard);
            _meter.Start();
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (!_svc.IsRecording || _current == null) return;
            var row = _current;
            _meter.Stop();
            StopButton.IsEnabled = false;
            Busy("Finishing the recording...");

            RecordResult? result = null;
            string? error = null;
            // A guided take exists only to be MEASURED by this panel, which runs its own analysis
            // over the raw files below - so it deliberately skips the post-recording sequence.
            // Issue #151: skipping is a NAMED operation that says why and logs it, never a bare
            // stop that skips the pipeline by omission.
            try
            {
                result = await Task.Run(() => RecordingStop.StopWithoutPostProcessing(
                    _svc, "guided test take - the Tests panel analyzes the raw files itself"));
            }
            catch (Exception ex)
            {
                // Issue #153: the catch that stops the exception writes the log entry, so the
                // "(logged)" the user is shown below is a fact rather than a claim. A failed stop
                // names the recording directory it lost.
                string dir = (ex as RecordingStopFailedException)?.Dir ?? "(unknown)";
                Log.Error($"[TestPanel] stopping the guided take FAILED: dir={dir}", ex);
                error = ex.Message;
            }
            StopButton.IsEnabled = true;

            if (error != null || result == null) { Busy("Stop error (logged): " + error); return; }

            Busy("Checking the recording" +
                 (row.WantsTranscript ? " and transcribing (first run downloads the speech model)..." : "..."));
            Outcome outcome;
            try { outcome = await Task.Run(() => ComputeGuided(row, result)); }
            catch (Exception ex)
            {
                // Issue #153: no catch on this path may report to the UI and nowhere else - the panel
                // exists to diagnose recordings, and an analysis that failed with no log entry is the
                // one thing it cannot diagnose.
                Log.Error($"[TestPanel] analyzing the guided take FAILED: dir={result.Dir}", ex);
                Busy("Could not analyze the recording: " + ex.Message);
                return;
            }

            Display(row, outcome);
        }

        private void RunAgain_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _svc.IsRecording) return;
            Start_Click(sender, e);                              // re-capture with the current slider values
        }

        // ---- automatic tests ---------------------------------------------

        private async Task RunAutomatic(TestRow row)
        {
            ApplyDefaultTuning(row);
            Busy(row.Kind == TestKind.SelfTest
                ? "Running the injected self-test (about a minute - it injects its own audio)..."
                : "Running " + row.Name + "...");
            Outcome outcome;
            try { outcome = await Task.Run(() => ComputeAutomatic(row)); }
            catch (Exception ex) { Busy(row.Name + " error: " + ex.Message); return; }
            Display(row, outcome);
        }

        private Outcome ComputeAutomatic(TestRow row) => row.Kind switch
        {
            TestKind.Devices => DevicesOutcome(),
            TestKind.Screenshot => ScreenshotOutcome(),
            TestKind.SelfTest => SelfTestOutcome(),
            _ => new Outcome(),
        };

        private static Outcome DevicesOutcome()
        {
            var o = new Outcome();
            var mons = Monitors.All();
            bool m = mons.Count >= 1;
            o.Checks.Add(new("Monitors", m, m
                ? string.Join(", ", mons.Select(x => $"#{x.Index} {x.Width}x{x.Height}{(x.Primary ? " (primary)" : "")}"))
                : "none detected"));
            var mics = AudioCapture.Devices();
            bool a = mics.Length >= 1;
            o.Checks.Add(new("Microphones", a, a ? string.Join(", ", mics.Select(d => d.Name)) : "none detected"));
            o.Pass = m && a;
            return o;
        }

        private Outcome ScreenshotOutcome()
        {
            var o = new Outcome();
            int screen = PrimaryScreen();
            var mon = Monitors.All().FirstOrDefault(x => x.Primary) ?? Monitors.Require(screen);

            string full = _svc.Screenshot(screen, null);
            var (fw, fh) = Dimensions(full);
            bool fullOk = fw == mon.Width && fh == mon.Height;
            o.Checks.Add(new("Full monitor", fullOk, $"{fw}x{fh}"));

            int[] region = { mon.Bounds.X + 40, mon.Bounds.Y + 40, 320, 200 };
            string reg = _svc.Screenshot(screen, region);
            var (rw, rh) = Dimensions(reg);
            bool regOk = rw == 320 && rh == 200;
            o.Checks.Add(new("Region", regOk, $"{rw}x{rh}"));

            o.Pass = fullOk && regOk;
            o.ArtifactFile = full;
            o.ArtifactDir = Path.GetDirectoryName(Path.GetDirectoryName(full));
            return o;
        }

        private static Outcome SelfTestOutcome()
        {
            var (work, results) = SelfTest.RunChecks();
            var o = new Outcome();
            foreach (var r in results) o.Checks.Add(new(r.Name, r.Pass, r.Detail));
            o.Pass = results.All(r => r.Pass);
            o.ArtifactDir = work;
            string report = Path.Combine(work, "selftest-report.html");
            if (File.Exists(report)) o.ArtifactFile = report;
            return o;
        }

        // ---- guided outcome ----------------------------------------------

        private Outcome ComputeGuided(TestRow row, RecordResult result)
        {
            var o = new Outcome { ArtifactFile = result.File, ArtifactDir = result.Dir };
            string? file = result.File;

            bool durOk = result.DurationSeconds >= 1.0;
            o.Checks.Add(new("Duration", durOk, $"{result.DurationSeconds:F1}s"));
            bool pass = durOk;

            if (file != null && File.Exists(file))
            {
                if (row.Kind == TestKind.ScreenWalkthrough)
                {
                    var (v, a) = MediaProbe.Streams(file);
                    o.Checks.Add(new("Video stream", v, v ? "present" : "missing"));
                    o.Checks.Add(new("Audio stream", a, a ? "present" : "missing"));
                    pass = pass && v && a;
                }
                else
                {
                    double db = MediaProbe.MeanVolumeDb(file);
                    bool heard = db > -40;
                    o.Checks.Add(new("Voice captured", heard,
                        heard ? $"{db:F1} dB" : $"{db:F1} dB  (too quiet - is the right mic selected and unmuted?)"));
                    pass = pass && heard;
                }
            }
            else
            {
                o.Checks.Add(new("Artifact", false, "no output file produced"));
                pass = false;
            }

            if (row.WantsTranscript && file != null && File.Exists(file))
            {
                o.Transcript = Transcribe(file, result.Dir);
                if (row.Kind == TestKind.MicCheck)
                {
                    bool words = !string.IsNullOrWhiteSpace(o.Transcript) && o.Transcript.Any(char.IsLetter);
                    o.Checks.Add(new("Transcribed speech", words, words ? "words recognized" : "nothing recognized"));
                    pass = pass && words;
                }
            }

            o.Pass = pass;
            return o;
        }

        private static string Transcribe(string mediaFile, string dir)
        {
            string tmp = Path.Combine(dir, "_transcribe16.wav");
            try
            {
                Ffmpeg.Run(FfmpegArgs.ExtractWav(mediaFile, tmp), "transcribe downmix");
                // Transcription runs 100% through DevThrottle (issue #87): needs a signed-in account.
                var segs = Transcriber.TranscribeWavAsync(tmp).GetAwaiter().GetResult();
                string text = string.Join(" ", segs.Select(s => s.Text)).Trim();
                return string.IsNullOrWhiteSpace(text) ? "(no speech recognized)" : text;
            }
            catch (Exception ex) { return "(transcription failed: " + ex.Message + ")"; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ---- result rendering --------------------------------------------

        private void Display(TestRow row, Outcome o)
        {
            ResultTitle.Text = row.Name;
            ResultSummary.Text = o.Pass ? "PASS" : "FAIL";
            ResultSummary.Foreground = o.Pass ? OkBrush : FailBrush;

            var rows = new ObservableCollection<CheckRow>();
            foreach (var c in o.Checks)
                rows.Add(new CheckRow
                {
                    Name = c.Name,
                    Status = c.Pass ? "PASS" : "FAIL",
                    Detail = c.Detail,
                    StatusBrush = c.Pass ? OkBrush : FailBrush,
                });
            ChecksList.ItemsSource = rows;

            if (o.Transcript != null) { TranscriptText.Text = o.Transcript; TranscriptPanel.Visibility = Visibility.Visible; }
            else TranscriptPanel.Visibility = Visibility.Collapsed;

            if (row.Tunable)
            {
                GateCheck.IsChecked = _gate;
                MicVol.Value = _micVol; SysVol.Value = _sysVol;
                MicVolText.Text = $"{_micVol:F0}%"; SysVolText.Text = $"{_sysVol:F0}%";
                TuningPanel.Visibility = Visibility.Visible;
            }
            else TuningPanel.Visibility = Visibility.Collapsed;

            bool hasFile = !string.IsNullOrEmpty(o.ArtifactFile) && File.Exists(o.ArtifactFile);
            PlayButton.IsEnabled = hasFile;
            OpenButton.IsEnabled = !string.IsNullOrEmpty(o.ArtifactDir) && Directory.Exists(o.ArtifactDir);
            RunAgainButton.Visibility = row.IsAutomatic ? Visibility.Collapsed : Visibility.Visible;
            PlayButton.Tag = o.ArtifactFile;
            OpenButton.Tag = o.ArtifactDir;

            ThumbUp.IsEnabled = ThumbDown.IsEnabled = true;
            ReportStatus.Text = "";
            _lastEntry = TestReport.Append(row, o);

            ShowCard(ResultCard);
        }

        // ---- result actions ----------------------------------------------

        private void Play_Click(object sender, RoutedEventArgs e) => OpenShell((sender as FrameworkElement)?.Tag as string);

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }

        private void ThumbUp_Click(object sender, RoutedEventArgs e) => SaveThumb("up");
        private void ThumbDown_Click(object sender, RoutedEventArgs e) => SaveThumb("down");

        private void SaveThumb(string thumb)
        {
            if (_lastEntry == null) return;
            _lastEntry.Thumb = thumb;
            TestReport.Save();
            ReportStatus.Text = thumb == "up" ? "Saved: looks good." : "Saved: needs work.";
        }

        // ---- tuning -------------------------------------------------------

        private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            _micVol = MicVol.Value; _sysVol = SysVol.Value;
            MicVolText.Text = $"{_micVol:F0}%"; SysVolText.Text = $"{_sysVol:F0}%";
        }

        private void Tuning_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            _gate = GateCheck.IsChecked == true;
        }

        private void ApplyDefaultTuning(TestRow row)
        {
            switch (row.Kind)
            {
                case TestKind.VoiceMusicSpeakers: _micVol = 100; _sysVol = 70; _gate = true; break;
                case TestKind.VoiceMusicHeadphones: _micVol = 100; _sysVol = 80; _gate = false; break;
                case TestKind.MicCheck: _micVol = 100; _sysVol = 70; _gate = false; break;   // raw capture
                default: _micVol = 100; _sysVol = 70; _gate = true; break;
            }
        }

        private AudioMixOptions CurrentOpts() => new()
        {
            // The test panel isolates the gate/volume variables, so the clean-voice chain
            // (RNNoise + leveling) stays off here - real recordings enable it via presets.
            NoiseSuppression = false,
            VoiceLeveling = false,
            NoiseGate = _gate,
            MicGain = _micVol / 100.0,
            SystemGain = _sysVol / 100.0,
        };

        // ---- meters -------------------------------------------------------

        private void OnMeterTick(object? sender, EventArgs e)
        {
            if (!_svc.IsRecording) return;
            TimerText.Text = Timecodes.Clock(_svc.Elapsed);
            double mic = Math.Min(100, _svc.MicLevel * 180);
            double sys = Math.Min(100, _svc.SystemLevel * 180);
            MicMeter.Value = mic; SysMeter.Value = sys;
            MicMeterText.Text = $"{mic:F0}";
            SysMeterText.Text = $"{sys:F0}";
        }

        private void ResetMeters()
        {
            TimerText.Text = "00:00";
            MicMeter.Value = SysMeter.Value = 0;
            MicMeterText.Text = SysMeterText.Text = "--";
        }

        // ---- card visibility ---------------------------------------------

        private void ShowCard(FrameworkElement card)
        {
            IdleHint.Visibility = Visibility.Collapsed;
            BusyText.Visibility = Visibility.Collapsed;
            InstructionCard.Visibility = card == InstructionCard ? Visibility.Visible : Visibility.Collapsed;
            RecordingCard.Visibility = card == RecordingCard ? Visibility.Visible : Visibility.Collapsed;
            ResultCard.Visibility = card == ResultCard ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Busy(string message)
        {
            InstructionCard.Visibility = RecordingCard.Visibility = ResultCard.Visibility = Visibility.Collapsed;
            IdleHint.Visibility = Visibility.Collapsed;
            BusyText.Text = message;
            BusyText.Visibility = Visibility.Visible;
        }

        // ---- instruction copy --------------------------------------------

        private static string WhatText(TestRow row) => row.Kind switch
        {
            TestKind.Devices => "Lists the monitors and microphones the app can see.",
            TestKind.Screenshot => "Captures a full-monitor screenshot and a small region screenshot.",
            TestKind.SelfTest => "Runs the full injected pipeline: tones and synthetic speech through loopback, mixing, gate, video, transcription and cleanup.",
            TestKind.MicCheck => "Confirms this microphone actually captures your voice, and shows what it heard.",
            TestKind.VoiceMusicSpeakers => "Checks that your narration sits clearly over program audio when using speakers.",
            TestKind.VoiceMusicHeadphones => "Clean baseline: the same voice-over-music check with headphones, so no speaker bleed reaches the mic.",
            TestKind.ScreenWalkthrough => "Records the screen with your narration, then lets you replay the MP4.",
            _ => "",
        };

        private static string StepsText(TestRow row) => row.Kind switch
        {
            TestKind.Devices => "Passes if at least one monitor and one microphone are detected.",
            TestKind.Screenshot => "Passes if both image files are written at the expected sizes.",
            TestKind.SelfTest => "Wait for the 12 checks to finish. No user action is needed.",
            TestKind.MicCheck => "Click Start, count out loud to ten in a normal voice, then click Stop. Watch the MIC meter - it should move as you speak.",
            TestKind.VoiceMusicSpeakers => "Click Start, play music or a video out loud AND talk over it, then click Stop. Replay, and use the sliders to retune if your voice is buried.",
            TestKind.VoiceMusicHeadphones => "Put on headphones. Click Start, play music or a video in the headphones and talk over it, then click Stop.",
            TestKind.ScreenWalkthrough => "Click Start, do something on screen and narrate what you are doing, then click Stop. Then click Play to watch it back.",
            _ => "",
        };

        // ---- helpers ------------------------------------------------------

        // Bright enough to read on the dark card background.
        private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly Brush FailBrush = Freeze(Color.FromRgb(0xE5, 0x48, 0x4D));

        private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static int PrimaryScreen()
        {
            foreach (var m in Monitors.All()) if (m.Primary) return m.Index;
            return 1;
        }

        private static string? FirstMic()
        {
            var d = AudioCapture.Devices();
            return d.Length > 0 ? d[0].Name : null;
        }

        private static (int W, int H) Dimensions(string imagePath)
        {
            try { using var img = Drawing.Image.FromFile(imagePath); return (img.Width, img.Height); }
            catch { return (0, 0); }
        }

        private static void OpenShell(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    // ---- view models ------------------------------------------------------

    internal enum TestKind { Devices, Screenshot, SelfTest, MicCheck, VoiceMusicSpeakers, VoiceMusicHeadphones, ScreenWalkthrough }

    internal sealed class TestRow
    {
        private static readonly Brush AutoBadge = MakeBrush(0xD6, 0x9E, 0x2E);
        private static readonly Brush GuidedBadge = MakeBrush(0x00, 0x7A, 0xCC);

        public TestRow(string id, string name, string desc, TestKind kind)
        {
            Id = id; Name = name; Desc = desc; Kind = kind;
        }

        public string Id { get; }
        public string Name { get; }
        public string Desc { get; }
        public TestKind Kind { get; }
        public string? Mic { get; set; }
        public bool Tunable { get; set; }

        public bool IsAutomatic => Kind is TestKind.Devices or TestKind.Screenshot or TestKind.SelfTest;
        public bool WantsTranscript => Kind is TestKind.MicCheck or TestKind.VoiceMusicSpeakers or TestKind.VoiceMusicHeadphones;

        public string Badge => IsAutomatic ? "AUTO" : "GUIDED";
        public Brush BadgeBrush => IsAutomatic ? AutoBadge : GuidedBadge;

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br;
        }
    }

    internal sealed class CheckRow
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Detail { get; set; } = "";
        public Brush StatusBrush { get; set; } = Brushes.Gray;
    }

    internal readonly record struct CheckItem(string Name, bool Pass, string Detail);

    internal sealed class Outcome
    {
        public List<CheckItem> Checks { get; } = new();
        public bool Pass { get; set; }
        public string? Transcript { get; set; }
        public string? ArtifactFile { get; set; }
        public string? ArtifactDir { get; set; }
    }

    // ---- report -----------------------------------------------------------

    internal sealed class TestReportEntry
    {
        public string TimeLocal { get; set; } = "";
        public string TestId { get; set; } = "";
        public string Test { get; set; } = "";
        public string Kind { get; set; } = "";
        public bool Pass { get; set; }
        public string Summary { get; set; } = "";
        public string? ArtifactDir { get; set; }
        public string? Thumb { get; set; }
    }

    /// <summary>Small JSON report of guided/automatic test runs under recordings\_tests\report.json.</summary>
    internal static class TestReport
    {
        private static readonly string Dir = Path.Combine(RecordingPaths.Root, "_tests");
        private static readonly string File_ = Path.Combine(Dir, "report.json");
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private static List<TestReportEntry>? _entries;

        public static TestReportEntry Append(TestRow row, Outcome o)
        {
            var list = Entries();
            var entry = new TestReportEntry
            {
                TimeLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TestId = row.Id,
                Test = row.Name,
                Kind = row.Kind.ToString(),
                Pass = o.Pass,
                Summary = string.Join("; ", o.Checks.Select(c => $"{c.Name}={(c.Pass ? "PASS" : "FAIL")} ({c.Detail})")),
                ArtifactDir = o.ArtifactDir,
            };
            list.Add(entry);
            Save();
            return entry;
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.WriteAllText(File_, JsonSerializer.Serialize(Entries(), JsonOpts));
            }
            catch (Exception ex) { Log.Error("test report save", ex); }
        }

        private static List<TestReportEntry> Entries()
        {
            if (_entries != null) return _entries;
            try
            {
                if (System.IO.File.Exists(File_))
                    _entries = JsonSerializer.Deserialize<List<TestReportEntry>>(System.IO.File.ReadAllText(File_)) ?? new();
                else _entries = new();
            }
            catch { _entries = new(); }
            return _entries;
        }
    }
}
