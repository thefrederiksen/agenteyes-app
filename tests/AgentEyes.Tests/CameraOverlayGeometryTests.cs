using System;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #36, AC11 - the geometry model behind the circular camera overlay: how a centre and a
    /// diameter are normalised, how they are clamped to a real frame, and what crop they describe.
    ///
    /// WHY THIS IS WORTH TESTING AT ALL. The circle is stored as fractions so a preset survives the
    /// camera changing (assumption E2), and the SAME fractions are read by three different places -
    /// the preset editor's adorner, the HUD's ImageBrush, and manifest.json. If they disagree the
    /// person frames one thing and records another, and every one of them looks convincing while
    /// doing it. These are the arithmetic facts all three depend on.
    ///
    /// WHAT THEY CANNOT SEE: whether WPF actually draws what the numbers say. That is a rendering
    /// fact and needs the running app; it is what the HUD and editor screenshots in the proof are
    /// for. An empty or absent result here is a broken instrument, never a pass.
    /// </summary>
    public class CameraOverlayGeometryTests
    {
        // ---- the documented defaults (assumption E3) ------------------------

        [Fact]
        public void Circle_Default_IsCentredHorizontallyAndHighInTheFrame()
        {
            var circle = new CameraOverlayCircle();

            // Horizontally centred, in the UPPER portion (above the middle), at 60% of frame height.
            Assert.Equal(0.50, circle.CentreX, 3);
            Assert.Equal(0.42, circle.CentreY, 3);
            Assert.Equal(0.60, circle.Diameter, 3);
            Assert.True(circle.CentreY < 0.5,
                $"The default circle sits at {circle.CentreY:0.###} of the frame height - assumption E3 "
                + "puts a seated speaker's head ABOVE the middle.");
        }

        [Fact]
        public void Overlay_Default_IsACircleInTheBottomRightAtTheIssue33InsetSize()
        {
            var overlay = new CameraOverlaySettings();

            // AC1: circle is the DEFAULT shape.
            Assert.Equal(CameraOverlayShape.Circle, overlay.ShapeValue);
            Assert.Equal("circle", overlay.Shape);
            // The corner and inset size are issue #33's, unchanged - this issue promotes them, it
            // does not redefine them.
            Assert.Equal(PreviewCorner.BottomRight, overlay.CornerValue);
            Assert.Equal(0.30, overlay.ClampedInsetFraction, 3);
        }

        // ---- normalisation --------------------------------------------------

        [Theory]
        [InlineData(-0.4, 0.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(0.33, 0.33)]
        [InlineData(1.0, 1.0)]
        [InlineData(4.2, 1.0)]
        public void Canonical_CentreOutsideTheFrame_IsBroughtBackToTheEdge(double given, double expected)
        {
            var c = new CameraOverlayCircle { CentreX = given, CentreY = given }.Canonical();

            Assert.Equal(expected, c.CentreX, 6);
            Assert.Equal(expected, c.CentreY, 6);
        }

        [Theory]
        [InlineData(-1.0, CameraOverlayCircle.MinDiameter)]
        [InlineData(0.0, CameraOverlayCircle.MinDiameter)]
        [InlineData(0.05, CameraOverlayCircle.MinDiameter)]
        [InlineData(0.25, 0.25)]
        [InlineData(1.0, CameraOverlayCircle.MaxDiameter)]
        [InlineData(9.0, CameraOverlayCircle.MaxDiameter)]
        public void Canonical_DiameterOutOfRange_IsBroughtIntoRange(double given, double expected)
        {
            Assert.Equal(expected, new CameraOverlayCircle { Diameter = given }.Canonical().Diameter, 6);
        }

        [Fact]
        public void Canonical_LeavesAValidCircleExactlyAsChosen()
        {
            var chosen = new CameraOverlayCircle { CentreX = 0.31, CentreY = 0.27, Diameter = 0.44 };

            var c = chosen.Canonical();

            Assert.True(c.SameAs(chosen), $"Canonical() moved a perfectly valid circle: {chosen} -> {c}");
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Canonical_NotANumber_ThrowsRatherThanQuietlyPickingAValue(double bad)
        {
            // No fallback programming: a geometry that is not a number is a broken input, and the
            // failure names the field rather than silently drawing a circle somewhere arbitrary.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new CameraOverlayCircle { CentreX = bad }.Canonical());
        }

        // ---- clamping to a real frame --------------------------------------

        [Fact]
        public void ClampedTo_CircleOffTheTopOfTheFrame_IsPushedFullyInside()
        {
            // A circle 60% of the frame height cannot have its centre 10% down: a third of it would
            // be above the picture.
            var c = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.10, Diameter = 0.60 }
                .ClampedTo(1920, 1080);

            Assert.Equal(0.30, c.CentreY, 6);   // exactly half a diameter down
            var box = c.Viewbox(1920, 1080);
            Assert.True(box.Y >= -1e-9, $"The circle still starts above the frame at y={box.Y:0.####}.");
            Assert.True(box.Bottom <= 1 + 1e-9, $"The circle runs past the bottom at {box.Bottom:0.####}.");
        }

        [Fact]
        public void ClampedTo_CircleOffTheLeftEdge_IsPushedInByTheFramesOwnAspect()
        {
            // The horizontal half-extent of a circle is NOT half its diameter: the diameter is a
            // fraction of the HEIGHT, so on a 16:9 frame it is that much narrower in width-units.
            var c = new CameraOverlayCircle { CentreX = 0.0, CentreY = 0.5, Diameter = 0.60 }
                .ClampedTo(1920, 1080);

            double expectedHalfX = 0.60 / (2.0 * (1920.0 / 1080.0));
            Assert.Equal(expectedHalfX, c.CentreX, 6);
            Assert.True(c.CentreX < 0.30,
                $"A 60%-of-height circle on a 16:9 frame is {c.CentreX * 2:0.###} of the WIDTH; clamping "
                + "it as if the diameter were a width fraction would push it to 0.300.");
        }

        [Fact]
        public void ClampedTo_TallNarrowFrame_ShrinksTheCircleUntilItFitsAcross()
        {
            // A 9:16 phone-shaped frame cannot hold a circle 60% of its HEIGHT - that is 107% of its
            // width. The circle is shrunk rather than being drawn off both sides.
            var c = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.5, Diameter = 0.60 }
                .ClampedTo(1080, 1920);

            Assert.Equal(1080.0 / 1920.0, c.Diameter, 6);
            var box = c.Viewbox(1080, 1920);
            Assert.Equal(1.0, box.Width, 6);           // it spans the full width
            Assert.True(box.X >= -1e-9 && box.Right <= 1 + 1e-9);
        }

        [Fact]
        public void ClampedTo_ValidCircle_IsLeftWhereItWasPut()
        {
            var chosen = new CameraOverlayCircle { CentreX = 0.62, CentreY = 0.38, Diameter = 0.40 };

            var c = chosen.ClampedTo(1280, 720);

            Assert.True(c.SameAs(chosen), $"A circle that already fits was moved: {chosen} -> {c}");
        }

        [Fact]
        public void ClampedTo_StoredValueSurvivesTheCameraChanging()
        {
            // Assumption E2: the STORED numbers are the person's choice and are not rewritten by any
            // particular camera. Fitting happens at draw time, on a copy.
            var stored = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.05, Diameter = 0.9 };

            stored.ClampedTo(640, 480);
            stored.ClampedTo(1920, 1080);

            Assert.Equal(0.05, stored.CentreY, 6);
            Assert.Equal(0.9, stored.Diameter, 6);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-16)]
        [InlineData(double.NaN)]
        public void ClampedTo_ImpossibleFrame_ThrowsRatherThanDividingByIt(double bad)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new CameraOverlayCircle().ClampedTo(bad, 480));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new CameraOverlayCircle().ClampedTo(640, bad));
        }

        // ---- the crop the circle describes ----------------------------------

        [Fact]
        public void Viewbox_IsTheCirclesBoundingSquare_InFractionsOfTheFrame()
        {
            var box = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.5, Diameter = 0.50 }
                .Viewbox(1920, 1080);

            // 50% of 1080 = 540 px across; as a fraction of 1920 that is 0.28125.
            Assert.Equal(0.28125, box.Width, 6);
            Assert.Equal(0.50, box.Height, 6);
            Assert.Equal(0.5, box.CentreX, 6);
            Assert.Equal(0.5, box.CentreY, 6);
        }

        [Fact]
        public void PixelBounds_IsASquare_SoTheOverlayIsRoundAndNotAnOval()
        {
            var box = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.5, Diameter = 0.50 }
                .PixelBounds(1920, 1080);

            Assert.Equal(540, box.Width, 6);
            Assert.Equal(540, box.Height, 6);
            Assert.Equal(box.Width, box.Height, 6);
        }

        [Fact]
        public void PixelBounds_MovingTheCentre_MovesWhichPixelsAreInsideTheCircle()
        {
            // AC2's arithmetic: moving the centre and changing the diameter must select a DIFFERENT
            // part of the frame, or the controls would be decoration.
            var frameW = 1280.0; var frameH = 720.0;
            var start = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.5, Diameter = 0.5 };
            var moved = new CameraOverlayCircle { CentreX = 0.25, CentreY = 0.35, Diameter = 0.5 };
            var resized = new CameraOverlayCircle { CentreX = 0.5, CentreY = 0.5, Diameter = 0.25 };

            var a = start.PixelBounds(frameW, frameH);
            var b = moved.PixelBounds(frameW, frameH);
            var c = resized.PixelBounds(frameW, frameH);

            Assert.NotEqual(Math.Round(a.X, 3), Math.Round(b.X, 3));
            Assert.NotEqual(Math.Round(a.Y, 3), Math.Round(b.Y, 3));
            Assert.Equal(a.Width, b.Width, 6);              // moved, not resized
            Assert.NotEqual(Math.Round(a.Width, 3), Math.Round(c.Width, 3));
            Assert.Equal(360, a.Width, 6);
            Assert.Equal(180, c.Width, 6);
        }

        // ---- the inset size, which is a DIFFERENT thing (assumption E5) ------

        [Theory]
        [InlineData(0.0, CameraOverlaySettings.MinInsetFraction)]
        [InlineData(0.10, CameraOverlaySettings.MinInsetFraction)]
        [InlineData(0.35, 0.35)]
        [InlineData(0.90, CameraOverlaySettings.MaxInsetFraction)]
        public void ClampedInsetFraction_OutOfRange_IsBroughtIntoRange(double given, double expected)
        {
            Assert.Equal(expected, new CameraOverlaySettings { InsetFraction = given }.ClampedInsetFraction, 6);
        }

        [Fact]
        public void InsetFractionAndDiameter_AreIndependent()
        {
            // E5: how big it looks on the preview, and how much of the camera is inside it, are two
            // different numbers. Changing one must not move the other.
            var o = new CameraOverlaySettings { InsetFraction = 0.5 };
            o.Circle.Diameter = 0.2;

            var canonical = o.Canonical();

            Assert.Equal(0.5, canonical.InsetFraction, 6);
            Assert.Equal(0.2, canonical.Circle.Diameter, 6);
        }

        // ---- wire spellings --------------------------------------------------

        // The theory takes the WIRE spelling: the shape enum is internal to the product and xUnit
        // needs a public signature to discover a test. Round-tripping the string through the enum is
        // what proves the two agree.
        [Theory]
        [InlineData("circle")]
        [InlineData("rectangle")]
        public void PreviewNames_ShapeSpellings_RoundTrip(string wire)
        {
            Assert.Equal(wire, PreviewNames.Text(PreviewNames.Shape(wire)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("oval")]
        [InlineData("Rectangle")]   // the wire spelling is lower case; anything else is unknown
        public void PreviewNames_UnknownShape_ReadsAsTheDocumentedDefault(string? wire)
        {
            // AC1: circle is the default, including for a preset written before this field existed
            // and for a hand-edited file with a spelling nothing produces.
            Assert.Equal(CameraOverlayShape.Circle, PreviewNames.Shape(wire));
        }

        [Fact]
        public void Canonical_UnknownSpellings_AreReplacedByTheDefaultsBeforeAnythingIsStored()
        {
            var o = new CameraOverlaySettings { Shape = "hexagon", Corner = "middle" }.Canonical();

            Assert.Equal("circle", o.Shape);
            Assert.Equal("bottom-right", o.Corner);
        }

        [Fact]
        public void Clone_IsDeep_SoTwoPresetsCannotShareOneCircle()
        {
            var a = new CameraOverlaySettings();
            var b = a.Clone();

            b.Circle.CentreX = 0.9;

            Assert.Equal(0.5, a.Circle.CentreX, 6);
        }

        // ---- the two nested fits the preset editor depends on ----------------

        [Fact]
        public void Contain_SameAspect_FillsTheBoxExactly()
        {
            var r = OverlayFit.Contain(480, 360, 320, 240);

            Assert.Equal(0, r.X, 6);
            Assert.Equal(0, r.Y, 6);
            Assert.Equal(480, r.Width, 6);
            Assert.Equal(360, r.Height, 6);
        }

        [Fact]
        public void Contain_WideContentInASquarerBox_LeavesBarsAboveAndBelow()
        {
            // A 16:9 camera inside the 4:3 preview buffer - the case that puts the circle over black
            // bars if the second fit is skipped.
            var r = OverlayFit.Contain(320, 240, 1280, 720);

            Assert.Equal(320, r.Width, 6);
            Assert.Equal(180, r.Height, 6);
            Assert.Equal(0, r.X, 6);
            Assert.Equal(30, r.Y, 6);
        }

        [Fact]
        public void Contain_TallContentInAWiderBox_LeavesBarsLeftAndRight()
        {
            var r = OverlayFit.Contain(480, 360, 720, 1280);

            Assert.Equal(202.5, r.Width, 6);
            Assert.Equal(360, r.Height, 6);
            Assert.Equal(138.75, r.X, 6);
            Assert.Equal(0, r.Y, 6);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        [InlineData(double.NaN)]
        public void Contain_ImpossibleSize_Throws(double bad)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlayFit.Contain(bad, 360, 320, 240));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlayFit.Contain(480, 360, 320, bad));
        }
    }
}
