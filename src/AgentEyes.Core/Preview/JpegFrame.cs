using System;

namespace AgentEyes.Preview
{
    /// <summary>
    /// Whether a run of bytes is a WHOLE JPEG image (issue #33).
    ///
    /// It exists because every consumer of a preview frame in this feature reads a buffer that
    /// something else is still filling - an MJPEG pipe that delivers a frame across several reads,
    /// or a file on disk that is being replaced ten times a second. The honest question in both
    /// places is the same, and it is a PRESENCE: does this buffer BEGIN with the JPEG start-of-image
    /// marker and END with the end-of-image marker?
    ///
    /// The absence-shaped version of that question - "did decoding fail?" - is what this deliberately
    /// avoids. A truncated JPEG can decode to a half-drawn image without throwing, so "no exception"
    /// would certify a frame that is not there yet. Two markers are either present or they are not.
    ///
    /// WHAT IT CANNOT SEE, stated rather than implied: it does not validate the entropy-coded data
    /// between the markers. A buffer that starts with SOI and ends with EOI but whose middle was
    /// corrupted in transit passes this check. Nothing in this feature can corrupt a middle - an
    /// anonymous pipe and an NTFS rename both deliver bytes intact or not at all - so the check is
    /// sized to the failure that actually happens here, which is a SHORT buffer.
    /// </summary>
    internal static class JpegFrame
    {
        /// <summary>The shortest buffer that could carry both markers. Nothing smaller is a frame.</summary>
        public const int MinimumBytes = 4;

        /// <summary>0xFF 0xD8 - JPEG start of image.</summary>
        public const byte Soi1 = 0xFF;
        public const byte Soi2 = 0xD8;

        /// <summary>0xFF 0xD9 - JPEG end of image.</summary>
        public const byte Eoi1 = 0xFF;
        public const byte Eoi2 = 0xD9;

        /// <summary>
        /// True when <paramref name="buffer"/>'s first <paramref name="count"/> bytes start with SOI
        /// and end with EOI. A null buffer, a negative or oversized count, and a buffer shorter than
        /// <see cref="MinimumBytes"/> are all false - "not a complete frame" - never an exception:
        /// this is asked on a hot path about data that is legitimately incomplete most of the time.
        /// </summary>
        public static bool IsComplete(byte[]? buffer, int count)
        {
            if (buffer == null) return false;
            if (count < MinimumBytes || count > buffer.Length) return false;
            return buffer[0] == Soi1 && buffer[1] == Soi2
                && buffer[count - 2] == Eoi1 && buffer[count - 1] == Eoi2;
        }

        /// <summary>The whole-buffer form of <see cref="IsComplete(byte[], int)"/>.</summary>
        public static bool IsComplete(byte[]? buffer) => IsComplete(buffer, buffer?.Length ?? 0);
    }
}
