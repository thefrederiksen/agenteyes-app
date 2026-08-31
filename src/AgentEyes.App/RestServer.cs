using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using AgentEyes;
using AgentEyes.Audio;
using AgentEyes.Packaging;
using AgentEyes.Video;

namespace AgentEyes.App
{
    /// <summary>
    /// Localhost-only REST control API (HttpListener). Maps HTTP requests onto the shared
    /// RecordingService so any local agent can drive the recorder. Bind is 127.0.0.1 only.
    /// </summary>
    internal sealed class RestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly RecordingService _svc;
        private readonly int _port;
        private readonly Action<string>? _onCaptured;
        private readonly Func<string?>? _captureSaveFolder;
        private Thread? _thread;
        private volatile bool _running;

        public string Url => $"http://127.0.0.1:{_port}/";

        public RestServer(RecordingService svc, int port,
            Action<string>? onCaptured = null, Func<string?>? captureSaveFolder = null)
        {
            _svc = svc;
            _port = port;
            _onCaptured = onCaptured;
            _captureSaveFolder = captureSaveFolder;
            _listener.Prefixes.Add(Url);
        }

        /// <summary>The configured save-folder override (Capture-tab Settings, null = default).</summary>
        private string? CaptureOverride => _captureSaveFolder?.Invoke();

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "AgentEyes-rest" };
            _thread.Start();
            Log.Info($"REST API listening on {Url}");
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (!_running) return; else continue; }
                try { Handle(ctx); }
                catch (Exception ex) { Log.Error("rest handler", ex); Error(ctx, 500, ex.Message, "internal"); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string method = ctx.Request.HttpMethod.ToUpperInvariant();
            string path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
            if (path == "") path = "/";
            Log.Info($"REST {method} {path}");

            try
            {
                switch (method, path)
                {
                    case ("GET", "/"): Json(ctx, Discovery()); return;
                    case ("GET", "/health"): Json(ctx, new { ok = true, app = "AgentEyes" }); return;
                    case ("GET", "/version"): Json(ctx, new { app = "AgentEyes", version = AppVersion() }); return;
                    case ("GET", "/status"): Json(ctx, _svc.Status()); return;
                    case ("GET", "/devices"): Json(ctx, Devices()); return;
                    case ("GET", "/recordings"): Json(ctx, Recordings(ctx)); return;
                    case ("GET", "/captures"): Json(ctx, Captures()); return;
                    case ("GET", "/presets"): Json(ctx, Presets()); return;

                    case ("POST", "/screenshot"):
                    {
                        var b = Body(ctx);
                        string file = _svc.Screenshot(GetInt(b, "screen", 1), GetIntArray(b, "region"));
                        Json(ctx, new { file });
                        return;
                    }
                    case ("GET", "/capture-info"):
                    {
                        // Headless seam for AC9/AC10: report the resolved save folder (the Windows
                        // Screenshots known folder by default, honoring OneDrive redirection) and the
                        // configured override, so QA can assert where snips land without the UI.
                        string @default = CaptureService.ScreenshotsKnownFolder();
                        string? @override = CaptureOverride;
                        string resolved = CaptureService.ResolveSaveFolder(@override);
                        Json(ctx, new { defaultFolder = @default, configuredOverride = @override, saveFolder = resolved });
                        return;
                    }
                    case ("POST", "/capture"):
                    {
                        // Capture feature (issue #64): full-screen, a specific monitor by index, or an
                        // explicit rect, saved as a PNG into the configured save folder (default: the
                        // Windows Screenshots known folder) and copied to the clipboard. This is the
                        // headless seam for the global shortcuts (which need the interactive overlay
                        // for region selection); pass an explicit region or monitor to drive it scripted.
                        var b = Body(ctx);
                        string mode = GetStr(b, "mode", "full").ToLowerInvariant();
                        string? folder = CaptureOverride;
                        CaptureInfo info;
                        if (mode == "region")
                        {
                            int[]? region = GetIntArray(b, "region");
                            if (region is not { Length: 4 })
                                throw new UsageException("mode 'region' requires a 4-element region [x,y,w,h].");
                            info = CaptureService.CaptureRegion(
                                new System.Drawing.Rectangle(region[0], region[1], region[2], region[3]), folder);
                        }
                        else if (mode is "full" or "monitor")
                        {
                            // 'full' and 'monitor' both capture a whole monitor by 1-based index; the
                            // monitor-picker (AC11) drives this with an explicit screen number.
                            info = CaptureService.CaptureFullScreen(GetInt(b, "screen", 1), folder);
                        }
                        else
                        {
                            throw new UsageException($"unknown capture mode '{mode}' (use 'full', 'monitor', or 'region').");
                        }
                        // Let the app refresh an open Capture gallery (issue #64) - same hook the
                        // shortcut path uses, so an API-driven snip shows up live too.
                        _onCaptured?.Invoke(info.File);
                        Json(ctx, new { file = info.File, width = info.Width, height = info.Height });
                        return;
                    }
                    case ("POST", "/record/start"):
                    {
                        var b = Body(ctx);
                        StartFrom(b);
                        Json(ctx, _svc.Status());
                        return;
                    }
                    case ("POST", "/record/shot"):
                        Json(ctx, new { file = _svc.MarkerShot() });
                        return;
                    case ("POST", "/record/stop"):
                    {
                        // RecordingStop.Keep is the ONE way the app stops a recording the user keeps
                        // (issue #151) - the same call the window Stop button, the HUD, the tray
                        // menu and tray Quit make. Nothing about the post-stop sequence is written
                        // here; bolting each new post-stop step onto individual handlers is what
                        // cost issues #141, #142 and #151. Keep returns as soon as the raw files are
                        // on disk and leaves the rest running in the background, so the response
                        // stays fast (issue #77's point).
                        var stopped = RecordingStop.Keep(_svc);
                        Json(ctx, stopped.Result);
                        return;
                    }
                    // Issue #103: import an external video into the library via the existing #100
                    // VideoImport engine (no reimplementation). Synchronous - the response carries the
                    // new recording id (AC1), so the import must finish before we answer; this mirrors
                    // the CLI 'agenteyes import'. A bad/missing path raises UsageException -> 400 (AC4).
                    case ("POST", "/import"):
                    {
                        var b = Body(ctx);
                        string src = GetStrOrNull(b, "path") ?? "";
                        var result = VideoImport.Run(src);
                        Log.Info($"REST import: id={result.Id}");
                        Json(ctx, new { id = result.Id, dir = result.Dir });
                        return;
                    }
                }

                // Parameterized read routes: /recordings/{id}[/shots|/transcript] (issue #73).
                if (method == "GET" && path.StartsWith("/recordings/", StringComparison.Ordinal)
                    && RecordingSubroute(ctx, path)) return;

                // Issue #103: parameterized write routes wiring the #101 Translator and #102
                // SubtitleBurner engines (no reimplementation).
                if (method == "POST" && path.StartsWith("/transcripts/", StringComparison.Ordinal)
                    && TranslateSubroute(ctx, path)) return;
                if (method == "POST" && path.StartsWith("/recordings/", StringComparison.Ordinal)
                    && SubtitleSubroute(ctx, path)) return;

                Error(ctx, 404, "no such endpoint: " + path, "not_found");
            }
            catch (UsageException ux)
            {
                // Map lifecycle conflicts to 409, other bad input to 400.
                bool conflict = ux.Message.Contains("already recording") || ux.Message.Contains("not recording");
                if (conflict) Error(ctx, 409, ux.Message, "conflict");
                else Error(ctx, 400, ux.Message, "bad_request");
            }
        }

        /// <summary>
        /// Handle GET /recordings/{id}, /recordings/{id}/shots, /recordings/{id}/transcript.
        /// Returns true when the path matched one of these shapes (including the 404 not_found for
        /// an unknown id); false when it is not a recordings subroute, so the caller falls through
        /// to the generic not_found.
        /// </summary>
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
                    // Issue #103: which languages this recording carries a subtitle-ready transcript
                    // for (the manifest per-language map, issue #98), so a caller can decide what to
                    // translate to or burn. Empty for a recording that predates the map.
                    languages = d.Manifest.Transcripts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                    manifest = d.Manifest,
                });
                return true;
            }

            if (seg.Length == 2 && seg[1] == "shots")
            {
                var shots = RecordingLibrary.GetShots(id);
                if (shots == null) { Error(ctx, 404, "no recording with id: " + id, "not_found"); return true; }
                Json(ctx, shots.Select(s => new { file = s.File, path = s.Path, offsetSeconds = s.OffsetSeconds }));
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

            return false;
        }

        /// <summary>
        /// Handle POST /transcripts/{id}/translate (issue #103): translate the recording's transcript
        /// into a target language via the existing #101 <see cref="Translator"/> engine (timing
        /// preserved), returning the resulting language, cue count, and produced VTT name. Returns true
        /// when the path matched this shape (including the 404 for an unknown id). Target language is
        /// taken from the body's "to" (or "language"). A missing language or engine guard raises
        /// UsageException, which the caller maps to 400 (AC4).
        /// </summary>
        private bool TranslateSubroute(HttpListenerContext ctx, string path)
        {
            string[] seg = path.Substring("/transcripts/".Length).Split('/');
            string id = Uri.UnescapeDataString(seg[0]);
            if (string.IsNullOrEmpty(id)) return false;
            if (seg.Length != 2 || seg[1] != "translate") return false;

            // Unknown id -> 404 not_found, consistent with the GET /recordings/{id} routes.
            if (RecordingLibrary.GetDetail(id) == null)
            { Error(ctx, 404, "no recording with id: " + id, "not_found"); return true; }

            var b = Body(ctx);
            string lang = GetStrOrNull(b, "to") ?? GetStrOrNull(b, "language") ?? "";
            if (string.IsNullOrWhiteSpace(lang))
                throw new UsageException("translate needs a target language: POST body { \"to\": \"tr\" }.");

            var result = Translator.Run(id, lang);
            Log.Info($"REST translate: id={result.Id}, lang={result.Language}, cues={result.CueCount}");
            Json(ctx, new
            {
                id = result.Id,
                dir = result.Dir,
                language = result.Language,
                cues = result.CueCount,
                vtt = WebVtt.FileNameFor(result.Language),
            });
            return true;
        }

        /// <summary>
        /// Handle POST /recordings/{id}/subtitle (issue #103): burn the recording's
        /// transcript.&lt;lang&gt;.vtt into a new subtitled MP4 via the existing #102
        /// <see cref="SubtitleBurner"/> engine, returning the output file name. Returns true when the
        /// path matched this shape (including the 404 for an unknown id). Language is taken from the
        /// body's "language" (or "lang"). A missing language or engine guard (no such VTT, no video)
        /// raises UsageException, which the caller maps to 400 (AC4).
        /// </summary>
        private bool SubtitleSubroute(HttpListenerContext ctx, string path)
        {
            string[] seg = path.Substring("/recordings/".Length).Split('/');
            string id = Uri.UnescapeDataString(seg[0]);
            if (string.IsNullOrEmpty(id)) return false;
            if (seg.Length != 2 || seg[1] != "subtitle") return false;

            // Unknown id -> 404 not_found, consistent with the GET /recordings/{id} routes.
            if (RecordingLibrary.GetDetail(id) == null)
            { Error(ctx, 404, "no recording with id: " + id, "not_found"); return true; }

            var b = Body(ctx);
            string lang = GetStrOrNull(b, "language") ?? GetStrOrNull(b, "lang") ?? "";
            if (string.IsNullOrWhiteSpace(lang))
                throw new UsageException("subtitle needs a language: POST body { \"language\": \"tr\" }.");

            var result = SubtitleBurner.Run(id, lang);
            Log.Info($"REST subtitle: id={result.Id}, lang={result.Language}, output={result.OutputFile}");
            Json(ctx, new
            {
                id = result.Id,
                dir = result.Dir,
                language = result.Language,
                output = result.OutputFile,
            });
            return true;
        }

        private void StartFrom(JsonElement b)
        {
            // Optional "preset" shorthand supplies the defaults; explicit fields still override.
            CapturePreset? preset = null;
            string? presetName = GetStrOrNull(b, "preset");
            if (presetName != null)
            {
                var all = PresetStore.Load();
                preset = all.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
                         ?? all.FirstOrDefault(p => p.Id == presetName);
                if (preset == null) throw new UsageException($"no preset named '{presetName}'");
            }

            string mode = GetStr(b, "mode", preset?.Mode ?? "video").ToLowerInvariant();
            int screen = GetInt(b, "screen", preset?.MonitorIndex ?? 1);
            var src = RecordingService.ParseSource(GetStr(b, "source", preset?.Source ?? "mic")); // issue #124: mic-only default
            string? mic = GetStrOrNull(b, "mic") ?? preset?.Mic;
            int[]? region = GetIntArray(b, "region") ?? (preset?.UseRegion == true ? preset.Region : null);
            int fps = GetInt(b, "fps", preset?.Fps ?? 30);
            // Issue #28: same precedence as "mic" - an explicit body field overrides the preset, and
            // an absent one falls back to the preset's saved camera (null = no camera track).
            string? camera = GetStrOrNull(b, "camera") ?? preset?.Camera;
            int cameraFps = GetInt(b, "cameraFps", preset?.CameraFps ?? 30);
            var opts = new AudioMixOptions
            {
                NoiseSuppression = GetBool(b, "denoise", preset?.Denoise ?? true),
                // Issue #83: absent "gate" -> the preset's explicit choice, else the source default
                // (mic-only OFF since it has no speaker bleed to tame; mixed/system ON).
                NoiseGate = GetBool(b, "gate", preset?.Gate ?? GateDefaults.For(src)),
                VoiceLeveling = GetBool(b, "level", preset?.Level ?? true),
                MicGain = GetDouble(b, "micVol", preset?.MicVol ?? 100) / 100.0,
                SystemGain = GetDouble(b, "sysVol", preset?.SysVol ?? 70) / 100.0,
            };

            if (mode == "shot") throw new UsageException("preset is screenshot mode - use POST /screenshot instead");
            if (mode == "audio")
            {
                _svc.StartAudio(screen, src, mic, opts);
                return;
            }

            // Issue #47: the framing follows the same precedence as everything else here - the named
            // preset's own choice first, then the persisted overlay config - so a recording started
            // over the API records the framing it was actually laid out with, and the composed video
            // has a layout to render.
            AgentEyes.Preview.CameraOverlaySettings? overlay = string.IsNullOrWhiteSpace(camera)
                ? null
                : (preset?.Overlay ?? HudOverlayConfig.Read(Config.Load()));
            _svc.StartVideo(screen, src, mic, region, opts, fps, camera, cameraFps, overlay);
        }

        private static object Presets() => PresetStore.Load().Select(p => new
        {
            p.Id, p.Name, p.Note, p.MonitorIndex, p.UseRegion, p.Region,
            p.Source, p.Mic, p.Denoise, p.Gate, p.Level, p.MicVol, p.SysVol, p.Mode, p.Fps,
            p.Camera, p.CameraFps,
        });

        private static object Discovery() => new
        {
            app = "AgentEyes",
            endpoints = new[]
            {
                "GET /version", "GET /health", "GET /status", "GET /devices",
                "GET /recordings {limit?, offset?}", "GET /recordings/{id}",
                "GET /recordings/{id}/shots", "GET /recordings/{id}/transcript",
                "GET /captures", "GET /presets",
                "POST /screenshot {screen, region?}",
                "GET /capture-info",
                "POST /capture {mode:full|monitor|region, screen?, region?}",
                "POST /record/start {preset?, mode, screen, source, mic?, camera?, cameraFps?, region?, denoise?, gate?, level?, micVol?, sysVol?, fps?}",
                "POST /record/shot", "POST /record/stop",
                "POST /import {path}",
                "POST /transcripts/{id}/translate {to}",
                "POST /recordings/{id}/subtitle {language}",
            },
        };

        private static object Devices()
        {
            var mons = Monitors.All().Select(m => new { m.Index, m.Width, m.Height, m.Primary, m.Name });
            var mics = AudioCapture.Devices().Select(d => new { d.Number, d.Name });
            string[] dshow; try { dshow = FfmpegDevices.ListAudio().ToArray(); } catch { dshow = Array.Empty<string>(); }
            // Issue #28: the DirectShow cameras a recording can film to camera.mp4, by their exact
            // ffmpeg names. A machine with no camera reports an EMPTY array - "no cameras" is a fact
            // about the machine, not a failure of the call.
            //
            // Which is exactly why enumeration is NOT wrapped (gate defect 5). Catching the failure
            // and returning [] with HTTP 200 made a broken enumerator - ffmpeg missing, unable to
            // start, or throwing - indistinguishable from a laptop with no webcam, and it made AC1's
            // "an empty array means no camera" false. The throw reaches the request handler, which
            // answers 500 with the real message, so the caller is told what to fix instead of being
            // told a comfortable lie.
            string[] cameras = FfmpegDevices.ListVideo().ToArray();
            return new { monitors = mons, mics, dshow, cameras };
        }

        /// <summary>App product version string (e.g. "0.8.2") for GET /version.</summary>
        private static string AppVersion() =>
            typeof(RestServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>GET /recordings - the full library, newest-first, with limit/offset paging.</summary>
        private static object Recordings(HttpListenerContext ctx)
        {
            int limit = QInt(ctx, "limit", 50);
            int offset = QInt(ctx, "offset", 0);
            var page = RecordingLibrary.List(limit, offset);
            return new
            {
                total = page.Total,
                items = page.Items.Select(s => new
                {
                    id = s.Id, dir = s.Dir, label = s.Label, title = s.Title, mode = s.Mode,
                    durationSeconds = s.DurationSeconds, createdUtc = s.CreatedUtc, shotCount = s.ShotCount,
                    hasVideo = s.HasVideo, hasAudio = s.HasAudio, hasTranscript = s.HasTranscript,
                }),
            };
        }

        /// <summary>GET /captures - the capture gallery (resolved save folder), newest-first.</summary>
        private object Captures() =>
            RecordingLibrary.Captures(CaptureOverride)
                .Select(c => new { file = c.File, path = c.Path, sizeBytes = c.SizeBytes, createdUtc = c.CreatedUtc });

        /// <summary>Read an integer query-string parameter, falling back to a default.</summary>
        private static int QInt(HttpListenerContext ctx, string key, int def) =>
            int.TryParse(ctx.Request.QueryString[key], out int v) ? v : def;

        // ---- json helpers -------------------------------------------------

        private static JsonElement Body(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            string text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return default;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        private static string GetStr(JsonElement b, string k, string def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;
        private static string? GetStrOrNull(JsonElement b, string k) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement b, string k, int def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.TryGetInt32(out int i) ? i : def;
        private static double GetDouble(JsonElement b, string k, double def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.TryGetDouble(out double d) ? d : def;
        private static bool GetBool(JsonElement b, string k, bool def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;
        private static int[]? GetIntArray(JsonElement b, string k)
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            return v.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        }

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private static void Json(HttpListenerContext ctx, object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        /// <summary>
        /// Write the uniform error envelope (issue #73): { "error": message, "code": short-code }
        /// with the given HTTP status. When <paramref name="errorCode"/> is omitted it is derived
        /// from the status (400 -> bad_request, 404 -> not_found, 409 -> conflict, else internal).
        /// </summary>
        private static void Error(HttpListenerContext ctx, int code, string message, string? errorCode = null)
        {
            string c = errorCode ?? CodeFor(code);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message, code = c }, JsonOpts));
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        private static string CodeFor(int status) => status switch
        {
            400 => "bad_request",
            404 => "not_found",
            409 => "conflict",
            _ => "internal",
        };

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
