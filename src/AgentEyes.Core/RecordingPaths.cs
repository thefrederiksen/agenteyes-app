using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>Where recordings live and how new per-session folders are named. Shared by all callers.</summary>
    internal static class RecordingPaths
    {
        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AgentEyes");

        public static string NewDir(string mode, string? label)
        {
            string safe = Sanitize(string.IsNullOrWhiteSpace(label) ? mode : label!);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string dir = Path.Combine(Root, $"{stamp}_{safe}");
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            return dir;
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Replace(' ', '-');
        }
    }
}
