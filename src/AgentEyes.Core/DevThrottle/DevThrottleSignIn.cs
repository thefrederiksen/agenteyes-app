using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentEyes.DevThrottle
{
    /// <summary>
    /// Loopback browser sign-in for DevThrottle, ported from the proven Director flow
    /// (devthrottle_internal/tools/DevThrottleDesktop/Services/LoopbackSignIn.cs):
    ///   1. Listen on http://127.0.0.1:&lt;ephemeral&gt;/devthrottle-login-callback/
    ///   2. Open the browser to {AuthBase}/signin?redirect_uri=&lt;loopback&gt;&amp;state=&lt;nonce&gt;
    ///   3. The website signs the user in and redirects back with ?access_token&amp;refresh_token&amp;state
    ///   4. Exchange the Supabase JWT for a dt_ key via POST {AuthBase}/api/v1/keys
    /// On success the credential is saved (DPAPI) and returned.
    ///
    /// The callback PATH must be exactly "/devthrottle-login-callback/": the website
    /// (website/src/lib/loopback.js) strictly allow-lists that one loopback path before it
    /// will ever append a token, to close the token-exfiltration hole. Any other path is
    /// rejected and no session is handed back.
    /// </summary>
    internal static class DevThrottleSignIn
    {
        private const string CallbackPath = "/devthrottle-login-callback/";

        /// <summary>
        /// Runs the sign-in handshake, saves the credential, and returns it. <paramref name="status"/>
        /// receives human-readable progress for the UI. Throws DevThrottleException on failure/timeout.
        /// </summary>
        public static async Task<DevThrottleCredential> SignInAsync(Action<string>? status = null, CancellationToken ct = default)
        {
            Log.Info("[DevThrottleSignIn] SignInAsync: starting");
            string authBase = DevThrottleAccount.AuthBaseUrl.TrimEnd('/');
            int port = FreeLoopbackPort();
            string redirect = $"http://127.0.0.1:{port}{CallbackPath}";
            string state = Guid.NewGuid().ToString("N");

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            string url = $"{authBase}/signin?redirect_uri={Uri.EscapeDataString(redirect)}&state={state}";
            status?.Invoke("Opening your browser to sign in to DevThrottle...");
            OpenBrowser(url);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new DevThrottleException("Sign-in timed out. Please try again.");
            }

            var q = ctx.Request.QueryString;
            string? access = q["access_token"];
            string? refresh = q["refresh_token"];
            string? gotState = q["state"];
            bool ok = !string.IsNullOrEmpty(access) && !string.IsNullOrEmpty(refresh) && gotState == state;

            await WriteBrowserResponse(ctx, ok, ct);
            listener.Stop();

            if (!ok)
                throw new DevThrottleException("Sign-in did not return a valid session (state mismatch or missing tokens).");

            status?.Invoke("Signed in. Creating your DevThrottle key...");
            string email = JwtEmail(access!) ?? "(signed in)";
            string apiKey = await MintKeyAsync(authBase, access!, ct);

            var cred = new DevThrottleCredential { ApiKey = apiKey, AccessToken = access, RefreshToken = refresh, Email = email };
            DevThrottleAccount.Save(cred);
            Log.Info($"[DevThrottleSignIn] SignInAsync: complete email={email}");
            return cred;
        }

        /// <summary>
        /// Exchanges a Supabase JWT for a dt_ key. Internal (not private) so silent recovery can
        /// reuse the one implementation rather than duplicating it (issue #131).
        /// </summary>
        internal static async Task<string> MintKeyAsync(string authBase, string jwt, CancellationToken ct)
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, authBase + "/api/v1/keys");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            req.Content = new StringContent("{\"name\":\"AgentEyes\"}", Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new DevThrottleException($"Could not create a DevThrottle key (HTTP {(int)resp.StatusCode}): {body}".Trim(), (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("data").GetProperty("key").GetString()
                ?? throw new DevThrottleException("Key response did not contain a key.");
        }

        private static async Task WriteBrowserResponse(HttpListenerContext ctx, bool ok, CancellationToken ct)
        {
            string html = ok
                ? "<html><body style='font-family:system-ui;background:#0f1115;color:#e6e8ec;text-align:center;padding-top:80px'>"
                  + "<h2>Signed in to DevThrottle</h2><p>You can close this tab and return to AgentEyes.</p></body></html>"
                : "<html><body style='font-family:system-ui;text-align:center;padding-top:80px'>"
                  + "<h2>Sign-in failed</h2><p>Return to AgentEyes and try again.</p></body></html>";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf, ct);
            ctx.Response.Close();
        }

        private static int FreeLoopbackPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static void OpenBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch (Exception ex) { Log.Info($"[DevThrottleSignIn] OpenBrowser FAILED (url surfaced in UI): {ex.Message}"); }
        }

        private static string? JwtEmail(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
            }
            catch { return null; }
        }
    }
}
