using System;

namespace AgentEyes.App
{
    /// <summary>
    /// The recording HUD's remembered preview-panel size, with no WPF in it (issue #33, AC7 - the
    /// persistence half). It answers one question a test can ask: "what size should the next
    /// recording's HUD open at?"
    ///
    /// WHAT THIS CLASS IS, AND WHY IT IS SO SMALL NOW. Three fixes were shipped on this one question
    /// and all three were the same mistake wearing a different hat: A LAYOUT EVENT WAS MISTAKEN FOR
    /// A PERSON'S INTENT.
    ///
    ///  1. 2026-08-28, round 1 - the save read the window's live size in the Closed handler, by which
    ///     point the stop had already auto-sized the HUD back to the pill. A HUD left at 1600x760
    ///     came back at 520x400.
    ///  2. round 2 - the fix for (1) recorded every size the window reported while manually sized.
    ///     Switching to manual sizing re-lays the window out synchronously and reports the pill's
    ///     367x52 from inside the assignment, so the pill became the remembered size and the panel
    ///     opened at pill size with a zero-sized picture.
    ///  3. round 3 - the fix for (2) suppressed the reports made during the panel-open transition,
    ///     ending the transition on the window's next completed layout. On a HUD CONSTRUCTED with
    ///     the preview already on, that layout belongs to some other element: the transition ended
    ///     while the window was still 0x0, and the first size it reported was attributed to the
    ///     person. A hands-off recording rewrote a stored 200x100 to 260x100.
    ///
    /// Each of those fixes was a BLOCKLIST: name a transition that produces a bogus size, suppress
    /// it. A blocklist can only exclude the transitions somebody has already been burned by, and WPF
    /// has an open-ended supply of them - DPI changes, monitor changes, restore from minimise, theme
    /// changes, whatever panel is added next.
    ///
    /// SO THE POLARITY IS INVERTED HERE, and that is the entire design. This class does not observe
    /// the window at all. It has ONE mutator, <see cref="RecordUserResize"/>, and it is called only
    /// from <see cref="HudUserResize"/> - the three places where a person resizing this window is
    /// POSITIVELY IDENTIFIED (the Win32 resize-modal loop, the panel's resize grip, and UI
    /// Automation's TransformPattern). There is no path from a layout pass to this class, because
    /// nothing here is subscribed to layout. A size that nobody asked for cannot be recorded by a
    /// transition nobody enumerated, because transitions are not an input to this class.
    ///
    /// A size on disk is a claim that somebody chose one. Only a gesture can make that claim.
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

        /// <summary>The width the preview panel should open at, or null when nobody has ever resized
        /// the HUD.</summary>
        public double? Width => _width;

        /// <summary>The height the preview panel should open at, or null when nobody has ever resized
        /// the HUD.</summary>
        public double? Height => _height;

        /// <summary>Whether a size is remembered at all - i.e. whether there is anything to persist
        /// or to restore. False on a fresh config whose HUD has never been resized, and that is the
        /// case in which no size may be written to config.</summary>
        public bool HasSize => _width is > 0 && _height is > 0;

        /// <summary>
        /// The size the preview panel should be opened at: the one the person left it at if they
        /// have ever resized the HUD, the caller's default otherwise. A pure read - opening the
        /// panel is not a resize and records nothing.
        /// </summary>
        public (double Width, double Height) PreferredSize(double defaultWidth, double defaultHeight)
        {
            if (!(defaultWidth > 0) || !(defaultHeight > 0))
                throw new ArgumentOutOfRangeException(nameof(defaultWidth),
                    $"The preview panel's default size must be positive, not {defaultWidth}x{defaultHeight}.");

            return (_width ?? defaultWidth, _height ?? defaultHeight);
        }

        /// <summary>
        /// A PERSON has just resized the HUD, and this is the size they left it at. The one and only
        /// way anything is ever written here.
        ///
        /// The caller is responsible for having positively identified the gesture - see
        /// <see cref="HudUserResize"/>, which is the only caller and whose three entry points are
        /// each a gesture that no layout pass can perform. It is NOT this class's job to guess
        /// whether a size is deserved; the point of the design is that nothing which has not earned
        /// the claim ever reaches this method at all.
        ///
        /// A non-positive size is a window that has not been laid out yet, not a size, and is
        /// refused rather than allowed to destroy a real one.
        /// </summary>
        public void RecordUserResize(double width, double height)
        {
            if (!(width > 0) || !(height > 0)) return;

            _width = width;
            _height = height;
        }
    }
}
