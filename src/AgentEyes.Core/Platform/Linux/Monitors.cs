using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AgentEyes.Platform.Linux;

namespace AgentEyes
{
    /// <summary>
    /// Linux monitor enumeration (issue #102): the X server's monitors as xrandr reports them. The
    /// same surface as the Windows Monitors class (All / VirtualBounds / Require), so every caller
    /// above it is shared. Coordinates are X screen pixels, which is what x11grab's +X,Y offset
    /// addresses. X11 only (assumption A1): under Wayland there is no X screen to grab.
    /// </summary>
    internal static class Monitors
    {
        // " 0: +*HDMI-1 1920/508x1080/286+0+0  HDMI-1"   ('*' marks the primary monitor)
        private static readonly Regex MonitorLine = new(
            @"^\s*\d+:\s+\+?(?<primary>\*?)(?<name>\S+)\s+(?<w>\d+)/\d+x(?<h>\d+)/\d+(?<x>[+-]\d+)(?<y>[+-]\d+)",
            RegexOptions.Compiled);

        public static IReadOnlyList<MonitorInfo> All()
        {
            LinuxDisplay.Require();
            var r = ToolRunner.Run("xrandr", new[] { "--listmonitors" }, TimeSpan.FromSeconds(10));
            if (r.ExitCode != 0)
                throw new UsageException($"xrandr --listmonitors failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
            var list = ParseListMonitors(r.StdOut);
            if (list.Count == 0)
                throw new UsageException("xrandr reported no monitors on display " + LinuxDisplay.Require() + ".");
            return list;
        }

        /// <summary>
        /// Parse the output of <c>xrandr --listmonitors</c>. Pure, so it is unit-tested without an X
        /// server. Index is 1-based in xrandr's order, which is what --screen N means on Windows too.
        /// </summary>
        internal static List<MonitorInfo> ParseListMonitors(string output)
        {
            var list = new List<MonitorInfo>();
            if (string.IsNullOrEmpty(output)) return list;
            foreach (var raw in output.Replace("\r", "").Split('\n'))
            {
                var m = MonitorLine.Match(raw);
                if (!m.Success) continue;
                list.Add(new MonitorInfo
                {
                    Index = list.Count + 1,
                    Name = m.Groups["name"].Value,
                    Width = int.Parse(m.Groups["w"].Value),
                    Height = int.Parse(m.Groups["h"].Value),
                    X = int.Parse(m.Groups["x"].Value),
                    Y = int.Parse(m.Groups["y"].Value),
                    Primary = m.Groups["primary"].Value == "*",
                });
            }
            return list;
        }

        /// <summary>The bounding box of every monitor - the area x11grab can grab.</summary>
        public static System.Drawing.Rectangle VirtualBounds() => Union(All());

        internal static System.Drawing.Rectangle Union(IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors.Count == 0) return System.Drawing.Rectangle.Empty;
            var u = monitors[0].Bounds;
            for (int i = 1; i < monitors.Count; i++) u = System.Drawing.Rectangle.Union(u, monitors[i].Bounds);
            return u;
        }

        public static MonitorInfo Require(int oneBasedIndex)
        {
            var all = All();
            if (oneBasedIndex < 1 || oneBasedIndex > all.Count)
            {
                throw new UsageException(
                    $"--screen {oneBasedIndex} out of range. There are {all.Count} monitor(s). Run 'agenteyes screens'.");
            }
            return all[oneBasedIndex - 1];
        }
    }
}
