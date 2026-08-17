using System;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #152, criterion 6: "is this app safe to restart" must mean no capture AND no
    /// post-recording work.
    ///
    /// The predicate used to read only <c>RecordingService.IsRecording</c>. An auto-update defers its
    /// restart while a session is active and completes it when the session ends - but the capture
    /// ends MINUTES before the work does, so the restart fired into the gap and killed the mux and
    /// the transcription that had not started yet. The recording was left with raw media and no
    /// transcript, and nothing recovered it.
    /// </summary>
    public sealed class SessionReadinessTests
    {
        [Fact]
        public void IsBusy_NothingHappening_False()
        {
            Assert.False(SessionReadiness.IsBusy(capturing: false, postRecordingWorkInFlight: false));
        }

        [Fact]
        public void IsBusy_Capturing_True()
        {
            Assert.True(SessionReadiness.IsBusy(capturing: true, postRecordingWorkInFlight: false));
        }

        [Fact]
        public void IsBusy_CaptureIdleButPostProcessingStillRunning_True()
        {
            // THE case. Capture is over, the mux and the transcription are not.
            Assert.True(SessionReadiness.IsBusy(capturing: false, postRecordingWorkInFlight: true));
        }

        [Fact]
        public void IsBusy_BothAtOnce_True()
        {
            // A recording started while the previous one is still being packaged.
            Assert.True(SessionReadiness.IsBusy(capturing: true, postRecordingWorkInFlight: true));
        }

        [Fact]
        public void AppReadinessPredicate_AsksBothQuestions()
        {
            // A source fact: the test assembly cannot reference AgentEyes.App (a WinExe), and the
            // predicate being wired to only ONE of the two inputs is precisely the defect. If
            // IsSessionActive ever goes back to reading capture state alone, this fails.
            string app = RepoSource.Read(@"src\AgentEyes.App\App.xaml.cs");
            string predicate = RepoSource.MethodBody(app, "private bool IsSessionActive()");

            Assert.Contains("SessionReadiness.IsBusy(", predicate, StringComparison.Ordinal);
            Assert.Contains("IsRecording", predicate, StringComparison.Ordinal);
            Assert.Contains("PostRecording.IsBusy", predicate, StringComparison.Ordinal);
        }

        [Fact]
        public void DeferredRestart_IsRetriedWhenThePostRecordingWorkFinishes()
        {
            // Deferring is only half the fix: something has to complete the restart afterwards. The
            // capture's own RecordingStopped signal fires too early for that, so the app also listens
            // for the moment no post-recording work is left.
            string app = RepoSource.Read(@"src\AgentEyes.App\App.xaml.cs");

            Assert.Contains("PostRecording.WorkIdle += UpdateChecker.OnSessionEnded;", app, StringComparison.Ordinal);
        }

        [Fact]
        public void KeepStop_MarksWorkInFlightBeforeItStopsTheCapture()
        {
            // RecordingService.Stop raises RecordingStopped inside the call, and a deferred restart
            // listens to it. The ticket has to be taken BEFORE that, or the app answers "idle" in the
            // gap between the capture ending and the background sequence starting.
            string stop = RepoSource.Read(@"src\AgentEyes.App\RecordingStop.cs");
            string keep = RepoSource.MethodBody(stop, "public static StoppedRecording Keep(");

            int ticket = keep.IndexOf("PostRecording.TrackWork(", StringComparison.Ordinal);
            int stopCall = keep.IndexOf("svc.Stop()", StringComparison.Ordinal);

            Assert.True(ticket >= 0, "Keep must take a post-recording work ticket");
            Assert.True(stopCall >= 0, "Keep must still be the caller of RecordingService.Stop");
            Assert.True(ticket < stopCall, "the work ticket must be taken before the capture is stopped");
        }
    }
}
