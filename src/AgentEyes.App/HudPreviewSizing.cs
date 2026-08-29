using System;
using System.Windows;
using AgentEyes;
using AgentEyes.Preview;

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
            // PreviewLog, not Log: this runs on the WPF dispatcher that serves the HUD's Stop button,
            // and the shared logger is a synchronous file append under a process-wide lock.
            PreviewLog.Info($"hud: preview panel opening at {width}x{height} "
                          + $"({(remembered ? "the size it was left at" : "the default")})");

            // ORDER IS LOAD-BEARING - see the class comment. Manual first, or the size is discarded.
            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;

            // Not a resize and not a claim that anybody chose this - it is the yardstick the
            // completeness canary in HidePanel measures against.
            memory.NoteOpenedAt(width, height);
        }

        /// <summary>
        /// Take the panel down and let the window auto-size back to the pill. The remembered size
        /// survives this on purpose: hiding the preview and stopping the recording BOTH come through
        /// here, so forgetting here would lose the size before it could ever be saved.
        ///
        /// THE COMPLETENESS CANARY IS REPORTED HERE (issue #33, AC7; Review Gate round 1 on PR #34,
        /// round 2 on PR #39): a size the HUD ended up at that no gesture ever claimed is written to
        /// the log by THIS method, before the auto-size destroys the evidence.
        ///
        /// It used to be returned for the CALLER to log, and that is exactly how it came to report to
        /// nobody: the explicit Show/Hide click logged it, and <c>HudWindow.SetStatus</c> - the
        /// ORDINARY STOP, the most common path there is, and the one where an unrecorded size
        /// actually costs the person their layout - discarded the return value. A canary whose alarm
        /// depends on each caller remembering to listen is not a canary. So the alarm sounds here,
        /// where it is computed, and no caller can drop it.
        ///
        /// The string is STILL returned, but only so a test can drive the known-bad arm and read the
        /// same words that were logged. Nothing acts on it: an unattributed size is precisely a size
        /// nobody has shown a person chose, so it is reported and NOT remembered.
        ///
        /// AND IT HAPPENS ONCE PER PANEL, however many times it is called (QA round 6 on PR #39).
        /// One ordinary stop calls <c>HudWindow.SetStatus</c> THREE times - "Stopping..." from the
        /// HUD's own button, then "Saving video..." and "Saving audio..." as the raw files flush -
        /// and each of those calls comes through here. Round 2's canary was dropped by its caller;
        /// the fix for it then rang the alarm once per CALL, so an untouched recording produced two
        /// spurious "a resize route is unaccounted for" warnings after the first, correct silence.
        /// The lesson from three rounds of this defect is that a guard belonging to a side effect
        /// goes on the method that HAS the side effect, not on each caller in turn - a caller-side
        /// guard is only ever as good as the next caller somebody writes. So both halves live here:
        /// the panel is taken down at most once (this method is the exact mirror of
        /// <see cref="ShowPanel"/>, which is likewise a no-op when the panel is already open), and
        /// the yardstick the canary measures against is retired with it.
        /// </summary>
        public static string? HidePanel(Window window, HudSizeMemory memory)
        {
            if (window is null) throw new ArgumentNullException(nameof(window));
            if (memory is null) throw new ArgumentNullException(nameof(memory));
            // Nothing to take down: the window is already auto-sizing to the pill. Returning here is
            // what stops the second and third call of one stop re-reporting, re-logging and
            // re-assigning what the first one already did.
            if (window.SizeToContent != SizeToContent.Manual) return null;

            string? unattributed = memory.UnattributedSize(window.ActualWidth, window.ActualHeight);
            if (unattributed != null) PreviewLog.Warn(unattributed);

            window.SizeToContent = SizeToContent.WidthAndHeight;
            // The panel is down, so the size the window is "supposed" to have no longer exists. The
            // remembered (persisted) size is deliberately untouched - see HudSizeMemory.
            memory.NotePanelClosed();
            PreviewLog.Info("hud: preview panel down; the HUD is back to its pill size, remembering "
                          + (memory.HasSize ? $"{memory.Width}x{memory.Height}" : "no size"));
            return unattributed;
        }
    }
}
