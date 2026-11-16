using System;
using System.Diagnostics;
using System.IO;
using AgentEyes.Platform.Mac;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// macOS still capture: one PNG, one AVFoundation frame, written by the SAME ffmpeg grabber the
    /// recording uses - so a marker shot shows exactly what the video shows. See
    /// FfmpegArgs.AvfStill for the measurement that moved this off Apple's screencapture tool
    /// (issue #115) and for the warm-up cost it now pays.
    ///
    /// WHY THE RUN IS BOUNDED, AND WHAT THE BOUND MEANS. An AVFoundation screen capture started by a
    /// process that does NOT hold the Screen Recording grant does not fail - it hangs, forever,
    /// having printed nothing but "Configuration of video device failed, falling back to default"
    /// (measured 2026-11-14 from an unentitled shell: 15 s, no frame, no file, killed). So the
    /// capture runs under <see cref="Budget"/> and the EXPIRY of that budget is the one condition
    /// that names the permission. Every other failure reports what ffmpeg actually said and says
    /// nothing about permissions, because blaming a granted permission is what issue #115 was about.
    ///
    /// The headless build has no clipboard: a request to copy is logged as not done, the PNG is
    /// still written.
    /// </summary>
    internal static class Screenshot
    {
        /// <summary>
        /// How long one still may take before the capture is called hung and killed.
        ///
        /// THE NUMBER IS MEASURED, not chosen for comfort. On devthrottle-mac-mini (2026-11-14,
        /// 1920x1080, service at ProcessType Interactive) the ffmpeg run itself took 766, 806 and
        /// 757 ms across three captures, about 1.1 s including the display resolution ahead of it -
        /// so 10 s is roughly twelve times the observed cost. It is deliberately NOT more generous
        /// than that: RecordingService.MarkerShot holds the session lock across this call, so a
        /// capture that cannot produce a frame must give up quickly rather than wedge /status.
        /// Every figure is in docs/cencon/proof/issue-115/handoff.md.
        /// </summary>
        internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Why a still did not happen, or null when one did. Pure, so the wording a person actually
        /// has to act on is unit-tested rather than only ever seen in an incident.
        ///
        /// NOTHING HERE MENTIONS THE SCREEN RECORDING PERMISSION, and that is the fix for issue
        /// #115's second half: the old wording sent every failure to a settings pane that was
        /// already correct. A missing grant does not reach this method at all - it hangs the capture
        /// and arrives at <see cref="Hung"/>.
        /// </summary>
        /// <param name="fileWritten">Whether the PNG is on disk - the only positive proof of a capture.</param>
        internal static string? Failure(int exitCode, string stderr, bool fileWritten)
        {
            if (exitCode == 0 && fileWritten) return null;

            string detail = Tail((stderr ?? "").Trim());
            if (exitCode != 0)
            {
                return $"ffmpeg could not capture the screen (exit {exitCode})"
                    + (detail.Length > 0 ? ": " + detail : "") + ".";
            }

            // Exit zero and no file: ffmpeg believed it had finished and nothing is there. Reported
            // as its own case, because the PRESENCE of the file is what proves a capture happened -
            // a check whose pass condition is an absence would certify one that never did.
            return "ffmpeg reported success but wrote no image file"
                + (detail.Length > 0 ? ": " + detail : "") + ".";
        }

        /// <summary>
        /// Why a still that produced no frame inside <see cref="Budget"/> is reported as a permission
        /// problem - the ONE error path that names the Screen Recording grant (issue #115,
        /// acceptance criterion 6).
        ///
        /// IT IS ACCURATE BECAUSE IT WAS MEASURED. An AVFoundation screen capture run by a process
        /// without the grant hangs indefinitely instead of erroring; a capture run by the process
        /// that HAS the grant completes in about a second. A hang is therefore what a missing grant
        /// looks like from here, and the message says so without asserting it as the only
        /// possibility - a machine slow enough to miss the budget is the other way in, and the
        /// wording leaves that open rather than sending someone to the wrong settings pane.
        /// </summary>
        internal static string Hung(TimeSpan budget, string stderr)
        {
            string detail = Tail((stderr ?? "").Trim());
            return $"the screen capture produced no frame within {budget.TotalSeconds:0}s and was "
                + "stopped. On macOS that is what a MISSING Screen Recording permission looks like: "
                + "the capture does not fail, it waits for a frame that never comes. Grant it in "
                + "System Settings > Privacy and Security > Screen and System Audio Recording, to "
                + "the application that launches AgentEyes, then try again - nothing in AgentEyes "
                + "can grant it, macOS only accepts that from the person at the keyboard. (If the "
                + "permission is already on and recording works, the machine simply did not deliver "
                + "a frame in time.)"
                + (detail.Length > 0 ? " ffmpeg said: " + detail : "");
        }

        /// <summary>
        /// Remove anything already sitting at the capture target.
        ///
        /// WHY THIS EXISTS. CaptureRect proves a capture happened by asking whether the file is
        /// there afterwards, because a capture can report success and write nothing. That proof only
        /// holds if the file could not have been there already - otherwise an earlier run's image
        /// satisfies the check and yesterday's screenshot is handed back as today's, with no error.
        /// After this returns, the file's presence means exactly one thing: this run wrote it.
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

        /// <summary>
        /// One whole monitor. This caller KNOWS its display, so the capture is correct on any
        /// monitor - see MacDisplay.RequireMainScreen for why the rectangle-only path cannot be.
        /// </summary>
        public static string CaptureMonitor(MonitorInfo monitor, string outputPath, bool copyToClipboard = true)
        {
            var (avfIndex, screen) = MacDisplay.RequireScreen(monitor.Index);
            return Capture(monitor.Bounds, avfIndex, screen, outputPath, copyToClipboard);
        }

        /// <summary>
        /// One region. On macOS a region is display-local and every monitor's bounds start at (0,0),
        /// so a rectangle alone cannot name a display: it is read as a crop inside the MAIN display,
        /// which is the space Monitors.VirtualBounds already clamps a macOS capture into.
        /// </summary>
        public static string CaptureRect(Drawing.Rectangle rect, string outputPath, bool copyToClipboard = true)
        {
            var (avfIndex, screen) = MacDisplay.RequireMainScreen();
            return Capture(rect, avfIndex, screen, outputPath, copyToClipboard);
        }

        private static string Capture(
            Drawing.Rectangle rect, int avfIndex, MacScreen screen, string outputPath, bool copyToClipboard)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new UsageException($"capture rectangle is empty ({rect.Width}x{rect.Height}).");
            Log.Info($"[Screenshot] Capture: {rect.Width}x{rect.Height} at ({rect.X},{rect.Y}) "
                + $"on AVFoundation screen {avfIndex} ({screen.Width}x{screen.Height}) -> {outputPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            ClearStaleTarget(outputPath);

            var args = FfmpegArgs.AvfStill(
                rect, avfIndex, new Drawing.Size(screen.Width, screen.Height), outputPath);

            var sw = Stopwatch.StartNew();
            bool finished = ToolRunner.TryRun(FfmpegLocator.Ffmpeg(), args, Budget, out var r);
            sw.Stop();

            if (!finished)
            {
                string hung = Hung(Budget, r.StdErr);
                Log.Error("[Screenshot] Capture FAILED: " + hung);
                throw new UsageException(hung);
            }

            // THE PRESENCE OF THE FILE IS WHAT IS ASSERTED, never the absence of an error.
            string? failure = Failure(r.ExitCode, r.StdErr, File.Exists(outputPath));
            if (failure != null)
            {
                Log.Error("[Screenshot] Capture FAILED: " + failure);
                throw new UsageException(failure);
            }

            Log.Info($"[Screenshot] Capture: wrote {outputPath} "
                + $"({new FileInfo(outputPath).Length} bytes) in {sw.Elapsed.TotalMilliseconds:0} ms");
            if (copyToClipboard)
                Log.Warn("[Screenshot] Capture: copy to clipboard was requested - the macOS headless build has no clipboard; the PNG is saved");
            return outputPath;
        }

        /// <summary>The tail of what a tool printed, so a message stays readable in a JSON error.</summary>
        private static string Tail(string s)
        {
            s = (s ?? "").Trim();
            return s.Length <= 400 ? s : "..." + s[^400..];
        }
    }
}
