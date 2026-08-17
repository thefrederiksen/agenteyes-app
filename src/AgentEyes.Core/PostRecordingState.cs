using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// The names of the post-recording stages (issue #152). They are the keys of
    /// <see cref="Manifest.PostProcessing"/>, so they are a fixed on-disk vocabulary - renaming one
    /// orphans the record of every recording already on disk.
    /// </summary>
    internal static class PostStage
    {
        /// <summary>Complete the deferred audio mux / system downmix (issue #77), which is what
        /// produces the final recording.mp4 / audio.wav.</summary>
        public const string Mux = "mux";

        /// <summary>Generate the Library poster / waveform tile.</summary>
        public const string Thumbnail = "thumbnail";

        /// <summary>Transcribe, name, and assemble the walkthrough.</summary>
        public const string Package = "package";

        /// <summary>The app's post-packaging plugin step (issue #13).</summary>
        public const string Plugins = "plugins";

        /// <summary>Every stage, in the order the sequence runs them.</summary>
        public static readonly string[] All = { Mux, Thumbnail, Package, Plugins };
    }

    /// <summary>The states a stage record can carry (issue #152).</summary>
    internal static class PostStageState
    {
        /// <summary>The stage was started and has not reported an outcome - the shape an
        /// interrupted process leaves behind.</summary>
        public const string Running = "running";

        /// <summary>The stage finished successfully.</summary>
        public const string Done = "done";

        /// <summary>The stage threw. <see cref="Manifest.PostStageRecord.Error"/> says why.</summary>
        public const string Failed = "failed";
    }

    /// <summary>
    /// Reads and writes the durable per-stage record in <see cref="Manifest.PostProcessing"/>
    /// (issue #152).
    ///
    /// Before this, the post-recording sequence was in-memory only: a thumbnail failure, a crash, or
    /// an update restart left a half-processed recording with nothing on disk saying which stages had
    /// run, and nothing ever finished it. The record is what makes a stage's outcome survive the
    /// process.
    ///
    /// It is deliberately a JOURNAL and not the authority on what still needs doing - manifest.json
    /// is not written atomically yet, so a torn record must not be able to strand a recording.
    /// <see cref="PostRecordingPlan"/> decides outstanding work from the artifacts on disk; this
    /// record adds the failure diagnosis and the one ceiling that has no artifact to count
    /// (<see cref="MaxMuxAttempts"/>).
    /// </summary>
    internal static class PostRecordingState
    {
        /// <summary>
        /// How many times the deferred mux is attempted automatically before the recovery pass
        /// leaves the recording alone. Its own ceiling because the mux has no artifact-based counter
        /// like <see cref="Manifest.ThumbAttempts"/> or <see cref="Manifest.TranscribeAttempts"/>,
        /// and because a raw capture ffmpeg can never mux would otherwise re-run on every 15-minute
        /// tick forever. Three matches the thumbnail ceiling: the same local-ffmpeg cost argument.
        /// </summary>
        public const int MaxMuxAttempts = 3;

        /// <summary>How much of a failure message is kept in the manifest. The log holds the full
        /// exception; this is the at-a-glance diagnosis next to the recording.</summary>
        public const int MaxErrorChars = 300;

        /// <summary>The stage record for <paramref name="stage"/>, or null when that stage has never
        /// reported for this recording.</summary>
        public static Manifest.PostStageRecord? Get(Manifest manifest, string stage)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrEmpty(stage)) throw new ArgumentException("stage is required", nameof(stage));
            return manifest.PostProcessing.TryGetValue(stage, out var record) ? record : null;
        }

        /// <summary>How many times <paramref name="stage"/> has been attempted for this
        /// recording.</summary>
        public static int Attempts(Manifest manifest, string stage) => Get(manifest, stage)?.Attempts ?? 0;

        /// <summary>True when <paramref name="stage"/>'s last recorded outcome was success. Callers
        /// use this for diagnosis and for stages with no artifact of their own (plugins) - never as
        /// the sole reason to skip work that the files on disk say is missing.</summary>
        public static bool IsDone(Manifest manifest, string stage) =>
            string.Equals(Get(manifest, stage)?.State, PostStageState.Done, StringComparison.Ordinal);

        /// <summary>
        /// Records that <paramref name="stage"/> has STARTED, counting the attempt before the work
        /// runs. Counted before, not after, for the same reason
        /// <see cref="TranscriptionBacklog.NoteAttempt"/> is: a process killed mid-stage must still
        /// consume a try, or work that hard-kills the pass would be retried on every launch forever.
        /// </summary>
        public static void NoteStarted(string dir, string stage) => NoteStarted(dir, stage, DateTime.UtcNow);

        /// <summary><see cref="NoteStarted(string, string)"/> against an explicit clock, so the
        /// stamp is testable.</summary>
        public static void NoteStarted(string dir, string stage, DateTime nowUtc)
        {
            Log.Info($"[PostRecordingState] NoteStarted: {Path.GetFileName(dir)} stage={stage}");
            Update(dir, stage, record =>
            {
                record.State = PostStageState.Running;
                record.Attempts++;
                record.LastAttemptUtc = nowUtc;
                record.Error = null;
            });
        }

        /// <summary>Records that <paramref name="stage"/> finished successfully.</summary>
        public static void NoteDone(string dir, string stage)
        {
            Log.Info($"[PostRecordingState] NoteDone: {Path.GetFileName(dir)} stage={stage}");
            Update(dir, stage, record =>
            {
                record.State = PostStageState.Done;
                record.Error = null;
                if (record.Attempts == 0) record.Attempts = 1;
                record.LastAttemptUtc ??= DateTime.UtcNow;
            });
        }

        /// <summary>Records that <paramref name="stage"/> failed, with the reason.</summary>
        public static void NoteFailed(string dir, string stage, string error)
        {
            Log.Info($"[PostRecordingState] NoteFailed: {Path.GetFileName(dir)} stage={stage} error={Shorten(error)}");
            Update(dir, stage, record =>
            {
                record.State = PostStageState.Failed;
                record.Error = Shorten(error);
                if (record.Attempts == 0) record.Attempts = 1;
                record.LastAttemptUtc ??= DateTime.UtcNow;
            });
        }

        /// <summary>
        /// Load - mutate one stage record - save. The caller holds the recording's
        /// <see cref="RecordingWorkset"/> claim, which is what keeps this load-mutate-save from
        /// racing a repair pass on the same manifest.
        /// </summary>
        private static void Update(string dir, string stage, Action<Manifest.PostStageRecord> mutate)
        {
            if (string.IsNullOrEmpty(dir)) throw new ArgumentException("dir is required", nameof(dir));
            if (string.IsNullOrEmpty(stage)) throw new ArgumentException("stage is required", nameof(stage));

            ManifestStore.Update(dir, manifest =>
            {
                if (!manifest.PostProcessing.TryGetValue(stage, out var record))
                {
                    record = new Manifest.PostStageRecord();
                    manifest.PostProcessing[stage] = record;
                }
                mutate(record);
            });
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= MaxErrorChars ? text : text.Substring(0, MaxErrorChars);
        }
    }
}
