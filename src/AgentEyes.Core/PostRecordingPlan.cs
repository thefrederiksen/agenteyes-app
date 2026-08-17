using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentEyes
{
    /// <summary>
    /// What a recording still needs from the post-recording sequence (issue #152).
    ///
    /// This is the resume rule, and it is deliberately derived from the ARTIFACTS ON DISK, exactly
    /// like the transcription and thumbnail backlogs before it: a pending mux means the final media
    /// file was never written, no thumb file means no poster, no transcript.json means it was never
    /// transcribed. The durable stage journal (<see cref="Manifest.PostProcessing"/>) records what
    /// happened and carries the mux ceiling, but it is never the reason to skip work the files say is
    /// missing.
    ///
    /// That split is what makes the design safe whatever state the record is in: an unreadable
    /// manifest cannot convince this class that a finished recording is unfinished, and cannot
    /// convince it that an unfinished one is done. The worst a damaged record can cost is one extra
    /// idempotent attempt, and a directory whose manifest cannot be read at all is logged and skipped
    /// by <see cref="FindUnfinished"/> instead of taking the whole scan down with it. (Manifest
    /// writes became atomic in issue #155 - <see cref="ManifestStore"/> - and a recording now has a
    /// valid manifest from the moment it starts, so a torn file is no longer the case being defended
    /// against; the artifacts stay the authority regardless.)
    /// </summary>
    internal static class PostRecordingPlan
    {
        /// <summary>
        /// True when the deferred audio mux (issue #77) never completed for this recording, so the
        /// final recording.mp4 / audio.wav does not exist yet. Bounded by
        /// <see cref="PostRecordingState.MaxMuxAttempts"/> - a raw capture ffmpeg can never mux drops
        /// out of the automatic pass instead of running on every tick forever.
        /// </summary>
        public static bool NeedsMux(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) return false;

            var manifest = Manifest.Load(dir);
            return NeedsMux(manifest);
        }

        /// <summary><see cref="NeedsMux(string)"/> against an already-loaded manifest.</summary>
        public static bool NeedsMux(Manifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (manifest.PendingMux == null) return false;
            return PostRecordingState.Attempts(manifest, PostStage.Mux) < PostRecordingState.MaxMuxAttempts;
        }

        /// <summary>
        /// True when this recording has no Library thumbnail and one can be generated now. Claim-blind
        /// on purpose: the caller of the sequence already OWNS the recording's
        /// <see cref="RecordingWorkset"/> claim, so the scan-time "someone else is working on it"
        /// check would otherwise veto the owner's own stage.
        /// </summary>
        public static bool NeedsThumbnail(string dir) => Thumbnails.NeedsThumb(dir, respectClaim: false);

        /// <summary>True when this recording has media but no transcript, and has attempts left.</summary>
        public static bool NeedsPackage(string dir) => TranscriptionBacklog.NeedsTranscription(dir);

        /// <summary>
        /// The stages still outstanding for <paramref name="dir"/> right now, in the order the
        /// sequence runs them. Each stage is re-evaluated as the sequence progresses (a mux writes the
        /// media file the next two stages need), so this is a snapshot for logging, scanning and
        /// tests - never a plan the sequence caches and then executes blind.
        /// </summary>
        public static IReadOnlyList<string> Outstanding(string dir)
        {
            var stages = new List<string>();
            if (NeedsMux(dir)) stages.Add(PostStage.Mux);
            if (NeedsThumbnail(dir)) stages.Add(PostStage.Thumbnail);
            if (NeedsPackage(dir)) stages.Add(PostStage.Package);
            return stages;
        }

        /// <summary>True when any post-recording stage is still outstanding for this recording.</summary>
        public static bool HasUnfinishedWork(string dir) => Outstanding(dir).Count > 0;

        /// <summary>
        /// Every recording under <paramref name="root"/> with unfinished post-processing, oldest
        /// first so a backlog clears in the order it was created.
        ///
        /// One damaged recording must never stop the recovery of the others (that is how a single
        /// torn manifest.json used to be able to strand an entire library), so a directory that
        /// cannot be read is logged and skipped rather than throwing out of the scan. A directory
        /// someone else is already working on is skipped too - it comes back on the next pass.
        /// </summary>
        public static IReadOnlyList<string> FindUnfinished(string root)
        {
            if (!Directory.Exists(root))
            {
                Log.Info($"[PostRecordingPlan] FindUnfinished: no recordings root at {root}");
                return Array.Empty<string>();
            }

            var unfinished = new List<string>();
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                if (RecordingWorkset.IsClaimed(dir))
                {
                    Log.Info($"[PostRecordingPlan] FindUnfinished: skipping {Path.GetFileName(dir)} - work is in flight for it");
                    continue;
                }

                // Entry point for the scan: a recording whose manifest cannot be parsed is quarantined
                // to itself and reported, never allowed to abort the pass for every later recording.
                try
                {
                    var outstanding = Outstanding(dir);
                    if (outstanding.Count == 0) continue;
                    Log.Info($"[PostRecordingPlan] FindUnfinished: {Path.GetFileName(dir)} needs {string.Join(", ", outstanding)}");
                    unfinished.Add(dir);
                }
                catch (Exception ex)
                {
                    Log.Error($"[PostRecordingPlan] FindUnfinished: skipping {dir} - it could not be read", ex);
                }
            }

            Log.Info($"[PostRecordingPlan] FindUnfinished: root={root}, unfinished={unfinished.Count}");
            return unfinished;
        }
    }
}
