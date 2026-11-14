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
            MuxInTwoPasses(rawMp4, outMp4, "mux video+mic+system",
                wav => FfmpegArgs.MixMicSystemToWav(rawMp4, sysWav, wav, o));
        }

        /// <summary>Video (no audio) + system WAV -> final MP4 with system audio only.</summary>
        public static void MuxVideoSystemOnly(string rawMp4, string sysWav, string outMp4, double sysGain)
            => MuxInTwoPasses(rawMp4, outMp4, "mux video+system",
                wav => FfmpegArgs.FilterSystemToWav(sysWav, wav, sysGain));

        /// <summary>Video with a mic-only track -> final MP4 with the mic run through the clean-voice chain.</summary>
        public static void ProcessVideoMic(string rawMp4, string outMp4, AudioMixOptions o)
        {
            Prepare(o, rawMp4);
            MuxInTwoPasses(rawMp4, outMp4, "process mic audio",
                wav => FfmpegArgs.FilterMicToWav(rawMp4, wav, o));
        }

        /// <summary>
        /// The intermediate processed-audio file pass 1 writes and pass 2 reads, in the recording's
        /// own directory beside the capture it belongs to - the same place sys_native.wav lives, and
        /// on the same volume as the output, so a 10-minute recording's intermediate cannot land on a
        /// smaller temp disk. Deleted when the mux ends, however it ends.
        /// </summary>
        internal const string ProcessedAudioFileName = "audio_processed.wav";

        /// <summary>
        /// Issue #136: run a video mux as TWO ffmpeg passes instead of one - the audio filter graph
        /// alone to an intermediate WAV, then the video copy plus that finished WAV with no filter
        /// graph at all.
        ///
        /// WHY. A single pass pairing "-c:v copy" with a filtered audio stream DEADLOCKS on macOS:
        /// the copied video is read and queued almost instantly while the mic is still being
        /// filtered, ffmpeg's muxing queue hits its default bound, and the process spins at ~99% CPU
        /// past the end of the content without ever finalizing, so the recording is never produced.
        /// Measured on a 76-second recording (issue #136). Splitting the command removes the race
        /// rather than widening the queue it loses, so there is no tuned number that stops being big
        /// enough on a longer recording. <see cref="FfmpegArgs"/> carries the measurement matrix.
        ///
        /// Both passes run under the issue #128 stall watchdog and partial-output cleanup
        /// (<see cref="RunStage"/>), so a pass that genuinely hangs is still killed and failed, and
        /// neither pass can leave a fragment behind that a later stage would mistake for the
        /// finished recording.
        /// </summary>
        /// <param name="rawMp4">The capture whose VIDEO stream is copied into the output.</param>
        /// <param name="outMp4">The final recording. Must be in a directory (it is where the
        /// intermediate goes).</param>
        /// <param name="label">The stage name for the log and for any failure message.</param>
        /// <param name="audioPass">Given the intermediate WAV path, the pass-1 argument list that
        /// writes the finished audio to it.</param>
        internal static void MuxInTwoPasses(
            string rawMp4, string outMp4, string label,
            System.Func<string, System.Collections.Generic.IReadOnlyList<string>> audioPass)
        {
            string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outMp4));
            if (string.IsNullOrEmpty(dir))
                throw new UsageException(
                    $"internal error: the mux output '{outMp4}' has no directory to write the " +
                    "intermediate processed audio into");

            string processed = System.IO.Path.Combine(dir, ProcessedAudioFileName);
            Log.Info($"[AudioMix] MuxInTwoPasses: {label} video={rawMp4} intermediate={processed} out={outMp4}");
            bool ok = false;
            try
            {
                RunStage(audioPass(processed), processed, label + " (audio)");
                RunStage(FfmpegArgs.MuxVideoWithProcessedAudio(rawMp4, processed, outMp4), outMp4, label);
                ok = true;
                Log.Info($"[AudioMix] MuxInTwoPasses: {label} done out={outMp4}");
            }
            finally
            {
                // However this ended: the intermediate is scratch and must not be left in the
                // recording directory, where every file-on-disk scan would count it as capture bytes.
                if (System.IO.File.Exists(processed))
                {
                    System.IO.File.Delete(processed);
                    Log.Info($"[AudioMix] MuxInTwoPasses: removed intermediate {processed}");
                }

                // And the issue #128 rule holds for the mux AS A WHOLE, not just for the pass that
                // happened to fail. Splitting the command nearly broke it: a failure in the AUDIO
                // pass cleans up the intermediate, not the final path, so a fragment already sitting
                // at the final path from an earlier killed attempt would have survived - and issue
                // #128 measured exactly that fragment being taken for the finished recording by the
                // thumbnail stage ("moov atom not found"). There is no valid final file unless both
                // passes succeeded, so if they did not, there must be no file there.
                if (!ok && System.IO.File.Exists(outMp4))
                {
                    System.IO.File.Delete(outMp4);
                    Log.Info($"[AudioMix] MuxInTwoPasses: {label} did not complete - removed {outMp4}");
                }
            }
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
