using System;
using System.IO;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Non-UI capture logic for issue #64: file naming, the save-folder resolver (default Windows
    /// Screenshots known folder vs. configured override), and the size round-trip embedded in the
    /// name. The actual GDI grab is exercised in the running app (proof), not here.
    /// </summary>
    public class CaptureServiceTests
    {
        private static readonly DateTime When = new(2026, 6, 9, 14, 5, 1);

        [Fact]
        public void FileNameFor_EmbedsTimestampAndDimensions()
        {
            string name = CaptureService.FileNameFor(1920, 1080, When);
            Assert.Equal("AgentEyes_2026-06-09_140501_1920x1080.png", name);
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(100, 0)]
        [InlineData(-1, 50)]
        public void FileNameFor_EmptySize_Throws(int w, int h)
        {
            Assert.Throws<UsageException>(() => CaptureService.FileNameFor(w, h, When));
        }

        [Fact]
        public void PathFor_CombinesFolderAndName()
        {
            string folder = Path.Combine(Path.GetTempPath(), "agenteyes-cap");
            string path = CaptureService.PathFor(folder, 800, 600, When);
            Assert.StartsWith(folder, path);
            Assert.EndsWith("AgentEyes_2026-06-09_140501_800x600.png", path);
        }

        // ---- AC9: default save folder = Windows Screenshots known folder --------------

        [Fact]
        public void ScreenshotsKnownFolder_ResolvesToTheWindowsScreenshotsFolder()
        {
            // The resolver must come from SHGetKnownFolderPath(FOLDERID_Screenshots) - an absolute,
            // existing-or-creatable shell path - NOT a hard-coded string and NOT a AgentEyes
            // subfolder. On a redirected setup it ends in OneDrive\...\Screenshots; otherwise in the
            // local Pictures\Screenshots. We assert the durable shape: rooted, ends in "Screenshots".
            string folder = CaptureService.ScreenshotsKnownFolder();
            Assert.False(string.IsNullOrWhiteSpace(folder));
            Assert.True(Path.IsPathRooted(folder), "known folder path must be absolute");
            Assert.Equal("Screenshots", new DirectoryInfo(folder).Name);
            Assert.DoesNotContain("AgentEyes", folder);
        }

        [Fact]
        public void ResolveSaveFolder_NoOverride_IsTheKnownScreenshotsFolder()
        {
            Assert.Equal(CaptureService.ScreenshotsKnownFolder(), CaptureService.ResolveSaveFolder(null));
            Assert.Equal(CaptureService.ScreenshotsKnownFolder(), CaptureService.ResolveSaveFolder(""));
            Assert.Equal(CaptureService.ScreenshotsKnownFolder(), CaptureService.ResolveSaveFolder("   "));
        }

        [Fact]
        public void ResolveSaveFolder_Override_Wins()
        {
            string custom = Path.Combine(Path.GetTempPath(), "agenteyes-custom-captures");
            Assert.Equal(custom, CaptureService.ResolveSaveFolder(custom));
            // Trims surrounding whitespace but otherwise returns the configured path verbatim.
            Assert.Equal(custom, CaptureService.ResolveSaveFolder("  " + custom + "  "));
        }

        // ---- size round-trip ----------------------------------------------------------

        [Theory]
        [InlineData("AgentEyes_2026-06-09_140501_1920x1080.png", 1920, 1080)]
        [InlineData("AgentEyes_2026-06-09_140501_800x600.png", 800, 600)]
        [InlineData("AgentEyes_2026-06-09_140501_2x2.png", 2, 2)]
        public void ParseSize_ReadsDimensionsBack(string name, int w, int h)
        {
            var (pw, ph) = CaptureService.ParseSize(name);
            Assert.Equal(w, pw);
            Assert.Equal(h, ph);
        }

        [Theory]
        [InlineData("hand-renamed.png")]
        [InlineData("capture_no_size_here.png")]
        [InlineData("AgentEyes_2026-06-09_140501_widexhigh.png")]
        public void ParseSize_NoDimensions_ReturnsZero(string name)
        {
            var (w, h) = CaptureService.ParseSize(name);
            Assert.Equal(0, w);
            Assert.Equal(0, h);
        }

        [Fact]
        public void Roundtrip_NameThenParse_PreservesDimensions()
        {
            string name = CaptureService.FileNameFor(1366, 768, When);
            var (w, h) = CaptureService.ParseSize(name);
            Assert.Equal(1366, w);
            Assert.Equal(768, h);
        }

        // ---- delete -------------------------------------------------------------------

        [Fact]
        public void Delete_MissingFile_ReturnsFalse()
        {
            Assert.False(CaptureService.Delete(Path.Combine(Path.GetTempPath(), "no-such-capture-xyz.png")));
        }

        [Fact]
        public void Delete_ExistingFile_RemovesItAndReturnsTrue()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "agenteyes-capture-test-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            Assert.True(CaptureService.Delete(tmp));
            Assert.False(File.Exists(tmp));
        }

        // ---- AC11: single-monitor picker collapse -------------------------------------

        [Theory]
        [InlineData(0, false)]   // no monitors enumerated -> nothing to pick
        [InlineData(1, false)]   // single monitor -> picker collapses
        [InlineData(2, true)]    // multi-monitor -> picker shown
        [InlineData(4, true)]
        public void ShouldShowMonitorPicker_OnlyWhenMoreThanOneMonitor(int count, bool expected)
        {
            Assert.Equal(expected, CaptureService.ShouldShowMonitorPicker(count));
        }
    }
}
