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
            Prepare(o, micWav);
            RunStage(FfmpegArgs.MixTwoWav(micWav, sysWav, outWav, o), outWav, "mix mic+system");
        }

        /// <summary>Video (with mic track) + system WAV -> final MP4 with mixed audio.</summary>
        public static void MuxVideoMixed(string rawMp4, string sysWav, string outMp4, AudioMixOptions o)
        {
            Prepare(o, rawMp4);
            RunStage(FfmpegArgs.MuxVideoMixMicSystem(rawMp4, sysWav, outMp4, o), outMp4, "mux video+mic+system");
        }

        /// <summary>Video (no audio) + system WAV -> final MP4 with system audio only.</summary>
        public static void MuxVideoSystemOnly(string rawMp4, string sysWav, string outMp4, double sysGain)
            => RunStage(FfmpegArgs.MuxVideoAddSystem(rawMp4, sysWav, outMp4, sysGain), outMp4, "mux video+system");

        /// <summary>Video with a mic-only track -> final MP4 with the mic run through the clean-voice chain.</summary>
        public static void ProcessVideoMic(string rawMp4, string outMp4, AudioMixOptions o)
        {
            Prepare(o, rawMp4);
            RunStage(FfmpegArgs.FilterVideoMic(rawMp4, outMp4, o), outMp4, "process mic audio");
        }

        /// <summary>
        /// Issue #128: run one audio post-processing pass under the stall watchdog, and if it does
        /// not succeed, remove whatever partial output it left. A killed or failed pass leaves a
        /// truncated file with no moov atom at the FINAL path; later stages test for that file's
        /// existence and would treat a fragment as the finished recording (measured: the thumbnail
        /// stage then failed on "moov atom not found"). The raw capture is untouched either way.
        /// </summary>
        internal static void RunStage(System.Collections.Generic.IReadOnlyList<string> args, string outPath, string label)
        {
            bool ok = false;
            try
            {
                Ffmpeg.RunWatched(args, label, Ffmpeg.PostProcessStallTimeout);
                ok = true;
            }
            finally
            {
                if (!ok && System.IO.File.Exists(outPath))
                {
                    System.IO.File.Delete(outPath);
                    Log.Info($"[AudioMix] RunStage: {label} did not complete - removed partial output {outPath}");
                }
            }
        }

        /// <summary>
        /// Everything the mic chain needs before it can be built: the RNNoise model materialized on
        /// disk, and the gate threshold measured from the capture that is about to be processed.
        /// </summary>
        private static void Prepare(AudioMixOptions o, string micSource)
        {
            if (o.NoiseSuppression && string.IsNullOrWhiteSpace(o.RnnoiseModelPath))
                o.RnnoiseModelPath = RnnoiseModel.Ensure();

            Calibrate(o, micSource);
        }

        /// <summary>
        /// Measures <paramref name="micSource"/> and sets the gate threshold from what it finds.
        ///
        /// Always re-measures rather than trusting a value already on the options: these options are
        /// serialized into the manifest for a deferred mux, so a stale threshold from an earlier
        /// attempt could otherwise be applied to a different capture.
        /// </summary>
        internal static void Calibrate(AudioMixOptions o, string micSource)
        {
            if (!o.NoiseGate)
            {
                // Nothing to measure for. Mark it resolved so the chain builder can tell "the person
                // turned the gate off" apart from "nobody measured yet".
                o.GateThresholdLinear = null;
                o.GateSkipReason = null;
                o.GateCalibrated = true;
                return;
            }

            var levels = MicMeasure.Measure(micSource);
            o.GateThresholdLinear = GateCalibration.ThresholdLinear(levels);
            o.GateSkipReason = o.GateThresholdLinear == null ? GateCalibration.SkipReason(levels) : null;
            o.GateCalibrated = true;

            Log.Info($"[AudioMix] Calibrate: source={micSource} {levels} -> {o.GateDescription()}");
        }
    }
}
