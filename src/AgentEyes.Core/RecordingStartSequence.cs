using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentEyes
{
    /// <summary>
    /// One named unit of START-UP in a capture session (issue #155): construct a writer and start it.
    /// Named for the same reason <see cref="RecordingStopStep"/> is - the name is what the failure is
    /// reported as, and "which writer failed to start" is the first question asked about a start that
    /// threw.
    /// </summary>
    internal sealed class RecordingStartStep
    {
        public RecordingStartStep(string name, Action start)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("a start step must be named - the name is what the failure is reported as", nameof(name));
            Name = name;
            Start = start ?? throw new ArgumentNullException(nameof(start));
        }

        /// <summary>What this step starts ("microphone", "system loopback", "video").</summary>
        public string Name { get; }

        public Action Start { get; }
    }

    /// <summary>
    /// The failure-safe START of a capture session (issue #155) - the mirror of
    /// <see cref="RecordingStopSequence"/>, and deliberately built on it rather than beside it.
    ///
    /// THE DEFECT THIS EXISTS FOR. A session starts more than one writer: mixed audio starts the
    /// microphone and THEN the system loopback; system/mixed video starts ffmpeg and THEN the
    /// loopback. The rollback used to release the directory claim and clear the session fields while
    /// leaving <c>_audio</c>, <c>_loop</c> or <c>_video</c> ALIVE. The caller saw a failed start, the
    /// service reported itself idle, another recording could be started - and the first writer went
    /// on capturing the microphone and the speakers with nothing on screen saying so. For a recorder
    /// whose whole posture is "visible, controllable", capture that continues after the app reports
    /// idle is the worst failure it has. The publish itself was outside the boundary too, so a failed
    /// first manifest write left the directory, a write-temp and the claim behind.
    ///
    /// So the boundary here covers EVERYTHING that a start puts into the world, in one try, and the
    /// rollback runs in strictly this order:
    ///
    ///  1. stop and dispose every writer that may have started - through
    ///     <see cref="RecordingStopSequence.StopWriters"/>, the same failure-isolated machinery the
    ///     stop uses, so a writer whose Stop throws is still disposed and does not abandon the
    ///     writers after it;
    ///  2. THEN release the directory claim and remove a directory that captured nothing.
    ///
    /// The order is the point. Releasing the claim first would publish - to every automatic repair
    /// pass in the app - a directory that a live writer still has open.
    ///
    /// A failure DURING the rollback never replaces the failure the caller is about to be given: it
    /// is collected, logged with the directory, and the original exception is rethrown. The caller is
    /// being told the start failed, which is the actionable fact.
    ///
    /// Injectable by construction, exactly like the stop: the publish, the steps and the rollback are
    /// delegates, so a test can fail any position - the first writer, the second writer, the video
    /// writer, or the first manifest write - with no sound card, no ffmpeg and no full disk.
    /// </summary>
    internal static class RecordingStartSequence
    {
        /// <summary>
        /// Publish the session and start every writer, inside ONE failure boundary. On any failure -
        /// including the publish - every writer that may have started is stopped and disposed, the
        /// claim is released, a directory that captured nothing is removed, and the original
        /// exception is rethrown.
        /// </summary>
        /// <param name="dir">The recording directory (used in every log line).</param>
        /// <param name="publish">Claim the directory and write the recording's FIRST manifest.</param>
        /// <param name="steps">The writers to start, in start order.</param>
        /// <param name="startedWriters">The writers that are live NOW - read only on the failure
        /// path, after the failing step, so it reports what actually has to be shut down.</param>
        /// <param name="releaseSession">Release the claim, discard the directory if it captured
        /// nothing, and clear the caller's session state. Runs AFTER the writers are down.</param>
        public static void Run(
            string dir,
            Action publish,
            IReadOnlyList<RecordingStartStep> steps,
            Func<IReadOnlyList<RecordingStopStep>> startedWriters,
            Action releaseSession)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (publish == null) throw new ArgumentNullException(nameof(publish));
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            if (startedWriters == null) throw new ArgumentNullException(nameof(startedWriters));
            if (releaseSession == null) throw new ArgumentNullException(nameof(releaseSession));

            Log.Info($"[RecordingStartSequence] Run: dir={dir} steps={steps.Count}");
            try
            {
                publish();
                foreach (var step in steps)
                {
                    if (step == null) throw new ArgumentException("a start step is null", nameof(steps));
                    Log.Info($"[RecordingStartSequence] Run: starting {step.Name} dir={dir}");
                    step.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RecordingStartSequence] Run FAILED: dir={dir} - rolling the session back", ex);
                Abandon(dir, startedWriters(), releaseSession);
                throw;
            }

            Log.Info($"[RecordingStartSequence] Run: dir={dir} started with no failures");
        }

        /// <summary>
        /// Roll a failed start back: stop and dispose every writer that may have started, THEN
        /// release the session. Returns everything that went wrong while doing it - logged here, and
        /// never thrown, because the caller is already carrying the real failure.
        /// </summary>
        public static IReadOnlyList<RecordingStopFailure> Abandon(
            string dir, IReadOnlyList<RecordingStopStep> startedWriters, Action releaseSession)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (startedWriters == null) throw new ArgumentNullException(nameof(startedWriters));
            if (releaseSession == null) throw new ArgumentNullException(nameof(releaseSession));

            Log.Warn($"[RecordingStartSequence] Abandon: dir={dir} writers={startedWriters.Count}");
            var failures = new List<RecordingStopFailure>();

            // The writers FIRST, and every one of them: the claim must not be released - and the
            // directory must not be removed - while anything is still capturing into it.
            RecordingStopSequence.StopWriters(dir, startedWriters, failures);

            // This is an entry point: the rollback's own failure is reported here and nowhere else,
            // because the original exception is what the caller is about to receive.
            try
            {
                releaseSession();
            }
            catch (Exception ex)
            {
                failures.Add(new RecordingStopFailure("session release", ex));
                Log.Error($"[RecordingStartSequence] Abandon: releasing the session FAILED: dir={dir}", ex);
            }

            if (failures.Count > 0)
                Log.Error($"[RecordingStartSequence] Abandon: dir={dir} rolled back with {failures.Count} failure(s) - "
                          + string.Join("; ", failures.Select(f => f.ToString())));
            else
                Log.Info($"[RecordingStartSequence] Abandon: dir={dir} rolled back cleanly");

            return failures;
        }

        /// <summary>
        /// Give up the directory a failed session created: release the claim, then remove the
        /// directory when it holds no capture bytes.
        ///
        /// The manifest is on disk by the time a writer can fail, and a directory with a manifest is
        /// a recording to every scan in the app - the Library lists it, the repair passes pick it up.
        /// A session that captured nothing must not leave one. A write-temp from a failed first
        /// manifest write is not capture bytes either (<see cref="RecordingStopSequence.HasRawArtifacts"/>),
        /// so the directory goes and the temp goes with it.
        ///
        /// A directory that DOES hold capture bytes is kept: those bytes plus the start manifest are
        /// a recoverable recording, which is the whole reason the manifest is written first.
        ///
        /// IT CLEANS UP ONLY WHAT THIS START OWNS (issue #154, round 3). <paramref name="claim"/> is
        /// the capture claim the start won, and a start that was REFUSED the claim - a directory-name
        /// collision, so the directory belongs to another owner - passes a ticket that holds nothing.
        /// In that case there is nothing to release AND nothing to remove: the directory is somebody
        /// else's recording, and deleting it (or freeing their claim) because OUR start failed is a
        /// far worse outcome than the failed start itself.
        /// </summary>
        /// <param name="dir">The directory the failed start was using.</param>
        /// <param name="claim">The capture claim this start actually won, or a default ticket when it
        /// never won one.</param>
        public static void Discard(string dir, in RecordingClaimTicket claim)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));

            Log.Info($"[RecordingStartSequence] Discard: dir={dir} claim={claim}");

            if (!claim.Held)
            {
                Log.Error($"[RecordingStartSequence] Discard: this start never owned {dir} - nothing is "
                    + "released and NOTHING is removed; the directory belongs to whoever holds it");
                return;
            }

            RecordingWorkset.Release(claim);

            if (!Directory.Exists(dir))
            {
                Log.Info($"[RecordingStartSequence] Discard: {dir} does not exist - the claim is released and there is nothing to remove");
                return;
            }

            if (RecordingStopSequence.HasRawArtifacts(dir))
            {
                Log.Warn($"[RecordingStartSequence] Discard: {dir} already holds capture bytes - keeping it (the start manifest makes it recoverable)");
                return;
            }

            Log.Warn($"[RecordingStartSequence] Discard: the capture never started - removing {dir}");
            Directory.Delete(dir, recursive: true);
            Log.Info($"[RecordingStartSequence] Discard: dir={dir} removed");
        }
    }
}
