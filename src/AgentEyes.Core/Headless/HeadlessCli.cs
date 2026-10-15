using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using AgentEyes.DevThrottle;

namespace AgentEyes.Headless
{
    /// <summary>
    /// The CLI side of the headless service (issue #102):
    /// <c>agenteyes record start|stop|status</c> drive a running <c>agenteyes serve</c> through its
    /// Control API (so a recording started from a terminal is the service's recording, and survives
    /// the terminal closing), and <c>agenteyes signin|signout|account</c> manage the DevThrottle
    /// credential the service uses for hosted transcription and titling.
    /// </summary>
    internal static class HeadlessCli
    {
        public const string Usage =
              "  agenteyes serve [--port N]                # headless recorder + Control API (the systemd service runs this)\n"
            + "  agenteyes record start [--mix | --loopback] [--mic \"NAME\"] [--screen N] [--camera \"NAME\"] [--fps N] [--audio-only]\n"
            + "  agenteyes record stop | status            # drive the running service\n"
            + "  agenteyes signin | signout | account      # the DevThrottle account used for transcription and titles";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public static int Record(CliArgs opts)
        {
            string verb = opts.Positional.Count > 0 ? opts.Positional[0].ToLowerInvariant() : "";
            Log.Info($"[HeadlessCli] Record: verb={verb}");
            switch (verb)
            {
                case "start":
                {
                    string body = JsonSerializer.Serialize(StartBody(opts));
                    var status = Call(HttpMethod.Post, "record/start", body);
                    Console.WriteLine($"[ok] recording -> {Str(status, "dir")}");
                    Console.WriteLine($"     source={Str(status, "source")} mode={Str(status, "mode")}"
                        + (Str(status, "camera") is { Length: > 0 } cam ? $" camera={cam}" : ""));
                    Console.WriteLine("     stop with: agenteyes record stop");
                    return 0;
                }
                case "stop":
                {
                    var result = Call(HttpMethod.Post, "record/stop", "{}");
                    Console.WriteLine($"[ok] saved {Str(result, "dir")} ({Num(result, "durationSeconds"):0.0}s)");
                    Console.WriteLine("     mixing, thumbnail, transcription and title continue in the service;");
                    Console.WriteLine("     watch with: agenteyes record status   (or GET /recordings)");
                    return 0;
                }
                case "status":
                {
                    var s = Call(HttpMethod.Get, "status", null);
                    Console.WriteLine($"state={Str(s, "state")} elapsed={Num(s, "elapsedSeconds"):0.0}s level={Num(s, "level"):0.000}");
                    if (Str(s, "dir") is { Length: > 0 } dir) Console.WriteLine($"dir={dir}");
                    Console.WriteLine($"signedIn={IsTrue(s, "signedIn")} "
                        + $"pendingTranscriptions={Num(s, "pendingTranscriptions"):0}");
                    if (IsTrue(s, "lastStopFailed"))
                        Console.WriteLine($"LAST STOP FAILED: {Str(s, "lastStopError")} (dir={Str(s, "lastStopDir")})");
                    return 0;
                }
                default:
                    throw new UsageException("record needs start, stop or status:\n" + Usage);
            }
        }

        /// <summary>The POST /record/start body for the CLI flags. Pure, unit-tested.</summary>
        internal static Dictionary<string, object> StartBody(CliArgs opts)
        {
            bool mix = opts.Has("mix");
            bool loopback = opts.Has("loopback");
            if (mix && loopback) throw new UsageException("--mix and --loopback are exclusive (--mix records the mic AND system audio).");
            string? mic = opts.Get("mic");
            string source = mix ? "mixed" : loopback ? "system" : (mic != null ? "mic" : "none");
            if ((source is "mixed" or "mic") && string.IsNullOrWhiteSpace(mic))
                throw new UsageException("--mix needs --mic \"NAME\" (a fragment of a microphone name from 'agenteyes screens').");

            var body = new Dictionary<string, object>
            {
                ["mode"] = opts.Has("audio-only") ? "audio" : "video",
                ["screen"] = opts.Has("screen") ? opts.RequireInt("screen", "--screen N") : 1,
                ["source"] = source,
            };
            if (mic != null) body["mic"] = mic;
            if (opts.Get("camera") is { } camera) body["camera"] = camera;
            if (opts.Has("fps")) body["fps"] = opts.RequireInt("fps", "--fps N");
            return body;
        }

        public static int SignIn()
        {
            Log.Info("[HeadlessCli] SignIn");
            Console.WriteLine("Signing in to DevThrottle. A browser window opens; finish the sign-in there.");
            var cred = DevThrottleSignIn.SignInAsync(
                status: s => Console.WriteLine("  " + s),
                urlReady: url => Console.WriteLine($"  If no browser opened, open this address yourself:{Environment.NewLine}  {url}"))
                .GetAwaiter().GetResult();
            Console.WriteLine($"[ok] signed in as {cred.Email}");
            Console.WriteLine("     the running service picks the key up at its next recording.");
            return 0;
        }

        public static int SignOut()
        {
            Log.Info("[HeadlessCli] SignOut");
            DevThrottleAccount.Clear();
            Console.WriteLine("[ok] signed out of DevThrottle (the stored key is deleted)");
            return 0;
        }

        public static int Account()
        {
            var cred = DevThrottleAccount.Load();
            Console.WriteLine(cred == null ? "not signed in (run: agenteyes signin)" : $"signed in as {cred.Email}");
            return 0;
        }

        private static JsonElement Call(HttpMethod method, string path, string? body)
        {
            string url = LocalAppConfig.Base + path;
            Log.Info($"[HeadlessCli] Call: {method} {url}");
            using var req = new HttpRequestMessage(method, url);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = Http.Send(req, CancellationToken.None);
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"[HeadlessCli] Call FAILED: {url}", ex);
                throw new UsageException(
                    $"the AgentEyes service is not answering on {LocalAppConfig.Base} ({ex.Message}). "
                    + StartItHint(), ex);
            }
            using (resp)
            {
                string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                JsonElement json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
                if (!resp.IsSuccessStatusCode)
                {
                    string err = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() ?? text : text;
                    Log.Error($"[HeadlessCli] Call: {url} -> HTTP {(int)resp.StatusCode}: {err}");
                    throw new UsageException($"the service refused: {err} (HTTP {(int)resp.StatusCode})");
                }
                return json;
            }
        }

        /// <summary>
        /// How to start the service, in the words of the service manager this platform actually has.
        /// Telling a Mac to run systemctl, or Windows to read a journal, is how a correct diagnosis
        /// turns into a wasted afternoon.
        /// </summary>
        private static string StartItHint()
        {
#if MACOS
            return "Start it: launchctl kickstart -k gui/$UID/com.agenteyes.recorder"
                + "   (logs: ~/Library/Application Support/AgentEyes/logs)";
#elif WINDOWS
            return "Start it: run AgentEyesApp.exe --tray, or 'agenteyes serve' in a terminal.";
#else
            return "Start it: systemctl --user start agenteyes   (logs: journalctl --user -u agenteyes)";
#endif
        }


        // The service serializes its records with their C# names (State, Dir, ElapsedSeconds) and its
        // anonymous objects in camelCase (dir, durationSeconds), so keys are matched ignoring case.
        internal static bool TryProp(JsonElement e, string key, out JsonElement value)
        {
            value = default;
            if (e.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in e.EnumerateObject())
            {
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                value = p.Value;
                return true;
            }
            return false;
        }

        internal static string Str(JsonElement e, string key) =>
            TryProp(e, key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        internal static double Num(JsonElement e, string key) =>
            TryProp(e, key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        private static bool IsTrue(JsonElement e, string key) =>
            TryProp(e, key, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
