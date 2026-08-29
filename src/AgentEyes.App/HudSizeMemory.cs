namespace AgentEyes.App
{
    /// <summary>
    /// The recording HUD's remembered size, with no WPF in it (issue #33, AC7 - the persistence
    /// half). It answers one question a test can ask: "what size should the next recording's HUD
    /// open at, given everything this one's window did?"
    ///
    /// IT EXISTS BECAUSE OF AN ORDERING TRAP, and no amount of care at the save site removes it.
    /// The HUD is manually sized only while the preview panel is up; in every other state it
    /// auto-sizes to its content (the pill). But the window LEAVES manual sizing BEFORE it closes:
    /// an ordinary stop runs <see cref="HudWindow.SetStatus"/>, which takes the panel down and puts
    /// SizeToContent back to WidthAndHeight, and merely hiding the preview does the same. So by the
    /// time the Closed handler runs, the window's live size IS the pill's and the panel size the
    /// person chose is already gone. Reading the size at save time therefore saves the pill, or -
    /// with a guard on the sizing mode - saves nothing at all. That second shape is exactly the
    /// defect QA reproduced on 2026-08-28: a HUD left at 1600x760 came back at 520x400, because the
    /// only writer of HudWidth/HudHeight was gated on a sizing mode that the stop had already
    /// cleared.
    ///
    /// So a size is remembered WHEN IT IS TRUE, not when it is needed. Every size the window takes
    /// is offered here; only those measured while the window was MANUALLY sized are kept, and the
    /// last one kept is what the config gets - however many auto-sized layouts happen in between,
    /// and whatever the window's live state has become by the time it closes.
    /// </summary>
    internal sealed class HudSizeMemory
    {
        private double? _width;
        private double? _height;

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

        /// <summary>The width the preview panel should open at, or null when the HUD has never been
        /// manually sized.</summary>
        public double? Width => _width;

        /// <summary>The height the preview panel should open at, or null when the HUD has never been
        /// manually sized.</summary>
        public double? Height => _height;

        /// <summary>Whether a size is remembered at all - i.e. whether there is anything to persist
        /// or to restore. False on a fresh config that has never shown the preview panel.</summary>
        public bool HasSize => _width is > 0 && _height is > 0;

        /// <summary>
        /// Offer a size the window has just taken.
        ///
        /// <paramref name="manuallySized"/> is the window's sizing mode AT THAT MOMENT: false means
        /// it is auto-sizing to its content, and a pill's dimensions must never become the preview
        /// panel's remembered size (a saved pill would come back as a preview panel the size of a
        /// pill). A non-positive size is a layout that has not happened yet and is likewise not a
        /// size.
        ///
        /// An auto-sized report is IGNORED, never destructive: the window auto-sizes on its way out
        /// of every recording, and forgetting the remembered size there would reproduce the very
        /// defect this class exists to fix.
        /// </summary>
        public void Observe(bool manuallySized, double width, double height)
        {
            if (!manuallySized) return;
            if (!(width > 0) || !(height > 0)) return;
            _width = width;
            _height = height;
        }
    }
}
