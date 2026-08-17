using AgentEyes.DevThrottle;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #131: parsing of the Supabase refresh response. The rotated refresh_token is the part
    /// that matters - dropping it would make recovery work exactly once and then fail silently.
    /// </summary>
    public class KeyRecoveryTests
    {
        [Fact]
        public void ParseSession_FullResponse_ReadsBothTokens()
        {
            var s = KeyRecovery.ParseSessionForTest(
                """{"access_token":"new-access","refresh_token":"rotated","token_type":"bearer","expires_in":3600}""");

            Assert.NotNull(s);
            Assert.Equal("new-access", s!.AccessToken);
            Assert.Equal("rotated", s.RefreshToken);
        }

        [Fact]
        public void ParseSession_NoRefreshToken_StillUsableAccessToken()
        {
            var s = KeyRecovery.ParseSessionForTest("""{"access_token":"new-access"}""");

            Assert.NotNull(s);
            Assert.Equal("new-access", s!.AccessToken);
            Assert.Null(s.RefreshToken);
        }

        [Fact]
        public void ParseSession_MissingAccessToken_Null()
        {
            Assert.Null(KeyRecovery.ParseSessionForTest("""{"refresh_token":"rotated"}"""));
        }

        [Fact]
        public void ParseSession_EmptyAccessToken_Null()
        {
            Assert.Null(KeyRecovery.ParseSessionForTest("""{"access_token":""}"""));
        }

        [Fact]
        public void ParseSession_ErrorBody_Null()
        {
            Assert.Null(KeyRecovery.ParseSessionForTest(
                """{"error":"invalid_grant","error_description":"Invalid Refresh Token"}"""));
        }

        [Fact]
        public void SupabaseAnonKey_IsTheAnonRole_NotAServiceKey()
        {
            // A service-role key here would be a credential leak in a shipped desktop app. The
            // anon key is public (it ships in the website bundle); assert we embedded that one.
            string[] parts = DevThrottleAccount.SupabaseAnonKey.Split('.');
            Assert.Equal(3, parts.Length);

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            string json = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(payload));

            Assert.Contains("\"role\":\"anon\"", json);
            Assert.DoesNotContain("service_role", json);
        }
    }
}
