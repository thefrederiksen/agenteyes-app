using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class MacScreenshotFailureTests
    {
        [Fact]
        public void Failure_ExitZeroAndFileWritten_IsNoFailure()
        {
            Assert.Null(Screenshot.Failure(0, "", fileWritten: true));
        }

        [Fact]
        public void Failure_ExitZeroButNoFile_IsAFailure()
        {
            // screencapture can succeed and write nothing when the permission is refused. Trusting
            // the exit code alone would report a capture that never happened.
            string? why = Screenshot.Failure(0, "", fileWritten: false);

            Assert.NotNull(why);
            Assert.Contains("wrote no file", why);
            Assert.Contains("Screen Recording permission", why);
        }

        [Fact]
        public void Failure_NonZeroExit_KeepsTheToolsOwnWordsAndNamesTheLikelyCause()
        {
            // The real message from this machine with the permission refused.
            string? why = Screenshot.Failure(1, "could not create image from rect\n", fileWritten: false);

            Assert.NotNull(why);
            Assert.Contains("exit 1", why);
            Assert.Contains("could not create image from rect", why);
            Assert.Contains("Privacy and Security", why);
        }

        [Fact]
        public void Failure_NonZeroExitWithNoStderr_StillExplainsItself()
        {
            string? why = Screenshot.Failure(2, "   ", fileWritten: false);

            Assert.NotNull(why);
            Assert.Contains("exit 2", why);
            Assert.DoesNotContain("):", why);
        }

        [Fact]
        public void Failure_NonZeroExitButAFileExists_IsStillAFailure()
        {
            // A leftover file from an earlier run must not make a failed capture look successful.
            Assert.NotNull(Screenshot.Failure(1, "boom", fileWritten: true));
        }

        [Fact]
        public void Failure_SaysThePermissionCannotBeGrantedByAgentEyes()
        {
            string? why = Screenshot.Failure(1, "", fileWritten: false);

            Assert.Contains("only accepts that from the person at the keyboard", why);
        }
    }
}
