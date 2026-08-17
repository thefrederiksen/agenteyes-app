using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentEyes.DevThrottle
{
    /// <summary>
    /// Silent recovery from a DevThrottle key that is no longer accepted (issue #131).
    ///
    /// An AgentEyes-minted dt_ key NEVER EXPIRES - devthrottle_internal
    /// `website/api/v1/keys.js` inserts the row with no `expires_at`, and
    /// `website/api/_lib/apikeys.js` rejects a key only when `revoked_at` is set or `expires_at`
    /// has passed. So a 401 means the key was REVOKED, and the fix is not to refresh it but to
    /// MINT A NEW ONE. The stored Supabase refresh token makes that possible with no browser.
    ///
    /// Best-effort by design: when any step fails the caller's original 401 surfaces unchanged and
    /// the app stays not-signed-in (issue #129). Nothing is hidden - the failure is still reported,
    /// this only removes the manual step when recovery IS possible.
    /// </summary>
    internal static class KeyRecovery
    {
        // One recovery at a time. Several calls can 401 at once (a backfill pass plus a title
        // request); without this they would each mint a key and leave orphans behind.
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Attempts one silent recovery. Returns true when a usable key is in place afterwards -
        /// either because this call minted one, or because a concurrent call already did.
        /// </summary>
        /// <param name="keyThatFailed">The key the caller was using when it got the 401, so a
        /// caller that lost the race does not mint a second key needlessly.</param>
        public static async Task<bool> TryRecoverAsync(string? keyThatFailed, CancellationToken ct = default)
        {
            Log.Info("[KeyRecovery] TryRecoverAsync: entry");
            await Gate.WaitAsync(ct);
            try
            {
                var cred = DevThrottleAccount.Load();

                // Someone else recovered while this call waited on the gate.
                if (cred?.ApiKey is { Length: > 0 } current
                    && !string.IsNullOrEmpty(keyThatFailed)
                    && !string.Equals(current, keyThatFailed, StringComparison.Ordinal))
                {
                    Log.Info("[KeyRecovery] TryRecoverAsync: another call already recovered; reusing its key");
                    return true;
                }

                if (cred?.RefreshToken is not { Length: > 0 })
                {
                    Log.Info("[KeyRecovery] TryRecoverAsync: no refresh token stored; cannot recover silently");
                    return false;
                }

                var session = await RefreshSessionAsync(cred.RefreshToken, ct);
                if (session is null)
                {
                    Log.Info("[KeyRecovery] TryRecoverAsync: refresh failed; interactive sign-in required");
                    return false;
                }

                string newKey = await DevThrottleSignIn.MintKeyAsync(
                    DevThrottleAccount.AuthBaseUrl.TrimEnd('/'), session.AccessToken, ct);

                DevThrottleAccount.Save(new DevThrottleCredential
                {
                    ApiKey = newKey,
                    AccessToken = session.AccessToken,
                    // Supabase ROTATES the refresh token on every use. Persisting the new one is
                    // what makes a second recovery possible; keeping the old one would work once.
                    RefreshToken = string.IsNullOrEmpty(session.RefreshToken) ? cred.RefreshToken : session.RefreshToken,
                    Email = cred.Email,
                });

                AccountState.Refresh();
                Log.Info("[KeyRecovery] TryRecoverAsync: recovered - new key minted and saved");
                return true;
            }
            catch (Exception ex)
            {
                // Recovery is an optimisation on top of the real error path. Its failure must not
                // replace the caller's 401, which is what the user actually needs to see.
                Log.Info($"[KeyRecovery] TryRecoverAsync FAILED: {ex.Message}");
                return false;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Renews just the Supabase ACCESS token (issue #134) - no new dt_ key.
        ///
        /// The access token is what account endpoints use, and it lives about an hour. The dt_ key
        /// is unaffected and keeps working, which is why an expired access token must never be
        /// treated as being signed out: it makes the credit balance unreadable and nothing else.
        /// Before this, that single hour was the whole reason Settings said "reconnect to refresh
        /// your account balance" for the rest of the day.
        /// </summary>
        public static async Task<bool> TryRefreshAccessTokenAsync(CancellationToken ct = default)
        {
            Log.Info("[KeyRecovery] TryRefreshAccessTokenAsync: entry");
            await Gate.WaitAsync(ct);
            try
            {
                var cred = DevThrottleAccount.Load();
                if (cred?.RefreshToken is not { Length: > 0 })
                {
                    Log.Info("[KeyRecovery] TryRefreshAccessTokenAsync: no refresh token stored");
                    return false;
                }

                var session = await RefreshSessionAsync(cred.RefreshToken, ct);
                if (session is null) return false;

                DevThrottleAccount.Save(new DevThrottleCredential
                {
                    ApiKey = cred.ApiKey,          // untouched - the inference key is still good
                    AccessToken = session.AccessToken,
                    RefreshToken = string.IsNullOrEmpty(session.RefreshToken) ? cred.RefreshToken : session.RefreshToken,
                    Email = cred.Email,
                });

                Log.Info("[KeyRecovery] TryRefreshAccessTokenAsync: access token renewed");
                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"[KeyRecovery] TryRefreshAccessTokenAsync FAILED: {ex.Message}");
                return false;
            }
            finally
            {
                Gate.Release();
            }
        }

        internal sealed record Session(string AccessToken, string? RefreshToken);

        /// <summary>
        /// Exchanges a Supabase refresh token for a fresh session. Returns null when the token is
        /// no longer valid - the caller then falls through to the interactive prompt.
        /// </summary>
        private static async Task<Session?> RefreshSessionAsync(string refreshToken, CancellationToken ct)
        {
            string url = DevThrottleAccount.SupabaseUrl.TrimEnd('/') + "/auth/v1/token?grant_type=refresh_token";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("apikey", DevThrottleAccount.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevThrottleAccount.SupabaseAnonKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { refresh_token = refreshToken }), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"[KeyRecovery] RefreshSessionAsync: status={(int)resp.StatusCode} - refresh token not accepted");
                return null;
            }

            return ParseSession(body);
        }

        /// <summary>
        /// Reads access_token/refresh_token out of a Supabase token response. Pure, so the parsing
        /// is unit-testable without a live Supabase call.
        /// </summary>
        internal static Session? ParseSessionForTest(string body) => ParseSession(body);

        private static Session? ParseSession(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            string? refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            return new Session(access, refresh);
        }
    }
}
