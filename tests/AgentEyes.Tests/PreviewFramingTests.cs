using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33 - the frame transport: whether a run of bytes is a WHOLE JPEG, and how a byte STREAM
    /// is cut back into whole frames.
    ///
    /// Everything here is asked as a PRESENCE. "Is this a frame?" is answered by both JPEG markers
    /// being there, never by "decoding did not throw" - a truncated JPEG decodes to a half-drawn
    /// picture without complaint, so an absence-shaped check would certify frames that are not there.
    /// </summary>
    public class PreviewFramingTests
    {
        /// <summary>A minimal but genuinely whole JPEG: SOI, some bytes, EOI.</summary>
        private static byte[] Frame(byte marker, int bodyBytes = 6)
        {
            var f = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
            for (int i = 0; i < bodyBytes; i++) f.Add(marker);
            f.Add(0xFF);
            f.Add(0xD9);
            return f.ToArray();
        }

        // ---- JpegFrame -----------------------------------------------------

        [Fact]
        public void IsComplete_WholeJpeg_True() => Assert.True(JpegFrame.IsComplete(Frame(0x11)));

        [Fact]
        public void IsComplete_TruncatedJpeg_False()
        {
            var whole = Frame(0x11);
            var truncated = whole.Take(whole.Length - 2).ToArray();   // the EOI is gone
            Assert.False(JpegFrame.IsComplete(truncated));
        }

        [Fact]
        public void IsComplete_MissingStartMarker_False()
        {
            var whole = Frame(0x11);
            var headless = whole.Skip(2).ToArray();
            Assert.False(JpegFrame.IsComplete(headless));
        }

        [Fact]
        public void IsComplete_Null_False() => Assert.False(JpegFrame.IsComplete(null));

        [Fact]
        public void IsComplete_Empty_False() => Assert.False(JpegFrame.IsComplete(Array.Empty<byte>()));

        [Fact]
        public void IsComplete_CountBeyondTheBuffer_False() =>
            Assert.False(JpegFrame.IsComplete(Frame(0x11), 9999));

        [Fact]
        public void IsComplete_CountShorterThanTheBuffer_JudgesOnlyThatPrefix()
        {
            // The buffer holds a whole frame followed by the start of the next one. Judged over the
            // whole buffer it is NOT complete; judged over the first frame's length it is.
            var whole = Frame(0x11);
            var buffer = whole.Concat(new byte[] { 0xFF, 0xD8, 0x01 }).ToArray();
            Assert.False(JpegFrame.IsComplete(buffer));
            Assert.True(JpegFrame.IsComplete(buffer, whole.Length));
        }

        // ---- MjpegFramer ---------------------------------------------------

        [Fact]
        public void Append_WholeFrameInOneChunk_EmitsIt()
        {
            var framer = new MjpegFramer();
            var frame = Frame(0x11);

            var got = framer.Append(frame, frame.Length);

            Assert.Single(got);
            Assert.Equal(frame, got[0]);
            Assert.True(JpegFrame.IsComplete(got[0]));
        }

        [Fact]
        public void Append_TwoFramesInOneChunk_EmitsBothInOrder()
        {
            var framer = new MjpegFramer();
            var a = Frame(0x11);
            var b = Frame(0x22);
            var chunk = a.Concat(b).ToArray();

            var got = framer.Append(chunk, chunk.Length);

            Assert.Equal(2, got.Count);
            Assert.Equal(a, got[0]);
            Assert.Equal(b, got[1]);
        }

        [Fact]
        public void Append_FrameSplitAcrossChunks_EmitsNothingUntilItIsWhole()
        {
            var framer = new MjpegFramer();
            var frame = Frame(0x11);
            var head = frame.Take(5).ToArray();
            var tail = frame.Skip(5).ToArray();

            Assert.Empty(framer.Append(head, head.Length));
            Assert.True(framer.PendingBytes > 0);

            var got = framer.Append(tail, tail.Length);

            Assert.Single(got);
            Assert.Equal(frame, got[0]);
        }

        [Fact]
        public void Append_StartMarkerSplitAcrossChunks_StillFindsTheFrame()
        {
            // The 0xFF of the SOI lands at the end of one read and the 0xD8 at the start of the next.
            // A framer that dropped everything it could not yet interpret would lose every frame.
            var framer = new MjpegFramer();
            var frame = Frame(0x11);

            Assert.Empty(framer.Append(new byte[] { 0xFF }, 1));
            var rest = frame.Skip(1).ToArray();
            var got = framer.Append(rest, rest.Length);

            Assert.Single(got);
            Assert.Equal(frame, got[0]);
        }

        [Fact]
        public void Append_BytesBeforeTheFirstFrame_AreDiscarded()
        {
            // Attaching mid-stream: the first bytes are the tail of a frame whose start was never
            // seen. They belong to no frame and must not be prepended to the next one.
            var framer = new MjpegFramer();
            var frame = Frame(0x11);
            var chunk = new byte[] { 0x01, 0x02, 0x03 }.Concat(frame).ToArray();

            var got = framer.Append(chunk, chunk.Length);

            Assert.Single(got);
            Assert.Equal(frame, got[0]);
        }

        [Fact]
        public void Append_PartialFrame_EmitsNothing()
        {
            var framer = new MjpegFramer();
            var frame = Frame(0x11);
            var head = frame.Take(frame.Length - 1).ToArray();   // everything but the final 0xD9

            Assert.Empty(framer.Append(head, head.Length));
        }

        [Fact]
        public void Append_EmptyChunk_EmitsNothing()
        {
            var framer = new MjpegFramer();
            Assert.Empty(framer.Append(new byte[8], 0));
        }

        [Fact]
        public void Append_FrameThatNeverEnds_IsDroppedAtTheCeilingAndCounted()
        {
            // A producer that emits a start marker and then never an end marker would otherwise grow
            // this buffer for the length of the recording. The drop is COUNTED rather than absorbed.
            var framer = new MjpegFramer(maxFrameBytes: 64);
            var runaway = new byte[] { 0xFF, 0xD8 }.Concat(Enumerable.Repeat((byte)0x41, 200)).ToArray();

            Assert.Empty(framer.Append(runaway, runaway.Length));
            Assert.Equal(1, framer.OversizeDrops);
            Assert.Equal(0, framer.PendingBytes);

            // ...and it resynchronises on the next whole frame rather than staying broken.
            var frame = Frame(0x33);
            var got = framer.Append(frame, frame.Length);
            Assert.Single(got);
            Assert.Equal(frame, got[0]);
        }

        [Fact]
        public void Append_ByteAtATime_StillProducesExactlyTheFramesPutIn()
        {
            // The worst split a pipe can hand us. Whole frames out, in order, with nothing invented.
            var framer = new MjpegFramer();
            var a = Frame(0x11, bodyBytes: 3);
            var b = Frame(0x22, bodyBytes: 9);
            var stream = a.Concat(b).ToArray();

            var got = new List<byte[]>();
            foreach (byte t in stream) got.AddRange(framer.Append(new[] { t }, 1));

            Assert.Equal(2, got.Count);
            Assert.Equal(a, got[0]);
            Assert.Equal(b, got[1]);
        }

        [Fact]
        public void Append_CountOutsideTheChunk_Throws() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new MjpegFramer().Append(new byte[4], 5));
    }
}
