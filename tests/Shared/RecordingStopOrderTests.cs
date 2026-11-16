using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// Issue #113. ONE source file, compiled into all three suites through a <Compile Include="..\Shared\..."
// Link> in each csproj, because the shutdown order is platform-neutral.
//
// WHAT THESE GUARD. A recording the capture watchdog ended must kill the capture BEFORE it asks any
// other writer to stop. Measured on devthrottle-mac-mini on 2026-11-15, on the tree that merges this
// issue with #114: with the microphone writer's stop in front of the kill, a SIGSTOPped screen ffmpeg
// left /status in "finalizing" for the whole 330 s observation, the frozen child was never killed, and
// the captured file was never finalized - while the watchdog's own verdict was correct and on time.
// See RecordingService.OrderStopSteps for the sampled cause.
//
// BOTH ORDERS ARE ASSERTED, so this cannot be satisfied by always putting the capture first: a healthy
// stop must still drain the audio writers before the screen capture, because the mux reads their files.
namespace AgentEyes.Tests.Shared
{
    using RecordingService = global::AgentEyes.RecordingService;
    using RecordingStopStep = global::AgentEyes.RecordingStopStep;

    public sealed class RecordingStopOrderTests
    {
        private static RecordingStopStep Step(string name) =>
            new RecordingStopStep(name, () => { }, () => { });

        private static List<string> Order(bool faulted, bool camera = false) =>
            RecordingService.OrderStopSteps(
                    Step("video"), faulted, Step("audio"), Step("loopback"),
                    camera ? Step("camera") : null)
                .Select(s => s.Name).ToList();

        [Fact]
        public void OrderStopSteps_WatchdogFaultedTheCapture_KillsTheCaptureFirst()
        {
            var order = Order(faulted: true);

            Assert.Equal(new[] { "video", "audio", "loopback" }, order);
            // Stated as the property that matters, not just the list: nothing may be asked to stop
            // in front of the kill, because on macOS a stopped capture blocks whatever is.
            Assert.Equal(0, order.IndexOf("video"));
        }

        [Fact]
        public void OrderStopSteps_HealthyStop_DrainsTheAudioWritersBeforeTheCapture()
        {
            var order = Order(faulted: false);

            Assert.Equal(new[] { "audio", "loopback", "video" }, order);
            Assert.True(order.IndexOf("audio") < order.IndexOf("video"),
                "a healthy stop must drain the audio writers first - the mux reads their files");
        }

        [Fact]
        public void OrderStopSteps_TheCameraIsLast_WhicheverWayTheCaptureEnded()
        {
            Assert.Equal(new[] { "video", "audio", "loopback", "camera" }, Order(faulted: true, camera: true));
            Assert.Equal(new[] { "audio", "loopback", "video", "camera" }, Order(faulted: false, camera: true));
        }

        [Fact]
        public void OrderStopSteps_EveryWriterThatExistsIsStoppedExactlyOnce()
        {
            // The instrument check for the three tests above: an ordering that DROPPED a writer would
            // satisfy an index comparison while leaving a microphone open for the life of the process.
            foreach (bool faulted in new[] { true, false })
            {
                var order = Order(faulted, camera: true);
                Assert.Equal(4, order.Count);
                Assert.Equal(order.Count, order.Distinct().Count());
                Assert.All(new[] { "video", "audio", "loopback", "camera" }, n => Assert.Contains(n, order));
            }
        }

        [Fact]
        public void OrderStopSteps_NoWriters_IsAnEmptySequence()
        {
            Assert.Empty(RecordingService.OrderStopSteps(null, faulted: true, null, null, null));
            Assert.Empty(RecordingService.OrderStopSteps(null, faulted: false, null, null, null));
        }
    }
}
