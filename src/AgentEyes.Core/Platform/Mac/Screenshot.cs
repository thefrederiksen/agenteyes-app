using System;
using System.IO;
using AgentEyes.Platform.Mac;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// macOS still capture: one PNG written by the system's own screencapture tool. See
    /// FfmpegArgs.ScreencaptureStill for why this diverges from the Linux still, which uses the same
    /// grabber as the recording.
    ///
    /// The headless build has no clipboard: a request to copy is logged as not done, the PNG is
    /// still written.
    /// </summary>
    internal static class Screenshot
    {
        /// <summary>screencapture is part of macOS and is always at this path.</summary>
        private const string Tool = "/usr/sbin/screencapture";

        /// <summary>
        /// Why a still did not happen, or null when one did. Pure, so the wording a person actually
        /// has to act on is unit-tested rather than only ever seen in an incident.
        /// </summary>
        /// <param name="fileWritten">Whether the PNG is on disk - the only positive proof of a capture.</param>
        internal static string? Failure(int exitCode, string stderr, bool fileWritten)
        {
            if (exitCode == 0 && fileWritten) return null;

            string detail = (stderr ?? "").Trim();
            string head = exitCode != 0
                ? $"screencapture failed (exit {exitCode})" + (detail.Length > 0 ? ": " + detail : "")
                : "screencapture reported success but wrote no file";

            return head
                + ". On macOS the usual cause is the Screen Recording permission: grant it in System "
                + "Settings > Privacy and Security > Screen and System Audio Recording, to the "
                + "application that launches AgentEyes, then try again. Nothing in AgentEyes can grant "
                + "it - macOS only accepts that from the person at the keyboard.";
        }

        /// <summary>
        /// Remove anything already sitting at the capture target.
        ///
        /// WHY THIS EXISTS. CaptureRect proves a capture happened by asking whether the file is
        /// there afterwards, because screencapture can exit ZERO and write nothing when the Screen
        /// Recording permission is refused. That proof only holds if the file could not have been
        /// there already - otherwise an earlier run's image satisfies the check and yesterday's
        /// screenshot is handed back as today's, with no error. After this returns, the file's
        /// presence means exactly one thing: this run wrote it.
        /// </summary>
        internal static void ClearStaleTarget(string outputPath)
        {
            if (!File.Exists(outputPath)) return;

            Log.Info($"[Screenshot] ClearStaleTarget: removing an older file at {outputPath}");
            File.Delete(outputPath);

            // Deleting is not assumed to have worked: if the old file survives, the check that
            // follows the capture is already invalid, so stop here rather than report a result
            // that cannot be trusted.
            if (File.Exists(outputPath))
            {
                throw new UsageException(
                    $"an older file at {outputPath} could not be removed, so a new screenshot "
                    + "cannot be told apart from it. Remove that file, or write to another path "
                    + "with --out.");
            }
        }

        public static string CaptureMonitor(MonitorInfo monitor, string outputPath, bool copyToClipboard = true) =>
            CaptureRect(monitor.Bounds, outputPath, copyToClipboard);

        public static string CaptureRect(Drawing.Rectangle rect, string outputPath, bool copyToClipboard = true)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new UsageException($"capture rectangle is empty ({rect.Width}x{rect.Height}).");
            Log.Info($"[Screenshot] CaptureRect: {rect.Width}x{rect.Height} at ({rect.X},{rect.Y}) -> {outputPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            ClearStaleTarget(outputPath);

            var args = FfmpegArgs.ScreencaptureStill(rect, outputPath);
            var r = ToolRunner.Run(Tool, args, TimeSpan.FromSeconds(30));

            // TWO WAYS THIS FAILS, and the permission is the likely cause of both. A refused Screen
            // Recording grant makes screencapture fail with "could not create image from rect"; it
            // can also exit ZERO and simply write nothing. So the PRESENCE of the file is what is
            // asserted, never the absence of an error - a check whose pass condition is an absence
            // would certify a capture that never happened.
            string? failure = Failure(r.ExitCode, r.StdErr, File.Exists(outputPath));
            if (failure != null)
            {
                Log.Error("[Screenshot] CaptureRect FAILED: " + failure);
                throw new UsageException(failure);
            }

            if (copyToClipboard)
                Log.Warn("[Screenshot] CaptureRect: copy to clipboard was requested - the macOS headless build has no clipboard; the PNG is saved");
            return outputPath;
        }
    }
}
