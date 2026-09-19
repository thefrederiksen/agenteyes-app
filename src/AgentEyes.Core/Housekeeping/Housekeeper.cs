using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AgentEyes.Housekeeping
{
    /// <summary>
    /// The doing half of the Housekeeper (issues #55, #56): walk the recordings root, ask
    /// <see cref="HousekeepingPlan"/> what each recording needs, carry it out, and write down what
    /// happened.
    ///
    /// It decides NOTHING. Every condition that could spare or condemn a file lives in the planner,
    /// which is pure and tested without a disk; this class turns a list of steps into file operations
    /// and a report. Keeping the two apart is what makes a destructive pass reviewable: you can read
    /// the whole policy in one file that cannot delete anything.
    ///
    /// It is called from <see cref="RepairService"/> and inherits that pump's guards rather than
    /// bringing its own - the 15-minute timer, the refusal to start while a recording is in progress,
    /// and the capture-epoch yield (issues #142, #154). Capture always wins: the yield is re-tested
    /// before EVERY step, not once per recording, because a single transcode of a multi-gigabyte WAV
    /// lasts long enough for a whole recording to start and finish inside it.
    /// </summary>
    internal static class Housekeeper
    {
        /// <summary>
        /// Run one pass.
        /// </summary>
        /// <param name="root">The recordings root.</param>
        /// <param name="settings">What the pass is allowed to do.</param>
        /// <param name="trigger">Why it ran, for the report and the log.</param>
        /// <param name="shouldYield">Asked before every step; true stops the pass. This is how the
        /// capture-epoch guard reaches in without this class knowing anything about capture.</param>
        /// <param name="nowUtc">The clock, injectable so age boundaries are testable.</param>
        public static HousekeepingReport Run(
            string root,
            HousekeepingSettings settings,
            string trigger,
            Func<bool> shouldYield,
            DateTime nowUtc)
        {
            var report = new HousekeepingReport
            {
                StartedUtc = nowUtc,
                Trigger = trigger,
                ReportOnly = settings.ReportOnly,
            };

            if (!settings.Enabled)
            {
                report.NotRun = "housekeeping is turned off in settings";
                report.FinishedUtc = DateTime.UtcNow;
                Log.Info($"[Housekeeper] Run: trigger={trigger} - {report.NotRun}");
                return report;
            }

            if (!Directory.Exists(root))
            {
                report.NotRun = $"the recordings root does not exist: {root}";
                report.FinishedUtc = DateTime.UtcNow;
                Log.Warn($"[Housekeeper] Run: trigger={trigger} - {report.NotRun}");
                return report;
            }

            Log.Info($"[Housekeeper] Run: trigger={trigger}, reportOnly={settings.ReportOnly}, root={root}");

            // Load every recording once: its manifest, its files, its age. Both the ceiling and the
            // planner need all three, and reading the tree twice would let it change underneath.
            var loaded = new List<LoadedRecording>();
            foreach (string dir in Directory.GetDirectories(root))
            {
                var one = Load(dir, nowUtc);
                if (one is not null) loaded.Add(one);
            }

            var advanced = HousekeepingCeiling.Advance(
                loaded.Select(l => new HousekeepingCandidate
                {
                    Recording = l.Name,
                    Bytes = l.Files.Values.Sum(),
                    AgeDays = l.AgeDays,
                    Pinned = l.Manifest.Keep,
                }).ToList(),
                settings.CeilingBytes,
                settings.PreservedOriginalDays);

            if (advanced.Count > 0)
            {
                Log.Info($"[Housekeeper] Run: the footprint ceiling advanced {advanced.Count} recording(s) "
                       + $"to the {settings.PreservedOriginalDays}-day boundary");
            }

            foreach (var one in loaded)
            {
                if (shouldYield())
                {
                    report.YieldedToCapture = true;
                    Log.Info($"[Housekeeper] Run: trigger={trigger} - yielding to a capture; report is partial");
                    break;
                }

                report.RecordingsExamined++;

                int age = advanced.Contains(one.Name) ? settings.PreservedOriginalDays : one.AgeDays;

                var steps = HousekeepingPlan.For(one.Manifest, one.Files, age, settings, out string? skip);

                var recordingReport = new HousekeepingRecordingReport
                {
                    Recording = one.Name,
                    AgeDays = one.AgeDays,
                    Skipped = skip,
                };

                foreach (var step in steps)
                {
                    if (shouldYield())
                    {
                        report.YieldedToCapture = true;
                        Log.Info($"[Housekeeper] Run: yielding to a capture mid-recording {one.Name}; "
                               + "report is partial");
                        break;
                    }

                    recordingReport.Steps.Add(settings.ReportOnly
                        ? Planned(step)
                        : Apply(one.Path, step, settings));
                }

                if (recordingReport.Steps.Count > 0 || skip is not null) report.Recordings.Add(recordingReport);
                if (report.YieldedToCapture) break;
            }

            report.FinishedUtc = DateTime.UtcNow;
            Log.Info($"[Housekeeper] Run: trigger={trigger} done - {report.Summary()}");
            return report;
        }

        /// <summary>A step in a report-only pass: described, costed, and not carried out.</summary>
        private static HousekeepingStepReport Planned(HousekeepingStep step) => new()
        {
            Kind = step.Kind.ToString(),
            File = step.File,
            Output = step.Output,
            Bytes = step.Bytes,
            // A transcode's saving cannot be known without doing it, so a report-only pass does not
            // claim one. Counting an estimate here would put a number in a report that the real pass
            // then contradicts, and the report exists to be trusted.
            BytesReclaimed = step.Kind switch
            {
                HousekeepingKind.DeleteDerived => step.Bytes,
                HousekeepingKind.DeletePreservedOriginal => step.Bytes,
                HousekeepingKind.TruncateLog => step.Bytes - step.KeepBytes,
                _ => 0,
            },
            Reason = step.Reason,
            Outcome = "planned",
        };

        /// <summary>Carry one step out. Every failure is caught and recorded; none is fatal to the pass.</summary>
        private static HousekeepingStepReport Apply(string dir, HousekeepingStep step, HousekeepingSettings settings)
        {
            var result = new HousekeepingStepReport
            {
                Kind = step.Kind.ToString(),
                File = step.File,
                Output = step.Output,
                Bytes = step.Bytes,
                Reason = step.Reason,
            };

            try
            {
                switch (step.Kind)
                {
                    case HousekeepingKind.DeleteDerived:
                        File.Delete(Path.Combine(dir, step.File));
                        result.Outcome = "done";
                        result.BytesReclaimed = step.Bytes;
                        break;

                    case HousekeepingKind.TruncateLog:
                        result.BytesReclaimed = TruncateToTail(Path.Combine(dir, step.File), step.KeepBytes);
                        result.Outcome = "done";
                        break;

                    case HousekeepingKind.DeletePreservedOriginal:
                        // The manifest stops naming the file BEFORE the file leaves the disk. A crash
                        // between the two leaves an unreferenced file on disk - inert, wasted bytes
                        // the next pass can see - never a manifest naming a file that is gone.
                        ManifestStore.Update(dir, m =>
                        {
                            m.OriginalFiles.RemoveAll(f => string.Equals(f, step.File, StringComparison.OrdinalIgnoreCase));
                            m.Files.RemoveAll(f => string.Equals(f, step.File, StringComparison.OrdinalIgnoreCase));
                        });
                        File.Delete(Path.Combine(dir, step.File));
                        result.Outcome = "done";
                        result.BytesReclaimed = step.Bytes;
                        break;

                    case HousekeepingKind.DeleteCompositionInput:
                        // The same law: the manifest stops naming the input, then the input leaves the
                        // disk. An interrupted run leaves an unreferenced file, never a dead name.
                        ManifestStore.Update(dir, m =>
                        {
                            if (string.Equals(m.CameraFile, step.File, StringComparison.OrdinalIgnoreCase)) m.CameraFile = null;
                            m.Files.RemoveAll(f => string.Equals(f, step.File, StringComparison.OrdinalIgnoreCase));
                        });
                        File.Delete(Path.Combine(dir, step.File));
                        result.Outcome = "done";
                        result.BytesReclaimed = step.Bytes;
                        break;

                    case HousekeepingKind.ExpireRecording:
                        ApplyExpiry(dir, step, result);
                        break;

                    case HousekeepingKind.TranscodePreservedAudio:
                        ApplyTranscode(dir, step, settings, result);
                        break;

                    case HousekeepingKind.ConvertFramesToJpeg:
                        ApplyFrameConversion(dir, settings, result);
                        break;

                    default:
                        result.Outcome = "refused";
                        result.Error = $"unknown step kind {step.Kind}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Outcome = "failed";
                result.Error = ex.Message;
                Log.Error($"[Housekeeper] {step.Kind} {step.File} in {dir} FAILED", ex);
            }

            Record(dir, step, result);
            return result;
        }

        /// <summary>
        /// The one step that can refuse itself. The source is deleted only after the output has been
        /// verified against it; anything else leaves both files in place and says why.
        /// </summary>
        private static void ApplyTranscode(
            string dir, HousekeepingStep step, HousekeepingSettings settings, HousekeepingStepReport result)
        {
            string source = Path.Combine(dir, step.File);
            string output = Path.Combine(dir, step.Output!);

            var transcode = PreservedAudioTranscode.Run(
                source, output,
                settings.PreservedAudioCodec,
                settings.PreservedAudioCompressionLevel,
                settings.PreservedAudioMustBeBitExact);

            result.BitExact = transcode.BitExact;
            result.SourceHash = transcode.SourceHash;
            result.OutputHash = transcode.OutputHash;

            if (!transcode.Verified)
            {
                result.Outcome = "refused";
                result.Error = transcode.Error;
                result.BytesReclaimed = 0;
                Log.Warn($"[Housekeeper] refusing to replace {step.File} in {dir}: {transcode.Error}");
                return;
            }

            // Repoint BEFORE deleting, the same law FrameConversion states for itself after the defect
            // that left 268 references pointing at deleted files. A crash between these two leaves the
            // manifest naming the verified output (which exists) beside an orphaned WAV on disk - wasted
            // bytes, inert - never a manifest naming a file that is gone.
            ManifestStore.Update(dir, m =>
            {
                Swap(m.OriginalFiles, step.File, step.Output!);
                Swap(m.Files, step.File, step.Output!);
            });

            File.Delete(source);

            result.Outcome = "done";
            result.BytesReclaimed = transcode.SourceBytes - transcode.OutputBytes;
            Log.Info($"[Housekeeper] {step.File} -> {step.Output} in {dir}: "
                   + $"{transcode.SourceBytes:N0} -> {transcode.OutputBytes:N0} bytes, "
                   + $"bitExact={transcode.BitExact}");
        }

        /// <summary>
        /// Convert one recording's frames. Reported as ONE action over the whole set, with the count in
        /// the reason, because a recording can hold 2,775 of them.
        ///
        /// A partial result is reported as "done" with what it actually reclaimed and how many frames
        /// refused to convert. It is not a failure: every frame that failed still has its PNG, so the
        /// walkthrough is intact and the next pass tries those again. Calling it a failure would hide
        /// the hundreds that worked.
        /// </summary>
        private static void ApplyFrameConversion(
            string dir, HousekeepingSettings settings, HousekeepingStepReport result)
        {
            var outcome = FrameConversion.Run(dir, settings.FrameJpegQuality);

            if (outcome.Error != null)
            {
                result.Outcome = "refused";
                result.Error = outcome.Error;
                return;
            }

            result.Outcome = "done";
            result.BytesReclaimed = outcome.BytesReclaimed;
            if (outcome.Failed > 0)
            {
                result.Error = $"{outcome.Converted} frame(s) converted, {outcome.Failed} left as PNG "
                             + "because they could not be re-encoded; their pages still resolve";
            }
        }

        /// <summary>
        /// The tail of the decay ladder (issue #59): the recording's video, its composition inputs and
        /// its extracted frames go; the transcript, the walkthrough text, the manifest, the thumbnail
        /// and the owner's hand-taken shots stay.
        ///
        /// The ordering law applies to the WHOLE set: the page is rewritten and the manifest stops
        /// naming every doomed file BEFORE anything is deleted. A crash at any point therefore leaves
        /// references that still resolve and a pass that simply runs again - never a page or manifest
        /// pointing at files that are gone.
        /// </summary>
        private static void ApplyExpiry(string dir, HousekeepingStep step, HousekeepingStepReport result)
        {
            // What goes, positively enumerated from the disk this instant: the keeper, the composition
            // inputs, the extracted frames. Nothing else - a hand-taken shot is not on this list and
            // cannot get onto it.
            var doomed = new List<(string Path, long Bytes)>();
            void Consider(string relative)
            {
                string p = Path.Combine(dir, relative);
                if (!File.Exists(p)) return;
                try { doomed.Add((p, new FileInfo(p).Length)); }
                catch (IOException) { /* a file that vanishes mid-enumeration is not one to delete */ }
            }

            Consider(step.File);
            foreach (string input in HousekeepingPlan.CompositionInputFiles) Consider(input);

            string shots = Path.Combine(dir, HousekeepingPlan.FramesDirectory);
            if (Directory.Exists(shots))
            {
                foreach (string pattern in new[] { "frame_*.png", "frame_*.jpg" })
                {
                    foreach (string frame in Directory.EnumerateFiles(shots, pattern))
                    {
                        try { doomed.Add((frame, new FileInfo(frame).Length)); }
                        catch (IOException) { }
                    }
                }
            }

            // 1) The page first, so nothing the reader opens points at what is about to disappear.
            string page = Path.Combine(dir, FrameConversion.WalkthroughFile);
            if (File.Exists(page))
            {
                string html = File.ReadAllText(page);
                string retired = WalkthroughRetirement.Rewrite(html, DateTime.UtcNow);
                if (retired != html)
                {
                    string temp = page + ".housekeeping.tmp";
                    File.WriteAllText(temp, retired);
                    File.Move(temp, page, overwrite: true);
                    Log.Info($"[Housekeeper] {Path.GetFileName(dir)}: {FrameConversion.WalkthroughFile} retired its frame images");
                }
            }

            // 2) The manifest stops naming every file the disk is about to lose.
            ManifestStore.Update(dir, m =>
            {
                m.VideoFile = null;
                foreach (string input in HousekeepingPlan.CompositionInputFiles)
                {
                    if (string.Equals(m.CameraFile, input, StringComparison.OrdinalIgnoreCase)) m.CameraFile = null;
                }
                m.Files.RemoveAll(f =>
                    string.Equals(f, step.File, StringComparison.OrdinalIgnoreCase)
                    || HousekeepingPlan.CompositionInputFiles.Contains(f, StringComparer.OrdinalIgnoreCase)
                    || HousekeepingPlan.IsExtractedFrameFile(f));
                m.Shots.RemoveAll(s => HousekeepingPlan.IsExtractedFrameFile(s.File));
            });

            // 3) Now the files can leave.
            long reclaimed = 0;
            foreach (var (path, bytes) in doomed)
            {
                try
                {
                    File.Delete(path);
                    reclaimed += bytes;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Housekeeper] could not delete {path} while expiring {dir}: {ex.Message}");
                }
            }

            result.Outcome = "done";
            result.BytesReclaimed = reclaimed;
            Log.Info($"[Housekeeper] expired {Path.GetFileName(dir)}: {doomed.Count} file(s), "
                   + $"{reclaimed / 1024.0 / 1024.0:N1} MB reclaimed; the recording survives as its text");
        }

        /// <summary>Replace a name in a manifest list, in place, leaving order alone.</summary>
        private static void Swap(List<string> names, string from, string to)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], from, StringComparison.OrdinalIgnoreCase)) names[i] = to;
            }
            if (!names.Contains(to, StringComparer.OrdinalIgnoreCase)) names.Add(to);
        }

        /// <summary>
        /// Rewrite a log so only its last <paramref name="keepBytes"/> survive, and return the bytes
        /// reclaimed. The kept part starts at the first line break inside the window, so the file does
        /// not begin mid-line.
        /// </summary>
        private static long TruncateToTail(string path, long keepBytes)
        {
            long before = new FileInfo(path).Length;
            if (before <= keepBytes) return 0;

            byte[] tail = new byte[keepBytes];
            using (var read = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                read.Seek(-keepBytes, SeekOrigin.End);
                int got = read.Read(tail, 0, tail.Length);
                if (got < tail.Length) Array.Resize(ref tail, got);
            }

            int start = Array.IndexOf(tail, (byte)'\n');
            start = start < 0 || start + 1 >= tail.Length ? 0 : start + 1;

            string banner = $"[truncated by AgentEyes housekeeping on "
                          + $"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}; "
                          + $"{before:N0} bytes became the last {tail.Length - start:N0}]\r\n";

            using (var write = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] head = System.Text.Encoding.UTF8.GetBytes(banner);
                write.Write(head, 0, head.Length);
                write.Write(tail, start, tail.Length - start);
            }

            return before - new FileInfo(path).Length;
        }

        /// <summary>
        /// Append what happened to the recording's manifest. Done for a refusal and a failure as well
        /// as a success, because "we looked at this and would not touch it" is the most useful line in
        /// the record and the easiest one to leave out.
        /// </summary>
        private static void Record(string dir, HousekeepingStep step, HousekeepingStepReport result)
        {
            try
            {
                ManifestStore.Update(dir, m => m.Housekeeping.Add(new Manifest.HousekeepingRecord
                {
                    Kind = result.Kind,
                    File = result.File,
                    Output = result.Output,
                    BytesReclaimed = result.BytesReclaimed,
                    WhenUtc = DateTime.UtcNow,
                    Outcome = result.Outcome,
                    Error = result.Error,
                    BitExact = step.Kind == HousekeepingKind.TranscodePreservedAudio ? result.BitExact : null,
                    SourceHash = step.Kind == HousekeepingKind.TranscodePreservedAudio ? result.SourceHash : null,
                    OutputHash = step.Kind == HousekeepingKind.TranscodePreservedAudio ? result.OutputHash : null,
                }));
            }
            catch (Exception ex)
            {
                // The action already happened. Losing its record is bad and is logged loudly, but it
                // must not turn one unwritable manifest into a failed pass for every other recording.
                Log.Error($"[Housekeeper] could not record {result.Kind} {result.File} in {dir}", ex);
            }
        }

        /// <summary>One recording, read once.</summary>
        private sealed class LoadedRecording
        {
            public string Path { get; init; } = "";
            public string Name { get; init; } = "";
            public Manifest Manifest { get; init; } = new();
            public Dictionary<string, long> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public int AgeDays { get; init; }
        }

        /// <summary>
        /// Read one directory, or return null when it is not a recording this pass can reason about.
        ///
        /// A directory with no manifest is not a recording. A manifest whose CreatedUtc cannot be parsed
        /// has no age, and a tier keyed on age cannot be applied to it - so it is skipped and said so,
        /// rather than being given a guessed age. Guessing here would mean deleting an owner's preserved
        /// original on the strength of a date this code invented.
        /// </summary>
        private static LoadedRecording? Load(string dir, DateTime nowUtc)
        {
            string name = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, ManifestStore.FileName))) return null;

            Manifest manifest;
            try
            {
                manifest = Manifest.Load(dir);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Housekeeper] skipping {name}: its manifest could not be read - {ex.Message}");
                return null;
            }

            if (!DateTime.TryParse(manifest.CreatedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime created))
            {
                Log.Warn($"[Housekeeper] skipping {name}: CreatedUtc is '{manifest.CreatedUtc}', "
                       + "which is not a date, so this recording has no age to apply a tier to");
                return null;
            }

            var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
                try
                {
                    files[rel] = new FileInfo(path).Length;
                }
                catch (Exception ex)
                {
                    // A file whose size cannot be read is a file this pass will not act on: every step
                    // is keyed on a name present in this map, so omitting it removes it from
                    // consideration entirely, which is the safe direction.
                    Log.Warn($"[Housekeeper] {name}: could not size {rel} - {ex.Message}");
                }
            }

            return new LoadedRecording
            {
                Path = dir,
                Name = name,
                Manifest = manifest,
                Files = files,
                AgeDays = Math.Max(0, (int)(nowUtc - created).TotalDays),
            };
        }
    }
}
