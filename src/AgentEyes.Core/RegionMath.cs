using System;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// Pure geometry for the region overlay: convert a drag rectangle expressed in
    /// window-local DIPs into virtual-desktop device pixels (what the capture engines want).
    /// </summary>
    internal static class RegionMath
    {
        /// <param name="windowLeftDip">Overlay window Left in DIPs (virtual-screen left).</param>
        /// <param name="windowTopDip">Overlay window Top in DIPs (virtual-screen top).</param>
        /// <param name="localX">Drag rect X within the canvas, DIPs.</param>
        /// <param name="localY">Drag rect Y within the canvas, DIPs.</param>
        /// <param name="localW">Drag rect width, DIPs.</param>
        /// <param name="localH">Drag rect height, DIPs.</param>
        /// <param name="scaleX">Device-per-DIP scale on X (e.g. 1.5 at 150%).</param>
        /// <param name="scaleY">Device-per-DIP scale on Y.</param>
        public static Drawing.Rectangle ToDeviceRect(
            double windowLeftDip, double windowTopDip,
            double localX, double localY, double localW, double localH,
            double scaleX, double scaleY)
        {
            double absX = windowLeftDip + localX;
            double absY = windowTopDip + localY;

            int px = (int)Math.Round(absX * scaleX);
            int py = (int)Math.Round(absY * scaleY);
            int pw = (int)Math.Round(localW * scaleX);
            int ph = (int)Math.Round(localH * scaleY);

            return new Drawing.Rectangle(px, py, pw, ph);
        }

        /// <summary>Even out odd dimensions - H.264/yuv420p requires even width and height.</summary>
        public static Drawing.Rectangle Evenize(Drawing.Rectangle r)
        {
            int w = r.Width - (r.Width % 2);
            int h = r.Height - (r.Height % 2);
            return new Drawing.Rectangle(r.X, r.Y, Math.Max(2, w), Math.Max(2, h));
        }

        /// <summary>
        /// An aspect-ratio lock for the region picker. <see cref="Free"/> (0:0) means no constraint.
        /// The ratio is dimensionless (W:H) - e.g. 1:1 square, 16:9 landscape, 9:16 vertical.
        /// </summary>
        public readonly struct AspectLock
        {
            public int W { get; }
            public int H { get; }

            public AspectLock(int w, int h) { W = w; H = h; }

            /// <summary>True when there is no constraint (either component is non-positive).</summary>
            public bool IsFree => W <= 0 || H <= 0;

            public static readonly AspectLock Free = new(0, 0);
            public static readonly AspectLock Square = new(1, 1);
            public static readonly AspectLock Landscape16x9 = new(16, 9);
            public static readonly AspectLock Vertical9x16 = new(9, 16);

            public override string ToString() => IsFree ? "Free" : $"{W}:{H}";
        }

        /// <summary>
        /// Constrain a drag - from a fixed anchor corner toward the current point - to a locked
        /// aspect ratio. The anchor corner stays put and the opposite corner is snapped so that
        /// width:height matches the lock. The axis the user pulled further (relative to the ratio)
        /// drives the size, so the constrained rectangle always encloses the raw drag. A
        /// <see cref="AspectLock.Free"/> lock returns the raw drag rectangle unchanged.
        /// </summary>
        /// <param name="anchorX">Drag anchor X (the corner that stays put), in the drag's units.</param>
        /// <param name="anchorY">Drag anchor Y, in the drag's units.</param>
        /// <param name="curX">Current pointer X.</param>
        /// <param name="curY">Current pointer Y.</param>
        /// <param name="aspect">The aspect lock; Free returns the raw drag rectangle.</param>
        public static Drawing.Rectangle SnapDragToAspect(
            double anchorX, double anchorY, double curX, double curY, AspectLock aspect)
        {
            double dx = curX - anchorX, dy = curY - anchorY;
            double w = Math.Abs(dx), h = Math.Abs(dy);

            if (!aspect.IsFree)
            {
                double ratio = (double)aspect.W / aspect.H; // width / height
                // Drive by whichever axis the user pulled further, measured against the ratio,
                // so the snapped rectangle grows to contain the drag rather than shrink inside it.
                if (w / aspect.W >= h / aspect.H) h = w / ratio;
                else w = h * ratio;
            }

            int iw = (int)Math.Round(w);
            int ih = (int)Math.Round(h);
            int left = (int)Math.Round(dx >= 0 ? anchorX : anchorX - w);
            int top = (int)Math.Round(dy >= 0 ? anchorY : anchorY - h);
            return new Drawing.Rectangle(left, top, iw, ih);
        }

        /// <summary>
        /// Place an exact WxH region (device pixels) centered on the given monitor. The requested
        /// size is preserved EXACTLY (never shrunk) so social formats hit their target dimensions;
        /// only the origin is clamped so the region never starts above or to the left of the
        /// monitor. Use <see cref="ExceedsMonitor"/> to warn when the size overflows the monitor.
        /// </summary>
        public static Drawing.Rectangle CenteredExactSize(Drawing.Rectangle monitor, int w, int h)
        {
            if (w < 2) w = 2;
            if (h < 2) h = 2;

            int x = monitor.X + (monitor.Width - w) / 2;
            int y = monitor.Y + (monitor.Height - h) / 2;
            if (x < monitor.X) x = monitor.X;
            if (y < monitor.Y) y = monitor.Y;

            return new Drawing.Rectangle(x, y, w, h);
        }

        /// <summary>True when a WxH region does not fit inside the monitor (the caller warns).</summary>
        public static bool ExceedsMonitor(Drawing.Rectangle monitor, int w, int h)
            => w > monitor.Width || h > monitor.Height;
    }
}
