using System.Collections.Generic;
using AgentEyes.Platform.Mac;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class AvfDevicesTests
    {
        // A real listing from a Mac with one display and one microphone, plus a camera, exactly as
        // ffmpeg prints it (the address in the prefix varies run to run, which is why it is parsed
        // rather than matched).
        private const string Listing =
            "[AVFoundation indev @ 0x7f8e1050] AVFoundation video devices:\n"
            + "[AVFoundation indev @ 0x7f8e1050] [0] FaceTime HD Camera\n"
            + "[AVFoundation indev @ 0x7f8e1050] [1] Capture screen 0\n"
            + "[AVFoundation indev @ 0x7f8e1050] AVFoundation audio devices:\n"
            + "[AVFoundation indev @ 0x7f8e1050] [0] MacBook Pro Microphone\n"
            + "[AVFoundation indev @ 0x7f8e1050] [1] BlackHole 2ch\n";

        [Fact]
        public void ParseListing_VideoAndAudioSections_AreSplitByHeader()
        {
            var l = AvfDevices.ParseListing(Listing);

            Assert.Equal(2, l.Video.Count);
            Assert.Equal(2, l.Audio.Count);
            Assert.Equal(new AvfDevice(0, "FaceTime HD Camera"), l.Video[0]);
            Assert.Equal(new AvfDevice(1, "Capture screen 0"), l.Video[1]);
            Assert.Equal(new AvfDevice(0, "MacBook Pro Microphone"), l.Audio[0]);
            Assert.Equal(new AvfDevice(1, "BlackHole 2ch"), l.Audio[1]);
        }

        [Fact]
        public void ParseListing_ScreenIsDistinguishedFromCamera()
        {
            var l = AvfDevices.ParseListing(Listing);

            Assert.False(l.Video[0].IsScreen);
            Assert.True(l.Video[1].IsScreen);
        }

        [Fact]
        public void ParseListing_Empty_IsEmptyNotAnError()
        {
            var l = AvfDevices.ParseListing("");

            Assert.Empty(l.Video);
            Assert.Empty(l.Audio);
        }

        [Fact]
        public void ParseListing_LinesBeforeAnyHeader_AreIgnored()
        {
            // ffmpeg prints its banner and other warnings around the listing; a numbered line that
            // is not under one of the two headers belongs to neither list.
            var l = AvfDevices.ParseListing(
                "[AVFoundation indev @ 0x1] [9] something before any header\n" + Listing);

            Assert.Equal(2, l.Video.Count);
            Assert.DoesNotContain(l.Video, d => d.Name.Contains("before any header"));
        }

        [Fact]
        public void ParseListing_IndexIsFfmpegsOwnNumberNotThePosition()
        {
            // A camera unplugged mid-session leaves a gap: position 0 holds index 1. Addressing by
            // position instead of by the printed index would open the wrong device.
            var l = AvfDevices.ParseListing(
                "[AVFoundation indev @ 0x1] AVFoundation video devices:\n"
                + "[AVFoundation indev @ 0x1] [3] Capture screen 0\n");

            Assert.Equal(3, l.Video[0].Index);
        }

        [Fact]
        public void MicrophoneIndexFor_ExactName_ReturnsFfmpegsIndex()
        {
            var audio = AvfDevices.ParseListing(Listing).Audio;

            Assert.Equal(1, AvfDevices.MicrophoneIndexFor(audio, "BlackHole 2ch"));
        }

        [Fact]
        public void MicrophoneIndexFor_UnknownName_IsNullNotAGuess()
        {
            var audio = AvfDevices.ParseListing(Listing).Audio;

            Assert.Null(AvfDevices.MicrophoneIndexFor(audio, "A microphone that was unplugged"));
        }

        [Fact]
        public void MicrophoneIndexFor_IsCaseSensitive_SoAPartialMatchNeverOpensTheWrongDevice()
        {
            var audio = AvfDevices.ParseListing(Listing).Audio;

            Assert.Null(AvfDevices.MicrophoneIndexFor(audio, "blackhole 2ch"));
        }
    }
}
