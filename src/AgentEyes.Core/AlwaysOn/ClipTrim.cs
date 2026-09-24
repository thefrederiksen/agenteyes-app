using System;
using System.Collections.Generic;

namespace AgentEyes.AlwaysOn
{
    /// <summary>One kept piece as the joiner sees it: where it is, when it opened, how long it is.</summary>
    internal sealed record TrimInput(string Path, DateTime StartUtc, double Seconds);

    /// <summary>
    /// One piece of a clip after trimming (issue #79). <see cref="InSeconds"/> is where, inside the
    /// piece, the clip starts (always on a keyframe), or null to keep it from its start;
    /// <see cref="OutSeconds"/> is where the clip ends inside it, or null to keep it to its end.
    /// </summary>
    internal sealed record TrimPart(string Path, DateTime StartUtc, double Seconds, double? InSeconds, double? OutSeconds)
    {
        /// <summary>True when the piece is cut at all - only a clip's first and last piece ever are.</summary>
        public bool IsTrimmed => InSeconds.HasValue || OutSeconds.HasValue;

        /// <summary>When the kept part begins.</summary>
        public DateTime KeptStartUtc => StartUtc.AddSeconds(InSeconds ?? 0);

        /// <summary>How many seconds of the piece the clip keeps.</summary>
        public double KeptSeconds => (OutSeconds ?? Seconds) - (InSeconds ?? 0);
    }

    /// <summary>What the joiner does with a closed clip's pieces (issue #79).</summary>
    internal sealed class TrimPlan
    {
        /// <summary>The pieces that go into the clip, in order, each with its cut (if any).</summary>
        public List<TrimPart> Parts { get; } = new();

        /// <summary>Pieces wholly outside the clip's span - only silence past the tail or before the
        /// lead-in. They are not joined.</summary>
        public List<TrimInput> Outside { get; } = new();
    }

    /// <summary>
    /// THE TRIM PLANNER (issue #79), pure so it is proven without ffmpeg.
    ///
    /// A closed clip keeps [span start, span end] (<see cref="ClipSpan"/>). Its pieces were kept whole;
    /// the joiner cuts only the FIRST piece (to start at the keyframe AT OR BEFORE the span start, so
    /// the cut is lossless - a stream copy can only start on a keyframe) and the LAST piece (to end at
    /// the span end - a stream copy can end on any frame). Every piece in between is joined untouched,
    /// so a clip is never split and the kept video is byte-for-byte what was captured.
    ///
    /// WHERE THE CUT LANDS. The planner asks for the span start rounded DOWN to the capture's keyframe
    /// grid (<c>keyframeSeconds</c>, see <see cref="AlwaysOnArgs.Capture"/>), but the trim itself is an
    /// INPUT-SIDE SEEK with stream copy (<see cref="AlwaysOnArgs.Trim"/>: -ss before -i, -c copy), and
    /// an input seek lands on the keyframe ACTUALLY at or before the asked time - the one in the file,
    /// not the one the grid promised. So a keyframe the encoder placed late makes the lead-in LONGER
    /// (the cut falls back to the previous keyframe), never shorter: no speech is lost, and a bit more
    /// picture before it is kept. The encoder GOP plus the forced keyframes (<see cref="AlwaysOnArgs.GopArgs"/>)
    /// keep that error within one keyframe interval when the encoder honours them; the live measurement
    /// of where h264_qsv really puts them is the tester's (ffprobe -skip_frame nokey).
    /// </summary>
    internal static class ClipTrim
    {
        public static TrimPlan Plan(IReadOnlyList<TrimInput> pieces, DateTime spanStartUtc, DateTime spanEndUtc, int keyframeSeconds)
        {
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (keyframeSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(keyframeSeconds), "the keyframe interval must be positive");
            if (spanEndUtc <= spanStartUtc) throw new ArgumentException("a clip's span must end after it starts", nameof(spanEndUtc));

            var plan = new TrimPlan();
            foreach (var p in pieces)
            {
                DateTime end = p.StartUtc.AddSeconds(p.Seconds);
                if (end <= spanStartUtc || p.StartUtc >= spanEndUtc)
                {
                    plan.Outside.Add(p);
                    continue;
                }

                double? cutIn = null;
                if (spanStartUtc > p.StartUtc)
                {
                    double offset = (spanStartUtc - p.StartUtc).TotalSeconds;
                    double keyframe = Math.Floor(offset / keyframeSeconds) * keyframeSeconds;
                    if (keyframe > 0) cutIn = keyframe;
                }
                double? cutOut = spanEndUtc < end ? Math.Round((spanEndUtc - p.StartUtc).TotalSeconds, 3) : null;
                plan.Parts.Add(new TrimPart(p.Path, p.StartUtc, p.Seconds, cutIn, cutOut));
            }
            return plan;
        }
    }
}
