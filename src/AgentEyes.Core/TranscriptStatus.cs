using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// What a recording's transcript presence actually is (issue #4). One recording is in exactly
    /// one of these states, and every surface that says "transcribed" derives it from here.
    /// </summary>
    internal enum TranscriptKind
    {
        /// <summary>No transcript artifact of any kind.</summary>
        None,

        /// <summary>Only a legacy flat transcript.txt exists - the recording was NEVER transcribed
        /// by the current pipeline (no transcript.json). The text is real content the user may
        /// read, but the recording must not be presented as transcribed; the backlog will still
        /// pick it up (<see cref="TranscriptionBacklog.NeedsTranscription"/>).</summary>
        FlatTextOnly,

        /// <summary>Transcription completed: the manifest-named transcript.json artifact exists.</summary>
        Transcribed,
    }

    /// <summary>
    /// The canonical transcript-presence predicate, shared by the Control API
    /// (<see cref="RecordingLibrary"/>) and the WPF Library surfaces (issue #4).
    ///
    /// Before this, the REST API, the Library card and the detail window each decided "has a
    /// transcript" on their own - the card on transcript.txt EXISTING, the detail window on
    /// flat-text LENGTH - so a legacy flat-text-only recording was shown as transcribed while the
    /// transcription backlog still had it queued. The user-visible contradiction is exactly the
    /// confusion this line of work exists to remove.
    ///
    /// Completion here means the same artifact the rest of the pipeline treats as completion: the
    /// manifest-named transcript JSON (default "transcript.json") - see
    /// <see cref="TranscriptionBacklog.NeedsTranscription"/> and <see cref="Package"/>, which
    /// writes it. Detection is existence-based like the backlog's; judging the artifact by PARSING
    /// it is a separate issue (#15) and deliberately not absorbed here - when that lands, it lands
    /// in one place.
    ///
    /// Pure file-system reads, no logging, matching the sibling predicates in
    /// <see cref="RecordingLibrary"/> (HasVideo / HasAudio) that run per card on every library load.
    /// </summary>
    internal static class TranscriptStatus
    {
        /// <summary>The flat transcript file name legacy recordings carry (and the current
        /// pipeline still writes alongside the JSON for plugins and humans).</summary>
        private const string FlatTextFile = "transcript.txt";

        /// <summary>Default transcript artifact name when the manifest does not name one.</summary>
        private const string DefaultJsonFile = "transcript.json";

        /// <summary>
        /// Canonical completion: the manifest-named transcript JSON exists in
        /// <paramref name="dir"/>. <paramref name="manifest"/> may be null (unreadable or missing
        /// manifest) - the default artifact name is used then.
        /// </summary>
        public static bool IsTranscribed(string dir, Manifest? manifest)
        {
            string jsonName = string.IsNullOrWhiteSpace(manifest?.Transcript)
                ? DefaultJsonFile : manifest!.Transcript!;
            return File.Exists(Path.Combine(dir, jsonName));
        }

        /// <summary>True when the legacy flat transcript.txt exists in <paramref name="dir"/>,
        /// regardless of whether the recording is also transcribed.</summary>
        public static bool HasFlatText(string dir) =>
            File.Exists(Path.Combine(dir, FlatTextFile));

        /// <summary>The single classification every UI/API decision hangs off.</summary>
        public static TranscriptKind Classify(string dir, Manifest? manifest)
        {
            if (IsTranscribed(dir, manifest)) return TranscriptKind.Transcribed;
            if (HasFlatText(dir)) return TranscriptKind.FlatTextOnly;
            return TranscriptKind.None;
        }
    }
}
