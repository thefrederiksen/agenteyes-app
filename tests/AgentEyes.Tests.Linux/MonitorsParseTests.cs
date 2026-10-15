using System.Linq;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class MonitorsParseTests
    {
        [Fact]
        public void ParseListMonitors_SingleMonitor_ReadsGeometryAndPrimary()
        {
            var list = Monitors.ParseListMonitors("Monitors: 1\n 0: +*HDMI-1 1920/508x1080/286+0+0  HDMI-1\n");

            var m = Assert.Single(list);
            Assert.Equal(1, m.Index);
            Assert.Equal("HDMI-1", m.Name);
            Assert.Equal((0, 0, 1920, 1080), (m.X, m.Y, m.Width, m.Height));
            Assert.True(m.Primary);
        }

        [Fact]
        public void ParseListMonitors_TwoMonitors_KeepsOrderOffsetsAndOnePrimary()
        {
            const string output =
                "Monitors: 2\n" +
                " 0: +*DP-1 2560/597x1440/336+0+0  DP-1\n" +
                " 1: +HDMI-1 1920/509x1080/286+2560+180  HDMI-1\n";

            var list = Monitors.ParseListMonitors(output);

            Assert.Equal(new[] { "DP-1", "HDMI-1" }, list.Select(m => m.Name));
            Assert.Equal(new[] { 1, 2 }, list.Select(m => m.Index));
            Assert.Equal((2560, 180), (list[1].X, list[1].Y));
            Assert.False(list[1].Primary);
        }

        [Fact]
        public void ParseListMonitors_NegativeOffset_IsParsed()
        {
            var m = Assert.Single(Monitors.ParseListMonitors(" 0: +eDP-1 1920/344x1200/215-1920+0  eDP-1"));
            Assert.Equal(-1920, m.X);
        }

        [Fact]
        public void ParseListMonitors_NoMonitorLines_ReturnsEmpty()
        {
            Assert.Empty(Monitors.ParseListMonitors("Monitors: 0\n"));
            Assert.Empty(Monitors.ParseListMonitors(""));
        }

        [Fact]
        public void Union_TwoMonitors_CoversBoth()
        {
            var list = Monitors.ParseListMonitors(" 0: +*DP-1 2560/597x1440/336+0+0  DP-1\n 1: +HDMI-1 1920/509x1080/286+2560+180  HDMI-1\n");
            Assert.Equal(new System.Drawing.Rectangle(0, 0, 4480, 1440), Monitors.Union(list));
        }
    }
}
