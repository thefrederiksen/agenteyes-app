using System;
using System.Diagnostics;
using System.Globalization;

namespace AgentEyes.Video
{
    /// <summary>Thin ffprobe wrapper to read a media file's duration (for manifests/verification).</summary>
    internal static class MediaProbe
    {
        public static double DurationSeconds(string mediaPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffprobe(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                mediaPath,
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);

            return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        }

        /// <summary>Which stream types a media file contains.</summary>
        public static (bool HasVideo, bool HasAudio) Streams(string mediaPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffprobe(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", mediaPath })
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (output.Contains("video"), output.Contains("audio"));
        }

        /// <summary>
        /// The first video stream's pixel dimensions. Issue #47 needs both the screen recording's and
        /// the camera's real size to place the inset, and neither can be assumed: the camera may be
        /// 4:3 while the screen is 16:9, and a region capture is neither.
        /// Throws when the file has no readable video stream - there is nothing to compose onto or from.
        /// </summary>
        public static (int Width, int Height) VideoSize(string mediaPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffprobe(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-of", "csv=p=0",
                mediaPath,
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);

            var parts = output.Split(',');
            if (parts.Length < 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                || w <= 0 || h <= 0)
            {
                throw new UsageException(
                    $"could not read the video size of {mediaPath} (ffprobe said: '{output}'). "
                    + "A composed video cannot be laid out without it.");
            }
            return (w, h);
        }

        /// <summary>Mean loudness in dB via ffmpeg volumedetect; ~-91 dB means digital silence.</summary>
        public static double MeanVolumeDb(string mediaPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-hide_banner", "-i", mediaPath, "-af", "volumedetect", "-f", "null", "-" })
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            var m = System.Text.RegularExpressions.Regex.Match(err, @"mean_volume:\s*(-?[\d.]+) dB");
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double db)
                ? db : -91.0;
        }
    }
}
