using System;
using System.Collections.Generic;
using System.Globalization;
using Drawing = System.Drawing;

namespace AgentEyes.Video
{
    /// <summary>
    /// The macOS capture inputs: AVFoundation for the screen, the microphone and the camera. Each
    /// builder mirrors its Windows twin (gdigrab / dshow) and its Linux twin (x11grab / pulse / v4l2)
    /// argument for argument - same encoder settings, same clamp-and-pad of an oversized region, same
    /// optional preview output - so the files the shared stop, mux and package stages receive are the
    /// same shape on all three. Pure, so every command line is unit-tested without launching ffmpeg
    /// and without the Screen Recording permission.
    ///
    /// THE ONE STRUCTURAL DIFFERENCE. gdigrab and x11grab take a region as an INPUT OFFSET into one
    /// big desktop. AVFoundation cannot: it opens a whole display and the region has to be a crop
    /// FILTER applied to those frames. So where the Windows and Linux builders narrow the input, this
    /// one grabs the display and narrows the filter chain. The visible result is identical; the
    /// argument list is not, which is why this is its own builder rather than a parameter on theirs.
    /// </summary>
    internal static partial class FfmpegArgs
    {
        /// <summary>
        /// What a region becomes on macOS: a crop inside the display, and a pad back to the asked-for
        /// size when the region hangs off the edge. Pure and unit-tested, and the pad arithmetic is
        /// deliberately the same as <see cref="GrabAndPad"/>'s so an oversized social-format region
        /// comes out the same size on every platform.
        /// </summary>
        /// <param name="capture">The wanted region, in display-local pixels.</param>
        /// <param name="screen">The display's full pixel size.</param>
        internal static (string? CropFilter, string? PadFilter) CropAndPad(
            Drawing.Rectangle capture, Drawing.Size screen)
        {
            var target = RegionMath.Evenize(capture);
            var display = new Drawing.Rectangle(0, 0, screen.Width, screen.Height);

            var raw = Drawing.Rectangle.Intersect(target, display);
            if (raw.Width < 2 || raw.Height < 2)
            {
                throw new UsageException(
                    $"the capture region {target.Width}x{target.Height} at ({target.X},{target.Y}) "
                    + $"does not overlap the display ({screen.Width}x{screen.Height}) - nothing to "
                    + "capture. On macOS a region is a crop inside ONE display; a region that spans "
                    + "two monitors cannot be recorded.");
            }

            var fit = RegionMath.Evenize(raw);

            // A crop is needed whenever the kept area is not the whole display.
            string? cropFilter = fit == display
                ? null
                : $"crop={fit.Width}:{fit.Height}:{fit.X}:{fit.Y}";

            string? padFilter = null;
            if (fit != target)
            {
                int ox = fit.X - target.X;
                int oy = fit.Y - target.Y;
                // yuv420p pad offsets must be even; round down (shifts content <=1px, chroma-aligned).
                ox -= ox % 2;
                oy -= oy % 2;
                if (ox < 0) ox = 0;
                if (oy < 0) oy = 0;
                if (ox + fit.Width > target.Width) ox = target.Width - fit.Width;
                if (oy + fit.Height > target.Height) oy = target.Height - fit.Height;
                padFilter = $"pad={target.Width}:{target.Height}:{ox}:{oy}:black";
            }

            return (cropFilter, padFilter);
        }

        /// <summary>Join the crop and pad stages into one -vf chain, or null when neither is needed.</summary>
        internal static string? FilterChain(string? cropFilter, string? padFilter)
        {
            if (cropFilter == null) return padFilter;
            if (padFilter == null) return cropFilter;
            return cropFilter + "," + padFilter;
        }

        /// <summary>
        /// A screen region through AVFoundation plus an optional microphone, muxed to MP4
        /// (H.264 + AAC) - the macOS twin of VideoCapture and X11VideoCapture.
        /// </summary>
        /// <param name="avfScreenIndex">The AVFoundation video index of the display (see AvfDevices).</param>
        /// <param name="screen">That display's full pixel size, which the region is cropped inside.</param>
        /// <param name="avfMicIndex">The AVFoundation AUDIO index of the microphone, or null for video only.</param>
        public static List<string> AvfVideoCapture(
            Drawing.Rectangle capture, int avfScreenIndex, Drawing.Size screen, int? avfMicIndex,
            int fps, int crf, string outPath, bool previewStream = false)
        {
            if (avfScreenIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(avfScreenIndex), "an AVFoundation index is zero or above");

            var (cropFilter, padFilter) = CropAndPad(capture, screen);

            var a = new List<string>
            {
                "-y",
                "-f", "avfoundation",
                "-thread_queue_size", "1024",
                // The pointer is part of what a recording is for; gdigrab and x11grab both draw it.
                "-capture_cursor", "1",
                "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                // "index:none" is video only. The microphone, when there is one, is its OWN input
                // below, as the pulse microphone is on Linux - separate inputs keep the audio queue
                // and the video queue from sharing one buffer.
                "-i", avfScreenIndex.ToString(CultureInfo.InvariantCulture) + ":none",
            };

            if (avfMicIndex != null) a.AddRange(AvfAudioInput(avfMicIndex.Value));

            string? chain = FilterChain(cropFilter, padFilter);
            if (chain != null)
            {
                a.Add("-vf");
                a.Add(chain);
            }

            a.AddRange(new[]
            {
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(CultureInfo.InvariantCulture),
            });

            if (avfMicIndex != null) a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });

            a.Add(outPath);
            if (previewStream) a.AddRange(PreviewOutput());
            return a;
        }

        /// <summary>
        /// An AVFoundation audio input. ":index" is audio with no video. The macOS twin of PulseInput
        /// and of the dshow -audio_buffer_size guard (issue #125): a short device buffer means a stop
        /// loses at most a fragment of the last words.
        /// </summary>
        internal static List<string> AvfAudioInput(int avfMicIndex) => new()
        {
            "-f", "avfoundation",
            "-thread_queue_size", "1024",
            "-audio_buffer_size", "20",   // milliseconds
            "-i", ":" + avfMicIndex.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>A webcam through AVFoundation to camera.mp4, video only - the twin of CameraCapture.</summary>
        public static List<string> AvfCameraCapture(
            int avfCameraIndex, int fps, int crf, string outPath, bool previewStream = false)
        {
            if (avfCameraIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(avfCameraIndex), "an AVFoundation index is zero or above");

            var a = new List<string>
            {
                "-y",
                "-f", "avfoundation",
                "-thread_queue_size", "1024",
                "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                "-i", avfCameraIndex.ToString(CultureInfo.InvariantCulture) + ":none",
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

        /// <summary>
        /// One still of a region, through macOS' own screencapture tool rather than through ffmpeg.
        ///
        /// WHY THIS ONE DIVERGES from the Linux still (which deliberately uses the same grabber as the
        /// recording): an AVFoundation still costs a capture-session warm-up of a second or more and,
        /// asked for a single frame, can sit waiting for one that never arrives. screencapture is the
        /// system's own screenshot tool, it is immediate, and it reads the SAME Screen Recording
        /// permission - so a marker shot still shows what a recording would show.
        /// </summary>
        public static List<string> ScreencaptureStill(Drawing.Rectangle rect, string outPath)
        {
            var r = RegionMath.Evenize(rect);
            return new List<string>
            {
                "-x",                                     // silent: no camera shutter sound
                "-t", "png",
                "-R", string.Format(
                    CultureInfo.InvariantCulture, "{0},{1},{2},{3}", r.X, r.Y, r.Width, r.Height),
                outPath,
            };
        }
    }
}
