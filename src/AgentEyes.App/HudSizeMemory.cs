using System;

namespace AgentEyes.App
{
    /// <summary>
    /// The recording HUD's remembered preview-panel size, with no WPF in it (issue #33, AC7 - the
    /// persistence half). It answers one question a test can ask: "what size should the next
    /// recording's HUD open at, given everything this one's window did?"
    ///
    /// IT EXISTS BECAUSE OF TWO ORDERING TRAPS, and neither is removable at the save site.
    ///
    /// TRAP 1 - THE SIZE IS GONE BY CLOSING TIME. The HUD is manually sized only while the preview
    /// panel is up; in every other state it auto-sizes to its content (the pill). But the window
    /// LEAVES manual sizing BEFORE it closes: an ordinary stop runs <see cref="HudWindow.SetStatus"/>,
    /// which takes the panel down, and merely hiding the preview does the same. So by the time the
    /// Closed handler runs, the window's live size IS the pill's and the panel size the person chose
    /// is already gone. Reading the size at save time therefore saves the pill, or - with a guard on
    /// the sizing mode - saves nothing at all.
    ///
    /// TRAP 2 - NOT EVERY SIZE THE WINDOW TAKES IS A SIZE ANYONE CHOSE. Opening the panel is not one
    /// event but a short transition, and WPF reports the window's size from INSIDE it, half-applied:
    /// switching to manual sizing re-lays the window out synchronously while it still measures the
    /// pill, and setting the width raises SizeChanged before the height has been set at all
    /// (measured in HudPreviewSizingOrderTests - this is not a theory about WPF, it is a recording of
    /// it). Trusting those reports is what shipped on 2026-08-28: the pill's 367x52 was recorded as a
    /// deliberate size, read straight back two statements later, and written to config - so the panel
    /// opened at pill size with a zero-sized picture, for that recording and every one after it.
    ///
    /// So this class does not observe sizes and hope. It is told when a transition BEGINS
    /// (<see cref="OpenPanel"/>), which size the window has been COMMANDED to take, and when the
    /// layout that carries out the command has FINISHED (<see cref="Settled"/>). Every report in
    /// between is the window mid-move and is discarded. What is left - a size reported while the
    /// panel is up, the window is manually sized, and nothing of ours is outstanding - can only have
    /// come from the person: the resize grip, a drag of the window border, or UI Automation's
    /// TransformPattern. THAT is what is remembered, and nothing else ever reaches the config.
    ///
    /// An auto-sized report is IGNORED, never destructive: the window auto-sizes on its way out of
    /// every recording, and forgetting the remembered size there would reproduce trap 1.
    /// </summary>
    internal sealed class HudSizeMemory
    {
        /// <summary>How close a reported size must be to the commanded one to count as "the command
        /// has landed", in device-independent pixels. Not zero: a window's size makes a round trip
        /// through physical pixels, so a commanded 520.0 can be reported back as 519.99 on a scaled
        /// display. Small enough that no resize a person could perform hides inside it.</summary>
        private const double LandedTolerance = 1.0;

        private double? _width;
        private double? _height;

        private bool _settling;
        private double _commandedWidth;
        private double _commandedHeight;

        /// <summary>
        /// Seed from what a previous HUD saved. A missing, zero or negative pair is "never resized",
        /// not "resized to nothing" - the caller falls back to its own default in that case.
        /// </summary>
        public HudSizeMemory(double? savedWidth, double? savedHeight)
        {
            if (savedWidth is > 0 && savedHeight is > 0)
            {
                _width = savedWidth;
                _height = savedHeight;
            }
        }

        /// <summary>The width the preview panel should open at, or null when nobody has ever resized
        /// the HUD.</summary>
        public double? Width => _width;

        /// <summary>The height the preview panel should open at, or null when nobody has ever resized
        /// the HUD.</summary>
        public double? Height => _height;

        /// <summary>Whether a size is remembered at all - i.e. whether there is anything to persist
        /// or to restore. False on a fresh config whose HUD has never been resized, and that is the
        /// case in which no size may be written to config: a size on disk is a claim that somebody
        /// chose it.</summary>
        public bool HasSize => _width is > 0 && _height is > 0;

        /// <summary>Whether a transition of ours is still outstanding, so no reported size can be
        /// attributed to the person. Exposed for the tests that assert the transition ends.</summary>
        public bool Settling => _settling;

        /// <summary>
        /// The preview panel is being opened. Returns the size the window must be commanded to take -
        /// the remembered one if the HUD has ever been resized, the caller's default otherwise - and
        /// puts the memory into its transition state, in which reported sizes are the window
        /// mid-move rather than anybody's choice.
        ///
        /// Called BEFORE the window is touched, deliberately: the shipped defect read the remembered
        /// size AFTER the switch to manual sizing had already overwritten it.
        /// </summary>
        public (double Width, double Height) OpenPanel(double defaultWidth, double defaultHeight)
        {
            if (!(defaultWidth > 0) || !(defaultHeight > 0))
                throw new ArgumentOutOfRangeException(nameof(defaultWidth),
                    $"The preview panel's default size must be positive, not {defaultWidth}x{defaultHeight}.");

            _commandedWidth = _width ?? defaultWidth;
            _commandedHeight = _height ?? defaultHeight;
            _settling = true;
            return (_commandedWidth, _commandedHeight);
        }

        /// <summary>
        /// The layout that carries out the commanded size has finished, so from here on a reported
        /// size is the person's doing. Called when the commanded size is reported back, and again
        /// from the window once the layout pass has completed - so a command that lands at a slightly
        /// different size (a display-scaling round trip, a minimum-size clamp) ends the transition
        /// too, rather than wedging the memory shut against every later resize.
        /// </summary>
        public void Settled() => _settling = false;

        /// <summary>The preview panel has gone down; the window is back to auto-sizing. Ends any
        /// outstanding transition - what follows is the pill, and the pill is never a panel size.
        /// The remembered size is deliberately NOT cleared: the panel comes down on the way out of
        /// every recording, and forgetting here is trap 1.</summary>
        public void PanelClosed() => _settling = false;

        /// <summary>
        /// Offer a size the window has just reported.
        ///
        /// Three things must all be true for it to be a size somebody chose:
        /// <paramref name="panelVisible"/> - the pill's dimensions must never become the preview
        /// panel's remembered size; <paramref name="manuallySized"/> - the window's sizing mode AT
        /// THAT MOMENT, since an auto-sizing window's size is its content's, not its owner's; and no
        /// transition of ours outstanding - a size the window was told to take is not a choice, and
        /// neither is a half-applied size it passes through on the way there.
        ///
        /// A non-positive size is a layout that has not happened yet and is likewise not a size.
        /// </summary>
        public void Observe(bool panelVisible, bool manuallySized, double width, double height)
        {
            if (!panelVisible || !manuallySized) return;
            if (!(width > 0) || !(height > 0)) return;

            if (_settling)
            {
                // The window is still taking the size it was commanded. On the way it reports the
                // pill it is leaving and the half-applied sizes in between; on arrival it reports
                // the command itself. None of them is a choice, and the arrival is what tells us the
                // transition is over.
                if (Landed(width, _commandedWidth) && Landed(height, _commandedHeight)) Settled();
                return;
            }

            _width = width;
            _height = height;
        }

        private static bool Landed(double reported, double commanded) =>
            Math.Abs(reported - commanded) <= LandedTolerance;
    }
}
