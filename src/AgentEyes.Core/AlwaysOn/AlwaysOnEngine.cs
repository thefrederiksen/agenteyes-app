using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.Housekeeping;
using AgentEyes.Video;

namespace AgentEyes.AlwaysOn
{
    /// <summary>What records the pieces. The real one is <see cref="ContinuousRecorder"/>; tests
    /// substitute one that writes pieces without a screen.</summary>
    internal interface IPieceRecorder : IDisposable
    {
        void Start(AlwaysOnOptions options, SoundLog sound);
        void Stop();
        bool HasExited { get; }
        string Encoder { get; }
        string StderrTail { get; }
    }

    internal sealed class ContinuousPieceRecorder : IPieceRecorder
    {
        private readonly ContinuousRecorder _rec = new();
        public void Start(AlwaysOnOptions options, SoundLog sound) => _rec.Start(options, sound);
        public void Stop() => _rec.Stop();
        public bool HasExited => _rec.HasExited;
        public string Encoder => _rec.Encoder;
        public string StderrTail => _rec.StderrTail;
        public void Dispose() => _rec.Dispose();
    }

    /// <summary>The always-on state, as the tray dot shows it.</summary>
    internal static class AlwaysOnState
    {
        public const string Off = "off";
        /// <summary>Recording, no sound lately: silence is being thrown away. Solid red dot.</summary>
        public const string Listening = "listening";
        /// <summary>Recording, and sound was heard within the after window. Red dot, white centre.</summary>
        public const string Keeping = "keeping";
        /// <summary>Paused while a normal recording runs. Grey dot.</summary>
        public const string Paused = "paused";
        /// <summary>Meant to be recording, but the capture failed and is being retried. Grey dot, and
        /// the tooltip says why - the indicator never claims a recording that is not happening.</summary>
        public const string Retrying = "retrying";
    }

    /// <summary>A snapshot of always-on for the page, the tray and the Control API.</summary>
    internal sealed class AlwaysOnStatus
    {
        public string State { get; set; } = AlwaysOnState.Off;
        public string? Setup { get; set; }
        public string? Counts { get; set; }
        public double? ThresholdDb { get; set; }
        public bool ThresholdAuto { get; set; }
        public double? KeepBeforeMinutes { get; set; }
        public double? KeepAfterMinutes { get; set; }
        public double? CapGb { get; set; }
        public string? ClipsFolder { get; set; }
        public string? Encoder { get; set; }
        public DateTime? SinceUtc { get; set; }
        public DateTime? LastSoundUtc { get; set; }
        public string? PausedReason { get; set; }
        public string? LastError { get; set; }
        public int PiecesWaiting { get; set; }
        public int PiecesKeptInOpenClip { get; set; }
        public long DiskUsedBytes { get; set; }
        public string Today { get; set; } = "";
        public int ClipsToday { get; set; }
        public double KeptSecondsToday { get; set; }
        public long KeptBytesToday { get; set; }
        public double DiscardedSecondsToday { get; set; }
        public string? LastClip { get; set; }
    }

    /// <summary>
    /// ALWAYS-ON RECORDING (issue #66). Records the chosen setup all day in one-minute pieces and
    /// keeps only the stretches with sound, plus a margin either side:
    ///
    ///  - the RECORDER (<see cref="IPieceRecorder"/>) never stops for silence; a supervisor restarts it
    ///    if ffmpeg dies;
    ///  - the SOUND LOG (<see cref="SoundLog"/>) notes every second above the line;
    ///  - the KEEPER (<see cref="KeeperRule"/>) decides each finished piece on a timer: kept pieces move
    ///    into a holding folder per clip, silent ones are deleted, and a finished clip is joined into
    ///    one MP4 with a lossless concat;
    ///  - the CAP (<see cref="HousekeepingCeiling.EvictOldest"/>) holds the clips folder under the
    ///    owner's limit, oldest clip first.
    ///
    /// CRASH SAFETY. A kept piece is MOVED into its clip's holding folder the moment it is kept, so a
    /// crash or a power cut loses no kept video: the next start joins every holding folder it finds.
    /// Loose pieces found at start have no sound log any more and are deleted, and that is logged.
    ///
    /// Every public method is serialised on one lock. Start, Stop, Pause and Resume talk to ffmpeg and
    /// can take seconds - callers keep them off the UI thread.
    /// </summary>
    internal sealed class AlwaysOnEngine : IDisposable
    {
        /// <summary>How often the keeper and the supervisor look. The keeper decides at least once a
        /// minute, as the rule asks; looking more often only makes the tray dot and counters fresher.</summary>
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

        /// <summary>The waits between restarts of a capture that keeps failing.</summary>
        public static readonly TimeSpan[] RestartBackoff =
        {
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
        };

        /// <summary>
        /// How long the capture may go without starting a new piece before it counts as hung and is
        /// restarted: two pieces and half a minute. An ffmpeg that is alive but writing nothing would
        /// otherwise look like a healthy recording forever (review finding 4).
        /// </summary>
        public static TimeSpan HungAfter(int pieceSeconds) => TimeSpan.FromSeconds(pieceSeconds * 2 + 30);

        private readonly object _lock = new();
        private readonly Func<IPieceRecorder> _recorderFactory;
        private readonly Func<DateTime> _utcNow;
        private readonly bool _ownTimer;
        private Timer? _timer;

        private AlwaysOnOptions? _options;
        private IPieceRecorder? _recorder;
        private SoundLog? _sound;
        private AlwaysOnDay _day = new();
        private volatile string _state = AlwaysOnState.Off;

        /// <summary>The last published status. Readers never take the engine lock: a keeper pass
        /// joining a clip holds it for seconds, and the tray and the page read status on the UI thread.</summary>
        private volatile AlwaysOnStatus _snapshot = new();
        private string? _pausedReason;
        private string? _lastError;
        private string? _lastClip;
        private string? _encoder;
        private DateTime? _sinceUtc;
        private int _restartAttempt;
        private DateTime _nextRestartUtc;
        private DateTime _recorderStartedUtc;

        // The keeper's memory between passes.
        private readonly Dictionary<int, string> _clipDirs = new();
        private int? _openClip;
        private DateTime? _openClipEndUtc;
        private int _nextClip = 1;
        private readonly HashSet<string> _failedJoins = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (on the engine's thread) whenever the state or the counters change.</summary>
        public event Action? Changed;

        public AlwaysOnEngine() : this(() => new ContinuousPieceRecorder(), () => DateTime.UtcNow, ownTimer: true) { }

        /// <param name="ownTimer">False for tests, which call <see cref="Tick"/> themselves.</param>
        public AlwaysOnEngine(Func<IPieceRecorder> recorderFactory, Func<DateTime> utcNow, bool ownTimer)
        {
            _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _ownTimer = ownTimer;
        }

        /// <summary>The current state. Lock-free.</summary>
        public string State => _state;

        /// <summary>True from Start until Stop - including while paused or retrying. Lock-free.</summary>
        public bool IsOn => _state != AlwaysOnState.Off;

        public AlwaysOnOptions? Options { get { lock (_lock) return _options; } }

        /// <summary>Turn always-on on. Throws with the reason when the capture cannot start; always-on
        /// is then off again, not half-on.</summary>
        /// <param name="pausedReason">Non-null to switch always-on on already PAUSED - a normal
        /// recording is running and has the screen and the microphone (decision 3). It starts
        /// capturing on <see cref="Resume"/>.</param>
        public void Start(AlwaysOnOptions options, string? pausedReason = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            lock (_lock)
            {
                if (_state != AlwaysOnState.Off) throw new UsageException("always-on is already on - stop it first.");
                Log.Info($"[AlwaysOnEngine] Start: {options}");
                if (options.KeepBefore < TimeSpan.Zero || options.KeepAfter < TimeSpan.Zero)
                    throw new UsageException("the keep windows cannot be negative.");

                Directory.CreateDirectory(options.WorkFolder);
                Directory.CreateDirectory(options.PieceFolder);
                Directory.CreateDirectory(options.PendingFolder);
                Directory.CreateDirectory(options.ClipsFolder);

                _options = options;
                // One sound log for the whole run: a capture restart must not forget what was heard
                // around the pieces the old capture wrote, or they would be decided as silence.
                _sound = new SoundLog(options.Counts, options.ThresholdDb);
                _day = AlwaysOnDay.Load(options.StatsFile);
                _day.EnsureDay(_utcNow());
                _lastError = null;
                _pausedReason = null;
                _failedJoins.Clear();
                ResetKeeper();

                Recover(options);
                EnforceCap(options);

                if (pausedReason == null)
                {
                    try
                    {
                        StartRecorder(options);
                    }
                    catch
                    {
                        _options = null;
                        _state = AlwaysOnState.Off;
                        throw;
                    }
                }
                _sinceUtc = _utcNow();
                _restartAttempt = 0;
                _state = pausedReason == null ? AlwaysOnState.Listening : AlwaysOnState.Paused;
                _pausedReason = pausedReason;
                if (_ownTimer && _timer == null)
                    _timer = new Timer(_ => TickFromTimer(), null, TickInterval, TickInterval);
            }
            lock (_lock) Publish();
            Log.Info($"[AlwaysOnEngine] Start: always-on is on{(pausedReason == null ? "" : " (paused: " + pausedReason + ")")}");
            RaiseChanged();
        }

        /// <summary>Turn always-on off: finish the current piece, decide every piece on the sound heard
        /// so far, and write every clip that was being kept.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_state == AlwaysOnState.Off) return;
                Log.Info("[AlwaysOnEngine] Stop: stopping always-on");
                _timer?.Dispose();
                _timer = null;
                try
                {
                    StopRecorderAndFinish("stop");
                }
                finally
                {
                    // Off is off even when the last keeper pass failed; what it did not decide is
                    // on disk and the next start recovers it.
                    _state = AlwaysOnState.Off;
                    _pausedReason = null;
                    _sinceUtc = null;
                    Publish();
                }
            }
            Log.Info("[AlwaysOnEngine] Stop: always-on is off");
            RaiseChanged();
        }

        /// <summary>
        /// Pause for a normal recording (decision 3): stop capturing and write what was being kept.
        /// The normal recording has the screen and the microphone now; always-on comes back when it
        /// ends. A no-op when always-on is off or already paused.
        /// </summary>
        public void Pause(string reason)
        {
            lock (_lock)
            {
                if (_state is AlwaysOnState.Off or AlwaysOnState.Paused) return;
                Log.Info($"[AlwaysOnEngine] Pause: {reason}");
                try
                {
                    StopRecorderAndFinish("pause");
                }
                finally
                {
                    _state = AlwaysOnState.Paused;
                    _pausedReason = reason;
                    Publish();
                }
            }
            RaiseChanged();
        }

        /// <summary>Resume after a pause. When the capture cannot restart, always-on stays on and
        /// retries on the supervisor's schedule, and the reason is in the status.</summary>
        public void Resume()
        {
            lock (_lock)
            {
                if (_state != AlwaysOnState.Paused) return;
                Log.Info("[AlwaysOnEngine] Resume: resuming always-on");
                _pausedReason = null;
                ResetKeeper();
                try
                {
                    StartRecorder(_options!);
                    _restartAttempt = 0;
                    _state = AlwaysOnState.Listening;
                }
                catch (Exception ex)
                {
                    Log.Error("[AlwaysOnEngine] Resume: the capture did not restart; retrying", ex);
                    _lastError = ex.Message;
                    _state = AlwaysOnState.Retrying;
                    ScheduleRestart();
                }
                Publish();
            }
            RaiseChanged();
        }

        /// <summary>One pass of the supervisor and the keeper. Called by the timer; tests call it.</summary>
        public void Tick()
        {
            bool changed;
            lock (_lock)
            {
                if (_options == null || _state is AlwaysOnState.Off or AlwaysOnState.Paused) return;
                var now = _utcNow();
                string before = _state;
                int clipsBefore = _day.Clips;
                double discardedBefore = _day.DiscardedSeconds;

                Supervise(now);
                bool running = _recorder != null && !_recorder.HasExited;
                RunKeeper(_options, now, final: false, recorderRunning: running);

                if (_state is AlwaysOnState.Listening or AlwaysOnState.Keeping)
                    _state = IsKeeping(now) ? AlwaysOnState.Keeping : AlwaysOnState.Listening;

                _day.EnsureDay(now);
                changed = before != _state || clipsBefore != _day.Clips || discardedBefore != _day.DiscardedSeconds;
                Publish();
            }
            if (changed) RaiseChanged();
        }

        /// <summary>The last published status - lock-free, safe on the UI thread.</summary>
        public AlwaysOnStatus Status() => _snapshot;

        /// <summary>Build and publish a fresh status. Caller holds the lock.</summary>
        private void Publish() => _snapshot = BuildStatus();

        private AlwaysOnStatus BuildStatus()
        {
            var now = _utcNow();
            _day.EnsureDay(now);
            var o = _options;
            var s = new AlwaysOnStatus
            {
                State = _state,
                Setup = o?.SetupName,
                Counts = o?.Counts.ToString().ToLowerInvariant(),
                ThresholdAuto = o != null && !o.ThresholdDb.HasValue,
                KeepBeforeMinutes = o?.KeepBefore.TotalMinutes,
                KeepAfterMinutes = o?.KeepAfter.TotalMinutes,
                CapGb = o == null ? null : Math.Round(o.CapBytes / 1024.0 / 1024 / 1024, 2),
                ClipsFolder = o?.ClipsFolder,
                Encoder = _encoder,
                SinceUtc = _sinceUtc,
                LastSoundUtc = _sound?.LastSoundUtc,
                PausedReason = _pausedReason,
                LastError = _lastError,
                Today = _day.Date,
                ClipsToday = _day.Clips,
                KeptSecondsToday = Math.Round(_day.KeptSeconds, 1),
                KeptBytesToday = _day.KeptBytes,
                DiscardedSecondsToday = Math.Round(_day.DiscardedSeconds, 1),
                LastClip = _lastClip,
            };
            if (_sound != null && o != null)
            {
                var src = o.Counts == SoundSource.System ? SoundSource.System : SoundSource.Mic;
                var line = _sound.CurrentThresholdDb(src);
                s.ThresholdDb = line.HasValue ? Math.Round(line.Value, 1) : null;
            }
            if (o != null)
            {
                s.PiecesWaiting = PieceFiles(o.PieceFolder).Count;
                s.PiecesKeptInOpenClip = _openClip.HasValue && _clipDirs.TryGetValue(_openClip.Value, out var d)
                    && Directory.Exists(d) ? Directory.GetFiles(d, "*.mp4").Length : 0;
                s.DiskUsedBytes = FolderBytes(o.WorkFolder) + ClipFiles(o).Sum(f => f.Length);
            }
            return s;
        }

        /// <summary>Today's counters as one line.</summary>
        public string TodaySummary()
        {
            var s = _snapshot;
            return new AlwaysOnDay
            {
                Date = s.Today, Clips = s.ClipsToday, KeptSeconds = s.KeptSecondsToday,
                KeptBytes = s.KeptBytesToday, DiscardedSeconds = s.DiscardedSecondsToday,
            }.Summary();
        }

        // ---- the recorder and its supervisor ---------------------------------

        private void StartRecorder(AlwaysOnOptions o)
        {
            var rec = _recorderFactory();
            try
            {
                rec.Start(o, _sound!);
            }
            catch
            {
                rec.Dispose();
                throw;
            }
            _recorder = rec;
            _encoder = rec.Encoder;
            _recorderStartedUtc = _utcNow();
        }

        private void Supervise(DateTime now)
        {
            if (_state == AlwaysOnState.Retrying)
            {
                if (now < _nextRestartUtc) return;
                TryRestart(now);
                return;
            }
            if (_recorder == null) return;

            string tail;
            if (_recorder.HasExited)
            {
                tail = _recorder.StderrTail;
                _lastError = "the capture stopped unexpectedly: " + LastLine(tail);
                Log.Error($"[AlwaysOnEngine] Supervise: ffmpeg exited or stalled; restarting. ffmpeg said: {tail}");
            }
            else
            {
                // Alive is not the same as recording: the newest piece must keep moving.
                var files = PieceFiles(_options!.PieceFolder);
                DateTime newest = files.Count > 0 && files[^1].StartUtc > _recorderStartedUtc ? files[^1].StartUtc : _recorderStartedUtc;
                if (now - newest <= HungAfter(_options.PieceSeconds)) return;
                tail = _recorder.StderrTail;
                _lastError = $"the capture stopped writing (no new piece since {newest.ToLocalTime():HH:mm:ss})";
                Log.Error($"[AlwaysOnEngine] Supervise: {_lastError}; restarting. ffmpeg said: {tail}");
            }
            // Stop it first - a hung ffmpeg still holds the piece it was writing - then everything it
            // wrote is finished: decide it before the new capture starts writing.
            try { _recorder.Stop(); }
            catch (Exception ex) { Log.Error("[AlwaysOnEngine] Supervise: stopping the failed capture threw", ex); }
            _recorder.Dispose();
            _recorder = null;
            try
            {
                RunKeeper(_options!, now, final: true, recorderRunning: false);
            }
            finally
            {
                // The restart never waits on the keeper (review round 2, finding 1): a keeper pass that
                // throws - a piece held open by a virus scanner - would otherwise leave always-on
                // saying it is on with no capture and nothing that ever starts one again. Pieces the
                // pass did not decide stay in the folder and the next pass decides them with the same
                // sound log; a clip it had open stays in its holding folder for the next start.
                ResetKeeper();
                TryRestart(now);
            }
        }

        private void TryRestart(DateTime now)
        {
            try
            {
                StartRecorder(_options!);
                Log.Info($"[AlwaysOnEngine] TryRestart: capture restarted (attempt {_restartAttempt + 1})");
                _restartAttempt = 0;
                _state = AlwaysOnState.Listening;
            }
            catch (Exception ex)
            {
                Log.Error($"[AlwaysOnEngine] TryRestart: restart attempt {_restartAttempt + 1} failed", ex);
                _lastError = ex.Message;
                _state = AlwaysOnState.Retrying;
                ScheduleRestart();
            }
        }

        private void ScheduleRestart()
        {
            var wait = RestartBackoff[Math.Min(_restartAttempt, RestartBackoff.Length - 1)];
            _restartAttempt++;
            _nextRestartUtc = _utcNow() + wait;
            Log.Info($"[AlwaysOnEngine] ScheduleRestart: next attempt in {wait.TotalSeconds:0}s");
        }

        private void StopRecorderAndFinish(string why)
        {
            if (_recorder != null)
            {
                try { _recorder.Stop(); }
                catch (Exception ex) { Log.Error($"[AlwaysOnEngine] {why}: stopping the capture failed", ex); }
                _recorder.Dispose();
                _recorder = null;
            }
            try
            {
                if (_options != null) RunKeeper(_options, _utcNow(), final: true, recorderRunning: false);
            }
            finally
            {
                ResetKeeper();
            }
        }

        private void ResetKeeper()
        {
            _openClip = null;
            _openClipEndUtc = null;
        }

        private bool IsKeeping(DateTime now)
        {
            if (_openClip.HasValue) return true;
            var last = _sound?.LastSoundUtc;
            return last.HasValue && _options != null && now - last.Value <= _options.KeepAfter;
        }

        // ---- the keeper ------------------------------------------------------

        private void RunKeeper(AlwaysOnOptions o, DateTime now, bool final, bool recorderRunning)
        {
            var files = PieceFiles(o.PieceFolder);
            // The newest piece is the one ffmpeg is writing - unless nothing is writing any more.
            int finishedCount = recorderRunning ? Math.Max(0, files.Count - 1) : files.Count;
            var pieces = new List<Piece>(finishedCount);
            for (int i = 0; i < finishedCount; i++)
            {
                var (path, startUtc) = files[i];
                var fi = new FileInfo(path);
                DateTime endUtc = i + 1 < files.Count ? files[i + 1].StartUtc : fi.LastWriteTimeUtc;
                if (endUtc < startUtc) endUtc = startUtc;
                pieces.Add(new Piece(path, startUtc, endUtc, fi.Length));
            }

            var sound = _sound;
            var plan = KeeperRule.Decide(
                pieces, (a, b) => sound != null && sound.AnySound(a, b), now, o.KeepBefore, o.KeepAfter,
                _openClip, _openClipEndUtc, _nextClip, final);

            foreach (var (piece, clip) in plan.Keep)
            {
                if (!_clipDirs.TryGetValue(clip, out var dir))
                {
                    dir = Path.Combine(o.PendingFolder, "clip_" + piece.StartUtc.ToString(AlwaysOnArgs.PieceStampFormat));
                    _clipDirs[clip] = dir;
                }
                Directory.CreateDirectory(dir);
                File.Move(piece.Path, Path.Combine(dir, Path.GetFileName(piece.Path)));
                Log.Info($"[AlwaysOnEngine] keeper: KEEP {Path.GetFileName(piece.Path)} ({piece.Duration.TotalSeconds:0}s) -> {Path.GetFileName(dir)}");
            }
            foreach (var piece in plan.Delete)
            {
                File.Delete(piece.Path);
                _day.EnsureDay(now);
                _day.DiscardedSeconds += piece.Duration.TotalSeconds;
                Log.Info($"[AlwaysOnEngine] keeper: DELETE {Path.GetFileName(piece.Path)} ({piece.Duration.TotalSeconds:0}s, no sound near it)");
            }
            foreach (int clip in plan.Close)
            {
                if (_clipDirs.TryGetValue(clip, out var dir))
                {
                    JoinClip(o, dir, now);
                    _clipDirs.Remove(clip);
                }
            }

            _openClip = plan.OpenClip;
            _openClipEndUtc = plan.OpenClipEndUtc;
            _nextClip = plan.NextClip;

            if (plan.Keep.Count + plan.Delete.Count + plan.Close.Count > 0) _day.Save(o.StatsFile);
            if (plan.Close.Count > 0) EnforceCap(o);

            // Sound older than anything still undecided can reach is never asked about again.
            _sound?.Prune(now - o.KeepBefore - o.KeepAfter - TimeSpan.FromSeconds(o.PieceSeconds * 3));
        }

        /// <summary>
        /// Join one clip's pieces into a single MP4 in the clips folder, without re-encoding, then
        /// remove the holding folder.
        ///
        /// NOTHING KEPT IS EVER DELETED BECAUSE SOMETHING FAILED (review finding 1). A piece that cannot
        /// be read is MOVED to the unreadable folder, never deleted, and the clip is joined from the
        /// rest; a join that fails leaves the whole holding folder in place for the next start.
        /// </summary>
        private void JoinClip(AlwaysOnOptions o, string dir, DateTime now)
        {
            if (_failedJoins.Contains(dir)) return;
            var parts = Directory.GetFiles(dir, "piece_*.mp4").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
            var readable = new List<string>();
            double seconds = 0;
            foreach (var p in parts)
            {
                try
                {
                    double d = MediaProbe.DurationSeconds(p);
                    if (d <= 0) throw new UsageException("zero duration");
                    readable.Add(p);
                    seconds += d;
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(o.UnreadableFolder);
                    string kept = Path.Combine(o.UnreadableFolder, Path.GetFileName(p));
                    File.Move(p, kept, overwrite: false);
                    _lastError = $"a kept piece could not be read and was set aside in {o.UnreadableFolder}";
                    Log.Warn($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(p)} cannot be read ({ex.Message}); "
                             + $"it is left out of the clip and kept at {kept}");
                }
            }
            if (readable.Count == 0)
            {
                Log.Warn($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(dir)} has no readable piece left; "
                         + $"its pieces are in {o.UnreadableFolder}");
                Directory.Delete(dir, recursive: true);
                return;
            }

            var start = (AlwaysOnArgs.PieceStartUtc(readable[0]) ?? now).ToLocalTime();
            string outPath = UniqueClipPath(o.ClipsFolder, start);
            string list = Path.Combine(dir, "join.txt");
            File.WriteAllText(list, AlwaysOnArgs.JoinList(readable));
            try
            {
                Ffmpeg.Run(AlwaysOnArgs.Join(list, outPath), "always-on join");
            }
            catch (UsageException ex)
            {
                _failedJoins.Add(dir);
                _lastError = $"joining the clip {Path.GetFileName(dir)} failed; its pieces are kept in {dir}";
                Log.Error($"[AlwaysOnEngine] JoinClip: {_lastError}", ex);
                if (File.Exists(outPath)) File.Delete(outPath);
                return;
            }

            var written = new FileInfo(outPath);
            long bytes = written.Length;
            // The ledger is written BEFORE the pieces go: a clip the cap may delete is a clip this
            // engine is on record as having written (review finding 3).
            File.AppendAllText(o.ClipLedger, LedgerLine(written) + Environment.NewLine);
            Directory.Delete(dir, recursive: true);
            _day.EnsureDay(now);
            _day.Clips++;
            _day.KeptSeconds += seconds;
            _day.KeptBytes += bytes;
            _lastClip = outPath;
            Log.Info($"[AlwaysOnEngine] JoinClip: wrote {outPath} ({readable.Count} pieces, {seconds:0}s, {bytes / 1024.0 / 1024:0.0} MB)");
        }

        private static string UniqueClipPath(string folder, DateTime startLocal)
        {
            string stem = startLocal.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(folder, stem + ".mp4");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{n}.mp4");
            return path;
        }

        /// <summary>
        /// At start: join every holding folder a previous run left (a crash or a power cut between
        /// keeping and joining), and delete loose pieces - their sound log died with that run, so
        /// nothing can say whether they were worth keeping.
        /// </summary>
        private void Recover(AlwaysOnOptions o)
        {
            foreach (var dir in Directory.GetDirectories(o.PendingFolder, "clip_*").OrderBy(d => d, StringComparer.Ordinal))
            {
                Log.Info($"[AlwaysOnEngine] Recover: joining {Path.GetFileName(dir)}, left by an earlier run");
                JoinClip(o, dir, _utcNow());
            }
            var loose = PieceFiles(o.PieceFolder);
            foreach (var (path, _) in loose)
            {
                Log.Warn($"[AlwaysOnEngine] Recover: deleting {Path.GetFileName(path)} - left by an earlier run that "
                         + "ended before deciding it, and its sound log went with that run");
                File.Delete(path);
            }
            _day.Save(o.StatsFile);
        }

        private void EnforceCap(AlwaysOnOptions o)
        {
            if (o.CapBytes <= 0) return;
            var clips = ClipFiles(o).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var candidates = clips.Select(f => new HousekeepingCandidate
            {
                Recording = f.FullName,
                Bytes = f.Length,
                AgeDays = (int)(_utcNow() - f.LastWriteTimeUtc).TotalDays,
                Pinned = false,
            }).ToList();
            long fixedBytes = FolderBytes(o.WorkFolder);
            var byPath = clips.ToDictionary(f => f.FullName, StringComparer.OrdinalIgnoreCase);
            var evicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in HousekeepingCeiling.EvictOldest(candidates, o.CapBytes, fixedBytes))
            {
                var proven = byPath[c.Recording];
                // Either way the name leaves the ledger: deleted, or no longer the file we wrote.
                evicted.Add(c.Recording);
                if (EvictIfUnchanged(c.Recording, proven.Length, proven.LastWriteTimeUtc.Ticks))
                    Log.Info($"[AlwaysOnEngine] EnforceCap: deleted {Path.GetFileName(c.Recording)} "
                             + $"({c.Bytes / 1024.0 / 1024:0.0} MB) - the clips were over the {o.CapBytes / 1024.0 / 1024 / 1024:0.##} GB cap");
            }
            // Rewrite the ledger to the clips that are still there and still the file that was
            // written: an evicted or replaced name stops being deletion authority at once (review
            // round 2, finding 2).
            WriteLedger(o, clips.Where(f => !evicted.Contains(f.FullName)));
        }

        /// <summary>
        /// Delete one clip ONLY if the file removed is the file the ledger proved (review round 3). The
        /// clip is first renamed, atomically, to a private name in the same folder; the check then runs
        /// on the renamed file, which nothing else can swap out from under it, and only that file is
        /// deleted. Checking the path and then deleting the path would remove whatever another program
        /// saved there in between. When the renamed file does not match, it goes back under its name
        /// (or, if that name was taken meanwhile, under a free one next to it) and nothing is deleted.
        /// </summary>
        /// <returns>True when the clip was deleted.</returns>
        internal static bool EvictIfUnchanged(string path, long bytes, long lastWriteTicksUtc)
        {
            string folder = Path.GetDirectoryName(path)!;
            string held = Path.Combine(folder, ".alwayson-evict-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Move(path, held);
            var f = new FileInfo(held);
            if (f.Length == bytes && f.LastWriteTimeUtc.Ticks == lastWriteTicksUtc)
            {
                File.Delete(held);
                return true;
            }
            string back = path;
            for (int n = 2; File.Exists(back); n++)
                back = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)}_kept{n}{Path.GetExtension(path)}");
            File.Move(held, back);
            Log.Warn($"[AlwaysOnEngine] EvictIfUnchanged: {Path.GetFileName(path)} changed since the ledger recorded it "
                     + $"({f.Length} bytes, expected {bytes}); it is not a clip this engine can prove it wrote, so it was "
                     + $"kept as {Path.GetFileName(back)} and dropped from the ledger");
            return false;
        }

        /// <summary>
        /// One ledger line: the clip's name, size and last-write time. The cap trusts a file only while
        /// all three still match, so another file later saved under an evicted clip's name is not
        /// mistaken for the clip.
        /// </summary>
        internal static string LedgerLine(FileInfo clip) =>
            $"{clip.Name}\t{clip.Length}\t{clip.LastWriteTimeUtc.Ticks}";

        private static void WriteLedger(AlwaysOnOptions o, IEnumerable<FileInfo> clips)
        {
            string tmp = o.ClipLedger + ".tmp";
            File.WriteAllLines(tmp, clips.Select(LedgerLine));
            File.Move(tmp, o.ClipLedger, overwrite: true);
        }

        // ---- files -----------------------------------------------------------

        /// <summary>The pieces in the folder, oldest first, with their start time.</summary>
        private static List<(string Path, DateTime StartUtc)> PieceFiles(string folder)
        {
            var list = new List<(string, DateTime)>();
            if (!Directory.Exists(folder)) return list;
            foreach (var f in Directory.GetFiles(folder, "piece_*.mp4"))
            {
                var start = AlwaysOnArgs.PieceStartUtc(f);
                if (start.HasValue) list.Add((f, start.Value));
            }
            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return list;
        }

        /// <summary>
        /// The clips THIS ENGINE WROTE that are still on disk, unchanged: a ledger entry whose name,
        /// size and write time all match the file. A name that merely looks like a clip is not proof -
        /// someone else's video called 2026-09-20_10-00-00.mp4 is never counted and never deleted, even
        /// when an evicted clip once had that name.
        ///
        /// Leans to keep: with the ledger lost, nothing is provably the engine's, so the cap deletes
        /// nothing and says so in the log - it never guesses from names.
        /// </summary>
        private static List<FileInfo> ClipFiles(AlwaysOnOptions o)
        {
            var list = new List<FileInfo>();
            if (!Directory.Exists(o.ClipsFolder)) return list;
            if (!File.Exists(o.ClipLedger))
            {
                if (Directory.EnumerateFiles(o.ClipsFolder, "*.mp4").Any())
                    Log.Warn($"[AlwaysOnEngine] ClipFiles: no clip ledger at {o.ClipLedger}; the cap will not delete "
                             + $"any video in {o.ClipsFolder} because none can be proven to be a clip this engine wrote");
                return list;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(o.ClipLedger))
            {
                var parts = line.Split('\t');
                if (parts.Length != 3 || parts[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                if (!long.TryParse(parts[1], out long bytes) || !long.TryParse(parts[2], out long ticks)) continue;
                if (!seen.Add(parts[0])) continue;
                var f = new FileInfo(Path.Combine(o.ClipsFolder, parts[0]));
                if (f.Exists && f.Length == bytes && f.LastWriteTimeUtc.Ticks == ticks) list.Add(f);
            }
            return list;
        }

        private static long FolderBytes(string folder) =>
            Directory.Exists(folder)
                ? new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;

        private static string LastLine(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? "(ffmpeg said nothing)" : lines[^1];
        }

        // ---- plumbing --------------------------------------------------------

        private void TickFromTimer()
        {
            // Timer callback: an entry point, so this is where a failure is caught and logged. A keeper
            // pass that throws (a file held open by a virus scanner, a full disk) must not end the
            // timer - the next pass tries again, and the error is in the status meanwhile.
            try { Tick(); }
            catch (Exception ex)
            {
                Log.Error("[AlwaysOnEngine] TickFromTimer: the keeper pass failed", ex);
                lock (_lock)
                {
                    _lastError = "the keeper failed: " + ex.Message;
                    Publish();
                }
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("[AlwaysOnEngine] RaiseChanged: a listener failed", ex); }
        }

        public void Dispose()
        {
            Stop();
            _timer?.Dispose();
        }
    }
}
