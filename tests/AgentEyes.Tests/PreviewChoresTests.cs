using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, Review Gate round 2 on PR #39, defect 1 - THE BOUND.
    ///
    /// <see cref="PreviewChores"/> exists so that the preview's filesystem work happens on a thread a
    /// RECORDING IS NOT WAITING ON, and so that when a caller does wait for it, the wait ENDS. The
    /// two halves are tested in two places on purpose, because neither can answer the other's
    /// question:
    ///
    ///  - WHICH CALLS ARE ON WHICH THREAD is read from the compiled IL, in
    ///    <c>PreviewTapTests.NothingOnARecordingsCriticalPaths_TouchesTheFilesystemOrTheSharedLogger</c>.
    ///    That is the half a behavioural test cannot give: round 5's stall test injected a delegate
    ///    at the write seam and left the real calls that could block a stop - on either side of that
    ///    seam - running against healthy local paths, so it certified a stop path it never exercised.
    ///  - WHETHER THE CALLER'S WAIT IS BOUNDED is measured here, against a worker that is genuinely,
    ///    provably stuck. A real filesystem cannot be made to hang inside a unit test, so the stall
    ///    goes at the narrowest seam there is - how a chore is carried out - with the queue, the
    ///    thread, the budget, the counters and the caller's own code all production. The stall is at
    ///    the seam BECAUSE the claim being tested is about the caller, not about the doer; the IL
    ///    guard is what proves the doer is the only thing on the far side of it.
    ///
    /// Every test here uses its OWN worker rather than the shared one. Wedging a process-wide worker
    /// would be a test that breaks every test after it.
    /// </summary>
    public class PreviewChoresTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "agenteyes-chores-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a test that left a handle open must not fail the run here */ }
        }

        private string FramePath => Path.Combine(_dir, "screen.jpg");

        /// <summary>
        /// THE DEFECT, AS A NUMBER. A caller on a recording's start or stop path waits for its
        /// budget and not one moment longer, however wedged the filesystem is.
        ///
        /// All three arms: returning inside the budget is the pass, exceeding it is the defect, and a
        /// run in which the worker was never actually stuck is a BROKEN INSTRUMENT and fails too,
        /// rather than passing by proving nothing.
        /// </summary>
        [Fact]
        public void ACallerWaitingOnAWedgedWorker_GivesUpOnItsBudget()
        {
            using var stalled = new ManualResetEventSlim(false);
            int entered = 0;

            var chores = new PreviewChores((_, _) =>
            {
                Interlocked.Increment(ref entered);
                stalled.Wait(TimeSpan.FromSeconds(30));   // a filesystem that does not answer
            });

            // The first caller wedges the worker and is itself timed out - it is the instrument.
            var clock = Stopwatch.StartNew();
            bool first = chores.Run(PreviewChores.Kind.Prepare, FramePath, 250);
            clock.Stop();

            Assert.True(SpinUntil(() => Volatile.Read(ref entered) >= 1, 5000),
                "The worker never entered the stalled chore, so nothing was blocked and this test "
                + "measured nothing.");
            Assert.True(clock.ElapsedMilliseconds < 1500,
                $"A preview chore held its caller for {clock.ElapsedMilliseconds}ms against a 250ms "
                + "budget. That caller is a recording's start or stop (issue #33, AC10; Review Gate "
                + "round 2 on PR #39): unbounded there means the recording never starts, or Stop "
                + "never returns and the service sits in 'finalizing'.");
            Assert.False(first, "a chore that never finished must not report success");
            Assert.Equal(1, chores.TimedOut);

            // And the SECOND caller - the next recording - is bounded too, rather than inheriting the
            // first one's wedge without limit.
            clock.Restart();
            bool second = chores.Run(PreviewChores.Kind.Remove, FramePath, 250);
            clock.Stop();
            Assert.False(second);
            Assert.True(clock.ElapsedMilliseconds < 1500,
                $"The next recording waited {clock.ElapsedMilliseconds}ms behind a wedged chore.");
            Assert.Equal(2, chores.TimedOut);

            stalled.Set();
        }

        /// <summary>The known-good arm: a healthy worker really does the work, and says so. Without
        /// this the bound above is satisfied by a worker that does nothing at all.</summary>
        [Fact]
        public void APreparedPath_HasItsDirectoryAndNoLeftoverFrame()
        {
            var chores = new PreviewChores();

            Assert.True(chores.Run(PreviewChores.Kind.Prepare, FramePath, 5000));
            Assert.True(Directory.Exists(_dir));

            File.WriteAllBytes(FramePath, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(FramePath + ".tmp", new byte[] { 4, 5 });

            Assert.True(chores.Run(PreviewChores.Kind.Prepare, FramePath, 5000));
            Assert.False(File.Exists(FramePath));
            Assert.False(File.Exists(FramePath + ".tmp"));
            Assert.Equal(0, chores.Failed);
            Assert.Equal(0, chores.TimedOut);
        }

        [Fact]
        public void RemovingAFrameThatIsNotThere_IsNotAFailure()
        {
            var chores = new PreviewChores();
            Directory.CreateDirectory(_dir);

            Assert.True(chores.Run(PreviewChores.Kind.Remove, FramePath, 5000));
            Assert.Equal(0, chores.Failed);
        }

        /// <summary>
        /// A REAL path that genuinely cannot be prepared - its parent is a FILE, so creating the
        /// directory throws - is reported as a failure and counted, and nothing escapes to the
        /// caller. This is the AC10 answer at start time: the recording proceeds without a preview.
        /// </summary>
        [Fact]
        public void APathThatCannotBePrepared_FailsWithoutThrowingAtTheCaller()
        {
            Directory.CreateDirectory(_dir);
            string blocker = Path.Combine(_dir, "not-a-directory");
            File.WriteAllText(blocker, "this is a file, so nothing can be created inside it");

            var chores = new PreviewChores();

            Assert.False(chores.Run(PreviewChores.Kind.Prepare, Path.Combine(blocker, "screen.jpg"), 5000));
            Assert.Equal(1, chores.Failed);
            Assert.Equal(0, chores.TimedOut);   // it failed, which is a different thing from stalling
        }

        [Fact]
        public void AChoreWithNoPath_Throws()
        {
            var chores = new PreviewChores();
            Assert.Throws<ArgumentException>(() => chores.Run(PreviewChores.Kind.Remove, "", 100));
        }

        [Fact]
        public void ANegativeBudget_Throws()
        {
            var chores = new PreviewChores();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => chores.Run(PreviewChores.Kind.Remove, FramePath, -1));
        }

        /// <summary>
        /// The preview's log lane, and the reason it exists: saying something costs an enqueue. The
        /// line still reaches the shared log - this waits for it, bounded - so nothing is lost by
        /// not waiting.
        /// </summary>
        [Fact]
        public void SayingSomethingThroughThePreviewLog_ReachesTheLogWithoutTheCallerWaitingForIt()
        {
            string marker = "[PreviewLogTest] " + Guid.NewGuid().ToString("N");

            var clock = Stopwatch.StartNew();
            PreviewLog.Info(marker);
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 100,
                $"Saying one line took {clock.ElapsedMilliseconds}ms on the caller's thread. The "
                + "preview says things from the drain, from the WPF dispatcher and from a recording's "
                + "stop; none of those may wait for a file append under a process-wide lock.");

            Assert.True(PreviewLog.Settle(5000), "the preview log appender never got the line out");
            Assert.Contains(marker, ReadTheLog());
        }

        private static string ReadTheLog()
        {
            // Read the live log the way anything else reading a file another process is appending to
            // must: sharing everything.
            using var stream = new FileStream(AgentEyes.Log.CurrentFile, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static bool SpinUntil(Func<bool> condition, int milliseconds)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }
    }
}
