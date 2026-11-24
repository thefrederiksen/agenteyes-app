using AgentEyes;
using AgentEyes.Platform.Linux;
using AgentEyes.Tests.Shared;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    /// <summary>
    /// Issue #221: the runtime refusal for a Wayland session with NO X display. Only that case is refused
    /// (there is no X screen to record); a Wayland session that also sets DISPLAY through Xwayland is not
    /// refused and records a black frame (measured on mutter 50.1, issue #227). The refusal names NO login
    /// session as a remedy, because the session it used to name is not installed by a default desktop
    /// install of current Ubuntu. Driven through Resolve so the process environment is never changed under
    /// the rest of the suite.
    /// </summary>
    public sealed class LinuxDisplayTests
    {
        [Fact]
        public void Resolve_WaylandDisplayWithoutXDisplay_ThrowsTheWaylandRefusal()
        {
            var ex = Assert.Throws<UsageException>(() => LinuxDisplay.Resolve(null, "wayland-0"));

            Assert.Equal(LinuxDisplay.WaylandRefusal, ex.Message);
            Assert.Contains("Wayland sessions are not supported", ex.Message);
            Assert.Contains("Supported Linux sessions", ex.Message);
        }

        [Fact]
        public void Resolve_WaylandRefusal_NamesNoLoginSession()
        {
            var ex = Assert.Throws<UsageException>(() => LinuxDisplay.Resolve("  ", "wayland-0"));

            Assert.DoesNotMatch(SessionRemedyGuardTests.SessionName, ex.Message);
            Assert.DoesNotMatch(SessionRemedyGuardTests.SessionInstruction, ex.Message);
            Assert.DoesNotContain("Xorg", ex.Message);
            Assert.DoesNotContain("log in with", ex.Message);
        }

        [Fact]
        public void Resolve_NoDisplayAndNoWaylandDisplay_ThrowsTheNoDisplayRefusal()
        {
            var ex = Assert.Throws<UsageException>(() => LinuxDisplay.Resolve(null, null));

            Assert.Equal(LinuxDisplay.NoDisplayRefusal, ex.Message);
        }

        [Fact]
        public void Resolve_XDisplaySet_ReturnsItTrimmedEvenWithAWaylandDisplay()
        {
            Assert.Equal(":0", LinuxDisplay.Resolve(" :0 ", "wayland-0"));
        }

        [Fact]
        public void WaylandRefusal_IsPlainAscii()
        {
            foreach (char c in LinuxDisplay.WaylandRefusal)
                Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }
}
