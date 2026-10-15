using System;
using System.Collections.Generic;
using System.Globalization;
using Drawing = System.Drawing;

namespace AgentEyes.Video
{
    /// <summary>
    /// The Linux capture inputs (issue #102): x11grab for the screen, pulse for a microphone, v4l2
    /// for a camera. Each builder mirrors its Windows twin (gdigrab / dshow) argument for argument -
    /// same encoder settings, same clamp-and-pad of an oversized region, same optional preview output -
    /// so the files the shared stop, mux and package stages receive are the same shape on both.
    /// Pure, so every command line is unit-tested without launching ffmpeg.
    /// </summary>
    internal static partial class FfmpegArgs
    {
        /// <summary>
        /// Screen region (x11grab) plus an optional microphone (pulse), muxed to MP4 (H.264 + AAC).
        /// </summary>
        /// <param name="display">The X display (DISPLAY), e.g. ":0".</param>
        /// <param name="pulseMic">The pulse SOURCE NAME of the microphone (alsa_input....), or null for video only.</param>
        public static List<string> X11VideoCapture(
            Drawing.Rectangle capture, string display, string? pulseMic, int fps, int crf, string outPath,
            Drawing.Rectangle? desktop = null, bool previewStream = false)
        {
            var (grab, padFilter) = GrabAndPad(capture, desktop);

            var a = new List<string>
            {
                "-y",
                "-f", "x11grab",
                "-thread_queue_size", "1024",
                "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{grab.Width}x{grab.Height}",
                "-draw_mouse", "1",
                "-i", Platform.Linux.LinuxDisplay.GrabInput(display, grab.X, grab.Y),
            };

            if (!string.IsNullOrWhiteSpace(pulseMic))
            {
                a.AddRange(PulseInput(pulseMic!));
            }

            if (padFilter != null)
            {
                a.Add("-vf");
                a.Add(padFilter);
            }

            a.AddRange(new[]
            {
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(CultureInfo.InvariantCulture),
            });

            if (!string.IsNullOrWhiteSpace(pulseMic))
            {
                a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
            }

            a.Add(outPath);
            if (previewStream) a.AddRange(PreviewOutput());
            return a;
        }

        /// <summary>
        /// A pulse input. Small fragments keep the server-side buffer short so a stop loses at most a
        /// fragment of the last words - the pulse form of the issue #125 dshow -audio_buffer_size guard.
        /// </summary>
        internal static List<string> PulseInput(string pulseSource) => new()
        {
            "-f", "pulse",
            "-thread_queue_size", "1024",
            "-fragment_size", "3840",   // 20 ms of 48 kHz stereo s16
            "-i", pulseSource,
        };

        /// <summary>A webcam (v4l2 device node) to camera.mp4, video only - the twin of CameraCapture.</summary>
        public static List<string> V4l2CameraCapture(
            string devicePath, int fps, int crf, string outPath, bool previewStream = false)
        {
            if (string.IsNullOrWhiteSpace(devicePath) || !devicePath.StartsWith("/dev/", StringComparison.Ordinal))
                throw new UsageException($"a Linux camera capture needs a /dev/video device node (got \"{devicePath}\").");

            var a = new List<string>
            {
                "-y",
                "-f", "v4l2",
                "-thread_queue_size", "1024",
                "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                "-i", devicePath,
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(CultureInfo.InvariantCulture),
                "-an",
                outPath,
            };

            if (previewStream) a.AddRange(PreviewOutput());
            return a;
        }

        /// <summary>One x11grab frame of a region as an image (marker shots, /screenshot).</summary>
        public static List<string> X11Still(Drawing.Rectangle rect, string display, string outPath)
        {
            var r = RegionMath.Evenize(rect);
            return new List<string>
            {
                "-y",
                "-f", "x11grab",
                "-video_size", $"{r.Width}x{r.Height}",
                "-draw_mouse", "1",
                "-i", Platform.Linux.LinuxDisplay.GrabInput(display, r.X, r.Y),
                "-frames:v", "1",
                "-update", "1",
                outPath,
            };
        }
    }
}
