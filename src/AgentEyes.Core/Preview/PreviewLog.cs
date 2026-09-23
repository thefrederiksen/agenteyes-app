using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AgentEyes.Preview
{
    /// <summary>
    /// The preview's log lane: SAYING something costs an enqueue, and the file append happens on a
    /// thread nothing is waiting on (issue #33; Review Gate round 2 on PR #39).
    ///
    /// WHY IT EXISTS. <see cref="Log"/> is a synchronous <c>File.AppendAllText</c> - preceded by a
    /// <c>Directory.CreateDirectory</c> - under a PROCESS-WIDE lock. That is fine on a thread that is
    /// allowed to wait, and it is a defect on the three threads this feature runs on that are not:
    ///
    ///  - THE DRAIN. It is the only reader of the pipe the recording's ffmpeg is filling. A pipe
    ///    nobody reads fills, and a full pipe blocks that ffmpeg, so a log line on the drain's thread
    ///    can truncate the recording.
    ///  - THE WPF UI THREAD. It serves the HUD's Stop button. A log line taken while the shared lock
    ///    is held by a thread sitting in a stalled append is a Stop the person cannot press.
    ///  - THE THREAD THAT STARTS AND STOPS A RECORDING. The Review Gate found the preview's teardown
    ///    logging synchronously there, with no bound, so a stalled logger left Stop unable to return
    ///    and the service stuck in "finalizing".
    ///
    /// The rule those three share is the one rule this whole feature keeps failing: PREVIEW WORK MAY
    /// NOT BLOCK SOMETHING THAT MUST NOT BLOCK. So the preview never calls the logger from a thread
    /// that matters. It hands the line over and returns.
    ///
    /// THE SHAPE, deliberately the same one the frame slot uses. A bounded queue, one event set, and
    /// one long-lived thread that does the appending. Nothing here takes a lock the appending thread
    /// could be holding while it is inside a filesystem call. Over the ceiling a line is DROPPED AND
    /// COUNTED, never silently lost: a non-zero drop count in the log says the appender stopped,
    /// which is the only way that ceiling can be reached.
    ///
    /// THE THREAD IS CREATED IN THE TYPE INITIALIZER, not lazily from <see cref="Info"/>. That is
    /// not decoration either: it is what keeps <see cref="Loop"/> - which does touch the filesystem -
    /// out of the call graph reachable from the drain, from a HUD click, and from a recording's start
    /// and stop, so the IL guards in <c>PreviewTapTests</c> measure those threads' own work rather
    /// than a thread body they happen to name.
    ///
    /// Ordering is preserved: one queue, one thread, first in first out. What is NOT preserved is
    /// interleaving with <see cref="Log"/> calls made directly by non-preview code - a preview line
    /// can appear a few milliseconds after a line that was written later. That is the price of never
    /// waiting, and it is stated here rather than discovered in a log.
    /// </summary>
    internal static class PreviewLog
    {
        /// <summary>Ceiling on unwritten lines. The preview writes a handful of lines per recording,
        /// so reaching this at all means the appender has stopped.</summary>
        private const int MaxPending = 512;

        /// <summary>How often the appender wakes with nothing to do, so a line that races the signal
        /// is still noticed promptly. The normal wake-up is a <see cref="Say"/>.</summary>
        private const int IdleWakeMs = 250;

        private static readonly ConcurrentQueue<(string Level, string Message)> Lines = new();
        private static readonly AutoResetEvent Work = new(false);
        private static readonly ManualResetEventSlim Idle = new(initialState: true);
        private static long _dropped;
        private static long _written;

        /// <summary>Created here, in the type initializer, and never from a caller's path. See the
        /// class comment: this is what keeps <see cref="Loop"/> out of every call graph that matters.</summary>
        private static readonly Thread Appender = StartAppender();

        /// <summary>Lines that reached the log file.</summary>
        public static long Written => Interlocked.Read(ref _written);

        /// <summary>Lines refused because the appender had stopped keeping up. A PRESENCE, not an
        /// absence: it is reported in the log itself the moment the appender recovers.</summary>
        public static long Dropped => Interlocked.Read(ref _dropped);

        public static void Info(string message) => Say("INFO", message);

        public static void Warn(string message) => Say("WARN", message);

        public static void Error(string message) => Say("ERROR", message);

        /// <summary>
        /// Wait, at most <paramref name="milliseconds"/>, for everything said so far to have reached
        /// the log file. Returns false when it has not - which is REPORTED rather than waited out,
        /// because the appender is allowed to be stuck in a filesystem call and an application exit
        /// is not. Called at exit and by tests.
        /// </summary>
        public static bool Settle(int milliseconds)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (true)
            {
                if (Lines.IsEmpty && Idle.IsSet) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// Hand one line over. EVERYTHING THIS DOES IS AN ENQUEUE AND AN EVENT SET - no lock the
        /// appender could be holding inside a filesystem call, and no I/O of any kind. Safe from the
        /// drain, from the WPF dispatcher, and from the thread stopping a recording, which is the
        /// entire point.
        /// </summary>
        private static void Say(string level, string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            if (Lines.Count >= MaxPending)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            Idle.Reset();
            Lines.Enqueue((level, message));
            Work.Set();
        }

        private static Thread StartAppender()
        {
            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "AgentEyes preview log",
            };
            thread.Start();
            return thread;
        }

        /// <summary>
        /// The appender loop: the one place in the preview that is allowed to sit inside the shared
        /// logger. A THREAD ENTRY POINT, hence the try/catch - an exception escaping here would end
        /// all further preview logging silently, so it is reported through the logger it was using.
        /// </summary>
        private static void Loop()
        {
            try
            {
                while (true)
                {
                    Work.WaitOne(IdleWakeMs);
                    Drain();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[PreviewLog] the preview's log appender stopped; preview lines will no "
                          + "longer reach the log until the app is restarted. Recording is unaffected", ex);
            }
        }

        private static void Drain()
        {
            while (Lines.TryDequeue(out var line))
            {
                if (line.Level == "ERROR") Log.Error(line.Message);
                else if (line.Level == "WARN") Log.Warn(line.Message);
                else Log.Info(line.Message);
                Interlocked.Increment(ref _written);
            }

            long dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                Log.Warn($"[PreviewLog] {dropped} preview log line(s) were dropped because the log "
                         + "appender could not keep up. Nothing waited for it, so the recording is "
                         + "unaffected.");

            if (Lines.IsEmpty) Idle.Set();
        }
    }
}
