using System;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #142: WHEN the repair passes are allowed to run. Both passes used to fire only at app
    /// start, so on an always-on recorder they effectively never ran; these are the rules that let
    /// them run continuously without stepping on a recording or on each other.
    /// </summary>
    public class RepairScheduleTests
    {
        [Fact]
        public void Interval_IsBetweenFiveAndSixtyMinutes()
        {
            // The band the spec fixed (issue #142 criterion 4): often enough that a lost title or
            // thumbnail is repaired inside the same working session, rarely enough that scanning
            // costs nothing.
            Assert.InRange(RepairSchedule.Interval,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
        }

        [Fact]
        public void ShouldRunNow_NotRecording_True()
        {
            Assert.True(RepairSchedule.ShouldRunNow(isRecording: false));
        }

        [Fact]
        public void ShouldRunNow_WhileRecording_False()
        {
            // Repairs spend CPU on ffmpeg and a network call; capture quality comes first, and the
            // next tick picks the same recordings up.
            Assert.False(RepairSchedule.ShouldRunNow(isRecording: true));
        }

        [Fact]
        public void TryEnter_FirstCaller_TakesTheGate()
        {
            var gate = new RepairGate();

            Assert.True(gate.TryEnter());
            Assert.True(gate.IsRunning);
        }

        [Fact]
        public void TryEnter_SecondCallerWhileRunning_ReturnsFalseImmediately()
        {
            // The core guard: a timer tick landing on top of a start-up pass, or on the pass that
            // runs when a recording finishes, must do nothing at all.
            var gate = new RepairGate();
            Assert.True(gate.TryEnter());

            Assert.False(gate.TryEnter());
            Assert.True(gate.IsRunning);   // still the FIRST pass; no second one started
        }

        [Fact]
        public void Exit_ReleasesTheGateForTheNextPass()
        {
            var gate = new RepairGate();
            gate.TryEnter();

            gate.Exit();

            Assert.False(gate.IsRunning);
            Assert.True(gate.TryEnter());
        }

        [Fact]
        public void Exit_WhenNotHeld_DoesNotThrowAndLeavesGateFree()
        {
            var gate = new RepairGate();

            gate.Exit();   // a finally block that never entered must be harmless

            Assert.False(gate.IsRunning);
            Assert.True(gate.TryEnter());
        }

        [Fact]
        public void TryEnter_ConcurrentCallers_ExactlyOneWins()
        {
            // The triggers really are on different threads/dispatch points; the gate must be
            // atomic, not a read-then-write.
            var gate = new RepairGate();
            int winners = 0;

            System.Threading.Tasks.Parallel.For(0, 64, _ =>
            {
                if (gate.TryEnter()) System.Threading.Interlocked.Increment(ref winners);
            });

            Assert.Equal(1, winners);
        }
    }
}
