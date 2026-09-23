using System;
using System.Text.RegularExpressions;

namespace AgentEyes.Video
{
    /// <summary>
    /// The size of the frames a camera is actually producing, as ffmpeg itself reported them
    /// (issue #36).
    ///
    /// WHY THIS HAS TO EXIST. The preset editor's live preview is scaled to fit a FIXED 320x240
    /// buffer and PADDED with black bars (<see cref="FfmpegArgs.CameraPreview"/> uses
    /// <c>force_original_aspect_ratio=decrease</c> + <c>pad</c>), because the reader counts a frame
    /// complete at an exact byte count. So the picture in the pane is NOT the camera frame - it is
    /// the camera frame letterboxed inside one. Issue #36 stores the circle in CAMERA-FRAME
    /// coordinates (assumption E2), so drawing the circle over the pane without knowing where the
    /// bars are would put it in the wrong place on any camera that is not 4:3 - and the HUD, whose
    /// own tap is <c>scale=-2:270</c> with no padding, would then disagree with the editor (AC3).
    ///
    /// IT IS A PRESENCE AND IT FAILS CLOSED. The size is read from the "Input #0 ... Stream ...
    /// Video: ... WxH" line ffmpeg prints when it opens the device. If that line has not been seen,
    /// this is NULL and the caller SAYS SO rather than assuming the picture fills the pane - an
    /// assumed size would silently mis-place the circle, which is precisely the failure this exists
    /// to prevent.
    ///
    /// WHAT IT CANNOT SEE: a camera that changes resolution mid-stream (ffmpeg reopens the device to
    /// do that, which produces a new Input line and a new preview session), and any geometry ffmpeg
    /// does not print. It reports what ffmpeg said, nothing more.
    /// </summary>
    internal readonly struct CameraFrameSize : IEquatable<CameraFrameSize>
    {
        /// <summary>Smallest dimension worth believing - anything below this is a parse accident.</summary>
        public const int MinDimension = 16;

        public CameraFrameSize(int width, int height)
        {
            if (width < MinDimension || height < MinDimension)
                throw new ArgumentOutOfRangeException(nameof(width),
                    $"a camera frame size must be at least {MinDimension}x{MinDimension}; got {width}x{height}.");
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>Width divided by height - the number the overlay geometry actually needs.</summary>
        public double Aspect => (double)Width / Height;

        public bool Equals(CameraFrameSize other) => Width == other.Width && Height == other.Height;
        public override bool Equals(object? obj) => obj is CameraFrameSize other && Equals(other);
        public override int GetHashCode() => (Width * 397) ^ Height;
        public override string ToString() => $"{Width}x{Height}";

        /// <summary>
        /// The INPUT video stream's size as printed in <paramref name="ffmpegLog"/>, or null when
        /// that line has not been printed (yet).
        ///
        /// Only the block under "Input #" is read. ffmpeg prints an "Output #" block describing the
        /// padded 320x240 buffer as well, and reading that one would answer the question with the
        /// very number that hides the bars.
        /// </summary>
        public static CameraFrameSize? FromFfmpegLog(string? ffmpegLog)
        {
            if (string.IsNullOrEmpty(ffmpegLog)) return null;

            bool inInput = false;
            foreach (string raw in ffmpegLog.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("Input #", StringComparison.Ordinal)) { inInput = true; continue; }
                // Anything that starts a new top-level block ends the input block. "Stream mapping:"
                // and "Output #" both do, and both are unindented.
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])
                    && !trimmed.StartsWith("Input #", StringComparison.Ordinal))
                {
                    inInput = false;
                    continue;
                }

                if (!inInput) continue;
                if (!trimmed.StartsWith("Stream #", StringComparison.Ordinal)) continue;
                if (trimmed.IndexOf(": Video:", StringComparison.Ordinal) < 0) continue;

                var m = Dimensions.Match(trimmed);
                if (!m.Success) continue;

                int w = int.Parse(m.Groups[1].Value);
                int h = int.Parse(m.Groups[2].Value);
                if (w < MinDimension || h < MinDimension) continue;
                return new CameraFrameSize(w, h);
            }

            return null;
        }

        /// <summary>
        /// "640x480" inside a stream line. The digit guards on both ends keep it off the hexadecimal
        /// FourCC codes on the same line ("0x32595559"), which are the only other thing there that
        /// looks remotely like this.
        /// </summary>
        private static readonly Regex Dimensions =
            new Regex(@"(?<![\dx])(\d{2,5})x(\d{2,5})(?![\dx])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
