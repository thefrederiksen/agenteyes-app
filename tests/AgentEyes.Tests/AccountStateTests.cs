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
        // Issue #173: the fixtures are synthetic addresses on reserved domains (RFC 2606 / RFC 6761),
        // never a real person's. Two DIFFERENT addresses run through the same assertions, and each
        // assertion checks the value it was given - so a Describe that stopped propagating the address
        // (or printed a fixed one) fails at least one case.
        [Theory]
        [InlineData("first.tester@example.com")]
        [InlineData("second-tester@example.invalid")]
        public void Describe_SignedInWithEmail_ShowsEmailInTooltipAndAutomationName(string email)
        {
            var d = AccountState.Describe(true, email);

            Assert.Equal("Signed in", d.Label);
            Assert.Contains(email, d.ToolTip);
            Assert.Contains(email, d.AutomationName);
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
            const string staleEmail = "stale.tester@example.invalid";
            var d = AccountState.Describe(false, staleEmail);

            Assert.DoesNotContain(staleEmail, d.ToolTip);
            Assert.DoesNotContain(staleEmail, d.AutomationName);
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
