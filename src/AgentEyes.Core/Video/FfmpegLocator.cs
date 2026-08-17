using System;
using System.IO;

namespace AgentEyes.Video
{
    /// <summary>
    /// Locate ffmpeg.exe / ffprobe.exe precisely. If not found we throw with exact guidance -
    /// no silent fallback to a different engine. ffmpeg is a standard media binary (not a cc-* tool).
    /// </summary>
    internal static class FfmpegLocator
    {
        private static string? _ffmpegCache;
        private static string? _ffprobeCache;

        public static string Ffmpeg() => _ffmpegCache ??= Find("ffmpeg");

        public static string Ffprobe() => _ffprobeCache ??= Find("ffprobe");

        private static string Find(string tool)
        {
            // 0) bundled next to our own executable - the installed product ships its own ffmpeg
            //    so a fresh machine needs nothing preinstalled.
            string bundled = Path.Combine(AppContext.BaseDirectory, tool + ".exe");
            if (File.Exists(bundled)) return bundled;

            // 1) explicit override
            string? env = Environment.GetEnvironmentVariable("QA_RECORD_FFMPEG_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                string p = Path.Combine(env, tool + ".exe");
                if (File.Exists(p)) return p;
            }

            // 2) PATH
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p;
                    try { p = Path.Combine(dir.Trim(), tool + ".exe"); }
                    catch { continue; }
                    if (File.Exists(p)) return p;
                }
            }

            // 3) common winget install location
            string winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
            {
                foreach (string match in Directory.GetFiles(winget, tool + ".exe", SearchOption.AllDirectories))
                {
                    return match;
                }
            }

            throw new UsageException(
                $"{tool}.exe not found. Install ffmpeg (winget install Gyan.FFmpeg) or set " +
                $"QA_RECORD_FFMPEG_DIR to the folder containing {tool}.exe.");
        }
    }
}
