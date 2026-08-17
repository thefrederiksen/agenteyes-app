using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEyes.DevThrottle
{
    /// <summary>
    /// The locally-stored DevThrottle credential: the dt_ inference key plus the
    /// refresh token and the signed-in email (for display). This is the ONLY secret
    /// the client holds; it is our issued key, not a provider key (see
    /// devthrottle_internal/docs/architecture/api-key-model.md).
    /// </summary>
    internal sealed class DevThrottleCredential
    {
        public string ApiKey { get; set; } = "";
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? Email { get; set; }
    }

    /// <summary>
    /// Endpoints and DPAPI-protected local storage for the DevThrottle account.
    /// AgentEyes runs 100% on DevThrottle: with no valid credential the app is gated
    /// (issue #87). The credential blob is encrypted with DPAPI (CurrentUser) so the
    /// dt_ key never sits in plaintext on disk.
    /// </summary>
    internal static class DevThrottleAccount
    {
        // The DevThrottle chat/audio proxy surface lives under /api/v1.
        public const string ApiBaseUrl = "https://devthrottle.com/api/v1";
        // The website root: sign-in and key minting hang off this.
        public const string AuthBaseUrl = "https://devthrottle.com";
        public const string CreditsUrl = "https://devthrottle.com/account/billing";

        // DevThrottle's Supabase project, used ONLY to exchange a stored refresh token for a fresh
        // session when the dt_ key has been revoked (issue #131). The anon key is public - it ships
        // in the website's browser bundle - so holding it here exposes nothing that a visitor to
        // devthrottle.com does not already have.
        public const string SupabaseUrl = "https://ompujpfrglgqvqprilxa.supabase.co";
        public const string SupabaseAnonKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9tcHVqcGZyZ2xncXZxcHJpbHhhIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODE2MTQ4OTksImV4cCI6MjA5NzE5MDg5OX0." +
            "YKq4AK2af5O0HbI9Q6ujaFrvRbLDeY8HSn-OdK6RAgo";

        private static string CredPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentEyes", "devthrottle.cred");

        /// <summary>True when a credential with a non-empty dt_ key is stored.</summary>
        public static bool IsSignedIn => Load()?.ApiKey is { Length: > 0 };

        /// <summary>Loads and decrypts the stored credential, or null when none/unreadable.</summary>
        public static DevThrottleCredential? Load()
        {
            try
            {
                if (!File.Exists(CredPath)) return null;
                byte[] protectedBytes = File.ReadAllBytes(CredPath);
                byte[] json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                var cred = JsonSerializer.Deserialize<DevThrottleCredential>(Encoding.UTF8.GetString(json));
                if (cred?.ApiKey is not { Length: > 0 })
                {
                    Log.Info("[DevThrottleAccount] Load: credential present but empty");
                    return null;
                }
                return cred;
            }
            catch (Exception ex)
            {
                Log.Info($"[DevThrottleAccount] Load FAILED: {ex.Message}");
                return null;
            }
        }

        /// <summary>Encrypts (DPAPI, CurrentUser) and stores the credential.</summary>
        public static void Save(DevThrottleCredential cred)
        {
            if (cred is null) throw new ArgumentNullException(nameof(cred));
            if (string.IsNullOrWhiteSpace(cred.ApiKey))
                throw new ArgumentException("Refusing to store a DevThrottle credential without a key.", nameof(cred));

            Log.Info($"[DevThrottleAccount] Save: email={cred.Email ?? "(none)"}");
            Directory.CreateDirectory(Path.GetDirectoryName(CredPath)!);
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cred));
            byte[] protectedBytes = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CredPath, protectedBytes);
        }

        /// <summary>Removes the stored credential (sign out).</summary>
        public static void Clear()
        {
            Log.Info("[DevThrottleAccount] Clear");
            try { if (File.Exists(CredPath)) File.Delete(CredPath); }
            catch (Exception ex) { Log.Info($"[DevThrottleAccount] Clear FAILED: {ex.Message}"); }
        }

        /// <summary>The stored dt_ key, or throws when not signed in (fail explicit, no fallback).</summary>
        public static string RequireApiKey()
        {
            var cred = Load();
            if (cred?.ApiKey is not { Length: > 0 })
                throw new DevThrottleException(
                    "Not signed in to DevThrottle. Open Settings > DevThrottle Account and sign in.", 401);
            return cred.ApiKey;
        }
    }
}
