using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// The V4L2 cameras on this machine (issue #102), read from /sys/class/video4linux. A USB
    /// camera exposes several /dev/video nodes (capture, metadata); only the node whose sysfs
    /// "index" is 0 is the capture node, so each camera is listed once.
    ///
    /// The listed name carries its device node - "HD Pro Webcam C920 [/dev/video0]" - so it is both
    /// what a person matches with a fragment and what <see cref="DevicePath"/> turns back into the
    /// node ffmpeg's v4l2 input opens.
    /// </summary>
    internal static class V4l2Devices
    {
        private const string SysClass = "/sys/class/video4linux";
        private static readonly Regex Bracketed = new(@"\[(?<dev>/dev/[^\]]+)\]\s*$", RegexOptions.Compiled);

        public static IReadOnlyList<string> CameraNames() => CameraNames(SysClass);

        internal static IReadOnlyList<string> CameraNames(string sysClassRoot)
        {
            var names = new List<string>();
            if (!Directory.Exists(sysClassRoot))
            {
                Log.Info($"[V4l2Devices] CameraNames: {sysClassRoot} does not exist - no V4L2 cameras");
                return names;
            }
            foreach (var node in Directory.GetDirectories(sysClassRoot).OrderBy(NodeNumber))
            {
                string nodeName = Path.GetFileName(node);
                if (!nodeName.StartsWith("video", StringComparison.Ordinal)) continue;
                string index = ReadTrimmed(Path.Combine(node, "index"));
                if (index != "0") continue;
                string name = ReadTrimmed(Path.Combine(node, "name"));
                if (name.Length == 0) name = nodeName;
                names.Add(Label(name, "/dev/" + nodeName));
            }
            Log.Info($"[V4l2Devices] CameraNames: {names.Count} camera(s)");
            return names;
        }

        internal static string Label(string name, string devicePath) => $"{name} [{devicePath}]";

        /// <summary>The /dev/videoN node inside a listed camera name.</summary>
        public static string DevicePath(string listedName)
        {
            var m = Bracketed.Match(listedName ?? "");
            if (!m.Success)
                throw new UsageException($"\"{listedName}\" is not a listed camera (expected \"Name [/dev/videoN]\"). Run 'agenteyes screens'.");
            return m.Groups["dev"].Value;
        }

        private static int NodeNumber(string dir)
        {
            string n = Path.GetFileName(dir);
            return int.TryParse(n.AsSpan("video".Length < n.Length ? "video".Length : 0), out int i) ? i : int.MaxValue;
        }

        private static string ReadTrimmed(string file) => File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    }
}
