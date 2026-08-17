namespace AgentEyes
{
    /// <summary>
    /// Is this app in the middle of something a restart would destroy? (issue #152)
    ///
    /// The predicate used to be "is the capture engine recording", and that was wrong in the exact
    /// case it existed for. An auto-update stages a new exe and defers its restart while a session is
    /// active; <c>RecordingService.Stop</c> then announces the end of the CAPTURE - minutes before
    /// the end of the WORK. The deferred restart fired into that gap and killed the mux and the
    /// transcription that had not started yet, leaving a recording with raw media and no transcript.
    ///
    /// A session is therefore active while EITHER the capture is running or post-recording work is in
    /// flight. Kept as a pure function so the composition is provable on its own - the two inputs
    /// come from <c>RecordingService.IsRecording</c> and <see cref="PostRecording.IsBusy"/>.
    /// </summary>
    internal static class SessionReadiness
    {
        /// <summary>
        /// True while a restart or exit would interrupt real work.
        /// </summary>
        /// <param name="capturing">The capture engine is recording right now.</param>
        /// <param name="postRecordingWorkInFlight">A recording's post-processing (mux, thumbnail,
        /// transcription, title, plugins) has been started and has not finished.</param>
        public static bool IsBusy(bool capturing, bool postRecordingWorkInFlight) =>
            capturing || postRecordingWorkInFlight;
    }
}
