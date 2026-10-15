using System.Linq;
using AgentEyes.Platform.Linux;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class PulseDevicesTests
    {
        private const string Sources =
            "Auto-detected sources for pulse:\n" +
            "  alsa_output.usb-Yealink_UH36-00.analog-stereo.monitor [Monitor of Yealink UH36 Analog Stereo]\n" +
            "  alsa_input.usb-Yealink_UH36-00.analog-stereo [Yealink UH36 Analog Stereo]\n" +
            "* alsa_input.usb-046d_HD_Pro_Webcam_C920-02.analog-stereo [HD Pro Webcam C920 Analog Stereo]\n";

        private const string Sinks =
            "Auto-detected sinks for pulse:\n" +
            "  alsa_output.pci-0000_0a_00.6.iec958-stereo [Family 17h HD Audio Controller Digital Stereo (IEC958)]\n" +
            "* alsa_output.usb-Yealink_UH36-00.analog-stereo [Yealink UH36 Analog Stereo]\n";

        [Fact]
        public void ParseListing_Sources_ReadsNameDescriptionAndDefault()
        {
            var list = PulseDevices.ParseListing(Sources);

            Assert.Equal(3, list.Count);
            Assert.True(list[0].IsMonitor);
            Assert.Equal("Yealink UH36 Analog Stereo", list[1].Description);
            Assert.Equal("alsa_input.usb-Yealink_UH36-00.analog-stereo", list[1].Name);
            Assert.False(list[1].IsDefault);
            Assert.True(list[2].IsDefault);
        }

        [Fact]
        public void ParseListing_HeaderAndBlankLines_AreIgnored()
        {
            Assert.Empty(PulseDevices.ParseListing("Auto-detected sources for pulse:\n\n"));
        }

        [Fact]
        public void SystemAudioSource_DefaultSinkWithMonitor_ReturnsItsMonitor()
        {
            var monitor = PulseDevices.SystemAudioSource(
                PulseDevices.ParseListing(Sinks), PulseDevices.ParseListing(Sources), out string why);

            Assert.NotNull(monitor);
            Assert.Equal("alsa_output.usb-Yealink_UH36-00.analog-stereo.monitor", monitor!.Name);
            Assert.Equal("", why);
        }

        [Fact]
        public void SystemAudioSource_NoDefaultSink_ReturnsNullWithReason()
        {
            var sinks = PulseDevices.ParseListing("  alsa_output.a [A]\n");
            var monitor = PulseDevices.SystemAudioSource(sinks, PulseDevices.ParseListing(Sources), out string why);

            Assert.Null(monitor);
            Assert.Contains("no output as the default", why);
        }

        [Fact]
        public void SystemAudioSource_DefaultSinkWithoutMonitor_ReturnsNullWithReason()
        {
            var sinks = PulseDevices.ParseListing("* alsa_output.other [Other]\n");
            var monitor = PulseDevices.SystemAudioSource(sinks, PulseDevices.ParseListing(Sources), out string why);

            Assert.Null(monitor);
            Assert.Contains("alsa_output.other.monitor", why);
        }

        [Fact]
        public void SourceNameFor_ExactDescription_ReturnsPulseName()
        {
            var mics = PulseDevices.ParseListing(Sources).Where(s => !s.IsMonitor).ToList();

            Assert.Equal("alsa_input.usb-Yealink_UH36-00.analog-stereo", PulseDevices.SourceNameFor(mics, "Yealink UH36 Analog Stereo"));
            Assert.Null(PulseDevices.SourceNameFor(mics, "Yealink"));
        }

        [Fact]
        public void DeviceResolver_FragmentOverDescriptions_PicksTheOneMicrophone()
        {
            var names = PulseDevices.ParseListing(Sources).Where(s => !s.IsMonitor).Select(s => s.Description).ToList();

            Assert.Equal("Yealink UH36 Analog Stereo", DeviceResolver.ResolveName(names, "yealink"));
        }
    }
}
