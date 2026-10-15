using System;
using System.IO;
using AgentEyes.Platform.Linux;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// Linux still capture (issue #102): one x11grab frame written as PNG by ffmpeg - the same
    /// grabber the recording uses, so a marker shot shows exactly what the video shows. The headless
    /// build has no clipboard: a request to copy is logged as not done, the PNG is still written.
    /// </summary>
    internal static class Screenshot
    {
        public static string CaptureMonitor(MonitorInfo monitor, string outputPath, bool copyToClipboard = true) =>
            CaptureRect(monitor.Bounds, outputPath, copyToClipboard);

        public static string CaptureRect(Drawing.Rectangle rect, string outputPath, bool copyToClipboard = true)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new UsageException($"capture rectangle is empty ({rect.Width}x{rect.Height}).");
            Log.Info($"[Screenshot] CaptureRect: {rect.Width}x{rect.Height} at ({rect.X},{rect.Y}) -> {outputPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            Ffmpeg.Run(FfmpegArgs.X11Still(rect, LinuxDisplay.Require(), outputPath), "screenshot");
            if (copyToClipboard)
                Log.Warn("[Screenshot] CaptureRect: copy to clipboard was requested - the Linux headless build has no clipboard; the PNG is saved");
            return outputPath;
        }
    }
}
