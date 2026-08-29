using System;
using System.Windows;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// What the recording HUD's window does about its own size when the live preview goes up and
    /// comes down (issue #33, AC1 and AC7), kept in one place and out of <see cref="HudWindow"/> for
    /// one reason: THIS is the code the ordering defect lived in, and a test can drive it against a
    /// real WPF window, which it cannot do with HudWindow itself (that needs a running Application's
    /// resources, a RecordingService and the user's real config file).
    /// <c>HudPreviewSizingOrderTests</c> drives exactly these two methods through real WPF layout.
    ///
    /// WHAT WPF ACTUALLY DOES HERE, measured rather than assumed (the spike is reproduced in the
    /// tests):
    ///  - A window with <see cref="SizeToContent.WidthAndHeight"/> has its Width and Height
    ///    properties WRITTEN BY WPF to whatever the content measured - the pill's. They are not
    ///    "unset" waiting for the panel.
    ///  - Width and Height set BEFORE the switch to <see cref="SizeToContent.Manual"/> are discarded:
    ///    the window stays the pill's size. The switch must come first. This ordering is not
    ///    interchangeable and is why the code below reads the way it does.
    ///  - Each of those assignments then re-lays the window out SYNCHRONOUSLY and raises SizeChanged
    ///    from inside the assignment - so the window is observed at the pill's size, and again at a
    ///    width-applied-but-not-height size, before it is ever observed at the size it was asked for.
    ///
    /// THAT LAST FACT USED TO MATTER AND NO LONGER DOES, which is the point of the round-4 design.
    /// Three shipped defects came from trying to sort the window's own size reports into "the
    /// person's" and "the layout's" - first by excluding the pill, then the panel-open transition,
    /// then the transition on an unshown window. Nothing here listens to those reports any more.
    /// A size is remembered only when a person is positively identified resizing the window, which
    /// happens in <see cref="HudUserResize"/> and nowhere else. Opening the panel is a command, not
    /// a choice: it reads the remembered size and never writes one, however many times WPF re-lays
    /// the window out on the way.
    /// </summary>
    internal static class HudPreviewSizing
    {
        /// <summary>
        /// Put the window into the manually-sized preview state at the size the person left it at,
        /// or at the caller's default if nobody has ever resized it. A no-op when the window is
        /// already manually sized - a mode or corner change re-applies the whole preview state, and
        /// re-opening a panel that is already open would fight the person's size.
        /// </summary>
        public static void ShowPanel(Window window, HudSizeMemory memory,
                                     double defaultWidth, double defaultHeight)
        {
            if (window is null) throw new ArgumentNullException(nameof(window));
            if (memory is null) throw new ArgumentNullException(nameof(memory));
            if (window.SizeToContent == SizeToContent.Manual) return;

            bool remembered = memory.HasSize;
            var (width, height) = memory.PreferredSize(defaultWidth, defaultHeight);
            Log.Info($"hud: preview panel opening at {width}x{height} "
                   + $"({(remembered ? "the size it was left at" : "the default")})");

            // ORDER IS LOAD-BEARING - see the class comment. Manual first, or the size is discarded.
            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;
        }

        /// <summary>
        /// Take the panel down and let the window auto-size back to the pill. The remembered size
        /// survives this on purpose: hiding the preview and stopping the recording BOTH come through
        /// here, so forgetting here would lose the size before it could ever be saved.
        /// </summary>
        public static void HidePanel(Window window, HudSizeMemory memory)
        {
            if (window is null) throw new ArgumentNullException(nameof(window));
            if (memory is null) throw new ArgumentNullException(nameof(memory));

            window.SizeToContent = SizeToContent.WidthAndHeight;
            Log.Info("hud: preview panel down; the HUD is back to its pill size, remembering "
                   + (memory.HasSize ? $"{memory.Width}x{memory.Height}" : "no size"));
        }
    }
}
