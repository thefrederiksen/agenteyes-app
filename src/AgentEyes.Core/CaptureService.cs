using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// One saved screen capture (snip): the PNG path and its pixel size. The Capture
    /// gallery (issue #64) is built from these.
    /// </summary>
    internal sealed class CaptureInfo
    {
        public string File { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public DateTime CreatedLocal { get; init; }
    }

    /// <summary>
    /// The Capture feature (issue #64): interactive full-screen and region snips that are
    /// both copied to the clipboard AND saved as PNG. Distinct from recordings (which live
    /// under Videos\AgentEyes): captures are still images.
    ///
    /// Save location (AC9/AC10): by default snips land in the Windows Screenshots known folder
    /// (SHGetKnownFolderPath(FOLDERID_Screenshots)) - the very place the OS Snipping Tool saves,
    /// honoring OneDrive redirection. The Capture tab can override that folder; the override is
    /// passed in from the App's config. Reuses the tested Screenshot/Monitors/RegionOverlay engine
    /// so behavior is identical on Windows 10 and 11 (never the OS Snipping Tool).
    ///
    /// Pure naming/path logic is kept here (no GDI) so it is unit-testable; the actual pixel grab
    /// lives in CaptureFullScreen/CaptureRegion which call Screenshot.CaptureRect.
    /// </summary>
    internal static class CaptureService
    {
        // FOLDERID_Screenshots {b7bede81-df94-4682-a7d8-57a52620b86f}: the Windows Screenshots
        // folder. SHGetKnownFolderPath honors a OneDrive-redirected Pictures folder, so this
        // resolves to e.g. D:\...\OneDrive\Pictures\Screenshots when Pictures is redirected.
        private static readonly Guid FOLDERID_Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

        // KF_FLAG_DONT_VERIFY: return the REGISTERED known-folder path without requiring the
        // directory to physically exist. Without it, SHGetKnownFolderPath refuses and returns
        // hr=0x80070002 (FILE_NOT_FOUND) on any profile where the Screenshots folder has never been
        // provisioned (a fresh first-run user, or the headless GitHub Actions runner). This is the
        // correct flag for resolving a destination path - it is NOT a fallback and NOT a hard-coded
        // path: the shell still honors OneDrive/Pictures redirection. The directory is materialized
        // later, at save time, by Directory.CreateDirectory in SaveRect.
        private const uint KF_FLAG_DONT_VERIFY = 0x00004000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        /// <summary>
        /// The Windows Screenshots known folder (FOLDERID_Screenshots), resolved via the shell so
        /// OneDrive redirection is honored. This is the default capture destination (AC9): it is
        /// NOT a hard-coded path and NOT a AgentEyes subfolder. Side-effect-free: it resolves a
        /// path and does NOT create any directory (KF_FLAG_DONT_VERIFY), so it succeeds even when the
        /// Screenshots folder has not yet been provisioned on disk.
        /// </summary>
        public static string ScreenshotsKnownFolder()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                int hr = SHGetKnownFolderPath(FOLDERID_Screenshots, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out ptr);
                if (hr != 0 || ptr == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"SHGetKnownFolderPath(FOLDERID_Screenshots) failed (hr=0x{hr:X8}). "
                        + "The Windows Screenshots folder could not be resolved.");
                string path = Marshal.PtrToStringUni(ptr)
                    ?? throw new InvalidOperationException("SHGetKnownFolderPath returned a null path.");
                return path;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            }
        }

        /// <summary>
        /// Where snips are saved. With no override (null/blank) this is the Windows Screenshots
        /// known folder (AC9). A non-blank override (the Capture-tab Settings folder, AC10) wins.
        /// Pure (no I/O) so it is unit-testable.
        /// </summary>
        public static string ResolveSaveFolder(string? configuredOverride)
            => string.IsNullOrWhiteSpace(configuredOverride)
                ? ScreenshotsKnownFolder()
                : configuredOverride.Trim();

        /// <summary>
        /// PNG file name for a capture: "AgentEyes_yyyy-MM-dd_HHmmss_WxH.png". The timestamp keeps
        /// captures ordered and unique; the dimensions make the file self-describing. The prefix
        /// distinguishes our snips from other tools' files in the shared Screenshots folder.
        /// Pure (no I/O) so it is unit-testable.
        /// </summary>
        public static string FileNameFor(int width, int height, DateTime when)
        {
            if (width <= 0 || height <= 0)
                throw new UsageException($"capture size is empty ({width}x{height}).");
            string stamp = when.ToString("yyyy-MM-dd_HHmmss");
            return $"AgentEyes_{stamp}_{width}x{height}.png";
        }

        /// <summary>The full output path for a new capture of the given size, in the given folder.</summary>
        public static string PathFor(string saveFolder, int width, int height, DateTime when)
            => Path.Combine(saveFolder, FileNameFor(width, height, when));

        /// <summary>
        /// Capture an entire monitor (no overlay) into the given save folder. The image is saved as
        /// PNG and copied to the clipboard. Returns the saved CaptureInfo.
        /// </summary>
        public static CaptureInfo CaptureFullScreen(int screen, string? saveFolderOverride)
        {
            var mon = Monitors.Require(screen);
            Log.Info($"[CaptureService] CaptureFullScreen: screen={screen} bounds={mon.Width}x{mon.Height}");
            var info = SaveRect(mon.Bounds, saveFolderOverride);
            Log.Info($"[CaptureService] CaptureFullScreen: saved {info.File}");
            return info;
        }

        /// <summary>
        /// Capture an explicit virtual-desktop rectangle (the region the overlay returned, or a
        /// rect supplied by the Control API) into the given save folder. Saved as PNG and copied
        /// to the clipboard.
        /// </summary>
        public static CaptureInfo CaptureRegion(Drawing.Rectangle rect, string? saveFolderOverride)
        {
            Log.Info($"[CaptureService] CaptureRegion: rect={rect.Width}x{rect.Height} at ({rect.X},{rect.Y})");
            var info = SaveRect(rect, saveFolderOverride);
            Log.Info($"[CaptureService] CaptureRegion: saved {info.File}");
            return info;
        }

        private static CaptureInfo SaveRect(Drawing.Rectangle rect, string? saveFolderOverride)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new UsageException($"capture rectangle is empty ({rect.Width}x{rect.Height}).");

            string folder = ResolveSaveFolder(saveFolderOverride);
            Directory.CreateDirectory(folder);
            var now = DateTime.Now;
            string file = PathFor(folder, rect.Width, rect.Height, now);
            // Both halves of the contract: PNG on disk AND bitmap on the clipboard.
            Screenshot.CaptureRect(rect, file, copyToClipboard: true);
            return new CaptureInfo { File = file, Width = rect.Width, Height = rect.Height, CreatedLocal = now };
        }

        /// <summary>
        /// Every saved capture in the given save folder, newest first. The gallery is built from
        /// this. Missing folder = an empty list (no captures taken yet), not an error.
        /// </summary>
        public static IReadOnlyList<CaptureInfo> List(string? saveFolderOverride)
        {
            var list = new List<CaptureInfo>();
            string folder = ResolveSaveFolder(saveFolderOverride);
            if (!Directory.Exists(folder)) return list;
            foreach (var path in Directory.GetFiles(folder, "AgentEyes_*.png").OrderByDescending(p => p))
            {
                var (w, h) = ParseSize(Path.GetFileName(path));
                list.Add(new CaptureInfo
                {
                    File = path,
                    Width = w,
                    Height = h,
                    CreatedLocal = File.GetCreationTime(path),
                });
            }
            return list;
        }

        /// <summary>
        /// Parse "WxH" out of "AgentEyes_2026-06-09_140501_1920x1080.png". Returns (0,0) when the
        /// name does not carry a size (hand-renamed files); the gallery still lists them.
        /// Pure (no I/O) so it is unit-testable.
        /// </summary>
        public static (int Width, int Height) ParseSize(string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            int us = stem.LastIndexOf('_');
            if (us < 0) return (0, 0);
            string tail = stem[(us + 1)..];
            int x = tail.IndexOf('x');
            if (x <= 0 || x >= tail.Length - 1) return (0, 0);
            if (int.TryParse(tail[..x], out int w) && int.TryParse(tail[(x + 1)..], out int h))
                return (w, h);
            return (0, 0);
        }

        /// <summary>
        /// Whether the Capture-tab monitor picker is shown (AC11). With a single monitor there is
        /// nothing to choose, so the picker collapses. Pure logic, kept here so it is unit-testable
        /// without touching the WPF UI.
        /// </summary>
        public static bool ShouldShowMonitorPicker(int monitorCount) => monitorCount > 1;

        /// <summary>Delete a saved capture from disk. Returns true if a file was removed.</summary>
        public static bool Delete(string file)
        {
            Log.Info($"[CaptureService] Delete: {file}");
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }
}
