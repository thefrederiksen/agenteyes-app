using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AgentEyes.Preview
{
    /// <summary>
    /// The preview's filesystem lane: every directory and file operation the preview performs on a
    /// thread a RECORDING is waiting on, moved onto a thread nothing is waiting on, and given a HARD
    /// BOUND (issue #33, AC10; Review Gate round 2 on PR #39, defect 1).
    ///
    /// WHY IT EXISTS - and it is the same sentence as every other defect this feature has had:
    /// PREVIEW WORK MAY NOT BLOCK SOMETHING THAT MUST NOT BLOCK.
    ///
    /// Round 1 moved publishing off the pipe-reading thread. Round 2 found the same hazard in the
    /// lifecycle AROUND that drain: preparing the preview directory ran synchronously on the thread
    /// that STARTS a recording, and removing the published frame ran synchronously on the thread that
    /// STOPS one. Both are <c>Directory.CreateDirectory</c>, <c>File.Exists</c> and
    /// <c>File.Delete</c> on the preview path, and a path that never answers - a reparse point onto
    /// an unavailable share, a filter driver, a disconnected share - makes those calls neither return
    /// nor throw. A catch cannot help with a call that never returns. The recording then never
    /// starts, or Stop never returns and the service sits in "finalizing" forever.
    ///
    /// THE SHAPE. One long-lived worker thread and a queue of TYPED JOBS - a kind and a path, never a
    /// delegate. Callers hand a job over and wait AT MOST a budget; when the budget expires the
    /// caller carries on without its preview and says so, and the worker keeps whatever it is stuck
    /// in to itself. A preview that cannot be prepared or cleaned up costs a WARNING. It never costs
    /// a recording.
    ///
    /// WHAT A WEDGED WORKER COSTS, stated rather than discovered: jobs behind a stuck one do not run.
    /// The next recording then finds the preview unavailable and records without one, and stale frame
    /// files are not removed until the path answers again. That is the correct price - the machine's
    /// filesystem has stopped answering - and it is visible in the log rather than silent.
    ///
    /// THE WORKER'S THREAD IS CREATED IN THE CONSTRUCTOR, and the shared instance is constructed in
    /// the type initializer, never from a caller's path. That is not decoration: it keeps
    /// <see cref="Loop"/> - which does touch the filesystem - out of the call graph reachable from a
    /// recording's start and stop, so the IL guards in <c>PreviewTapTests</c> measure those threads'
    /// own work rather than a thread body they happen to name.
    /// </summary>
    internal sealed class PreviewChores
    {
        /// <summary>How long a caller on a recording's start or stop path may wait for the preview's
        /// filesystem work. Long enough that a healthy machine always finishes (these are three
        /// metadata operations in a directory beside the logs), short enough that a machine whose
        /// filesystem has stopped answering costs a picture rather than a recording.</summary>
        public const int BudgetMs = 2000;

        /// <summary>Ceiling on jobs waiting to run. A recording queues two of these; reaching this
        /// means the worker has stopped, and a refused job is reported rather than dropped.</summary>
        private const int MaxPending = 64;

        private const int IdleWakeMs = 250;

        /// <summary>What a chore is. A KIND AND A PATH, never a delegate: a caller on a recording's
        /// critical path must not be able to hand this worker arbitrary code, and a scan of the
        /// compiled IL must be able to see that the caller itself performs none of it.</summary>
        internal enum Kind
        {
            /// <summary>Make the directory the frame is published into, and remove a frame left by a
            /// previous recording - it is a picture of something else.</summary>
            Prepare,

            /// <summary>Remove the published frame and any half-written temporary beside it.</summary>
            Remove,
        }

        private sealed class Chore
        {
            public Chore(Kind kind, string framePath)
            {
                Job = kind;
                FramePath = framePath;
            }

            public Kind Job { get; }
            public string FramePath { get; }
            public ManualResetEventSlim Done { get; } = new(false);
            public bool Succeeded { get; set; }
        }

        /// <summary>The one the product uses. Constructed HERE, in the type initializer - see the
        /// class comment for why that placement is load-bearing.</summary>
        private static readonly PreviewChores Shared = new();

        private readonly ConcurrentQueue<Chore> _queue = new();
        private readonly AutoResetEvent _work = new(false);
        private readonly Action<Kind, string> _perform;
        private long _done;
        private long _failed;
        private long _refused;
        private long _timedOut;

        /// <param name="perform">How a chore is carried out, as (kind, frame path). The default does
        /// the real filesystem work; the only other implementation is a test's, because what has to
        /// be proven here is what a CALLER does while the worker is STALLED - and a stall is
        /// something no test can produce on a real filesystem. The real calls are held on this side
        /// of the seam by <c>PreviewTapTests</c>, which reads the compiled IL and fails if any of
        /// them appears on a caller's thread.</param>
        internal PreviewChores(Action<Kind, string>? perform = null)
        {
            _perform = perform ?? Carry;
            var worker = new Thread(Loop)
            {
                IsBackground = true,
                Name = "AgentEyes preview chores",
            };
            worker.Start();
        }

        /// <summary>Jobs that ran to completion, successfully or not.</summary>
        public long Done => Interlocked.Read(ref _done);

        /// <summary>Jobs that threw. Counted as well as logged, so a caller or a test sees a broken
        /// instrument rather than an absence.</summary>
        public long Failed => Interlocked.Read(ref _failed);

        /// <summary>Jobs refused because the queue was full - i.e. the worker has stopped.</summary>
        public long Refused => Interlocked.Read(ref _refused);

        /// <summary>Callers that gave up on their budget. The measured cost of never letting the
        /// preview delay a recording.</summary>
        public long TimedOut => Interlocked.Read(ref _timedOut);

        /// <summary>
        /// Make <paramref name="framePath"/> ready to publish into: create its directory and remove a
        /// frame (and temporary) left by a previous recording. Returns false when it could not be
        /// done inside <paramref name="budgetMs"/> - which is the caller's cue to record WITHOUT a
        /// preview, never to wait longer.
        /// </summary>
        public static bool Prepare(string framePath, int budgetMs) =>
            Shared.Run(Kind.Prepare, framePath, budgetMs);

        /// <summary>
        /// Remove the published frame and any temporary beside it. Returns false when it could not be
        /// done inside <paramref name="budgetMs"/>; the caller reports it and carries on, because
        /// this can run on the thread that is STOPPING a recording.
        /// </summary>
        public static bool Remove(string framePath, int budgetMs) =>
            Shared.Run(Kind.Remove, framePath, budgetMs);

        /// <summary>
        /// Hand one chore to the worker and wait AT MOST <paramref name="budgetMs"/> for it. THE
        /// CALLER'S WHOLE CONTACT WITH THE FILESYSTEM IS THIS METHOD, and this method performs none
        /// of it: an enqueue, an event set, and a bounded wait.
        /// </summary>
        internal bool Run(Kind kind, string framePath, int budgetMs)
        {
            if (string.IsNullOrWhiteSpace(framePath))
                throw new ArgumentException("a preview chore must be told which frame file it is about", nameof(framePath));
            if (budgetMs < 0)
                throw new ArgumentOutOfRangeException(nameof(budgetMs), budgetMs,
                    "A preview chore's budget is how long a recording may wait for it, and that cannot be negative.");

            if (_queue.Count >= MaxPending)
            {
                Interlocked.Increment(ref _refused);
                PreviewLog.Warn($"[PreviewChores] {kind} refused for {framePath}: {MaxPending} preview "
                                + "chores are already waiting, so the worker has stopped. The recording "
                                + "is unaffected and proceeds without a preview.");
                return false;
            }

            var chore = new Chore(kind, framePath);
            _queue.Enqueue(chore);
            _work.Set();

            if (chore.Done.Wait(budgetMs)) return chore.Succeeded;

            Interlocked.Increment(ref _timedOut);
            PreviewLog.Warn($"[PreviewChores] {kind} for {framePath} did not finish within {budgetMs}ms. "
                            + "The preview filesystem is not answering, so this recording carries on "
                            + "without waiting for it (issue #33, AC10).");
            return false;
        }

        /// <summary>
        /// The worker loop: the one place in the preview where a recording's start or stop path
        /// reaches the filesystem, and it reaches it from HERE, where nothing is waiting for it
        /// beyond a budget. A THREAD ENTRY POINT, hence the try/catch.
        /// </summary>
        private void Loop()
        {
            try
            {
                while (true)
                {
                    _work.WaitOne(IdleWakeMs);
                    while (_queue.TryDequeue(out var chore)) Perform(chore);
                }
            }
            catch (Exception ex)
            {
                PreviewLog.Error("[PreviewChores] the preview's filesystem worker stopped; previews "
                                 + "will be unavailable until the app is restarted. Recording is "
                                 + $"unaffected{Environment.NewLine}{ex}");
            }
        }

        private void Perform(Chore chore)
        {
            try
            {
                _perform(chore.Job, chore.FramePath);
                chore.Succeeded = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                PreviewLog.Warn($"[PreviewChores] {chore.Job} FAILED for {chore.FramePath} - {ex.Message}. "
                                + "The recording is unaffected and proceeds without a preview.");
            }
            finally
            {
                Interlocked.Increment(ref _done);
                chore.Done.Set();
            }
        }

        private static void Carry(Kind kind, string framePath)
        {
            if (kind == Kind.Prepare)
            {
                string? dir = Path.GetDirectoryName(framePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            // A frame left by a previous recording is a LIE the moment this one starts: it is a
            // picture of something else that the staleness watchdog would need seconds to catch. So
            // preparing and removing end in the same two deletes.
            DoRemove(framePath);
        }

        private static void DoRemove(string framePath)
        {
            if (File.Exists(framePath)) File.Delete(framePath);
            string temp = framePath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
