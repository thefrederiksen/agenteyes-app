using System;
using System.IO;

namespace AgentEyes.Preview
{
    /// <summary>
    /// Reads a published preview frame off disk (issue #33) - the consumer half of
    /// <see cref="PreviewTap"/>.
    ///
    /// It answers ONE question and answers it as a PRESENCE: are there bytes here that are a whole
    /// JPEG? Not "did the read succeed", not "does the file exist" - a file exists for the whole
    /// recording and a read of a file mid-rename succeeds while returning nothing useful. An absent
    /// file, an empty file and a partial file all come back the same way, as null, and null means
    /// "no frame", which is what the HUD's staleness watchdog is counting.
    ///
    /// The share flags matter and are not incidental. The tap renames a temporary file over this one
    /// ten times a second; opening it without <see cref="FileShare.Delete"/> would make the tap's
    /// rename fail, which would turn a reader into something that breaks the thing it is reading.
    /// </summary>
    internal static class PreviewFrameFile
    {
        /// <summary>
        /// The bytes of the frame at <paramref name="path"/>, or null when there is no WHOLE frame
        /// there right now (no file, an empty file, or a buffer missing either JPEG marker).
        ///
        /// Never throws for an ordinary miss: this is polled several times a second against a file
        /// another thread is replacing, and a missing or momentarily unreadable file is the normal
        /// case rather than an error. It DOES report a read that failed for a reason worth knowing
        /// through <paramref name="error"/>, so a caller that wants to log one can.
        /// </summary>
        public static byte[]? TryRead(string path, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                long length = stream.Length;
                if (length < JpegFrame.MinimumBytes || length > int.MaxValue) return null;

                var bytes = new byte[(int)length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                return JpegFrame.IsComplete(bytes, read) ? bytes : null;
            }
            catch (FileNotFoundException)
            {
                return null;   // the tap has not published yet, or has stopped publishing
            }
            catch (DirectoryNotFoundException)
            {
                return null;   // the preview directory is gone - the staleness watchdog reports it
            }
            catch (IOException ex)
            {
                error = ex.Message;   // in use for an instant during the rename; try again next tick
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }
}
