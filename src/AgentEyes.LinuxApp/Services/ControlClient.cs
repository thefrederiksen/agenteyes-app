using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Headless;

namespace AgentEyes.LinuxApp.Services
{
    /// <summary>The service answered with an error, or did not answer (issue #107).</summary>
    internal sealed class ControlApiException : Exception
    {
        public int StatusCode { get; }
        public ControlApiException(string message, int statusCode = 0, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;
    }

    internal sealed record ServiceStatus(
        string State, double ElapsedSeconds, double MicLevel, double SystemLevel, string? Dir,
        bool SignedIn, string? Email, int PendingTranscriptions, bool LastStopFailed, string? LastStopError)
    {
        public bool IsRecording => State is "recording" or "finalizing";
    }

    internal sealed record MonitorChoice(int Index, int Width, int Height, bool Primary, string Name, int X = 0, int Y = 0)
    {
        public string Label => $"Monitor {Index} - {Width} x {Height}{(Primary ? " (primary)" : "")}";
    }

    internal sealed record DeviceList(IReadOnlyList<MonitorChoice> Monitors, IReadOnlyList<string> Mics, string SystemAudio);

    /// <summary>
    /// The Linux app's client of the agenteyes service's loopback Control API (issue #107). The service
    /// records and is the only writer of recording folders; every call here is async and is awaited
    /// off the UI thread's critical path. Errors carry the service's own message.
    /// </summary>
    internal sealed class ControlClient
    {
        public const string BaseUrl = "http://127.0.0.1:7882";

        private static readonly HttpClient Http = new() { BaseAddress = new Uri(BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

        public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var s = await CallAsync(HttpMethod.Get, "/status", null, TimeSpan.FromSeconds(5), ct);
            return new ServiceStatus(
                HeadlessCli.Str(s, "state"), HeadlessCli.Num(s, "elapsedSeconds"),
                HeadlessCli.Num(s, "micLevel"), HeadlessCli.Num(s, "systemLevel"), NullIfEmpty(HeadlessCli.Str(s, "dir")),
                IsTrue(s, "signedIn"), NullIfEmpty(HeadlessCli.Str(s, "accountEmail")),
                (int)HeadlessCli.Num(s, "pendingTranscriptions"), IsTrue(s, "lastStopFailed"), NullIfEmpty(HeadlessCli.Str(s, "lastStopError")));
        }

        public async Task<DeviceList> GetDevicesAsync(CancellationToken ct = default)
        {
            Log.Info("[ControlClient] GetDevicesAsync");
            var d = await CallAsync(HttpMethod.Get, "/devices", null, TimeSpan.FromSeconds(20), ct);
            var monitors = new List<MonitorChoice>();
            if (HeadlessCli.TryProp(d, "monitors", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    monitors.Add(new MonitorChoice((int)HeadlessCli.Num(m, "index"), (int)HeadlessCli.Num(m, "width"),
                        (int)HeadlessCli.Num(m, "height"), IsTrue(m, "primary"), HeadlessCli.Str(m, "name"),
                        (int)HeadlessCli.Num(m, "x"), (int)HeadlessCli.Num(m, "y")));
            var mics = new List<string>();
            if (HeadlessCli.TryProp(d, "mics", out var mc) && mc.ValueKind == JsonValueKind.Array)
                mics.AddRange(mc.EnumerateArray().Select(m => HeadlessCli.Str(m, "name")).Where(n => n.Length > 0));
            Log.Info($"[ControlClient] GetDevicesAsync: {monitors.Count} monitor(s), {mics.Count} mic(s)");
            return new DeviceList(monitors, mics, HeadlessCli.Str(d, "systemAudio"));
        }

        public Task StartAsync(int screen, string source, string? mic, CancellationToken ct = default)
        {
            var body = new Dictionary<string, object> { ["mode"] = "video", ["screen"] = screen, ["source"] = source };
            if (!string.IsNullOrEmpty(mic)) body["mic"] = mic;
            Log.Info($"[ControlClient] StartAsync: screen={screen} source={source} mic={mic ?? "(none)"}");
            return CallAsync(HttpMethod.Post, "/record/start", body, TimeSpan.FromSeconds(60), ct);
        }

        public async Task<(string Dir, double Seconds)> StopAsync(CancellationToken ct = default)
        {
            Log.Info("[ControlClient] StopAsync");
            var r = await CallAsync(HttpMethod.Post, "/record/stop", new { }, TimeSpan.FromSeconds(180), ct);
            return (HeadlessCli.Str(r, "dir"), HeadlessCli.Num(r, "durationSeconds"));
        }

        public Task RenameAsync(string id, string name, CancellationToken ct = default)
        {
            Log.Info($"[ControlClient] RenameAsync: {id} -> {name}");
            return CallAsync(HttpMethod.Post, $"/recordings/{Uri.EscapeDataString(id)}/rename", new { name }, TimeSpan.FromSeconds(30), ct);
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            Log.Info($"[ControlClient] DeleteAsync: {id}");
            return CallAsync(HttpMethod.Delete, $"/recordings/{Uri.EscapeDataString(id)}", null, TimeSpan.FromSeconds(60), ct);
        }

        public async Task<string> ExportSubtitlesAsync(string id, string language, CancellationToken ct = default)
        {
            Log.Info($"[ControlClient] ExportSubtitlesAsync: {id} lang={language}");
            var r = await CallAsync(HttpMethod.Post, $"/recordings/{Uri.EscapeDataString(id)}/subtitles", new { language }, TimeSpan.FromMinutes(30), ct);
            return HeadlessCli.Str(r, "file");
        }

        public async Task<string> BuildWalkthroughAsync(string id, CancellationToken ct = default)
        {
            Log.Info($"[ControlClient] BuildWalkthroughAsync: {id}");
            var r = await CallAsync(HttpMethod.Post, $"/recordings/{Uri.EscapeDataString(id)}/walkthrough", new { }, TimeSpan.FromMinutes(30), ct);
            return HeadlessCli.Str(r, "file");
        }

        public Task RefreshAccountAsync(CancellationToken ct = default)
        {
            Log.Info("[ControlClient] RefreshAccountAsync");
            return CallAsync(HttpMethod.Post, "/account/refresh", new { }, TimeSpan.FromSeconds(10), ct);
        }

        private static async Task<JsonElement> CallAsync(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(method, path);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ControlApiException(
                    $"the AgentEyes recorder service is not answering on {BaseUrl} - start it with: systemctl --user start agenteyes", 0, ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ControlApiException($"the AgentEyes service did not answer {method} {path} within {timeout.TotalSeconds:0}s", 0, ex);
            }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                JsonElement json = default;
                if (text.Length > 0)
                {
                    try { json = JsonDocument.Parse(text).RootElement.Clone(); }
                    catch (JsonException) when (!resp.IsSuccessStatusCode) { }
                }
                if (!resp.IsSuccessStatusCode)
                {
                    string err = json.ValueKind == JsonValueKind.Object ? HeadlessCli.Str(json, "error") : "";
                    throw new ControlApiException(err.Length > 0 ? err : $"{(int)resp.StatusCode} {resp.ReasonPhrase}", (int)resp.StatusCode);
                }
                return json;
            }
        }

        private static bool IsTrue(JsonElement e, string key) => HeadlessCli.TryProp(e, key, out var v) && v.ValueKind == JsonValueKind.True;

        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
