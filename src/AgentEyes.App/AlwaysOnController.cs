using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes;
using AgentEyes.AlwaysOn;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.App
{
    /// <summary>
    /// The app's side of always-on recording (issue #66): turns the Always On page's settings and the
    /// chosen recording setup into an engine run, keeps it paused while a normal recording runs
    /// (decision 3), and remembers that it was on so it comes back when AgentEyes starts.
    ///
    /// One instance, owned by App, shared by the page, the tray and the Control API - so all three
    /// always show and change the same thing.
    ///
    /// Start and Stop talk to ffmpeg and can take seconds; they run on a background task and the
    /// callers show feedback immediately. <see cref="Changed"/> fires on a background thread; UI
    /// listeners marshal it.
    /// </summary>
    internal sealed class AlwaysOnController : IDisposable
    {
        /// <summary>How often the pause/resume reconcile looks at the normal recorder.</summary>
        public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);

        public const string PausedForRecording = "a normal recording is running";

        /// <summary>The reason shown for a pause the user asked for.</summary>
        public const string PausedByHand = "paused by you";

        private readonly RecordingService _svc;
        private readonly Config _cfg;
        private readonly AlwaysOnEngine _engine;
        private readonly Timer _reconcile;
        private readonly SemaphoreSlim _ops = new(1, 1);
        private volatile bool _busy;
        private volatile string? _busyText;
        private volatile string? _lastStartError;

        public event Action? Changed;

        public AlwaysOnController(RecordingService svc, Config cfg) : this(svc, cfg, new AlwaysOnEngine()) { }

        internal AlwaysOnController(RecordingService svc, Config cfg, AlwaysOnEngine engine)
        {
            _svc = svc ?? throw new ArgumentNullException(nameof(svc));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _engine.Changed += RaiseChanged;
            _reconcile = new Timer(_ => Reconcile(), null, ReconcileInterval, ReconcileInterval);
        }

        public AlwaysOnStatus Status() => _engine.Status();
        public string State => _engine.State;
        public bool IsOn => _engine.IsOn;

        /// <summary>True while a start or stop is in progress (the page shows it).</summary>
        public bool Busy => _busy;
        public string? BusyText => _busyText;

        /// <summary>Why the last start failed, or null. Cleared by the next start.</summary>
        public string? LastStartError => _lastStartError;

        public string ClipsFolder => string.IsNullOrWhiteSpace(_cfg.AlwaysOnClipsFolder)
            ? AlwaysOnOptions.DefaultClipsFolder
            : _cfg.AlwaysOnClipsFolder!;

        public string TodaySummary() => _engine.TodaySummary();

        /// <summary>The recording setups always-on can use: the ones that record the screen.</summary>
        public static List<CapturePreset> VideoPresets() => PresetStore.Load().Where(p => p.Mode == "video").ToList();

        /// <summary>The setup always-on will record. When one was chosen, exactly that one - null when it
        /// no longer exists, never a different setup in its place. When none was chosen, the last used
        /// video setup, else the first. Null when there is none.</summary>
        public CapturePreset? ChosenPreset()
        {
            var presets = VideoPresets();
            if (!string.IsNullOrEmpty(_cfg.AlwaysOnPresetId))
                return presets.FirstOrDefault(p => p.Id == _cfg.AlwaysOnPresetId);
            return presets.FirstOrDefault(p => p.Id == _cfg.LastUsedPresetId)
                   ?? presets.FirstOrDefault();
        }

        /// <summary>Switch always-on on and remember it. Throws with the reason when it cannot start.
        /// A start the user asks for clears a hand pause.</summary>
        public Task StartAsync(string why) => StartCoreAsync(why, restoring: false);

        private Task StartCoreAsync(string why, bool restoring) => Task.Run(async () =>
        {
            await _ops.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_engine.IsOn) return;
                SetBusy("Starting...");
                Log.Info($"[AlwaysOnController] StartAsync: {why}");
                _lastStartError = null;
                try
                {
                    var options = BuildOptions();
                    bool handPaused = restoring && _cfg.AlwaysOnHandPaused;
                    _engine.Start(options, handPaused ? PausedByHand : _svc.IsRecording ? PausedForRecording : null);
                }
                catch (Exception ex)
                {
                    _lastStartError = ex.Message;
                    Log.Error($"[AlwaysOnController] StartAsync FAILED ({why})", ex);
                    throw;
                }
                _cfg.AlwaysOnEnabled = true;
                if (!restoring) _cfg.AlwaysOnHandPaused = false;
                _cfg.Save();
            }
            finally
            {
                SetBusy(null);
                _ops.Release();
            }
        });

        /// <summary>Switch always-on off and remember it: it will not come back at the next start.</summary>
        public Task StopAsync(string why) => Task.Run(async () =>
        {
            await _ops.WaitAsync().ConfigureAwait(false);
            try
            {
                Log.Info($"[AlwaysOnController] StopAsync: {why}");
                SetBusy("Stopping - writing the clip...");
                _engine.Stop();
                _cfg.AlwaysOnEnabled = false;
                _cfg.AlwaysOnHandPaused = false;
                _cfg.Save();
            }
            finally
            {
                SetBusy(null);
                _ops.Release();
            }
        });

        /// <summary>Pause always-on. A hand pause (any reason but <see cref="PausedForRecording"/>) is
        /// remembered in config.json, so a restart brings always-on back paused; the reconcile never
        /// resumes it.</summary>
        public Task PauseAsync(string reason) => Task.Run(async () =>
        {
            await _ops.WaitAsync().ConfigureAwait(false);
            try
            {
                Log.Info($"[AlwaysOnController] PauseAsync: {reason}");
                _engine.Pause(reason);
                // Only when the engine really took THIS reason: a hand pause that landed on a pause for
                // a recording is a no-op there, and saving the flag would contradict what the engine does.
                if (reason != PausedForRecording && _engine.Status().PausedReason == reason && !_cfg.AlwaysOnHandPaused)
                {
                    _cfg.AlwaysOnHandPaused = true;
                    _cfg.Save();
                }
            }
            finally { _ops.Release(); }
        });

        public Task ResumeAsync() => Task.Run(async () =>
        {
            await _ops.WaitAsync().ConfigureAwait(false);
            try
            {
                Log.Info("[AlwaysOnController] ResumeAsync");
                if (_cfg.AlwaysOnHandPaused)
                {
                    _cfg.AlwaysOnHandPaused = false;
                    _cfg.Save();
                }
                if (_svc.IsRecording)
                {
                    Log.Info("[AlwaysOnController] ResumeAsync: a normal recording is still running; staying paused until it ends");
                    // Swap the hand reason for the recording reason, so the end of the recording resumes it.
                    _engine.Resume();
                    _engine.Pause(PausedForRecording);
                    return;
                }
                _engine.Resume();
            }
            finally { _ops.Release(); }
        });

        /// <summary>At app start: bring always-on back when it was on (it survives restarts).</summary>
        public void RestoreOnStartup()
        {
            if (!_cfg.AlwaysOnEnabled)
            {
                Log.Info("[AlwaysOnController] RestoreOnStartup: always-on was off");
                return;
            }
            Log.Info("[AlwaysOnController] RestoreOnStartup: always-on was on - starting it again"
                     + (_cfg.AlwaysOnHandPaused ? " (paused, as the user left it)" : ""));
            _ = StartCoreAsync("restored at app start", restoring: true).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error("[AlwaysOnController] RestoreOnStartup: always-on could not start again", t.Exception);
                RaiseChanged();
            }, TaskScheduler.Default);
        }

        /// <summary>At app exit: finish the current piece and write what was being kept. It stays
        /// "enabled" in the config, so the next start brings it back.</summary>
        public void ShutdownForExit()
        {
            if (!_engine.IsOn) return;
            Log.Info("[AlwaysOnController] ShutdownForExit: stopping always-on for exit (it will come back at the next start)");
            _reconcile.Change(Timeout.Infinite, Timeout.Infinite);
            _engine.Stop();
        }

        /// <summary>
        /// Decision 3: a normal recording pauses always-on, and its end resumes it. Read from the
        /// recorder's own state once a second rather than from its events - the stop event does not
        /// fire when a stop fails, and always-on must not stay paused forever because of that.
        /// </summary>
        private void Reconcile()
        {
            // Timer callback: an entry point.
            try
            {
                if (_busy) return;
                var status = _engine.Status();
                switch (ReconcileAction(_engine.IsOn, _engine.State, status.PausedReason, _svc.IsRecording))
                {
                    case ReconcileStep.Pause:
                        Log.Info("[AlwaysOnController] Reconcile: a normal recording started - pausing always-on");
                        _ = PauseAsync(PausedForRecording);
                        break;
                    case ReconcileStep.Resume:
                        Log.Info("[AlwaysOnController] Reconcile: the normal recording ended - resuming always-on");
                        _ = ResumeForRecordingEndedAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[AlwaysOnController] Reconcile failed", ex);
            }
        }

        internal enum ReconcileStep { None, Pause, Resume }

        /// <summary>
        /// The reconcile's decision, pure so it is tested: pause when a normal recording runs and
        /// always-on is not already paused; resume ONLY a pause that was for a recording, once it has
        /// ended. A hand pause is never resumed here.
        /// </summary>
        internal static ReconcileStep ReconcileAction(bool isOn, string state, string? pausedReason, bool recording)
        {
            if (!isOn) return ReconcileStep.None;
            if (recording && state != AlwaysOnState.Paused) return ReconcileStep.Pause;
            if (!recording && state == AlwaysOnState.Paused && pausedReason == PausedForRecording) return ReconcileStep.Resume;
            return ReconcileStep.None;
        }

        /// <summary>The reconcile's resume: unlike the tray's Resume, it never touches a hand pause -
        /// it resumes only while always-on is still paused for the recording.</summary>
        private Task ResumeForRecordingEndedAsync() => Task.Run(async () =>
        {
            await _ops.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_engine.Status().PausedReason == PausedForRecording && !_svc.IsRecording) _engine.Resume();
            }
            finally { _ops.Release(); }
        });

        /// <summary>The engine options for the saved settings and the chosen setup.</summary>
        internal AlwaysOnOptions BuildOptions()
        {
            if (!string.IsNullOrEmpty(_cfg.AlwaysOnPresetId) && ChosenPreset() == null)
                throw new UsageException("the recording setup chosen for always-on no longer exists. Pick another one on the Always On page.");
            var preset = ChosenPreset()
                ?? throw new UsageException("there is no recording setup that records the screen. Create a video setup on the Record page first.");
            return BuildOptions(_cfg, preset, ClipsFolder);
        }

        /// <summary>Pure mapping from settings and a setup to engine options (devices resolved here).</summary>
        internal static AlwaysOnOptions BuildOptions(Config cfg, CapturePreset preset, string clipsFolder)
        {
            var mon = Monitors.Require(preset.MonitorIndex);
            var capture = preset.UseRegion && preset.Region is { Length: 4 } r
                ? new System.Drawing.Rectangle(r[0], r[1], r[2], r[3])
                : mon.Bounds;

            var src = RecordingService.ParseSource(preset.Source);
            bool recordMic = src is AudioSourceKind.Mic or AudioSourceKind.Mixed;
            bool recordSystem = src is AudioSourceKind.Mixed or AudioSourceKind.System;
            var counts = ParseCounts(cfg.AlwaysOnCounts);

            // Mic = null on a setup means Windows' default microphone, resolved now - the same rule a
            // normal recording follows.
            string? micName = string.IsNullOrWhiteSpace(preset.Mic) ? null : preset.Mic;
            if (micName == null && (recordMic || counts is SoundSource.Mic or SoundSource.Both))
                micName = DefaultMic.FriendlyName();

            string? dshowMic = recordMic
                ? DeviceResolver.ResolveName(FfmpegDevices.ListAudio(), micName!)
                : null;

            return new AlwaysOnOptions
            {
                SetupName = preset.Name,
                Capture = capture,
                Desktop = Monitors.VirtualBounds(),
                DshowMic = dshowMic,
                MicLevelDevice = counts is SoundSource.Mic or SoundSource.Both ? micName : null,
                RecordSystem = recordSystem,
                MicGain = preset.MicVol / 100.0,
                SystemGain = preset.SysVol / 100.0,
                Counts = counts,
                ThresholdDb = cfg.AlwaysOnThresholdDb,
                KeepBefore = TimeSpan.FromMinutes(Math.Max(0, cfg.AlwaysOnBeforeMinutes)),
                KeepAfter = TimeSpan.FromMinutes(Math.Max(0, cfg.AlwaysOnAfterMinutes)),
                CapBytes = cfg.AlwaysOnCapGb <= 0 ? 0 : (long)(cfg.AlwaysOnCapGb * 1024 * 1024 * 1024),
                ClipsFolder = clipsFolder,
            };
        }

        internal static SoundSource ParseCounts(string? s) => (s ?? "mic").ToLowerInvariant() switch
        {
            "system" => SoundSource.System,
            "both" => SoundSource.Both,
            _ => SoundSource.Mic,
        };

        /// <summary>Open the clips folder in Explorer, creating it when it does not exist yet.</summary>
        public void OpenClipsFolder()
        {
            string folder = ClipsFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }

        /// <summary>
        /// Open a clip's folder in Explorer with the clip selected (issue #70). Throws with the reason
        /// when the clip is no longer there - the disk cap deleted it, or it was moved - rather than
        /// opening some other folder in its place.
        /// </summary>
        public void RevealClip(string path)
        {
            Log.Info($"[AlwaysOnController] RevealClip: {path}");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("no clip path given", nameof(path));
            if (!File.Exists(path))
            {
                Log.Warn($"[AlwaysOnController] RevealClip: {path} is not there any more");
                throw new UsageException($"{Path.GetFileName(path)} is no longer in {Path.GetDirectoryName(path)} - "
                                         + "the disk cap may have deleted it, or it was moved.");
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            Log.Info($"[AlwaysOnController] RevealClip: opened {Path.GetDirectoryName(path)} with {Path.GetFileName(path)} selected");
        }

        private void SetBusy(string? text)
        {
            _busy = text != null;
            _busyText = text;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("[AlwaysOnController] a Changed listener failed", ex); }
        }

        public void Dispose()
        {
            _reconcile.Dispose();
            _engine.Changed -= RaiseChanged;
            _engine.Dispose();
            _ops.Dispose();
        }
    }
}
