using System;
using System.IO;
using System.Text;
using AgentEyes.Platform.Linux;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class WavStreamWriterTests
    {
        [Fact]
        public void Dispose_AfterWrites_PatchesRiffAndDataSizes()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("wav"), "t.wav");
            using (var w = new WavStreamWriter(path, 48000, 2))
            {
                w.Write(new byte[1000], 1000);
                w.Write(new byte[24], 24);
            }

            byte[] b = File.ReadAllBytes(path);
            Assert.Equal(44 + 1024, b.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(b, 0, 4));
            Assert.Equal(36 + 1024, BitConverter.ToInt32(b, 4));
            Assert.Equal(2, BitConverter.ToInt16(b, 22));          // channels
            Assert.Equal(48000, BitConverter.ToInt32(b, 24));      // rate
            Assert.Equal(48000 * 4, BitConverter.ToInt32(b, 28));  // byte rate
            Assert.Equal(16, BitConverter.ToInt16(b, 34));         // bits
            Assert.Equal("data", Encoding.ASCII.GetString(b, 36, 4));
            Assert.Equal(1024, BitConverter.ToInt32(b, 40));
        }

        [Fact]
        public void Dispose_NoWrites_IsAValidEmptyWav()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("wav"), "e.wav");
            new WavStreamWriter(path, 48000, 1).Dispose();

            byte[] b = File.ReadAllBytes(path);
            Assert.Equal(44, b.Length);
            Assert.Equal(0, BitConverter.ToInt32(b, 40));
        }

        [Fact]
        public void Write_AfterDispose_Throws()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("wav"), "d.wav");
            var w = new WavStreamWriter(path, 48000, 1);
            w.Dispose();
            Assert.Throws<ObjectDisposedException>(() => w.Write(new byte[2], 2));
        }
    }
}
