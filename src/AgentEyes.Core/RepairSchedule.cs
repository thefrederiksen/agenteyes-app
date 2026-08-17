using System;
using System.Threading;

namespace AgentEyes
{
    /// <summary>
    /// WHEN the automatic repair passes (missing titles, missing thumbnails) are allowed to run
    /// (issue #142).
    ///
    /// Both repair passes used to fire only at app start. AgentEyes is an always-on recorder: on
    /// 2026-08-11 the app ran from before 05:59 until at least 12:45 without a single restart, and
    /// in that window one recording lost its title to a transient failure and three were left with
    /// no thumbnail. Nothing repaired any of it for hours, because the only trigger was a restart
    /// that never happened. A repair pass that only runs at start-up does not exist for the way this
    /// product is actually used.
    /// </summary>
    internal static class RepairSchedule
    {
        /// <summary>
        /// How often the repair pass runs while the app is up.
        ///
        /// 15 minutes is the balance point: short enough that a title or thumbnail lost to a
        /// transient failure is repaired inside the same working session (the 2026-08-11 gap was
        /// nearly seven hours), and long enough that the cost is negligible - the pass is four
        /// directory scans an hour over the recordings root, and it does nothing at all unless
        /// something is actually broken. Anything under ~5 minutes would scan far more often than
        /// failures occur; anything over an hour puts a visible hole back in the library for the
        /// rest of the day.
        /// </summary>
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

        /// <summary>
        /// True when the repair pass may start now.
        ///
        /// It must NOT start while a recording is in progress: repairs spend CPU on ffmpeg
        /// (thumbnails) and a network call (titling), and capture quality is the product's first
        /// duty. The skipped tick is not lost work - the next tick, or the pass that runs when the
        /// recording finishes post-processing, picks the same recordings up.
        ///
        /// This answers "may it START". It is NOT the whole exclusion: a recording that starts one
        /// millisecond after this returns true has to stop the pass as well, which is what
        /// <see cref="CaptureSignal"/> is for (issue #154).
        /// </summary>
        public static bool ShouldRunNow(bool isRecording) => !isRecording;
    }

    /// <summary>
    /// "A capture has started" as a process-wide, edge-triggered signal (issue #154).
    ///
    /// THE DEFECT THIS EXISTS FOR. <see cref="RepairService.RunAsync"/> read <c>IsRecording</c> ONCE,
    /// before taking its gate, and then ran ffmpeg thumbnail work and hosted title calls for as long
    /// as the pass lasted. A recording that started a moment after that read did not stop any of it -
    /// the guard was a check-then-act, so the exclusion the comment promised did not exist. Recording
    /// start took nothing at all on the repair side, so nothing could tell the pass to stand down.
    ///
    /// Two things are needed and a repeated <c>IsRecording()</c> sample gives only one of them. The
    /// sample catches a recording that is still running when the pass looks; it MISSES a short
    /// recording that starts and finishes entirely between two of the pass's stages - which is
    /// precisely the case where repair ffmpeg competed with capture. So this is a COUNTER, not a
    /// flag: a pass remembers the value it started with, and any change means a capture happened
    /// since, whether or not one is running right now.
    ///
    /// Capture is the winner by design. Repair yields to recording; recording never waits for repair.
    /// A recorder that made the user wait for a thumbnail backfill before it would start capturing
    /// would be a worse product than one that finishes its repairs a few minutes later.
    /// </summary>
    internal static class CaptureSignal
    {
        private static int _epoch;

        /// <summary>How many captures have been started in this process. A pass takes this once and
        /// compares against it - the VALUE is meaningless, the CHANGE is the signal.</summary>
        public static int Epoch => Volatile.Read(ref _epoch);

        /// <summary>
        /// A capture session is starting - called by <c>RecordingService.BeginSession</c> before a
        /// single writer is started, so an in-flight repair pass sees it at its next stage boundary.
        /// This is how starting a recording interacts with the repair gate.
        /// </summary>
        public static void CaptureStarted()
        {
            int epoch = Interlocked.Increment(ref _epoch);
            Log.Info($"[CaptureSignal] CaptureStarted: epoch={epoch} - any repair pass in flight must yield");
        }

        /// <summary>True when a capture has started since <paramref name="epoch"/> was taken.</summary>
        public static bool ChangedSince(int epoch) => Epoch != epoch;
    }

    /// <summary>
    /// One-at-a-time gate for the repair passes (issue #142). Now that the passes are triggered
    /// from three places - app start / sign-in, the end of a recording's post-processing, and the
    /// periodic timer - two of them can easily land together (a timer tick on top of a start-up
    /// pass). A second entry returns false immediately and the caller does nothing, so the same
    /// recording is never repaired twice at once.
    /// </summary>
    internal sealed class RepairGate
    {
        private int _running;

        /// <summary>True while a pass holds the gate.</summary>
        public bool IsRunning => Volatile.Read(ref _running) == 1;

        /// <summary>
        /// Takes the gate. Returns true when the caller owns it and must call <see cref="Exit"/>
        /// when finished; false when a pass is already running and the caller must do nothing.
        /// </summary>
        public bool TryEnter() => Interlocked.Exchange(ref _running, 1) == 0;

        /// <summary>Releases the gate. Safe to call when it is already released.</summary>
        public void Exit() => Interlocked.Exchange(ref _running, 0);
    }
}
