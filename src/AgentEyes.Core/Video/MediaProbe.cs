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
