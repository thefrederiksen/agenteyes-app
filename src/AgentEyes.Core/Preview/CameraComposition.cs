using System;

namespace AgentEyes.Preview
{
    /// <summary>
    /// Issue #47: where the webcam actually lands in the composed video, in PIXELS.
    ///
    /// <see cref="CameraOverlaySettings"/> is a framing CHOICE in fractions - it survives a camera
    /// being swapped or a monitor changing resolution. This turns that choice into the concrete
    /// rectangle a renderer needs, given a real screen size and a real camera size. It is a pure
    /// function so the geometry can be tested without launching ffmpeg, which matters because
    /// getting it wrong puts the face half off the screen and no unit test would notice.
    ///
    /// Two things it does NOT do, both deliberate:
    ///  - it never changes camera.mp4, which stays the full rectangular frame (issue #36, E1);
    ///  - it does not decide the mask, only the geometry. <see cref="Circular"/> says which shape
    ///    the renderer should cut, and the crop is the circle's bounding square either way.
    /// </summary>
    internal sealed class CameraComposition
    {
        /// <summary>
        /// How far the inset sits from the edges it hugs, as a fraction of the screen's width.
        /// Small enough to read as "in the corner", large enough not to look like a mistake.
        /// </summary>
        public const double MarginFraction = 0.02;

        private CameraComposition(
            OverlayRect cameraCrop, int insetWidth, int insetHeight, int x, int y, bool circular)
        {
            CameraCrop = cameraCrop;
            InsetWidth = insetWidth;
            InsetHeight = insetHeight;
            X = x;
            Y = y;
            Circular = circular;
        }

        /// <summary>The rectangle to take OUT of the camera frame, in camera pixels.</summary>
        public OverlayRect CameraCrop { get; }

        /// <summary>The inset's size in output pixels. Always even - yuv420p needs even dimensions.</summary>
        public int InsetWidth { get; }

        public int InsetHeight { get; }

        /// <summary>Top-left of the inset in output pixels. Always even, for the same reason.</summary>
        public int X { get; }

        public int Y { get; }

        /// <summary>True when the inset should be masked to a circle inscribed in its bounds.</summary>
        public bool Circular { get; }

        public int Right => X + InsetWidth;

        public int Bottom => Y + InsetHeight;

        /// <summary>
        /// Work out the composition for a real screen and a real camera.
        /// </summary>
        /// <param name="screenWidth">Composed output width in pixels (the screen recording's width).</param>
        /// <param name="screenHeight">Composed output height in pixels.</param>
        /// <param name="cameraWidth">camera.mp4's width in pixels.</param>
        /// <param name="cameraHeight">camera.mp4's height in pixels.</param>
        /// <param name="overlay">The framing chosen before recording and stored in the manifest.</param>
        public static CameraComposition For(
            int screenWidth, int screenHeight, int cameraWidth, int cameraHeight,
            CameraOverlaySettings overlay)
        {
            if (overlay == null) throw new ArgumentNullException(nameof(overlay));
            RequirePositive(screenWidth, nameof(screenWidth));
            RequirePositive(screenHeight, nameof(screenHeight));
            RequirePositive(cameraWidth, nameof(cameraWidth));
            RequirePositive(cameraHeight, nameof(cameraHeight));

            var settings = overlay.Canonical();
            bool circular = settings.ShapeValue == CameraOverlayShape.Circle;

            // What comes out of the camera frame.
            OverlayRect crop = circular
                ? settings.Circle.PixelBounds(cameraWidth, cameraHeight)
                : new OverlayRect(0, 0, cameraWidth, cameraHeight);

            // How big it is on the screen. A circle's bounding square is square, so the inset is
            // square too; a rectangle keeps the camera's own proportions.
            double wantWidth = screenWidth * settings.ClampedInsetFraction;
            double wantHeight = circular ? wantWidth : wantWidth * (crop.Height / crop.Width);

            // An inset can never be larger than the frame it sits in, however the fractions were set.
            // SHRINK BOTH AXES BY THE SAME FACTOR (Review Gate round 1, defect 2). Clamping them
            // independently turned a circle into an ellipse the moment only one axis was too big:
            // on a 3840x1080 output at the supported maximum inset of 0.60, the width wanted 2304
            // and the height was cut to 1080, and the round mask was then stretched across it.
            double fit = Math.Min(1.0, Math.Min(screenWidth / wantWidth, screenHeight / wantHeight));
            int insetWidth = Even((int)Math.Round(wantWidth * fit));
            int insetHeight = circular ? insetWidth : Even((int)Math.Round(wantHeight * fit));

            if (insetWidth < 2) insetWidth = 2;
            if (insetHeight < 2) insetHeight = 2;
            if (circular) insetHeight = insetWidth;   // squareness is the whole property here

            int margin = Even((int)Math.Round(screenWidth * MarginFraction));

            // With a big inset and a small screen the margin would push it off the far edge; the
            // inset staying fully visible matters more than the margin being exact.
            margin = Math.Max(0, Math.Min(margin, Math.Min(screenWidth - insetWidth, screenHeight - insetHeight) / 2));

            int left = margin;
            int right = Even(screenWidth - insetWidth - margin);
            int top = margin;
            int bottom = Even(screenHeight - insetHeight - margin);

            (int x, int y) = settings.CornerValue switch
            {
                PreviewCorner.TopLeft => (left, top),
                PreviewCorner.TopRight => (right, top),
                PreviewCorner.BottomLeft => (left, bottom),
                PreviewCorner.BottomRight => (right, bottom),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(overlay), settings.Corner, "unknown overlay corner"),
            };

            return new CameraComposition(crop, insetWidth, insetHeight,
                Math.Max(0, x), Math.Max(0, y), circular);
        }

        /// <summary>Round DOWN to an even number: yuv420p cannot express odd sizes or offsets.</summary>
        private static int Even(int value) => value - (value % 2);

        private static void RequirePositive(int value, string name)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(name, value, "a frame size must be positive");
        }

        public override string ToString() =>
            $"{(Circular ? "circle" : "rectangle")} {InsetWidth}x{InsetHeight} at ({X},{Y}) "
            + $"from camera crop {CameraCrop}";
    }
}
