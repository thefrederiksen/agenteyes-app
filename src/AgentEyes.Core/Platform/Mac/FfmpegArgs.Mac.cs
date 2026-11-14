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
    /// ISSUE #111 STILL APPLIES, in AvfStream. The AVFoundation option set is audio_device_index,
    /// video_device_index, pixel_format, framerate, video_size, capture_cursor, capture_mouse_clicks,
    /// capture_raw_data, drop_late_frames and list_devices - there is no device-buffer option among
    /// them, in any ffmpeg release, and putting DirectShow's back made ffmpeg exit 8 before it opened
    /// a device. The microphone argument builder that guard was written around now lives in
    /// AvfStream.Arguments (issue #114 moved the macOS microphone into its own process), and
    /// MacForeignFfmpegOptionTests sweeps it there.
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
        /// A screen region through AVFoundation, muxed to MP4 (H.264) - the macOS twin of
        /// VideoCapture and X11VideoCapture.
        ///
        /// VIDEO ONLY, AND THAT IS THE FIX FOR ISSUE #114. Its Windows and Linux twins take the
        /// microphone as a second input of the SAME ffmpeg process. On macOS that topology destroys
        /// the audio: a screen+mic capture kept about 12% of its samples (a 30 s ask produced 35.3 s
        /// of video holding 4.0 s of audio). Measured on devthrottle-mac-mini, ffmpeg 9.0.2:
        ///
        ///  * mic alone, one ffmpeg               -> 12.000 s asked, 189 packets = 12.10 s. No loss.
        ///  * two avfoundation MICS, one ffmpeg   -> both streams complete. Not an "two inputs" limit.
        ///  * screen+mic, one ffmpeg, 320x240 out -> still 15% of the audio, encoder at speed=0.997x.
        ///  * screen+mic, one ffmpeg              -> 87.92% CPU IDLE. Not a starved machine.
        ///  * screen in one ffmpeg, mic in ANOTHER, at the same time, same devices
        ///                                        -> 25.000 s asked, 392 packets = 25.09 s. NO LOSS.
        ///
        /// So the loss is coupling INSIDE one ffmpeg process between an AVFoundation screen input and
        /// an AVFoundation audio input - ffmpeg's avfoundation demuxer holds exactly ONE sample
        /// buffer per device and silently discards whatever arrives before the reader takes it, and
        /// beside a screen input the mic was read once per ~2 s (exactly 4096 samples each time, in a
        /// burst every 1.7-2.3 s depending on video load). It is not the encoder, not the CPU, not
        /// the device, and not the frame rate. The surviving topology is the last line above, so on
        /// macOS the microphone is captured by its OWN ffmpeg (AvfStream, which is what the level
        /// meter already ran) and the deferred mux puts it into the recording.
        ///
        /// There is therefore NO microphone parameter here, by construction: this list cannot carry
        /// an AVFoundation audio input again. MacFfmpegArgsTests asserts exactly that.
        /// </summary>
        /// <param name="avfScreenIndex">The AVFoundation video index of the display (see AvfDevices).</param>
        /// <param name="screen">That display's full pixel size, which the region is cropped inside.</param>
        public static List<string> AvfVideoCapture(
            Drawing.Rectangle capture, int avfScreenIndex, Drawing.Size screen,
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
                // ASK THE DISPLAY FOR THE FORMAT THE ENCODER WANTS. The avfoundation demuxer's own
                // default for this option is yuv420p, which no display supports, so every macOS
                // capture logged "Selected pixel format (yuv420p) is not supported by the input
                // device / Overriding ... to use uyvy422 instead" and then spent a per-frame swscale
                // pass turning 16-bpp packed 4:2:2 into the 12-bpp planar 4:2:0 that x264 encodes.
                // nv12 is 4:2:0 already - the same subsampling as yuv420p, semi-planar - so the
                // conversion becomes a plane split instead of a resample, and the misleading log
                // lines go away. A display that does not offer nv12 is overridden exactly as before,
                // so this is never worse than the default it replaces.
                "-pixel_format", "nv12",
                "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                // "index:none" is video only - see the topology note above. The microphone is NOT an
                // input of this process on macOS.
                "-i", avfScreenIndex.ToString(CultureInfo.InvariantCulture) + ":none",
            };

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
                // DECLARE THE FRAME RATE ON THE OUTPUT (issue #114). An AVFoundation screen input
                // reports no frame rate of its own ("not enough frames to estimate rate"), so
                // without this the stream is carried on the demuxer's 1 us timebase and ffmpeg wrote
                // mp4 headers reading "1000k fps, 1000k tbn". That is not cosmetic: x264 sized the
                // file's level from it and said so - "MB rate (8160000000) > level limit (16711680)"
                // - stamping level 6.2 on 1080p30 footage, and a 1000k-tbn mp4 is awkward for players
                // and editors to seek. With -r the output is constant-rate at the rate that was
                // asked for, which is what the gdigrab and x11grab twins already produce.
                //
                // IT DOES NOT CREATE FRAMES, and must not be read as the fix for the capture rate.
                // AVFoundation screen delivery tops out near 13 fps on this machine whatever it is
                // asked for (measured: ask 10 -> 10.0, 15 -> 12.2, 20 -> 12.5, 30 -> 13.2,
                // 60 -> 14.0 fps), and no avfoundation option moves it - nv12, -capture_cursor 0 and
                // -drop_late_frames false were each measured and each changed nothing. Above ~13 fps
                // -r repeats the frame that was captured; the unique content is still ~13 fps. The
                // ceiling is ffmpeg's avfoundation indev, the only screen backend this ffmpeg has,
                // and raising it needs a different capture API (ScreenCaptureKit) - its own issue.
                "-r", fps.ToString(CultureInfo.InvariantCulture),
            });

            a.Add(outPath);
            if (previewStream) a.AddRange(PreviewOutput());
            return a;
        }

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
