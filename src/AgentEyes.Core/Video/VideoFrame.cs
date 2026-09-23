using System;
using System.Globalization;
using System.IO;

namespace AgentEyes.Video
{
    /// <summary>
    /// One frame of a video, extracted on demand (issue #59).
    ///
    /// A recording whose walkthrough frames are not stored on disk asks for each frame at its
    /// offset: the video IS the storage and this is the read. One ffmpeg invocation per request,
    /// quality 2 (the ffmpeg scale, where 2 is near-transparent), the JPEG held in memory and
    /// never kept on disk - the temp file is deleted the moment its bytes are read, including on
    /// failure.
    /// </summary>
    internal static class VideoFrame
    {
        /// <summary>
        /// Extract the frame at <paramref name="offsetSeconds"/> as JPEG bytes. Throws
        /// <see cref="UsageException"/> with the encoder's own reason when extraction fails, and
        /// an <see cref="InvalidOperationException"/> when the encoder produces nothing - a
        /// zero-byte "success" is not a frame.
        /// </summary>
        public static byte[] ExtractJpeg(string videoPath, double offsetSeconds)
        {
            if (offsetSeconds < 0) throw new ArgumentOutOfRangeException(nameof(offsetSeconds));
            if (!File.Exists(videoPath)) throw new UsageException("no video at " + videoPath);

            string temp = Path.Combine(Path.GetTempPath(),
                "agenteyes-frame-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                Ffmpeg.Run(new[]
                {
                    "-y",
                    "-ss", offsetSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", videoPath,
                    "-frames:v", "1",
                    "-q:v", "2",
                    temp,
                }, "frame on demand");

                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    throw new InvalidOperationException(
                        "the frame extraction produced nothing (offset past the end of the video?)");
                }

                return File.ReadAllBytes(temp);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
