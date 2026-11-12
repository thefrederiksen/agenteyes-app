using System;
using System.Collections.Generic;
using AgentEyes;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    /// <summary>
    /// Issue #111: what a macOS failure CALLS the capture API it could not find a device in.
    ///
    /// The resolver is shared by all three platforms and hardcoded the word "DirectShow", so a Mac
    /// that could not match "--mic Yealink" told its owner the name "may not match a DirectShow
    /// device" - a device class macOS has never had, pointing every reader at the device name while
    /// the real fault was in our own argument list. These assertions are the macOS half of that
    /// wording; the Windows half (the same messages still saying DirectShow) is asserted by
    /// DeviceResolverTests and FfmpegRecorderTests in the Windows suite, which is where a Windows
    /// build is compiled. This file CANNOT see the Windows wording, by construction - it runs against
    /// the macOS configuration of the engine.
    /// </summary>
    public sealed class MacDeviceWordingTests
    {
        private static readonly List<string> TwoMics = new()
        {
            "HD Pro Webcam C920", "Yealink UH36",
        };

        [Fact]
        public void ResolveName_NoMatchOnMac_NamesAvFoundationNotDirectShow()
        {
            var ex = Assert.Throws<UsageException>(() => DeviceResolver.ResolveName(TwoMics, "no-such-mic"));

            Assert.Contains("AVFoundation", ex.Message);
            Assert.DoesNotContain("DirectShow", ex.Message);
            Assert.Contains("no-such-mic", ex.Message);
        }

        [Fact]
        public void ResolveName_AmbiguousOnMac_NamesAvFoundationNotDirectShow()
        {
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveName(new List<string> { "Yealink UH36", "Yealink UH38" }, "Yealink"));

            Assert.Contains("AVFoundation", ex.Message);
            Assert.DoesNotContain("DirectShow", ex.Message);
        }

        [Fact]
        public void ResolveName_NoDevicesAtAllOnMac_NamesAvFoundationNotDirectShow()
        {
            var ex = Assert.Throws<UsageException>(() => DeviceResolver.ResolveName(new List<string>(), "any"));

            Assert.Contains("AVFoundation", ex.Message);
            Assert.DoesNotContain("DirectShow", ex.Message);
        }

        [Fact]
        public void ResolveCameraName_NoMatchOnMac_NamesAvFoundationNotDirectShow()
        {
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveCameraName(new List<string> { "FaceTime HD Camera" }, "brio"));

            Assert.Contains("AVFoundation", ex.Message);
            Assert.DoesNotContain("DirectShow", ex.Message);
        }

        [Fact]
        public void ResolveCameraName_NoCamerasAtAllOnMac_NamesAvFoundationNotDirectShow()
        {
            var ex = Assert.Throws<UsageException>(
                () => DeviceResolver.ResolveCameraName(new List<string>(), "brio"));

            Assert.Contains("AVFoundation", ex.Message);
            Assert.DoesNotContain("DirectShow", ex.Message);
        }

        [Fact]
        public void DiagnoseImmediateExit_MicBlamedOnMac_NamesAvFoundationNotDirectShow()
        {
            string cause = FfmpegRecorder.DiagnoseImmediateExit("could not open input", "Yealink UH36");

            Assert.Contains("AVFoundation", cause);
            Assert.DoesNotContain("DirectShow", cause);
        }

        [Fact]
        public void DiagnoseImmediateExit_RejectedOption_BlamesTheCommandLineNotTheDevice()
        {
            // The exact stderr issue #111 produced. Blaming the microphone here is what sent the
            // reader after the device name instead of the argument list.
            const string stderr = "Unrecognized option 'audio_buffer_size'.\n"
                + "Error splitting the argument list: Option not found\n";

            string cause = FfmpegRecorder.DiagnoseImmediateExit(stderr, "Yealink UH36");

            Assert.Contains("option", cause, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("microphone", cause);
            Assert.DoesNotContain("Yealink", cause);
        }
    }
}
