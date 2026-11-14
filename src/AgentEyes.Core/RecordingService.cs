using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public double MicLevel { get; set; }           // 0..1 (peak) - the microphone alone
        public double SystemLevel { get; set; }        // 0..1 (peak) - system sound alone
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

        // ---- capture liveness (issue #113) ---------------------------------
        //
        // POSITIVE EVIDENCE THAT FRAMES ARE BEING WRITTEN. ElapsedSeconds and Level are not that
        // evidence: both kept moving for 18 hours on 2026-11-12 while the video file had stopped
        // growing two minutes in. These are read off the output file itself by the capture
        // watchdog, every two seconds. All null when no screen capture is running.

        // "starting" (no output yet), "growing", "not-growing" (did not grow at the latest check,
        // not yet a fault) or "faulted" (the watchdog is ending the recording).
        public string? CaptureLiveness { get; set; }

        // The file being watched (raw.mp4 or recording.mp4).
        public string? CaptureFile { get; set; }

        // Bytes in that file at the latest check. Climbs on a live capture; stands still on a dead one.
        public long? CaptureBytes { get; set; }

        // Wall-clock UTC time the file was last seen to grow, and how long ago that was.
        public DateTime? CaptureLastGrowthUtc { get; set; }
        public double? CaptureSecondsSinceGrowth { get; set; }

        // The capture fault of the most recent recording, kept until the next recording starts -
        // the same lifetime as LastStopFailed. "stalled: raw.mp4 stopped growing at ..." when the
        // watchdog ended it; "behind: ..." when the stop found its output short of the take. Null
        // for a recording that wrote everything it was asked to.
        public string? LastCaptureFault { get; set; }
        public DateTime? LastCaptureFaultUtc { get; set; }
        public string? LastCaptureFaultDir { get; set; }
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

        // ---- the capture watchdog (issue #113) ------------------------------
        //
        // Started with every screen recording, stopped by Stop. Its timer is one-shot and re-armed
        // after each check under _lock, so two checks never overlap and none is armed after Stop.
        private CaptureWatchdog? _watchdog;
        private System.Threading.Timer? _watchdogTimer;
        private volatile CaptureLiveness? _liveness;
        private string? _watchedFile;

        // Set by the watchdog, under _lock, the moment it decides to end the recording; consumed by
        // Stop under the same lock, which is what makes the verdict reach the manifest and /status
        // whichever of the watchdog and a user stop gets there first.
        private CaptureFault? _pendingFault;

        // The most recent recording's capture fault, for /status, until the next start.
        private volatile CaptureFault? _lastCaptureFault;
        private volatile string? _lastCaptureFaultDir;

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
        /// Record the overlay framing the camera is being watched in (issue #33 AC5, issue #36 AC4).
        /// Sticky for the session: the LAST framing chosen is what reaches manifest.json at the stop,
        /// because that is the framing the person settled on.
        ///
        /// NULL MEANS "THE PREVIEW HAS NOTHING TO SAY", NOT "THERE IS NO FRAMING" (issue #51). The
        /// PRESET is the source of truth - it is what the person chose before recording, and since
        /// issue #47 it is seeded at StartVideo. The HUD is a refinement channel on top of that, and
        /// it reports null whenever its preview PANEL is simply not showing, which is the default.
        ///
        /// Assigning that null straight through is what broke the composed video in v1.8.0: the HUD's
        /// apply-at-construction landed about 1.5 seconds after the start and erased the preset's
        /// framing, so the stop wrote no framing, NeedsCompose returned false, and camera.mp4 was left
        /// beside recording.mp4 exactly as it had been before the feature shipped. Hiding a monitor
        /// panel is a choice about a preview, never about the output video.
        ///
        /// It writes EDIT METADATA and nothing else - it composites nothing, crops nothing, and
        /// changes no recorded file. The caller's object is COPIED and canonicalised here, so a later
        /// edit in the HUD cannot reach back and rewrite what this recording was framed with, and an
        /// unrecognised spelling can never reach the manifest. Safe when there is no recording; the
        /// value is simply dropped at the next start, which is also where a framing is cleared - the
        /// session boundary, not a passing null.
        /// </summary>
        public void SetPreviewOverlay(CameraOverlaySettings? overlay)
        {
            var copy = overlay?.Canonical();
            if (copy == null)
            {
                Log.Info("[RecordingService] SetPreviewOverlay: (none) - the preview is not framing "
                         + $"anything; keeping {(_previewOverlay == null ? "(no framing)" : _previewOverlay.ToString())}");
                return;
            }

            Log.Info($"[RecordingService] SetPreviewOverlay: overlay={copy}");
            _previewOverlay = copy;
        }

        public RecordStatus Status()
        {
            var failure = _lastStopFailure;
            // Liveness is reported only while a screen capture is live; once the service is idle
            // there is nothing being written for it to describe.
            var live = _state == "idle" ? null : _liveness;
            var captureFault = _lastCaptureFault;

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
                MicLevel = Math.Round(_peakMic, 3),
                SystemLevel = Math.Round(_peakSys, 3),
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
                CaptureLiveness = live?.State,
                CaptureFile = live == null ? null : _watchedFile,
                CaptureBytes = live?.Bytes,
                CaptureLastGrowthUtc = live?.LastGrowthUtc,
                CaptureSecondsSinceGrowth = live?.SecondsSinceGrowth,
                LastCaptureFault = captureFault?.ToString(),
                LastCaptureFaultUtc = captureFault?.DetectedUtc,
                LastCaptureFaultDir = captureFault == null ? null : _lastCaptureFaultDir,
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

                // Issue #47, AC1, and the half of it the Review Gate found missing (round 1,
                // defect 5). The framing was only ever written to the manifest by the STOP path, so
                // a recording killed before it stopped had a durable manifest naming camera.mp4 with
                // no framing beside it - and recovery, which decides from that manifest, then saw
                // nothing to compose. The framing is known before the first byte is captured, so it
                // belongs in the first manifest with everything else that is known then. The stop
                // still writes the final value, which may have been refined in the HUD meanwhile.
                if (dshowCamera != null && overlay != null)
                {
                    var framing = overlay.Canonical();
                    _manifest.PreviewOverlayCorner = framing.Corner;
                    _manifest.PreviewOverlayShape = framing.Shape;
                    _manifest.PreviewOverlayInset = framing.InsetFraction;
                    _manifest.PreviewOverlayCircle =
                        framing.ShapeValue == Preview.CameraOverlayShape.Circle
                            ? framing.Circle.Clone()
                            : null;
                    Log.Info($"[RecordingService] StartVideo: framing recorded at start - {framing}");
                }

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
                        capture, src == AudioSourceKind.System ? null : dshowMic, fps, 23, ffOut, _screenTap,
                        screenNumber: _monitor!.Index);
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
                StartWatchdog(_video!);
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
            CaptureFault? fault;
            lock (_lock)
            {
                if (_state != "recording") throw new UsageException("not recording");
                _state = "finalizing";
                // Taken with the rest of the session state so the framing written into the manifest
                // is the one this recording was framed with, not one a later recording chose.
                previewOverlay = _previewOverlay;
                audio = _audio; loop = _loop; video = _video; camera = _camera;
                micWav = _micWav; sysWav = _sysWav; rawVideo = _rawVideo;
                // Issue #113: down BEFORE the writers stop, and the watchdog's verdict (if it is what
                // is ending this recording) taken with the rest of the session.
                StopWatchdog();
                fault = _pendingFault;
                _pendingFault = null;
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
            // A capture the watchdog judged dead is abandoned rather than drained (issue #113): there
            // is no pipeline to wait for.
            if (video != null)
                steps.Add(new RecordingStopStep("video",
                    fault != null ? () => video.Abandon(fault.ToString()) : video.Stop, video.Dispose));
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
            // What the stop reports back to its caller - the same honest duration the manifest gets
            // (issue #113), not the session length when the capture saved less.
            double reportedDuration = Math.Round(elapsed, 2);
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

                // Issue #113: the screen capture's own account of what it wrote. A recording is
                // never longer than its file - the 2026-11-14 Linux recording reported 6.46 s and
                // saved 1.43 s, because this used to be wall time alone. Healthy captures drain to
                // the take, so for them this changes nothing.
                if (video != null)
                {
                    // After an ABANDONED capture ffmpeg's last progress report runs ahead of the file
                    // by up to one fragment (about two seconds) - the fragment it was building when it
                    // was killed never reached the disk. Measured 2026-11-14: reported 20.94 s, file
                    // 18.73 s. So the file is asked instead. This is the watchdog's thread, with no
                    // user waiting on it, so the probe does not slow any stop a person makes (issue
                    // #77); an unreadable file probes as 0, which is the honest answer for it.
                    double captured = fault != null
                        ? Math.Round(MediaProbe.DurationSeconds(video.OutputPath), 2)
                        : Math.Round(video.MediaSeconds, 2);
                    if (fault != null)
                        Log.Info($"stop: abandoned capture - {Path.GetFileName(video.OutputPath)} probes at {captured:F2}s "
                                 + $"(ffmpeg last reported {video.MediaSeconds:F2}s)");
                    manifest.CapturedSeconds = captured;

                    var recorded = fault
                        ?? BehindAtStop(Path.GetFileName(video.OutputPath), captured, elapsed, DateTime.UtcNow);
                    // A recording is never recorded as longer than its file when the capture failed.
                    if (recorded != null && captured < manifest.DurationSeconds)
                    {
                        Log.Warn($"stop: the screen capture wrote {captured:F2}s of a {elapsed:F2}s session - "
                                 + $"recording DurationSeconds={captured:F2}, not the session length");
                        manifest.DurationSeconds = captured;
                    }
                    reportedDuration = manifest.DurationSeconds;

                    if (recorded != null)
                    {
                        manifest.CaptureFault = recorded.Kind;
                        manifest.CaptureFaultDetail = recorded.Message;
                        manifest.CaptureFaultDetectedUtc = recorded.DetectedUtc;
                        manifest.CaptureFaultElapsedSeconds = Math.Round(recorded.ElapsedSeconds, 1);
                        _lastCaptureFault = recorded;
                        _lastCaptureFaultDir = dir;
                    }
                }

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
                    m.CapturedSeconds = manifest.CapturedSeconds;
                    m.CaptureFault = manifest.CaptureFault;
                    m.CaptureFaultDetail = manifest.CaptureFaultDetail;
                    m.CaptureFaultDetectedUtc = manifest.CaptureFaultDetectedUtc;
                    m.CaptureFaultElapsedSeconds = manifest.CaptureFaultElapsedSeconds;
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
                    () => RecoveryManifest.Save(manifest!, elapsed, dir!),
                    fault == null
                        ? null
                        : new[] { new RecordingStopFailure("capture watchdog", new CaptureFaultException(fault)) });
                _lastStopFailure = report.Failed ? report : null;

                string? outFile = manifest!.VideoFile != null ? finalVideo : (manifest.AudioFile != null ? finalAudio : null);
                result = new RecordResult { Dir = dir!, File = outFile, DurationSeconds = reportedDuration, Shots = manifest.Shots.Count };
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

        /// <summary>
        /// Issue #113: how far short of the session a capture may end before the take is recorded as
        /// short. A healthy capture trails wall time by its start-up and pipeline latency - about a
        /// second measured on Linux on 2026-11-14 - so a smaller gap is that latency, not lost video.
        /// The 2026-11-14 Linux recording this exists for was 5.13 s short (1.33 s written of 6.46 s).
        /// </summary>
        internal const double BehindToleranceSeconds = 3.0;

        /// <summary>
        /// Issue #113: a capture that was still running at the stop but had written well short of the
        /// session - the shape of the 2026-11-14 Linux recording ("pipeline did not reach 6860ms within
        /// 5s (reached 0ms)"), which was then finalised with the full session's duration. Decided from
        /// <paramref name="capturedSeconds"/> against <paramref name="sessionSeconds"/> only, so it
        /// does not depend on the stop drain's internals. Null when the take is whole.
        /// </summary>
        internal static CaptureFault? BehindAtStop(string fileName, double capturedSeconds, double sessionSeconds, DateTime nowUtc)
        {
            double shortBy = sessionSeconds - capturedSeconds;
            if (shortBy <= BehindToleranceSeconds) return null;
            return new CaptureFault("behind",
                $"the capture wrote {capturedSeconds:F2}s of a {sessionSeconds:F2}s session; "
                + $"{fileName} is short by {shortBy:F2}s",
                nowUtc, sessionSeconds);
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
            // Issue #113: the same for the previous recording's capture fault.
            _lastCaptureFault = null;
            _lastCaptureFaultDir = null;
            _pendingFault = null;
            _liveness = null;
        }

        // ---- the capture watchdog (issue #113) -----------------------------

        /// <summary>
        /// Start watching the screen capture that has just started. Called under <see cref="_lock"/>
        /// once the recording is live. The first check runs one interval later; each check re-arms
        /// the next, so checks never overlap.
        /// </summary>
        private void StartWatchdog(FfmpegRecorder video)
        {
            string path = video.OutputPath;
            _watchedFile = Path.GetFileName(path);
            _watchdog = new CaptureWatchdog(
                _watchedFile, () => CaptureWatchdog.FileSize(path), () => video.ExitCode, new SystemCaptureClock());
            _liveness = _watchdog.Last;
            _watchdogTimer = new System.Threading.Timer(
                _ => OnWatchdogTick(), null, CaptureWatchdog.DefaultCheckInterval, System.Threading.Timeout.InfiniteTimeSpan);
            Log.Info($"[RecordingService] StartWatchdog: watching {path} (stall {_watchdog.StallTimeout.TotalSeconds:F0}s, "
                     + $"start-up {_watchdog.StartupTimeout.TotalSeconds:F0}s, every "
                     + $"{CaptureWatchdog.DefaultCheckInterval.TotalSeconds:F0}s)");
        }

        /// <summary>
        /// Take the watchdog down. Called under <see cref="_lock"/> by Stop, BEFORE any writer is
        /// stopped: a capture being stopped on purpose stops growing, and that must not be read as a
        /// stall. Disposing a one-shot timer from inside its own callback is safe.
        /// </summary>
        private void StopWatchdog()
        {
            var timer = _watchdogTimer;
            _watchdogTimer = null;
            _watchdog = null;
            timer?.Dispose();
            if (timer != null) Log.Info("[RecordingService] StopWatchdog: capture watchdog stopped");
        }

        /// <summary>
        /// One watchdog check (timer thread - an entry point, so it catches). Reads the capture's
        /// file, publishes the reading for /status, and on a fault ends the recording.
        /// </summary>
        private void OnWatchdogTick()
        {
            try
            {
                CaptureWatchdog? watchdog;
                lock (_lock)
                {
                    if (_state != "recording") return;
                    watchdog = _watchdog;
                }
                if (watchdog == null) return;

                // Outside the lock: a stat of one file, but on a wedged disk even that can block, and
                // /status must keep answering meanwhile.
                var live = watchdog.Check();

                lock (_lock)
                {
                    // Published, re-armed and acted on only while this is still the LIVE session's
                    // watchdog: a Stop (and even a new Start) may have run during the check, and a
                    // reading or a verdict about the previous capture must not land on the next one.
                    if (_state != "recording" || !ReferenceEquals(_watchdog, watchdog)) return;
                    _liveness = live;
                    if (live.Fault == null)
                    {
                        _watchdogTimer?.Change(CaptureWatchdog.DefaultCheckInterval, System.Threading.Timeout.InfiniteTimeSpan);
                        return;
                    }
                }

                EndFaultedCapture(watchdog, live.Fault);
            }
            catch (Exception ex)
            {
                Log.Error("[RecordingService] OnWatchdogTick FAILED - the capture watchdog stopped checking", ex);
            }
        }

        /// <summary>
        /// The watchdog decided the capture is producing nothing: say so in the log NOW, at
        /// detection time (issue #113, AC7), then end the recording through the ordinary stop, which
        /// records the fault in the manifest and on /status and saves what was captured.
        /// </summary>
        private void EndFaultedCapture(CaptureWatchdog watchdog, CaptureFault fault)
        {
            string? dir;
            lock (_lock)
            {
                if (_state != "recording" || !ReferenceEquals(_watchdog, watchdog))
                {
                    Log.Info($"[RecordingService] EndFaultedCapture: the recording already ended - not acting on {fault}");
                    return;
                }
                _pendingFault = fault;
                dir = _dir;
            }

            Log.Error($"[RecordingService] CAPTURE FAULT at elapsed={_sw.Elapsed.TotalSeconds:F1}s "
                      + $"(detected {fault.DetectedUtc:yyyy-MM-ddTHH:mm:ss.fffZ}) dir={dir} - {fault} - "
                      + "ending the recording now and keeping what was captured");

            // Post-recording work is in flight from the moment this stop is decided (issue #152's
            // rule, the same ticket RecordingStop.Keep takes), so nothing restarts the process between
            // the capture ending and the mux starting.
            var work = PostRecording.TrackWork("capture fault " + (dir ?? "(unknown)"));
            bool queued = false;
            try
            {
                Stop();
                Log.Info($"[RecordingService] EndFaultedCapture: recording ended dir={dir}");
            }
            catch (RecordingStopFailedException ex)
            {
                // The expected outcome: the fault leads the stop report, so this stop is a FAILED stop
                // on /status (LastStopFailed) - the recording lost everything after the stall.
                Log.Warn($"[RecordingService] EndFaultedCapture: recording ended as a failed stop dir={dir} - {ex.Message}");

                // When the verdict is the ONLY failure, every writer stopped and the manifest is on
                // disk: what was captured is as good as any kept recording, so it is finished now
                // (mux, thumbnail, transcript) exactly as a user stop would. Any OTHER failure leaves
                // it to the periodic recovery pass, as RecordingStop.Keep does for every failed stop.
                bool onlyTheVerdict = ex.Report.HasManifest
                    && ex.Report.Failures.All(f => f.Error is CaptureFaultException);
                if (onlyTheVerdict && ex.Dir is { } stoppedDir)
                {
                    Log.Info($"[RecordingService] EndFaultedCapture: queuing post-processing for {stoppedDir}");
                    queued = true;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { PostRecording.Run(stoppedDir); }
                        catch (Exception postEx) { Log.Error($"[RecordingService] EndFaultedCapture: post-processing FAILED dir={stoppedDir}", postEx); }
                        finally { work.Dispose(); }
                    });
                }
                else
                {
                    Log.Warn($"[RecordingService] EndFaultedCapture: other stop failures besides the verdict - "
                             + $"leaving {dir} to the recovery pass");
                }
            }
            catch (UsageException ex)
            {
                // A user stop won the race; it took the verdict with it (_pendingFault is read under
                // the lock by whichever Stop ran) and queued its own post-processing.
                Log.Info($"[RecordingService] EndFaultedCapture: {ex.Message} - a stop got there first");
            }
            finally
            {
                if (!queued) work.Dispose();
            }
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
