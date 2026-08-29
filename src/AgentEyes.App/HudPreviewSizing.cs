using System;
using System.Windows;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// The three things the recording HUD's window does about its own size while the live preview
    /// goes up and comes down (issue #33, AC1 and AC7), kept in one place and out of
    /// <see cref="HudWindow"/> for one reason: THIS is the code the ordering defect lived in, and a
    /// test can drive it against a real WPF window, which it cannot do with HudWindow itself (that
    /// needs a running Application's resources, a RecordingService and the user's real config file).
    /// <c>HudPreviewSizingOrderTests</c> drives exactly these three methods, and the decisions they
    /// delegate to <see cref="HudSizeMemory"/>, through real WPF layout.
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
    ///    Recording those reports as deliberate sizes is the defect QA reproduced on 2026-08-28.
    /// </summary>
    internal static class HudPreviewSizing
    {
        /// <summary>
        /// Let the memory see every size the window takes. Nothing is decided here - what a report
        /// is worth depends on facts this handler can only pass along (is the panel up, is the
        /// window manually sized, is a transition of ours still outstanding), and
        /// <see cref="HudSizeMemory"/> owns that judgement.
        ///
        /// Deliberately silent: SizeChanged fires throughout every drag of the resize grip, and a log
        /// line per report would bury the recording's own log.
        /// </summary>
        public static void Attach(Window window, HudSizeMemory memory, Func<bool> panelVisible)
        {
            if (window is null) throw new ArgumentNullException(nameof(window));
            if (memory is null) throw new ArgumentNullException(nameof(memory));
            if (panelVisible is null) throw new ArgumentNullException(nameof(panelVisible));

            window.SizeChanged += (_, _) => memory.Observe(
                panelVisible(),
                window.SizeToContent == SizeToContent.Manual,
                window.ActualWidth,
                window.ActualHeight);
        }

        /// <summary>
        /// Put the window into the manually-sized preview state at the size the person left it at,
        /// or at the caller's default if nobody has ever resized it. A no-op when the window is
        /// already manually sized - a mode or corner change re-applies the whole preview state, and
        /// re-opening a panel that is already open would both fight the person's size and restart the
        /// transition on top of it.
        /// </summary>
        public static void ShowPanel(Window window, HudSizeMemory memory,
                                     double defaultWidth, double defaultHeight)
        {
            if (window is null) throw new ArgumentNullException(nameof(window));
            if (memory is null) throw new ArgumentNullException(nameof(memory));
            if (window.SizeToContent == SizeToContent.Manual) return;

            bool remembered = memory.HasSize;
            var (width, height) = memory.OpenPanel(defaultWidth, defaultHeight);
            Log.Info($"hud: preview panel opening at {width}x{height} "
                   + $"({(remembered ? "the size it was left at" : "the default")})");

            // ORDER IS LOAD-BEARING - see the class comment. Manual first, or the size is discarded;
            // and the memory has already been told (by OpenPanel, above) that everything WPF reports
            // from inside these three statements is this window mid-move, not anybody's choice.
            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;

            // Whatever WPF did above, the transition is over once the layout it started completes.
            // Subscribing AFTER the assignments means any layout they ran synchronously has already
            // happened, so this cannot end the transition early and let a half-applied size be taken
            // for a choice; and it means a command that lands at a slightly different size still
            // ends the transition, instead of wedging the memory shut against every later resize.
            new SettleWhenLaidOut(window, memory).Subscribe();
        }

        /// <summary>
        /// Ends the memory's transition the first time the window completes a layout pass. Written
        /// as a class rather than the obvious self-unsubscribing lambda so that ShowPanel compiles
        /// to ONE method: the IL guard in HudSizeMemoryTests reads ShowPanel's call ORDER, and a
        /// lambda would split the body into two definitions and make that order unreadable.
        /// </summary>
        private sealed class SettleWhenLaidOut
        {
            private readonly Window _window;
            private readonly HudSizeMemory _memory;

            public SettleWhenLaidOut(Window window, HudSizeMemory memory)
            {
                _window = window;
                _memory = memory;
            }

            public void Subscribe() => _window.LayoutUpdated += OnLayoutUpdated;

            private void OnLayoutUpdated(object? sender, EventArgs e)
            {
                _window.LayoutUpdated -= OnLayoutUpdated;
                _memory.Settled();
            }
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

            memory.PanelClosed();
            window.SizeToContent = SizeToContent.WidthAndHeight;
            Log.Info("hud: preview panel down; the HUD is back to its pill size, remembering "
                   + (memory.HasSize ? $"{memory.Width}x{memory.Height}" : "no size"));
        }
    }
}
