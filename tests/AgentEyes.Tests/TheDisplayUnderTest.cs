using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentEyes.Tests
{
    /// <summary>
    /// HOW BIG A WINDOW THE MACHINE RUNNING THIS SUITE CAN ACTUALLY MAKE.
    ///
    /// WHY THIS EXISTS. The v1.7.0 release workflow failed at "Run tests" with eleven failures in
    /// the HUD resize area, every one of them the same pair: expected 1560, actual 1044. The same
    /// tests were green on the developer's machine, and had been through four rounds of review
    /// there. Nothing about the product was wrong. The tests had named an absolute number of pixels
    /// and the GitHub runner's display could not supply them.
    ///
    /// WINDOWS REFUSES TO MAKE A RESIZABLE WINDOW BIGGER THAN ITS MAXIMUM TRACKING SIZE, which is
    /// the VIRTUAL SCREEN plus the window frame - <c>SM_CXMAXTRACK</c> / <c>SM_CYMAXTRACK</c>.
    /// DefWindowProc enforces it on WM_WINDOWPOSCHANGING, so it applies to every route a size can
    /// arrive by: a <c>Window.Width</c> assignment, a bare <c>SetWindowPos</c>, and a person
    /// dragging the border alike. The runner's desktop is 1024x768 and the frame adds 20, so 1044
    /// was the widest window that existed on that machine - and 1560 was simply not available.
    /// (Measured on the developer's machine: SM_CXVIRTUALSCREEN 3840, SM_CXMAXTRACK 3860. 1024 + 20
    /// = 1044, exactly what CI reported.)
    ///
    /// NOTE WHAT THIS IS NOT. It is not a DPI or scaling mismatch. Both sides of every one of those
    /// assertions were already in the same unit - WPF device-independent pixels - and the one place
    /// the suite crosses into device pixels (<c>SetWindowPos</c>) already converts through
    /// <see cref="VisualTreeHelper.GetDpi"/>. Scaling only ever entered as a red herring because
    /// 1560/1044 happens to be 1.494. The heights, which fit on the runner, were all correct.
    ///
    /// SO THE SIZE IS MEASURED RATHER THAN NAMED. A test that wants "three times the default preview
    /// width" asks <see cref="AtMost"/> for it, and gets it wherever the display can supply it and
    /// the largest the display CAN supply otherwise. The measurement is taken the only way that
    /// needs no unit conversion and no assumption about the process's DPI awareness: a real window,
    /// in the same shape the rigs use, is asked for an impossible size, and whatever it comes back
    /// as is this machine's ceiling - in exactly the device-independent pixels the assertions are
    /// written in.
    ///
    /// THE PRECONDITION THAT REMAINS, stated rather than implied. A display can be too small to
    /// perform a meaningful resize at all, and no measurement can invent room that is not there.
    /// The HUD tests assert that themselves - see
    /// <c>HudPreviewSizingOrderTests.TheDisplayRunningThisSuite_CanHoldTheWindowsTheseTestsResize</c> -
    /// so a display that cannot hold them fails by name rather than as a mysterious number.
    /// </summary>
    internal static class TheDisplayUnderTest
    {
        /// <summary>Bigger than any desktop, and small enough that its device-pixel form is nowhere
        /// near overflowing the int SetWindowPos takes. The window will not get it; that is the
        /// point.</summary>
        private const double MoreThanAnyDisplayCanGive = 32000;

        private static readonly Lazy<Size> TheCeiling =
            new(Measure, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The widest a resizable top-level window can be on this machine, in the same
        /// device-independent pixels <see cref="FrameworkElement.ActualWidth"/> reports.</summary>
        public static double LargestWindowWidth => TheCeiling.Value.Width;

        /// <summary>The tallest a resizable top-level window can be on this machine, in the same
        /// device-independent pixels <see cref="FrameworkElement.ActualHeight"/> reports.</summary>
        public static double LargestWindowHeight => TheCeiling.Value.Height;

        /// <summary>
        /// The size a test wants, or as much of it as this display can actually give a window. The
        /// value comes back EXACT rather than rounded, so that assigning it to a window reproduces
        /// it to the last sub-pixel: it is a size a window has already been observed to take here.
        /// </summary>
        public static Size AtMost(double width, double height)
        {
            if (!(width > 0) || !(height > 0))
                throw new ArgumentOutOfRangeException(nameof(width),
                    $"A test asked for a {width}x{height} window, which is not a size.");

            return new Size(Math.Min(width, LargestWindowWidth), Math.Min(height, LargestWindowHeight));
        }

        /// <summary>
        /// Ask a real window for an impossible size and report what Windows gave it. Same window
        /// shape as the HUD rigs (chromeless, resizable, its own MinWidth/MinHeight), because WPF
        /// serves WM_GETMINMAXINFO from the window's own Min/Max properties and a different shape
        /// could have a different ceiling. Deliberately invisible and parked off the desktop, so
        /// "dotnet test" stays as quiet as it is today.
        /// </summary>
        private static Size Measure()
        {
            Size measured = default;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                Window? probe = null;
                try
                {
                    probe = new Window
                    {
                        Title = "display-ceiling probe",
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.CanResize,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        Opacity = 0,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Topmost = false,
                        SizeToContent = SizeToContent.Manual,
                        MinWidth = 260,
                        MinHeight = 52,
                        Left = -8000,
                        Top = -8000,
                        Width = MoreThanAnyDisplayCanGive,
                        Height = MoreThanAnyDisplayCanGive,
                    };
                    probe.Show();

                    for (int i = 0; i < 3; i++)
                    {
                        var frame = new DispatcherFrame();
                        probe.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                            new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                    }

                    measured = new Size(probe.ActualWidth, probe.ActualHeight);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { probe?.Close(); } catch (Exception ex) { failure ??= ex; }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromSeconds(60)))
                throw new TimeoutException(
                    "The display-ceiling probe did not finish within 60 s. The HUD sizing tests need "
                    + "an interactive desktop session: they create real (invisible, off-screen) "
                    + "windows so the production code reads a real laid-out size.");

            if (failure != null)
                throw new InvalidOperationException(
                    "The display-ceiling probe could not measure how big a window this machine "
                    + "allows, so the HUD sizing tests have no size to resize to: " + failure, failure);

            if (!(measured.Width > 0) || !(measured.Height > 0))
                throw new InvalidOperationException(
                    $"The display-ceiling probe measured a {measured.Width}x{measured.Height} window. "
                    + "That is not a size, so every HUD sizing test built on it would be measuring "
                    + "nothing. This machine has no usable desktop for the suite to resize a window on.");

            return measured;
        }
    }
}
