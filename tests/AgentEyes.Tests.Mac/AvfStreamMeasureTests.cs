using AgentEyes.Platform.Mac;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class AvfStreamMeasureTests
    {
        private static byte[] Samples(params short[] values)
        {
            var b = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                b[i * 2] = (byte)(values[i] & 0xFF);
                b[i * 2 + 1] = (byte)((values[i] >> 8) & 0xFF);
            }
            return b;
        }

        [Fact]
        public void Measure_Silence_IsZeroPeak()
        {
            var level = AvfStream.Measure(Samples(0, 0, 0, 0), 8);

            Assert.Equal(0f, level.Peak);
            Assert.Equal(4, level.Samples);
        }

        [Fact]
        public void Measure_FullScaleNegative_DoesNotOverflow()
        {
            // short.MinValue is a real full-scale sample and Math.Abs on it overflows if the value
            // is not widened first - the bug this test exists to keep out.
            var level = AvfStream.Measure(Samples(short.MinValue), 2);

            Assert.Equal(1f, level.Peak);
        }

        [Fact]
        public void Measure_PeakIsTheLoudestSample()
        {
            var level = AvfStream.Measure(Samples(100, -16384, 50), 6);

            Assert.Equal(0.5f, level.Peak, 3);
        }

        [Fact]
        public void Measure_CountHonoursTheValidByteCount_NotTheBufferLength()
        {
            // The reader hands a reused buffer and how many of its bytes are valid; measuring the
            // whole buffer would mix in the previous block's tail.
            var buf = Samples(16384, 32767, 32767, 32767);
            var level = AvfStream.Measure(buf, 2);

            Assert.Equal(1, level.Samples);
            Assert.Equal(0.5f, level.Peak, 3);
        }
    }
}
