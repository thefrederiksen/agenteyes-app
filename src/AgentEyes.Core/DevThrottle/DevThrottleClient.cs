using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentEyes.DevThrottle
{
    /// <summary>Raised for any non-2xx from the DevThrottle proxy; carries the HTTP status.</summary>
    internal sealed class DevThrottleException : Exception
    {
        public int Status { get; }
        public DevThrottleException(string message, int status = 0) : base(message) => Status = status;
    }

    /// <summary>
    /// The result of a transcription: the text, the audio duration (when the proxy reports it), and the
    /// per-segment timing (issue #99) when the hosted response carries a segments[] array. <see
    /// cref="Segments"/> is null for the legacy no-segments response shape - callers then synthesize a
    /// single whole-clip segment (unchanged behavior).
    /// </summary>
    internal sealed class DevThrottleTranscript
    {
        public string Text { get; set; } = "";
        public double? DurationSeconds { get; set; }
        public IReadOnlyList<TranscriptSegmentDto>? Segments { get; set; }
    }

    internal sealed class DevThrottleCredits
    {
        public long BalanceMicros { get; set; }
        public long LowBalanceThresholdMicros { get; set; }
        public bool HasCredits => BalanceMicros > 0;
    }

    /// <summary>
    /// Client for the DevThrottle proxy. AgentEyes' only inference path
    /// (issue #87). No fallbacks: any non-2xx surfaces the proxy's error as a
    /// DevThrottleException, with 401 (reconnect) and 402 (add credits) called out for the UI.
    /// </summary>
    internal static class DevThrottleClient
    {
        public const string TranscriptionModel = "whisper-large-v3";
        // Curated chat models on the DevThrottle catalog (models-catalog.js). Small/fast for cheap
        // hot-path tasks like titling; the stronger driver for plugin deliverable generation.
        public const string ChatModel = "zai-org/GLM-4.7-Flash";
        public const string ChatModelStrong = "zai-org/GLM-4.7";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

        /// <summary>
        /// Read the signed-in member's DevThrottle credit balance. This endpoint is an account
        /// endpoint, so it uses the Supabase access token returned by browser sign-in, not the dt_
        /// inference key. Older saved credentials may need reconnect before this is available.
        /// </summary>
        public static async Task<DevThrottleCredits> GetCreditsAsync(CancellationToken ct = default)
        {
            var cred = DevThrottleAccount.Load();
            if (cred?.ApiKey is not { Length: > 0 })
                throw new DevThrottleException(
                    "Not signed in to DevThrottle. Open Settings > Account and sign in.", 401);
            // No access token stored at all, but a refresh token might still earn one.
            if (cred.AccessToken is not { Length: > 0 } && !await KeyRecovery.TryRefreshAccessTokenAsync(ct))
                throw new DevThrottleException(
                    "Reconnect to DevThrottle to show your credit balance in AgentEyes.", 401);

            string url = DevThrottleAccount.AuthBaseUrl.TrimEnd('/') + "/api/v1/account/credits?limit=1&scope=money";
            string token = DevThrottleAccount.Load()?.AccessToken ?? "";

            var (status, body) = await GetCreditsOnceAsync(url, token, ct);
            if (status == 401)
            {
                // The access token lives about an hour, so this is the NORMAL state for any session
                // older than that - not a sign-out. Renew it and read the balance again (issue #134).
                Log.Info("[DevThrottleClient] GetCreditsAsync: 401 - renewing the account access token");
                if (await KeyRecovery.TryRefreshAccessTokenAsync(ct))
                {
                    (status, body) = await GetCreditsOnceAsync(url, DevThrottleAccount.Load()?.AccessToken ?? "", ct);
                    Log.Info($"[DevThrottleClient] GetCreditsAsync: after renewal status={status}");
                }
            }

            if (status is < 200 or > 299) throw ErrorFrom((HttpStatusCode)status, body);

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            return new DevThrottleCredits
            {
                BalanceMicros = data.TryGetProperty("balance_micros", out var b) && b.TryGetInt64(out var balance)
                    ? balance : 0,
                LowBalanceThresholdMicros = data.TryGetProperty("low_balance_threshold_micros", out var l) && l.TryGetInt64(out var low)
                    ? low : 0,
            };
        }

        private static async Task<(int Status, string Body)> GetCreditsOnceAsync(
            string url, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            return ((int)resp.StatusCode, body);
        }

        /// <summary>
        /// Fail early when a fresh sign-in token shows an empty wallet. If the account token is
        /// missing/expired, the proxy's own 402 pre-flight remains the hard gate.
        /// </summary>
        public static async Task EnsureCreditsForHostedWorkAsync(CancellationToken ct = default)
        {
            var cred = DevThrottleAccount.Load();
            if (cred?.AccessToken is not { Length: > 0 }) return;
            DevThrottleCredits credits;
            try
            {
                credits = await GetCreditsAsync(ct);
            }
            catch (DevThrottleException ex) when (ex.Status == 401)
            {
                Log.Info("[DevThrottleClient] EnsureCreditsForHostedWorkAsync: account token unavailable; proxy will enforce credits");
                return;
            }
            if (!credits.HasCredits)
                throw new DevThrottleException(
                    "Out of DevThrottle credits. Add credits (from $5) at devthrottle.com to keep transcribing.", 402);
        }

        /// <summary>
        /// True when <paramref name="ex"/> (or anything it wraps) means "the wallet is empty".
        /// Lives here rather than in the window because the automatic repair passes run with no
        /// window at all (tray mode) and must stop the same way the UI does (issue #142).
        /// </summary>
        public static bool IsCreditsFailure(Exception ex)
        {
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is DevThrottleException { Status: 402 }) return true;
                if (cur.Message.Contains("Out of DevThrottle credits", StringComparison.OrdinalIgnoreCase)) return true;
                if (cur.Message.Contains("credit balance is empty", StringComparison.OrdinalIgnoreCase)) return true;
                if (cur.Message.Contains("insufficient_credits", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// The shared <see cref="Http"/> timeout is five minutes because a Whisper upload needs it.
        /// A chat completion does not: titling a recording normally returns in a second or two, and
        /// even a 114-minute transcript came back at 12k tokens without trouble. Applying the
        /// upload timeout to a chat call meant one stalled titling request held an entire packaging
        /// pass for 300 seconds (issue #138). Chat calls get their own, far shorter budget.
        /// </summary>
        /// Sized from MEASUREMENT, not taste: observed real titling calls took 39.2s and 9.5s
        /// (2026-08-10). A first guess of 45s would have started killing legitimate titles. 120s is
        /// ~3x the slowest observed call and still 60% below the 300s stall that caused #138.
        public static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// POST a chat-completions payload to the DevThrottle proxy on the
        /// signed-in account. Returns the status code and body; callers shape their own errors.
        /// Throws DevThrottleException(401) when not signed in, and
        /// <see cref="DevThrottleException"/>(408) when EVERY attempt outlives <paramref name="timeout"/>.
        ///
        /// A transient answer (429 "Model busy", 5xx) is retried under <see cref="ChatRetryPolicy"/>:
        /// bounded attempts, bounded delays, and a bounded total so the retry tail cannot re-create the
        /// packaging stall issue #138 removed (issue #140).
        ///
        /// A HUNG attempt - the per-attempt budget expiring with no answer - is retried on that same
        /// bounded policy (issue #145). It is the failure the field log actually shows: three naming
        /// calls died this way in one hour on the #140 build, because a stall produces no status for a
        /// status-driven policy to judge. <see cref="SendChatOnceAsync"/> now reports it as
        /// <see cref="ChatRetryPolicy.LocalTimeoutStatus"/> so it is classified like any other failed
        /// attempt, and this method raises the same typed exception it always did once the attempts run
        /// out. A CALLER cancellation is not a stall and is never retried - it leaves
        /// <see cref="SendChatOnceAsync"/> as <see cref="OperationCanceledException"/>.
        /// </summary>
        /// <param name="timeout">Per-attempt budget. Defaults to <see cref="ChatTimeout"/>.</param>
        public static async Task<(int Status, string Body)> PostChatAsync(
            string payloadJson, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            string key = DevThrottleAccount.RequireApiKey();
            string url = DevThrottleAccount.ApiBaseUrl.TrimEnd('/') + "/chat/completions";
            var perAttempt = timeout ?? ChatTimeout;
            // The ceiling on the whole call: the larger of the #140 status tail (180s) and the #145
            // timeout worst case (242s), both named and asserted in ChatRetryPolicyTests. Sized so a
            // retried attempt still gets its FULL budget - a legitimate naming call was measured at
            // 66.7s, so a second attempt truncated to a leftover would kill calls about to succeed.
            var totalBudget = ChatRetryPolicy.WorstCaseTotal(perAttempt);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int status;
            string body;

            for (int attempt = 1; ; attempt++)
            {
                // Every attempt is also capped by what is left of the whole-call budget, so a slow
                // transient answer cannot let the tail run past totalBudget.
                var remaining = totalBudget - sw.Elapsed;
                var attemptBudget = remaining < perAttempt ? remaining : perAttempt;

                string? retryAfter;
                (status, body, retryAfter) = await SendChatOnceAsync(url, key, payloadJson, attemptBudget, ct);
                if (status == 401)
                {
                    // Revoked key: mint a new one and send once more (issue #131).
                    Log.Info("[DevThrottleClient] PostChatAsync: 401 - attempting silent key recovery");
                    if (await KeyRecovery.TryRecoverAsync(key, ct))
                    {
                        key = DevThrottleAccount.RequireApiKey();
                        (status, body, retryAfter) = await SendChatOnceAsync(url, key, payloadJson, attemptBudget, ct);
                        Log.Info($"[DevThrottleClient] PostChatAsync: after recovery status={status}");
                    }

                    if (status == 401) AccountState.NoteUnauthorized();
                }

                if (!ChatRetryPolicy.ShouldRetry(status, attempt, ct.IsCancellationRequested)) break;

                var delay = ChatRetryPolicy.DelayFor(attempt, retryAfter);
                if (sw.Elapsed + delay >= totalBudget)
                {
                    Log.Warn(ChatRetryPolicy.BudgetExhaustedMessage(status, attempt, sw.Elapsed));
                    break;
                }

                Log.Info(ChatRetryPolicy.IsLocalTimeout(status)
                    ? ChatRetryPolicy.TimeoutRetryMessage(attempt + 1, delay)
                    : ChatRetryPolicy.RetryMessage(status, attempt + 1, delay));
                await Task.Delay(delay, ct);
            }

            // Elapsed is logged on every call so a slow-but-succeeding chat request is visible
            // BEFORE it becomes a timeout (issue #138).
            Log.Info($"[DevThrottleClient] PostChatAsync: status={status}, elapsed={sw.ElapsedMilliseconds}ms");

            // A hang is this client's own budget expiring, not an answer, so it must not be handed back
            // as one. Callers have always received the typed DevThrottleException(408) here and still do
            // - only the number of attempts behind it changed (issue #145).
            if (ChatRetryPolicy.IsLocalTimeout(status))
                throw new DevThrottleException(body, ChatRetryPolicy.LocalTimeoutStatus);

            return (status, body);
        }

        private static async Task<(int Status, string Body, string? RetryAfter)> SendChatOnceAsync(
            string url, string key, string payloadJson, TimeSpan timeout, CancellationToken ct)
        {
            // The shared HttpClient carries the long upload timeout, so the chat budget is applied
            // per request via a linked token rather than by mutating the client.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            try
            {
                using var resp = await Http.SendAsync(req, budget.Token);
                string body = await resp.Content.ReadAsStringAsync(budget.Token);
                // The RAW header value is carried out so the retry decision stays a pure, testable
                // function of (status, header) instead of reaching into HttpResponseMessage.
                string? retryAfter = resp.Headers.TryGetValues("Retry-After", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return ((int)resp.StatusCode, body, retryAfter);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our budget expired, not the caller's cancellation (the filter is what tells them
                // apart - a caller cancellation propagates untouched and is never retried). Reported as
                // a STATUS rather than thrown so the stall reaches the same bounded retry that judges
                // 429/5xx; PostChatAsync raises the unchanged typed DevThrottleException(408) once the
                // attempts are spent (issue #145).
                Log.Info($"[DevThrottleClient] SendChatOnceAsync: timed out after {timeout.TotalSeconds:0}s");
                return (ChatRetryPolicy.LocalTimeoutStatus, ChatRetryPolicy.TimeoutMessage(timeout), null);
            }
        }

        /// <summary>
        /// Transcribe a 16 kHz mono WAV through DevThrottle-hosted Whisper. Requires a
        /// signed-in account; throws DevThrottleException(401) when not signed in and
        /// DevThrottleException(402) when the credit balance is empty.
        /// </summary>
        public static async Task<DevThrottleTranscript> TranscribeAsync(string wavPath, CancellationToken ct = default)
        {
            Log.Info($"[DevThrottleClient] TranscribeAsync: wavPath={wavPath}");
            if (!File.Exists(wavPath))
                throw new UsageException($"audio file not found for transcription: {wavPath}");

            await EnsureCreditsForHostedWorkAsync(ct);

            string key = DevThrottleAccount.RequireApiKey();
            string url = DevThrottleAccount.ApiBaseUrl.TrimEnd('/') + "/audio/transcriptions";
            byte[] bytes = await File.ReadAllBytesAsync(wavPath, ct);

            // A long recording is split into short parts and transcribed IN PARALLEL (transcription
            // reliability epic, devthrottle_internal#324) so an hour of audio does not time out on one
            // oversized request. A clip within one window is still a single POST.
            ChunkResult result;
            try
            {
                result = await BatchTranscription.TranscribeAsync(
                    bytes, Path.GetFileName(wavPath),
                    (audio, fileName, c) => PostOneAsync(audio, fileName, key, url, c),
                    IsTransientTranscription, ct);
            }
            catch (DevThrottleException ex) when (ex.Status == 401)
            {
                // The key was revoked. Mint a new one and run the batch once more (issue #131).
                // Transcription is a pure read, so replaying it is safe. Exactly one retry - if the
                // second attempt 401s, that exception propagates and the user sees the real error.
                Log.Info("[DevThrottleClient] TranscribeAsync: 401 - attempting silent key recovery");
                if (!await KeyRecovery.TryRecoverAsync(key, ct))
                {
                    // The dt_ key really is unusable and could not be replaced (issue #129).
                    AccountState.NoteUnauthorized();
                    throw;
                }

                key = DevThrottleAccount.RequireApiKey();
                result = await BatchTranscription.TranscribeAsync(
                    bytes, Path.GetFileName(wavPath),
                    (audio, fileName, c) => PostOneAsync(audio, fileName, key, url, c),
                    IsTransientTranscription, ct);
                Log.Info("[DevThrottleClient] TranscribeAsync: succeeded after key recovery");
            }

            Log.Info($"[DevThrottleClient] TranscribeAsync: length={result.Text.Length}, duration={result.DurationSeconds}, " +
                     $"segments={result.Segments?.Count ?? 0}");
            return new DevThrottleTranscript
            {
                Text = result.Text,
                DurationSeconds = result.DurationSeconds,
                Segments = result.Segments,
            };
        }

        /// <summary>One /audio/transcriptions POST of a single bounded WAV chunk. A non-2xx surfaces as a
        /// DevThrottleException carrying the status, so the batch retry can tell a transient 5xx/429 from a
        /// permanent 4xx.</summary>
        private static async Task<ChunkResult> PostOneAsync(byte[] audio, string fileName, string key, string url, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", string.IsNullOrEmpty(fileName) ? "audio.wav" : fileName);
            form.Add(new StringContent(TranscriptionModel), "model");
            form.Add(new StringContent("verbose_json"), "response_format");

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = form;

            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw ErrorFrom(resp.StatusCode, body);

            return ParseTranscriptionResponse(body);
        }

        /// <summary>
        /// Parse one Whisper verbose_json body (issue #99) into a <see cref="ChunkResult"/>: the joined
        /// text, the audio duration, and - when the response carries a <c>segments[]</c> array - the timed
        /// segments. The expected shape (documented in docs/cencon/proof/issue-99/handoff.md):
        /// <code>
        /// { "text": "...", "duration": 12.3,
        ///   "segments": [ { "start": 0.0, "end": 4.2, "text": "..." }, ... ] }
        /// </code>
        /// <c>start</c>/<c>end</c> are SECONDS (float). When <c>segments[]</c> is absent (legacy shape) the
        /// result carries a null <see cref="ChunkResult.Segments"/> so callers keep the single-segment
        /// fallback. No local Whisper.net path is involved - this is purely the hosted response consumer.
        /// </summary>
        internal static ChunkResult ParseTranscriptionResponse(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string text = root.TryGetProperty("text", out var t) ? (t.GetString()?.Trim() ?? "") : "";
            double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() : null;

            List<TranscriptSegmentDto>? segments = null;
            if (root.TryGetProperty("segments", out var segArr) && segArr.ValueKind == JsonValueKind.Array)
            {
                segments = new List<TranscriptSegmentDto>();
                foreach (var seg in segArr.EnumerateArray())
                {
                    if (seg.ValueKind != JsonValueKind.Object) continue;
                    segments.Add(new TranscriptSegmentDto
                    {
                        StartSeconds = seg.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number
                            ? s.GetDouble() : 0,
                        EndSeconds = seg.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number
                            ? e.GetDouble() : 0,
                        Text = (seg.TryGetProperty("text", out var x) ? x.GetString() : null)?.Trim() ?? "",
                    });
                }
                // An empty or entirely non-object segments[] is treated as absent, so the fallback holds.
                if (segments.Count == 0) segments = null;
            }

            return new ChunkResult(text, duration, segments);
        }

        /// <summary>A chunk failure worth one more attempt: a 5xx/429 from the proxy (which already tried
        /// its own provider fallback) or a network/timeout error. A 4xx/402 is permanent.</summary>
        private static bool IsTransientTranscription(Exception ex) =>
            (ex is DevThrottleException dex && (dex.Status >= 500 || dex.Status == 429))
            || ex is HttpRequestException
            || ex is TaskCanceledException;

        /// <summary>
        /// Lightweight connection check for the Settings status line: GET /models with the
        /// stored key. Returns true on 200; throws DevThrottleException(401) on a bad/expired key.
        /// </summary>
        public static async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
        {
            string key = DevThrottleAccount.RequireApiKey();
            string url = DevThrottleAccount.ApiBaseUrl.TrimEnd('/') + "/models";

            var (ok, status, body) = await CheckOnceAsync(url, key, ct);
            if (ok) return true;

            if (status == 401 && await KeyRecovery.TryRecoverAsync(key, ct))
            {
                Log.Info("[DevThrottleClient] CheckConnectionAsync: retrying after key recovery");
                (ok, status, body) = await CheckOnceAsync(url, DevThrottleAccount.RequireApiKey(), ct);
                if (ok) return true;
            }

            if (status == 401) AccountState.NoteUnauthorized();
            throw ErrorFrom((HttpStatusCode)status, body);
        }

        private static async Task<(bool Ok, int Status, string Body)> CheckOnceAsync(
            string url, string key, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, "");
            string body = await resp.Content.ReadAsStringAsync(ct);
            return (false, (int)resp.StatusCode, body);
        }

        /// <summary>Maps a proxy error into a DevThrottleException with a user-actionable message.</summary>
        internal static DevThrottleException ErrorFrom(int status, string body) =>
            ErrorFrom((HttpStatusCode)status, body);

        private static DevThrottleException ErrorFrom(HttpStatusCode status, string body)
        {
            int code = (int)status;
            string? proxyMsg = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                    proxyMsg = m.GetString();
            }
            catch { /* non-JSON error body */ }

            string message = code switch
            {
                401 => "DevThrottle rejected the sign-in key. Open Settings > DevThrottle Account and reconnect.",
                402 => "Out of DevThrottle credits. Add credits (from $5) at devthrottle.com to keep transcribing.",
                413 => "That audio clip is too large for DevThrottle transcription.",
                _   => proxyMsg ?? $"DevThrottle request failed (HTTP {code}).",
            };
            Log.Info($"[DevThrottleClient] ErrorFrom: status={code}, msg={message}");

            // NOT hooked here on purpose. This helper is shared by TWO different credentials: the
            // dt_ inference key AND the short-lived Supabase access token used only to read the
            // credit balance. That access token expires in about an hour, so hooking 401 here
            // reported "Not signed in" while the dt_ key was perfectly good and transcription was
            // succeeding (observed 2026-08-10 14:05). AccountState.NoteUnauthorized() is called at
            // the specific dt_-key call sites instead, and only once recovery has failed.
            return new DevThrottleException(message, code);
        }
    }
}
