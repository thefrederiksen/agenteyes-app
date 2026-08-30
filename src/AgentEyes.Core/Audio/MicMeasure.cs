using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentEyes.Video;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Measures the levels of an already-captured microphone track with one ffmpeg <c>astats</c>
    /// pass, so <see cref="GateCalibration"/> can choose a gate threshold from the real signal
    /// instead of a constant.
    ///
    /// This is affordable precisely because the mic chain runs AFTER the capture, over a file that
    /// is already complete on disk (raw.mp4 or the mic WAV). It is one extra decode with no output.
    /// </summary>
    internal static class MicMeasure
    {
        /// <summary>Marker that ends astats' per-channel sections and begins the summary.</summary>
        private const string OverallMarker = "Overall";

        private const string NoiseFloorKey = "Noise floor dB:";
        private const string RmsKey = "RMS level dB:";

        /// <summary>
        /// The overall noise floor and RMS of the first audio stream in <paramref name="mediaPath"/>.
        /// Throws when the file has no readable audio or astats does not report both figures - the
        /// caller must not proceed to gate on a measurement that did not happen.
        /// </summary>
        public static MicLevels Measure(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                throw new ArgumentException("a path is required", nameof(mediaPath));

            Log.Info($"[MicMeasure] Measure: path={mediaPath}");

            string stderr = RunAstats(mediaPath);
            var levels = ParseOverall(stderr);

            if (levels == null)
            {
                string tail = stderr.Length <= 600 ? stderr : "..." + stderr.Substring(stderr.Length - 600);
                string msg = $"could not measure the microphone levels of {mediaPath}: ffmpeg astats "
                           + $"did not report both '{NoiseFloorKey}' and '{RmsKey}' in its {OverallMarker} "
                           + $"section. The gate threshold is derived from this measurement and there "
                           + $"is no default to fall back on. ffmpeg said: {tail}";
                Log.Error($"[MicMeasure] Measure FAILED: {msg}");
                throw new UsageException(msg);
            }

            Log.Info($"[MicMeasure] Measure: {levels.Value}");
            return levels.Value;
        }

        /// <summary>
        /// Pulls the two figures out of astats' text output, reading only the summary section so a
        /// per-channel figure can never be mistaken for the overall one. Null when either is absent.
        /// </summary>
        internal static MicLevels? ParseOverall(string astatsOutput)
        {
            if (string.IsNullOrEmpty(astatsOutput)) return null;

            int overall = astatsOutput.LastIndexOf(OverallMarker, StringComparison.Ordinal);
            if (overall < 0) return null;

            string summary = astatsOutput.Substring(overall);
            double? floor = ValueAfter(summary, NoiseFloorKey);
            double? rms = ValueAfter(summary, RmsKey);

            return floor == null || rms == null ? null : new MicLevels(floor.Value, rms.Value);
        }

        /// <summary>The first finite number following <paramref name="key"/>, or null.</summary>
        private static double? ValueAfter(string text, string key)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;

            int start = at + key.Length;
            int end = start;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;

            string raw = text.Substring(start, end - start).Trim();
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return null;

            // astats reports digital silence as -inf; that is not a level we can calibrate against.
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
        }

        private static string RunAstats(string mediaPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "-hide_banner", "-nostats",
                "-i", mediaPath,
                "-map", "0:a",
                "-af", "astats=metadata=1:reset=0",
                "-f", "null", "-",
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return err.ToString();
        }
    }
}
