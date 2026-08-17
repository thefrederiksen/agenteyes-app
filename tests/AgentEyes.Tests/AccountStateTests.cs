using AgentEyes.DevThrottle;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #129: the sign-in indicator's state-to-text mapping. Kept pure so it is testable
    /// without a credential store - the disk-reading half of AccountState is exercised by the
    /// running-app proof instead.
    /// </summary>
    public class AccountStateTests
    {
        [Fact]
        public void Describe_SignedInWithEmail_ShowsEmailInTooltipAndAutomationName()
        {
            var d = AccountState.Describe(true, "soren@centerconsulting.com");

            Assert.Equal("Signed in", d.Label);
            Assert.Contains("soren@centerconsulting.com", d.ToolTip);
            Assert.Contains("soren@centerconsulting.com", d.AutomationName);
            Assert.Contains("Signed in", d.AutomationName);
        }

        [Fact]
        public void Describe_SignedInWithoutEmail_OmitsEmailAndDoesNotSayNull()
        {
            var d = AccountState.Describe(true, null);

            Assert.Equal("Signed in", d.Label);
            Assert.Equal("Signed in to DevThrottle", d.ToolTip);
            Assert.DoesNotContain("null", d.ToolTip);
            Assert.DoesNotContain("null", d.AutomationName);
        }

        [Fact]
        public void Describe_SignedInWithBlankEmail_TreatedAsNoEmail()
        {
            var d = AccountState.Describe(true, "   ");

            Assert.Equal("Signed in to DevThrottle", d.ToolTip);
            Assert.Equal("DevThrottle account: Signed in", d.AutomationName);
        }

        [Fact]
        public void Describe_NotSignedIn_SaysWhatIsDisabledAndWhatToDo()
        {
            var d = AccountState.Describe(false, null);

            Assert.Equal("Not signed in", d.Label);
            Assert.Contains("transcription", d.ToolTip);
            Assert.Contains("Click to sign in", d.ToolTip);
            Assert.Equal("DevThrottle account: Not signed in", d.AutomationName);
        }

        [Fact]
        public void Describe_NotSignedIn_NeverLeaksAStaleEmail()
        {
            // A rejected key still has an email on disk; the not-signed-in state must not present
            // it as though the account were live.
            var d = AccountState.Describe(false, "soren@centerconsulting.com");

            Assert.DoesNotContain("soren@centerconsulting.com", d.ToolTip);
            Assert.DoesNotContain("soren@centerconsulting.com", d.AutomationName);
        }

        [Fact]
        public void Describe_StatesAreDistinguishable()
        {
            var on = AccountState.Describe(true, "a@b.com");
            var off = AccountState.Describe(false, "a@b.com");

            Assert.NotEqual(on.Label, off.Label);
            Assert.NotEqual(on.ToolTip, off.ToolTip);
            Assert.NotEqual(on.AutomationName, off.AutomationName);
        }
    }
}
