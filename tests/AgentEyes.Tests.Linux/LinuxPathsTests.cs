using System;
using System.IO;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class LinuxPathsTests
    {
        [LinuxOnlyFact]
        public void MachineLocalAppData_OnLinux_IsTheXdgDataHome()
        {
            // AppDataPaths' %LOCALAPPDATA% is .NET's LocalApplicationData, which on Linux is
            // $XDG_DATA_HOME or ~/.local/share - the XDG data directory the brief asks for.
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string expected = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".local", "share")
                : xdg;
            Assert.Equal(expected, AppDataPaths.MachineLocalAppData);
        }

        [Fact]
        public void ScreenshotsKnownFolder_OnLinux_IsPicturesScreenshots()
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);
            Assert.False(string.IsNullOrEmpty(pictures));
            Assert.Equal(Path.Combine(pictures, "Screenshots"), CaptureService.ScreenshotsKnownFolder());
        }

        [Fact]
        public void Roots_UnderTheTestRedirect_AreTheRunFolders()
        {
            Assert.True(AppDataPaths.IsRedirected);
            Assert.StartsWith(LinuxTestIsolation.RunRoot, AppDataPaths.Root);
            Assert.StartsWith(LinuxTestIsolation.RunRoot, AppDataPaths.RecordingsRoot);
        }
    }
}
