using System;
using System.Collections.Generic;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// What the always-on ffmpeg wrote to stderr, kept as WHOLE LINES (issue #81). The supervisor logs
    /// the last <see cref="TailLines"/> of them in full when it restarts a capture - the old tail was the
    /// last 800 characters with a "..." in front, which cut off exactly the line that said why.
    ///
    /// It also watches for an input that has died. ffmpeg keeps running when ONE input ends with an
    /// error (the screen grab after a desktop switch: "Failed to capture image (error 5)", then "Error
    /// during demuxing: I/O error") as long as another input - the sound - still delivers. Always-on
    /// then writes one piece of sound with no picture and never cuts a new piece, because the segment
    /// muxer cuts only on a video keyframe. <see cref="FatalInputLine"/> lets the supervisor restart it
    /// on the next pass instead of waiting for the missing piece.
    ///
    /// Thread-safe: ffmpeg's stderr arrives on a thread-pool thread; the supervisor reads on its timer.
    /// </summary>
    internal sealed class FfmpegStderr
    {
        /// <summary>How many lines the supervisor logs when a capture fails (issue #81: 20).</summary>
        public const int TailLines = 20;

        /// <summary>How many lines are kept - an all-day capture keeps the recent past, not the day.</summary>
        public const int Capacity = 200;

        /// <summary>The text ffmpeg writes when an input's reading thread ends on an error.</summary>
        public const string DemuxErrorMarker = "Error during demuxing";

        private readonly Queue<string> _lines = new();
        private readonly object _gate = new();
        private string? _fatal;

        /// <summary>The first line that said an input died, or null while every input is alive.</summary>
        public string? FatalInputLine { get { lock (_gate) return _fatal; } }

        /// <summary>Record one line ffmpeg wrote. A null line (the end of the stream) is ignored.</summary>
        public void Add(string? line)
        {
            if (line == null) return;
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Capacity) _lines.Dequeue();
                if (_fatal == null && IsFatalInputError(line))
                {
                    _fatal = line;
                    Log.Warn($"[FfmpegStderr] Add: an always-on ffmpeg input died - {line}");
                }
            }
        }

        /// <summary>The last <paramref name="count"/> lines, oldest first, each in full, one per line.
        /// "(ffmpeg said nothing)" when there are none - an empty string in a log reads as a lost log.</summary>
        public string Tail(int count = TailLines)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "count must be positive");
            lock (_gate)
            {
                if (_lines.Count == 0) return "(ffmpeg said nothing)";
                var all = _lines.ToArray();
                int from = Math.Max(0, all.Length - count);
                return string.Join(Environment.NewLine, all, from, all.Length - from);
            }
        }

        /// <summary>True for a line that says one of ffmpeg's inputs stopped for good.</summary>
        public static bool IsFatalInputError(string line) =>
            line != null && line.Contains(DemuxErrorMarker, StringComparison.Ordinal);
    }
}
