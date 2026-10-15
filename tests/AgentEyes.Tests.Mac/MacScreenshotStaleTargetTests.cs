using System.IO;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    /// <summary>
    /// Regression tests for issue 108, defect 1.
    ///
    /// Screenshot proves a capture happened by asking whether the file exists afterwards, because
    /// screencapture can exit ZERO and write nothing when the Screen Recording permission is
    /// refused. That proof is only worth anything if the file could not already have been there.
    /// Before the fix, nothing removed the target, so an earlier run's image satisfied the check
    /// and a capture that wrote nothing was reported as a success.
    /// </summary>
    public sealed class MacScreenshotStaleTargetTests
    {
        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "agenteyes-stale-" + Path.GetRandomFileName() + ".png");

        [Fact]
        public void ClearStaleTarget_AnEarlierFileIsThere_ItIsRemoved()
        {
            string path = TempPath();
            File.WriteAllText(path, "an earlier run's screenshot");
            Assert.True(File.Exists(path));

            try
            {
                Screenshot.ClearStaleTarget(path);

                // The whole point: after this, presence of the file means THIS run wrote it.
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ClearStaleTarget_NothingThere_DoesNothingAndDoesNotThrow()
        {
            string path = TempPath();
            Assert.False(File.Exists(path));

            Screenshot.ClearStaleTarget(path);

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void ClearStaleTarget_CalledTwice_IsStillQuiet()
        {
            string path = TempPath();
            File.WriteAllText(path, "x");
            try
            {
                Screenshot.ClearStaleTarget(path);
                Screenshot.ClearStaleTarget(path);
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
