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
        /// and of the dshow audio input.
        ///
        /// AVFOUNDATION HAS NO DEVICE-BUFFER OPTION, OF ANY NAME (issue #111). This input used to
        /// pass dshow's buffer-length option as if it were the macOS form of the issue #125 guard,
        /// and it is not: that option belongs to DIRECTSHOW alone. ffmpeg answered "Unrecognized
        /// option" and exited 8 before it opened a device, so every macOS capture with audio failed
        /// instantly. The whole AVFoundation option set is audio_device_index, video_device_index,
        /// pixel_format, framerate, video_size, capture_cursor, capture_mouse_clicks,
        /// capture_raw_data, drop_late_frames and list_devices - there is no buffer length among
        /// them, in any ffmpeg release. So macOS does NOT get the issue #125 short-buffer
        /// mitigation; whether it needs some other end-of-recording guard is its own question, not
        /// something to invent here. Do not put a buffer option back: the guard in
        /// MacForeignFfmpegOptionTests fails if one reappears. (The option is not spelled out here
        /// on purpose - issue #111's first acceptance criterion is that grepping this folder for
        /// its name finds nothing at all.)
        /// </summary>
        internal static List<string> AvfAudioInput(int avfMicIndex) => new()
        {
            "-f", "avfoundation",
            "-thread_queue_size", "1024",
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
        /// The frame rate a still asks the display for. One frame is taken, so the number only has
        /// to be a rate the display accepts; this is the rate the recorder itself asks for, which
        /// keeps a marker shot on the same input configuration as the recording around it.
        /// </summary>
        internal const int StillFramerate = 30;

        /// <summary>
        /// One still of a region, through the SAME AVFoundation grabber the recording uses - the
        /// macOS twin of X11Still, and now built the same way for the same reason (issue #115).
        ///
        /// WHAT THIS REPLACED, AND WHY THE OLD REASONING WAS WRONG. Until #115 the macOS still shelled
        /// out to Apple's own /usr/sbin/screencapture, on the stated grounds that it was immediate and
        /// read "the SAME Screen Recording permission" as the recording. THAT LAST CLAUSE IS FALSE,
        /// and it was the whole basis of the divergence. TCC attributes a grant to the RESPONSIBLE
        /// process, and screencapture spawned by the launchd agent is not covered by the agent's
        /// grant. Measured on devthrottle-mac-mini 2026-11-14: with a recording running and this
        /// builder's ffmpeg writing frames of the screen successfully in that same process, at that
        /// same moment, screencapture exited 1 with "could not create image from rect" - so every
        /// marker shot on macOS failed, always, and the error blamed a permission that was granted
        /// and working. A still taken through ffmpeg uses the grabber that demonstrably has the grant.
        ///
        /// THE WARM-UP COST IS REAL AND WAS MEASURED, NOT DISMISSED. The old comment's other claim -
        /// that an AVFoundation still costs a capture-session warm-up of a second or more - stands:
        /// see docs/cencon/proof/issue-115/handoff.md for the per-call figures from the running
        /// service. It is a cost a working still pays and a broken one does not.
        ///
        /// THE "FRAME THAT NEVER ARRIVES" IS A REAL FAILURE MODE AND IS BOUNDED BY THE CALLER, not by
        /// this list. An AVFoundation screen capture started by a process WITHOUT the Screen Recording
        /// grant does not fail - it hangs forever, with no frame, no file and nothing on stderr but
        /// "Configuration of video device failed, falling back to default" (measured the same day from
        /// an unentitled shell: 15 s, no output, killed). Screenshot.CaptureRect therefore runs this
        /// under a hard timeout and treats expiry as the one condition that names the permission.
        ///
        /// THE REGION IS A CROP, as everywhere else on macOS: AVFoundation opens a whole display, so
        /// CropAndPad narrows the filter chain rather than the input - the same arithmetic, and the
        /// same pad of an oversized region, that AvfVideoCapture uses, so a marker shot frames the
        /// region exactly as the video does.
        /// </summary>
        /// <param name="capture">The wanted region, in display-local pixels.</param>
        /// <param name="avfScreenIndex">The AVFoundation video index of the display (see AvfDevices).</param>
        /// <param name="screen">That display's full pixel size, which the region is cropped inside.</param>
        public static List<string> AvfStill(
            Drawing.Rectangle capture, int avfScreenIndex, Drawing.Size screen, string outPath)
        {
            if (avfScreenIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(avfScreenIndex), "an AVFoundation index is zero or above");

            var (cropFilter, padFilter) = CropAndPad(capture, screen);

            var a = new List<string>
            {
                "-y",
                "-f", "avfoundation",
                // The pointer is part of what a recording is for, and a marker shot is a frame of
                // that recording - AvfVideoCapture draws it too.
                "-capture_cursor", "1",
                // Same reason as AvfVideoCapture: the demuxer's own default is yuv420p, which no
                // display supports, so without this every still logs an override to uyvy422 and then
                // resamples. nv12 is 4:2:0 already.
                "-pixel_format", "nv12",
                "-framerate", StillFramerate.ToString(CultureInfo.InvariantCulture),
                // Video only. There is no audio in a PNG, and a second AVFoundation input in this
                // process is exactly what issue #114 removed from the recorder.
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
                // One frame, written over the single output file rather than as a numbered sequence -
                // the same pair X11Still uses, so both platforms' stills are one PNG of one frame.
                "-frames:v", "1",
                "-update", "1",
            });

            a.Add(outPath);
            return a;
        }
    }
}
