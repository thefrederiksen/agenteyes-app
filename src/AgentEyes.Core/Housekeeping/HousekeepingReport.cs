using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentEyes.Housekeeping
{
    /// <summary>
    /// What one recording's pass did or would do (issue #56).
    /// </summary>
    internal sealed class HousekeepingRecordingReport
    {
        /// <summary>The recording directory's name, not its full path.</summary>
        public string Recording { get; set; } = "";

        public int AgeDays { get; set; }

        /// <summary>Set when the whole recording was exempt - pinned, or housekeeping off.</summary>
        public string? Skipped { get; set; }

        public List<HousekeepingStepReport> Steps { get; set; } = new();

        public long BytesReclaimed => Steps.Sum(s => s.BytesReclaimed);
    }

    /// <summary>One step, and what became of it.</summary>
    internal sealed class HousekeepingStepReport
    {
        public string Kind { get; set; } = "";
        public string File { get; set; } = "";
        public string? Output { get; set; }
        public long Bytes { get; set; }
        public long BytesReclaimed { get; set; }
        public string Reason { get; set; } = "";

        /// <summary>"planned" in a report-only pass; otherwise "done", "refused" or "failed".</summary>
        public string Outcome { get; set; } = "";

        public string? Error { get; set; }
        public bool? BitExact { get; set; }

        /// <summary>For a transcode: the decoded-stream hash of the source that was removed.</summary>
        public string? SourceHash { get; set; }

        /// <summary>For a transcode: the decoded-stream hash of the file that replaced it.</summary>
        public string? OutputHash { get; set; }
    }

    /// <summary>
    /// The whole pass (issue #56). This is what the Control API returns and what the user interface
    /// will eventually render, so it has to be readable on its own: the mode it ran in, what it looked
    /// at, what it did, and the bytes.
    ///
    /// It reports what it FOUND as well as what it changed. A pass that reclaimed nothing because
    /// there was nothing to reclaim and a pass that reclaimed nothing because it never ran are
    /// different events, and a report that cannot tell them apart is the failure mode this product has
    /// been bitten by before - a button that reports nothing reads as broken.
    /// </summary>
    internal sealed class HousekeepingReport
    {
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }

        /// <summary>What triggered it: "timer", "startup", "post-recording", "api".</summary>
        public string Trigger { get; set; } = "";

        /// <summary>True when nothing was changed on disk by design.</summary>
        public bool ReportOnly { get; set; }

        /// <summary>True when the pass stood down for a capture, so the report is PARTIAL.</summary>
        public bool YieldedToCapture { get; set; }

        /// <summary>Why the pass did nothing at all, when that is the case. Null otherwise.</summary>
        public string? NotRun { get; set; }

        public int RecordingsExamined { get; set; }

        public List<HousekeepingRecordingReport> Recordings { get; set; } = new();

        public long BytesReclaimed => Recordings.Sum(r => r.BytesReclaimed);

        /// <summary>
        /// The kinds whose saving cannot be known until they are done, because they RE-ENCODE rather
        /// than delete. Their input size is reported on its own; it is never folded into a reclaim
        /// figure, so no number in a report is part measurement and part guess.
        /// </summary>
        private static readonly string[] ReEncodingKinds =
        {
            nameof(HousekeepingKind.TranscodePreservedAudio),
            nameof(HousekeepingKind.ConvertFramesToJpeg),
        };

        /// <summary>
        /// Bytes the pass WOULD reclaim outright - the deletes and the truncations, whose saving is
        /// exactly their size. Re-encodes are deliberately absent; see <see cref="BytesToReEncode"/>.
        /// </summary>
        public long BytesPlanned => Recordings.Sum(r => r.Steps
            .Where(s => !ReEncodingKinds.Contains(s.Kind))
            .Sum(s => s.Bytes - (s.Bytes - s.BytesReclaimed)));

        /// <summary>
        /// Bytes of preserved audio a report-only pass has listed for re-encoding. Reported SEPARATELY
        /// from the reclaim figure and never folded into it: what a transcode saves cannot be known
        /// without doing it, so a single combined total would be part measurement and part guess with
        /// no way for the reader to tell which. A dry run that answered "2,894 MB" while quietly
        /// listing 27.8 GB of audio to re-encode understated the job by an order of magnitude.
        /// </summary>
        public long BytesToTranscode => Recordings
            .SelectMany(r => r.Steps)
            .Where(s => ReEncodingKinds.Contains(s.Kind))
            .Sum(s => s.Bytes);

        /// <summary>Same figure, named for what it is when the reader is not thinking about audio.</summary>
        public long BytesToReEncode => BytesToTranscode;

        /// <summary>One line for the log and the tray, in plain words.</summary>
        public string Summary()
        {
            if (NotRun is not null) return $"housekeeping did not run: {NotRun}";
            int steps = Recordings.Sum(r => r.Steps.Count);
            string verb = ReportOnly ? "would reclaim" : "reclaimed";
            long bytes = ReportOnly ? BytesPlanned : BytesReclaimed;
            string partial = YieldedToCapture ? ", stopped early for a recording" : "";

            // The transcode figure is the INPUT size, stated as such. How much of it comes back is
            // measured per file by the real pass and is roughly 60% at the bit-exact default, but this
            // line does not predict it - it says what is going to be re-encoded.
            string transcodes = ReportOnly && BytesToReEncode > 0
                ? $", plus {BytesToReEncode / 1024.0 / 1024.0:N0} MB to re-encode "
                  + "(audio and frames; how much comes back is only known once it is done)"
                : "";

            return $"{RecordingsExamined} recording(s), {steps} action(s), {verb} "
                 + $"{bytes / 1024.0 / 1024.0:N0} MB{transcodes}{partial}";
        }
    }
}
