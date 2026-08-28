namespace AgentEyes.Video
{
    /// <summary>
    /// HOW the camera process ended, as OBSERVED (issue #28, spec amendment 2026-08-28).
    ///
    /// It replaces nothing and proves nothing - it REPORTS. Three rounds of this fix tried to
    /// establish that camera.mp4 was complete, and each time the conclusion was wrong in the user's
    /// favour: a camera that ticked once and stalled, and a file that was force-killed mid-write,
    /// were both recorded as clean complete takes. So the recorder no longer reasons its way to
    /// "complete"; it writes down what it saw, and this is half of what it saw.
    /// </summary>
    internal enum CameraStopKind
    {
        /// <summary>It answered "q" and exited on its own, so ffmpeg finalized the MP4 itself.</summary>
        CleanQuit,

        /// <summary>It ignored "q" and was killed. Whatever is in camera.mp4 was never finalized.</summary>
        ForceKilled,

        /// <summary>It died before the stop was ever requested (unplugged, crashed, taken).</summary>
        ExitedEarly,

        /// <summary>It survived the quit, the kill AND the Dispose retry, and is STILL RUNNING.</summary>
        Abandoned,
    }

    /// <summary>
    /// Whether camera.mp4 is a complete take - a THREE-STATE answer, and the third state is the
    /// point (issue #28, spec amendment 2026-08-28, assumption A7).
    ///
    /// The boolean this replaces could only say "complete" or "truncated", so every case the code
    /// had not anticipated came out as "complete" - a claim made from an absence of evidence. There
    /// is now somewhere honest for those cases to go, and <see cref="Unknown"/> is the CORRECT
    /// answer whenever the evidence is incomplete. Writing it is never a failure of this code;
    /// writing <see cref="Yes"/> on incomplete evidence is.
    /// </summary>
    internal enum CameraCompleteness
    {
        /// <summary>The evidence does not say. The default, and the answer for every case not
        /// explicitly established as one of the other two.</summary>
        Unknown,

        /// <summary>KNOWN short or broken: the process exited early, was force-killed, or never
        /// reported writing a single frame.</summary>
        No,

        /// <summary>Established complete, and only from the full presence: a clean quit, ffmpeg's
        /// stderr read to end of stream, and output still advancing when the stop was asked for.
        /// A one-way door - see <see cref="FfmpegCameraRecorder.Completeness"/>.</summary>
        Yes,
    }

    /// <summary>
    /// The wire spelling of the two observations above - what goes into manifest.json.
    ///
    /// They are STRINGS in the manifest and not booleans or numbers (assumption A7): a consumer
    /// reading "unknown" cannot coerce it into false the way a nullable bool invites, and the three
    /// states are visible to a human opening the file.
    /// </summary>
    internal static class CameraObservation
    {
        public const string CleanQuit = "clean-quit";
        public const string ForceKilled = "force-killed";
        public const string ExitedEarly = "exited-early";
        public const string Abandoned = "abandoned";

        public const string Yes = "yes";
        public const string No = "no";
        public const string Unknown = "unknown";

        public static string Text(CameraStopKind kind) => kind switch
        {
            CameraStopKind.CleanQuit => CleanQuit,
            CameraStopKind.ForceKilled => ForceKilled,
            CameraStopKind.ExitedEarly => ExitedEarly,
            CameraStopKind.Abandoned => Abandoned,
            // No default that guesses: a stop kind nobody added here must break the build's own
            // tests rather than quietly become one of the four.
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind, "unknown camera stop kind"),
        };

        /// <summary>The manifest spelling of a stop kind, or null when no stop was ever observed -
        /// an ABSENT field, because "we never watched it stop" is not one of the four kinds.</summary>
        public static string? Text(CameraStopKind? kind) => kind is { } k ? Text(k) : null;

        public static string Text(CameraCompleteness completeness) => completeness switch
        {
            CameraCompleteness.Yes => Yes,
            CameraCompleteness.No => No,
            CameraCompleteness.Unknown => Unknown,
            _ => throw new System.ArgumentOutOfRangeException(nameof(completeness), completeness, "unknown camera completeness"),
        };
    }
}
