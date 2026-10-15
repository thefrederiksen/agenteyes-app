using System;
#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
#endif

namespace AgentEyes.Video
{
    /// <summary>
    /// Issue #47: draws the grayscale alpha mask that turns the square camera inset into a circle.
    ///
    /// White is opaque and black is transparent, which is what ffmpeg's <c>alphamerge</c> reads out
    /// of the mask's luma. Drawing it once as an image is what keeps the compose fast - see the note
    /// on <see cref="ComposeArgs"/> about why <c>geq</c> was not used.
    /// </summary>
    internal static class CircleMask
    {
        /// <summary>
        /// Write a <paramref name="size"/> x <paramref name="size"/> PNG holding a white circle
        /// inscribed in a black square, antialiased at the edge so the composed inset does not have
        /// a staircase rim.
        /// </summary>
        public static void Write(string path, int size)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("required", nameof(path));
            if (size < 2) throw new ArgumentOutOfRangeException(nameof(size), size, "a mask needs at least 2 pixels");

            Log.Info($"[CircleMask] Write: path={path} size={size}");

#if !WINDOWS
            // Issue #102: Linux has no GDI+. ffmpeg draws the same white disc on black (geq picks
            // every pixel whose centre lies inside the circle), written as PNG.
            string r = (size / 2.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", $"color=c=black:s={size}x{size}",
                "-vf", $"format=gray,geq=lum='if(lte(pow(X+0.5-{r},2)+pow(Y+0.5-{r},2),pow({r}-0.5,2)),255,0)'",
                "-frames:v", "1", path,
            }, "circle mask");
#else
            using var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(Color.White);
                // Inset by half a pixel so the antialiased rim lands inside the bitmap rather than
                // being clipped flat against its edge.
                g.FillEllipse(brush, 0.5f, 0.5f, size - 1f, size - 1f);
            }
            bmp.Save(path, ImageFormat.Png);
#endif
        }
    }
}
