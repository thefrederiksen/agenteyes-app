using System;
using AgentEyes.Preview;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #43 - the geometry behind the preset editor's recording schematic, the drawing that
    /// gives "Size on screen" and "Corner" something to change.
    ///
    /// THESE ARE THE ASSERTIONS THAT COULD NOT PASS AGAINST THE OLD CODE (AC8). The old dialog
    /// answered both controls with a text label, so a test asserting the label was green before the
    /// defect was fixed and green after, and proved nothing either way. Every assertion here is about
    /// a SIZE and a POSITION that responds to the fraction and to the corner - the thing that was
    /// missing - and <see cref="InsetSchematic"/> did not exist at all until this issue, so this file
    /// does not even compile against the code that shipped the bug.
    ///
    /// What they cannot see, said plainly: that the schematic is actually PAINTED in the dialog.
    /// That is the wiring, asserted as source facts in <see cref="InsetSchematicUiTests"/>, and the
    /// screenshots in the running-app proof.
    /// </summary>
    public class InsetSchematicTests
    {
        private const double BoxWidth = 320;
        private const double BoxHeight = 180;   // 16:9, the schematic's own shape

        private static CameraOverlaySettings Framing(double inset,
                                                     PreviewCorner corner = PreviewCorner.BottomRight,
                                                     CameraOverlayShape shape = CameraOverlayShape.Circle) =>
            new()
            {
                InsetFraction = inset,
                Corner = PreviewNames.Text(corner),
                Shape = PreviewNames.Text(shape),
            };

        private static OverlayRect Place(CameraOverlaySettings overlay) =>
            InsetSchematic.Place(BoxWidth, BoxHeight, overlay, InsetSchematic.DefaultFrameAspect);

        // ---- AC1 / AC4: the drawing responds to the fraction, to scale ------

        [Theory]
        [InlineData(0.15)]
        [InlineData(0.30)]
        [InlineData(0.45)]
        [InlineData(0.60)]
        public void Place_InsetWidth_IsTheChosenFractionOfTheRecordingsWidth(double fraction)
        {
            var placed = Place(Framing(fraction));
            Assert.Equal(BoxWidth * fraction, placed.Width, 6);
        }

        [Fact]
        public void Place_MovingTheSlider_MovesTheDrawing_NotJustALabel()
        {
            // The defect in one assertion: at the two ends of the slider the drawn inset must be a
            // different size. The old dialog drew the same thing at both ends - there was nothing to
            // draw the inset into at all.
            var small = Place(Framing(CameraOverlaySettings.MinInsetFraction));
            var large = Place(Framing(CameraOverlaySettings.MaxInsetFraction));

            Assert.True(large.Width > small.Width + 1,
                $"The inset is {small.Width} wide at {CameraOverlaySettings.MinInsetFraction} and "
                + $"{large.Width} at {CameraOverlaySettings.MaxInsetFraction} - the slider changes nothing.");
            Assert.True(large.Height > small.Height + 1, "The inset's height does not follow the slider.");
        }

        [Fact]
        public void Place_MaxFraction_IsFourTimesTheWidthAndSixteenTimesTheAreaOfMinFraction()
        {
            // AC4, measured. 0.60 / 0.15 = 4 in EACH DIMENSION, so the circle covers 16 times the
            // area - which is what "to scale" means for a square inset. (The criterion's aside says
            // "about four times the area"; four times is the LINEAR ratio. The drawing follows the
            // arithmetic, and this is the number it produces.)
            var small = Place(Framing(CameraOverlaySettings.MinInsetFraction));
            var large = Place(Framing(CameraOverlaySettings.MaxInsetFraction));

            Assert.Equal(4.0, large.Width / small.Width, 6);
            Assert.Equal(16.0, (large.Width * large.Height) / (small.Width * small.Height), 6);
        }

        [Fact]
        public void Place_OutOfRangeFraction_IsBroughtIntoTheSlidersRange()
        {
            Assert.Equal(BoxWidth * CameraOverlaySettings.MinInsetFraction, Place(Framing(0.0)).Width, 6);
            Assert.Equal(BoxWidth * CameraOverlaySettings.MaxInsetFraction, Place(Framing(9.0)).Width, 6);
            Assert.Equal(BoxWidth * CameraOverlaySettings.DefaultInsetFraction, Place(Framing(double.NaN)).Width, 6);
        }

        // ---- AC2: the drawing responds to the corner ------------------------

        [Fact]
        public void Place_EachCorner_PutsTheInsetInThatCorner()
        {
            var overlay = Framing(0.30);
            double margin = BoxWidth * InsetSchematic.MarginFraction;

            var topLeft = Place(Framing(0.30, PreviewCorner.TopLeft));
            var topRight = Place(Framing(0.30, PreviewCorner.TopRight));
            var bottomLeft = Place(Framing(0.30, PreviewCorner.BottomLeft));
            var bottomRight = Place(Framing(0.30, PreviewCorner.BottomRight));

            Assert.Equal(margin, topLeft.X, 6);
            Assert.Equal(margin, topLeft.Y, 6);
            Assert.Equal(margin, bottomLeft.X, 6);
            Assert.Equal(margin, topRight.Y, 6);
            Assert.Equal(BoxWidth - margin, topRight.Right, 6);
            Assert.Equal(BoxWidth - margin, bottomRight.Right, 6);
            Assert.Equal(BoxHeight - margin, bottomLeft.Bottom, 6);
            Assert.Equal(BoxHeight - margin, bottomRight.Bottom, 6);

            // And no two corners land in the same place - four screenshots that differ (AC2).
            var all = new[] { topLeft, topRight, bottomLeft, bottomRight };
            for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                    Assert.True(Math.Abs(all[i].X - all[j].X) > 1 || Math.Abs(all[i].Y - all[j].Y) > 1,
                        $"Corners {i} and {j} draw the inset in the same place.");

            // The size is the corner's business only in where it starts, never in how big it is.
            foreach (var corner in all)
            {
                Assert.Equal(Place(overlay).Width, corner.Width, 6);
                Assert.Equal(Place(overlay).Height, corner.Height, 6);
            }
        }

        // ---- the two shapes -------------------------------------------------

        [Fact]
        public void Place_Circle_IsSquare_SoTheDrawingIsRoundAndNotAnOval()
        {
            var placed = Place(Framing(0.40, PreviewCorner.BottomRight, CameraOverlayShape.Circle));
            Assert.Equal(placed.Width, placed.Height, 6);
        }

        [Fact]
        public void Place_Rectangle_TakesItsHeightFromTheCamerasOwnAspect()
        {
            var overlay = Framing(0.40, PreviewCorner.BottomRight, CameraOverlayShape.Rectangle);

            var wide = InsetSchematic.Place(BoxWidth, BoxHeight, overlay, 16.0 / 9.0);
            var fourThree = InsetSchematic.Place(BoxWidth, BoxHeight, overlay, 4.0 / 3.0);

            Assert.Equal(BoxWidth * 0.40, wide.Width, 6);
            Assert.Equal(BoxWidth * 0.40, fourThree.Width, 6);
            Assert.Equal(wide.Width / (16.0 / 9.0), wide.Height, 6);
            Assert.Equal(fourThree.Width / (4.0 / 3.0), fourThree.Height, 6);
            Assert.True(fourThree.Height > wide.Height, "A 4:3 camera must inset a taller box than a 16:9 one.");
        }

        [Fact]
        public void Place_LargestCircle_IsTallerThanA16By9Recording_AndSaysSoByOverhangingTheBox()
        {
            // 60% of a 16:9 frame's WIDTH is 1.07 of its height, so the circle really does run off the
            // top and bottom. The schematic clips it rather than shrinking it, because that is what
            // the HUD's preview surface does with the same numbers - and shrinking it would be the
            // drawing quietly disagreeing with the recording.
            var placed = Place(Framing(CameraOverlaySettings.MaxInsetFraction));
            Assert.True(placed.Height > BoxHeight,
                $"The largest inset is {placed.Height} tall in a {BoxHeight} box - it should overhang.");
            Assert.True(placed.Y < 0, "The overhanging inset should be pushed off the top, not clipped short.");
        }

        // ---- the instrument itself ------------------------------------------

        [Theory]
        [InlineData(0, 180)]
        [InlineData(320, 0)]
        [InlineData(double.NaN, 180)]
        [InlineData(320, double.PositiveInfinity)]
        public void Place_ABoxWithNoSize_Throws_RatherThanDrawingToNoScale(double width, double height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => InsetSchematic.Place(width, height, Framing(0.30), InsetSchematic.DefaultFrameAspect));
        }

        [Fact]
        public void Place_WithoutAFraming_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => InsetSchematic.Place(BoxWidth, BoxHeight, null!, InsetSchematic.DefaultFrameAspect));
        }

        [Fact]
        public void Place_ACameraWithNoAspect_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => InsetSchematic.Place(BoxWidth, BoxHeight,
                                           Framing(0.30, PreviewCorner.BottomRight, CameraOverlayShape.Rectangle), 0));
        }

        // ---- AC5: this drawing changes nothing that is stored ----------------

        [Fact]
        public void Place_NeverChangesTheFramingItWasGiven()
        {
            var overlay = Framing(0.42, PreviewCorner.TopLeft);
            overlay.Circle.Diameter = 0.33;

            InsetSchematic.Place(BoxWidth, BoxHeight, overlay, InsetSchematic.DefaultFrameAspect);

            Assert.Equal(0.42, overlay.InsetFraction, 6);
            Assert.Equal(0.33, overlay.Circle.Diameter, 6);
            Assert.Equal(PreviewNames.TopLeft, overlay.Corner);
        }
    }
}
