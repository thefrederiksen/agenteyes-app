using NAudio.CoreAudioApi;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Windows default capture device lookup. Presets that say "system default mic"
    /// (Mic = null) resolve through here at record time, so switching headsets never
    /// requires editing presets.
    /// </summary>
    internal static class DefaultMic
    {
        /// <summary>Full friendly name of the Windows default input device. Throws a clear
        /// UsageException when Windows has no default input device set.</summary>
        public static string FriendlyName()
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                throw new UsageException(
                    "no default microphone is set in Windows (Settings > System > Sound > Input).");
            }
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.FriendlyName;
        }
    }
}
