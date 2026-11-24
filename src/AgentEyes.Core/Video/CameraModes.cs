using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentEyes.Video
{
    /// <summary>
    /// One capture mode a DirectShow camera offers, as ffmpeg's <c>-list_options true</c> prints it
    /// (issue #229). Exactly one of <see cref="Codec"/> (a compressed mode: mjpeg, h264) and
    /// <see cref="PixelFormat"/> (a raw mode: yuyv422, nv12) is set.
    /// </summary>
    internal sealed record CameraMode(string? Codec, string? PixelFormat, int Width, int Height, double MaxFps)
    {
        /// <summary>What the mode is called in a log line, the manifest and /status: "mjpeg 1920x1080@30".</summary>
        public string Describe() =>
            $"{Codec ?? PixelFormat} {Width}x{Height}@{MaxFps.ToString("0.##", CultureInfo.InvariantCulture)}";

        /// <summary>The input codec name for the manifest: the compressed codec, or the raw pixel format.</summary>
        public string InputCodec => Codec ?? PixelFormat ?? "unknown";
    }

    /// <summary>
    /// Issue #229: choose the camera mode to record from the camera's OWN list of modes, so a 1080p
    /// webcam records at 1080p instead of DirectShow's default (raw YUY2 640x480 on a C920).
    ///
    /// The rule: the largest frame the device can deliver at the requested frame rate. At equal size
    /// a raw mode beats a compressed one (no decode), and mjpeg beats h264 (every ffmpeg decodes
    /// mjpeg; the dshow h264 pin is less reliable). Choosing from what the device lists - rather than
    /// pinning a size - keeps assumption A2 of issue #28: a camera that lacks a mode never fails to open.
    /// </summary>
    internal static class CameraModes
    {
        // [dshow @ ...]   vcodec=mjpeg  min s=1920x1080 fps=5 max s=1920x1080 fps=30
        // [in#0 @ ...]   pixel_format=yuyv422  min s=640x480 fps=5 max s=640x480 fps=30 (tv, ...)
        private static readonly Regex ModeRx = new(
            @"(?:(vcodec)|(pixel_format))=(\S+)\s+min s=\d+x\d+ fps=[\d.]+\s+max s=(\d+)x(\d+) fps=([\d.]+)",
            RegexOptions.Compiled);

        /// <summary>Pure parser for the stderr of <c>ffmpeg -f dshow -list_options true -i video=NAME</c>.
        /// Duplicates (ffmpeg prints most modes twice, once with colour details) are dropped.</summary>
        public static IReadOnlyList<CameraMode> Parse(string ffmpegStderr)
        {
            var modes = new List<CameraMode>();
            if (string.IsNullOrEmpty(ffmpegStderr)) return modes;
            foreach (Match m in ModeRx.Matches(ffmpegStderr))
            {
                bool compressed = m.Groups[1].Success;
                string name = m.Groups[3].Value;
                var mode = new CameraMode(
                    compressed ? name : null,
                    compressed ? null : name,
                    int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture));
                if (!modes.Contains(mode)) modes.Add(mode);
            }
            return modes;
        }

        /// <summary>
        /// The mode to record at <paramref name="fps"/>, or - when <paramref name="width"/> and
        /// <paramref name="height"/> are given - that exact size. Throws a <see cref="UsageException"/>
        /// listing what the device DOES offer when nothing fits, so a wrong request is refused before
        /// the recording starts instead of failing inside ffmpeg.
        /// </summary>
        public static CameraMode Choose(IReadOnlyList<CameraMode> modes, int fps, int? width = null, int? height = null)
        {
            if (modes == null || modes.Count == 0)
                throw new UsageException("the camera listed no capture modes, so no mode can be chosen for it. "
                                         + "Check that it works in another app (Camera) and that nothing else holds it.");

            var fits = modes.Where(m => m.MaxFps + 0.01 >= fps)
                            .Where(m => width == null || (m.Width == width && m.Height == height))
                            .ToList();
            if (fits.Count == 0)
            {
                string want = width == null ? $"{fps} fps" : $"{width}x{height} at {fps} fps";
                throw new UsageException($"the camera offers no mode for {want}. It offers: "
                                         + string.Join(", ", modes.Select(m => m.Describe()).Distinct()));
            }

            return fits.OrderByDescending(m => (long)m.Width * m.Height)
                       .ThenBy(Preference)
                       .First();
        }

        private static int Preference(CameraMode m) => m.Codec switch
        {
            null => 0,       // raw: no decode at all
            "mjpeg" => 1,
            _ => 2,          // h264 and anything else
        };

#if WINDOWS
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, IReadOnlyList<CameraMode>> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The modes this DirectShow camera offers, read once per process per camera (listing opens
        /// the device, so it is not repeated before every take). Must not run while a recording or a
        /// preview holds the camera - callers release it first.
        /// </summary>
        public static IReadOnlyList<CameraMode> List(string dshowCameraName)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(dshowCameraName, out var cached)) return cached;
            }

            Log.Info($"[CameraModes] List: reading the modes of \"{dshowCameraName}\"");
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "-hide_banner", "-f", "dshow", "-list_options", "true", "-i", $"video={dshowCameraName}" })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg could not be started to list the camera's modes");
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch (Exception ex) { Log.Error("[CameraModes] List: kill after timeout failed", ex); }
                throw new UsageException($"listing the modes of the camera \"{dshowCameraName}\" did not finish in 15 seconds.");
            }
            string stderr = stderrTask.GetAwaiter().GetResult();
            var modes = Parse(stderr);
            Log.Info($"[CameraModes] List: \"{dshowCameraName}\" offers {modes.Count} mode(s)");
            if (modes.Count > 0)
            {
                lock (CacheLock) Cache[dshowCameraName] = modes;
            }
            else
            {
                Log.Warn($"[CameraModes] List: no modes parsed for \"{dshowCameraName}\"; ffmpeg said: {stderr}");
            }
            return modes;
        }
#endif
    }
}
