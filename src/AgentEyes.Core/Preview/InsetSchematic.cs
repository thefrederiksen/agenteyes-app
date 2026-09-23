using System;

namespace AgentEyes.Preview
{
    /// <summary>
    /// WHERE THE CAMERA INSET SITS ON THE RECORDING, as a pure function (issue #43).
    ///
    /// The preset editor's "Size on screen" slider used to change a percentage and nothing else: the
    /// only drawing in the dialog was the circle over the LIVE CAMERA picture, which the inset
    /// fraction has no part in. A control that gives no feedback is indistinguishable from a broken
    /// one, and that is exactly the conclusion the first person to use it drew.
    ///
    /// This is the geometry behind the small schematic that fixes it - a 16:9 box standing for the
    /// recording, with the camera inset drawn in the chosen corner at the chosen fraction. It is a
    /// SCHEMATIC, not a composite (assumption F1): nothing here captures the screen, it only says
    /// where a box of the chosen size lands.
    ///
    /// It mirrors <c>HudWindow.LayOutInset</c> on purpose, so the picture in the editor and the inset
    /// the HUD actually draws are the same arithmetic:
    ///
    ///  - the inset's WIDTH is a fraction of the surface's width (that is what the slider means);
    ///  - a CIRCLE gets a SQUARE box, because a circle is round in pixels - an aspect-fitted box
    ///    would draw an oval;
    ///  - a RECTANGLE keeps the camera frame's own aspect;
    ///  - it is pushed into the chosen corner with a small margin.
    ///
    /// TWO DELIBERATE DIFFERENCES FROM THE HUD, both because this is a scale drawing:
    ///
    ///  - no <c>MinInsetWidth</c> pixel floor. The HUD floors the inset so it stays legible on a
    ///    small window; applying a pixel floor here would make the small end of the slider stop
    ///    shrinking and the drawing would no longer be to scale (AC4).
    ///  - the margin is a FRACTION of the box rather than 8 device pixels, for the same reason.
    ///
    /// The returned rectangle can extend past the box at the top of the slider's range - a circle
    /// 60% of a 16:9 frame's WIDTH is taller than that frame is HIGH. That is not a bug in this
    /// arithmetic, it is what the HUD does with the same numbers (its preview surface sets
    /// ClipToBounds), so the schematic clips it the same way and the person sees the truth: at 60%
    /// the camera really does fill the height of the recording.
    /// </summary>
    internal static class InsetSchematic
    {
        /// <summary>The shape of a recording when nothing better is known - the ordinary widescreen
        /// monitor. Used for the schematic box itself, and for a rectangle inset while the camera has
        /// not yet reported its own frame size.</summary>
        public const double DefaultFrameAspect = 16.0 / 9.0;

        /// <summary>How far the inset sits from the recording's edge, as a fraction of the box's
        /// width. The HUD uses a fixed 8px margin on a surface a few hundred pixels wide; this is the
        /// same gap expressed so it survives being drawn at any size.</summary>
        public const double MarginFraction = 0.025;

        /// <summary>
        /// Where the camera inset lands inside a schematic box of the given size, in that box's own
        /// pixels.
        /// </summary>
        /// <param name="boxWidth">Width of the box standing for the recording.</param>
        /// <param name="boxHeight">Height of that box.</param>
        /// <param name="overlay">The framing as the editor's controls currently read it.</param>
        /// <param name="cameraAspect">The camera frame's width/height, used only by the rectangle
        /// shape. Pass <see cref="DefaultFrameAspect"/> when the camera has not said yet.</param>
        public static OverlayRect Place(double boxWidth, double boxHeight,
                                        CameraOverlaySettings overlay, double cameraAspect)
        {
            if (overlay == null) throw new ArgumentNullException(nameof(overlay));
            RequirePositive(boxWidth, nameof(boxWidth));
            RequirePositive(boxHeight, nameof(boxHeight));
            RequirePositive(cameraAspect, nameof(cameraAspect));

            double width = boxWidth * overlay.ClampedInsetFraction;
            double height = overlay.ShapeValue == CameraOverlayShape.Circle
                ? width                     // a circle is round in pixels: a square box or nothing
                : width / cameraAspect;

            double margin = boxWidth * MarginFraction;
            var corner = overlay.CornerValue;

            double x = corner is PreviewCorner.TopLeft or PreviewCorner.BottomLeft
                ? margin
                : boxWidth - margin - width;
            double y = corner is PreviewCorner.TopLeft or PreviewCorner.TopRight
                ? margin
                : boxHeight - margin - height;

            return new OverlayRect(x, y, width, height);
        }

        private static void RequirePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentOutOfRangeException(name, value,
                    "the recording schematic needs a positive, finite size");
        }
    }
}
