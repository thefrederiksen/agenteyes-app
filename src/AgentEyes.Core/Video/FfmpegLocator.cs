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

#if WINDOWS
        private const string ExeSuffix = ".exe";
#else
        private const string ExeSuffix = "";
#endif

        private static string Find(string tool)
        {
            // 0) bundled next to our own executable - the installed product ships its own ffmpeg
            //    so a fresh machine needs nothing preinstalled.
            string bundled = Path.Combine(AppContext.BaseDirectory, tool + ExeSuffix);
            if (File.Exists(bundled)) return bundled;

            // 1) explicit override
            string? env = Environment.GetEnvironmentVariable("QA_RECORD_FFMPEG_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                string p = Path.Combine(env, tool + ExeSuffix);
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
                    try { p = Path.Combine(dir.Trim(), tool + ExeSuffix); }
                    catch { continue; }
                    if (File.Exists(p)) return p;
                }
            }

#if WINDOWS
            // 3) common winget install location
            //    (a tool location, not AgentEyes state - so the machine's real folder, never redirected)
            string winget = Path.Combine(AppDataPaths.MachineLocalAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
            {
                foreach (string match in Directory.GetFiles(winget, tool + ExeSuffix, SearchOption.AllDirectories))
                {
                    return match;
                }
            }

            throw new UsageException(
                $"{tool}.exe not found. Install ffmpeg (winget install Gyan.FFmpeg) or set " +
                $"QA_RECORD_FFMPEG_DIR to the folder containing {tool}.exe.");
#elif MACOS
            throw new UsageException(
                $"{tool} not found on PATH. Install it with 'brew install ffmpeg', or set " +
                $"QA_RECORD_FFMPEG_DIR to the folder containing {tool}.");
#else
            throw new UsageException(
                $"{tool} not found on PATH. Install it with 'sudo apt install -y ffmpeg', or set " +
                $"QA_RECORD_FFMPEG_DIR to the folder containing {tool}.");
#endif
        }
    }
}
