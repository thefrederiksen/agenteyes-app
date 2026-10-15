using System;
using System.Collections.Generic;
using AgentEyes.Platform.Mac;

namespace AgentEyes
{
    /// <summary>
    /// macOS monitor enumeration: the same surface as the Windows and Linux Monitors classes
    /// (All / VirtualBounds / Require), so every caller above it is shared.
    ///
    /// ONE REAL DIFFERENCE, AND IT IS DELIBERATE. Windows gdigrab and Linux x11grab both grab out of
    /// one big virtual desktop, addressed with a global +X,Y offset, so a region may span monitors.
    /// AVFoundation does not work that way: it opens ONE display by index and a region is a crop
    /// INSIDE that display. So a monitor's Bounds here is display-local - always at (0,0) - and that
    /// is not a placeholder, it is the coordinate space an AVFoundation capture actually uses.
    ///
    /// The consequence, stated rather than hidden: a region spanning two monitors cannot be recorded
    /// on a Mac. VirtualBounds returns the main display's bounds, which is the space a capture is
    /// clamped and padded into, and spanning regions are refused in FfmpegArgs rather than silently
    /// cropped to one screen.
    /// </summary>
    internal static class Monitors
    {
        public static IReadOnlyList<MonitorInfo> All()
        {
            var screens = MacDisplay.Screens();
            var list = new List<MonitorInfo>(screens.Count);
            for (int i = 0; i < screens.Count; i++)
            {
                list.Add(new MonitorInfo
                {
                    Index = i + 1,
                    Name = screens[i].Name.Length > 0 ? screens[i].Name : "Display " + (i + 1),
                    Width = screens[i].Width,
                    Height = screens[i].Height,
                    // Display-local: see the class comment. AVFoundation crops inside one display.
                    X = 0,
                    Y = 0,
                    Primary = screens[i].Primary,
                });
            }
            return list;
        }

        /// <summary>
        /// The space a capture is clamped into: the MAIN display's bounds, not a union. macOS cannot
        /// grab across displays, so a union would describe an area no capture can produce.
        /// </summary>
        public static System.Drawing.Rectangle VirtualBounds()
        {
            var all = All();
            foreach (var m in all)
            {
                if (m.Primary) return m.Bounds;
            }
            // No display marked as the main one is not something to paper over with a guess.
            throw new UsageException(
                "macOS reported displays but marked none of them as the main display, so AgentEyes "
                + "cannot tell which one a capture is clamped into. Run 'agenteyes screens'.");
        }

        public static MonitorInfo Require(int oneBasedIndex)
        {
            var all = All();
            if (oneBasedIndex < 1 || oneBasedIndex > all.Count)
            {
                throw new UsageException(
                    $"--screen {oneBasedIndex} out of range. There are {all.Count} monitor(s). "
                    + "Run 'agenteyes screens'.");
            }
            return all[oneBasedIndex - 1];
        }
    }
}
