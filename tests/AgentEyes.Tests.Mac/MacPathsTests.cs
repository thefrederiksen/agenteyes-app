using System;
using System.IO;
using AgentEyes.Platform.Mac;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class MacPathsTests
    {
        [Fact]
        public void MachineLocalAppData_OnMac_IsTheApplicationSupportFolder()
        {
            // The Linux build expects the XDG data directory here. macOS is NOT Linux: .NET maps
            // LocalApplicationData to ~/Library/Application Support, which is the right place on a
            // Mac and the reason the Linux path test fails on one.
            string expected = Path.Combine(
                Environment.GetEnvironmentVariable("HOME")!, "Library", "Application Support");

            Assert.Equal(expected, AppDataPaths.MachineLocalAppData);
        }

        [Fact]
        public void ScreenshotsKnownFolder_OnMac_IsPicturesScreenshots()
        {
            string pictures = Environment.GetFolderPath(
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);

            Assert.False(string.IsNullOrEmpty(pictures));
            Assert.Equal(Path.Combine(pictures, "Screenshots"), CaptureService.ScreenshotsKnownFolder());
        }

        [Fact]
        public void Roots_UnderTheTestRedirect_AreTheRunFolders()
        {
            Assert.True(AppDataPaths.IsRedirected);
            Assert.StartsWith(MacTestIsolation.RunRoot, AppDataPaths.Root);
            Assert.StartsWith(MacTestIsolation.RunRoot, AppDataPaths.RecordingsRoot);
        }
    }
}
