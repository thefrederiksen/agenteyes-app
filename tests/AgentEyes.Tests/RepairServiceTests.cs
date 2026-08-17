using System;
using System.IO;
using System.Threading;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #142: the repair passes must fire for EVERY recording, not only for one stopped from
    /// the window. The service listens to <see cref="PostRecording.Completed"/> - the single signal
    /// both stop paths raise - instead of being called from one of them.
    ///
    /// Every service in here is constructed as "currently recording", so each pass stops at
    /// <see cref="RepairSchedule.ShouldRunNow"/> before it touches the recordings folder: these
    /// tests verify the WIRING, and must never scan a real library or start ffmpeg.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public class RepairServiceTests
    {
        private static string SomeRecording() =>
            Path.Combine(Path.GetTempPath(), "agenteyes-repairsvc-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void StartupDelay_RunsTheFirstPassWithinTheFirstMinute()
        {
            // A repair that waits a full interval leaves a broken card on screen for the whole time
            // the user is looking at a freshly started app.
            Assert.InRange(RepairService.StartupDelay, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        }

        [Fact]
        public void RecordingCompleted_RunsARepairPass_WhicheverStopPathAnnouncedIt()
        {
            // The defect this replaces: the trigger lived in the window's stop handler, so a
            // recording driven through the REST Control API - the way agents record - never
            // reached it.
            int passes = 0;
            using var service = new RepairService(() => { Interlocked.Increment(ref passes); return true; });
            service.Start();

            PostRecording.NotifyCompleted(SomeRecording());

            Assert.True(passes >= 1, "a finished recording must trigger a repair pass");
        }

        [Fact]
        public void RecordingCompletedWhileRecording_DoesNotStartAPass()
        {
            // Criterion 6: repairs spend CPU on ffmpeg and a network call; capture comes first.
            var gate = new RepairGate();
            using var service = new RepairService(() => true);
            service.Start();

            PostRecording.NotifyCompleted(SomeRecording());

            // The pass stopped before taking the gate, so the gate is still free.
            Assert.False(service.Gate.IsRunning);
        }

        [Fact]
        public void Dispose_StopsListeningForFinishedRecordings()
        {
            int passes = 0;
            var service = new RepairService(() => { Interlocked.Increment(ref passes); return true; });
            service.Start();
            service.Dispose();
            int before = passes;

            PostRecording.NotifyCompleted(SomeRecording());

            Assert.Equal(before, passes);
        }

        [Fact]
        public void Constructor_WithoutARecordingStateReader_Throws()
        {
            // No fallback: without live capture state the service cannot honor "not while recording".
            Assert.Throws<ArgumentNullException>(() => new RepairService(null!));
        }
    }
}
