using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33 - the preview tap, and above all the ONE property the whole feature rests on:
    /// THE DRAIN IS UNCONDITIONAL (AC10).
    ///
    /// The tap reads a pipe that the ffmpeg WRITING THE RECORDING is filling. A pipe nobody reads
    /// fills, and a full pipe blocks that ffmpeg. So "we stopped reading because the preview was
    /// hidden / the disk was full / a write threw" is not a degraded preview - it is a damaged
    /// recording. Every test below that counts <see cref="PreviewTap.FramesRead"/> while publishing
    /// is off or failing is checking exactly that, and each is stated as a presence: the count is a
    /// NUMBER that must be there, not an error that must be absent.
    ///
    /// Measured evidence for why the frames go through this tap at all rather than being written by
    /// ffmpeg itself is in the handoff note for this issue: giving ffmpeg the preview as a file
    /// output and then removing the directory mid-run terminated the whole process and truncated a
    /// 15-second recording to 5.1 seconds.
    /// </summary>
    public class PreviewTapTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "agenteyes-preview-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a test that left a handle open must not fail the run here */ }
        }

        private string FramePath => Path.Combine(_dir, "screen.jpg");

        private static byte[] Frame(byte marker)
        {
            var f = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
            for (int i = 0; i < 8; i++) f.Add(marker);
            f.Add(0xFF);
            f.Add(0xD9);
            return f.ToArray();
        }

        private static MemoryStream StreamOf(params byte[][] frames) =>
            new(frames.SelectMany(f => f).ToArray());

        // ---- the drain ------------------------------------------------------

        [Fact]
        public void Pump_WhilePublishing_WritesTheNewestFrameOutAndAccountsForEveryFrame()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;

            var first = Frame(0x11);
            var last = Frame(0x22);
            tap.Pump(StreamOf(first, last));

            Assert.True(tap.WaitForDrain(5000), "the pump did not reach the end of its stream");
            Assert.True(tap.WaitForPublisher(5000), "the publisher never finished the frames it was handed");
            Assert.Equal(2, tap.FramesRead);

            // THE CONSERVATION LAW, and it is the exact statement of the round-2 design (Review Gate
            // round 1 on PR #34). Publishing is asynchronous now, so a frame the drain hands over can
            // be superseded before the publisher takes it - the drain is never allowed to wait, and
            // this is what that costs. What may NOT happen is a frame going missing unrecorded: every
            // frame read is either on disk or counted as dropped.
            Assert.Equal(tap.FramesRead, tap.FramesPublished + tap.FramesDropped);
            Assert.True(tap.FramesPublished >= 1,
                $"nothing was published at all (read={tap.FramesRead} dropped={tap.FramesDropped})");
            Assert.False(tap.PublishFailed);
            Assert.True(File.Exists(FramePath));
            // The published frame is the LAST one: this is a monitor, not a recording.
            Assert.Equal(last, File.ReadAllBytes(FramePath));
        }

        [Fact]
        public void Pump_WhileNotPublishing_STILL_DRAINS_ANDWritesNothing()
        {
            // The preview is hidden. ffmpeg is still writing to the pipe, so the pipe is still read -
            // the count proves it - and nothing reaches the disk. This is the AC9 cost story and the
            // AC10 safety story in one assertion.
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            Assert.False(tap!.Publishing);

            tap.Pump(StreamOf(Frame(0x11), Frame(0x22), Frame(0x33)));

            Assert.True(tap.WaitForDrain(5000));
            Assert.Equal(3, tap.FramesRead);
            Assert.Equal(0, tap.FramesPublished);
            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void Pump_WhenPublishingFails_KeepsDrainingAndReportsTheFailure()
        {
            // The preview's own failure: the directory it publishes into is gone. The recording must
            // not notice - so every frame is still taken off the pipe (FramesRead), none is published
            // (FramesPublished), and the failure is reported rather than swallowed.
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            Directory.Delete(_dir, recursive: true);

            tap.Pump(StreamOf(Frame(0x11), Frame(0x22), Frame(0x33)));

            Assert.True(tap.WaitForDrain(5000));
            Assert.True(tap.WaitForPublisher(5000));
            Assert.Equal(3, tap.FramesRead);
            Assert.Equal(0, tap.FramesPublished);
            Assert.True(tap.PublishFailed);
        }

        [Fact]
        public void Pump_WhenPublishingRecovers_PublishesAgainAndClearsTheFailure()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            Directory.Delete(_dir, recursive: true);

            // First stream: nowhere to publish to.
            tap.Pump(StreamOf(Frame(0x11)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(tap.WaitForPublisher(5000));
            Assert.True(tap.PublishFailed);

            // The directory comes back. A second tap over the same path publishes normally again -
            // a failed preview is not a permanently broken one.
            Directory.CreateDirectory(_dir);
            var again = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(again);
            again!.Publishing = true;
            again.Pump(StreamOf(Frame(0x44)));
            Assert.True(again.WaitForDrain(5000));
            Assert.True(again.WaitForPublisher(5000));
            Assert.False(again.PublishFailed);
            Assert.Equal(Frame(0x44), File.ReadAllBytes(FramePath));
        }

        [Fact]
        public void Publishing_TurnedOff_RemovesThePublishedFrame()
        {
            // Hiding the preview must not leave the last picture on disk: the next reader would find
            // a file that looks live and is a photograph of the past.
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(tap.WaitForPublisher(5000));
            Assert.True(File.Exists(FramePath));

            tap.Publishing = false;

            // The delete happens on the publisher thread, never on the caller's - the caller here is
            // the HUD's click handler on the WPF UI thread (Review Gate round 1 on PR #34).
            Assert.True(tap.WaitForPublisher(5000), "the published frame was never removed");
            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void Dispose_RemovesThePublishedFrameAndIsSafeTwice()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(tap.WaitForPublisher(5000));
            Assert.True(File.Exists(FramePath));

            tap.Dispose();
            tap.Dispose();

            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void TryCreate_DeletesAFrameLeftByThePreviousRecording()
        {
            // A leftover frame is a picture of a DIFFERENT recording. Left in place it would be shown
            // as this recording's first frame, and the staleness watchdog would need seconds to
            // notice.
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(FramePath, Frame(0x99));

            var tap = PreviewTap.TryCreateAt("screen", FramePath);

            Assert.NotNull(tap);
            Assert.False(File.Exists(FramePath));
        }

        [Fact]
        public void Pump_LeavesNoTemporaryFileBehind()
        {
            var tap = PreviewTap.TryCreateAt("screen", FramePath);
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11), Frame(0x22)));
            Assert.True(tap.WaitForDrain(5000));
            Assert.True(tap.WaitForPublisher(5000));

            // A named presence check, not "the directory looks tidy": the ONLY file here is the
            // published frame.
            var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "screen.jpg" }, files);
        }

        [Fact]
        public void Pump_Twice_Throws() =>
            Assert.Throws<InvalidOperationException>(() =>
            {
                var tap = PreviewTap.TryCreateAt("screen", FramePath)!;
                tap.Pump(StreamOf(Frame(0x11)));
                tap.WaitForDrain(5000);
                tap.Pump(StreamOf(Frame(0x22)));
            });

        [Fact]
        public void TryCreateAt_WithNoPathToPublishTo_Throws() =>
            Assert.Throws<ArgumentException>(() => PreviewTap.TryCreateAt("screen", ""));

        // ---- the drain never waits for publishing (Review Gate round 1, PR #34) ----

        /// <summary>
        /// THE DEFECT THIS SECTION EXISTS FOR, and the one the round-1 evidence could not see.
        ///
        /// The first implementation published INLINE from the drain loop. Every test above proves
        /// that a publish which THROWS is caught - and the reviewer's point was that catching an
        /// exception is a different claim from never stopping. A directory reparse point onto an
        /// unavailable share, an NTFS stall, a filter driver: File.WriteAllBytes then neither returns
        /// nor throws, so the catch never runs, the drain performs no further pipe read, the
        /// anonymous pipe fills, and the ffmpeg WRITING RECORDING.MP4 blocks on a full pipe. The cost
        /// is a truncated recording, which is exactly what AC10 forbids.
        ///
        /// A real filesystem cannot be made to hang inside a unit test, so the stall is injected at
        /// the write seam - the narrowest possible substitution, one delegate, with everything else
        /// (the framer, the slot, both threads, the counters) production code.
        ///
        /// STATED AS A PRESENCE, all three arms: the drain must REACH THE END OF ITS STREAM and
        /// account for all 40 frames WHILE the publisher is provably inside the stalled write. A
        /// drain that finished with fewer frames is a defect; a run where the publisher was never
        /// stalled is a BROKEN INSTRUMENT and fails too, rather than passing by proving nothing.
        /// </summary>
        [Fact]
        public void Drain_WhilePublishingIsStalledForever_StillReadsThePipeToTheEnd()
        {
            using var stalled = new ManualResetEventSlim(false);
            int writesEntered = 0;

            var tap = PreviewTap.TryCreateAt("screen", FramePath, _ =>
            {
                Interlocked.Increment(ref writesEntered);
                stalled.Wait(TimeSpan.FromSeconds(30));   // a filesystem that does not answer
            });
            Assert.NotNull(tap);
            tap!.Publishing = true;

            var frames = Enumerable.Range(0, 40).Select(i => Frame((byte)(0x10 + i))).ToArray();
            tap.Pump(StreamOf(frames));

            Assert.True(tap.WaitForDrain(5000),
                "The drain did not reach the end of its stream while publishing was stalled. That is "
                + "the round-1 defect: the pump is sitting inside a filesystem call, so ffmpeg's "
                + "stdout is no longer being read, the pipe fills, and the ffmpeg writing "
                + "recording.mp4 blocks on it (issue #33, AC10).");
            Assert.Equal(frames.Length, tap.FramesRead);

            // The instrument really did stall. Without this the test could pass over a write that
            // returned instantly, which would prove nothing at all. It is waited for rather than
            // asserted outright BECAUSE the drain is now faster than the publisher - which is the
            // whole point: the drain reached the end of the stream without waiting for a write that
            // had not even started.
            Assert.True(SpinUntil(() => Volatile.Read(ref writesEntered) >= 1, 5000),
                "The stalled write was never entered, so nothing was actually blocked and this test "
                + "measured nothing.");
            Assert.False(tap.WaitForPublisher(100),
                "The publisher reported itself idle while it was supposed to be stuck inside the "
                + "stalled write - the stall did not take, so the drain was never tested against it.");

            // Every frame the drain offered is still accounted for. At most two are unaccounted at
            // any instant - one in the publisher's hands, one in the slot it has not taken yet - and
            // everything else is either published or counted as dropped. A larger gap would mean
            // frames were vanishing, which is the one thing the latest-wins slot may not do.
            long unaccounted = tap.FramesRead - (tap.FramesPublished + tap.FramesDropped);
            Assert.InRange(unaccounted, 0, 2);

            stalled.Set();
            tap.Dispose();
        }

        /// <summary>
        /// Hiding the preview is a click on the HUD, on the WPF UI thread, and the dispatcher it
        /// returns to is the one that serves the STOP button. So turning publishing off must not do
        /// I/O - it requests the delete and the publisher performs it - and it must return even while
        /// the publisher is wedged in a filesystem call.
        ///
        /// The budget is deliberately generous (200ms for an interlocked swap and an event set):
        /// what is being caught is a SYNCHRONOUS filesystem call, which under the stall injected here
        /// does not return for 30 seconds.
        /// </summary>
        [Fact]
        public void TurningThePreviewOff_WhileThePublisherIsStalled_ReturnsAtOnce()
        {
            using var stalled = new ManualResetEventSlim(false);
            int writesEntered = 0;

            var tap = PreviewTap.TryCreateAt("screen", FramePath, _ =>
            {
                Interlocked.Increment(ref writesEntered);
                stalled.Wait(TimeSpan.FromSeconds(30));
            });
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11), Frame(0x22)));

            Assert.True(SpinUntil(() => Volatile.Read(ref writesEntered) >= 1, 5000),
                "The publisher never entered the stalled write, so nothing was blocked and this test "
                + "measured nothing.");

            var clock = Stopwatch.StartNew();
            tap.Publishing = false;
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 200,
                $"Hiding the preview took {clock.ElapsedMilliseconds}ms while the filesystem was "
                + "stalled. On the HUD that thread is the WPF dispatcher, so for that whole time the "
                + "person cannot stop the recording either (issue #33; repo coding standard 1).");

            stalled.Set();
            tap.Dispose();
        }

        /// <summary>
        /// THE STRUCTURAL FORM OF THE SAME CLAIM, read from the compiled IL rather than from a run.
        /// The behavioural test above proves the drain survives one injected stall; this proves there
        /// is no filesystem call on the drain's path AT ALL to stall in - transitively, through
        /// whatever helper anybody adds next, and independently of how the C# is spelled.
        ///
        /// WHAT IT CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): the closure
        /// stops at the assembly boundary, so a filesystem call the drain reaches through a method in
        /// another assembly is invisible here. Today the drain calls nothing outside AgentEyes.Core
        /// but Stream.Read on the pipe it is draining and the interlocked/event primitives of the
        /// handoff. It also cannot see a blocking call that is not a filesystem one.
        /// </summary>
        [Fact]
        public void NothingTheDrainCanReach_TouchesTheFilesystem()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.CoreAssembly,
                                       new[] { "AgentEyes.Preview.PreviewTap::Drain" }),
                StringComparer.Ordinal);

            var offenders = CompiledCode
                .CallSites(CompiledCode.CoreAssembly, TouchesTheFilesystem)
                .Where(site => reached.Contains(site.Method))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Something the preview drain can reach touches the filesystem. The drain is the only "
                + "reader of the ffmpeg pipe that is writing the recording: a filesystem call there "
                + "can STOP it (a stall neither returns nor throws, so no catch helps), the pipe "
                + "fills, and the recording is truncated. All fallible or blocking work belongs on "
                + "the publisher thread (issue #33, AC10):" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        /// <summary>
        /// And the same for the one preview call the WPF UI thread makes: showing or hiding the
        /// panel. It used to delete the published frame inline, on the caller's thread.
        /// </summary>
        [Fact]
        public void NothingTurningThePreviewOffCanReach_TouchesTheFilesystem()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.CoreAssembly,
                                       new[] { "AgentEyes.Preview.PreviewTap::set_Publishing" }),
                StringComparer.Ordinal);

            var offenders = CompiledCode
                .CallSites(CompiledCode.CoreAssembly, TouchesTheFilesystem)
                .Where(site => reached.Contains(site.Method))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Showing or hiding the live preview touches the filesystem on the caller's thread. "
                + "That caller is the HUD's click handler on the WPF dispatcher - the same dispatcher "
                + "that serves the Stop button (issue #33; repo coding standard 1):" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        // ---- nor does starting or stopping one (Review Gate round 2, PR #39) ----

        /// <summary>
        /// THE ROUND-2 DEFECT, and it is the round-1 defect one method further out.
        ///
        /// The drain was made safe and the LIFECYCLE AROUND IT was left alone. Creating a tap
        /// prepared the preview directory - <c>Directory.CreateDirectory</c>, <c>File.Exists</c>,
        /// <c>File.Delete</c> - synchronously on the thread that STARTS a recording, and
        /// <c>RecordingService.StartVideo</c> called it for both tracks before any writer began.
        /// Disposing a tap logged synchronously and then, AFTER the publisher join had already timed
        /// out because the filesystem was not answering, flushed the logger and deleted that same
        /// unanswering path - on the thread that STOPS a recording, before the service went back to
        /// idle. Point the preview directory at a reparse point onto an unavailable share and the
        /// recording never starts; make it stall mid-run and Stop never returns and the service sits
        /// in "finalizing".
        ///
        /// THE SHARED LOGGER IS INCLUDED HERE, deliberately, and that is a stronger bar than the
        /// drain's test above. <c>AgentEyes.Log</c> is a <c>Directory.CreateDirectory</c> plus a
        /// <c>File.AppendAllText</c> under a PROCESS-WIDE lock: on these threads a log line is both a
        /// filesystem call and a lock another thread may be holding inside one, which is how a stalled
        /// logger could block a stop before it ever reached the joins.
        ///
        /// WHY THIS AND NOT AN INJECTED STALL. Round 5's stall test injected a delegate at the write
        /// seam and concluded the stop path was safe - but the calls that could actually block a stop
        /// were the REAL ones on either side of that seam, on healthy local paths that never stalled.
        /// A behavioural test can only stall what it can reach. This asks the question the other way
        /// round: is there ANY filesystem or logger call on these threads' paths to stall in -
        /// transitively, through whatever helper anybody adds next, and independently of how the C#
        /// is spelled. All four threads a recording depends on are seeded at once.
        ///
        /// WHAT IT CANNOT SEE (DEVELOPMENT_METHOD.md 6c.6): the closure stops at the assembly
        /// boundary, and it sees filesystem and logger calls rather than every blocking call. The
        /// bounded <c>Thread.Join</c>s in Dispose are blocking on purpose and are bounded by
        /// construction; <see cref="PreviewChoresTests"/> measures the other bound, the one on a
        /// caller waiting for the chores worker.
        /// </summary>
        [Fact]
        public void NothingOnARecordingsCriticalPaths_TouchesTheFilesystemOrTheSharedLogger()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.CoreAssembly, new[]
                {
                    "AgentEyes.Preview.PreviewTap::Drain",           // the thread reading ffmpeg's pipe
                    "AgentEyes.Preview.PreviewTap::TryCreateAt",     // the thread STARTING a recording
                    "AgentEyes.Preview.PreviewTap::Dispose",         // the thread STOPPING a recording
                    "AgentEyes.Preview.PreviewTap::set_Publishing",  // the WPF dispatcher
                }),
                StringComparer.Ordinal);

            var offenders = CompiledCode
                .CallSites(CompiledCode.CoreAssembly, TouchesTheFilesystemOrTheSharedLogger)
                .Where(site => reached.Contains(site.Method))
                .ToList();

            Assert.True(offenders.Count == 0,
                "The preview touches the filesystem or the shared logger on a thread a RECORDING is "
                + "waiting on. None of these four threads may wait for a disk: the drain is the only "
                + "reader of the pipe ffmpeg is filling, the WPF dispatcher serves the Stop button, "
                + "and the other two ARE a recording's start and stop. A stall in any of them costs a "
                + "recording rather than a picture (issue #33, AC10; Review Gate round 2 on PR #39). "
                + "Filesystem work belongs in PreviewChores and log lines in PreviewLog, both of "
                + "which perform it on their own threads:" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        /// <summary>
        /// The companion PRESENCE, because the guard above is satisfied just as well by a preview
        /// that no longer prepares or cleans up anything at all - an absence certifying a feature
        /// that does not work. The work still happens; it happens on the worker's thread.
        /// </summary>
        [Fact]
        public void ThePreviewsFilesystemWork_StillHappens_OnTheChoresWorker()
        {
            var fromStart = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.CoreAssembly,
                                       new[] { "AgentEyes.Preview.PreviewTap::TryCreateAt" }),
                StringComparer.Ordinal);
            Assert.Contains("AgentEyes.Preview.PreviewChores::Prepare", fromStart);

            var fromRemoval = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.CoreAssembly,
                                       new[] { "AgentEyes.Preview.PreviewTap::RemoveFrameFile" }),
                StringComparer.Ordinal);
            Assert.Contains("AgentEyes.Preview.PreviewChores::Remove", fromRemoval);

            // And the worker really does the deleting, rather than having lost it in the move.
            var worker = CompiledCode
                .CallSites(CompiledCode.CoreAssembly, c => c == "System.IO.File::Delete")
                .Where(s => s.Method.StartsWith("AgentEyes.Preview.", StringComparison.Ordinal))
                .Select(s => s.Method)
                .Distinct()
                .ToList();
            Assert.Equal(new[] { "AgentEyes.Preview.PreviewChores::DoRemove" }, worker);
        }

        /// <summary>
        /// THE STOP PATH, MEASURED. The publisher is genuinely wedged - a real thread, inside a real
        /// blocking wait it cannot leave - which is the state Dispose was written for, and the state
        /// in which round 2's Dispose went on to touch the very path the publisher was stuck on.
        ///
        /// Two presences, and the second is the one that fails against the round-2 code:
        ///  1. Dispose RETURNS, inside its two bounded joins plus a margin.
        ///  2. THE PUBLISHED FRAME IS STILL ON DISK. That is not tidiness - it is the evidence that
        ///     the stopping thread performed no filesystem work of its own. The only thread allowed
        ///     to touch that path is the publisher, and it is wedged; round 2's Dispose deleted the
        ///     file HERE, on the stopping thread, which is exactly the call that never returns when
        ///     the path is a share that has gone away rather than a healthy temp directory.
        /// The frame does not stay behind forever, and the next test is that promise.
        /// </summary>
        [Fact]
        public void Dispose_WhileThePublisherIsWedged_ReturnsAndDoesNoFileWorkOfItsOwn()
        {
            using var stalled = new ManualResetEventSlim(false);
            int writesEntered = 0;

            var tap = PreviewTap.TryCreateAt("screen", FramePath, frame =>
            {
                File.WriteAllBytes(FramePath, frame);      // a real published frame, first time through
                Interlocked.Increment(ref writesEntered);
                stalled.Wait(TimeSpan.FromSeconds(30));    // and then a filesystem that does not answer
            });
            Assert.NotNull(tap);
            tap!.Publishing = true;
            tap.Pump(StreamOf(Frame(0x11), Frame(0x22)));

            Assert.True(SpinUntil(() => Volatile.Read(ref writesEntered) >= 1, 5000),
                "The publisher never entered the stalled write, so nothing was wedged and this test "
                + "measured nothing.");
            Assert.True(File.Exists(FramePath), "no frame was ever published, so there is nothing "
                + "whose survival could show that the stopping thread left the path alone");

            var clock = Stopwatch.StartNew();
            tap.Dispose();
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 8000,
                $"Dispose took {clock.ElapsedMilliseconds}ms with the publisher wedged. It is called "
                + "by RecordingService.Stop BEFORE the service returns to idle, so an unbounded wait "
                + "here is a Stop that never returns and a service stuck in 'finalizing' (issue #33, "
                + "AC10).");

            Assert.True(File.Exists(FramePath),
                "The stopping thread deleted the published frame itself. That is Review Gate round "
                + "2's defect 1: the publisher is wedged in a filesystem call on this exact path, and "
                + "the stop path followed it in. On a preview directory that is a reparse point onto "
                + "an unavailable share, that delete never returns and Stop never returns with it.");

            stalled.Set();
        }

        /// <summary>
        /// The promise the test above leans on, stated as its own presence: a frame a wedged
        /// publisher could not remove does not stay behind. The next recording's preparation removes
        /// it - on the chores worker, inside its budget - before a single frame of the new recording
        /// is published, so nobody is ever shown a picture of the last one.
        /// </summary>
        [Fact]
        public void TheNextRecording_RemovesAFrameAnEarlierOneCouldNotClearUp()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(FramePath, Frame(0x99));
            File.WriteAllBytes(FramePath + ".tmp", Frame(0x98));

            var next = PreviewTap.TryCreateAt("screen", FramePath);

            Assert.NotNull(next);
            Assert.False(File.Exists(FramePath), "the stale frame from the previous recording survived");
            Assert.False(File.Exists(FramePath + ".tmp"), "the stale temporary survived");
        }

        /// <summary>
        /// Any System.IO entry point that goes to a disk, read or write, OR the shared logger. The
        /// logger belongs on this list on a recording's start and stop paths: <c>AgentEyes.Log</c>
        /// creates a directory and appends to a file under a process-wide lock, so a stalled log is a
        /// stalled recording lifecycle (Review Gate round 2 on PR #39, defect 1).
        /// </summary>
        private static bool TouchesTheFilesystemOrTheSharedLogger(string callee) =>
            TouchesTheFilesystem(callee) || callee.StartsWith("AgentEyes.Log::", StringComparison.Ordinal);

        /// <summary>
        /// Any System.IO entry point that goes to a disk, read or write. Broader than
        /// <see cref="CompiledCode.IsFileWriteApi"/> on purpose: on the drain's path a READ blocks
        /// exactly as fatally as a write. System.IO.Stream itself is deliberately absent - the pipe
        /// the drain reads IS a Stream, and reading it is the whole job.
        /// </summary>
        private static bool TouchesTheFilesystem(string callee) =>
            callee.StartsWith("System.IO.File::", StringComparison.Ordinal)
         || callee.StartsWith("System.IO.FileInfo::", StringComparison.Ordinal)
         || callee.StartsWith("System.IO.Directory::", StringComparison.Ordinal)
         || callee.StartsWith("System.IO.DirectoryInfo::", StringComparison.Ordinal)
         || callee.StartsWith("System.IO.FileStream::", StringComparison.Ordinal)
         || callee.StartsWith("System.IO.StreamWriter::", StringComparison.Ordinal)
         || callee.StartsWith("System.IO.StreamReader::", StringComparison.Ordinal);

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

        // ---- the reader -----------------------------------------------------

        [Fact]
        public void TryRead_MissingFile_IsNoFrameAndNoError()
        {
            var bytes = PreviewFrameFile.TryRead(Path.Combine(_dir, "nothing-here.jpg"), out string? error);
            Assert.Null(bytes);
            Assert.Null(error);
        }

        [Fact]
        public void TryRead_PartialJpeg_IsNoFrame()
        {
            // The exact hazard of reading a file another process replaces ten times a second: bytes
            // are there, but they are not a picture yet.
            Directory.CreateDirectory(_dir);
            var whole = Frame(0x11);
            File.WriteAllBytes(FramePath, whole.Take(whole.Length - 2).ToArray());

            Assert.Null(PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_EmptyFile_IsNoFrame()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(FramePath, Array.Empty<byte>());

            Assert.Null(PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_WholeJpeg_ReturnsExactlyThoseBytes()
        {
            Directory.CreateDirectory(_dir);
            var whole = Frame(0x11);
            File.WriteAllBytes(FramePath, whole);

            Assert.Equal(whole, PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_WhileTheWriterHasTheFileOpen_StillReadsIt()
        {
            // The share flags are not incidental. The tap replaces this file constantly; a reader
            // that demanded exclusive access would break the thing it is reading.
            Directory.CreateDirectory(_dir);
            var whole = Frame(0x11);
            File.WriteAllBytes(FramePath, whole);

            using var writerHandle = new FileStream(
                FramePath, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            Assert.Equal(whole, PreviewFrameFile.TryRead(FramePath, out _));
        }

        [Fact]
        public void TryRead_NoPath_IsNoFrame()
        {
            Assert.Null(PreviewFrameFile.TryRead(null!, out _));
            Assert.Null(PreviewFrameFile.TryRead("", out _));
        }
    }
}
