using Xunit;
using AgentEyes.Video;

namespace AgentEyes.Tests
{
    public class FfmpegDevicesTests
    {
        // Classic ffmpeg dshow listing format (name and (audio) on the same line).
        private const string ClassicStderr =
            "[dshow @ 0001] DirectShow video devices\n" +
            "[dshow @ 0001]  \"HD Webcam\" (video)\n" +
            "[dshow @ 0001] DirectShow audio devices\n" +
            "[dshow @ 0001]  \"Microphone (Realtek)\" (audio)\n" +
            "[dshow @ 0001]  \"Microphone (Yeti Stereo)\" (audio)\n";

        // Newer ffmpeg format where (audio) sits on the name line with an Alternative name below.
        private const string NewerStderr =
            "[dshow @ 0001] \"Microphone (Realtek)\" (audio)\n" +
            "[dshow @ 0001]   Alternative name \"@device_cm...\"\n" +
            "[dshow @ 0001] \"Microphone (Yeti Stereo)\" (audio)\n" +
            "[dshow @ 0001]   Alternative name \"@device_cm...\"\n";

        [Fact]
        public void Parses_classic_audio_device_names()
        {
            var names = FfmpegDevices.ParseDshowAudio(ClassicStderr);
            Assert.Equal(2, names.Count);
            Assert.Contains("Microphone (Realtek)", names);
            Assert.Contains("Microphone (Yeti Stereo)", names);
        }

        [Fact]
        public void Does_not_include_video_devices()
        {
            var names = FfmpegDevices.ParseDshowAudio(ClassicStderr);
            Assert.DoesNotContain("HD Webcam", names);
        }

        [Fact]
        public void Parses_newer_format()
        {
            var names = FfmpegDevices.ParseDshowAudio(NewerStderr);
            Assert.Equal(2, names.Count);
            Assert.Contains("Microphone (Yeti Stereo)", names);
        }

        [Fact]
        public void Empty_input_returns_empty()
        {
            Assert.Empty(FfmpegDevices.ParseDshowAudio(""));
        }

        [Fact]
        public void Deduplicates_repeated_names()
        {
            string dup = ClassicStderr + "[dshow @ 0001]  \"Microphone (Yeti Stereo)\" (audio)\n";
            var names = FfmpegDevices.ParseDshowAudio(dup);
            Assert.Equal(2, names.Count);
        }
    }
}
