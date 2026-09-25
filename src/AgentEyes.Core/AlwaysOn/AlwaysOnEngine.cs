using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.Audio;
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
        /// <summary>The last lines ffmpeg wrote, each in full (issue #81).</summary>
        string StderrTail { get; }
        /// <summary>Running or exited (with the exit code), and why a running one counts as failed (issue #81).</summary>
        string ProcessState { get; }
    }

    internal sealed class ContinuousPieceRecorder : IPieceRecorder
    {
        private readonly ContinuousRecorder _rec = new();
        public void Start(AlwaysOnOptions options, SoundLog sound) => _rec.Start(options, sound);
        public void Stop() => _rec.Stop();
        public bool HasExited => _rec.HasExited;
        public string Encoder => _rec.Encoder;
        public string StderrTail => _rec.StderrTail;
        public string ProcessState => _rec.ProcessState;
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
        /// <summary>The line in force, in dBFS RMS: fixed, or Auto's floor + margin (issue #72).</summary>
        public double? ThresholdDb { get; set; }
        /// <summary>The measured noise floor the Auto line is derived from, in dBFS RMS (issue #72).</summary>
        public double? FloorDb { get; set; }
        public bool ThresholdAuto { get; set; }
        /// <summary>Keep before the speech, in seconds (issue #79); null while always-on is off.</summary>
        public double? KeepBeforeSeconds { get; set; }
        /// <summary>Keep after the speech, in seconds (issue #79).</summary>
        public double? KeepAfterSeconds { get; set; }
        /// <summary>The silence that closes a clip, in seconds (issue #79).</summary>
        public double? SilenceGapSeconds { get; set; }
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

        /// <summary>
        /// When the clip being kept right now began (issue #70): the start of its first piece. Null
        /// when no clip is in progress - the state is then anything but <see cref="AlwaysOnState.Keeping"/>.
        /// Until the clip is written it exists only as pieces in the work folder; it is saved to
        /// <see cref="ClipsFolder"/> once <see cref="SilenceGapSeconds"/> of quiet have passed.
        /// </summary>
        public DateTime? OpenClipStartUtc { get; set; }

        /// <summary>How long the clip in progress has run, in seconds, as of when this status was
        /// published (issue #70). Null when no clip is in progress.</summary>
        public double? OpenClipElapsedSeconds { get; set; }

        /// <summary>Every clip written today, oldest first, with its full path (issue #70).</summary>
        public List<AlwaysOnClip> ClipsKeptToday { get; set; } = new();

        /// <summary>How many times the capture was restarted today (issue #81) - zero on a healthy day.</summary>
        public int RestartsToday { get; set; }

        /// <summary>Each of today's capture restarts, oldest first: when, why, and when it recovered (issue #81).</summary>
        public List<AlwaysOnRestart> Restarts { get; set; } = new();

        /// <summary>
        /// The silent-microphone banner (issue #77), or null when the microphone is not silent: the
        /// issue's sentence plus why - Windows reports it muted, or no loud second for
        /// <see cref="SilentMicRule.NoSoundAfter"/>. Never derived from the measured floor (see
        /// <see cref="SilentMicRule"/>). Null too while always-on is off or paused, or when only the
        /// system sound counts.
        /// </summary>
        public string? SilentMic { get; set; }

        /// <summary>The microphone whose level counts, as Windows names it (issue #77); null when the
        /// microphone does not count or its endpoint could not be read.</summary>
        public string? MicDevice { get; set; }

        /// <summary>Windows' mute state for <see cref="MicDevice"/> as last read (at start, then once a
        /// minute); null when unknown.</summary>
        public bool? MicMuted { get; set; }

        /// <summary>Windows' master volume for <see cref="MicDevice"/> in percent, as last read; null when unknown.</summary>
        public double? MicVolumePercent { get; set; }

        /// <summary>Seconds from <see cref="OpenClipStartUtc"/> to <paramref name="nowUtc"/>, or null
        /// when no clip is in progress. Never negative.</summary>
        public double? OpenClipElapsedAt(DateTime nowUtc) =>
            OpenClipStartUtc.HasValue ? Math.Round(Math.Max(0, (nowUtc - OpenClipStartUtc.Value).TotalSeconds), 1) : null;

        /// <summary>
        /// The in-progress line for the Always On page (issue #70), or null when no clip is in
        /// progress: "Recording a clip now - 12 min so far. Saved to C:\AgentEyes after 5 min of quiet."
        /// </summary>
        public string? InProgressLine(DateTime nowUtc)
        {
            var elapsed = OpenClipElapsedAt(nowUtc);
            if (!elapsed.HasValue) return null;
            return $"Recording a clip now - {ClipDuration(elapsed.Value)} so far. "
                   + $"Saved to {ClipsFolder} after {GapText()} of quiet.";
        }

        /// <summary>The same fact in short form, for the tray tooltip (issue #70), or null.</summary>
        public string? InProgressShort(DateTime nowUtc)
        {
            var elapsed = OpenClipElapsedAt(nowUtc);
            if (!elapsed.HasValue) return null;
            return $"Clip in progress, {ClipDuration(elapsed.Value)} so far - saved to {ClipsFolder} after {GapText()} quiet.";
        }

        private string GapText() => AlwaysOnKeepSettings.Describe(TimeSpan.FromSeconds(SilenceGapSeconds ?? 0));

        /// <summary>A clip's running time in whole minutes: "under 1 min", "12 min", "1 h 5 min".</summary>
        public static string ClipDuration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalMinutes < 1) return "under 1 min";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
            return $"{(int)t.TotalMinutes} min";
        }
    }

    /// <summary>One clip written today (issue #70): where it is, and whether it is still there - the
    /// disk cap may have deleted it since, or the owner moved it.</summary>
    internal sealed class AlwaysOnClip
    {
        public string File { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Exists { get; set; }

        /// <summary>How the page lists it: "2026-09-23_10-00-00.mp4 in C:\AgentEyes", and says so
        /// when the file is no longer there.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Label => $"{File} in {Folder}" + (Exists ? "" : " (no longer there)");

        public static AlwaysOnClip For(string path) => new()
        {
            File = System.IO.Path.GetFileName(path),
            Folder = System.IO.Path.GetDirectoryName(path) ?? "",
            Path = path,
            Exists = System.IO.File.Exists(path),
        };
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
    /// PLANNED STOPS (issue #86). An app exit or an update's restart is not a crash:
    /// <see cref="StopForRestart"/> decides what it can with an ordinary (not a final) pass, like a
    /// capture restart (issue #81), and writes a HANDOVER (<see cref="AlwaysOnHandover"/>) - the clip
    /// left open and the sound log behind the pieces still waiting. The next start reads it: the clip
    /// stays open and continues if sound resumes within the silence gap (the restart bridge), the
    /// waiting pieces are judged on the sound that was actually heard, and nothing is deleted for want
    /// of a sound log.
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
        /// How late a new piece may be before the capture counts as hung (issue #81). Pieces are cut on a
        /// forced keyframe exactly every piece length of recorded time and were seen landing within a
        /// second or two of it; twenty seconds is slack for a loaded machine.
        /// </summary>
        public static readonly TimeSpan HungMargin = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How long the capture may go without starting a new piece before it counts as hung and is
        /// restarted: one piece and <see cref="HungMargin"/>. An ffmpeg that is alive but writing nothing
        /// would otherwise look like a healthy recording forever (review finding 4). Checked every
        /// <see cref="TickInterval"/>, so a stall is caught at most margin + one tick after the piece
        /// should have ended - 35 s for one-minute pieces (issue #81 asks for 75 s; it was ~150 s).
        /// </summary>
        public static TimeSpan HungAfter(int pieceSeconds) => TimeSpan.FromSeconds(pieceSeconds) + HungMargin;

        private readonly object _lock = new();
        private readonly Func<IPieceRecorder> _recorderFactory;
        private readonly Func<DateTime> _utcNow;
        private readonly bool _ownTimer;
        private readonly AlwaysOnHistory _history;
        private readonly Func<string?, MicEndpointState> _micEndpoint;
        private Timer? _timer;

        // The silent-microphone rule's inputs (issue #77): Windows' view of the microphone as last
        // read, when the current capture began listening, and the banner in force.
        private bool? _micMuted;
        private double? _micVolume;
        private string? _micDevice;
        private string? _micReadError;
        private string? _silentMic;
        /// <summary>When THIS on-period began delivering the microphone's level: Start or Resume. A
        /// capture restart does not move it - the silence continued across the restart.</summary>
        private DateTime _listeningSinceUtc;
        /// <summary>When Windows is next asked about the microphone (once a minute, outside the lock).</summary>
        private DateTime _nextMicReadUtc;

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

        /// <summary>
        /// Captures in a row that failed before writing one whole piece (issue #81). A screen that stays
        /// unreadable - a locked session, a secure desktop - kills every new capture's screen input at
        /// once; restarting on every pass would churn ffmpeg four times a minute, so from the second
        /// such failure the restart waits on <see cref="RestartBackoff"/> instead.
        /// </summary>
        private int _quickFailures;
        private DateTime _nextRestartUtc;
        /// <summary>When the current capture was launched - taken BEFORE ffmpeg starts, so its first
        /// piece (opened during the start-up wait) is never older than this (issue #81 round 2).</summary>
        private DateTime _recorderStartedUtc;

        /// <summary>The pieces already in the folder when the current capture was launched. A piece
        /// not in this set is the current capture's own - known by name, not by comparing times that
        /// are only whole seconds (issue #81 round 2).</summary>
        private HashSet<string> _piecesBeforeLaunch = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When the keeper last logged the levels, or null for "not yet this run" (issue #72).</summary>
        private DateTime? _levelsLoggedUtc;

        /// <summary>How often the keeper logs the floor, the line and what it heard (issue #72).</summary>
        public static readonly TimeSpan LevelLogInterval = TimeSpan.FromMinutes(1);

        /// <summary>The last levels line the keeper logged, or null before the first (issue #72).</summary>
        public string? LastLevelsLine { get; private set; }

        // The keeper's memory between passes.
        private readonly Dictionary<int, string> _clipDirs = new();
        /// <summary>When each clip in <see cref="_clipDirs"/> began - its first kept piece (issue #70).</summary>
        private readonly Dictionary<int, DateTime> _clipStartsUtc = new();
        /// <summary>The clip being kept, with its first and last sound (issue #79), or null.</summary>
        private OpenClip? _open;
        /// <summary>The last sound of the newest closed clip - sound that never starts a new one (issue #79).</summary>
        private DateTime? _closedSoundUtc;
        private int _nextClip = 1;
        private readonly HashSet<string> _failedJoins = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (on the engine's thread) whenever the state or the counters change.</summary>
        public event Action? Changed;

        public AlwaysOnEngine() : this(() => new ContinuousPieceRecorder(), () => DateTime.UtcNow, ownTimer: true) { }

        /// <param name="ownTimer">False for tests, which call <see cref="Tick"/> themselves.</param>
        /// <param name="history">Where the events go (issue #77); the product's file when null.</param>
        /// <param name="micEndpoint">How Windows' mute state and volume of a microphone are read (issue
        /// #77); the real endpoint when null. Tests substitute a muted or an unmuted answer.</param>
        public AlwaysOnEngine(Func<IPieceRecorder> recorderFactory, Func<DateTime> utcNow, bool ownTimer,
            AlwaysOnHistory? history = null, Func<string?, MicEndpointState>? micEndpoint = null)
        {
            _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _ownTimer = ownTimer;
            _history = history ?? new AlwaysOnHistory(AlwaysOnOptions.DefaultHistoryFile, _utcNow);
            _micEndpoint = micEndpoint ?? MicEndpoint.Read;
        }

        /// <summary>The event history (issue #77): every decision, level line and problem, persisted.</summary>
        public AlwaysOnHistory History => _history;

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
        /// <param name="why">Who or what started it, for the history (issue #77): "Always On page",
        /// "control api", "restored at app start", "command line".</param>
        public void Start(AlwaysOnOptions options, string? pausedReason = null, string why = "user")
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            lock (_lock) ThrowIfOn();
            try
            {
                StartCore(options, pausedReason, why);
            }
            catch (Exception ex)
            {
                // Recorded, then rethrown: the history says why always-on is not on (issue #77).
                Record(HistoryKind.Problem, HistorySeverity.Error, $"Always-on could not start ({why}): {ex.Message}");
                throw;
            }
        }

        /// <summary>Caller holds the lock.</summary>
        private void ThrowIfOn()
        {
            if (_state != AlwaysOnState.Off) throw new UsageException("always-on is already on - stop it first.");
        }

        private void StartCore(AlwaysOnOptions options, string? pausedReason, string why)
        {
            lock (_lock)
            {
                ThrowIfOn();
                Log.Info($"[AlwaysOnEngine] Start: {options}");
                AlwaysOnKeepSettings.Validate(options.KeepBefore, options.KeepAfter, options.SilenceGap);
                if (options.KeyframeSeconds <= 0 || options.PieceSeconds % options.KeyframeSeconds != 0)
                    throw new UsageException($"a piece ({options.PieceSeconds}s) must be a whole number of keyframe intervals ({options.KeyframeSeconds}s).");
                string? leadInNote = AlwaysOnKeepSettings.LeadInNote(options.KeepBefore, options.KeepAfter, options.SilenceGap, options.PieceSeconds);
                if (leadInNote != null) Log.Warn($"[AlwaysOnEngine] Start: {leadInNote}");

                Directory.CreateDirectory(options.WorkFolder);
                Directory.CreateDirectory(options.PieceFolder);
                Directory.CreateDirectory(options.PendingFolder);
                Directory.CreateDirectory(options.ClipsFolder);

                _options = options;
                // One sound log for the whole run: a capture restart must not forget what was heard
                // around the pieces the old capture wrote, or they would be decided as silence.
                _sound = new SoundLog(options.Counts, options.ThresholdDb);
                _levelsLoggedUtc = null;
                _day = AlwaysOnDay.Load(options.StatsFile);
                _day.EnsureDay(_utcNow());
                _lastError = null;
                _pausedReason = null;
                _failedJoins.Clear();
                ResetKeeper();
                _closedSoundUtc = null;
                _silentMic = null;
                _micReadError = null;

                Recover(options);
                EnforceCap(options);
                RecordDeviceFacts(options);

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
                _listeningSinceUtc = _sinceUtc.Value;
                _nextMicReadUtc = _sinceUtc.Value + LevelLogInterval;
                _restartAttempt = 0;
                _quickFailures = 0;
                _state = pausedReason == null ? AlwaysOnState.Listening : AlwaysOnState.Paused;
                _pausedReason = pausedReason;
                Record(HistoryKind.State, HistorySeverity.Info,
                    $"Always-on started ({why})" + (pausedReason == null ? "" : $" - paused: {pausedReason}"));
                // A microphone Windows reports muted is said at once, not after a minute (issue #77).
                if (pausedReason == null) UpdateSilentMic(_utcNow(), listening: true);
                if (_ownTimer && _timer == null)
                    _timer = new Timer(_ => TickFromTimer(), null, TickInterval, TickInterval);
            }
            lock (_lock) Publish();
            Log.Info($"[AlwaysOnEngine] Start: always-on is on{(pausedReason == null ? "" : " (paused: " + pausedReason + ")")}");
            RaiseChanged();
        }

        /// <summary>Turn always-on off: finish the current piece, decide every piece on the sound heard
        /// so far, and write every clip that was being kept.</summary>
        /// <param name="why">Who or what stopped it, for the history (issue #77).</param>
        public void Stop(string why = "user")
        {
            lock (_lock)
            {
                if (_state == AlwaysOnState.Off) return;
                Log.Info($"[AlwaysOnEngine] Stop: stopping always-on ({why})");
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
                    _silentMic = null;
                    Record(HistoryKind.State, HistorySeverity.Info, $"Always-on stopped ({why})");
                    Publish();
                }
            }
            Log.Info("[AlwaysOnEngine] Stop: always-on is off");
            RaiseChanged();
        }

        /// <summary>
        /// Turn always-on off for a PLANNED stop - the app is exiting, or an update is restarting it
        /// (issue #86) - and leave the next start what it needs to carry on where this run left off.
        ///
        /// A capture restart (issue #81) already keeps the open clip: the failed capture's pieces are
        /// decided with an ordinary pass and the new capture's pieces continue the clip across the hole.
        /// A planned stop is the same event with the process boundary in the middle, so it does the same
        /// pass - the clip stays open, a piece past its tail waits for sound that may still come, and a
        /// silent piece whose keep window has fully passed is deleted by the rule as on any pass - and
        /// then writes the handover: the open clip and the sound heard. The next start's
        /// <see cref="Recover"/> restores both, so the clip is continued if sound resumes within the
        /// silence gap and closed with its proper span otherwise, and the waiting pieces are judged on
        /// their own sound log - never deleted for want of one, which is what the manual kill + start in
        /// the 2026-09-24 report did to piece_20260924-175612.
        ///
        /// Compare <see cref="Stop"/>, which is the person switching always-on OFF: it closes and writes
        /// the clip at once, because nothing is coming back.
        /// </summary>
        /// <param name="why">Who or what stopped it, for the history.</param>
        public void StopForRestart(string why = "restart")
        {
            lock (_lock)
            {
                if (_state == AlwaysOnState.Off) return;
                var o = _options!;
                Log.Info($"[AlwaysOnEngine] StopForRestart: stopping always-on for a restart ({why})");
                _timer?.Dispose();
                _timer = null;
                try
                {
                    StopRecorder("restart");
                    // NOT final (issue #81's restart pass): the open clip stays open, a piece past its tail
                    // waits. A pass that throws - a piece held open by a scanner - leaves what it did not
                    // decide on disk for the next start, which has the handover below to decide it with;
                    // the handover is written whatever the pass did, or the restart would lose the clip
                    // to the very failure the handover exists to survive. Logged, never hidden.
                    try
                    {
                        RunKeeper(o, _utcNow(), final: false, recorderRunning: false);
                    }
                    catch (Exception ex)
                    {
                        _lastError = "the keeper failed at the planned stop: " + ex.Message;
                        Log.Error("[AlwaysOnEngine] StopForRestart: the keeper pass failed; the pieces it did not decide wait for the next start", ex);
                        Record(HistoryKind.Problem, HistorySeverity.Error,
                            $"The keeper pass at the planned stop failed: {ex.Message}. The pieces it did not decide wait for the next start with their sound log");
                    }
                    WriteHandover(o, why);
                }
                finally
                {
                    _state = AlwaysOnState.Off;
                    _pausedReason = null;
                    _sinceUtc = null;
                    _silentMic = null;
                    ResetKeeper();
                    Publish();
                }
            }
            Log.Info("[AlwaysOnEngine] StopForRestart: always-on is off until the next start");
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
                    // The silent-mic verdict is KEPT across the pause (the status hides the banner while
                    // paused): nothing is judged until Resume, which re-reads Windows and records a
                    // transition only if the verdict actually changed - so a microphone muted across
                    // many pause/resume cycles is warned about once, not once per cycle (review of #77).
                    Record(HistoryKind.State, HistorySeverity.Info, $"Always-on paused: {reason}");
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
                    _quickFailures = 0;
                    _state = AlwaysOnState.Listening;
                    _listeningSinceUtc = _utcNow();
                    _nextMicReadUtc = _listeningSinceUtc + LevelLogInterval;
                    Record(HistoryKind.State, HistorySeverity.Info, "Always-on resumed");
                    // The pause may have lasted half an hour: Windows is asked again before the rule is
                    // judged, so a mic unmuted (or muted) meanwhile is not judged on the stale state.
                    // The verdict from before the pause is still in force, so this records a
                    // transition only when Windows' answer differs from it.
                    ReadMicEndpoint(_options!);
                    UpdateSilentMic(_utcNow(), listening: true);
                }
                catch (Exception ex)
                {
                    Log.Error("[AlwaysOnEngine] Resume: the capture did not restart; retrying", ex);
                    _lastError = ex.Message;
                    _state = AlwaysOnState.Retrying;
                    var wait = ScheduleRestart();
                    Record(HistoryKind.Problem, HistorySeverity.Error,
                        $"Always-on resumed, but the capture did not start: {ex.Message}. Next attempt in {wait.TotalSeconds:0}s");
                }
                Publish();
            }
            RaiseChanged();
        }

        /// <summary>One pass of the supervisor and the keeper. Called by the timer; tests call it.</summary>
        public void Tick()
        {
            bool changed;
            // Once a minute Windows is asked whether the microphone is muted (issue #77) - OUTSIDE the
            // lock: the audio service can take seconds to answer after a sleep or a device hot-plug, and
            // Pause, Stop and the keeper must not wait behind that. The answer is applied under the lock.
            MicEndpointState? micState = null;
            Exception? micError = null;
            var o = _options;
            if (o != null && o.Counts != SoundSource.System && _state is not (AlwaysOnState.Off or AlwaysOnState.Paused)
                && _utcNow() >= _nextMicReadUtc)
            {
                try { micState = _micEndpoint(o.MicLevelDevice); }
                catch (Exception ex) { micError = ex; }
            }
            lock (_lock)
            {
                if (_options == null || _state is AlwaysOnState.Off or AlwaysOnState.Paused) return;
                var now = _utcNow();
                string before = _state;
                int clipsBefore = _day.Clips;
                double discardedBefore = _day.DiscardedSeconds;
                if (micState.HasValue || micError != null)
                {
                    ApplyMicEndpoint(micState, micError);
                    _nextMicReadUtc = now + LevelLogInterval;
                }

                Supervise(now);
                NoteRecovery();
                bool running = _recorder != null && !_recorder.HasExited;
                if (!running && _open != null && now - _open.LastPieceEndUtc > _options.SilenceGap)
                {
                    // The capture has been down (retrying) for longer than a clip may be carried across
                    // (issue #81): nothing recorded later can continue it, so finish it now.
                    Log.Warn($"[AlwaysOnEngine] Tick: the capture has been down since {_open.LastPieceEndUtc.ToLocalTime():HH:mm:ss}, "
                             + "longer than the silence gap; the open clip is finished and written");
                    try { RunKeeper(_options, now, final: true, recorderRunning: false); }
                    finally { ResetKeeper(); }
                }
                RunKeeper(_options, now, final: false, recorderRunning: running);
                // The rule is judged every pass on the last-read mute state, so a microphone that goes
                // quiet - or speaks again - moves the banner within a tick, not a minute (issue #77);
                // the minute line that may follow carries the judgement as its flag.
                string? silentBefore = _silentMic;
                UpdateSilentMic(now, listening: running);
                LogLevels(now);

                if (_state is AlwaysOnState.Listening or AlwaysOnState.Keeping)
                    _state = IsKeeping(now) ? AlwaysOnState.Keeping : AlwaysOnState.Listening;

                _day.EnsureDay(now);
                // While a clip is in progress every pass changes its running time, which the page and
                // the tray show (issue #70) - so a keeping pass always counts as a change.
                changed = before != _state || clipsBefore != _day.Clips || discardedBefore != _day.DiscardedSeconds
                          || _state == AlwaysOnState.Keeping || silentBefore != _silentMic;
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
                KeepBeforeSeconds = o?.KeepBefore.TotalSeconds,
                KeepAfterSeconds = o?.KeepAfter.TotalSeconds,
                SilenceGapSeconds = o?.SilenceGap.TotalSeconds,
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
                // The banner is not shown while paused (nothing is being listened to); the verdict
                // behind it is kept for Resume.
                SilentMic = _state == AlwaysOnState.Paused ? null : _silentMic,
                MicDevice = _micDevice,
                MicMuted = _micMuted,
                MicVolumePercent = _micVolume,
            };
            if (_state == AlwaysOnState.Keeping)
            {
                s.OpenClipStartUtc = OpenClipStart(o);
                s.OpenClipElapsedSeconds = s.OpenClipElapsedAt(now);
            }
            s.ClipsKeptToday = _day.ClipPaths.Select(AlwaysOnClip.For).ToList();
            s.Restarts = _day.Restarts.Select(r => new AlwaysOnRestart
            {
                AtUtc = r.AtUtc, Reason = r.Reason, LastPieceStartUtc = r.LastPieceStartUtc, RecoveredUtc = r.RecoveredUtc,
            }).ToList();
            s.RestartsToday = s.Restarts.Count;
            if (_sound != null && o != null)
            {
                var src = o.Counts == SoundSource.System ? SoundSource.System : SoundSource.Mic;
                var line = _sound.CurrentThresholdDb(src);
                s.ThresholdDb = line.HasValue ? Math.Round(line.Value, 1) : null;
                var floor = _sound.CurrentFloorDb(src);
                s.FloorDb = floor.HasValue ? Math.Round(floor.Value, 1) : null;
            }
            if (o != null)
            {
                s.PiecesWaiting = PieceFiles(o.PieceFolder).Count;
                s.PiecesKeptInOpenClip = _open != null && _clipDirs.TryGetValue(_open.Id, out var d)
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

        /// <summary>
        /// When the clip in progress began (issue #70). Caller holds the lock; only asked while keeping.
        ///
        /// Once the keeper has kept a piece, the clip is open and began with that first kept piece.
        /// Before that - sound was heard, but no finished piece has been decided since - every piece
        /// still waiting is inside the sound's lead-in window (a piece whose window had passed with no
        /// sound was already deleted), so the clip will begin with the oldest waiting piece, or with
        /// the piece being written when none waits.
        /// </summary>
        private DateTime OpenClipStart(AlwaysOnOptions? o)
        {
            if (_open != null && _clipStartsUtc.TryGetValue(_open.Id, out var start))
            {
                // The clip keeps from keep-before ahead of its first sound, or from its first piece when
                // the recording does not reach back that far (issue #79).
                DateTime leadIn = o == null ? start : KeeperRule.SpanStart(_open.FirstSoundUtc, o.Windows);
                return leadIn > start ? leadIn : start;
            }
            if (o != null)
            {
                var files = PieceFiles(o.PieceFolder);
                if (files.Count > 0)
                {
                    // Sound was heard and no piece is decided yet: the clip will begin keep-before ahead
                    // of that sound, or with the oldest piece still waiting when that is later.
                    DateTime oldest = files[0].StartUtc;
                    DateTime from = oldest - o.KeepAfter;
                    if (_closedSoundUtc is DateTime closed && from <= closed) from = closed.AddSeconds(1);
                    if (_sound?.FirstSound(from, _utcNow()) is DateTime first)
                    {
                        DateTime leadIn = KeeperRule.SpanStart(first, o.Windows);
                        return leadIn > oldest ? leadIn : oldest;
                    }
                    return oldest;
                }
            }
            // Keeping with no piece on disk at all: the capture has only just started its first piece.
            return _sound?.LastSoundUtc ?? _utcNow();
        }

        // ---- the recorder and its supervisor ---------------------------------

        /// <summary>
        /// Start a new capture and take ownership of it. It is installed ONLY once it has started and is
        /// not already failing: a capture whose process is alive but whose input died during the start
        /// (issue #81 round 2) is stopped here and reported as a failed start, never installed as a
        /// recovery. Every capture this creates is either <see cref="_recorder"/> or disposed.
        /// </summary>
        private void StartRecorder(AlwaysOnOptions o)
        {
            if (_recorder != null)
                throw new InvalidOperationException("a capture is already running; it must be stopped before another starts");
            var launched = _utcNow();
            var before = new HashSet<string>(
                PieceFiles(o.PieceFolder).Select(f => Path.GetFileName(f.Path)), StringComparer.OrdinalIgnoreCase);
            var rec = _recorderFactory();
            try
            {
                rec.Start(o, _sound!);
                if (rec.HasExited)
                {
                    string state = rec.ProcessState;
                    string tail = rec.StderrTail;
                    Log.Error($"[AlwaysOnEngine] StartRecorder: the new capture is already failing - ffmpeg {state}. "
                              + $"Its last {FfmpegStderr.TailLines} lines:{Environment.NewLine}{tail}");
                    throw new UsageException($"the capture failed as it started: ffmpeg {state}: {LastLine(tail)}");
                }
            }
            catch
            {
                // Dispose stops it: an ffmpeg that is alive with a dead input must not outlive the attempt.
                rec.Dispose();
                throw;
            }
            _recorder = rec;
            _encoder = rec.Encoder;
            _recorderStartedUtc = launched;
            _piecesBeforeLaunch = before;
        }

        /// <summary>The current capture's own pieces, oldest first (see <see cref="_piecesBeforeLaunch"/>).</summary>
        private List<(string Path, DateTime StartUtc)> CurrentCapturePieces(AlwaysOnOptions o) =>
            PieceFiles(o.PieceFolder).Where(f => !_piecesBeforeLaunch.Contains(Path.GetFileName(f.Path))).ToList();

        /// <summary>
        /// Stamp the last restart as recovered once the new capture is PROVEN to record (issue #81 round
        /// 2): it is running, not failing, and has opened a piece of its own - ffmpeg opens a piece on the
        /// first encoded picture, so a piece means pictures are flowing again. The stamp is when that
        /// piece began, i.e. when recording resumed - not when ffmpeg was launched.
        /// </summary>
        private void NoteRecovery()
        {
            if (_recorder == null || _recorder.HasExited || _day.Restarts.Count == 0) return;
            var last = _day.Restarts[^1];
            if (last.RecoveredUtc != null) return;
            var mine = CurrentCapturePieces(_options!);
            if (mine.Count == 0) return;
            last.RecoveredUtc = mine[0].StartUtc;
            Log.Info($"[AlwaysOnEngine] NoteRecovery: recording again since {mine[0].StartUtc.ToLocalTime():HH:mm:ss} "
                     + $"(the capture failed at {last.AtUtc.ToLocalTime():HH:mm:ss})");
            Record(HistoryKind.Problem, HistorySeverity.Info,
                $"Recording again since {mine[0].StartUtc.ToLocalTime():HH:mm:ss} (the capture failed at {last.AtUtc.ToLocalTime():HH:mm:ss})");
            SaveDay("recovery");
        }

        /// <summary>
        /// Write today's counters and restart history. The history is a record OF the recording, never a
        /// condition FOR it (issue #81 round 2): a stats file that cannot be written - locked by a scanner,
        /// a full disk - is logged as an error and retried on the next save, and supervision, restarts and
        /// the keeper go on. Everything saved is also still in memory and in the status.
        /// </summary>
        private void SaveDay(string why)
        {
            string file = _options!.StatsFile;
            try
            {
                _day.Save(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"[AlwaysOnEngine] SaveDay: today's counters ({why}) could not be written to {file}; "
                          + "the recording goes on and the next save writes them", ex);
                Record(HistoryKind.Problem, HistorySeverity.Error,
                    $"Today's counters could not be written to {file} ({ex.Message}); the recording goes on and the next save retries");
            }
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

            // The newest piece THIS capture opened - the first one included, although it opened during
            // the start-up wait (issue #81 round 2).
            var mine = CurrentCapturePieces(_options!);
            DateTime? newestPiece = mine.Count > 0 ? mine[^1].StartUtc : null;
            string reason;
            if (_recorder.HasExited)
            {
                reason = "the capture stopped unexpectedly";
            }
            else
            {
                // Alive is not the same as recording: the newest piece must keep moving.
                DateTime newest = newestPiece ?? _recorderStartedUtc;
                if (now - newest <= HungAfter(_options.PieceSeconds)) return;
                reason = $"the capture stopped writing (no new piece since {newest.ToLocalTime():HH:mm:ss}, "
                         + $"one was due by {newest.AddSeconds(_options.PieceSeconds).ToLocalTime():HH:mm:ss})";
            }
            RestartFailedCapture(now, reason, newestPiece);
        }

        /// <summary>
        /// Stop a capture that failed, decide what it wrote, and start a new one (issue #81).
        ///
        /// The log gets ffmpeg's last lines IN FULL and whether the process is still alive - the old
        /// 800-character tail cut off the line that said why. The restart KEEPS THE OPEN CLIP: the pieces
        /// the failed capture wrote are decided with an ordinary (not a final) pass, so a clip that was
        /// being kept stays open and the new capture's pieces continue it across the hole (see
        /// <see cref="KeeperRule"/>), instead of the clip being cut at the stall and the next sound
        /// starting a new one with no lead-in.
        /// </summary>
        private void RestartFailedCapture(DateTime now, string reason, DateTime? newestPiece)
        {
            var recorder = _recorder!;
            var o = _options!;
            string state = recorder.ProcessState;
            string tail = recorder.StderrTail;
            _lastError = reason + ": " + LastLine(tail);
            LastRestartReport = $"{reason}; ffmpeg {state}. Its last {FfmpegStderr.TailLines} lines:{Environment.NewLine}{tail}";
            Log.Error($"[AlwaysOnEngine] Supervise: {reason}; restarting. ffmpeg {state}. "
                      + $"Its last {FfmpegStderr.TailLines} lines:{Environment.NewLine}{tail}");
            Record(HistoryKind.Problem, HistorySeverity.Error,
                $"Capture failed: {reason}; ffmpeg {state}. ffmpeg said: {LastLine(tail)}", detail: tail);
            _day.EnsureDay(now);
            _day.Restarts.Add(new AlwaysOnRestart { AtUtc = now, Reason = reason + " - ffmpeg " + state, LastPieceStartUtc = newestPiece });
            SaveDay("restart");
            bool quick = now - _recorderStartedUtc < TimeSpan.FromSeconds(o.PieceSeconds);
            _quickFailures = quick ? _quickFailures + 1 : 0;

            // Stop it first - a hung ffmpeg still holds the piece it was writing - then everything it
            // wrote is finished: decide it before the new capture starts writing.
            try { recorder.Stop(); }
            catch (Exception ex) { Log.Error("[AlwaysOnEngine] Supervise: stopping the failed capture threw", ex); }
            recorder.Dispose();
            _recorder = null;
            try
            {
                // NOT final: an open clip stays open and a piece still inside its lead-in window waits
                // for sound the new capture may yet hear (issue #81).
                RunKeeper(o, now, final: false, recorderRunning: false);
            }
            finally
            {
                // The restart never waits on the keeper (review round 2, finding 1): a keeper pass that
                // throws - a piece held open by a virus scanner - would otherwise leave always-on
                // saying it is on with no capture and nothing that ever starts one again. Pieces the
                // pass did not decide stay in the folder and the next pass decides them with the same
                // sound log and the same open clip.
                if (_quickFailures >= 2)
                {
                    _state = AlwaysOnState.Retrying;
                    _restartAttempt = Math.Min(_quickFailures - 2, RestartBackoff.Length - 1);
                    Log.Warn($"[AlwaysOnEngine] Supervise: {_quickFailures} captures in a row failed before writing a whole piece; "
                             + "the next restart waits instead of starting at once");
                    var wait = ScheduleRestart();
                    Record(HistoryKind.Problem, HistorySeverity.Warning,
                        $"{_quickFailures} captures in a row failed before writing a whole piece; the next restart waits {wait.TotalSeconds:0}s");
                }
                else
                {
                    TryRestart(now);
                }
            }
        }

        /// <summary>The last capture failure as logged: the reason, the process state and ffmpeg's last
        /// lines in full (issue #81). Null until a capture has failed in this run.</summary>
        public string? LastRestartReport { get; private set; }

        private void TryRestart(DateTime now)
        {
            try
            {
                StartRecorder(_options!);
                Log.Info($"[AlwaysOnEngine] TryRestart: capture restarted (attempt {_restartAttempt + 1}); it counts as "
                         + "recovered once it opens a piece"
                         + (_open != null ? "; the clip in progress stays open and the new pieces continue it" : ""));
                Record(HistoryKind.Problem, HistorySeverity.Info,
                    $"Capture restarted (attempt {_restartAttempt + 1}); it counts as recovered once it opens a piece"
                    + (_open != null ? "; the clip in progress stays open" : ""));
                _restartAttempt = 0;
                _state = AlwaysOnState.Listening;
            }
            catch (Exception ex)
            {
                Log.Error($"[AlwaysOnEngine] TryRestart: restart attempt {_restartAttempt + 1} failed", ex);
                _lastError = ex.Message;
                _state = AlwaysOnState.Retrying;
                int attempt = _restartAttempt + 1;
                var wait = ScheduleRestart();
                Record(HistoryKind.Problem, HistorySeverity.Error,
                    $"Restart attempt {attempt} failed: {ex.Message}. Next attempt in {wait.TotalSeconds:0}s");
            }
        }

        /// <returns>How long the wait is.</returns>
        private TimeSpan ScheduleRestart()
        {
            var wait = RestartBackoff[Math.Min(_restartAttempt, RestartBackoff.Length - 1)];
            _restartAttempt++;
            _nextRestartUtc = _utcNow() + wait;
            Log.Info($"[AlwaysOnEngine] ScheduleRestart: next attempt in {wait.TotalSeconds:0}s");
            return wait;
        }

        private void StopRecorderAndFinish(string why)
        {
            StopRecorder(why);
            try
            {
                if (_options != null) RunKeeper(_options, _utcNow(), final: true, recorderRunning: false);
            }
            finally
            {
                ResetKeeper();
            }
        }

        /// <summary>Stop the capture, if one runs, so its current piece is finished and closed. Caller holds the lock.</summary>
        private void StopRecorder(string why)
        {
            if (_recorder == null) return;
            try { _recorder.Stop(); }
            catch (Exception ex) { Log.Error($"[AlwaysOnEngine] {why}: stopping the capture failed", ex); }
            _recorder.Dispose();
            _recorder = null;
        }

        /// <summary>Write what the next start needs to carry on (issue #86). Caller holds the lock.</summary>
        private void WriteHandover(AlwaysOnOptions o, string why)
        {
            var now = _utcNow();
            HandoverClip? open = null;
            if (_open != null)
            {
                if (_clipDirs.TryGetValue(_open.Id, out var dir) && _clipStartsUtc.TryGetValue(_open.Id, out var start))
                {
                    open = new HandoverClip
                    {
                        Id = _open.Id, FirstSoundUtc = _open.FirstSoundUtc, LastSoundUtc = _open.LastSoundUtc,
                        LastPieceEndUtc = _open.LastPieceEndUtc, Dir = dir, StartUtc = start,
                    };
                }
                else
                {
                    Log.Warn($"[AlwaysOnEngine] WriteHandover: clip {_open.Id} is open but has no holding folder on record; it is not carried over");
                }
            }
            // The same horizon the keeper prunes to: nothing still undecided can reach further back.
            var horizon = now - o.KeepBefore - o.KeepAfter - o.SilenceGap - TimeSpan.FromSeconds(o.PieceSeconds * 3);
            var handover = new AlwaysOnHandover
            {
                StoppedUtc = now,
                Why = why,
                Open = open,
                ClosedSoundUtc = _closedSoundUtc,
                NextClip = _nextClip,
                Sound = _sound!.Export(horizon),
            };
            handover.Save(o.HandoverFile);
            int waiting = PieceFiles(o.PieceFolder).Count;
            string clipNote = open == null
                ? "no clip in progress"
                : $"the clip in progress ({Path.GetFileName(open.Dir)}, sound {open.FirstSoundUtc.ToLocalTime():HH:mm:ss} to "
                  + $"{open.LastSoundUtc.ToLocalTime():HH:mm:ss}) stays open";
            Log.Info($"[AlwaysOnEngine] WriteHandover: {o.HandoverFile} - {clipNote}; {waiting} piece(s) wait for the next start "
                     + $"with {handover.Sound.SoundSeconds.Length}s of sound on record");
            Record(HistoryKind.State, HistorySeverity.Info,
                $"Always-on stopped for a restart ({why}) - {clipNote}"
                + (open != null ? " and continues at the next start if sound resumes within the silence gap" : "")
                + (waiting > 0 ? $"; {waiting} piece(s) wait with their sound log" : ""));
        }

        private void ResetKeeper()
        {
            _open = null;
        }

        private bool IsKeeping(DateTime now)
        {
            if (_open != null) return true;
            var last = _sound?.LastSoundUtc;
            if (!last.HasValue || _options == null) return false;
            // Sound a closed clip already ended with is not a clip in progress.
            if (_closedSoundUtc is DateTime closed && last.Value <= closed) return false;
            return now - last.Value <= _options.SilenceGap;
        }

        // ---- the keeper ------------------------------------------------------

        /// <summary>
        /// Once a minute, log the floor, the line, how many loud seconds the last minute had and
        /// whether any of it was sustained sound (issue #72) - so a line that sits in the room noise
        /// shows in the log instead of as a five-hour clip. Caller holds the lock.
        /// </summary>
        private void LogLevels(DateTime now)
        {
            var sound = _sound;
            if (sound == null) return;
            if (_levelsLoggedUtc == null)
            {
                // The first minute is measured from the first tick, so the first line covers a full minute.
                _levelsLoggedUtc = now;
                return;
            }
            // A tolerance of one second: the 15-second timer fires a few milliseconds early as often as
            // late, and a strict compare then skips a whole tick (a 75-second "minute" seen live).
            if (now - _levelsLoggedUtc.Value < LevelLogInterval - TimeSpan.FromSeconds(1)) return;
            DateTime from = _levelsLoggedUtc.Value, to = now.AddSeconds(-1);
            string line = sound.Describe(from, to);
            // Issue #77: the minute's peak and average RMS per source, after the floor/line/loud part.
            string levels = "";
            foreach (var source in new[] { SoundSource.Mic, SoundSource.System })
            {
                if (sound.Levels(source, from, to) is MinuteLevels m)
                    levels += $"; {source.ToString().ToLowerInvariant()} peak={m.PeakDb:0.0}dBFS avg={m.AverageDb:0.0}dBFS";
            }
            Log.Info($"[AlwaysOnEngine] levels: {line}{levels}");
            LastLevelsLine = line;
            _levelsLoggedUtc = now;

            // The flag follows the silent-microphone rule as judged this pass (Tick reads Windows' mute
            // state once a minute and applies the rule before this line) - never the floor (issue #77,
            // tester's finding). The line is always Info: the flag is in its TEXT, and the two
            // transition events in UpdateSilentMic are the problems - not one row per quiet minute.
            bool silent = _silentMic != null;
            Record(HistoryKind.Level, HistorySeverity.Info,
                $"Levels: {line}{levels}" + (silent ? " " + SilentMicRule.LevelFlag : ""));
        }

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
                // A piece ends where the next begins - or, when ffmpeg stopped writing it earlier (the last
                // piece of a capture that was restarted), where it was last written, so the restart's
                // hole shows as a hole and not as a longer piece (issue #81).
                DateTime endUtc = fi.LastWriteTimeUtc;
                if (i + 1 < files.Count && files[i + 1].StartUtc < endUtc) endUtc = files[i + 1].StartUtc;
                if (endUtc < startUtc) endUtc = startUtc;
                pieces.Add(new Piece(path, startUtc, endUtc, fi.Length));
            }

            ISoundTimes sound = (ISoundTimes?)_sound ?? NoSound.Instance;
            // The restart bridge is the silence gap: a hole no longer than that could not have closed
            // the clip anyway (issues #79, #81).
            var plan = KeeperRule.Decide(
                pieces, sound, now, o.Windows, _open, _closedSoundUtc, _nextClip, final, restartBridge: o.SilenceGap);

            foreach (var (clip, fromUtc, toUtc) in plan.Bridged)
            {
                string name = _clipDirs.TryGetValue(clip, out var bridgedDir) ? Path.GetFileName(bridgedDir) : $"clip {clip}";
                Log.Warn($"[AlwaysOnEngine] keeper: {name} continues across a capture restart - a gap of "
                         + $"{(toUtc - fromUtc).TotalSeconds:0}s ({fromUtc.ToLocalTime():HH:mm:ss} to {toUtc.ToLocalTime():HH:mm:ss}) "
                         + "has no piece; the clip is not split there");
                Record(HistoryKind.Problem, HistorySeverity.Warning,
                    $"{name} continues across a capture restart - {(toUtc - fromUtc).TotalSeconds:0}s "
                    + $"({fromUtc.ToLocalTime():HH:mm:ss} to {toUtc.ToLocalTime():HH:mm:ss}) has no video; the clip is not split there");
            }

            foreach (var (piece, clip, copy) in plan.Keep)
            {
                if (!_clipDirs.TryGetValue(clip, out var dir))
                {
                    dir = FreeClipDir(o, piece.StartUtc);
                    _clipDirs[clip] = dir;
                    _clipStartsUtc[clip] = piece.StartUtc;
                }
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, Path.GetFileName(piece.Path));
                if (copy) File.Copy(piece.Path, dest);
                else File.Move(piece.Path, dest);
                Log.Info($"[AlwaysOnEngine] keeper: KEEP {Path.GetFileName(piece.Path)} ({piece.Duration.TotalSeconds:0}s) -> {Path.GetFileName(dir)}"
                         + (copy ? " (a copy: the rest of it may be the lead-in of the next clip, so it stays to be decided again)" : "")
                         + (piece.Duration < KeeperRule.ShortPiece ? " (shorter than 5s - a restart's leftover - joined to the clip it follows)" : ""));
                Record(HistoryKind.Decision, HistorySeverity.Info,
                    $"KEEP {Path.GetFileName(piece.Path)} ({piece.Duration.TotalSeconds:0}s) -> {Path.GetFileName(dir)}"
                    + (copy ? " (a copy; the rest may begin the next clip)" : "")
                    + (piece.Duration < KeeperRule.ShortPiece ? " (a restart's short leftover, joined to the clip it follows)" : ""));
            }
            foreach (var piece in plan.Delete)
            {
                File.Delete(piece.Path);
                _day.EnsureDay(now);
                _day.DiscardedSeconds += piece.Duration.TotalSeconds;
                Log.Info($"[AlwaysOnEngine] keeper: DELETE {Path.GetFileName(piece.Path)} ({piece.Duration.TotalSeconds:0}s, no sound near it)");
                Record(HistoryKind.Decision, HistorySeverity.Info,
                    $"DELETE {Path.GetFileName(piece.Path)} ({piece.Duration.TotalSeconds:0}s) - no sound within "
                    + $"{AlwaysOnKeepSettings.Describe(o.KeepBefore)} before it or {AlwaysOnKeepSettings.Describe(o.KeepAfter)} after it");
            }
            foreach (var span in plan.Close)
            {
                if (_clipDirs.TryGetValue(span.Clip, out var dir))
                {
                    Log.Info($"[AlwaysOnEngine] keeper: CLOSE {Path.GetFileName(dir)} - sound {span.FirstSoundUtc.ToLocalTime():HH:mm:ss} "
                             + $"to {span.LastSoundUtc.ToLocalTime():HH:mm:ss}, kept {span.StartUtc.ToLocalTime():HH:mm:ss} "
                             + $"to {span.EndUtc.ToLocalTime():HH:mm:ss} ({AlwaysOnKeepSettings.Describe(o.KeepBefore)} before, "
                             + $"{AlwaysOnKeepSettings.Describe(o.KeepAfter)} after)");
                    JoinClip(o, dir, now, span);
                    _clipDirs.Remove(span.Clip);
                    _clipStartsUtc.Remove(span.Clip);
                }
            }

            _open = plan.Open;
            _closedSoundUtc = plan.ClosedSoundUtc;
            _nextClip = plan.NextClip;

            if (plan.Keep.Count + plan.Delete.Count + plan.Close.Count > 0) _day.Save(o.StatsFile);
            if (plan.Close.Count > 0) EnforceCap(o);

            // Sound older than anything still undecided can reach is never asked about again: a waiting
            // piece looks back keep-after (and a gap's worth of sound chains), an open clip only forward.
            _sound?.Prune(now - o.KeepBefore - o.KeepAfter - o.SilenceGap - TimeSpan.FromSeconds(o.PieceSeconds * 3));
        }

        /// <summary>A holding folder for a clip that begins with the piece opened at <paramref name="startUtc"/>.
        /// Two clips can begin in the same piece (issue #79), so the second gets a numbered name.</summary>
        private static string FreeClipDir(AlwaysOnOptions o, DateTime startUtc)
        {
            string dir = Path.Combine(o.PendingFolder, "clip_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat));
            for (int n = 2; Directory.Exists(dir); n++)
                dir = Path.Combine(o.PendingFolder, $"clip_{startUtc.ToString(AlwaysOnArgs.PieceStampFormat)}_{n}");
            return dir;
        }

        /// <summary>No sound log (only before the first start): nothing was ever heard.</summary>
        private sealed class NoSound : ISoundTimes
        {
            public static readonly NoSound Instance = new();
            public DateTime? FirstSound(DateTime fromUtc, DateTime toUtc) => null;
            public DateTime? LastSound(DateTime fromUtc, DateTime toUtc) => null;
        }

        /// <summary>
        /// Trim a clip's first and last piece to its span and join the pieces into a single MP4 in the
        /// clips folder, all without re-encoding (issue #79), then remove the holding folder.
        ///
        /// <paramref name="span"/> is null for a holding folder an earlier run left (a crash): its sound
        /// log went with that run, so nothing says where the speech was, and the clip is joined whole -
        /// it holds at most a piece more than it would have.
        ///
        /// NOTHING KEPT IS EVER DELETED BECAUSE SOMETHING FAILED (review finding 1). A piece that cannot
        /// be read is MOVED to the unreadable folder, never deleted, and the clip is joined from the
        /// rest; a trim or a join that fails leaves the whole holding folder - every piece uncut - in
        /// place for the next start.
        /// </summary>
        private void JoinClip(AlwaysOnOptions o, string dir, DateTime now, ClipSpan? span)
        {
            if (_failedJoins.Contains(dir)) return;
            var parts = Directory.GetFiles(dir, "piece_*.mp4").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
            var readable = new List<TrimInput>();
            foreach (var p in parts)
            {
                try
                {
                    double d = MediaProbe.DurationSeconds(p);
                    if (d <= 0) throw new UsageException("zero duration");
                    readable.Add(new TrimInput(p, AlwaysOnArgs.PieceStartUtc(p) ?? now, d));
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(o.UnreadableFolder);
                    string kept = Path.Combine(o.UnreadableFolder, Path.GetFileName(p));
                    File.Move(p, kept, overwrite: false);
                    _lastError = $"a kept piece could not be read and was set aside in {o.UnreadableFolder}";
                    Log.Warn($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(p)} cannot be read ({ex.Message}); "
                             + $"it is left out of the clip and kept at {kept}");
                    Record(HistoryKind.Problem, HistorySeverity.Warning,
                        $"{Path.GetFileName(p)} cannot be read ({ex.Message}); it is left out of the clip and kept at {kept}");
                }
            }
            if (readable.Count == 0)
            {
                Log.Warn($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(dir)} has no readable piece left; "
                         + $"its pieces are in {o.UnreadableFolder}");
                Record(HistoryKind.Problem, HistorySeverity.Warning,
                    $"{Path.GetFileName(dir)} has no readable piece left; no clip is written and its pieces are in {o.UnreadableFolder}");
                Directory.Delete(dir, recursive: true);
                return;
            }

            TrimPlan trim;
            if (span == null)
            {
                trim = new TrimPlan();
                trim.Parts.AddRange(readable.Select(r => new TrimPart(r.Path, r.StartUtc, r.Seconds, null, null)));
                Log.Info($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(dir)} has no known speech times (left by an earlier run); "
                         + "it is joined whole, untrimmed");
            }
            else
            {
                trim = ClipTrim.Plan(readable, span.StartUtc, span.EndUtc, o.KeyframeSeconds);
            }
            foreach (var outside in trim.Outside)
                Log.Info($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(outside.Path)} ({outside.Seconds:0.#}s) is wholly outside the clip's "
                         + "span - silence past its tail or ahead of its lead-in - and is not joined");
            double outsideSeconds = trim.Outside.Sum(p => p.Seconds);
            if (trim.Parts.Count == 0)
            {
                // Every kept piece lies outside the span. Usually the speech fell where nothing was
                // recorded (a capture restart's hole) and there is no video of it to write - but
                // "outside" is judged from each piece's ffprobe duration and its file-name stamp, and a
                // piece a stall truncated can be misjudged. Kept video is never deleted on a judgement
                // that can be wrong: the holding folder is SET ASIDE whole in the unreadable folder, where
                // the pieces a join could not read already go (review fix pass, finding 4).
                Directory.CreateDirectory(o.UnreadableFolder);
                string setAside = UniqueFolder(o.UnreadableFolder, Path.GetFileName(dir));
                Directory.Move(dir, setAside);
                _lastError = $"a clip had no recorded video inside its span; its pieces were set aside in {setAside}";
                Log.Warn($"[AlwaysOnEngine] JoinClip: {Path.GetFileName(dir)} - no recorded video inside the clip's span "
                         + $"({span!.StartUtc.ToLocalTime():HH:mm:ss} to {span.EndUtc.ToLocalTime():HH:mm:ss}); no clip is written and "
                         + $"its {trim.Outside.Count} piece(s) ({outsideSeconds:0.#}s) are set aside, not deleted, in {setAside}");
                Record(HistoryKind.Problem, HistorySeverity.Warning,
                    $"No clip written for {Path.GetFileName(dir)}: no recorded video inside its span "
                    + $"({span.StartUtc.ToLocalTime():HH:mm:ss} to {span.EndUtc.ToLocalTime():HH:mm:ss}); its {trim.Outside.Count} piece(s) are set aside in {setAside}");
                return;
            }

            var start = trim.Parts[0].KeptStartUtc.ToLocalTime();
            string outPath = UniqueClipPath(o.ClipsFolder, start);
            string list = Path.Combine(dir, "join.txt");
            try
            {
                var joinPaths = new List<string>(trim.Parts.Count);
                foreach (var part in trim.Parts)
                {
                    if (!part.IsTrimmed)
                    {
                        joinPaths.Add(part.Path);
                        continue;
                    }
                    // The cut goes next to the piece under a name that is not a piece's, so a crash
                    // before the join leaves the uncut piece to be joined whole at the next start.
                    string cut = Path.Combine(dir, "cut_" + Path.GetFileName(part.Path));
                    Ffmpeg.Run(AlwaysOnArgs.Trim(part.Path, cut, part.InSeconds, part.OutSeconds), "always-on trim");
                    Log.Info($"[AlwaysOnEngine] JoinClip: trimmed {Path.GetFileName(part.Path)} (stream copy) - kept "
                             + $"{(part.InSeconds ?? 0):0.#}s to {(part.OutSeconds ?? part.Seconds):0.#}s of {part.Seconds:0.#}s");
                    joinPaths.Add(cut);
                }
                File.WriteAllText(list, AlwaysOnArgs.JoinList(joinPaths));
                Ffmpeg.Run(AlwaysOnArgs.Join(list, outPath), "always-on join");
            }
            catch (UsageException ex)
            {
                _failedJoins.Add(dir);
                _lastError = $"joining the clip {Path.GetFileName(dir)} failed; its pieces are kept in {dir}";
                Log.Error($"[AlwaysOnEngine] JoinClip: {_lastError}", ex);
                Record(HistoryKind.Problem, HistorySeverity.Error, $"Joining the clip {Path.GetFileName(dir)} failed: {ex.Message}. Its pieces are kept in {dir}");
                if (File.Exists(outPath)) File.Delete(outPath);
                return;
            }

            double seconds = trim.Parts.Sum(p => p.KeptSeconds);
            double trimmedAway = trim.Parts.Sum(p => p.Seconds - p.KeptSeconds);
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
            _day.DiscardedSeconds += outsideSeconds + trimmedAway;
            _day.ClipPaths.Add(outPath);
            _lastClip = outPath;
            Log.Info($"[AlwaysOnEngine] JoinClip: wrote {outPath} ({trim.Parts.Count} pieces, {seconds:0}s, {bytes / 1024.0 / 1024:0.0} MB"
                     + (span == null ? "" : $", starts {start:HH:mm:ss}") + ")");
            Record(HistoryKind.Clip, HistorySeverity.Info,
                $"Clip saved: {outPath} - {AlwaysOnDay.Duration(seconds)}, {AlwaysOnDay.Size(bytes)}, {trim.Parts.Count} piece(s)"
                + (span == null ? " (left by an earlier run; joined whole)" : ""));
        }

        /// <summary>A folder path under <paramref name="parent"/> named <paramref name="name"/> that does
        /// not exist yet (a numbered suffix when it does).</summary>
        private static string UniqueFolder(string parent, string name)
        {
            string path = Path.Combine(parent, name);
            for (int n = 2; Directory.Exists(path); n++) path = Path.Combine(parent, $"{name}_{n}");
            return path;
        }

        private static string UniqueClipPath(string folder, DateTime startLocal)
        {
            string stem = startLocal.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(folder, stem + ".mp4");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{n}.mp4");
            return path;
        }

        /// <summary>
        /// At start: take the handover a planned stop left (issue #86) - the clip it left open stays
        /// open and its sound log is restored, so the pieces it left are judged on what was heard - then
        /// join every OTHER holding folder a previous run left (a crash or a power cut between keeping
        /// and joining), and delete loose pieces ONLY when there is no handover: their sound log died
        /// with that run, so nothing can say whether they were worth keeping.
        /// </summary>
        private void Recover(AlwaysOnOptions o)
        {
            RestoreEvictHolds(o.ClipsFolder);
            var handover = AlwaysOnHandover.Load(o.HandoverFile);
            string? carried = handover == null ? null : RestoreHandover(o, handover);
            // Consumed: a later start must not replay a handover this one has already taken. One that
            // could not be read (Load said so in the log) is set aside under .bad for a look, not replayed.
            if (handover != null) File.Delete(o.HandoverFile);
            else if (File.Exists(o.HandoverFile)) File.Move(o.HandoverFile, o.HandoverFile + ".bad", overwrite: true);
            foreach (var dir in Directory.GetDirectories(o.PendingFolder, "clip_*").OrderBy(d => d, StringComparer.Ordinal))
            {
                if (carried != null && string.Equals(dir, carried, StringComparison.OrdinalIgnoreCase)) continue;
                Log.Info($"[AlwaysOnEngine] Recover: joining {Path.GetFileName(dir)}, left by an earlier run");
                JoinClip(o, dir, _utcNow(), span: null);
            }
            var loose = PieceFiles(o.PieceFolder);
            if (handover != null)
            {
                if (loose.Count > 0)
                    Log.Info($"[AlwaysOnEngine] Recover: {loose.Count} piece(s) left by the planned stop wait for the keeper, "
                             + "which has their sound log; none is deleted here");
            }
            else
            {
                foreach (var (path, _) in loose)
                {
                    Log.Warn($"[AlwaysOnEngine] Recover: deleting {Path.GetFileName(path)} - left by an earlier run that "
                             + "ended before deciding it, and its sound log went with that run");
                    File.Delete(path);
                    Record(HistoryKind.Decision, HistorySeverity.Warning,
                        $"DELETE {Path.GetFileName(path)} - left by an earlier run that ended before deciding it; nothing says whether it had sound");
                }
            }
            _day.Save(o.StatsFile);
        }

        /// <summary>
        /// Restore what a planned stop handed over (issue #86): the open clip (when its holding folder is
        /// still there), the closed-sound mark, the clip numbering and the sound log. Returns the holding
        /// folder of the clip carried over, or null when none is. Caller holds the lock.
        /// </summary>
        private string? RestoreHandover(AlwaysOnOptions o, AlwaysOnHandover h)
        {
            string? carried = null;
            if (h.Open != null)
            {
                if (Directory.Exists(h.Open.Dir))
                {
                    _open = new OpenClip(h.Open.Id, h.Open.FirstSoundUtc, h.Open.LastSoundUtc, h.Open.LastPieceEndUtc);
                    _clipDirs[h.Open.Id] = h.Open.Dir;
                    _clipStartsUtc[h.Open.Id] = h.Open.StartUtc;
                    carried = h.Open.Dir;
                }
                else
                {
                    Log.Warn($"[AlwaysOnEngine] RestoreHandover: the handover names {h.Open.Dir} as the open clip, but it is not there; "
                             + "no clip is carried over");
                    Record(HistoryKind.Problem, HistorySeverity.Warning,
                        $"The clip the planned stop left open ({Path.GetFileName(h.Open.Dir)}) is not there; it is not continued");
                }
            }
            _closedSoundUtc = h.ClosedSoundUtc;
            if (h.NextClip > _nextClip) _nextClip = h.NextClip;
            _sound!.Import(h.Sound);
            string clipNote = carried == null
                ? "no clip in progress"
                : $"the clip in progress ({Path.GetFileName(carried)}) stays open and continues if sound resumes within "
                  + AlwaysOnKeepSettings.Describe(o.SilenceGap);
            Log.Info($"[AlwaysOnEngine] RestoreHandover: continuing after the planned stop at {h.StoppedUtc.ToLocalTime():HH:mm:ss} "
                     + $"({h.Why}) - {clipNote}; {h.Sound.SoundSeconds.Length}s of sound restored");
            Record(HistoryKind.State, HistorySeverity.Info,
                $"Always-on continues after the planned stop at {h.StoppedUtc.ToLocalTime():HH:mm:ss} ({h.Why}) - {clipNote}");
            return carried;
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
                {
                    Log.Info($"[AlwaysOnEngine] EnforceCap: deleted {Path.GetFileName(c.Recording)} "
                             + $"({c.Bytes / 1024.0 / 1024:0.0} MB) - the clips were over the {o.CapBytes / 1024.0 / 1024 / 1024:0.##} GB cap");
                    Record(HistoryKind.Clip, HistorySeverity.Info,
                        $"Clip deleted: {c.Recording} ({AlwaysOnDay.Size(c.Bytes)}) - the clips were over the {o.CapBytes / 1024.0 / 1024 / 1024:0.##} GB cap");
                }
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
            // The original name rides in the private name, so a crash between the rename and the
            // delete can be undone at the next start (RestoreEvictHolds).
            string held = Path.Combine(folder, EvictPrefix + Guid.NewGuid().ToString("N") + "." + Path.GetFileName(path));
            File.Move(path, held);
            var f = new FileInfo(held);
            if (f.Length == bytes && f.LastWriteTimeUtc.Ticks == lastWriteTicksUtc)
            {
                File.Delete(held);
                return true;
            }
            string back = FreeName(path);
            File.Move(held, back);
            Log.Warn($"[AlwaysOnEngine] EvictIfUnchanged: {Path.GetFileName(path)} changed since the ledger recorded it "
                     + $"({f.Length} bytes, expected {bytes}); it is not a clip this engine can prove it wrote, so it was "
                     + $"kept as {Path.GetFileName(back)} and dropped from the ledger");
            return false;
        }

        private const string EvictPrefix = ".alwayson-evict-";

        /// <summary>
        /// Put back every clip an earlier run renamed aside for eviction and never finished with (a
        /// crash, or a delete refused by a lock): each goes back under its own name, or a free one next
        /// to it. Leans to keep - a file that is back under its ledger name, unchanged, is simply
        /// evicted again by the next cap pass if the cap still needs the room.
        /// </summary>
        internal static void RestoreEvictHolds(string clipsFolder)
        {
            if (!Directory.Exists(clipsFolder)) return;
            foreach (var held in Directory.GetFiles(clipsFolder, EvictPrefix + "*"))
            {
                string name = Path.GetFileName(held);
                // .alwayson-evict-<32 hex>.<original name>
                int cut = EvictPrefix.Length + 32 + 1;
                if (name.Length <= cut || name[cut - 1] != '.')
                {
                    Log.Warn($"[AlwaysOnEngine] RestoreEvictHolds: {name} does not carry an original name; left as it is");
                    continue;
                }
                string back = FreeName(Path.Combine(clipsFolder, name.Substring(cut)));
                File.Move(held, back);
                Log.Warn($"[AlwaysOnEngine] RestoreEvictHolds: {name} was left mid-eviction by an earlier run; restored as {Path.GetFileName(back)}");
            }
        }

        /// <summary>The path itself when nothing is there, else the first free "_keptN" name next to it.</summary>
        private static string FreeName(string path)
        {
            string folder = Path.GetDirectoryName(path)!;
            string back = path;
            for (int n = 2; File.Exists(back); n++)
                back = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)}_kept{n}{Path.GetExtension(path)}");
            return back;
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
                    Record(HistoryKind.Problem, HistorySeverity.Error, $"The keeper pass failed: {ex.Message}. The next pass tries again");
                    Publish();
                }
                RaiseChanged();
            }
        }

        // ---- the history and the silent-microphone rule (issue #77) --------

        /// <summary>Add one event to the history. Never throws (the history is a record of the
        /// recording, not a condition for it).</summary>
        private void Record(HistoryKind kind, HistorySeverity severity, string text, string? detail = null)
        {
            try
            {
                _history.Append(new AlwaysOnEvent { AtUtc = _utcNow(), Kind = kind, Severity = severity, Text = text, Detail = detail });
            }
            catch (Exception ex)
            {
                Log.Error($"[AlwaysOnEngine] Record: the history refused an event ({kind}/{severity}: {text})", ex);
            }
        }

        /// <summary>
        /// The device facts at start (issue #77): which microphone and whether Windows reports it muted
        /// and at what volume, whether the system sound is recorded, and which sound counts. Caller holds
        /// the lock.
        /// </summary>
        private void RecordDeviceFacts(AlwaysOnOptions o)
        {
            ReadMicEndpoint(o);
            string mic;
            if (o.Counts == SoundSource.System)
                mic = o.DshowMic == null ? "microphone not recorded and not listened to" : $"microphone \"{o.DshowMic}\" recorded (its level does not count)";
            else if (_micDevice != null)
                mic = $"microphone \"{_micDevice}\" - Windows: {(_micMuted == true ? "MUTED" : "not muted")}, volume {_micVolume:0}%"
                      + (o.DshowMic == null ? " (level only, not recorded)" : "");
            else
                mic = $"microphone \"{o.MicLevelDevice ?? "(default)"}\" - Windows state unknown: {_micReadError}";
            string system = o.RecordSystem ? "system sound recorded (default playback device)" : "system sound not recorded";
            string counts = o.Counts switch { SoundSource.System => "system sound", SoundSource.Both => "microphone or system sound", _ => "microphone" };
            Record(HistoryKind.Device, _micMuted == true ? HistorySeverity.Warning : HistorySeverity.Info,
                $"Devices: {mic}; {system}; {counts} counts. Setup \"{o.SetupName}\"");
        }

        /// <summary>
        /// Ask Windows whether the microphone whose level counts is muted, and how loud it is set. Not
        /// asked when only the system sound counts. A read that fails - no such device, no default
        /// microphone - leaves the state unknown (null), is recorded once per distinct message, and the
        /// rule then judges on sound alone. Caller holds the lock.
        /// </summary>
        private void ReadMicEndpoint(AlwaysOnOptions o)
        {
            if (o.Counts == SoundSource.System)
            {
                _micMuted = null;
                _micVolume = null;
                _micDevice = null;
                return;
            }
            MicEndpointState? state = null;
            Exception? error = null;
            try { state = _micEndpoint(o.MicLevelDevice); }
            catch (Exception ex) { error = ex; }
            ApplyMicEndpoint(state, error);
        }

        /// <summary>Take Windows' answer about the microphone (or the failure to get one). Caller holds the lock.</summary>
        private void ApplyMicEndpoint(MicEndpointState? state, Exception? error)
        {
            if (state.HasValue)
            {
                bool? wasMuted = _micMuted;
                _micMuted = state.Value.Muted;
                _micVolume = state.Value.VolumePercent;
                _micDevice = state.Value.Name;
                _micReadError = null;
                if (wasMuted.HasValue && wasMuted.Value != state.Value.Muted)
                    Log.Info($"[AlwaysOnEngine] ApplyMicEndpoint: {state.Value.Describe()}");
                return;
            }
            _micMuted = null;
            _micVolume = null;
            _micDevice = null;
            string message = error?.Message ?? "no answer";
            if (_micReadError != message)
            {
                _micReadError = message;
                Log.Warn($"[AlwaysOnEngine] ApplyMicEndpoint: Windows' mute state for the microphone could not be read: {message}");
                Record(HistoryKind.Problem, HistorySeverity.Warning,
                    $"Windows' mute state for the microphone could not be read ({message}); the silent-microphone rule judges on sound alone");
            }
        }

        /// <summary>
        /// Apply <see cref="SilentMicRule"/> and record a transition (issue #77). Judged only while
        /// always-on is on and the microphone counts.
        ///
        /// While the capture is down (<paramref name="listening"/> false - it failed and is being
        /// restarted) no sound can arrive, so nothing is learned about the no-sound arm: a banner that
        /// was up STAYS up and one that was down stays down; only the mute arm can change it. A capture
        /// restart does not restart the ten-minute clock either (<see cref="_listeningSinceUtc"/>) -
        /// otherwise every restart would clear a true banner with a false "sending sound again" and
        /// raise it again ten minutes later. Caller holds the lock.
        /// </summary>
        private void UpdateSilentMic(DateTime now, bool listening)
        {
            string? text = null;
            if (_options != null && _options.Counts != SoundSource.System
                && _state is AlwaysOnState.Listening or AlwaysOnState.Keeping or AlwaysOnState.Retrying)
            {
                if (listening)
                {
                    var reason = SilentMicRule.Evaluate(_micMuted, _sound?.LastLoudUtc(SoundSource.Mic), _listeningSinceUtc, now);
                    text = reason.HasValue ? SilentMicRule.Describe(reason.Value) : null;
                }
                else if (_micMuted == true)
                {
                    text = SilentMicRule.Describe(SilentMicReason.Muted);
                }
                else
                {
                    // Capture down, not muted: the sound's last verdict stands. A mute verdict whose
                    // mute has ended cannot stand on its own; the no-sound arm is judged when sound can arrive again.
                    text = _silentMic == SilentMicRule.Describe(SilentMicReason.Muted) ? null : _silentMic;
                }
            }
            if (text == _silentMic) return;
            if (text != null)
            {
                Log.Warn($"[AlwaysOnEngine] UpdateSilentMic: {text}");
                Record(HistoryKind.Problem, HistorySeverity.Warning, text);
            }
            else
            {
                // Say what happened, not what it might mean: a cleared MUTE is Windows' report changing
                // (no sound need have arrived - the capture may be down); only the no-sound arm's
                // clearing means a loud second was heard.
                string cleared = _silentMic == SilentMicRule.Describe(SilentMicReason.Muted)
                    ? SilentMicRule.UnmutedText
                    : SilentMicRule.ClearedText;
                Log.Info($"[AlwaysOnEngine] UpdateSilentMic: {cleared}");
                Record(HistoryKind.Problem, HistorySeverity.Info, cleared);
            }
            _silentMic = text;
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("[AlwaysOnEngine] RaiseChanged: a listener failed", ex); }
        }

        public void Dispose()
        {
            Stop("shutdown");
            _timer?.Dispose();
        }
    }
}
