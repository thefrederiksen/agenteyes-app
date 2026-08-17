using System;
using AgentEyes.DevThrottle;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #138: a chat call must not inherit the Whisper upload's five-minute budget. One
    /// stalled titling request held an entire packaging pass for 300 seconds.
    /// </summary>
    public class ChatTimeoutTests
    {
        [Fact]
        public void ChatTimeout_IsFarShorterThanTheUploadBudget()
        {
            Assert.True(DevThrottleClient.ChatTimeout < TimeSpan.FromMinutes(5),
                "a chat call must not inherit the Whisper upload timeout");
        }

        [Fact]
        public void ChatTimeout_IsWellBelowTheStallThatCausedTheIssue()
        {
            // The observed failure held the pass for 300s. The budget must cut that down hard.
            Assert.True(DevThrottleClient.ChatTimeout <= TimeSpan.FromSeconds(150));
        }

        [Fact]
        public void ChatTimeout_SurvivesTheSlowestMeasuredTitlingCall()
        {
            // MEASURED, not guessed: real titling calls on 2026-08-10 took 39.2s and 9.5s. A first
            // attempt at this fix used 45s, which would have started killing legitimate titles.
            // The budget must clear the slowest observed call with real headroom.
            Assert.True(DevThrottleClient.ChatTimeout >= TimeSpan.FromSeconds(80),
                "must not be so tight that a normal slow titling call is killed");
        }
    }
}
