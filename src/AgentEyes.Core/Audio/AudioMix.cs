using AgentEyes.Video;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Combines separately-captured mic and system-audio streams using ffmpeg (amix), with the
    /// clean-voice chain on the mic (RNNoise suppression, gate, leveling, volume) and a safety
    /// limiter on the result. Run at stop time.
    /// </summary>
    internal static class AudioMix
    {
        /// <summary>Mic WAV + system WAV -> one mixed WAV.</summary>
        public static void MixWavs(string micWav, string sysWav, string outWav, AudioMixOptions o)
        {
            Prepare(o);
            Ffmpeg.Run(FfmpegArgs.MixTwoWav(micWav, sysWav, outWav, o), "mix mic+system");
        }

        /// <summary>Video (with mic track) + system WAV -> final MP4 with mixed audio.</summary>
        public static void MuxVideoMixed(string rawMp4, string sysWav, string outMp4, AudioMixOptions o)
        {
            Prepare(o);
            Ffmpeg.Run(FfmpegArgs.MuxVideoMixMicSystem(rawMp4, sysWav, outMp4, o), "mux video+mic+system");
        }

        /// <summary>Video (no audio) + system WAV -> final MP4 with system audio only.</summary>
        public static void MuxVideoSystemOnly(string rawMp4, string sysWav, string outMp4, double sysGain)
            => Ffmpeg.Run(FfmpegArgs.MuxVideoAddSystem(rawMp4, sysWav, outMp4, sysGain), "mux video+system");

        /// <summary>Video with a mic-only track -> final MP4 with the mic run through the clean-voice chain.</summary>
        public static void ProcessVideoMic(string rawMp4, string outMp4, AudioMixOptions o)
        {
            Prepare(o);
            Ffmpeg.Run(FfmpegArgs.FilterVideoMic(rawMp4, outMp4, o), "process mic audio");
        }

        /// <summary>Materialize the RNNoise model on disk when suppression is enabled.</summary>
        private static void Prepare(AudioMixOptions o)
        {
            if (o.NoiseSuppression && string.IsNullOrWhiteSpace(o.RnnoiseModelPath))
                o.RnnoiseModelPath = RnnoiseModel.Ensure();
        }
    }
}
