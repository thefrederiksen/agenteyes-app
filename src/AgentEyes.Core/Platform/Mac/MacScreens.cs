using System;
using AgentEyes.Video;

namespace AgentEyes.Platform.Mac
{
    /// <summary>
    /// <c>agenteyes screens</c> on macOS: the displays, the AVFoundation microphones (the names
    /// --mic and "mic" match against), the system-audio device a --mix / --loopback recording needs,
    /// and the cameras.
    ///
    /// The display list prints SIZE but not position, because on macOS a region is a crop inside one
    /// display rather than an offset into a shared desktop - printing an (x, y) here would invite a
    /// spanning region that cannot be recorded. See the Monitors class comment.
    /// </summary>
    internal static class MacScreens
    {
        public static int Print()
        {
            Log.Info("[MacScreens] Print");
            Console.WriteLine("MONITORS (AVFoundation + system_profiler)");
            Console.WriteLine("  #  Resolution      Primary  Name");
            foreach (var m in Monitors.All())
            {
                Console.WriteLine(
                    $"  {m.Index}  {m.Width,5} x {m.Height,-5}".PadRight(20)
                    + $"  {(m.Primary ? "yes" : "no"),-7}  {m.Name}");
            }
            Console.WriteLine();
            Console.WriteLine("  Note: a region is cropped inside ONE display on macOS - a region");
            Console.WriteLine("        spanning two monitors cannot be recorded.");

            var listing = AvfDevices.Read();

            Console.WriteLine();
            Console.WriteLine("MICROPHONES - AVFoundation audio inputs (--mic \"fragment\" matches these)");
            if (listing.Audio.Count == 0) Console.WriteLine("  (none found)");
            for (int i = 0; i < listing.Audio.Count; i++)
                Console.WriteLine($"  [{i}] \"{listing.Audio[i].Name}\"  <AVFoundation :{listing.Audio[i].Index}>");

            Console.WriteLine();
            Console.WriteLine("SYSTEM AUDIO (--mix / --loopback record this)");
            var system = MacSystemAudio.Find(listing.Audio, out string why);
            if (system != null)
                Console.WriteLine($"  \"{system.Value.Name}\"  <AVFoundation :{system.Value.Index}>");
            else
                Console.WriteLine("  (unavailable: " + why + " macOS needs a virtual audio device - see 'agenteyes video --mix')");

            Console.WriteLine();
            Console.WriteLine("CAMERAS - AVFoundation (--camera \"fragment\" matches these)");
            var cams = FfmpegDevices.ListVideo();
            if (cams.Count == 0) Console.WriteLine("  (none found)");
            foreach (var name in cams) Console.WriteLine($"  \"{name}\"");
            return 0;
        }
    }
}
