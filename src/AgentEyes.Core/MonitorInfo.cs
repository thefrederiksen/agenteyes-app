namespace AgentEyes
{
    /// <summary>One connected display. Shared by the Windows enumeration (Monitors.cs, WinForms
    /// Screen) and the Linux one (Platform/Linux/LinuxMonitors.cs, the X server via xrandr).</summary>
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
}
