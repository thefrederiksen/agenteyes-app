using System.Collections.Generic;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class DeviceResolverTests
    {
        private static List<(int, string)> Devices() => new()
        {
            (0, "Microphone (Realtek High Definition Audio)"),
            (1, "Microphone (Yeti Stereo Microphone)"),
            (2, "Line In (HD Webcam)"),
        };

        [Fact]
        public void Resolves_unique_substring_to_number()
        {
            Assert.Equal(1, DeviceResolver.Resolve(Devices(), "Yeti"));
        }

        [Fact]
        public void Match_is_case_insensitive()
        {
            Assert.Equal(1, DeviceResolver.Resolve(Devices(), "yeti"));
        }

        [Fact]
        public void No_match_throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(Devices(), "Nonexistent"));
        }

        [Fact]
        public void Ambiguous_match_throws()
        {
            // "Microphone" matches devices 0 and 1.
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(Devices(), "Microphone"));
        }

        [Fact]
        public void Empty_device_list_throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(new List<(int, string)>(), "x"));
        }

        [Fact]
        public void Empty_fragment_throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.Resolve(Devices(), "  "));
        }

        [Fact]
        public void ResolveName_returns_matching_name()
        {
            var names = new List<string> { "Microphone (Yeti Stereo Microphone)", "Line In" };
            Assert.Equal("Microphone (Yeti Stereo Microphone)", DeviceResolver.ResolveName(names, "Yeti"));
        }

        [Fact]
        public void ResolveName_ambiguous_throws()
        {
            var names = new List<string> { "Mic A", "Mic B" };
            Assert.Throws<UsageException>(() => DeviceResolver.ResolveName(names, "Mic"));
        }

        // ---- the API a Windows failure names (issue #111) --------------------
        //
        // The resolver is shared by all three platforms and used to hardcode "DirectShow", which on
        // a Mac sent its owner after a device class macOS has never had. The word now comes from
        // PlatformApiNames, and these are the assertions that the WINDOWS branch of it still says
        // DirectShow: without them, editing that branch - or a TFM change that stopped defining
        // WINDOWS, which would compile the PulseAudio/V4L2 branch into the Windows build - would
        // silently reword every Windows microphone and camera error with the whole gate green. The
        // macOS half is MacDeviceWordingTests, which only a macOS build compiles.

        [Fact]
        public void Windows_wording_no_mic_match_names_DirectShow()
        {
            var names = new List<string> { "Microphone (Yeti Stereo Microphone)" };

            var ex = Assert.Throws<UsageException>(() => DeviceResolver.ResolveName(names, "no-such-mic"));

            Assert.Contains("DirectShow", ex.Message);
            Assert.Contains("no-such-mic", ex.Message);
        }

        [Fact]
        public void Windows_wording_ambiguous_mic_names_DirectShow()
        {
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveName(new List<string> { "Mic A", "Mic B" }, "Mic"));

            Assert.Contains("DirectShow", ex.Message);
        }

        [Fact]
        public void Windows_wording_no_audio_devices_at_all_names_DirectShow()
        {
            var ex = Assert.Throws<UsageException>(() => DeviceResolver.ResolveName(new List<string>(), "any"));

            Assert.Contains("DirectShow", ex.Message);
        }

        [Fact]
        public void Windows_wording_no_camera_match_names_DirectShow()
        {
            var ex = Assert.Throws<UsageException>(() => DeviceResolver.ResolveCameraName(Cameras(), "no-such-camera"));

            Assert.Contains("DirectShow", ex.Message);
        }

        [Fact]
        public void Windows_wording_no_cameras_at_all_names_DirectShow()
        {
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveCameraName(new List<string>(), "any"));

            Assert.Contains("DirectShow", ex.Message);
        }

        [Fact]
        public void Windows_wording_ambiguous_camera_names_DirectShow()
        {
            var ex = Assert.Throws<UsageException>(() => DeviceResolver.ResolveCameraName(Cameras(), "Logitech"));

            Assert.Contains("DirectShow", ex.Message);
        }

        // ---- camera resolution (issue #28) -----------------------------------

        private static List<string> Cameras() => new()
        {
            "HD Webcam",
            "Logitech BRIO 4K",
            "Logitech StreamCam",
        };

        [Fact]
        public void ResolveCameraName_UniqueFragment_ReturnsTheExactDeviceName()
        {
            Assert.Equal("HD Webcam", DeviceResolver.ResolveCameraName(Cameras(), "Webcam"));
        }

        [Fact]
        public void ResolveCameraName_FragmentIsCaseInsensitive()
        {
            Assert.Equal("Logitech BRIO 4K", DeviceResolver.ResolveCameraName(Cameras(), "brio"));
        }

        [Fact]
        public void ResolveCameraName_NoMatch_ThrowsNamingTheFragment()
        {
            // AC8: the error has to name what the user asked for - that is the thing they change.
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveCameraName(Cameras(), "no-such-device"));
            Assert.Contains("no-such-device", ex.Message);
        }

        [Fact]
        public void ResolveCameraName_AmbiguousFragment_ThrowsRatherThanPickingOne()
        {
            // "Logitech" matches two cameras. There is deliberately no "take the first" path: a
            // recording that quietly filmed the wrong lens is worse than one that refused to start.
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveCameraName(Cameras(), "Logitech"));
            Assert.Contains("Logitech", ex.Message);
            Assert.Contains("2", ex.Message);
        }

        [Fact]
        public void ResolveCameraName_NoCamerasOnTheMachine_ThrowsNamingTheFragment()
        {
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveCameraName(new List<string>(), "Webcam"));
            Assert.Contains("Webcam", ex.Message);
        }

        [Fact]
        public void ResolveCameraName_EmptyFragment_Throws()
        {
            Assert.Throws<UsageException>(() => DeviceResolver.ResolveCameraName(Cameras(), "  "));
        }

        [Fact]
        public void ResolveCameraName_ExactFullName_ResolvesToItself()
        {
            // What the preset stores is the EXACT device name, so the round trip through the resolver
            // must survive a name that is also a prefix of nothing else.
            Assert.Equal("Logitech StreamCam", DeviceResolver.ResolveCameraName(Cameras(), "Logitech StreamCam"));
        }
    }
}
