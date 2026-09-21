using AgentEyes.Audio;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #64: the kept microphone recording is captured at full quality (48 kHz), and the
    /// transcriber keeps getting its own 16 kHz mono copy. The two rates are pinned separately so
    /// neither can quietly follow the other: lowering the capture again would ruin narration, and
    /// raising the transcriber input would triple every upload to DevThrottle.
    /// </summary>
    public class AudioCaptureFormatTests
    {
        [Fact]
        public void CaptureFormat_MicRecording_Is48kHz16BitMono()
        {
            var format = AudioCapture.CaptureFormat;

            Assert.Equal(48000, format.SampleRate);
            Assert.Equal(16, format.BitsPerSample);
            Assert.Equal(1, format.Channels);
        }

        [Fact]
        public void ExtractWav_TranscriberInput_Stays16kHzMono()
        {
            var args = FfmpegArgs.ExtractWav("audio.wav", "audio_16k.wav");

            Assert.Equal("16000", args[args.IndexOf("-ar") + 1]);
            Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
            Assert.Equal("audio_16k.wav", args[^1]);
        }
    }
}
