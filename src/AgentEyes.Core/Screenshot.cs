using System;
using System.Threading;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;
using WinForms = System.Windows.Forms;

namespace AgentEyes
{
    /// <summary>
    /// OS-level screenshot capture (full monitor or an explicit region) via GDI
    /// Graphics.CopyFromScreen. All coordinates are virtual-desktop device pixels.
    /// </summary>
    internal static class Screenshot
    {
        /// <summary>Capture an entire monitor; returns the saved file path.</summary>
        public static string CaptureMonitor(MonitorInfo monitor, string outputPath, bool copyToClipboard = true)
        {
            return CaptureRect(monitor.Bounds, outputPath, copyToClipboard);
        }

        /// <summary>Capture an arbitrary virtual-desktop rectangle; returns the saved file path.</summary>
        public static string CaptureRect(Drawing.Rectangle rect, string outputPath, bool copyToClipboard = true)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                throw new UsageException($"capture rectangle is empty ({rect.Width}x{rect.Height}).");
            }

            using var bmp = new Drawing.Bitmap(rect.Width, rect.Height, Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.X, rect.Y, 0, 0, new Drawing.Size(rect.Width, rect.Height),
                    Drawing.CopyPixelOperation.SourceCopy);
            }

            bmp.Save(outputPath, Imaging.ImageFormat.Png);
            if (copyToClipboard)
            {
                TryCopyToClipboard(bmp);
            }
            return outputPath;
        }

        private static void TryCopyToClipboard(Drawing.Bitmap bmp)
        {
            // Clipboard.SetImage REQUIRES an STA thread. The CLI's Program.Main is [STAThread]
            // and the WPF UI thread is STA, but the Control API serves /capture from a background
            // (MTA) listener thread - calling SetImage there throws ThreadStateException and the
            // bitmap never lands. So when we are not already on an STA thread, do the copy on a
            // short-lived dedicated STA thread (root-cause fix, not a swallow).
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                CopyOnStaThread(bmp);
                return;
            }

            // Clone so the bitmap is safe to use after this method returns (the caller disposes it).
            using var copy = new Drawing.Bitmap(bmp);
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { CopyOnStaThread(copy); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null)
                Log.Error("[Screenshot] clipboard copy on STA thread failed", failure);
            else
                Log.Info("[Screenshot] clipboard image set on STA thread");
        }

        private static void CopyOnStaThread(Drawing.Bitmap bmp)
        {
            // SetDataObject with copy:true FLUSHES the image to the OS clipboard, so it survives
            // after this (often short-lived) STA thread exits. Plain Clipboard.SetImage leaves the
            // data owned by the thread, and when that thread dies the clipboard goes empty - which
            // is exactly what bit the Control API path. A clipboard miss is non-fatal to the saved
            // file, so we surface it but do not throw out of the capture.
            WinForms.Clipboard.SetDataObject(bmp, copy: true);
        }
    }
}
