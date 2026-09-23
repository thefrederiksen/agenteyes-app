using System;
using System.Collections.Generic;
using System.Globalization;
using AgentEyes.Preview;

namespace AgentEyes.Video
{
    /// <summary>
    /// Issue #47: the ffmpeg command that renders the webcam into the screen recording.
    ///
    /// Pure argument building, like the rest of <see cref="FfmpegArgs"/>, so the filtergraph can be
    /// asserted in unit tests without launching ffmpeg or owning a camera.
    ///
    /// THE CIRCLE IS MASKED WITH AN IMAGE, NOT WITH <c>geq</c>, and that is a performance decision
    /// rather than a stylistic one. <c>geq</c> evaluates an expression per pixel per frame: a 576px
    /// inset over a 160 second 30fps take is about 1.6 billion evaluations, which turns a fast
    /// compose into a multi-minute one. A pre-drawn grayscale circle fed through <c>alphamerge</c>
    /// costs one scale.
    /// </summary>
    internal static class ComposeArgs
    {
        /// <summary>x264 preset for the compose pass - the same one the capture uses.</summary>
        public const string Preset = "veryfast";

        /// <summary>
        /// Compose <paramref name="cameraMp4"/> over <paramref name="screenMp4"/>.
        /// </summary>
        /// <param name="screenMp4">The screen recording. Its audio is copied through untouched.</param>
        /// <param name="cameraMp4">The full-frame camera track. Never modified.</param>
        /// <param name="maskPng">Grayscale circle mask, or null for a rectangular inset.</param>
        /// <param name="outMp4">Where the composed video is written.</param>
        /// <param name="c">The geometry, already resolved against both real frame sizes.</param>
        /// <param name="cameraStartOffsetSeconds">
        /// The manifest's CameraStartOffsetSeconds: how far the camera started AFTER the screen.
        /// Negative is the normal case (the camera opens first), and means the camera file already
        /// contains footage from before the screen recording began, which must be skipped.
        /// </param>
        /// <param name="crf">x264 quality for the composed output.</param>
        public static List<string> CameraInset(
            string screenMp4, string cameraMp4, string? maskPng, string outMp4,
            CameraComposition c, double cameraStartOffsetSeconds, int crf)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (string.IsNullOrWhiteSpace(screenMp4)) throw new ArgumentException("required", nameof(screenMp4));
            if (string.IsNullOrWhiteSpace(cameraMp4)) throw new ArgumentException("required", nameof(cameraMp4));
            if (string.IsNullOrWhiteSpace(outMp4)) throw new ArgumentException("required", nameof(outMp4));
            if (c.Circular && string.IsNullOrWhiteSpace(maskPng))
                throw new UsageException(
                    "a circular camera inset needs a mask image - the caller must draw one before "
                    + "building the compose command");

            var a = new List<string> { "-y", "-i", screenMp4 };

            // Alignment. A negative offset means the camera was already running when the screen
            // capture started, so that head has to come off the camera before anything lines up.
            // An input seek (before -i) is the cheap one: ffmpeg decodes from the keyframe rather
            // than decoding and discarding the whole head.
            double skip = -cameraStartOffsetSeconds;
            if (skip > 0) { a.Add("-ss"); a.Add(Seconds(skip)); }
            a.Add("-i");
            a.Add(cameraMp4);

            if (c.Circular)
            {
                // A still image: loop it so it never becomes the reason the output ends.
                a.Add("-loop"); a.Add("1");
                a.Add("-i"); a.Add(maskPng!);
            }

            // A positive offset means the camera started LATE, so the inset must appear late too.
            //
            // TWO PARTS, and the second is the one that was missing (Review Gate round 1, defect 3).
            // tpad shifts the camera's timing by padding its head with black frames - but padding
            // CONTENT is not the same as delaying the OVERLAY, and those black frames were composited
            // like any other, painting an opaque black box over the screen for the first second of a
            // recording whose camera started late. The enable expression below is what actually
            // withholds the inset until there is camera footage to draw; before then the screen is
            // passed through untouched, which is the only honest thing to show.
            string delay = cameraStartOffsetSeconds > 0
                ? $"tpad=start_duration={Seconds(cameraStartOffsetSeconds)}:start_mode=add:color=black,"
                : "";
            string appear = cameraStartOffsetSeconds > 0
                ? $":enable='gte(t,{Seconds(cameraStartOffsetSeconds)})'"
                : "";

            string crop = $"crop={Num(c.CameraCrop.Width)}:{Num(c.CameraCrop.Height)}:"
                        + $"{Num(c.CameraCrop.X)}:{Num(c.CameraCrop.Y)}";
            string scale = $"scale={c.InsetWidth}:{c.InsetHeight}";

            string fc;
            if (c.Circular)
            {
                fc = $"[1:v]{delay}{crop},{scale},format=rgba[cam];"
                   + $"[2:v]{scale},format=gray[mask];"
                   + "[cam][mask]alphamerge[inset];"
                   + $"[0:v][inset]overlay={c.X}:{c.Y}:eof_action=pass{appear}[v]";
            }
            else
            {
                fc = $"[1:v]{delay}{crop},{scale}[inset];"
                   + $"[0:v][inset]overlay={c.X}:{c.Y}:eof_action=pass{appear}[v]";
            }

            a.AddRange(new[]
            {
                "-filter_complex", fc,
                "-map", "[v]",
                // The screen recording's audio passes through byte for byte. Composing is a video
                // operation and must not re-encode, re-level or re-gate what was already produced.
                "-map", "0:a?", "-c:a", "copy",
                "-c:v", "libx264",
                "-preset", Preset,
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(CultureInfo.InvariantCulture),
                outMp4,
            });
            return a;
        }

        /// <summary>A pixel coordinate: whole numbers, because pixels are whole numbers.</summary>
        private static string Num(double d) =>
            Math.Round(d).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// A duration in seconds, to the millisecond. NOT rounded to whole seconds - the offset this
        /// formats is typically a fraction of a second (-0.855 on the take that motivated the
        /// feature) and rounding it away would misalign the face by most of a second.
        /// </summary>
        private static string Seconds(double d) =>
            d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
