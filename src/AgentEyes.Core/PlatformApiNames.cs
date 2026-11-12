namespace AgentEyes
{
    /// <summary>
    /// The name of the capture API this build actually talks to, for the error messages a user reads
    /// (issue #111).
    ///
    /// WHY THIS EXISTS. The device-resolution failures in <see cref="DeviceResolver"/> were written on
    /// Windows and hardcoded the word "DirectShow", so a Mac that could not match a microphone sent
    /// its owner looking for a DirectShow device on a machine that has never had one - and the same
    /// wording on Linux named the wrong API too. The resolver itself is shared and correct; only the
    /// noun was wrong. Naming it in one place keeps the Windows wording byte-for-byte what it was and
    /// makes the other two platforms say what they really use.
    ///
    /// These are compile-time constants on purpose: the engine is built once per platform (see the
    /// AgentEyesUnixPlatform split in AgentEyes.Core.csproj), so there is no runtime decision to make
    /// and no fallback to get wrong.
    /// </summary>
    internal static class PlatformApiNames
    {
#if WINDOWS
        /// <summary>The API the microphones come from: DirectShow, via ffmpeg's dshow demuxer.</summary>
        public const string Audio = "DirectShow";

        /// <summary>The API the cameras come from: DirectShow video devices.</summary>
        public const string Camera = "DirectShow";
#elif MACOS
        /// <summary>The API the microphones come from: AVFoundation audio inputs.</summary>
        public const string Audio = "AVFoundation";

        /// <summary>The API the cameras come from: AVFoundation video inputs that are not displays.</summary>
        public const string Camera = "AVFoundation";
#else
        /// <summary>The API the microphones come from: PulseAudio sources.</summary>
        public const string Audio = "PulseAudio";

        /// <summary>The API the cameras come from: V4L2 capture nodes.</summary>
        public const string Camera = "V4L2";
#endif
    }
}
