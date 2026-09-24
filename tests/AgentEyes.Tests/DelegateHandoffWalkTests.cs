using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #75: the reachability walk's delegate-handoff rules, proven in both directions.
    ///
    /// Since issue #2 the walk reaches a type's static constructor whenever a member of the type is
    /// touched. The product's background workers are built in static constructors - on purpose, so
    /// their thread bodies stay off the callers' threads - and the walk then followed
    /// <c>new Thread(Loop)</c> and each parked <c>Action</c> straight into those thread bodies. Four
    /// HUD/preview guards went red on work that never runs on the guarded threads.
    ///
    /// The walk now resolves two delegate handoffs (see <c>CompiledCode.DelegateHandoffs</c>): a
    /// delegate handed to <c>new Thread(...)</c> is not an edge from its builder, and a delegate
    /// parked in a private field is an edge from every method that READS that field. Both remove
    /// edges, so this class holds the other half of the bargain - the guards still catch every real
    /// shape, measured with the exact scans the product guards run:
    ///
    ///  - REAL HUD-thread file writes (the <c>HudResponsivenessTests</c> scan) and REAL drain ->
    ///    shared-logger calls (the <c>PreviewTapTests</c> scan) compiled as decoys in
    ///    <c>DelegateHandoffDecoys.cs</c>, each of which MUST be reported.
    ///  - The product's own worker shapes, copied member for member, which must NOT be.
    /// </summary>
    public class DelegateHandoffWalkTests
    {
        private const string Ns = "AgentEyes.Tests.HandoffDecoys.";

        private static List<CompiledCode.CallSite> Offenders(string seed, Func<string, bool> offending)
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.TestAssembly, new[] { Ns + seed }), StringComparer.Ordinal);

            return CompiledCode
                .CallSites(CompiledCode.TestAssembly, offending)
                .Where(site => reached.Contains(site.Method))
                .ToList();
        }

        // ---- the HUD guard's scan: file writes on the UI thread ------------------------------

        /// <summary>
        /// NEGATIVE CONTROL for <c>HudResponsivenessTests.NothingTheHudsUiThreadCanReach_WritesAFile</c>:
        /// every real way a HUD click can end in a file write on its own thread is still reported,
        /// each at the method that actually writes.
        /// </summary>
        [Theory]
        [InlineData("HudDecoy::ClickTouchesAWritingTypeInitializer", "CctorWritingSettings::Load")]
        [InlineData("HudDecoy::ClickInvokesAParkedDelegate", "ParkedAndInvokedHere::WriteToDisk")]
        [InlineData("HudDecoy::ClickHandsADelegateToASameThreadInvoker", "HudDecoy::WriteOne")]
        [InlineData("HudDecoy::ClickBuildsAWriterWhoseCtorAlsoInvokesIt", "HudDecoy::WriteTwo")]
        public void HudFileWriteScan_RealUiThreadWrite_IsStillReported(string seed, string writer)
        {
            var offenders = Offenders(seed, CompiledCode.IsFileWriteApi);

            Assert.True(offenders.Any(o => o.Method == Ns + writer && o.Callee == "System.IO.File::WriteAllText"),
                $"The HUD guard's scan no longer reports a REAL UI-thread file write reached from {seed} "
                + $"through {writer}. The delegate-handoff rules (issue #75) have cut an edge that runs "
                + "on the caller's thread, which would let a real defect through every HUD guard. Reported:"
                + Environment.NewLine + CompiledCode.Describe(offenders));
        }

        /// <summary>The narrowness half: the product's background-writer shape (Config + its
        /// BackgroundFileWriter) reports nothing, because its write only ever runs on the writer's
        /// own thread. Before issue #75 this is exactly what went red.</summary>
        [Fact]
        public void HudFileWriteScan_TheProductsBackgroundWriterShape_IsNotReported()
        {
            var offenders = Offenders("HudDecoy::ClickQueuesThroughTheBackgroundWriter", CompiledCode.IsFileWriteApi);

            Assert.True(offenders.Count == 0,
                "Queuing a save through a background writer is reported as a UI-thread file write. The "
                + "write runs only on the writer's own thread:" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        // ---- the preview guards' scan: the shared logger on a recording's thread -------------

        /// <summary>
        /// NEGATIVE CONTROL for the <c>PreviewTapTests</c> guards: every real way the drain can end in
        /// the shared logger on its own thread is still reported.
        /// </summary>
        [Theory]
        [InlineData("DrainDecoy::DrainLogsDirectly", "DrainDecoy::DrainLogsDirectly", "AgentEyes.Log::Error")]
        [InlineData("DrainDecoy::DrainTouchesALoggingTypeInitializer", "CctorLogging::Announce", "AgentEyes.Log::Warn")]
        [InlineData("DrainDecoy::DrainInvokesAParkedLogger", "ParkedLogger::SayNow", "AgentEyes.Log::Info")]
        public void DrainLoggerScan_RealDrainToSharedLoggerCall_IsStillReported(string seed, string caller, string logger)
        {
            var offenders = Offenders(seed, PreviewTapTests.TouchesTheFilesystemOrTheSharedLogger);

            Assert.True(offenders.Any(o => o.Method == Ns + caller && o.Callee == logger),
                $"The preview guards' scan no longer reports a REAL drain -> shared-logger call reached "
                + $"from {seed} ({caller} -> {logger}). The delegate-handoff rules (issue #75) have cut "
                + "an edge that runs on the drain's own thread. Reported:" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        /// <summary>The narrowness half: the product's PreviewLog and PreviewChores shapes report
        /// nothing, because their logger and filesystem work only runs on their own threads.</summary>
        [Theory]
        [InlineData("DrainDecoy::DrainSaysThroughTheLogLane")]
        [InlineData("DrainDecoy::DrainHandsAChoreToTheWorker")]
        public void DrainLoggerScan_TheProductsWorkerShapes_AreNotReported(string seed)
        {
            var offenders = Offenders(seed, PreviewTapTests.TouchesTheFilesystemOrTheSharedLogger);

            Assert.True(offenders.Count == 0,
                $"{seed} is reported as touching the filesystem or the shared logger on the drain's "
                + "thread, but that work runs only on the worker's own thread:" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        // ---- the walk itself ------------------------------------------------------------------

        /// <summary>A thread body is not reached from the method that starts the thread - and the
        /// work parked for it IS reached from the body, including a delegate that arrived through a
        /// constructor parameter (Config's WriteJson shape). The edge moved; it did not vanish.</summary>
        [Fact]
        public void Reachable_ThreadStartAndParkedDelegate_EdgesMoveToTheThreadBody()
        {
            var fromStart = CompiledCode.Reachable(CompiledCode.TestAssembly, new[] { Ns + "DecoyBackgroundWriter::Start" });
            Assert.DoesNotContain(Ns + "DecoyBackgroundWriter::Loop", fromStart);

            var fromLoop = CompiledCode.Reachable(CompiledCode.TestAssembly, new[] { Ns + "DecoyBackgroundWriter::Loop" });
            Assert.Contains(Ns + "DecoyBackgroundWriter::WriteToDisk", fromLoop);
            Assert.Contains(Ns + "DecoyConfig::WriteJson", fromLoop);
        }

        /// <summary>The same presence on the PRODUCT: the writes and log lines the four guards no
        /// longer see from the HUD thread or the drain are still seen from the thread bodies that
        /// perform them - so the guards' silence is about WHERE the work runs, not a walk that lost
        /// the work.</summary>
        [Fact]
        public void Reachable_TheProductsThreadBodies_StillReachTheirFileAndLogWork()
        {
            var writer = CompiledCode.Reachable(CompiledCode.AppAssembly, new[] { "AgentEyes.App.BackgroundFileWriter::Loop" });
            Assert.Contains("AgentEyes.App.BackgroundFileWriter::WriteToDisk", writer);
            Assert.Contains("AgentEyes.App.Config::WriteJson", writer);

            var chores = CompiledCode.Reachable(CompiledCode.CoreAssembly, new[] { "AgentEyes.Preview.PreviewChores::Loop" });
            Assert.Contains("AgentEyes.Preview.PreviewChores::Carry", chores);
            Assert.Contains("AgentEyes.Preview.PreviewChores::DoRemove", chores);

            var log = CompiledCode.Reachable(CompiledCode.CoreAssembly, new[] { "AgentEyes.Preview.PreviewLog::Loop" });
            Assert.Contains("AgentEyes.Preview.PreviewLog::Drain", log);
            Assert.Contains("AgentEyes.Log::Write", log);
        }
    }
}
