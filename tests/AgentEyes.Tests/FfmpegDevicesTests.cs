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

        // ---- video / camera devices (issue #28) ------------------------------
        //
        // WHAT THESE CAN AND CANNOT SEE: they pin the PARSER against the two ffmpeg listing layouts
        // that exist in the wild. They say nothing about whether ffmpeg is installed or whether a
        // camera is attached to this machine - ListVideo() is the part that needs hardware, and it is
        // exercised by the running-app proof, not here.

        // Two cameras plus an audio section, classic layout.
        private const string TwoCameraStderr =
            "[dshow @ 0001] DirectShow video devices\n" +
            "[dshow @ 0001]  \"HD Webcam\" (video)\n" +
            "[dshow @ 0001]  \"Elgato Cam Link\" (video)\n" +
            "[dshow @ 0001] DirectShow audio devices\n" +
            "[dshow @ 0001]  \"Microphone (Realtek)\" (audio)\n";

        // Newer ffmpeg layout: the marker sits on the name line, Alternative name below.
        private const string NewerVideoStderr =
            "[dshow @ 0001] \"HD Webcam\" (video)\n" +
            "[dshow @ 0001]   Alternative name \"@device_pnp...\"\n" +
            "[dshow @ 0001] \"Elgato Cam Link\" (video)\n" +
            "[dshow @ 0001]   Alternative name \"@device_pnp...\"\n";

        [Fact]
        public void ParseDshowVideo_ClassicListing_ReturnsTheCameraNames()
        {
            var names = FfmpegDevices.ParseDshowVideo(TwoCameraStderr);
            Assert.Equal(2, names.Count);
            Assert.Contains("HD Webcam", names);
            Assert.Contains("Elgato Cam Link", names);
        }

        [Fact]
        public void ParseDshowVideo_ClassicListing_ExcludesAudioDevices()
        {
            var names = FfmpegDevices.ParseDshowVideo(TwoCameraStderr);
            Assert.DoesNotContain("Microphone (Realtek)", names);
        }

        [Fact]
        public void ParseDshowVideo_NewerListingFormat_ReturnsTheCameraNames()
        {
            var names = FfmpegDevices.ParseDshowVideo(NewerVideoStderr);
            Assert.Equal(2, names.Count);
            Assert.Contains("Elgato Cam Link", names);
        }

        [Fact]
        public void ParseDshowVideo_EmptyInput_ReturnsEmpty()
        {
            Assert.Empty(FfmpegDevices.ParseDshowVideo(""));
        }

        [Fact]
        public void ParseDshowVideo_RepeatedName_IsListedOnce()
        {
            string dup = TwoCameraStderr + "[dshow @ 0001]  \"HD Webcam\" (video)\n";
            Assert.Equal(2, FfmpegDevices.ParseDshowVideo(dup).Count);
        }

        [Fact]
        public void ParseDshowVideo_ListingWithNoVideoSection_ReturnsEmpty()
        {
            // "No cameras" is a fact about the machine (AC1: still 200, cameras []), so the parser
            // states it as an empty list rather than throwing or spilling the audio devices into it.
            Assert.Empty(FfmpegDevices.ParseDshowVideo(NewerStderr));
        }

        [Fact]
        public void ParseDshow_OneListing_SplitsCamerasAndMicrophonesCleanly()
        {
            // The fail-open trap this closes: a video parser that matched EVERY quoted name would
            // pass all the tests above (both cameras really are in its output) while silently
            // offering the microphone as a camera. Asserting both sides of the SAME listing, by exact
            // sequence, is what catches that.
            var cameras = FfmpegDevices.ParseDshowVideo(TwoCameraStderr);
            var mics = FfmpegDevices.ParseDshowAudio(TwoCameraStderr);

            Assert.Equal(new[] { "HD Webcam", "Elgato Cam Link" }, cameras);
            Assert.Equal(new[] { "Microphone (Realtek)" }, mics);
            foreach (var mic in mics) Assert.DoesNotContain(mic, cameras);
        }
    }
}
