using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AgentEyes.Audio;
using AgentEyes.Preview;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    internal enum AudioSourceKind { None, Mic, System, Mixed }

    internal sealed class RecordStatus
    {
        public string State { get; set; } = "idle";   // idle | recording | finalizing
        public string? Mode { get; set; }              // audio | video
        public string? Source { get; set; }            // mic | system | mixed | none
        public double ElapsedSeconds { get; set; }
        public double Level { get; set; }              // 0..1 (peak)
        public string? Dir { get; set; }

        // The camera being recorded to camera.mp4 alongside the screen (issue #28), by its exact
        // DirectShow device name. Null when this recording has no camera track, and null while idle.
        public string? Camera { get; set; }

        // DevThrottle account state (issue #129). Carried on /status so the sign-in indicator is
        // verifiable without a screenshot - recording works signed out, but the AI stages do not.
        public bool SignedIn { get; set; }
        public string? AccountEmail { get; set; }

        // Recordings still awaiting automatic transcription (issue #132). 0 means the library is
        // fully transcribed; a non-zero value that never falls is a backfill that cannot proceed.
        public int PendingTranscriptions { get; set; }

        // The last stop that FAILED (issue #153). The service returns to idle after a failed stop so
        // the user can record again, but it must not present that as a CLEAN idle: these three carry
        // the failure until the next recording starts. LastStopFailed=false is the normal state.
        public bool LastStopFailed { get; set; }

        // Every failure from that stop on one line ("audio stop: ...; manifest save: ..."), not just
        // the first one.
        public string? LastStopError { get; set; }

        // The directory of the recording whose stop failed - what to look at to recover it.
        public string? LastStopDir { get; set; }

        // Camera ffmpeg processes AgentEyes asked to die, killed, retried - and could not end
        // (issue #28, AC16). True is a live process still holding a webcam and a camera.mp4 right
        // now; it is not history the way LastStopFailed is. False is the normal state.
        public bool CameraStuck { get; set; }

        // One row per stuck camera process, each carrying its PID - the field that makes it
        // actionable rather than merely alarming. Empty in the normal state.
        public IReadOnlyList<StrandedCameraReport> StuckCameras { get; set; } = Array.Empty<StrandedCameraReport>();

        // ---- the HUD live preview (issue #33) ------------------------------
        //
        // These are here so the preview is verifiable WITHOUT a screenshot. The HUD is deliberately
        // excluded from screen capture (WDA_EXCLUDEFROMCAPTURE), which is exactly what stops a screen
        // preview inside it becoming a mirror tunnel - and also what makes a full-screen grab useless
        // as evidence about it. /status is the focus-free way to read preview state instead.

        // Where the screen / camera preview frame is published, or null when this recording has no
        // such tap. The file exists only while PreviewPublishing is true.
        public string? PreviewScreenFrame { get; set; }
        public string? PreviewCameraFrame { get; set; }

        // Whether this recording carries a preview feed at all. False means it was started with the
        // preview switched off, so its ffmpeg has no preview output and the HUD panel has nothing to
        // show - the honest reading, and the one that keeps a preview-less recording identical to
        // what it was before this feature (issue #33, AC11).
        public bool PreviewAvailable { get; set; }

        // Whether the NEXT recording will carry one.
        public bool PreviewArmed { get; set; }

        // Whether frames are being written out right now (i.e. the HUD panel is showing).
        public bool PreviewPublishing { get; set; }

        // Whole preview frames taken off each ffmpeg pipe so far. These are a PRESENCE: a count that
        // climbs is a live tap, and a count stuck at zero is a tap that has never seen a frame - a
        // distinction "the file exists" cannot make.
        public long PreviewScreenFramesRead { get; set; }
        public long PreviewCameraFramesRead { get; set; }

        // True when a tap is currently failing to publish (the preview directory is gone, read-only
        // or full). It says the PREVIEW is broken and says nothing about the recording, which is
        // unaffected by design (issue #33, AC10).
        public bool PreviewFailed { get; set; }

        // The overlay corner framed during this recording, or null when none was. The same value that
        // reaches manifest.json at the stop.
        public string? PreviewOverlayCorner { get; set; }

        // Issue #36: the overlay SHAPE framed during this recording - "circle" or "rectangle" - or
        // null when no overlay was framed. On /status so the shape can be asserted without a
        // screenshot, which matters because the HUD is deliberately invisible to screen capture.
        public string? PreviewOverlayShape { get; set; }
    }

    internal sealed class RecordResult
    {
        public string Dir { get; set; } = "";
        public string? File { get; set; }
        public double DurationSeconds { get; set; }
        public int Shots { get; set; }
    }

    /// <summary>
    /// The single owner of a capture session. Drives the engine (screenshot, audio, video, mux) and
    /// holds session state. Used by the GUI, the tray, and the REST API so there is one implementation.
    /// One active recording at a time; thread-safe start/stop.
    /// </summary>
    internal sealed class RecordingService
    {
        private readonly object _lock = new();
        private volatile string _state = "idle";

        private string? _dir;
        private Manifest? _manifest;
        private string _mode = "";
        private AudioSourceKind _src;
        private AudioMixOptions _opts = new();
        private MonitorInfo? _monitor;
        private Drawing.Rectangle _captureRect;

        private AudioCapture? _audio;
        private LoopbackCapture? _loop;
        private FfmpegRecorder? _video;
        private string? _micWav, _sysWav, _rawVideo;

        /// <summary>
        /// The webcam capture running alongside the screen capture (issue #28), or null when this
        /// recording has no camera. It is a separate ffmpeg process writing its own camera.mp4; it
        /// never feeds the screen video and never participates in the deferred audio mux.
        /// </summary>
        private FfmpegCameraRecorder? _camera;

        /// <summary>The exact DirectShow name of <see cref="_camera"/>'s device, for /status.</summary>
        private volatile string? _cameraName;

        /// <summary>
        /// The live preview taps for this session (issue #33) - one per recorded video track, null
        /// when the track is not being recorded or when the machine could not host a preview.
        ///
        /// They are NOT writers and are deliberately absent from the start/stop step sequences. A
        /// writer's failure is the recording's failure; a tap's failure costs a picture (AC10). Being
        /// outside those sequences is what keeps a preview problem out of
        /// <see cref="RecordingStopReport"/> and off the "this stop failed" surface.
        /// </summary>
        private PreviewTap? _screenTap, _cameraTap;

        /// <summary>
        /// The overlay FRAMING the person chose while this recording ran - shape, circle, corner and
        /// inset size - or null if they never framed one (issue #33 AC5, extended by issue #36 AC4).
        /// Written into the manifest at the stop as EDIT METADATA: it composites nothing, crops
        /// nothing, and changes neither recorded file.
        /// </summary>
        private volatile CameraOverlaySettings? _previewOverlay;

        /// <summary>
        /// This session's OWN capture claim on <see cref="_dir"/> (issue #154, round 3) - the only
        /// thing that can release it.
        ///
        /// The stop used to release by directory name, which removes whichever claim is on the
        /// directory rather than this session's. That is harmless while the claim was granted and
        /// destructive when it was not: a start that had been refused (a directory-name collision)
        /// still ran that release and tore down the claim of the pipeline that refused it. A capture
        /// that does not own its directory no longer starts at all, and a session that never claimed
        /// carries a ticket that releases nothing.
        /// </summary>
        private RecordingClaimTicket _captureClaim;

        /// <summary>
        /// The camera ffmpeg processes this service could not kill (issue #28, AC16).
        ///
        /// It is the reference the Review Gate said was missing: the recorder correctly keeps its
        /// process handle when a stop cannot confirm ffmpeg dead, but the object holding it used to
        /// be dropped one line later - <c>_camera</c> cleared, the local out of scope, the claim
        /// released, the service idle. Nothing in the app could reach that process again. This
        /// survives the session, keeps the recording's claim with it, reports it on <c>/status</c>,
        /// and retries it the next time a recording starts.
        /// </summary>
        private readonly StrandedCameraOwner _stranded = new();

        private readonly Stopwatch _sw = new();
        private volatile float _peakMic, _peakSys;
        private volatile RecordingStopReport? _lastStopFailure;

        public bool IsRecording => _state == "recording";

        /// <summary>
        /// The report from the most recent stop that FAILED (issue #153), or null when the last stop
        /// was clean and when a new recording has started.
        ///
        /// A failed stop leaves the service idle - refusing to record again would punish the user for
        /// a writer that misbehaved - but "idle" on its own used to be indistinguishable from a
        /// healthy one, which is how a lost recording looked exactly like a recording that was never
        /// made. This is that distinction, and it is on <c>/status</c> as well
        /// (<see cref="RecordStatus.LastStopFailed"/>).
        /// </summary>
        public RecordingStopReport? LastStopFailure => _lastStopFailure;

        /// <summary>
        /// Raised (issue #107) after a recording session has fully stopped and state is back to
        /// "idle", so the app can act on an AutoUpdate restart that was deferred while the session
        /// was in progress. Raised on the thread that called <see cref="Stop"/>, AFTER the state
        /// lock is released, so a subscriber may safely marshal to the UI thread and shut down.
        /// </summary>
        public event Action? RecordingStopped;

        public float Level => Math.Max(_peakMic, _peakSys);
        public float MicLevel => _peakMic;
        public float SystemLevel => _peakSys;
        public TimeSpan Elapsed => _sw.Elapsed;

        /// <summary>The camera processes this service could not kill, for tests and for the status
        /// surface (issue #28, AC16).</summary>
        public StrandedCameraOwner StrandedCameras => _stranded;

        // ---- live preview (issue #33) --------------------------------------

        /// <summary>
        /// Whether the next recording should carry a live preview feed. FALSE BY DEFAULT, and that
        /// default is load-bearing rather than cautious (issue #33, AC11).
        ///
        /// A preview feed is a SECOND OUTPUT on the recording's own ffmpeg, so arming it changes the
        /// command line - and that command line is written into manifest.json as
        /// <see cref="Manifest.FfmpegCommand"/>. With this false, a recording is byte-for-byte the
        /// recording it was before this feature existed: same arguments, same files, same manifest.
        /// The CLI never sets it, so <c>agenteyes video</c> is untouched by the feature entirely.
        ///
        /// The app sets it from the persisted "show preview" choice, so the cost is paid only by
        /// someone who actually uses the preview - which is also what makes AC9's preview-OFF control
        /// run a genuine control rather than the same run under a different name.
        ///
        /// It is read once, at the start of a recording. Changing it mid-recording arms the NEXT one:
        /// ffmpeg's outputs are fixed when the process starts, and restarting ffmpeg to add a monitor
        /// would interrupt the thing being monitored.
        /// </summary>
        public bool PreviewArmed { get; set; }

        /// <summary>
        /// True when THIS recording carries a preview feed, i.e. the HUD can show live frames. False
        /// says the panel has nothing to show and should say so rather than sit blank - a recording
        /// started while <see cref="PreviewArmed"/> was false has no feed and cannot grow one.
        /// </summary>
        public bool PreviewAvailable => _screenTap != null || _cameraTap != null;

        /// <summary>
        /// The file the HUD reads for the SCREEN preview, or null when this recording has no screen
        /// preview tap. The file only exists while <see cref="PreviewPublishing"/> is on.
        /// </summary>
        public string? PreviewScreenFrame => _screenTap?.FramePath;

        /// <summary>The file the HUD reads for the CAMERA preview, or null when this recording has no
        /// camera track (or no tap on it).</summary>
        public string? PreviewCameraFrame => _cameraTap?.FramePath;

        /// <summary>Whether preview frames are being written out right now.</summary>
        public bool PreviewPublishing => _screenTap?.Publishing == true || _cameraTap?.Publishing == true;

        /// <summary>The overlay corner recorded for this session, or null when none was framed.</summary>
        public string? PreviewOverlayCorner => _previewOverlay?.Corner;

        /// <summary>The whole overlay framing recorded for this session (issue #36), or null when
        /// none was framed.</summary>
        public CameraOverlaySettings? PreviewOverlay => _previewOverlay;

        /// <summary>
        /// Turn frame publishing on or off for every tap in this session. This is the WHOLE cost of
        /// showing or hiding the preview mid-recording (AC8): no ffmpeg is restarted, no output is
        /// added or removed, and the recording is not told. Cheap enough to call from a UI click
        /// handler, and safe when there is no recording - it does nothing.
        /// </summary>
        public void SetPreviewPublishing(bool on)
        {
            Log.Info($"[RecordingService] SetPreviewPublishing: on={on}");
            var screen = _screenTap;
            var camera = _cameraTap;
            if (screen != null) screen.Publishing = on;
            if (camera != null) camera.Publishing = on;
        }

        /// <summary>
        /// Record the overlay framing the camera is being watched in (issue #33 AC5, issue #36 AC4),
        /// or null to record none. Sticky for the session: the LAST framing chosen is what reaches
        /// manifest.json at the stop, because that is the framing the person settled on.
        ///
        /// It writes EDIT METADATA and nothing else - it composites nothing, crops nothing, and
        /// changes no recorded file. The caller's object is COPIED and canonicalised here, so a later
        /// edit in the HUD cannot reach back and rewrite what this recording was framed with, and an
        /// unrecognised spelling can never reach the manifest. Safe when there is no recording; the
        /// value is simply dropped at the next start.
        /// </summary>
        public void SetPreviewOverlay(CameraOverlaySettings? overlay)
        {
            var copy = overlay?.Canonical();
            Log.Info($"[RecordingService] SetPreviewOverlay: overlay={(copy == null ? "(none)" : copy.ToString())}");
            _previewOverlay = copy;
        }

        public RecordStatus Status()
        {
            var failure = _lastStopFailure;

            // BOTH kinds of stuck camera, because a person reading /status wants to know that THE
            // WEBCAM IS HELD - not which of our two owners is holding the process that is holding
            // it. The preset editor's preview can strand an ffmpeg exactly as a recording can
            // (issue #35, Review Gate round 1, defect 4), and a status that showed only one of them
            // would report an empty list while a live process sat on the camera.
            var stuck = new List<StrandedCameraReport>(_stranded.Report());
            stuck.AddRange(Video.CameraDeviceArbiter.StrandedPreviews.Report());
            return new RecordStatus
            {
                State = _state,
                Mode = _state == "idle" ? null : _mode,
                Source = _state == "idle" ? null : _src.ToString().ToLowerInvariant(),
                ElapsedSeconds = Math.Round(_sw.Elapsed.TotalSeconds, 2),
                Level = Math.Round(Math.Max(_peakMic, _peakSys), 3),
                Dir = _dir,
                Camera = _state == "idle" ? null : _cameraName,
                SignedIn = DevThrottle.AccountState.IsSignedIn,
                AccountEmail = DevThrottle.AccountState.Email,
                PendingTranscriptions = TranscriptionBacklog.FindPending(RecordingPaths.Root).Count,
                LastStopFailed = failure != null,
                LastStopError = failure?.Summary(),
                LastStopDir = failure?.Dir,
                CameraStuck = stuck.Count > 0,
                StuckCameras = stuck,
                PreviewScreenFrame = _state == "idle" ? null : _screenTap?.FramePath,
                PreviewCameraFrame = _state == "idle" ? null : _cameraTap?.FramePath,
                PreviewAvailable = _state != "idle" && this.PreviewAvailable,
                PreviewArmed = this.PreviewArmed,
                PreviewPublishing = _state != "idle" && PreviewPublishing,
                PreviewScreenFramesRead = _screenTap?.FramesRead ?? 0,
                PreviewCameraFramesRead = _cameraTap?.FramesRead ?? 0,
                PreviewFailed = _screenTap?.PublishFailed == true || _cameraTap?.PublishFailed == true,
                PreviewOverlayCorner = _previewOverlay?.Corner,
                PreviewOverlayShape = _previewOverlay?.Shape,
            };
        }

        // ---- one-shot screenshot (no session) -----------------------------

        public string Screenshot(int screen, int[]? region)
        {
            var mon = Monitors.Require(screen);
            string dir = RecordingPaths.NewDir("shot", "shot");
            var manifest = NewManifest("shot", mon);
            string file;
            if (region is { Length: 4 })
            {
                var rect = new Drawing.Rectangle(region[0], region[1], region[2], region[3]);
                file = Path.Combine(dir, "shots", $"region_{rect.Width}x{rect.Height}.png");
                AgentEyes.Screenshot.CaptureRect(rect, file, copyToClipboard: true);
                manifest.Region = region;
            }
            else
            {
                file = Path.Combine(dir, "shots", $"monitor{mon.Index}_full.png");
                AgentEyes.Screenshot.CaptureMonitor(mon, file, copyToClipboard: true);
            }
            manifest.Files.Add(Path.GetFileName(file));
            ManifestStore.Replace(dir, manifest);   // a directory this call just created (issue #155)
            Log.Info($"screenshot -> {file}");
            return file;
        }

        // ---- start audio (Mode A) -----------------------------------------

        public void StartAudio(int screen, AudioSourceKind src, string? micFragment, AudioMixOptions opts)
        {
            lock (_lock)
            {
                if (_state != "idle") throw new UsageException("already recording");
                var mon = Monitors.Require(screen);
                if (src is not (AudioSourceKind.Mic or AudioSourceKind.System or AudioSourceKind.Mixed))
                    throw new UsageException("audio requires source mic, system, or mixed");

                // Resolve the microphone before touching disk - a bad device name (e.g. a preset
                // saved over RDP referencing "Remote Audio") must fail cleanly, not leave an
                // empty recording folder behind.
                int mic = -1;
                if (src is AudioSourceKind.Mic or AudioSourceKind.Mixed)
                    mic = AudioCapture.ResolveDevice(Require(micFragment, "mic"));

                Reset(mon, "audio", src, opts);
                _dir = RecordingPaths.NewDir("audio", "audio");
                _manifest = NewManifest("audio", mon);
                _manifest.AudioFile = "audio.wav";

                // Everything the recording's FIRST manifest needs is known before a single byte is
                // captured (issue #155): which raw files this session will write, what the microphone
                // is called, and whether the mux is deferred. Deciding it here - rather than at stop -
                // is what lets the record reach disk before the media does.
                if (src is AudioSourceKind.Mixed) _micWav = Path.Combine(_dir, "mic.wav");
                if (src is AudioSourceKind.System or AudioSourceKind.Mixed) _sysWav = Path.Combine(_dir, "sys_native.wav");
                _manifest.Microphone = src switch
                {
                    AudioSourceKind.Mic => AudioCapture.Devices()[mic].Name,
                    AudioSourceKind.System => "(system loopback)",
                    _ => $"{AudioCapture.Devices()[mic].Name} + (system)",
                };
                _manifest.PendingMux = BuildPendingMux(
                    "audio", src, _micWav, _sysWav, rawVideo: null,
                    finalFile: Path.Combine(_dir, "audio.wav"), rawDurationSeconds: 0, opts);

                // Issue #155: the publish AND every writer start run inside ONE failure boundary
                // (RecordingStartSequence). Mixed audio starts two writers, and the microphone is
                // already capturing by the time the loopback can fail - a rollback that did not stop
                // it left the microphone recording while the service reported idle.
                string dir = _dir;
                var steps = new List<RecordingStartStep>();
                switch (src)
                {
                    case AudioSourceKind.Mic:
                        steps.Add(new RecordingStartStep("microphone", () =>
                        {
                            _audio = new AudioCapture(mic);
                            _audio.LevelChanged += p => _peakMic = p;
                            _audio.Start(Path.Combine(dir, "audio.wav"));
                        }));
                        break;
                    case AudioSourceKind.System:
                        steps.Add(new RecordingStartStep("system loopback", () =>
                        {
                            _loop = new LoopbackCapture();
                            _loop.LevelChanged += p => _peakSys = p;
                            _loop.Start(_sysWav!);
                        }));
                        break;
                    default: // Mixed
                        steps.Add(new RecordingStartStep("microphone", () =>
                        {
                            _audio = new AudioCapture(mic);
                            _audio.LevelChanged += p => _peakMic = p;
                            _audio.Start(_micWav!);
                        }));
                        steps.Add(new RecordingStartStep("system loopback", () =>
                        {
                            _loop = new LoopbackCapture();
                            _loop.LevelChanged += p => _peakSys = p;
                            _loop.Start(_sysWav!);
                        }));
                        break;
                }

                StartSession(steps);

                _sw.Restart();
                _state = "recording";
                Log.Info($"start audio src={src} dir={_dir}");
            }
        }

        // ---- start video (Mode B) -----------------------------------------

        /// <param name="cameraFragment">A camera name fragment to record to camera.mp4 alongside the
        /// screen (issue #28), or null for no camera track. Resolved to ONE exact DirectShow device
        /// before anything touches disk; absent or ambiguous fails the start.</param>
        /// <param name="cameraFps">Frame rate requested from the camera. The camera's own default
        /// resolution is used (assumption A2).</param>
        /// <param name="overlay">Issue #47: the camera framing chosen in the PRESET, seeded here so it
        /// is on the record from the first frame. Before this it arrived only from the HUD preview
        /// window (SetPreviewOverlay), so a recording made without that window open wrote no framing
        /// at all and there was nothing for the compose stage to lay out. The HUD can still refine it
        /// while recording; this is the starting value, not a competing one.</param>
        public void StartVideo(int screen, AudioSourceKind src, string? micFragment, int[]? region,
            AudioMixOptions opts, int fps, string? cameraFragment = null, int cameraFps = 30,
            CameraOverlaySettings? overlay = null)
        {
            // Retained recorders are only worth retaining if something ever uses them again (issue
            // #28, AC16). This is that moment: the user is asking for a camera recording, which is
            // exactly when a webcam still held by an ffmpeg from a previous session matters. Run
            // OUTSIDE the state lock - the retry talks to a process and can take seconds - and
            // before the start, so a camera that has since been freed frees its claim too.
            _stranded.Recover();

            lock (_lock)
            {
                if (_state != "idle") throw new UsageException("already recording");
                var mon = Monitors.Require(screen);
                Reset(mon, "video", src, opts);
                var capture = region is { Length: 4 }
                    ? new Drawing.Rectangle(region[0], region[1], region[2], region[3])
                    : mon.Bounds;
                _captureRect = capture;

                // Resolve the microphone before touching disk - a bad device name (e.g. a preset
                // saved over RDP referencing "Remote Audio") must fail cleanly, not leave an
                // empty recording folder behind.
                string? dshowMic = null;
                if (src is AudioSourceKind.Mic or AudioSourceKind.Mixed)
                    dshowMic = DeviceResolver.ResolveName(FfmpegDevices.ListAudio(), Require(micFragment, "mic"));

                // Same reason, same place, for the camera (issue #28): a fragment that names no
                // camera - or two - must fail BEFORE a recording directory exists, so an unknown
                // camera leaves nothing behind for the Library and the repair passes to find.
                string? dshowCamera = null;
                if (!string.IsNullOrWhiteSpace(cameraFragment))
                {
                    dshowCamera = DeviceResolver.ResolveCameraName(FfmpegDevices.ListVideo(), cameraFragment!);
                    Log.Info($"[RecordingService] StartVideo: camera \"{cameraFragment}\" resolved to \"{dshowCamera}\"");
                }
                _cameraName = dshowCamera;

                _dir = RecordingPaths.NewDir("video", "video");
                _manifest = NewManifest("video", mon);
                _manifest.VideoFile = "recording.mp4";
                _manifest.Region = region;
                // Named in the FIRST manifest, like every other file this session will write, so a
                // recording interrupted before its stop still says on disk that a camera track exists.
                if (dshowCamera != null) _manifest.CameraFile = "camera.mp4";

                // Mixed/system capture the system loopback and mux after; a mic-only recording
                // gets a post pass too (no loopback!) when any mic processing (suppression,
                // gate, leveling, volume) is on, so clean-voice applies to plain narration.
                bool needLoopback = src is AudioSourceKind.Mixed or AudioSourceKind.System;
                bool postMux = needLoopback || (src == AudioSourceKind.Mic && opts.MicProcessing);
                string finalPath = Path.Combine(_dir, "recording.mp4");
                string ffOut = postMux ? Path.Combine(_dir, "raw.mp4") : finalPath;
                if (postMux) _rawVideo = ffOut;

                _manifest.Microphone = src switch
                {
                    AudioSourceKind.Mixed => $"{dshowMic} + (system)",
                    AudioSourceKind.System => "(system)",
                    AudioSourceKind.Mic => dshowMic,
                    _ => null,
                };

                // The raw file names and the deferred-mux plan are known before ffmpeg writes a
                // frame (issue #155), so the recording's FIRST manifest can carry them - see
                // BuildPendingMux. The ffmpeg command line is not known until the process starts;
                // it is recorded at stop, which is the only field of this session that is.
                if (needLoopback) _sysWav = Path.Combine(_dir, "sys_native.wav");
                _manifest.PendingMux = BuildPendingMux(
                    "video", src, micWav: null, _sysWav, _rawVideo,
                    finalFile: finalPath, rawDurationSeconds: 0, opts);

                // The live preview taps (issue #33). Created BEFORE the writers because each one has
                // to exist when its ffmpeg starts: the preview output is only added to a command line
                // when there is a tap to drain the pipe it writes to, and an undrained pipe would
                // block the process recording the file.
                //
                // NOT ARMED, NOTHING CHANGES. With PreviewArmed false there is no tap, so no second
                // output, so the same command line, the same files and the same manifest this
                // recording had before the feature existed (AC11) - which is also the CLI's position,
                // since it never arms one.
                //
                // TryCreate returning null is the same complete answer for a machine that cannot host
                // a preview: record without one. A preview that cannot be prepared never stops a
                // recording from starting (AC10).
                //
                // AND IT CANNOT DELAY ONE EITHER (Review Gate round 2 on PR #39, defect 1). This is
                // the thread that starts the recording, and preparing a preview directory used to be
                // synchronous filesystem work right here - so a preview path that never answered (a
                // reparse point onto an unavailable share) meant the recording never started at all.
                // The preparing is done by PreviewChores on its own thread now, and the most this
                // line can cost is PreviewChores.BudgetMs before it gives up and records without a
                // preview.
                _screenTap = PreviewArmed ? PreviewTap.TryCreate(PreviewPaths.ScreenTrack) : null;
                _cameraTap = PreviewArmed && dshowCamera != null
                    ? PreviewTap.TryCreate(PreviewPaths.CameraTrack)
                    : null;
                // Issue #47: seed the preset's framing rather than clearing it. Clearing here was
                // what made the chosen corner a property of the preview WINDOW instead of the
                // recording - with no HUD open, the manifest got no framing and the composed video
                // could not be laid out.
                _previewOverlay = overlay?.Canonical();
                Log.Info($"[RecordingService] StartVideo: framing={(_previewOverlay == null ? "(none)" : _previewOverlay.ToString())}");
                Log.Info($"[RecordingService] StartVideo: preview armed={PreviewArmed} "
                         + $"screenTap={_screenTap != null} cameraTap={_cameraTap != null}");

                // Issue #155: the publish AND every writer start run inside ONE failure boundary
                // (RecordingStartSequence). ffmpeg is already writing frames by the time the loopback
                // can fail - a rollback that did not stop it left the screen being recorded while the
                // service reported idle.
                var steps = new List<RecordingStartStep>();

                // THE CAMERA STARTS FIRST, and that ordering is load-bearing (issue #28, AC9).
                //
                // A camera that cannot be opened - absent, or already held by another application -
                // fails the whole start (decision 3). The rollback may only remove a directory that
                // holds no capture bytes (RecordingStartSequence.Discard), so if the SCREEN recorder
                // had already started, its recording.mp4 and its ffmpeg log would keep the directory
                // alive and a failed start would leave an empty recording in the Library. Opening the
                // camera first means the only thing that exists at that moment is the first manifest,
                // which Discard is free to remove.
                if (dshowCamera != null)
                {
                    steps.Add(new RecordingStartStep("camera", () =>
                    {
                        // CONSTRUCTED AND STORED BEFORE FFMPEG EXISTS, and that ordering is the fix
                        // for issue #28 gate round 3, defect 1 - the same rule every other writer
                        // here already follows (issue #155): the field is set the moment the writer
                        // is constructed, so a writer whose start threw is still in LiveWriters and
                        // still gets stopped and disposed.
                        //
                        // While opening the camera was one static call, its failure threw before
                        // this assignment could complete. A camera whose open probe timed out and
                        // whose kill was REFUSED therefore left a live ffmpeg on the webcam that
                        // this service had no handle to: the rollback below stopped an _camera that
                        // was still null. Now Open() fails with the recorder already in the field,
                        // so the rollback retries the termination like any other writer.
                        _camera = FfmpegCameraRecorder.Create(
                            dshowCamera, cameraFps, 23, Path.Combine(_dir!, "camera.mp4"), _cameraTap);
                        _camera.Open();
                    }));
                }

                steps.Add(new RecordingStartStep("video", () =>
                {
                    _video = FfmpegRecorder.Start(
                        capture, src == AudioSourceKind.System ? null : dshowMic, fps, 23, ffOut, _screenTap);
                    _manifest!.FfmpegCommand = _video.CommandLine;

                    // The alignment hint (assumption A5): how far the camera start sits from the
                    // screen start, measured between the two Process.Start returns. Negative here,
                    // because the camera is opened first - see the comment above.
                    if (_camera != null)
                        _manifest.CameraStartOffsetSeconds =
                            Math.Round((_camera.StartedUtc - _video.StartedUtc).TotalSeconds, 3);
                }));

                // ffmpeg owns the mic stream in video mode, so nothing in-process sees the samples.
                // Open a monitor-only WaveIn capture (shared mode, no file) purely to drive the level
                // meter. The meter is auxiliary: if the WaveIn name does not resolve (dshow-only
                // fragment), log it loudly and record without one - so this step never fails the
                // start, and the recording it would have metered is not thrown away for it.
                if (src is AudioSourceKind.Mic or AudioSourceKind.Mixed)
                {
                    steps.Add(new RecordingStartStep("mic level meter", () =>
                    {
                        try
                        {
                            _audio = new AudioCapture(AudioCapture.ResolveDevice(micFragment!));
                            _audio.LevelChanged += p => _peakMic = p;
                            _audio.StartMonitor();
                        }
                        catch (Exception ex)
                        {
                            // The meter is auxiliary, but a half-started capture still owns the
                            // device. Dropping the reference without disposing it would hold the
                            // microphone open for the life of the process - the same "writer nobody
                            // shut down" defect this whole boundary exists for (issue #155) - and it
                            // escapes LiveWriters precisely because the field is being cleared here.
                            try { _audio?.Dispose(); }
                            catch (Exception disposeFailed)
                            {
                                Log.Error("[RecordingService] StartVideo: disposing the failed mic level meter failed", disposeFailed);
                            }
                            _audio = null;
                            Log.Warn($"mic level meter unavailable for this recording: {ex.Message}");
                        }
                    }));
                }

                if (needLoopback)
                {
                    steps.Add(new RecordingStartStep("system loopback", () =>
                    {
                        _loop = new LoopbackCapture();
                        _loop.LevelChanged += p => _peakSys = p;
                        _loop.Start(_sysWav!);
                    }));
                }

                StartSession(steps);

                _sw.Restart();
                _state = "recording";
                Log.Info($"start video src={src} dir={_dir} camera={dshowCamera ?? "(none)"}");
            }
        }

        // ---- marker screenshot during a session ---------------------------

        public string MarkerShot()
        {
            lock (_lock)
            {
                if (_state != "recording") throw new UsageException("not recording");
                var off = _sw.Elapsed;
                var rect = _mode == "video" ? _captureRect : _monitor!.Bounds;
                string shot = Path.Combine(_dir!, "shots", Timecodes.FileName(off));
                AgentEyes.Screenshot.CaptureRect(rect, shot, copyToClipboard: false);
                _manifest!.Shots.Add(new Manifest.ShotEntry
                {
                    OffsetSeconds = Math.Round(off.TotalSeconds, 2),
                    File = Path.Combine("shots", Path.GetFileName(shot)).Replace('\\', '/'),
                });
                return shot;
            }
        }

        // ---- stop & finalize ----------------------------------------------

        /// <summary>
        /// Stop the capture. Issue #77: the synchronous part does ONLY what makes the capture
        /// durable - stop ffmpeg + the WAV writers (which flushes the raw files to disk), record
        /// the deferred mux as <see cref="Manifest.PendingMux"/>, and write the manifest. It does
        /// NOT run the audio mux or any ffprobe duration probe (those scale with recording length);
        /// those move to <see cref="FinalizePending"/> on the background packaging pass. Returns as
        /// soon as the raw files + manifest are on disk, so the HUD / <c>/status</c> goes ready in
        /// fixed time regardless of how long the recording was.
        ///
        /// Issue #153 - the stop is FAILURE-ISOLATED. Every writer is stopped and disposed in its own
        /// protected block by <see cref="RecordingStopSequence"/>, so one that throws can no longer
        /// abandon the writers after it or the manifest save; the manifest is written even after a
        /// writer failed, because the raw bytes are already on disk and the manifest is what makes
        /// them recoverable. All failures are collected and then raised together as a
        /// <see cref="RecordingStopFailedException"/> - a failed stop is reported to the caller, kept
        /// in <see cref="LastStopFailure"/> and shown on <c>/status</c>, never silently turned into a
        /// clean idle. <see cref="RecordingStopped"/> fires only on a clean stop, as before.
        /// </summary>
        public RecordResult Stop()
        {
            Log.Info("stop: begin (synchronous raw save only)");
            // Flip to finalizing under lock, then flush the raw files outside the lock.
            AudioCapture? audio; LoopbackCapture? loop; FfmpegRecorder? video; FfmpegCameraRecorder? camera;
            string? micWav, sysWav, rawVideo, dir; Manifest? manifest; string mode; AudioSourceKind src; AudioMixOptions opts;
            CameraOverlaySettings? previewOverlay;
            lock (_lock)
            {
                if (_state != "recording") throw new UsageException("not recording");
                _state = "finalizing";
                // Taken with the rest of the session state so the framing written into the manifest
                // is the one this recording was framed with, not one a later recording chose.
                previewOverlay = _previewOverlay;
                audio = _audio; loop = _loop; video = _video; camera = _camera;
                micWav = _micWav; sysWav = _sysWav; rawVideo = _rawVideo;
                dir = _dir; manifest = _manifest; mode = _mode; src = _src; opts = _opts;
                _audio = null; _loop = null; _video = null; _camera = null;
                _micWav = _sysWav = _rawVideo = null;
            }
            _sw.Stop();
            double elapsed = _sw.Elapsed.TotalSeconds;

            string finalAudio = Path.Combine(dir!, "audio.wav");
            string finalVideo = Path.Combine(dir!, "recording.mp4");

            // Flush + close the raw capture. The instant these return the bytes are safe on disk;
            // this is the only durable work that has to happen synchronously. Issue #153: each writer
            // is its own named step, so one that throws stops neither the writers after it nor the
            // manifest save.
            var steps = new List<RecordingStopStep>();
            if (audio != null) steps.Add(new RecordingStopStep("audio", audio.Stop, audio.Dispose));
            if (loop != null) steps.Add(new RecordingStopStep("loopback", loop.Stop, loop.Dispose));
            if (video != null) steps.Add(new RecordingStopStep("video", video.Stop, video.Dispose));
            // The camera is its own writer with its own final file - it never joins the deferred
            // audio mux (assumption A4), so stopping it IS finalizing camera.mp4. Its Stop never
            // throws for a camera that already died mid-run (decision 4): that loss was reported when
            // it happened and must not turn an otherwise clean stop into a failed one.
            //
            // It DOES throw when ffmpeg survived the quit and the kill (issue #28, gate defect 2),
            // and that failure has to land here rather than be swallowed: the process still owns the
            // webcam, so this stop is a FAILED stop. The sequence collects it, the manifest is still
            // saved, and /status reports the failure instead of the service quietly going idle with
            // the camera still held.
            if (camera != null) steps.Add(new RecordingStopStep("camera", camera.Stop, camera.Dispose));

            bool deferred = false;
            void SaveManifest()
            {
                // The deferred mux, now with the measured duration. Built by the SAME helper the
                // start used, so the record written at stop can never describe different work from
                // the one already on disk. The raw files stay on disk until FinalizePending runs, so
                // a kill in the deferred window loses nothing.
                manifest!.PendingMux = BuildPendingMux(
                    mode, src, micWav, sysWav, rawVideo,
                    finalFile: mode == "audio" ? finalAudio : finalVideo,
                    rawDurationSeconds: elapsed, opts);
                deferred = manifest.PendingMux != null;

                manifest.DurationSeconds = Math.Round(elapsed, 2);

                // The framing the person settled on while recording (issue #33 AC5, issue #36 AC4).
                // EDIT METADATA: nothing was composited, nothing was cropped, and neither recorded
                // file was touched - camera.mp4 is the full rectangular frame whatever this says
                // (issue #36, AC5). Null when no overlay was framed, and a null field is not written
                // at all - so a recording made without the overlay has the manifest it always had
                // (issue #33 AC11 / issue #36 AC10).
                manifest.PreviewOverlayCorner = previewOverlay?.Corner;
                manifest.PreviewOverlayShape = previewOverlay?.Shape;
                manifest.PreviewOverlayInset = previewOverlay?.InsetFraction;
                // The circle is written only when the overlay WAS a circle. A rectangle overlay
                // frames the whole camera frame, so there is no circle to reproduce and the field
                // stays absent rather than carrying geometry nothing used.
                manifest.PreviewOverlayCircle =
                    previewOverlay is { } framing && framing.ShapeValue == CameraOverlayShape.Circle
                        ? framing.Circle.Clone()
                        : null;

                if (manifest.AudioFile != null && !manifest.Files.Contains(manifest.AudioFile)) manifest.Files.Add(manifest.AudioFile);
                if (manifest.VideoFile != null && !manifest.Files.Contains(manifest.VideoFile)) manifest.Files.Add(manifest.VideoFile);

                // The camera track's own account of itself (issue #28, spec amendment 2026-08-28).
                //
                // These are OBSERVATIONS, not conclusions: what ffmpeg said it wrote, how the
                // process actually ended, and whether its stderr was read to the end. CameraComplete
                // is the only judgement among them, and it is three-state precisely so that the
                // cases this code did not anticipate have somewhere honest to go instead of being
                // rounded to "complete", which is what the boolean it replaces did three times.
                if (camera != null)
                {
                    CameraTrackRecord.Write(manifest, camera);
                    if (camera.LostMidRun)
                        Log.Warn($"stop: the camera \"{camera.DeviceName}\" was lost during this recording - "
                                 + $"camera.mp4 covers {camera.CapturedSeconds:F1}s of a {elapsed:F1}s session; "
                                 + "the screen recording is unaffected");
                    else if (camera.Completeness != CameraCompleteness.Yes)
                        Log.Warn($"stop: the camera \"{camera.DeviceName}\" track is recorded as "
                                 + $"complete={CameraObservation.Text(camera.Completeness)} "
                                 + $"(stopKind={CameraObservation.Text(camera.StopKind) ?? "(not observed)"}, "
                                 + $"stderrComplete={camera.StderrComplete}) - camera.mp4 covers "
                                 + $"{camera.CapturedSeconds:F1}s of a {elapsed:F1}s session; the screen recording "
                                 + "is unaffected");
                }

                // Issue #155: this is NOT the recording's first manifest write - StartAudio /
                // StartVideo already wrote a valid record before the first byte was captured - so the
                // stop is a read-modify-write of that file, applying only the fields this session
                // owns. Two things follow, and both were defects when this was a whole-content
                // Replace of a copy held for the length of the recording:
                //  - an interrupted stop write leaves the START record intact, so the directory is
                //    still a live, parseable, recoverable recording rather than raw media with no
                //    manifest beside it;
                //  - anything written to this manifest DURING the recording (a rename in the
                //    Library, which is possible now that the recording is listed while it runs) is
                //    not erased by the stop.
                ManifestStore.Update(dir!, m =>
                {
                    m.DurationSeconds = manifest.DurationSeconds;
                    m.PendingMux = manifest.PendingMux;
                    m.FfmpegCommand = manifest.FfmpegCommand;
                    m.PreviewOverlayCorner = manifest.PreviewOverlayCorner;
                    m.PreviewOverlayShape = manifest.PreviewOverlayShape;
                    m.PreviewOverlayCircle = manifest.PreviewOverlayCircle;
                    m.PreviewOverlayInset = manifest.PreviewOverlayInset;
                    CameraTrackRecord.CopyTo(m, manifest);
                    // The shot index belongs to the session: only MarkerShot adds to it, and only
                    // while this session is running, so the session's list IS the truth for it.
                    m.Shots.Clear();
                    foreach (var shot in manifest.Shots) m.Shots.Add(shot);
                    foreach (string file in manifest.Files)
                        if (!m.Files.Contains(file)) m.Files.Add(file);
                });
            }

            RecordingStopReport report;
            RecordResult result;
            try
            {
                Log.Info($"stop: saving {(mode == "video" ? "video" : "audio")} (flush raw + writers)");
                report = RecordingStopSequence.Run(
                    dir!,
                    steps,
                    SaveManifest,
                    () => RecoveryManifest.Save(manifest!, elapsed, dir!));
                _lastStopFailure = report.Failed ? report : null;

                string? outFile = manifest!.VideoFile != null ? finalVideo : (manifest.AudioFile != null ? finalAudio : null);
                result = new RecordResult { Dir = dir!, File = outFile, DurationSeconds = Math.Round(elapsed, 2), Shots = manifest.Shots.Count };
            }
            finally
            {
                // The preview taps come down HERE - after the stop sequence, so every ffmpeg has
                // already closed its pipe and each pump has ended by itself, and before the session
                // fields are cleared. They are not stop steps: a preview that failed to close must
                // not be able to turn a clean recording into a failed stop (issue #33, AC10).
                //
                // THIS IS BEFORE THE SERVICE RETURNS TO IDLE, which is why every wait inside that
                // disposal is bounded (Review Gate round 2 on PR #39, defect 1). It used to log,
                // flush and delete synchronously on this thread, on the very preview path a wedged
                // publisher was already stuck in - so a share that stopped answering left Stop unable
                // to return and the service reporting "finalizing" forever.
                DisposePreviewTaps();

                RecordingClaimTicket claim;
                lock (_lock)
                {
                    _manifest = null; _dir = null; _monitor = null;
                    _cameraName = null;
                    _previewOverlay = null;
                    _peakMic = _peakSys = 0;
                    _state = "idle";

                    // Taken UNDER the lock, with the rest of the session state (issue #154, round 3).
                    // The instant _state goes back to "idle" another thread may start a recording,
                    // and BeginSession writes this same field: reading it after the lock could hand
                    // this stop the NEXT session's ticket and release a live capture's claim.
                    claim = _captureClaim;
                    _captureClaim = default;
                }

                // The capture no longer holds the recording (issue #155): the claim taken at start
                // kept every automatic pass off a directory that was still being written to, and the
                // post-recording sequence takes its own claim on the way in.
                //
                // Released through THIS session's ticket, not by directory name: releasing by name
                // removes whichever claim happens to be there, which is how a session that never
                // owned the directory used to remove the owner's claim. Released OUTSIDE the lock -
                // the release announces itself to the queue, and that fan-out must not run under the
                // service's state lock.
                //
                // UNLESS the camera ffmpeg is still running (issue #28, AC16), which is the one case
                // where "the capture no longer holds the recording" is false: a process AgentEyes
                // could not kill is still writing camera.mp4 into that directory. The owner keeps
                // the recorder AND the claim, and reports both on /status - because releasing here
                // would publish a live writer's directory to every automatic pass in the app, and
                // dropping the recorder would leave nothing in the process able to reach the ffmpeg.
                _stranded.ReleaseClaimUnlessStranded(camera, claim, dir);
            }

            // Issue #153: a stop that lost anything is reported as a failure. The service is idle
            // again (so the user can record), but LastStopFailure and /status say the last stop
            // failed, the recording keeps a manifest on disk for the recovery passes, and the caller
            // gets every failure rather than only the first. RecordingStopped is NOT raised - it
            // means "a session ended cleanly"; a caller waiting on work being finished is released by
            // PostRecording.WorkIdle instead.
            if (report.Failed)
            {
                Log.Error($"stop: FAILED dir={dir} dur={elapsed:F1}s manifest={(report.HasManifest ? "on disk" : "MISSING")} - {report.Summary()}");
                throw new RecordingStopFailedException(report);
            }

            Log.Info($"stop: return dir={dir} dur={elapsed:F1}s deferredMux={deferred} (HUD may close now)");

            // State is idle and the lock is released: safe to notify listeners (issue #107) that the
            // session ended, so a deferred update restart can now proceed.
            RecordingStopped?.Invoke();
            return result;
        }

        /// <summary>
        /// Issue #77: complete the deferred audio mux / system downmix recorded by <see cref="Stop"/>
        /// in <see cref="Manifest.PendingMux"/>. Runs the same ffmpeg work that the old synchronous
        /// stop ran (byte-for-byte equivalent output), probes the final duration, then clears the
        /// pending state and re-saves the manifest. Idempotent: a no-op when there is no pending mux
        /// (so a kill before this runs leaves the raw files intact and a later package still finishes
        /// the job). Called on the background packaging pass, ahead of transcription.
        /// </summary>
        public static void FinalizePending(string dir)
        {
            var manifest = Manifest.Load(dir);
            var p = manifest.PendingMux;
            if (p == null) { Log.Info($"finalize: no pending mux in {dir}"); return; }

            string finalPath = Path.Combine(dir, p.FinalFile);
            string? raw = p.RawVideo != null ? Path.Combine(dir, p.RawVideo) : null;
            string? sys = p.SysWav != null ? Path.Combine(dir, p.SysWav) : null;
            string? mic = p.MicWav != null ? Path.Combine(dir, p.MicWav) : null;
            var src = ParseSource(p.Source);
            double elapsed = p.RawDurationSeconds;

            Log.Info($"finalize: begin mux {p.Mode} src={p.Source} dir={dir}");
            if (p.Mode == "audio")
            {
                if (src == AudioSourceKind.System) Ffmpeg.Run(FfmpegArgs.ExtractWav(sys!, finalPath), "downmix loopback");
                else if (src == AudioSourceKind.Mixed) AudioMix.MixWavs(mic!, sys!, finalPath, p.Options);
            }
            else
            {
                if (src == AudioSourceKind.Mixed) { AudioMix.MuxVideoMixed(raw!, sys!, finalPath, p.Options); elapsed = SafeDur(finalPath, elapsed); }
                else if (src == AudioSourceKind.System) { AudioMix.MuxVideoSystemOnly(raw!, sys!, finalPath, p.Options.SystemGain); elapsed = SafeDur(finalPath, elapsed); }
                else if (src == AudioSourceKind.Mic && raw != null) { AudioMix.ProcessVideoMic(raw, finalPath, p.Options); elapsed = SafeDur(finalPath, elapsed); }
            }

            // Issue #155: a recording recovered from its START manifest has no measured duration -
            // the stop that would have written one never landed - so the muxed file itself is the
            // only source of truth for it. A normal stop records the elapsed time and this is a
            // no-op (the video branches above have always probed).
            if (elapsed <= 0) elapsed = SafeDur(finalPath, 0);

            // Issue #83: keep the untouched pre-processing capture (renamed to a ".original" backup)
            // instead of deleting it, so over-removal by the clean-voice chain is recoverable.
            var originals = OriginalBackup.Preserve(dir, p.Mode, src);

            // Issue #155: apply only what the mux produced, to the manifest as it reads NOW. The
            // load above happened before minutes of ffmpeg work; saving that copy would erase
            // anything written in between (a rename, an attempt counter, a stage record).
            ManifestStore.Update(dir, m =>
            {
                foreach (string name in originals)
                {
                    if (!m.OriginalFiles.Contains(name)) m.OriginalFiles.Add(name);
                    if (!m.Files.Contains(name)) m.Files.Add(name);
                }
                m.DurationSeconds = Math.Round(elapsed, 2);
                m.PendingMux = null;   // mux done; the final file now exists
            });
            Log.Info($"finalize: done dir={dir} dur={elapsed:F1}s");
        }

        // ---- helpers ------------------------------------------------------

        /// <summary>
        /// Publish the session that <see cref="StartAudio"/> / <see cref="StartVideo"/> has just set
        /// up: claim the directory, then write the recording's FIRST manifest - before any capture
        /// is started (issue #155).
        ///
        /// This is the fix for the stranding case the atomic write alone did not cover. The session
        /// used to keep its manifest in memory for the whole recording and write it once, at stop.
        /// That single write was therefore the FIRST write of the file, and a process death between
        /// the flushed temp and the rename left raw media plus a manifest.json.&lt;id&gt;.tmp with no
        /// manifest.json at all - a directory the Library excludes and every recovery pass skips,
        /// which is exactly the raw-media-only stranding issues #151/#152/#153 exist to prevent.
        /// With the record on disk first, the stop is an UPDATE of an existing file: an interrupted
        /// stop leaves the start record, and the recording stays live and recoverable
        /// (<see cref="Manifest.PendingMux"/> is already in it, so the deferred mux can still run).
        ///
        /// The claim is the other half of writing it early: a directory with a manifest.json is a
        /// recording to every scan in the app, and this one is still being written to. The claim is
        /// what keeps the automatic repair passes off it until <see cref="Stop"/> releases it.
        /// </summary>
        private void BeginSession()
        {
            Log.Info($"[RecordingService] BeginSession: dir={_dir}");

            // Issue #154: capture is what the repair passes must yield to, so starting one is
            // announced BEFORE any writer starts. A repair pass in flight sees the change at its next
            // stage boundary and stands down instead of running ffmpeg against this recording's
            // machine. This is how starting a recording interacts with the repair gate - capture
            // never waits for repair, repair yields to capture.
            //
            // THE ORDER OF THESE TWO LINES IS THE GUARD (issue #154, QA round 1). The claim is taken
            // FIRST and the epoch is bumped SECOND, and the pair is what makes
            // RepairService.CaptureYielded complete for a repair pass that reads the epoch at ANY
            // instant:
            //  - read before the claim -> the bump happens after the read, so ChangedSince is true
            //    from then on;
            //  - read between the claim and the bump -> the claim is already held, so
            //    RecordingWorkset.CaptureInProgress is true (and the bump still follows);
            //  - read after the bump -> the claim was taken before it and is held until Stop (or the
            //    start rollback) releases it, so CaptureInProgress is true for as long as this
            //    capture lives.
            // Announcing first left the reverse window - epoch already bumped, claim not yet taken -
            // in which a live capture was invisible to both signals. That window is not theoretical:
            // CaptureStarted writes a log line to disk before it returns.
            //
            // The three cases above hold while the claim is GRANTED - and this capture does not start
            // at all without it (issue #154, round 3).
            //
            // A refusal means something ELSE already owns this directory: the previous session's
            // pipeline, or a repair stage. It takes a directory-name collision to happen at all,
            // because RecordingPaths.NewDir stamps to the SECOND with no collision suffix, so two
            // recordings of the same mode started inside one second get the same directory. That NAME
            // defect is issue #169 and is not fixed here - what is fixed here is what this method does
            // when the premise it is built on is false.
            //
            // It used to log the refusal and carry on: bump the epoch, replace the owner's manifest,
            // start writers into the owner's directory - and then, at stop, run an unconditional
            // release that removed whichever claim was there, i.e. the OTHER owner's. Every one of
            // those is damage done to a recording this session does not own. So the start fails
            // instead, before anything is published and before any writer exists, and the rollback
            // that follows can only release a claim this attempt actually holds (the ticket) and
            // cannot remove a directory it did not create (RecordingStartSequence.Discard).
            if (!RecordingWorkset.TryClaim(_dir!, RecordingWorkKind.Capture, "capture session", out _captureClaim))
            {
                string owner = RecordingWorkset.OwnerDescription(_dir!) ?? "(released while being read)";
                Log.Error($"[RecordingService] BeginSession: {_dir} is already held by {owner} - this capture "
                    + "will NOT start. The directory name collided: check RecordingPaths.NewDir (issue #169).");
                throw new UsageException(
                    $"the recording folder {Path.GetFileName(_dir)} is already in use by {owner} - "
                    + "wait a second and start again");
            }

            CaptureSignal.CaptureStarted();

            ManifestStore.Replace(_dir!, _manifest!);   // a directory this call just created (issue #155)
        }

        /// <summary>
        /// Publish the session and start its writers inside one failure boundary (issue #155). See
        /// <see cref="RecordingStartSequence"/> for what that boundary covers and why the rollback
        /// order - writers down first, claim released second - is the whole point. The original
        /// exception is rethrown to the caller either way.
        /// </summary>
        private void StartSession(IReadOnlyList<RecordingStartStep> steps)
        {
            RecordingStartSequence.Run(_dir!, BeginSession, steps, LiveWriters, ReleaseSession);
        }

        /// <summary>
        /// The capture writers that exist RIGHT NOW, as stop steps. Read on the start-failure path,
        /// after the step that threw, so it reports exactly what has to be shut down - a field is set
        /// the moment its writer is constructed, so a writer whose Start threw is still in here and
        /// still gets stopped and disposed.
        /// </summary>
        private IReadOnlyList<RecordingStopStep> LiveWriters()
        {
            var steps = new List<RecordingStopStep>();
            if (_audio != null) steps.Add(new RecordingStopStep("audio", _audio.Stop, _audio.Dispose));
            if (_loop != null) steps.Add(new RecordingStopStep("loopback", _loop.Stop, _loop.Dispose));
            if (_video != null) steps.Add(new RecordingStopStep("video", _video.Stop, _video.Dispose));
            if (_camera != null) steps.Add(new RecordingStopStep("camera", _camera.Stop, _camera.Dispose));
            return steps;
        }

        /// <summary>
        /// Undo <see cref="BeginSession"/> when the start failed (issue #155). Called by
        /// <see cref="RecordingStartSequence"/> AFTER every writer has been stopped and disposed -
        /// never before, because releasing the claim publishes the directory to every automatic
        /// repair pass in the app.
        ///
        /// The session fields are cleared FIRST and the directory is given up second: the service is
        /// back to a clean idle even if removing the directory itself fails, and that failure is
        /// reported by the sequence rather than swallowed here.
        /// </summary>
        private void ReleaseSession()
        {
            string? dir = _dir;
            var claim = _captureClaim;
            var camera = _camera;
            _audio = null; _loop = null; _video = null; _camera = null;
            _micWav = _sysWav = _rawVideo = null;
            _cameraName = null;
            _dir = null; _manifest = null; _monitor = null;
            _captureClaim = default;
            _peakMic = _peakSys = 0;
            DisposePreviewTaps();
            _previewOverlay = null;

            if (dir == null) return;

            // The ticket is what says how much of this directory belongs to the failed start (issue
            // #154, round 3). A start that never won the claim owns NOTHING here - not the claim, not
            // the directory - and Discard must not release or remove either.
            //
            // And a failed start whose camera ffmpeg is STILL RUNNING owns something else again
            // (issue #28, AC16): a live process inside the directory this was about to delete. The
            // owner keeps that recorder, the claim and the directory, and reports the process with
            // its PID on /status. Deleting a directory around a live ffmpeg does not stop the
            // ffmpeg - it fails on the file it holds open and replaces "the camera is already in use"
            // with an IO error about camera.mp4.
            _stranded.DiscardDirectoryUnlessStranded(camera, dir, claim);
        }

        /// <summary>
        /// Shut the live preview taps down (issue #33). Called AFTER the writers are stopped, on both
        /// the stop path and the failed-start rollback, and deliberately OUTSIDE the stop/start step
        /// sequences: a preview problem must never be collected as a failure of the recording, and it
        /// must never be reported on the "this stop failed" surface (AC10).
        ///
        /// Order matters. A tap's Dispose joins its pump thread, and that thread only ends when
        /// ffmpeg closes the pipe - so disposing before the writers are stopped would wait on a
        /// process that is still recording.
        /// </summary>
        private void DisposePreviewTaps()
        {
            var screen = _screenTap;
            var camera = _cameraTap;
            _screenTap = null;
            _cameraTap = null;

            // An entry point for the preview's own failures: this is the last place they can be
            // reported, and the caller is either finishing a recording or already carrying a real
            // failure. Neither may be turned into a preview problem.
            try { screen?.Dispose(); }
            catch (Exception ex) { Log.Error("[RecordingService] DisposePreviewTaps: the screen preview tap failed to close", ex); }
            try { camera?.Dispose(); }
            catch (Exception ex) { Log.Error("[RecordingService] DisposePreviewTaps: the camera preview tap failed to close", ex); }
        }

        /// <summary>
        /// The deferred audio mux/downmix this session will need (issue #77), or null when the
        /// capture writes its final file directly (mic-only audio; mic-only video with no
        /// processing).
        ///
        /// Issue #155: this is built TWICE for one recording - at start into the first manifest with
        /// no duration yet, and at stop with the measured one - so it lives in ONE method and the two
        /// records cannot drift apart. Writing it at start is what makes an interrupted stop
        /// recoverable: without this block, raw.mp4 + sys_native.wav are bytes nothing knows how to
        /// turn into recording.mp4.
        /// </summary>
        private static Manifest.PendingMuxInfo? BuildPendingMux(
            string mode, AudioSourceKind src, string? micWav, string? sysWav, string? rawVideo,
            string finalFile, double rawDurationSeconds, AudioMixOptions opts)
        {
            bool deferred = mode == "audio"
                ? src is AudioSourceKind.System or AudioSourceKind.Mixed
                // mic-only video with no processing: ffmpeg wrote recording.mp4 directly.
                : src is AudioSourceKind.System or AudioSourceKind.Mixed
                  || (src == AudioSourceKind.Mic && rawVideo != null);
            if (!deferred) return null;

            return new Manifest.PendingMuxInfo
            {
                Mode = mode,
                Source = src.ToString().ToLowerInvariant(),
                RawVideo = rawVideo != null ? Path.GetFileName(rawVideo) : null,
                MicWav = micWav != null ? Path.GetFileName(micWav) : null,
                SysWav = sysWav != null ? Path.GetFileName(sysWav) : null,
                FinalFile = Path.GetFileName(finalFile),
                RawDurationSeconds = Math.Round(rawDurationSeconds, 2),
                Options = opts,
            };
        }

        private void Reset(MonitorInfo mon, string mode, AudioSourceKind src, AudioMixOptions opts)
        {
            _monitor = mon; _mode = mode; _src = src; _opts = opts;
            _peakMic = _peakSys = 0;
            _micWav = _sysWav = _rawVideo = null;
            // A tap belongs to exactly one session (issue #33). Both exits from a session - the stop
            // and the failed-start rollback - already close them, so this normally closes nothing;
            // it is here so that a tap which somehow outlived its session cannot be inherited by the
            // next one and show it a picture of the last recording.
            DisposePreviewTaps();
            _previewOverlay = null;
            // Issue #153: the previous stop's failure belongs to the previous recording. It is
            // reported until a new one starts, and this is that moment.
            _lastStopFailure = null;
        }

        private static string Require(string? value, string what) =>
            string.IsNullOrWhiteSpace(value) ? throw new UsageException($"{what} is required for this source") : value!;

        private static Manifest NewManifest(string mode, MonitorInfo m) => new()
        {
            Mode = mode,
            Label = mode,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            MonitorIndex = m.Index,
            MonitorName = m.Name,
        };

        private static double SafeDur(string path, double fallback)
        {
            try { double d = MediaProbe.DurationSeconds(path); return d > 0 ? d : fallback; } catch { return fallback; }
        }

        public static AudioSourceKind ParseSource(string? s) => (s ?? "").ToLowerInvariant() switch
        {
            "mic" => AudioSourceKind.Mic,
            "system" => AudioSourceKind.System,
            "mixed" => AudioSourceKind.Mixed,
            "none" => AudioSourceKind.None,
            _ => AudioSourceKind.Mixed,
        };
    }
}
