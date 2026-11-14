using System;
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
        /// Where pass 2 writes, so that the FINAL path is only ever created by the atomic rename at
        /// the end (issue #136, the process-death case). Beside the output and on its volume, so the
        /// rename is a rename and not a copy.
        /// </summary>
        internal static string MuxingTempPath(string outMp4) => outMp4 + ".muxing.tmp";

        /// <summary>
        /// Issue #136: run a video mux as TWO ffmpeg passes instead of one - the audio filter graph
        /// alone to an intermediate WAV, then the video copy plus that finished WAV with no filter
        /// graph at all - and publish the result to the final path with a single atomic rename.
        ///
        /// WHY TWO PASSES. A single pass pairing "-c:v copy" with a filtered audio stream DEADLOCKS
        /// on macOS: the copied video is read and queued almost instantly while the mic is still
        /// being filtered, ffmpeg's muxing queue hits its default bound, and the process spins at
        /// ~99% CPU past the end of the content without ever finalizing, so the recording is never
        /// produced. Measured on a 76-second recording (issue #136). Splitting the command removes
        /// the race rather than widening the queue it loses, so there is no tuned number that stops
        /// being big enough on a longer recording. <see cref="FfmpegArgs"/> carries the matrix.
        ///
        /// WHY THE RENAME, which is a separate point and a measured one. ffmpeg writes an MP4
        /// incrementally and puts the moov atom in LAST, so a process death mid-mux leaves a
        /// moov-less file wherever it was writing. Issue #128's QA measured exactly that at the FINAL
        /// path, where later stages test for the file's existence and take the fragment for the
        /// finished recording (the thumbnail stage then fails on "moov atom not found"), and
        /// <see cref="RunStage"/>'s cleanup cannot help because a killed process runs no finally.
        /// So pass 2 writes <see cref="MuxingTempPath"/> and the final path is created by one
        /// File.Move. A death at any point now leaves the final path either absent or holding a
        /// complete file - never a fragment - because a rename on one volume is atomic. Measured by
        /// killing the host mid-pass-2: before this, 35,913,776 bytes of moov-less mp4 at the final
        /// path; after, nothing there and the fragment at the temp.
        ///
        /// Both passes still run under issue #128's stall watchdog and partial-output cleanup
        /// (<see cref="RunStage"/>), which is unchanged and which this does not replace: the
        /// watchdog is what fails a pass that HANGS, and the rename is what keeps a pass that DIES
        /// from leaving anything behind that looks finished.
        /// </summary>
        /// <param name="rawMp4">The capture whose VIDEO stream is copied into the output.</param>
        /// <param name="outMp4">The final recording. Must be in a directory (it is where the
        /// intermediate and the muxing temp go).</param>
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
            string muxing = MuxingTempPath(outMp4);
            Log.Info($"[AudioMix] MuxInTwoPasses: {label} video={rawMp4} intermediate={processed} " +
                     $"muxing={muxing} out={outMp4}");

            // A temp sitting here now means an earlier attempt DIED here - a process death runs no
            // finally, so nothing cleared it. This says so, at WARN, before overwriting it.
            //
            // Be clear about what this is and is not: it is the DIAGNOSTIC, not the guarantee. The
            // finally below is what actually removes both temps, on every path, and ffmpeg's "-y"
            // would have overwritten them anyway - so disabling these two lines breaks no test and
            // leaks no file. What it would lose is the only signal that distinguishes "a previous
            // mux was killed here" from routine cleanup, on a machine where that is exactly the
            // question someone will be asking.
            RemoveStaleTemp(processed, label);
            RemoveStaleTemp(muxing, label);

            bool ok = false;
            try
            {
                RunStage(audioPass(processed), processed, label + " (audio)");
                RunStage(FfmpegArgs.MuxVideoWithProcessedAudio(rawMp4, processed, muxing), muxing, label);

                // The publish. Overwrite: a retry of a deferred mux legitimately replaces whatever a
                // previous attempt left, and PendingMux being set is what says this file is still
                // being produced.
                System.IO.File.Move(muxing, outMp4, overwrite: true);
                ok = true;
                Log.Info($"[AudioMix] MuxInTwoPasses: {label} done out={outMp4}");
            }
            finally
            {
                // However this ended: the temps are scratch and must not be left in the recording
                // directory, where every file-on-disk scan would count them as capture bytes.
                if (System.IO.File.Exists(processed))
                {
                    System.IO.File.Delete(processed);
                    Log.Info($"[AudioMix] MuxInTwoPasses: removed intermediate {processed}");
                }
                if (System.IO.File.Exists(muxing))
                {
                    System.IO.File.Delete(muxing);
                    Log.Info($"[AudioMix] MuxInTwoPasses: removed muxing temp {muxing}");
                }

                // Issue #128's rule, kept for the mux AS A WHOLE and not only for the pass that
                // failed. With the rename above, THIS build can no longer write a fragment to the
                // final path at all - but a fragment left there by an older binary still can be, and
                // a failed mux must not leave one for the thumbnail stage to find.
                if (!ok && System.IO.File.Exists(outMp4))
                {
                    System.IO.File.Delete(outMp4);
                    Log.Info($"[AudioMix] MuxInTwoPasses: {label} did not complete - removed {outMp4}");
                }
            }
        }

        /// <summary>
        /// Remove a temp a killed attempt left behind, saying so at WARN. This exists for the
        /// message, not for the deletion: <see cref="MuxInTwoPasses"/>'s finally removes both temps
        /// on every path regardless. Silently overwriting one would hide that a mux died here.
        /// </summary>
        private static void RemoveStaleTemp(string path, string label)
        {
            if (!System.IO.File.Exists(path)) return;
            long bytes = new System.IO.FileInfo(path).Length;
            System.IO.File.Delete(path);
            Log.Warn($"[AudioMix] MuxInTwoPasses: {label} removed a stale temp from an attempt that " +
                     $"did not finish: {path} ({bytes} bytes)");
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
            try
            {
                Ffmpeg.RunWatched(args, label, Ffmpeg.CurrentPostProcessStallTimeout);
            }
            catch (Exception stageError)
            {
                RemovePartialOutput(outPath, label, stageError);
                throw;
            }
        }

        /// <summary>
        /// Issue #128 round two (D5): remove a failed pass's partial output WITHOUT letting a cleanup
        /// failure replace the reason the pass failed. Measured: a File.Delete that threw an
        /// IOException in a finally block discarded the stall error, and the manifest recorded the
        /// IOException instead. When the delete fails, the stage's own error stays first in the
        /// message and the cleanup failure is appended to it - neither is hidden. The fragment that
        /// stays behind is removed by <see cref="MuxCoordinator.Reconcile"/> before the next attempt.
        /// </summary>
        private static void RemovePartialOutput(string outPath, string label, Exception stageError)
        {
            if (!System.IO.File.Exists(outPath)) return;
            try
            {
                System.IO.File.Delete(outPath);
            }
            catch (Exception cleanupError) when (cleanupError is System.IO.IOException or UnauthorizedAccessException)
            {
                string msg = $"{stageError.Message} (and its partial output {outPath} could not be removed: {cleanupError.Message})";
                Log.Error($"[AudioMix] RunStage: {label} FAILED and its partial output could not be removed: {cleanupError.Message}");
                throw new UsageException(msg, stageError);
            }
            Log.Info($"[AudioMix] RunStage: {label} did not complete - removed partial output {outPath}");
        }

        /// <summary>
        /// Everything the mic chain needs before it can be built: the RNNoise model materialized on
        /// disk, and the gate threshold measured from the capture that is about to be processed.
        /// </summary>
        private static void Prepare(AudioMixOptions o, string micSource)
        {
            if (o.NoiseSuppression && string.IsNullOrWhiteSpace(o.RnnoiseModelPath))
                o.RnnoiseModelPath = RnnoiseModel.Ensure();

            // Issue #128 round two (D7): the sanitizer is pinned to the mic's own layout, so measure it.
            o.MicChannels = (o.NoiseSuppression || o.VoiceLeveling) ? MediaProbe.AudioChannels(micSource) : null;

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
