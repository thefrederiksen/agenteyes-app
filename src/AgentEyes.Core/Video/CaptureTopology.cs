namespace AgentEyes.Video
{
    /// <summary>
    /// WHICH PROCESS CAPTURES THE MICROPHONE during a screen recording - the one platform difference
    /// that the shared recording pipeline has to know about, named once here so RecordingService stays
    /// free of platform conditionals (issue #114).
    ///
    /// Windows (gdigrab + dshow) and Linux (x11grab + pulse) take the microphone as a second input of
    /// the SAME ffmpeg that captures the screen, and that ffmpeg writes a file with sound in it.
    /// macOS does not: the microphone is captured by its own ffmpeg (AvfStream, behind AudioCapture)
    /// into its own WAV, and the deferred mux puts it into the recording - the same shape the
    /// system-audio path has always had.
    ///
    /// This split is NOT the fix for #114's audio loss, and reading it as one is the mistake to
    /// avoid. The measured cause was scheduling class: the launchd agent carried no ProcessType, so
    /// macOS ran the recorder as throttled background work, and ffmpeg's AVFoundation demuxer keeps
    /// exactly one sample buffer per device and silently discards whatever arrives before the reader
    /// takes it. `ProcessType Interactive` in scripts/mac/com.agenteyes.recorder.plist is the
    /// load-bearing fix; the numbers and the retraction of the earlier process-coupling hypothesis
    /// are on FfmpegArgs.AvfVideoCapture. The split is kept because one AVFoundation input per
    /// process has one fewer way to drop samples, and because a mic WAV on disk makes any future
    /// loss visible and measurable against the take.
    /// </summary>
    internal static class CaptureTopology
    {
        /// <summary>
        /// True when the screen recorder's own ffmpeg captures the microphone, so a mic-only video
        /// recording needs no audio writer beside it and needs no mux to have sound.
        /// False on macOS: there the microphone is a separate writer and the mux is REQUIRED.
        /// </summary>
        public static bool ScreenRecorderCapturesMic =>
#if MACOS
            false;
#else
            true;
#endif

        /// <summary>
        /// The microphone WAV a screen recording captures beside the video when
        /// <see cref="ScreenRecorderCapturesMic"/> is false. Named like sys_native.wav - the raw
        /// capture the deferred mux consumes, not a file the library shows.
        /// </summary>
        public const string MicNativeWav = "mic_native.wav";
    }
}
