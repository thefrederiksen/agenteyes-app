using AgentEyes.Platform.Mac;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    public sealed class MacDisplayTests
    {
        // Trimmed from the real output of 'system_profiler SPDisplaysDataType -json' on an Apple
        // silicon Mac. The keys with a leading underscore are system_profiler's own naming.
        private const string OneDisplay = @"{
          ""SPDisplaysDataType"" : [
            {
              ""_name"" : ""Apple M4"",
              ""spdisplays_ndrvs"" : [
                {
                  ""_name"" : ""spdisplays_display"",
                  ""_spdisplays_pixels"" : ""1920 x 1080"",
                  ""spdisplays_main"" : ""spdisplays_yes"",
                  ""spdisplays_online"" : ""spdisplays_yes""
                }
              ]
            }
          ]
        }";

        [Fact]
        public void ParseSystemProfiler_OneDisplay_ReadsPixelsAndMain()
        {
            var screens = MacDisplay.ParseSystemProfiler(OneDisplay);

            Assert.Single(screens);
            Assert.Equal(1920, screens[0].Width);
            Assert.Equal(1080, screens[0].Height);
            Assert.True(screens[0].Primary);
        }

        [Fact]
        public void ParseSystemProfiler_TwoDisplays_KeepsSystemProfilersOrder()
        {
            var screens = MacDisplay.ParseSystemProfiler(@"{
              ""SPDisplaysDataType"" : [ { ""spdisplays_ndrvs"" : [
                { ""_spdisplays_pixels"" : ""3840 x 2160"", ""spdisplays_main"" : ""spdisplays_yes"", ""spdisplays_online"" : ""spdisplays_yes"" },
                { ""_spdisplays_pixels"" : ""1920 x 1080"", ""spdisplays_main"" : ""spdisplays_no"",  ""spdisplays_online"" : ""spdisplays_yes"" }
              ] } ] }");

            Assert.Equal(2, screens.Count);
            Assert.Equal(3840, screens[0].Width);
            Assert.True(screens[0].Primary);
            Assert.Equal(1920, screens[1].Width);
            Assert.False(screens[1].Primary);
        }

        [Fact]
        public void ParseSystemProfiler_OfflineDisplay_IsLeftOut()
        {
            // An offline display cannot be captured, and counting it would shift every later
            // display's number against the AVFoundation listing.
            var screens = MacDisplay.ParseSystemProfiler(@"{
              ""SPDisplaysDataType"" : [ { ""spdisplays_ndrvs"" : [
                { ""_spdisplays_pixels"" : ""1920 x 1080"", ""spdisplays_main"" : ""spdisplays_yes"", ""spdisplays_online"" : ""spdisplays_yes"" },
                { ""_spdisplays_pixels"" : ""1280 x 720"",  ""spdisplays_main"" : ""spdisplays_no"",  ""spdisplays_online"" : ""spdisplays_no"" }
              ] } ] }");

            Assert.Single(screens);
            Assert.Equal(1920, screens[0].Width);
        }

        [Fact]
        public void ParseSystemProfiler_SeveralGpus_AreAllRead()
        {
            var screens = MacDisplay.ParseSystemProfiler(@"{
              ""SPDisplaysDataType"" : [
                { ""spdisplays_ndrvs"" : [ { ""_spdisplays_pixels"" : ""1920 x 1080"", ""spdisplays_online"" : ""spdisplays_yes"" } ] },
                { ""spdisplays_ndrvs"" : [ { ""_spdisplays_pixels"" : ""1280 x 1024"", ""spdisplays_online"" : ""spdisplays_yes"" } ] }
              ] }");

            Assert.Equal(2, screens.Count);
        }

        [Fact]
        public void ParseSystemProfiler_NoDisplayArray_IsEmptyNotAnError()
        {
            Assert.Empty(MacDisplay.ParseSystemProfiler(@"{ ""SPDisplaysDataType"" : [ { ""_name"" : ""Apple M4"" } ] }"));
        }

        [Fact]
        public void ParseSystemProfiler_UnreadablePixels_IsLeftOutRatherThanGuessed()
        {
            // A display whose size cannot be read is not given a made-up size: a wrong size would
            // crop the recording wrongly and silently.
            Assert.Empty(MacDisplay.ParseSystemProfiler(@"{
              ""SPDisplaysDataType"" : [ { ""spdisplays_ndrvs"" : [
                { ""_spdisplays_pixels"" : ""unknown"", ""spdisplays_online"" : ""spdisplays_yes"" }
              ] } ] }"));
        }

        [Fact]
        public void ParseSystemProfiler_Empty_IsEmpty()
        {
            Assert.Empty(MacDisplay.ParseSystemProfiler(""));
        }

        [Fact]
        public void FriendlyName_SystemProfilersOwnPlaceholder_IsNotShownAsAName()
        {
            // "spdisplays_display" is system_profiler's internal key for an unnamed display. Shown
            // as-is it looks like a bug in AgentEyes, so it is treated as no name and the display is
            // numbered instead.
            Assert.Equal("", MacDisplay.FriendlyName("spdisplays_display"));
        }

        [Fact]
        public void FriendlyName_ARealName_IsKept()
        {
            Assert.Equal("Studio Display", MacDisplay.FriendlyName("Studio Display"));
        }

        [Fact]
        public void ParseSystemProfiler_UnnamedDisplay_HasNoName()
        {
            var screens = MacDisplay.ParseSystemProfiler(OneDisplay);

            Assert.Equal("", screens[0].Name);
        }
    }
}
