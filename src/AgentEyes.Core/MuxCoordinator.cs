using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// Issue #128 round two (D1, D2): the rules every deferred-mux attempt follows, whichever path
    /// starts it - the automatic post-recording runner or `agenteyes package` / the walkthrough
    /// rebuild.
    ///
    /// 1. One writer: the attempt runs under <see cref="MuxLock"/>, a lock on disk, so a second
    ///    process cannot start a second ffmpeg on the same output (D1, criterion 12).
    /// 2. Every attempt counts: <see cref="PostRecordingState.NoteStarted(string, string)"/> runs
    ///    before the work on every path, so an attempt abandoned by the stall watchdog or by process
    ///    death consumes a try and <see cref="PostRecordingState.MaxMuxAttempts"/> bounds it (D1,
    ///    criterion 13).
    /// 3. A dead attempt is reconciled, not trusted (D2, criterion 11): a mux killed by process death
    ///    never reaches its own cleanup, so it leaves the manifest saying "running" with no error and
    ///    a moov-less fragment at the FINAL path. Holding the lock proves no live process owns that
    ///    attempt, so <see cref="Reconcile"/> records it as failed with an "interrupted" error and
    ///    removes the fragment before anything else can read it.
    /// </summary>
    internal static class MuxCoordinator
    {
        /// <summary>The error recorded for an attempt that a dead process left "running".</summary>
        public const string InterruptedPrefix = "interrupted: ";

        /// <summary>
        /// Under the mux lock (the caller MUST hold it): settle what a dead earlier attempt left.
        /// A no-op when there is no pending mux. Never deletes a file it cannot prove is a fragment.
        /// </summary>
        public static void Reconcile(string dir)
        {
            if (!MuxLock.IsHeldByCurrentThread(dir))
                throw new InvalidOperationException($"MuxCoordinator.Reconcile called for {dir} without holding its MuxLock");
            Log.Info($"[MuxCoordinator] Reconcile: {Path.GetFileName(dir)}");

            var m = Manifest.Load(dir);
            var p = m.PendingMux;
            if (p == null) return;

            var record = PostRecordingState.Get(m, PostStage.Mux);
            if (record != null && string.Equals(record.State, PostStageState.Running, StringComparison.Ordinal))
            {
                string started = record.LastAttemptUtc?.ToString("o") ?? "(no start time recorded)";
                string error = $"{InterruptedPrefix}mux attempt {record.Attempts} (started {started}) never finished - " +
                               "the AgentEyes process running it ended (killed, crashed or restarted) part-way through";
                Log.Error($"[MuxCoordinator] Reconcile: {Path.GetFileName(dir)} - {error}");
                PostRecordingState.NoteFailed(dir, PostStage.Mux, error);
            }

            RemoveFragment(dir, p);
        }

        /// <summary>
        /// The final path of a recording whose mux is still pending can only hold a fragment: the
        /// mux clears <see cref="Manifest.PendingMux"/> only after it wrote the file completely. The
        /// one exception is a death in the instant between the mux finishing and that manifest write
        /// - after <see cref="OriginalBackup.Preserve"/> has already renamed the raw inputs away. So
        /// the file is removed only while every raw input the mux reads is still present; if one is
        /// gone the mux had completed, the file is kept, and that is logged loudly.
        /// </summary>
        private static void RemoveFragment(string dir, Manifest.PendingMuxInfo p)
        {
            if (string.IsNullOrWhiteSpace(p.FinalFile)) return;
            string finalPath = Path.Combine(dir, p.FinalFile);
            if (!File.Exists(finalPath)) return;

            foreach (string? input in new[] { p.RawVideo, p.SysWav, p.MicWav })
            {
                if (input == null) continue;
                if (!File.Exists(Path.Combine(dir, input)))
                {
                    Log.Error($"[MuxCoordinator] RemoveFragment: {Path.GetFileName(dir)} - {p.FinalFile} exists, the mux is " +
                              $"still marked pending, but its input {input} is gone (already preserved as the original): " +
                              "the mux completed and only its manifest update was lost. The file is KEPT.");
                    return;
                }
            }

            long size = new FileInfo(finalPath).Length;
            File.Delete(finalPath);
            Log.Info($"[MuxCoordinator] RemoveFragment: {Path.GetFileName(dir)} - removed {size}-byte fragment {p.FinalFile} " +
                     "left by an attempt that never finished");
        }

        /// <summary>
        /// The packaging path's mux (`agenteyes package`, the walkthrough rebuild): one counted
        /// attempt, under the lock, journaled like the automatic runner's. A no-op when nothing is
        /// pending. Throws when another process holds the lock, and when the attempts are used up.
        /// </summary>
        public static void RunForPackage(string dir)
        {
            Log.Info($"[MuxCoordinator] RunForPackage: {dir}");
            if (Manifest.Load(dir).PendingMux == null)
            {
                Log.Info($"[MuxCoordinator] RunForPackage: {Path.GetFileName(dir)} - no pending mux");
                return;
            }

            using var held = MuxLock.Acquire(dir, "package");
            Reconcile(dir);
            Package.RefuseExhaustedMux(dir);
            if (!PostRecordingPlan.NeedsMux(dir))
            {
                Log.Info($"[MuxCoordinator] RunForPackage: {Path.GetFileName(dir)} - the mux finished elsewhere before the lock was taken");
                return;
            }

            PostRecordingState.NoteStarted(dir, PostStage.Mux);
            try
            {
                RecordingService.FinalizePending(dir);
            }
            catch (Exception ex)
            {
                // Journal the failure on the record this attempt counted, then let the caller report it.
                PostRecordingState.NoteFailed(dir, PostStage.Mux, ex.Message);
                throw;
            }
            PostRecordingState.NoteDone(dir, PostStage.Mux);
            Log.Info($"[MuxCoordinator] RunForPackage: {Path.GetFileName(dir)} - mux done");
        }
    }
}
