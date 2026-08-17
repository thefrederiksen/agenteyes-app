using System.Drawing;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class RegionMathTests
    {
        [Fact]
        public void Identity_scale_offsets_by_window_origin()
        {
            // window at (100,50) DIP, drag rect (10,20,200,100), scale 1.0
            var r = RegionMath.ToDeviceRect(100, 50, 10, 20, 200, 100, 1.0, 1.0);
            Assert.Equal(new Rectangle(110, 70, 200, 100), r);
        }

        [Fact]
        public void Scaled_150_percent_multiplies_device_pixels()
        {
            // At 150% DPI a 200x100 DIP rect becomes 300x150 device px.
            var r = RegionMath.ToDeviceRect(0, 0, 0, 0, 200, 100, 1.5, 1.5);
            Assert.Equal(new Rectangle(0, 0, 300, 150), r);
        }

        [Fact]
        public void Negative_virtual_origin_is_handled()
        {
            var r = RegionMath.ToDeviceRect(-1920, 0, 50, 50, 100, 100, 1.0, 1.0);
            Assert.Equal(new Rectangle(-1870, 50, 100, 100), r);
        }

        [Theory]
        [InlineData(101, 101, 100, 100)]
        [InlineData(200, 150, 200, 150)]
        [InlineData(1, 1, 2, 2)]
        public void Evenize_forces_even_dimensions(int w, int h, int expW, int expH)
        {
            var r = RegionMath.Evenize(new Rectangle(0, 0, w, h));
            Assert.Equal(expW, r.Width);
            Assert.Equal(expH, r.Height);
            Assert.Equal(0, r.Width % 2);
            Assert.Equal(0, r.Height % 2);
        }

        [Fact]
        public void Evenize_keeps_position()
        {
            var r = RegionMath.Evenize(new Rectangle(13, 27, 101, 101));
            Assert.Equal(13, r.X);
            Assert.Equal(27, r.Y);
        }

        // ---- aspect lock (issue #69) --------------------------------------

        [Fact]
        public void SnapDragToAspect_free_returns_raw_drag()
        {
            var r = RegionMath.SnapDragToAspect(10, 20, 210, 140, RegionMath.AspectLock.Free);
            Assert.Equal(new Rectangle(10, 20, 200, 120), r);
        }

        [Fact]
        public void SnapDragToAspect_square_yields_equal_width_and_height()
        {
            // AC1: a 1:1 lock forces width == height regardless of the free-form drag shape.
            var r = RegionMath.SnapDragToAspect(0, 0, 300, 100, RegionMath.AspectLock.Square);
            Assert.Equal(r.Width, r.Height);
            Assert.Equal(300, r.Width);   // the wider axis drives the size
        }

        [Fact]
        public void SnapDragToAspect_square_driven_by_taller_drag()
        {
            var r = RegionMath.SnapDragToAspect(0, 0, 100, 400, RegionMath.AspectLock.Square);
            Assert.Equal(r.Width, r.Height);
            Assert.Equal(400, r.Height);
        }

        [Theory]
        [InlineData(16, 9)]
        [InlineData(9, 16)]
        public void SnapDragToAspect_holds_the_requested_ratio(int rw, int rh)
        {
            var r = RegionMath.SnapDragToAspect(0, 0, 1600, 100, new RegionMath.AspectLock(rw, rh));
            // width/height must equal rw/rh (allow +-1px rounding).
            double expected = (double)rw / rh;
            double actual = (double)r.Width / r.Height;
            Assert.True(System.Math.Abs(expected - actual) < 0.02, $"ratio {actual} != {expected}");
        }

        [Fact]
        public void SnapDragToAspect_anchor_corner_stays_fixed_when_dragging_up_left()
        {
            // Drag from (500,500) up-and-left; the anchor corner stays at bottom-right,
            // so the rectangle's bottom-right must remain (500,500).
            var r = RegionMath.SnapDragToAspect(500, 500, 200, 100, RegionMath.AspectLock.Square);
            Assert.Equal(500, r.Right);
            Assert.Equal(500, r.Bottom);
            Assert.Equal(r.Width, r.Height);
        }

        // ---- exact centered size (issue #69) ------------------------------

        [Fact]
        public void CenteredExactSize_preserves_exact_dimensions()
        {
            // AC2/AC3: requested size is honored to the pixel.
            var mon = new Rectangle(0, 0, 1920, 1080);
            var r = RegionMath.CenteredExactSize(mon, 1080, 1080);
            Assert.Equal(1080, r.Width);
            Assert.Equal(1080, r.Height);
        }

        [Fact]
        public void CenteredExactSize_centers_on_the_monitor()
        {
            var mon = new Rectangle(0, 0, 1920, 1080);
            var r = RegionMath.CenteredExactSize(mon, 1080, 1080);
            Assert.Equal((1920 - 1080) / 2, r.X);
            Assert.Equal((1080 - 1080) / 2, r.Y);
        }

        [Fact]
        public void CenteredExactSize_offsets_by_monitor_origin()
        {
            var mon = new Rectangle(2560, 0, 1920, 1080);
            var r = RegionMath.CenteredExactSize(mon, 1000, 1000);
            Assert.Equal(2560 + (1920 - 1000) / 2, r.X);
            Assert.Equal((1080 - 1000) / 2, r.Y);
        }

        [Fact]
        public void CenteredExactSize_keeps_exact_size_but_clamps_origin_when_taller_than_monitor()
        {
            // AC4: a 1080x1920 vertical exceeds a 1080-tall monitor. The size stays exact
            // (so the output is 1080x1920); only the origin is clamped to the monitor top-left.
            var mon = new Rectangle(0, 0, 1920, 1080);
            var r = RegionMath.CenteredExactSize(mon, 1080, 1920);
            Assert.Equal(1080, r.Width);
            Assert.Equal(1920, r.Height);
            Assert.Equal(0, r.Y);                       // clamped: never starts above the monitor
            Assert.Equal((1920 - 1080) / 2, r.X);       // still centered horizontally
        }

        [Theory]
        [InlineData(1080, 1080, false)]
        [InlineData(1080, 1920, true)]   // taller than a 1080 monitor
        [InlineData(1920, 1080, false)]
        [InlineData(2000, 500, true)]    // wider than a 1920 monitor
        public void ExceedsMonitor_flags_overflow(int w, int h, bool expected)
        {
            var mon = new Rectangle(0, 0, 1920, 1080);
            Assert.Equal(expected, RegionMath.ExceedsMonitor(mon, w, h));
        }
    }
}
