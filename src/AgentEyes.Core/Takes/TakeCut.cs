using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgentEyes.Video;

namespace AgentEyes.Takes
{
    /// <summary>The span of the continuous file one attempt is cut from (issue #230 round 3).</summary>
    internal readonly record struct CutSpan(double StartSeconds, double DurationSeconds)
    {
        public double EndSeconds => StartSeconds + DurationSeconds;

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{StartSeconds:F2}s-{EndSeconds:F2}s ({DurationSeconds:F2}s)");
    }

    /// <summary>
    /// Issue #230 round 3: the takes screen records ONE continuous camera+mic file for as long as it
    /// is open, and Space / R only MARK where an attempt starts and ends. Each attempt is then cut
    /// out of the continuous file with a margin on both sides, so a word said a little before the
    /// start mark or a little after the end mark is never lost. The continuous file is always kept.
    ///
    /// The cut re-encodes the short span (input seek + libx264): the seek is frame-exact even on the
    /// fragmented file while it is still being written - checked against a decode-from-the-start cut
    /// of the same span, identical frame hashes - so the margin is exactly what it says.
    /// </summary>
    internal static class TakeCut
    {
        /// <summary>Seconds kept before the start mark and after the end mark.</summary>
        public const double MarginSeconds = 2.0;

        /// <summary>
        /// How far past the end of a span the camera must have written before that span is cut from
        /// the live file. The continuous recording puts a keyframe every second and a fragment reaches
        /// disk at each keyframe, so one second would do; three leaves room for the muxer's queue.
        /// </summary>
        public const double WrittenBeyondSeconds = 3.0;

        /// <summary>A cut shorter than its span by more than this is a failed cut, never a quiet one.</summary>
        public const double ShortfallToleranceSeconds = 0.5;

        /// <summary>
        /// The span to cut for an attempt marked from <paramref name="startMark"/> to
        /// <paramref name="endMark"/> (seconds in the continuous file): the margin either side,
        /// clamped to 0 and, when known, to the file's duration.
        /// </summary>
        public static CutSpan Span(double startMark, double endMark, double? fileDurationSeconds, double margin = MarginSeconds)
        {
            if (double.IsNaN(startMark) || double.IsNaN(endMark))
                throw new ArgumentException("a mark is not a number.");
            if (endMark < startMark)
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"the end mark {endMark:F2}s is before the start mark {startMark:F2}s."));
            if (margin < 0) throw new ArgumentOutOfRangeException(nameof(margin), "the margin cannot be negative.");

            double start = Math.Max(0, startMark - margin);
            double end = endMark + margin;
            if (fileDurationSeconds is double d) end = Math.Min(end, d);
            if (end <= start)
                throw new UsageException(string.Create(CultureInfo.InvariantCulture,
                    $"nothing to cut: the attempt starts at {start:F2}s but the continuous file ends at {end:F2}s."));
            return new CutSpan(Math.Round(start, 3), Math.Round(end - start, 3));
        }

        /// <summary>The camera position the live file must reach before <paramref name="span"/> can be cut.</summary>
        public static double PositionNeeded(CutSpan span) => span.EndSeconds + WrittenBeyondSeconds;

        /// <summary>The ffmpeg arguments that cut <paramref name="span"/> of <paramref name="source"/>
        /// into <paramref name="output"/>, re-encoded.</summary>
        public static List<string> Args(string source, CutSpan span, string output)
        {
            string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
            return new List<string>
            {
                "-y",
                "-ss", F(span.StartSeconds),
                "-i", source,
                "-t", F(span.DurationSeconds),
                "-map", "0:v:0", "-map", "0:a:0",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
                "-movflags", "+faststart",
                output,
            };
        }

        /// <summary>
        /// Cut <paramref name="span"/> out of <paramref name="source"/> into <paramref name="output"/>
        /// and return the cut's duration. Throws when ffmpeg fails or the cut comes out shorter than
        /// its span - never a quiet short file.
        /// </summary>
        public static double Cut(string source, CutSpan span, string output)
        {
            Log.Info($"[TakeCut] Cut: {source} span {span} -> {output}");
            if (!File.Exists(source)) throw new UsageException($"the continuous recording {source} is not there.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            Ffmpeg.RunWatched(Args(source, span, output), "take cut", Ffmpeg.CurrentPostProcessStallTimeout);

            double got = MediaProbe.DurationSeconds(output);
            if (got < span.DurationSeconds - ShortfallToleranceSeconds)
            {
                string msg = string.Create(CultureInfo.InvariantCulture,
                    $"the cut {output} is {got:F2}s long but its span is {span.DurationSeconds:F2}s - the continuous file did not hold the whole span.");
                Log.Error($"[TakeCut] Cut FAILED: {msg}");
                throw new UsageException(msg);
            }
            Log.Info($"[TakeCut] Cut: ok, {got:F2}s");
            return got;
        }
    }

    /// <summary>
    /// Issue #230 round 3: turns a moment on the screen's clock into a time in the continuous file.
    ///
    /// The file's time 0 is the camera's first frame, which the screen never sees directly. What it
    /// sees is ffmpeg's own position ("time=") at moments it reads it. Each reading says time 0 was
    /// at or before (wall - position) - a reading can only lag the picture, never lead it - so the
    /// smallest (wall - position) seen is the closest estimate, and it only gets better. Any error
    /// left places a mark slightly EARLY, which the cut margin absorbs.
    /// </summary>
    internal sealed class ContinuousClock
    {
        private readonly object _lock = new();
        private double? _anchor;

        /// <summary>The screen-clock second at which the file's time 0 was recorded, once known.</summary>
        public double? AnchorSeconds { get { lock (_lock) return _anchor; } }

        /// <summary>Feed one reading: the screen clock now and the camera position, or null when the
        /// camera is not recording. A position of 0 says nothing (ffmpeg prints it before the first frame).</summary>
        public void Observe(double wallSeconds, double? positionSeconds)
        {
            if (positionSeconds is not double p || p <= 0) return;
            double candidate = wallSeconds - p;
            lock (_lock)
            {
                if (_anchor == null || candidate < _anchor) _anchor = candidate;
            }
        }

        /// <summary>The file time of the screen-clock moment <paramref name="wallSeconds"/>, clamped at 0,
        /// or null before the first reading.</summary>
        public double? FileSeconds(double wallSeconds) =>
            AnchorSeconds is double a ? Math.Max(0, wallSeconds - a) : null;
    }
}
