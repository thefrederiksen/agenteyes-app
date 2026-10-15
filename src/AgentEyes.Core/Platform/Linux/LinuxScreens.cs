using System;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// <c>agenteyes screens</c> on Linux (issue #102): the X monitors, the pulse microphones (the
    /// names --mic and "mic" match against), the system-audio source a --mix / --loopback recording
    /// uses, and the V4L2 cameras. One list per thing - on Windows NAudio and DirectShow name the
    /// same microphone twice; on Linux both are the pulse source.
    /// </summary>
    internal static class LinuxScreens
    {
        public static int Print()
        {
            Log.Info("[LinuxScreens] Print");
            Console.WriteLine($"MONITORS (X display {LinuxDisplay.Require()}, xrandr)");
            Console.WriteLine("  #  Resolution      Position        Primary  Name");
            foreach (var m in Monitors.All())
            {
                Console.WriteLine(
                    $"  {m.Index}  {m.Width,5} x {m.Height,-5}  ({m.X}, {m.Y})".PadRight(34)
                    + $"  {(m.Primary ? "yes" : "no"),-7}  {m.Name}");
            }

            Console.WriteLine();
            Console.WriteLine("MICROPHONES - PulseAudio sources (--mic \"fragment\" matches these)");
            var mics = PulseDevices.Microphones();
            if (mics.Count == 0) Console.WriteLine("  (none found)");
            for (int i = 0; i < mics.Count; i++)
                Console.WriteLine($"  [{i}] \"{mics[i].Description}\"{(mics[i].IsDefault ? "  (default)" : "")}  <{mics[i].Name}>");

            Console.WriteLine();
            Console.WriteLine("SYSTEM AUDIO (--mix / --loopback record this)");
            try
            {
                var monitor = PulseDevices.RequireSystemAudioSource();
                Console.WriteLine($"  \"{monitor.Description}\"  <{monitor.Name}>");
            }
            catch (UsageException ex)
            {
                Console.WriteLine("  (unavailable: " + ex.Message + ")");
            }

            Console.WriteLine();
            Console.WriteLine("CAMERAS - V4L2 (--camera \"fragment\" matches these)");
            var cams = FfmpegDevices.ListVideo();
            if (cams.Count == 0) Console.WriteLine("  (none found)");
            foreach (var name in cams) Console.WriteLine($"  \"{name}\"");
            return 0;
        }
    }
}
