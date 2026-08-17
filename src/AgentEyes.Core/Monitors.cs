using System;
using System.Collections.Generic;
using WinForms = System.Windows.Forms;

namespace AgentEyes
{
    /// <summary>One connected display.</summary>
    internal sealed class MonitorInfo
    {
        public int Index { get; init; }            // 1-based, stable for --screen
        public string Name { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public bool Primary { get; init; }

        public System.Drawing.Rectangle Bounds => new(X, Y, Width, Height);
    }

    /// <summary>
    /// Multi-monitor enumeration via System.Windows.Forms.Screen (Win32 EnumDisplayMonitors).
    /// TODO Phase 1: surface per-monitor DPI scale explicitly for mixed-DPI correctness.
    /// </summary>
    internal static class Monitors
    {
        public static IReadOnlyList<MonitorInfo> All()
        {
            var list = new List<MonitorInfo>();
            var screens = WinForms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                list.Add(new MonitorInfo
                {
                    Index = i + 1,
                    Name = CleanDeviceName(s.DeviceName),
                    X = s.Bounds.X,
                    Y = s.Bounds.Y,
                    Width = s.Bounds.Width,
                    Height = s.Bounds.Height,
                    Primary = s.Primary,
                });
            }
            return list;
        }

        /// <summary>
        /// The virtual desktop bounding rectangle (all monitors), in device pixels. This is the
        /// area gdigrab can capture from - a requested capture region that extends past it must be
        /// grabbed clamped and padded back to size (see <see cref="Video.FfmpegArgs.VideoCapture"/>).
        /// </summary>
        public static System.Drawing.Rectangle VirtualBounds()
        {
            var v = WinForms.SystemInformation.VirtualScreen;
            return new System.Drawing.Rectangle(v.X, v.Y, v.Width, v.Height);
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

        private static string CleanDeviceName(string raw)
        {
            // Device names look like "\\.\DISPLAY1" - trim to something readable.
            return raw.Replace("\\\\.\\", "").Trim();
        }
    }
}
