using System;
using System.Collections.Generic;

namespace AgentEyes.Preview
{
    /// <summary>
    /// Cuts an MJPEG byte STREAM into whole JPEG FRAMES (issue #33).
    ///
    /// The preview tap reads ffmpeg's stdout, and a pipe read returns whatever bytes happen to have
    /// arrived: half a frame, three frames, a frame and a half. Nothing in the transport marks where
    /// one image ends, so the frame boundaries have to be recovered from the JPEG markers themselves.
    /// This class is that recovery and NOTHING else - no threads, no files, no ffmpeg - so every
    /// boundary case it exists for (a split across two reads, a frame that never terminates, bytes
    /// before the first image) is a unit test rather than a race.
    ///
    /// It is deliberately PRESENCE-driven: a frame is emitted only once its end-of-image marker has
    /// actually arrived. There is no "we have probably got it all now" path, and no timeout that
    /// releases a partial image - a frame that never completes is never emitted, which is the correct
    /// answer for a monitor whose next frame is 100ms away.
    ///
    /// MEMORY IS BOUNDED, and that bound is load-bearing rather than defensive. This buffers a live
    /// stream from a process that outlives any single read; a producer that emitted an SOI and then
    /// never an EOI would otherwise grow this buffer for the length of the recording. On overrun the
    /// buffer is dropped, the drop is COUNTED (never silently absorbed - see
    /// <see cref="OversizeDrops"/>), and framing resynchronises on the next SOI.
    ///
    /// WHAT IT CANNOT SEE: it finds the end of a frame by scanning for the two-byte EOI marker.
    /// Inside JPEG entropy-coded data a literal 0xFF is byte-stuffed as FF 00, so an FF D9 pair
    /// cannot occur there - but an EOI embedded in a JPEG-encoded EXIF THUMBNAIL would cut a frame
    /// short. ffmpeg's mjpeg encoder, which is the only producer this is used with, writes no EXIF
    /// thumbnail. A short cut of that kind would fail <see cref="JpegFrame.IsComplete"/> at the
    /// consumer, so it degrades to a dropped frame rather than a wrong image.
    /// </summary>
    internal sealed class MjpegFramer
    {
        /// <summary>Default ceiling on one buffered frame. A 480x270 preview JPEG is tens of
        /// kilobytes; a megabyte is far beyond any real frame and far below anything that matters
        /// to the process.</summary>
        public const int DefaultMaxFrameBytes = 1024 * 1024;

        private readonly int _maxFrameBytes;
        private byte[] _buffer;
        private int _length;

        public MjpegFramer(int maxFrameBytes = DefaultMaxFrameBytes)
        {
            if (maxFrameBytes < JpegFrame.MinimumBytes)
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes),
                    $"a frame ceiling below {JpegFrame.MinimumBytes} bytes could never hold a JPEG");
            _maxFrameBytes = maxFrameBytes;
            _buffer = new byte[Math.Min(maxFrameBytes, 64 * 1024)];
        }

        /// <summary>How many times a frame was abandoned for exceeding the byte ceiling. Non-zero
        /// means the producer is not writing the MJPEG this expects - it is reported, not hidden.</summary>
        public int OversizeDrops { get; private set; }

        /// <summary>Bytes currently held for a frame that has not finished arriving. For tests and
        /// diagnostics.</summary>
        public int PendingBytes => _length;

        /// <summary>
        /// Add the first <paramref name="count"/> bytes of <paramref name="chunk"/> to the stream and
        /// return every WHOLE frame that completed, in order. An empty list is the normal answer for
        /// a read that landed in the middle of an image.
        /// </summary>
        public IReadOnlyList<byte[]> Append(byte[] chunk, int count)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            if (count < 0 || count > chunk.Length)
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must lie inside the chunk");

            var frames = new List<byte[]>();
            if (count == 0) return frames;

            EnsureCapacity(_length + count);
            Buffer.BlockCopy(chunk, 0, _buffer, _length, count);
            _length += count;

            // Bytes before the first start-of-image are not part of any frame - a mid-stream attach,
            // or the tail of a frame whose start was dropped. Discard them rather than carry them.
            int soi = IndexOfMarker(0, JpegFrame.Soi2);
            if (soi < 0)
            {
                // Keep only a possible trailing 0xFF: the SOI may be split across this read and the next.
                int keep = _length > 0 && _buffer[_length - 1] == JpegFrame.Soi1 ? 1 : 0;
                Discard(_length - keep);
                return frames;
            }
            if (soi > 0) Discard(soi);

            while (true)
            {
                // Search for the end marker from just after the start marker.
                int eoi = IndexOfMarker(2, JpegFrame.Eoi2);
                if (eoi < 0) break;

                int frameLength = eoi + 2;
                var frame = new byte[frameLength];
                Buffer.BlockCopy(_buffer, 0, frame, 0, frameLength);
                frames.Add(frame);
                Discard(frameLength);

                int next = IndexOfMarker(0, JpegFrame.Soi2);
                if (next < 0)
                {
                    int keep = _length > 0 && _buffer[_length - 1] == JpegFrame.Soi1 ? 1 : 0;
                    Discard(_length - keep);
                    break;
                }
                if (next > 0) Discard(next);
                if (_length < JpegFrame.MinimumBytes) break;
            }

            if (_length > _maxFrameBytes)
            {
                // COUNTED, NOT LOGGED, and that is deliberate (issue #33, AC10; Review Gate round 1
                // on PR #34). This runs on the preview DRAIN thread, the only reader of the ffmpeg
                // pipe that is writing the recording, and the shared logger is a synchronous file
                // append taken under a process-wide lock - either of which can stop the drain, fill
                // the pipe and block that ffmpeg. The drop is reported where it is safe to report it:
                // PreviewTap logs OversizeDrops from its publisher thread at the end of the stream.
                OversizeDrops++;
                _length = 0;
            }

            return frames;
        }

        /// <summary>Index of the two-byte marker 0xFF <paramref name="second"/> at or after
        /// <paramref name="from"/>, or -1.</summary>
        private int IndexOfMarker(int from, byte second)
        {
            for (int i = Math.Max(0, from); i + 1 < _length; i++)
                if (_buffer[i] == JpegFrame.Soi1 && _buffer[i + 1] == second) return i;
            return -1;
        }

        private void Discard(int bytes)
        {
            if (bytes <= 0) return;
            if (bytes >= _length) { _length = 0; return; }
            Buffer.BlockCopy(_buffer, bytes, _buffer, 0, _length - bytes);
            _length -= bytes;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buffer.Length) return;
            int size = _buffer.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref _buffer, size);
        }
    }
}
