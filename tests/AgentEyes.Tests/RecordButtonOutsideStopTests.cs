using System;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #100: a recording stopped from the tray, REST or any path other than the main window's
    /// own button left the button on STOP over an idle engine. The window now resets itself when the
    /// engine goes idle - on the clean-stop event (fast path) and on its own tick (the guarantee,
    /// because a FAILED stop raises no event) - except while its own stop is running.
    ///
    /// Source facts, like <see cref="StopFailureSourceTests"/>: the test assembly cannot reference
    /// AgentEyes.App (a WinExe).
    /// </summary>
    public sealed class RecordButtonOutsideStopTests
    {
        private const string Window = @"src\AgentEyes.App\MainWindow.xaml.cs";

        private static string Body(string signature) => RepoSource.MethodBody(RepoSource.Read(Window), signature);

        [Fact]
        public void MainWindow_SubscribesToRecordingStopped_AndUnsubscribesOnClose()
        {
            string src = RepoSource.Read(Window);

            Assert.Contains("_svc.RecordingStopped += OnRecordingStopped;", src, StringComparison.Ordinal);
            Assert.Contains("_svc.RecordingStopped -= OnRecordingStopped;", src, StringComparison.Ordinal);
        }

        [Fact]
        public void OnRecordingStopped_RaisedOnTheStoppingThread_MarshalsToTheReset()
        {
            string body = Body("private void OnRecordingStopped()");

            Assert.Contains("Dispatcher.BeginInvoke", body, StringComparison.Ordinal);
            Assert.Contains("ResetAfterOutsideStop(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void OnTick_EngineIdle_ResetsEvenWhenAFailedStopRaisedNoEvent()
        {
            // The review's blocking case: RecordingService.Stop throws on a lost writer, goes idle and
            // never raises RecordingStopped. Only the tick can notice that.
            string body = Body("private void OnTick(object? sender, EventArgs e)");

            Assert.Contains("if (!_svc.IsRecording) { ResetAfterOutsideStop(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void ResetAfterOutsideStop_PutsTheWindowBackToRec()
        {
            string body = Body("private void ResetAfterOutsideStop(string trigger)");

            Assert.Contains("_timer.Stop();", body, StringComparison.Ordinal);
            Assert.Contains("RecordButton.IsEnabled = true;", body, StringComparison.Ordinal);
            Assert.Contains("SetRecordingUi(false);", body, StringComparison.Ordinal);
            Assert.Contains("UpdateSummary();", body, StringComparison.Ordinal);   // puts REC / CAPTURE back
            Assert.Contains("Log.Info(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void ResetAfterOutsideStop_WindowsOwnStopOrANewRecordingOrAlreadyAtRec_LeftAlone()
        {
            string body = Body("private void ResetAfterOutsideStop(string trigger)");

            int guard = body.IndexOf("if (_stoppingHere || _svc.IsRecording) return;", StringComparison.Ordinal);
            int idle = body.IndexOf("if (!_timer.IsEnabled) return;", StringComparison.Ordinal);
            int reset = body.IndexOf("SetRecordingUi(false);", StringComparison.Ordinal);

            Assert.True(guard >= 0 && idle >= 0, "both guards must be present");
            Assert.True(guard < reset && idle < reset, "the guards must run before anything is reset");
        }

        [Theory]
        [InlineData("private async Task StopAsync()")]
        [InlineData("private async Task DiscardAsync()")]
        public void OwnStop_FlagSetFirst_ClearedInAFinallyRightAfterTheReset(string signature)
        {
            string body = Body(signature);

            int set = body.IndexOf("_stoppingHere = true;", StringComparison.Ordinal);
            int reset = body.IndexOf("SetRecordingUi(false);", StringComparison.Ordinal);
            int @finally = body.IndexOf("finally", reset < 0 ? 0 : reset, StringComparison.Ordinal);
            int cleared = body.IndexOf("_stoppingHere = false;", StringComparison.Ordinal);

            Assert.True(set >= 0 && reset > set, $"{signature}: the flag must be set before the stop runs");
            Assert.True(@finally > reset && cleared > @finally,
                $"{signature}: the flag must be cleared in a finally after the reset, so a throw cannot leave it stuck");
            // Cleared BEFORE waiting on post-processing, so a later outside stop is not ignored while
            // this recording is still transcribing.
            int postProcessing = body.IndexOf("await stopped.PostProcessing", StringComparison.Ordinal);
            if (postProcessing >= 0)
                Assert.True(cleared < postProcessing, $"{signature}: the flag must not be held across post-processing");
        }
    }
}
