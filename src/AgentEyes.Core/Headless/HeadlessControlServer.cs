using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.Headless
{
    /// <summary>
    /// The Control API without the WPF app (issue #102): a loopback-only HttpListener over the shared
    /// <see cref="RecordingService"/>, for the headless service (<c>agenteyes serve</c>). It serves the
    /// recording subset of the app's RestServer with the SAME JSON shapes - /status is
    /// <see cref="RecordingService.Status"/> itself - so a client of one drives the other. Presets,
    /// captures, always-on, housekeeping and plugins are app features and are not served here.
    ///
    /// A stop goes through <see cref="RecordingStop.Keep"/>, the one stop operation every host uses
    /// (issue #151): the raw files are saved before the answer, then mux, thumbnail, hosted
    /// transcription and titling run in the background exactly as they do in the app.
    /// </summary>
    internal sealed class HeadlessControlServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly HttpListener _listener = new();
        private readonly RecordingService _svc;
        private readonly object _postGate = new();
        private readonly List<Task> _postWork = new();
        private Thread? _thread;
        private volatile bool _running;

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}/";

        public HeadlessControlServer(RecordingService svc, int port)
        {
            _svc = svc ?? throw new ArgumentNullException(nameof(svc));
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "not a TCP port");
            Port = port;
            _listener.Prefixes.Add(Url);
        }

        public void Start()
        {
            Log.Info($"[HeadlessControlServer] Start: binding {Url}");
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Log.Error($"[HeadlessControlServer] Start FAILED: {Url}", ex);
                throw new UsageException(
                    $"the Control API could not listen on {Url} ({ex.Message}). Another AgentEyes (or another "
                    + "program) is already using port " + Port + ". Stop it, or set \"Port\" in "
                    + Path.Combine(AppDataPaths.Root, "config.json") + ".", ex);
            }
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "AgentEyes-headless-api" };
            _thread.Start();
            Log.Info($"[HeadlessControlServer] Start: listening on {Url}");
        }

        /// <summary>The post-recording work started by stops that has not finished yet.</summary>
        public Task[] PendingPostProcessing()
        {
            lock (_postGate)
            {
                _postWork.RemoveAll(t => t.IsCompleted);
                return _postWork.ToArray();
            }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    if (!_running) return;
                    Log.Error("[HeadlessControlServer] Loop: accepting a request FAILED", ex);
                    continue;
                }
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    Log.Error("[HeadlessControlServer] request handler FAILED", ex);
                    Error(ctx, 500, ex.Message, "internal");
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string method = ctx.Request.HttpMethod.ToUpperInvariant();
            string path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
            if (path == "") path = "/";
            Log.Info($"[HeadlessControlServer] {method} {path}");

            try
            {
                switch (method, path)
                {
                    case ("GET", "/"): Json(ctx, Discovery()); return;
                    case ("GET", "/health"): Json(ctx, new { ok = true, app = "AgentEyes", host = "headless", platform = PlatformName() }); return;
                    case ("GET", "/version"): Json(ctx, new { app = "AgentEyes", version = AppVersion() }); return;
                    case ("GET", "/status"): Json(ctx, _svc.Status()); return;
                    case ("GET", "/devices"): Json(ctx, Devices()); return;
                    case ("GET", "/account"): Json(ctx, Account()); return;
                    case ("GET", "/recordings"): Json(ctx, Recordings(ctx)); return;
                    case ("POST", "/record/start"):
                        StartFrom(ControlApiRequest.Parse(ReadBody(ctx)));
                        Json(ctx, _svc.Status());
                        return;
                    case ("POST", "/record/shot"):
                        Json(ctx, new { file = _svc.MarkerShot() });
                        return;
                    case ("POST", "/record/stop"):
                        Json(ctx, Stop());
                        return;
                }

                if (method == "GET" && path.StartsWith("/recordings/", StringComparison.Ordinal) && RecordingSubroute(ctx, path)) return;

                Error(ctx, 404, "no such endpoint: " + path, "not_found");
            }
            catch (UsageException ux)
            {
                bool conflict = ux.Message.Contains("already recording") || ux.Message.Contains("not recording");
                if (conflict) Error(ctx, 409, ux.Message, "conflict");
                else Error(ctx, 400, ux.Message, "bad_request");
            }
            catch (PlatformNotSupportedException px)
            {
                Error(ctx, 501, px.Message, "not_supported");
            }
        }

        private void StartFrom(JsonElement body)
        {
            var r = ControlApiRequest.ReadStart(body);
            Log.Info($"[HeadlessControlServer] StartFrom: mode={r.Mode} screen={r.Screen} source={r.Source} "
                     + $"mic={r.Mic ?? "(none)"} camera={r.Camera ?? "(none)"} fps={r.Fps}");
            if (r.Mode == "shot") throw new UsageException("mode 'shot' is not a recording - the headless host takes marker shots with POST /record/shot");
            if (r.Mode == "audio")
            {
                _svc.StartAudio(r.Screen, r.Source, r.Mic, r.Options);
                return;
            }
            if (r.Mode != "video") throw new UsageException($"unknown mode '{r.Mode}' (use 'video' or 'audio')");
            _svc.StartVideo(r.Screen, r.Source, r.Mic, r.Region, r.Options, r.Fps, r.Camera, r.CameraFps, overlay: null);
        }

        private RecordResult Stop()
        {
            var stopped = RecordingStop.Keep(_svc);
            lock (_postGate)
            {
                _postWork.RemoveAll(t => t.IsCompleted);
                _postWork.Add(stopped.PostProcessing);
            }
            Log.Info($"[HeadlessControlServer] Stop: {stopped.Result.Dir} saved; post-processing running in the background");
            return stopped.Result;
        }

        private bool RecordingSubroute(HttpListenerContext ctx, string path)
        {
            string[] seg = path.Substring("/recordings/".Length).Split('/');
            string id = Uri.UnescapeDataString(seg[0]);
            if (string.IsNullOrEmpty(id)) return false;

            if (seg.Length == 1)
            {
                var d = RecordingLibrary.GetDetail(id);
                if (d == null) { Error(ctx, 404, "no recording with id: " + id, "not_found"); return true; }
                Json(ctx, new
                {
                    id = d.Id, dir = d.Dir,
                    hasVideo = d.HasVideo, hasAudio = d.HasAudio, hasTranscript = d.HasTranscript,
                    hasFlatTranscript = d.HasFlatTranscript,
                    languages = d.Manifest.Transcripts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                    manifest = d.Manifest,
                });
                return true;
            }

            if (seg.Length == 2 && seg[1] == "transcript")
            {
                var t = RecordingLibrary.GetTranscript(id);
                if (t == null) { Error(ctx, 404, "no transcript for recording: " + id, "not_found"); return true; }
                Json(ctx, new
                {
                    text = t.Text,
                    segments = t.Segments.Select(g => new { start = g.Start, end = g.End, text = g.Text }),
                });
                return true;
            }

            if (seg.Length == 3 && seg[1] == "frame")
            {
                Frame(ctx, id, seg[2]);
                return true;
            }

            return false;
        }

        /// <summary>A still from the recording at an offset - what walkthrough.html's on-demand
        /// frames point at (LocalAppConfig.FrameUrl).</summary>
        private static void Frame(HttpListenerContext ctx, string id, string offsetText)
        {
            var d = RecordingLibrary.GetDetail(id);
            if (d == null) { Error(ctx, 404, "no recording with id: " + id, "not_found"); return; }
            string? video = !string.IsNullOrEmpty(d.Manifest.VideoFile) && File.Exists(Path.Combine(d.Dir, d.Manifest.VideoFile))
                ? Path.Combine(d.Dir, d.Manifest.VideoFile)
                : null;
            if (video == null) { Error(ctx, 404, "no video for recording " + id, "not_found"); return; }
            if (!double.TryParse(offsetText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double offset) || offset < 0)
            {
                Error(ctx, 400, "offset must be a non-negative number of seconds, got: " + offsetText, "bad_request");
                return;
            }

            byte[] bytes = VideoFrame.ExtractJpeg(video, offset);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static object Discovery() => new
        {
            app = "AgentEyes",
            host = "headless",
            endpoints = new[]
            {
                "GET /version", "GET /health", "GET /status", "GET /devices", "GET /account",
                "GET /recordings {limit?, offset?}", "GET /recordings/{id}", "GET /recordings/{id}/transcript",
                "GET /recordings/{id}/frame/{offset}",
                "POST /record/start {mode?:video|audio, screen?, source?:mic|system|mixed|none, mic?, camera?, cameraFps?, region?, denoise?, gate?, level?, micVol?, sysVol?, fps?}",
                "POST /record/shot", "POST /record/stop",
            },
        };

        private static object Devices()
        {
            var mons = Monitors.All().Select(m => new { m.Index, m.Width, m.Height, m.Primary, m.Name, m.X, m.Y });
            var mics = AudioCapture.Devices().Select(d => new { d.Number, d.Name });
            return new
            {
                monitors = mons,
                mics,
                dshow = FfmpegDevices.ListAudio(),
                cameras = FfmpegDevices.ListVideo(),
                systemAudio = SystemAudioName(),
            };
        }

        private static string SystemAudioName()
        {
#if WINDOWS
            return "(WASAPI loopback of the default output)";
#elif MACOS
            // macOS has no system-audio input of its own; it needs a virtual device the user
            // installed. Reported either way, because /devices answers "what is here", and on a Mac
            // "nothing carries the system output" is the common and correct answer.
            var device = Platform.Mac.MacSystemAudio.Find(Platform.Mac.AvfDevices.Read().Audio, out string why);
            return device != null
                ? $"{device.Value.Name} (AVFoundation :{device.Value.Index})"
                : "(unavailable: " + why + " macOS needs a virtual audio device such as BlackHole)";
#else
            var monitor = Platform.Linux.PulseDevices.RequireSystemAudioSource();
            return $"{monitor.Description} ({monitor.Name})";
#endif
        }

        private static object Account()
        {
            var cred = DevThrottle.DevThrottleAccount.Load();
            return new { signedIn = cred != null, email = cred?.Email };
        }

        private static object Recordings(HttpListenerContext ctx)
        {
            int limit = int.TryParse(ctx.Request.QueryString["limit"], out int l) ? l : 50;
            int offset = int.TryParse(ctx.Request.QueryString["offset"], out int o) ? o : 0;
            var page = RecordingLibrary.List(limit, offset);
            return new
            {
                total = page.Total,
                items = page.Items.Select(s => new
                {
                    id = s.Id, dir = s.Dir, label = s.Label, title = s.Title, mode = s.Mode,
                    durationSeconds = s.DurationSeconds, createdUtc = s.CreatedUtc, shotCount = s.ShotCount,
                    hasVideo = s.HasVideo, hasAudio = s.HasAudio, hasTranscript = s.HasTranscript,
                    hasFlatTranscript = s.HasFlatTranscript,
                }),
            };
        }

        internal static string PlatformName() =>
            OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "other";

        private static string AppVersion() =>
            typeof(HeadlessControlServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private static string ReadBody(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            return reader.ReadToEnd();
        }

        private static void Json(HttpListenerContext ctx, object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void Error(HttpListenerContext ctx, int code, string message, string errorCode)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message, code = errorCode }, JsonOpts));
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                Log.Warn($"[HeadlessControlServer] Error: could not send the {code} answer ({ex.Message}) - the client went away");
            }
        }

        public void Dispose()
        {
            Log.Info("[HeadlessControlServer] Dispose");
            _running = false;
            try { _listener.Stop(); }
            catch (ObjectDisposedException) { }
            _listener.Close();
        }
    }
}
