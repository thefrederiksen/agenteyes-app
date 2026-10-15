using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes.LinuxApp;
using AgentEyes.LinuxApp.Model;
using AgentEyes.LinuxApp.Views;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    /// <summary>Issue #107: the Linux app's toolkit-free logic - the library rules it shares with the
    /// Windows app, the capture setup, the launch commands and the desktop entries.</summary>
    public sealed class LinuxAppTests
    {
        private static string NewRecording(string root, string id, string manifestJson, params string[] files)
        {
            string dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);
            foreach (var f in files)
            {
                string path = Path.Combine(dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }
            return dir;
        }

        [Fact]
        public void From_VideoWithDisplayNameAndTitle_UsesDisplayNameAndVidBadge()
        {
            string root = LinuxTestIsolation.NewTempDir("app");
            string dir = NewRecording(root, "2026-10-15_120000_video",
                "{\"Mode\":\"video\",\"MonitorIndex\":1,\"Title\":\"Generated\",\"DisplayName\":\"Renamed\",\"DurationSeconds\":125.4,"
                + "\"CreatedUtc\":\"2026-10-15T16:00:00Z\",\"Description\":\"A demo\"}", "recording.mp4");

            var e = LibraryEntry.From(dir);

            Assert.Equal("Renamed", e.Title);
            Assert.Equal("VID", e.Badge);
            Assert.Equal("2:05", e.Duration);
            Assert.Equal(MediaKind.Video, e.Media);
            Assert.EndsWith("   -   A demo", e.Detail);
            Assert.Equal(new DateTime(2026, 10, 15, 16, 0, 0, DateTimeKind.Utc), e.StartedUtc);
        }

        [Fact]
        public void From_VideoWithoutTitle_IsNamedAfterTheMonitorAndHasNoMediaWhenTheFileIsMissing()
        {
            string root = LinuxTestIsolation.NewTempDir("app");
            string dir = NewRecording(root, "2026-10-15_120001_video", "{\"Mode\":\"video\",\"MonitorIndex\":2}");

            var e = LibraryEntry.From(dir);

            Assert.Equal("Monitor 2", e.Title);
            Assert.False(e.CanPreview);
            Assert.Equal("Undated", e.DateText);
            Assert.Null(e.StartedUtc);
        }

        [Fact]
        public void From_Audio_IsTitledByTheMicAndBadgedAud()
        {
            string root = LinuxTestIsolation.NewTempDir("app");
            string dir = NewRecording(root, "2026-10-15_120002_audio",
                "{\"Mode\":\"audio\",\"Microphone\":\"Yealink UH36 Analog Stereo + (system)\"}", "audio.wav");

            var e = LibraryEntry.From(dir);

            Assert.Equal("AUD", e.Badge);
            Assert.Equal("Yealink UH36 Analog Stereo", e.Title);
            Assert.Equal(MediaKind.Audio, e.Media);
        }

        [Fact]
        public void Scan_MixedDates_NewestFirstAndUndatedLast()
        {
            string root = LinuxTestIsolation.NewTempDir("app");
            NewRecording(root, "a_video", "{\"Mode\":\"video\",\"CreatedUtc\":\"2026-10-01T10:00:00Z\"}");
            NewRecording(root, "b_video", "{\"Mode\":\"video\"}");
            NewRecording(root, "c_video", "{\"Mode\":\"video\",\"CreatedUtc\":\"2026-10-15T10:00:00Z\"}");
            Directory.CreateDirectory(Path.Combine(root, "not-a-recording"));

            var ids = LibraryScanner.Scan(root).Select(e => e.Id).ToList();

            Assert.Equal(new[] { "c_video", "a_video", "b_video" }, ids);
        }

        [Fact]
        public void Scan_UnreadableManifest_IsListedNotDropped()
        {
            string root = LinuxTestIsolation.NewTempDir("app");
            NewRecording(root, "broken_video", "{ this is not json");

            var list = LibraryScanner.Scan(root);

            Assert.Single(list);
            Assert.Equal("broken_video", list[0].Title);
        }

        [Theory]
        [InlineData("Yealink UH36 Analog Stereo + (system)", "Yealink UH36 Analog Stereo")]
        [InlineData("Microphone (Yealink UH36)", "Yealink UH36")]
        [InlineData("(system)", "")]
        [InlineData(null, "")]
        public void MicName_LinuxAndWindowsShapes_ReturnsTheMicrophone(string? raw, string expected)
        {
            Assert.Equal(expected, LibraryEntry.MicName(raw));
        }

        [Theory]
        [InlineData(0.0123, false, 0, "$0.0123")]
        [InlineData(0.01, true, 0, "~$0.0100 est")]
        [InlineData(0, false, 11887, "11887 tokens")]
        [InlineData(0, false, 0, "")]
        public void FormatCost_AsTheWindowsCard_Formats(double usd, bool estimate, int tokens, string expected)
        {
            Assert.Equal(expected, LibraryEntry.FormatCost(usd, estimate, tokens));
        }

        [Theory]
        [InlineData("video", 0, "-")]
        [InlineData("shot", 30, "-")]
        [InlineData("video", 3905.9, "65:05")]
        public void FormatDuration_ModesAndLengths_Formats(string mode, double seconds, string expected)
        {
            Assert.Equal(expected, LibraryEntry.FormatDuration(mode, seconds));
        }

        [Theory]
        [InlineData("Yealink", true, "mixed")]
        [InlineData("Yealink", false, "mic")]
        [InlineData("", true, "system")]
        [InlineData(null, false, "none")]
        public void SourceFor_MicAndSystemChoices_MapsToTheControlApiSource(string? mic, bool system, string expected)
        {
            Assert.Equal(expected, AppSettings.SourceFor(mic, system));
        }

        [Theory]
        [InlineData(new string[0], "show")]
        [InlineData(new[] { "--tray" }, "tray")]
        [InlineData(new[] { "--view", "Library" }, "view:library")]
        [InlineData(new[] { "--details", "2026-10-15_120000_video" }, "details:2026-10-15_120000_video")]
        [InlineData(new[] { "--settings", "ACCOUNT" }, "settings:account")]
        [InlineData(new[] { "--snap", "/tmp/x" }, "snap:/tmp/x")]
        public void CommandFrom_LaunchArguments_ReturnsTheCommand(string[] args, string expected)
        {
            Assert.Equal(expected, AgentEyes.LinuxApp.Program.CommandFrom(args));
        }

        [Fact]
        public void DesktopEntry_Tray_StartsInTheTrayAndIsAutostartEnabled()
        {
            string text = Autostart.DesktopEntry("/opt/a/AgentEyesLinux", "/opt/a/agenteyes.png", tray: true);

            Assert.Contains("Exec=/opt/a/AgentEyesLinux --tray\n", text);
            Assert.Contains("X-GNOME-Autostart-enabled=true", text);
            Assert.Contains("StartupWMClass=AgentEyesLinux", text);
        }

        [Fact]
        public void DesktopEntry_Launcher_HasNoTrayFlag()
        {
            string text = Autostart.DesktopEntry("/opt/a/AgentEyesLinux", "/opt/a/agenteyes.png", tray: false);

            Assert.Contains("Exec=/opt/a/AgentEyesLinux\n", text);
            Assert.DoesNotContain("Autostart", text);
        }

        [Fact]
        public void AppSettings_SaveThenLoad_RoundTrips()
        {
            var s = new AppSettings { Screen = 2, Mic = "Yealink UH36 Analog Stereo", SystemSound = false, LibraryMode = "list" };

            s.Save();
            var back = AppSettings.Load();

            Assert.Equal(2, back.Screen);
            Assert.Equal("Yealink UH36 Analog Stereo", back.Mic);
            Assert.False(back.SystemSound);
            Assert.Equal("list", back.LibraryMode);
        }

        [Theory]
        [InlineData("dt_live_abcdefgh7I14", "dt_live_...7I14")]
        [InlineData("", "")]
        public void MaskKey_DevThrottleKey_ShowsPrefixAndTail(string key, string expected)
        {
            Assert.Equal(expected, SettingsWindow.MaskKey(key));
        }

        [Theory]
        [InlineData(65, "01:05")]
        [InlineData(3725, "1:02:05")]
        public void Elapsed_Seconds_FormatsLikeTheRecordTimer(double seconds, string expected)
        {
            Assert.Equal(expected, TrayController.Elapsed(seconds));
        }

        [Theory]
        [InlineData(4000, "0:04")]
        [InlineData(3_725_000, "1:02:05")]
        public void Fmt_Milliseconds_FormatsLikeThePlayer(long ms, string expected)
        {
            Assert.Equal(expected, PlayerWindow.Fmt(ms));
        }
    }
}
