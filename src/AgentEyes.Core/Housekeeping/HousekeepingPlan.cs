using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentEyes.Housekeeping
{
    /// <summary>What one housekeeping step does to one file (issue #56).</summary>
    internal enum HousekeepingKind
    {
        /// <summary>Delete a file that is fully derived from one the recording keeps.</summary>
        DeleteDerived,

        /// <summary>Rewrite a log so only its tail remains.</summary>
        TruncateLog,

        /// <summary>Re-encode a preserved audio original into a smaller container.</summary>
        TranscodePreservedAudio,

        /// <summary>Delete a preserved original whose comparison window has passed.</summary>
        DeletePreservedOriginal,

        /// <summary>Re-encode a recording's extracted walkthrough frames from PNG to JPEG.</summary>
        ConvertFramesToJpeg,

        /// <summary>
        /// Delete a composition input (camera.mp4, recording.screen.mp4) whose composed keeper exists
        /// and whose window has passed.</summary>
        DeleteCompositionInput,

        /// <summary>
        /// Expire the recording: delete the composed video, the composition inputs and the extracted
        /// frames, and rewrite the walkthrough so nothing points at what is gone. The text survives.</summary>
        ExpireRecording,
    }

    /// <summary>
    /// One planned action. Every field is data - a step knows nothing about the file system, which is
    /// what lets the whole planner be tested without ffmpeg and without real files.
    /// </summary>
    internal sealed class HousekeepingStep
    {
        public HousekeepingKind Kind { get; init; }

        /// <summary>The file this acts on, relative to the recording directory.</summary>
        public string File { get; init; } = "";

        /// <summary>For a transcode, the file it produces (relative). Null otherwise.</summary>
        public string? Output { get; init; }

        /// <summary>What the file occupies on disk now.</summary>
        public long Bytes { get; init; }

        /// <summary>For a truncate, how many bytes survive. 0 otherwise.</summary>
        public long KeepBytes { get; init; }

        /// <summary>Why this step is safe, in plain words, for the report and the log.</summary>
        public string Reason { get; init; } = "";

        public override string ToString() => $"{Kind} {File}"
            + (Output is null ? "" : $" -> {Output}")
            + $" ({Bytes:N0} bytes)";
    }

    /// <summary>
    /// The decision layer of the Housekeeper: given ONE recording's manifest, the files actually on
    /// disk, its age, and the settings, say what should happen to it (issues #55, #56).
    ///
    /// IT IS PURE ON PURPOSE. A destructive sweep is exactly the kind of code that must be provable
    /// without being run, and the only way to prove one is to separate DECIDING from DOING. Every
    /// safety rule in #55 lives here as a positive condition on a named file; <see cref="Housekeeper"/>
    /// executes the result and never decides anything of its own.
    ///
    /// The rule it follows: enumerate what to DELETE, never what to skip. A step is emitted only when
    /// the manifest names the file, or the file is on a fixed allowlist of things this recording can
    /// positively recreate. Anything the planner has not been taught about is left alone BY
    /// CONSTRUCTION, because no code path here can emit a step for it.
    /// </summary>
    internal static class HousekeepingPlan
    {
        /// <summary>
        /// The derived intermediates a recording can recreate from files it keeps. Exact names, not
        /// patterns - a pattern is how an allowlist grows to cover something nobody checked.
        ///
        /// audio_16k.wav is the transcriber's input. It appears in neither Files nor OriginalFiles,
        /// and Package.ResolveAudioWav extracts it again from recording.mp4 in seconds. It was
        /// 2.80 GiB across one machine's 33 recordings.
        /// </summary>
        public static readonly string[] DerivedFiles = { "audio_16k.wav" };

        /// <summary>
        /// The composition inputs: the raw camera and screen tracks that CameraCompose built the
        /// composed keeper from. Exact names for the same reason as <see cref="DerivedFiles"/> - a
        /// name is what proves where a file came from and what can recreate it.
        ///
        /// They are kept for a window (not deleted at composition time) because a re-frame - a
        /// different corner, a circle instead of a rectangle - needs the raw rectangular camera track
        /// (issue #36). After the window they are deleted, and ONLY while the composed keeper exists:
        /// for a recording whose composition never finished they are not inputs, they are the
        /// recording.
        /// </summary>
        public static readonly string[] CompositionInputFiles = { "camera.mp4", "recording.screen.mp4" };

        /// <summary>The subdirectory holding a recording's shots, relative and with forward slashes.</summary>
        public const string FramesDirectory = "shots";

        /// <summary>
        /// True for a frame EXTRACTED from the video by packaging - the only images Tier 2 may touch.
        ///
        /// The name is the whole test, because the name is what says where the file came from.
        /// Package writes these as shots/frame_%03d.png; a shot the owner took by hand during a
        /// recording is shots/region_WxH.png or shots/monitorN_full.png and can never be recreated
        /// from anything.
        /// </summary>
        public static bool IsExtractedFrame(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            string p = relativePath.Replace('\\', '/');
            if (!p.StartsWith(FramesDirectory + "/", StringComparison.OrdinalIgnoreCase)) return false;
            string name = p.Substring(FramesDirectory.Length + 1);
            return name.StartsWith("frame_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for a file that WAS an extracted frame - the PNG packaging wrote or the JPEG Tier 2
        /// converted it into. This is the enumeration the expire tier (issue #59) deletes; Tier 2
        /// keeps using <see cref="IsExtractedFrame"/> because it converts PNGs only, and counting
        /// JPEGs there would plan a repair pass every day forever. Hand-taken shots are excluded by
        /// the same name rule.
        /// </summary>
        public static bool IsExtractedFrameFile(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            string p = relativePath.Replace('\\', '/');
            if (!p.StartsWith(FramesDirectory + "/", StringComparison.OrdinalIgnoreCase)) return false;
            string name = p.Substring(FramesDirectory.Length + 1);
            return name.StartsWith("frame_", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The suffix of an ffmpeg log. These are progress output - one real recording's was 3.6 MB -
        /// and only the tail has ever been read, because that is where a failure is.
        /// </summary>
        public const string LogSuffix = ".ffmpeg.log";

        /// <summary>
        /// Plan one recording.
        /// </summary>
        /// <param name="manifest">Its manifest, already loaded.</param>
        /// <param name="filesOnDisk">Relative file name to size in bytes, for that directory.</param>
        /// <param name="ageDays">How old the recording is. The caller decides this, because the
        /// footprint ceiling can legitimately age a recording forward (see #55).</param>
        /// <param name="settings">What the pass is allowed to do.</param>
        /// <param name="skipReason">Set when the whole recording is exempt, for the report.</param>
        public static IReadOnlyList<HousekeepingStep> For(
            Manifest manifest,
            IReadOnlyDictionary<string, long> filesOnDisk,
            int ageDays,
            HousekeepingSettings settings,
            out string? skipReason)
        {
            var steps = new List<HousekeepingStep>();
            skipReason = null;

            if (!settings.Enabled)
            {
                skipReason = "housekeeping is turned off";
                return steps;
            }

            // The pin. A recording the owner is still working from is exempt from every tier, with no
            // exception and no partial treatment - #55. It is checked FIRST so nothing below can reach
            // a pinned recording however the tiers are later extended.
            if (manifest.Keep)
            {
                skipReason = "pinned: manifest Keep is true";
                return steps;
            }

            // Whether packaging has FINISHED, decided from the artifact and not from the journal.
            // PostProcessing is documented as a journal rather than the authority (issues #152, #155),
            // and the artifact that proves transcription happened is the transcript itself - which is
            // precisely the thing that consumed audio_16k.wav. So this is both the honest test and the
            // directly relevant one.
            bool transcribed = !string.IsNullOrEmpty(manifest.Transcript)
                               && filesOnDisk.ContainsKey(manifest.Transcript!);

            // A deferred mux (issue #77) means the raw capture files are still the durable artifact.
            // Nothing is touched until it has been completed.
            bool muxPending = manifest.PendingMux is not null;

            // The composed keeper video, on disk. The tail of the ladder and the composition-input
            // tier both key off it: the first cannot expire a recording whose video is already gone,
            // and the second must not delete raw tracks that ARE the recording because composition
            // never produced anything from them.
            string? keeper = manifest.VideoFile;
            bool keeperOnDisk = keeper is not null && filesOnDisk.ContainsKey(keeper);

            // ---- Tier 0: derived intermediates -------------------------------------------------
            if (transcribed && !muxPending)
            {
                foreach (string name in DerivedFiles)
                {
                    if (!filesOnDisk.TryGetValue(name, out long size)) continue;

                    // Against a future manifest that starts naming one of these as a kept file: if the
                    // recording claims it, it is not derived and it stays.
                    if (manifest.Files.Contains(name) || manifest.OriginalFiles.Contains(name)) continue;

                    steps.Add(new HousekeepingStep
                    {
                        Kind = HousekeepingKind.DeleteDerived,
                        File = name,
                        Bytes = size,
                        Reason = "the transcriber's input; the transcript exists and Package re-extracts it on demand",
                    });
                }
            }

            foreach (var entry in filesOnDisk.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!entry.Key.EndsWith(LogSuffix, StringComparison.OrdinalIgnoreCase)) continue;

                // TWICE the tail, not merely more than it, and that is the difference between a pass
                // that settles and one that never stops. A truncated log ends up at the tail size plus
                // a short banner saying it was truncated - which is still "larger than the tail", so a
                // condition of "greater than LogTailBytes" re-truncated the same log on every tick,
                // shaving the banner's few bytes off an always-on app's disk four times an hour
                // forever. Requiring double the tail makes one truncation the end of the matter by
                // construction, and guarantees that any truncation this pass does reclaims at least
                // LogTailBytes rather than a rounding error.
                if (entry.Value < settings.LogTailBytes * 2) continue;

                steps.Add(new HousekeepingStep
                {
                    Kind = HousekeepingKind.TruncateLog,
                    File = entry.Key,
                    Bytes = entry.Value,
                    KeepBytes = settings.LogTailBytes,
                    Reason = "ffmpeg progress output; only the tail has diagnostic value",
                });
            }

            // ---- Tier 2: the extracted walkthrough frames --------------------------------------
            // ONE step for the whole set, not one per frame, and that is a deliberate choice. A
            // recording holds up to 2,775 of these; a step each would mean 2,775 lines in the report
            // and 2,775 entries appended to manifest.json, which would make the record of what
            // housekeeping did larger than the transcript it is protecting.
            //
            // Only frame_* files. A shot the owner took by hand during a recording - region_*.png,
            // monitor*_full.png - is not a derived artifact, is not regenerable from the video, and is
            // never touched.
            //
            // A recording that is being EXPIRED this pass (below) is not also converted: its frames
            // are about to be deleted, and re-encoding them first would be work done to throw away.
            // The same suppression applies to a PENDING expiry: a resumed pass takes its doomed set
            // from the written record, and a conversion running beside it would RENAME a frame out
            // from under that record - the PNG the record names would become an unreachable JPEG.
            bool expiring = settings.KeepVideoDays > 0 && ageDays >= settings.KeepVideoDays && keeperOnDisk;
            bool pendingExpiry = manifest.PendingExpiry is { Count: > 0 };

            if (settings.ConvertFramesToJpeg && settings.FrameDays > 0 && ageDays >= settings.FrameDays
                && !expiring && !pendingExpiry)
            {
                long frameBytes = 0;
                int frameCount = 0;
                foreach (var entry in filesOnDisk)
                {
                    if (!IsExtractedFrame(entry.Key)) continue;
                    frameBytes += entry.Value;
                    frameCount++;
                }

                if (frameCount > 0)
                {
                    steps.Add(new HousekeepingStep
                    {
                        Kind = HousekeepingKind.ConvertFramesToJpeg,
                        File = FramesDirectory,
                        Bytes = frameBytes,
                        Reason = $"{frameCount} extracted frame(s) as PNG; they are re-derivable from "
                               + "the recording, and JPEG at quality "
                               + $"{settings.FrameJpegQuality} keeps screen text readable",
                    });
                }
            }

            // ---- Tier 1 and 3: the preserved originals ----------------------------------------
            // These are the files issue #83 deliberately kept instead of deleting, so a cleaned take
            // can be compared against its raw one. Tier 3 is the end of that window; Tier 1 makes the
            // window cheap. A file that has reached Tier 3 is NEVER also transcoded - it is about to
            // go, and encoding it first would be work done to throw away.
            bool windowPassed = ageDays >= settings.PreservedOriginalDays;

            foreach (string original in manifest.OriginalFiles)
            {
                if (!filesOnDisk.TryGetValue(original, out long size)) continue;

                if (windowPassed)
                {
                    steps.Add(new HousekeepingStep
                    {
                        Kind = HousekeepingKind.DeletePreservedOriginal,
                        File = original,
                        Bytes = size,
                        Reason = $"preserved original, {ageDays} days old; "
                               + $"the comparison window is {settings.PreservedOriginalDays} days",
                    });
                    continue;
                }

                if (!settings.TranscodePreservedAudio) continue;
                if (!transcribed || muxPending) continue;
                if (!original.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;

                string output = original.Substring(0, original.Length - 4) + settings.PreservedAudioExtension;

                steps.Add(new HousekeepingStep
                {
                    Kind = HousekeepingKind.TranscodePreservedAudio,
                    File = original,
                    Output = output,
                    Bytes = size,
                    Reason = settings.PreservedAudioMustBeBitExact
                        ? $"preserved audio as uncompressed PCM; {settings.PreservedAudioCodec} holds it bit for bit"
                        : $"preserved audio as uncompressed PCM; {settings.PreservedAudioCodec} is smaller and NOT bit-exact",
                });
            }

            // ---- The composition inputs (issue #59) --------------------------------------------
            // The raw tracks the keeper was composed from. Deleted at the same window as the
            // preserved originals - a re-frame someone wanted has had a week to happen - and only
            // while the composed keeper exists, because without it these files are not inputs, they
            // are the recording. A file that IS the keeper is never an input to itself.
            // A recording being EXPIRED this pass does not also get input steps: the expire step
            // names the inputs in its own cost and deletes them itself, so separate steps would
            // double-count the same bytes in the report and the record.
            if (windowPassed && keeperOnDisk && !muxPending && !expiring && !pendingExpiry)
            {
                foreach (string input in CompositionInputFiles)
                {
                    if (string.Equals(input, keeper, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!filesOnDisk.TryGetValue(input, out long size)) continue;

                    steps.Add(new HousekeepingStep
                    {
                        Kind = HousekeepingKind.DeleteCompositionInput,
                        File = input,
                        Bytes = size,
                        Reason = $"a composition input the composed recording was built from; "
                               + $"a re-frame has had {settings.PreservedOriginalDays} days to happen",
                    });
                }
            }

            // ---- The tail of the ladder: the keeper expires (issue #59) -----------------------
            // One step for the whole recording, planned FIRST in nothing but the order it runs: the
            // page is rewritten and the manifest repointed before anything is deleted, inside the
            // Apply. What goes: the composed video, the composition inputs, and the extracted frames.
            // What stays: the transcript, the walkthrough text, the manifest, the thumbnail, and the
            // owner's own hand-taken shots, which no video can regenerate. ONE-WAY by design: after
            // this the frames cannot be re-extracted either, because the video that held them is the
            // thing that was deleted.
            if ((expiring || pendingExpiry) && !muxPending)
            {
                long doomed = 0;
                string reason;

                if (pendingExpiry)
                {
                    // An earlier pass wrote the doomed names into the manifest and died before the
                    // deletes finished. This pass completes it from the written record - without
                    // that record the video would sit on disk forever, named by nothing.
                    foreach (string name in manifest.PendingExpiry!)
                    {
                        if (filesOnDisk.TryGetValue(name, out long size)) doomed += size;
                    }
                    reason = "completing an expiry an earlier pass left half done (PendingExpiry)";
                }
                else
                {
                    if (filesOnDisk.TryGetValue(keeper!, out long keeperSize)) doomed += keeperSize;
                    foreach (string input in CompositionInputFiles)
                    {
                        if (filesOnDisk.TryGetValue(input, out long size)
                            && !string.Equals(input, keeper, StringComparison.OrdinalIgnoreCase)) doomed += size;
                    }
                    int frameCount = 0;
                    foreach (var entry in filesOnDisk)
                    {
                        if (!IsExtractedFrameFile(entry.Key)) continue;
                        doomed += entry.Value;
                        frameCount++;
                    }
                    reason = $"{ageDays} days old; the recording expires to its source of truth "
                           + $"(transcript, walkthrough, manifest) after {settings.KeepVideoDays} days"
                           + (frameCount > 0 ? $"; {frameCount} extracted frame(s) go with it" : "");
                }

                steps.Add(new HousekeepingStep
                {
                    Kind = HousekeepingKind.ExpireRecording,
                    File = keeper ?? "",
                    Bytes = doomed,
                    Reason = reason,
                });
            }

            return steps;
        }
    }
}
