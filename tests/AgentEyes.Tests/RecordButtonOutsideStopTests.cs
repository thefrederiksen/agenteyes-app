using System;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #100: a recording stopped from the tray, REST or any path other than the main window's
    /// own button left the button on STOP over an idle engine. The window now hears every clean stop
    /// (RecordingService.RecordingStopped) and resets itself, except while its own stop is running.
    ///
    /// Source facts, like <see cref="StopFailureSourceTests"/>: the test assembly cannot reference
    /// AgentEyes.App (a WinExe).
    /// </summary>
    public sealed class RecordButtonOutsideStopTests
    {
        private const string Window = @"src\AgentEyes.App\MainWindow.xaml.cs";

        [Fact]
        public void MainWindow_SubscribesToRecordingStopped_AndUnsubscribesOnClose()
        {
            string src = RepoSource.Read(Window);

            Assert.Contains("_svc.RecordingStopped += OnRecordingStopped;", src, StringComparison.Ordinal);
            Assert.Contains("_svc.RecordingStopped -= OnRecordingStopped;", src, StringComparison.Ordinal);
        }

        [Fact]
        public void OnRecordingStopped_OutsideStop_ResetsTheButtonOnTheUiThread()
        {
            string body = RepoSource.MethodBody(RepoSource.Read(Window), "private void OnRecordingStopped()");

            Assert.Contains("Dispatcher.BeginInvoke", body, StringComparison.Ordinal);
            Assert.Contains("RecordButton.IsEnabled = true;", body, StringComparison.Ordinal);
            Assert.Contains("SetRecordingUi(false);", body, StringComparison.Ordinal);
            Assert.Contains("UpdateSummary();", body, StringComparison.Ordinal);   // puts REC / CAPTURE back
            Assert.Contains("Log.Info(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void OnRecordingStopped_WindowsOwnStopOrANewRecording_LeftAlone()
        {
            string body = RepoSource.MethodBody(RepoSource.Read(Window), "private void OnRecordingStopped()");

            Assert.Contains("if (_stoppingHere || _svc.IsRecording) return;", body, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("private async Task StopAsync()")]
        [InlineData("private async Task DiscardAsync()")]
        public void OwnStop_SetsTheFlagFirst_AndClearsItRightAfterTheReset(string signature)
        {
            string body = RepoSource.MethodBody(RepoSource.Read(Window), signature);

            int set = body.IndexOf("_stoppingHere = true;", StringComparison.Ordinal);
            int reset = body.IndexOf("SetRecordingUi(false);", StringComparison.Ordinal);
            int cleared = body.IndexOf("_stoppingHere = false;", StringComparison.Ordinal);

            Assert.True(set >= 0 && reset > set, $"{signature}: the flag must be set before the stop runs");
            Assert.True(cleared > reset, $"{signature}: the flag must be cleared after the window resets itself");
            // Cleared BEFORE waiting on post-processing, so a later outside stop is not ignored while
            // this recording is still transcribing.
            int postProcessing = body.IndexOf("await stopped.PostProcessing", StringComparison.Ordinal);
            if (postProcessing >= 0)
                Assert.True(cleared < postProcessing, $"{signature}: the flag must not be held across post-processing");
        }
    }
}
