using System;

namespace AgentEyes.Preview
{
    /// <summary>
    /// The shape the camera is framed in when it is inset over the screen (issue #36).
    ///
    /// CIRCLE IS THE DEFAULT, and the reason is the whole point of the issue: a webcam frame is
    /// mostly desk, wall and shoulders, so a rectangular inset covers a large piece of the screen
    /// recording in order to show a small face. A circle around the face reads at a fraction of the
    /// area. <see cref="Rectangle"/> is today's behaviour, kept for anyone who wants it.
    /// </summary>
    internal enum CameraOverlayShape
    {
        /// <summary>A round bust shot cropped out of the camera frame (issue #36, the default).</summary>
        Circle,

        /// <summary>The whole rectangular camera frame, inset - what issue #33 shipped.</summary>
        Rectangle,
    }

    /// <summary>
    /// A plain rectangle of doubles, used for both NORMALISED rectangles (fractions of a frame, the
    /// units a WPF ImageBrush viewbox wants) and PIXEL rectangles. It carries no units of its own -
    /// the method that returns one says which it is - and it exists so this geometry can be computed
    /// and tested without dragging WPF's <c>Rect</c> into AgentEyes.Core.
    /// </summary>
    internal readonly struct OverlayRect
    {
        public OverlayRect(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        public double Right => X + Width;
        public double Bottom => Y + Height;
        public double CentreX => X + Width / 2.0;
        public double CentreY => Y + Height / 2.0;

        public override string ToString() =>
            $"{X:0.####},{Y:0.####} {Width:0.####}x{Height:0.####}";
    }

    /// <summary>
    /// WHERE THE CIRCLE SITS IN THE CAMERA FRAME (issue #36, assumption E2) - a centre and a
    /// diameter, both stored as FRACTIONS of the frame rather than pixels, so a preset survives the
    /// camera being swapped or the resolution changing.
    ///
    /// The units are deliberately asymmetric and it matters:
    ///
    ///  - <see cref="CentreX"/> is a fraction of the frame WIDTH, <see cref="CentreY"/> a fraction of
    ///    the frame HEIGHT - the natural reading of "where in the picture".
    ///  - <see cref="Diameter"/> is a fraction of the frame HEIGHT for BOTH axes, because a circle is
    ///    round in PIXELS. Normalising it per-axis would make it an ellipse the moment the frame was
    ///    not square.
    ///
    /// IT IS A FRAMING CHOICE, NOT A CROP (assumption E1). Nothing here changes camera.mp4, which
    /// keeps recording the full rectangular frame; this is what the preview draws and what
    /// manifest.json records, so a later edit can reproduce the framing - and can move it, because
    /// the pixels outside the circle were never thrown away.
    /// </summary>
    internal sealed class CameraOverlayCircle
    {
        /// <summary>Smallest circle that is still a face rather than an eye, as a fraction of frame height.</summary>
        public const double MinDiameter = 0.10;

        /// <summary>Largest circle: the full frame height.</summary>
        public const double MaxDiameter = 1.00;

        /// <summary>Issue #36, assumption E3: horizontally centred.</summary>
        public const double DefaultCentreX = 0.50;

        /// <summary>Issue #36, assumption E3: the upper portion of the frame, where a seated
        /// speaker's head usually sits. A STARTING POINT that still needs adjusting - it is not an
        /// attempt at auto-framing, which is explicitly out of scope.</summary>
        public const double DefaultCentreY = 0.42;

        /// <summary>Issue #36, assumption E3: roughly 60% of the frame height.</summary>
        public const double DefaultDiameter = 0.60;

        public double CentreX { get; set; } = DefaultCentreX;
        public double CentreY { get; set; } = DefaultCentreY;
        public double Diameter { get; set; } = DefaultDiameter;

        public CameraOverlayCircle Clone() =>
            new() { CentreX = CentreX, CentreY = CentreY, Diameter = Diameter };

        /// <summary>
        /// The same circle with every number brought into range, WITHOUT knowing the frame's shape:
        /// the diameter into [<see cref="MinDiameter"/>, <see cref="MaxDiameter"/>] and both centres
        /// into [0, 1]. This is what is stored and written to the manifest - it is the person's own
        /// choice, canonicalised, not fitted to any particular camera.
        /// </summary>
        public CameraOverlayCircle Canonical()
        {
            Require(CentreX, nameof(CentreX));
            Require(CentreY, nameof(CentreY));
            Require(Diameter, nameof(Diameter));
            return new CameraOverlayCircle
            {
                CentreX = Clamp(CentreX, 0.0, 1.0),
                CentreY = Clamp(CentreY, 0.0, 1.0),
                Diameter = Clamp(Diameter, MinDiameter, MaxDiameter),
            };
        }

        /// <summary>
        /// The same circle fitted to a REAL frame: shrunk if it cannot fit and nudged until the whole
        /// circle is inside the picture. Clamping happens here, at the moment something is drawn,
        /// rather than when the choice is stored - a preset made against a 16:9 camera keeps its
        /// numbers when it is opened against a 4:3 one, and each render fits them to what it has.
        /// </summary>
        /// <param name="frameWidth">Frame width in pixels (or any unit, as long as both match).</param>
        /// <param name="frameHeight">Frame height in the same unit.</param>
        public CameraOverlayCircle ClampedTo(double frameWidth, double frameHeight)
        {
            RequirePositive(frameWidth, nameof(frameWidth));
            RequirePositive(frameHeight, nameof(frameHeight));

            var c = Canonical();
            double aspect = frameWidth / frameHeight;

            // A circle is round in pixels, so its height fraction is capped by the frame's height AND
            // by its width expressed in height-units (that is what the aspect is).
            double maxDiameter = Math.Min(MaxDiameter, aspect);
            double diameter = Math.Min(c.Diameter, maxDiameter);

            double halfY = diameter / 2.0;
            double halfX = diameter / (2.0 * aspect);

            return new CameraOverlayCircle
            {
                CentreX = Clamp(c.CentreX, halfX, 1.0 - halfX),
                CentreY = Clamp(c.CentreY, halfY, 1.0 - halfY),
                Diameter = diameter,
            };
        }

        /// <summary>
        /// The circle's bounding square as a NORMALISED rectangle of the frame - exactly the
        /// <c>Viewbox</c> a WPF ImageBrush wants with relative units, and exactly the crop a later
        /// edit would apply. Fitted to the frame first, so it is always inside [0, 1] on both axes.
        /// </summary>
        public OverlayRect Viewbox(double frameWidth, double frameHeight)
        {
            var c = ClampedTo(frameWidth, frameHeight);
            double aspect = frameWidth / frameHeight;
            double height = c.Diameter;
            double width = c.Diameter / aspect;
            return new OverlayRect(c.CentreX - width / 2.0, c.CentreY - height / 2.0, width, height);
        }

        /// <summary>The circle's bounding square in PIXELS of a frame of the given size.</summary>
        public OverlayRect PixelBounds(double frameWidth, double frameHeight)
        {
            var vb = Viewbox(frameWidth, frameHeight);
            return new OverlayRect(vb.X * frameWidth, vb.Y * frameHeight,
                                   vb.Width * frameWidth, vb.Height * frameHeight);
        }

        /// <summary>True when the two circles describe the same framing to within a rounding tick.</summary>
        public bool SameAs(CameraOverlayCircle other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            return Near(CentreX, other.CentreX) && Near(CentreY, other.CentreY) && Near(Diameter, other.Diameter);
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

        internal static double Clamp(double value, double min, double max) =>
            value < min ? min : value > max ? max : value;

        private static void Require(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value,
                    "the circle overlay geometry must be a finite number");
        }

        private static void RequirePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentOutOfRangeException(name, value,
                    "a camera frame must have a positive, finite width and height");
        }

        public override string ToString() =>
            $"centre {CentreX:0.###},{CentreY:0.###} diameter {Diameter:0.###}";
    }

    /// <summary>
    /// THE WHOLE OVERLAY FRAMING (issue #36): what shape the camera is drawn in, where the circle
    /// sits inside the camera frame, which corner the inset goes in, and how large the inset is on
    /// the preview.
    ///
    /// One type serves three places on purpose, so they cannot drift: it is a field on the preset
    /// (chosen BEFORE recording), the framing the HUD is currently drawing, and the block written
    /// into manifest.json at the stop. Being the same object is what makes "the corner in the preset
    /// is the corner the HUD shows" a fact rather than a hope.
    ///
    /// <see cref="InsetFraction"/> and <see cref="CameraOverlayCircle.Diameter"/> are DIFFERENT
    /// THINGS and issue #36 calls this out as assumption E5: the inset fraction is how big the
    /// overlay appears on the preview, the diameter is how much of the camera frame is inside it.
    /// </summary>
    internal sealed class CameraOverlaySettings
    {
        /// <summary>Smallest the inset may be, as a fraction of the preview's width.</summary>
        public const double MinInsetFraction = 0.15;

        /// <summary>Largest the inset may be, as a fraction of the preview's width.</summary>
        public const double MaxInsetFraction = 0.60;

        /// <summary>What issue #33 shipped as its fixed inset width, kept as the default.</summary>
        public const double DefaultInsetFraction = 0.30;

        /// <summary>Wire spelling of the shape - "circle" (default) or "rectangle".</summary>
        public string Shape { get; set; } = PreviewNames.Circle;

        /// <summary>Where the circle sits in the camera frame. Ignored while the shape is a rectangle,
        /// and KEPT rather than cleared, so switching back to a circle restores the framing.</summary>
        public CameraOverlayCircle Circle { get; set; } = new();

        /// <summary>Wire spelling of the corner the inset goes in - "bottom-right" by default.</summary>
        public string Corner { get; set; } = PreviewNames.BottomRight;

        /// <summary>How wide the inset is on the preview, as a fraction of the preview's width.</summary>
        public double InsetFraction { get; set; } = DefaultInsetFraction;

        public CameraOverlayShape ShapeValue => PreviewNames.Shape(Shape);

        public PreviewCorner CornerValue => PreviewNames.Corner(Corner);

        /// <summary>The inset fraction brought into range.</summary>
        public double ClampedInsetFraction =>
            double.IsNaN(InsetFraction) || double.IsInfinity(InsetFraction)
                ? DefaultInsetFraction
                : CameraOverlayCircle.Clamp(InsetFraction, MinInsetFraction, MaxInsetFraction);

        public CameraOverlaySettings Clone() => new()
        {
            Shape = Shape,
            Circle = Circle?.Clone() ?? new CameraOverlayCircle(),
            Corner = Corner,
            InsetFraction = InsetFraction,
        };

        /// <summary>
        /// The same framing with every value canonicalised: known wire spellings, the circle brought
        /// into range, the inset fraction brought into range. This is what is written to
        /// manifest.json, so an unrecognised string in config.json or presets.json can never reach the
        /// recording's own record - it is read as the documented default and written back as such.
        /// </summary>
        public CameraOverlaySettings Canonical() => new()
        {
            Shape = PreviewNames.Text(ShapeValue),
            Circle = (Circle ?? new CameraOverlayCircle()).Canonical(),
            Corner = PreviewNames.Text(CornerValue),
            InsetFraction = ClampedInsetFraction,
        };

        public override string ToString() =>
            $"{PreviewNames.Text(ShapeValue)} {PreviewNames.Text(CornerValue)} inset {ClampedInsetFraction:0.##} "
            + $"({(Circle ?? new CameraOverlayCircle())})";
    }

    /// <summary>
    /// Where a picture of one shape ends up inside a box of another - the "Uniform" / "contain" fit,
    /// as a pure function (issue #36).
    ///
    /// The preset editor needs it TWICE and stacked, which is the whole reason it is worth extracting
    /// and testing: the 320x240 preview buffer is drawn into the pane with WPF's Uniform stretch, and
    /// the camera's own picture sits inside THAT buffer letterboxed by ffmpeg's pad filter. Getting
    /// either fit wrong puts the circle over the wrong part of the face, which is the one thing this
    /// feature exists to get right.
    /// </summary>
    internal static class OverlayFit
    {
        /// <summary>
        /// The largest rectangle with <paramref name="contentWidth"/>:<paramref name="contentHeight"/>
        /// proportions that fits inside the box, centred. Returned in the box's own units.
        /// </summary>
        public static OverlayRect Contain(double boxWidth, double boxHeight, double contentWidth, double contentHeight)
        {
            RequirePositive(boxWidth, nameof(boxWidth));
            RequirePositive(boxHeight, nameof(boxHeight));
            RequirePositive(contentWidth, nameof(contentWidth));
            RequirePositive(contentHeight, nameof(contentHeight));

            double scale = Math.Min(boxWidth / contentWidth, boxHeight / contentHeight);
            double w = contentWidth * scale;
            double h = contentHeight * scale;
            return new OverlayRect((boxWidth - w) / 2.0, (boxHeight - h) / 2.0, w, h);
        }

        private static void RequirePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentOutOfRangeException(name, value,
                    "a fit needs a positive, finite width and height");
        }
    }
}
