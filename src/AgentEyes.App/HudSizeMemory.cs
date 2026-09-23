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
    ///
    /// AND THE ALLOWLIST WATCHES ITSELF (Review Gate round 1 on PR #34). An allowlist proves its
    /// members and cannot prove its own exhaustiveness: nothing in this process can demonstrate that
    /// Windows has no further way to resize a window. So this class also remembers the size the
    /// window is SUPPOSED to have right now - the one the panel was opened at, or the last one a
    /// gesture recorded - and <see cref="UnattributedSize"/> compares that against the size the
    /// window actually ended up at. A difference is a resize route nobody listed, reported by name
    /// and by number when the panel comes down. It cannot record the missing size (it has no gesture
    /// to attribute it to, and inventing one is the defect this class exists to prevent), but a
    /// missing route stops being invisible, which is the most an in-process check can honestly do.
    /// </summary>
    internal sealed class HudSizeMemory
    {
        /// <summary>How far an observed size may differ from the accounted one and still be the same
        /// size. Absorbs sub-pixel layout rounding and nothing else.</summary>
        private const double SamePixel = 1.0;

        private double? _width;
        private double? _height;

        /// <summary>The size the window is expected to have while the panel is up: the size the panel
        /// was opened at, or the last size a gesture recorded. NOT what gets persisted - it is the
        /// yardstick <see cref="UnattributedSize"/> measures against, and it is null until a panel
        /// has actually been opened, because before that there is nothing to be surprised by.</summary>
        private double? _accountedWidth;
        private double? _accountedHeight;

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
            _accountedWidth = width;
            _accountedHeight = height;
        }

        /// <summary>
        /// The panel has just been COMMANDED to open at this size. Not a resize and not a claim that
        /// anybody chose it - it is the yardstick <see cref="UnattributedSize"/> measures against, so
        /// that a size the window later acquires without a gesture can be seen.
        /// </summary>
        public void NoteOpenedAt(double width, double height)
        {
            if (!(width > 0) || !(height > 0)) return;

            _accountedWidth = width;
            _accountedHeight = height;
        }

        /// <summary>
        /// The panel has just come DOWN, so there is no longer a size the window is supposed to
        /// have, and <see cref="UnattributedSize"/> goes quiet until the next panel opens.
        ///
        /// THIS IS WHAT KEEPS THE CANARY HONEST (QA round 6 on PR #39). The yardstick is only
        /// meaningful WHILE THE PANEL IS UP. Taking the panel down auto-sizes the window back to the
        /// pill, so the very next thing the window's size is, is the pill's - and comparing that
        /// against the panel size the yardstick still held was the canary reporting the teardown's
        /// OWN auto-size as a resize route nobody had listed. It fired on every ordinary stop, on a
        /// recording where nothing had been touched. A warning that is present on every stop cannot
        /// distinguish "a route was missed" from "a recording ended", which is worse than no warning
        /// at all: it trains everyone to ignore the one signal that exists to catch a genuinely
        /// missed route.
        ///
        /// The same staleness bites a second, slower way: with the panel down, the pill's own border
        /// can still be dragged. That gesture is deliberately NOT recorded (the pill's dimensions
        /// are not a preview-panel size - see <see cref="HudUserResize"/>), so a stale yardstick
        /// would report it as an unaccounted PANEL size at the next stop. It is neither.
        ///
        /// The REMEMBERED size - the one that is persisted and reopened at - is untouched here. That
        /// survives the panel coming down on purpose: hiding the preview and stopping the recording
        /// both take the panel down, and forgetting it would lose the person's size before it could
        /// be saved.
        /// </summary>
        public void NotePanelClosed()
        {
            _accountedWidth = null;
            _accountedHeight = null;
        }

        /// <summary>
        /// The completeness canary (issue #33, AC7; Review Gate round 1 on PR #34). Returns a
        /// description of a size the window ended up at that NO gesture ever claimed, or null when
        /// the size is accounted for.
        ///
        /// This is the honest half of an allowlist design. The four gesture routes in
        /// <see cref="HudUserResize"/> are each positively identified, but the list cannot prove it
        /// covers every way Windows can resize a window - exactly how a maximise went unrecorded
        /// until a reviewer measured it. So the size the window ACTUALLY has when the panel comes
        /// down is compared with the size it was opened at or last recorded, and a difference is
        /// reported. It deliberately does NOT record the size: an unattributed size is one nobody has
        /// shown a person chose, and recording sizes nobody chose is the defect this class exists to
        /// prevent. It makes the gap visible; it does not paper over it.
        ///
        /// Null when no panel is currently open - before the first <see cref="NoteOpenedAt"/> and
        /// after <see cref="NotePanelClosed"/>. In both of those states there is no expectation to
        /// violate, because the window is not supposed to be any particular size.
        /// </summary>
        public string? UnattributedSize(double width, double height)
        {
            if (_accountedWidth is not > 0 || _accountedHeight is not > 0) return null;
            if (!(width > 0) || !(height > 0)) return null;
            if (Math.Abs(width - _accountedWidth.Value) <= SamePixel
             && Math.Abs(height - _accountedHeight.Value) <= SamePixel) return null;

            return $"hud: the HUD ended up at {width:0.##}x{height:0.##} but the last size anything "
                 + $"attributed to a person was {_accountedWidth.Value:0.##}x{_accountedHeight.Value:0.##}. "
                 + "A resize route is unaccounted for (issue #33, AC7): the size will NOT be "
                 + "remembered, because no gesture claimed it. If this is reproducible, the gesture "
                 + "that produced it needs a route in HudUserResize.";
        }
    }
}
