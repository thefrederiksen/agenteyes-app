using System;
using System.Runtime.InteropServices;
using AgentEyes.AlwaysOn;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AgentEyes.App
{
    /// <summary>
    /// The always-on dot on the tray icon, and the tooltip beside it (issue #66). Kept out of
    /// <see cref="TrayHost"/> so the tooltip wording is testable without a tray.
    /// </summary>
    internal static class TrayDot
    {
        public static readonly Drawing.Color Red = Drawing.Color.FromArgb(0xE5, 0x48, 0x4D);
        public static readonly Drawing.Color Grey = Drawing.Color.FromArgb(0x9A, 0xA0, 0xAA);

        /// <summary>The longest tooltip Windows shows; NotifyIcon throws above it.</summary>
        public const int MaxTooltip = 127;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>The app icon at tray size with a dot in its bottom-right corner - solid, or with a
        /// centre of another colour. A dark rim keeps the dot readable on a light or dark taskbar.</summary>
        public static Drawing.Icon Compose(Drawing.Icon baseIcon, Drawing.Color dot, Drawing.Color? centre)
        {
            int size = WinForms.SystemInformation.SmallIconSize.Width;
            using var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var sized = new Drawing.Icon(baseIcon, size, size))
                    g.DrawIcon(sized, new Drawing.Rectangle(0, 0, size, size));

                float d = size * 0.56f;
                var r = new Drawing.RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                using (var rim = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1B, 0x1D, 0x21)))
                    g.FillEllipse(rim, Drawing.RectangleF.Inflate(r, 1f, 1f));
                using (var fill = new Drawing.SolidBrush(dot))
                    g.FillEllipse(fill, r);
                if (centre is Drawing.Color c)
                {
                    float cd = d * 0.46f;
                    var cr = new Drawing.RectangleF(r.X + (d - cd) / 2, r.Y + (d - cd) / 2, cd, cd);
                    using var cb = new Drawing.SolidBrush(c);
                    g.FillEllipse(cb, cr);
                }
            }
            IntPtr h = bmp.GetHicon();
            try
            {
                using var borrowed = Drawing.Icon.FromHandle(h);
                return (Drawing.Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(h);
            }
        }

        /// <summary>The tray tooltip for an always-on status, at most <see cref="MaxTooltip"/> characters.</summary>
        public static string Tooltip(AlwaysOnStatus s) => Tooltip(s, DateTime.UtcNow);

        /// <summary>The tray tooltip as of <paramref name="nowUtc"/>. While a clip is in progress it says
        /// that instead of today's totals - how long so far and where it will be saved (issue #70). The
        /// two do not fit together in the 127 characters Windows allows, and the clip in progress is the
        /// fact the owner cannot see anywhere else on the tray.</summary>
        public static string Tooltip(AlwaysOnStatus s, DateTime nowUtc)
        {
            string counts = s.Counts switch { "system" => "system sound", "both" => "mic + system", _ => "microphone" };
            string cap = s.CapGb.HasValue && s.CapGb.Value > 0 ? $" of {s.CapGb.Value:0.#} GB" : "";
            string today = $"Today: {s.ClipsToday} clip{(s.ClipsToday == 1 ? "" : "s")}, {AlwaysOnDay.Size(s.DiskUsedBytes)}{cap}";
            if (s.State == AlwaysOnState.Keeping && s.InProgressShort(nowUtc) is string now)
                return Fit("AgentEyes - Always-on recording\n" + now);
            string text = s.State switch
            {
                AlwaysOnState.Listening => $"AgentEyes - Always-on recording\nListening ({counts}). {today}",
                AlwaysOnState.Keeping => $"AgentEyes - Always-on recording\nKeeping a clip ({counts}). {today}",
                AlwaysOnState.Paused => $"AgentEyes - Always-on paused\n{Capitalise(s.PausedReason ?? "paused")}. {today}",
                AlwaysOnState.Retrying => $"AgentEyes - Always-on NOT recording\n{s.LastError ?? "the capture failed"} - retrying",
                _ => "AgentEyes",
            };
            return Fit(text);
        }

        private static string Fit(string text) =>
            text.Length <= MaxTooltip ? text : text.Substring(0, MaxTooltip - 3) + "...";

        private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
