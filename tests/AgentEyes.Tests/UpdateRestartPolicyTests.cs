using System;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Regression tests for issue #107: an AutoUpdate that swapped the on-disk single-file exe must
    /// never leave the running process serving from the replaced bundle (which manifests as
    /// FileNotFoundException for every not-yet-loaded assembly). The decision the running app makes
    /// after applying an update is the pure <see cref="UpdateRestartPolicy"/>: it always either
    /// restarts now or defers the restart - there is deliberately no "keep serving" outcome.
    /// </summary>
    public sealed class UpdateRestartPolicyTests
    {
        [Fact]
        public void Decide_NoActiveSession_RestartsNow()
        {
            Assert.Equal(UpdateApplyDecision.RestartNow, UpdateRestartPolicy.Decide(sessionActive: false));
        }

        [Fact]
        public void Decide_ActiveSession_DefersRestart()
        {
            // A recording session is in progress: the restart is deferred (so in-flight
            // capture is not truncated), NOT skipped - the caller completes it when the session ends.
            Assert.Equal(UpdateApplyDecision.DeferSessionActive, UpdateRestartPolicy.Decide(sessionActive: true));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Decide_AppliedUpdate_NeverLeavesProcessOnStaleBundle(bool sessionActive)
        {
            // The invariant: once the exe is replaced on disk, the decision is ALWAYS a restart action
            // (now or deferred). Neither branch is a no-op that would keep the stale process running.
            var decision = UpdateRestartPolicy.Decide(sessionActive);
            Assert.True(decision is UpdateApplyDecision.RestartNow or UpdateApplyDecision.DeferSessionActive);
        }

        [Fact]
        public void UpdateApplyDecision_HasNoKeepServingOutcome()
        {
            // Encodes the design: the enum offers exactly two safe end states - restart or defer.
            // If a "do nothing / keep running the replaced bundle" value were ever added, this fails.
            var values = Enum.GetValues<UpdateApplyDecision>();
            Assert.Equal(2, values.Length);
            Assert.Contains(UpdateApplyDecision.RestartNow, values);
            Assert.Contains(UpdateApplyDecision.DeferSessionActive, values);
        }
    }
}
