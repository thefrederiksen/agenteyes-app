using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, AC7 (the HUD comes back at the size it was left at) and AC1 (toggling the preview
    /// shows the panel), at the level of the DECISION: which size the next recording's HUD opens at,
    /// and what is allowed to change it.
    ///
    /// THREE defects were shipped on this one question, and all three were the same mistake:
    ///
    ///  1. 2026-08-28, round 1 - the save read the window's live size in the Closed handler, by which
    ///     point the stop had already auto-sized the HUD back to the pill. A HUD left at 1600x760
    ///     came back at 520x400.
    ///  2. round 2 - the fix for (1) recorded every size the window reported while manually sized.
    ///     Switching to manual sizing re-lays the window out synchronously and reports the pill's
    ///     367x52 from inside the assignment, so the pill became the remembered size, was read
    ///     straight back, and was written to config: the panel opened at pill size with a zero-sized
    ///     picture, for that recording and every one after it.
    ///  3. round 3 - the fix for (2) suppressed the reports made during the panel-open transition
    ///     and ended the transition on the window's next completed layout. On a HUD CONSTRUCTED with
    ///     the preview already on, that layout belongs to some other element: the transition ended
    ///     at 0x0 and the first size the window reported was attributed to the person. A hands-off
    ///     recording silently rewrote a stored 200x100 to 260x100.
    ///
    /// Every one of those fixes was a BLOCKLIST - name the transition that produced a bogus size and
    /// suppress it - and a blocklist only ever excludes what somebody has already been burned by.
    ///
    /// SO THE POLARITY IS INVERTED, and these tests are short because the class is. There is no
    /// judgement left in <see cref="HudSizeMemory"/> to test: it does not observe the window, it has
    /// one mutator, and that mutator is called only from <see cref="HudUserResize"/>'s three
    /// positively-identified gestures. The questions "which report is real" and "is the transition
    /// over" no longer exist.
    ///
    /// WHAT THESE TESTS CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): they
    /// call the memory directly, so they can only prove what it does with a given call - never that
    /// a layout pass cannot make that call. That is the whole question this round, and it is
    /// answered in two other places: <c>HudPreviewSizingOrderTests</c> drives a real WPF window
    /// through the production code (including the constructor path and the gestures), and
    /// <c>HudUserResizeTests</c> holds the call graph shut against the compiled IL. The IL
    /// assertions at the bottom of this file are the wiring leg: they hold HudWindow to reaching
    /// this logic through <see cref="HudPreviewSizing"/> and to owning no sizing decisions itself.
    /// </summary>
    public class HudSizeMemoryTests
    {
        // The window's own numbers, so a reader can see the reproduction rather than infer it.
        private const double PillWidth = 367, PillHeight = 52;          // observed by QA on the pill HUD
        private const double DefaultPreviewWidth = 520, DefaultPreviewHeight = 400;
        private const double ResizedWidth = 1600, ResizedHeight = 760;  // the size QA left the HUD at

        // ---- what the panel opens at -----------------------------------------

        /// <summary>
        /// A HUD nobody has ever resized opens the panel at the caller's default - and asking is a
        /// READ. Round 2 shipped because asking the window instead returned the pill.
        /// </summary>
        [Fact]
        public void PreferredSize_OnAFreshMemory_IsTheDefaultAndRecordsNothing()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.Equal(DefaultPreviewWidth, width);
            Assert.Equal(DefaultPreviewHeight, height);
            Assert.False(memory.HasSize);
            Assert.Null(memory.Width);
            Assert.Null(memory.Height);
        }

        /// <summary>Opening the panel over and over - a mode change, a corner change, hide and show -
        /// is not a resize however many times it happens.</summary>
        [Fact]
        public void PreferredSize_AskedRepeatedly_NeverRecordsAnything()
        {
            var memory = new HudSizeMemory(null, null);

            for (int i = 0; i < 5; i++) memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.False(memory.HasSize);
        }

        [Fact]
        public void PreferredSize_WithARememberedSize_IsThatSize()
        {
            var memory = new HudSizeMemory(ResizedWidth, ResizedHeight);

            var (width, height) = memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.Equal(ResizedWidth, width);
            Assert.Equal(ResizedHeight, height);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        [Theory]
        [InlineData(0, 400)]
        [InlineData(520, 0)]
        [InlineData(-1, -1)]
        public void PreferredSize_WithANonPositiveDefault_Throws(double defaultWidth, double defaultHeight)
        {
            var memory = new HudSizeMemory(null, null);

            // No fallback: a caller that cannot say how big the panel should be has a bug, and a
            // silently substituted number would put an arbitrary size on the person's screen.
            Assert.ThrowsAny<System.ArgumentException>(() => memory.PreferredSize(defaultWidth, defaultHeight));
        }

        // ---- only a gesture writes -------------------------------------------

        [Fact]
        public void RecordUserResize_RemembersTheSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.RecordUserResize(ResizedWidth, ResizedHeight);

            Assert.True(memory.HasSize);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>
        /// THE WEDGE THAT MUST NOT EXIST. Round 3's transition state could be left outstanding, and
        /// every later resize was then discarded. There is no state to wedge here: a gesture takes
        /// effect whatever the panel has been doing.
        /// </summary>
        [Fact]
        public void RecordUserResize_AfterAnyAmountOfPanelActivity_StillTakesEffect()
        {
            var memory = new HudSizeMemory(null, null);

            for (int i = 0; i < 5; i++) memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);
            memory.RecordUserResize(ResizedWidth, ResizedHeight);

            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>
        /// ROUND 1'S DEFECT, at the decision level. The stop takes the panel down and the window
        /// auto-sizes back to the pill BEFORE the Closed handler saves. Nothing about that sequence
        /// may disturb the remembered size - and now nothing can, because the auto-size is not an
        /// input to this class at all.
        /// </summary>
        [Fact]
        public void RecordUserResize_ThenTheStopAutoSizesBackToThePill_StillRemembersTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);   // panel opens
            memory.RecordUserResize(ResizedWidth, ResizedHeight);              // the person resizes
            // The stop, the hide, the pill: none of them can speak to the memory.

            Assert.True(memory.HasSize);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>
        /// The whole criterion end to end at the decision level: resize, stop, save, start a new
        /// recording. The second memory is seeded the way the next HUD is seeded - from the config
        /// the first one wrote.
        /// </summary>
        [Fact]
        public void ResizedThenStoppedThenReopened_NextRecordingOpensAtTheResizedSize()
        {
            var first = new HudSizeMemory(null, null);
            first.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);
            first.RecordUserResize(ResizedWidth, ResizedHeight);

            // What SavePosition persists on Closed.
            double? savedWidth = first.Width, savedHeight = first.Height;

            // Recording 2: a new HUD seeded from that config.
            var second = new HudSizeMemory(savedWidth, savedHeight);
            var (nextWidth, nextHeight) = second.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.Equal(ResizedWidth, nextWidth);
            Assert.Equal(ResizedHeight, nextHeight);
        }

        /// <summary>Show, resize, hide, show again inside ONE recording: the panel comes back at the
        /// size it was left at, not at the default, with nothing written to disk in between.</summary>
        [Fact]
        public void HiddenAndShownAgainInOneRecording_ReopensAtTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);
            memory.RecordUserResize(ResizedWidth, ResizedHeight);
            var (again, andAgain) = memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.Equal(ResizedWidth, again);
            Assert.Equal(ResizedHeight, andAgain);
        }

        /// <summary>
        /// QA'S ROUND-3 REPRODUCTION, at the decision level: a hands-off recording on a HUD seeded
        /// with a size the WINDOW cannot take (MinWidth is 260). Round 3 wrote back whatever the
        /// window landed at - 260 - as the person's deliberate choice. Nothing the window lands at
        /// reaches this class now, so what went in comes out.
        /// </summary>
        [Fact]
        public void AHandsOffRecording_WithASizeTheWindowMustClamp_LeavesTheSizeExactlyAsItWas()
        {
            var memory = new HudSizeMemory(200, 100);

            memory.PreferredSize(DefaultPreviewWidth, DefaultPreviewHeight);   // the panel opens at 200x100
            // The window clamps to its MinWidth of 260 and reports 260x100. It has no way to say so.

            Assert.Equal(200, memory.Width);
            Assert.Equal(100, memory.Height);
        }

        // ---- degenerate layouts are not sizes --------------------------------

        [Theory]
        [InlineData(0, 400)]
        [InlineData(520, 0)]
        [InlineData(-1, 400)]
        [InlineData(520, -1)]
        public void RecordUserResize_NonPositiveSize_IsNotASize(double width, double height)
        {
            var memory = new HudSizeMemory(null, null);

            memory.RecordUserResize(width, height);

            Assert.False(memory.HasSize);
        }

        [Fact]
        public void RecordUserResize_NonPositiveSize_DoesNotDestroyAnEarlierSize()
        {
            var memory = new HudSizeMemory(ResizedWidth, ResizedHeight);

            memory.RecordUserResize(0, 0);

            Assert.Equal(ResizedWidth, memory.Width);
        }

        /// <summary>The pill is never a panel size. It can only become one by being handed to
        /// RecordUserResize, which only a gesture reaches - and a gesture that produced the pill is
        /// refused by <c>HudUserResize</c> because the window is auto-sized there
        /// (<c>AGestureWhileThePanelIsDown_IsNotAPanelSize</c>).</summary>
        [Fact]
        public void APillSizedWindow_LeavesNothingBehindOnItsOwn()
        {
            var memory = new HudSizeMemory(null, null);

            memory.PreferredSize(PillWidth, PillHeight);

            Assert.False(memory.HasSize);
        }

        // ---- seeding from config ---------------------------------------------

        [Fact]
        public void Ctor_NoSavedSize_HasNothingToRestore()
        {
            var memory = new HudSizeMemory(null, null);

            Assert.False(memory.HasSize);
            Assert.Null(memory.Width);
            Assert.Null(memory.Height);
        }

        [Fact]
        public void Ctor_SavedSize_IsWhatThePanelOpensAt()
        {
            var memory = new HudSizeMemory(ResizedWidth, ResizedHeight);

            Assert.True(memory.HasSize);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        [Theory]
        [InlineData(null, 760.0)]
        [InlineData(1600.0, null)]
        [InlineData(0.0, 760.0)]
        [InlineData(1600.0, 0.0)]
        [InlineData(-5.0, -5.0)]
        public void Ctor_HalfOrNonPositiveSavedSize_IsNeverResized(double? width, double? height)
        {
            var memory = new HudSizeMemory(width, height);

            Assert.False(memory.HasSize);
            Assert.Null(memory.Width);
            Assert.Null(memory.Height);
        }

        // ---- what the WINDOW does, read from the compiled IL -------------------
        //
        // These are the bridge between the decisions above and the window that has to make them.
        // They are read from the built AgentEyesApp.dll rather than from source text, because a
        // source scan is defeated by an alias, a helper or a different spelling (CompiledCode's own
        // reasoning, issue #155). They are PRESENCE and ORDER assertions, and CallsIn/CallSites THROW
        // rather than return empty when the method or the assembly is missing, so none of them can
        // pass by finding nothing.

        /// <summary>
        /// SavePosition runs from the Closed handler, by which point the window has already
        /// auto-sized back to the pill. It must therefore persist the REMEMBERED size, and must not
        /// consult the window's live size or sizing mode at all - reading them there is round 1's
        /// defect.
        /// </summary>
        [Fact]
        public void SavePosition_DoesNotReadTheWindowsLiveSizeAtCloseTime()
        {
            IReadOnlyList<string> calls =
                CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.HudWindow::SavePosition");

            string[] liveSizeReads =
            {
                "System.Windows.FrameworkElement::get_ActualWidth",
                "System.Windows.FrameworkElement::get_ActualHeight",
                "System.Windows.Window::get_SizeToContent",
            };

            var found = calls.Where(c => liveSizeReads.Contains(c)).Distinct().ToList();

            Assert.True(found.Count == 0,
                "HudWindow.SavePosition reads the window's live sizing state at close time: "
                + string.Join(", ", found)
                + ". The stop has already put SizeToContent back to WidthAndHeight by then, so this "
                + "reads the pill (or, behind a sizing-mode guard, writes nothing) - issue #33 AC7. "
                + "Persist the size remembered by HudSizeMemory instead.");

            Assert.Contains("AgentEyes.App.HudSizeMemory::get_Width", calls);
            Assert.Contains("AgentEyes.App.HudSizeMemory::get_Height", calls);
        }

        /// <summary>
        /// A memory seeded from nothing restores nothing. The HUD must hand its memory the size the
        /// LAST recording's HUD saved, or AC7 fails across recordings however well the memory works
        /// within one - and that is a wiring fact no behavioural test of the memory can see.
        /// </summary>
        [Fact]
        public void HudWindow_SeedsItsMemoryFromTheSavedConfig()
        {
            // CallSites, not CallsIn: the constructor's many lambdas all fold back onto ".ctor", so
            // there is no single body to read an order from - and presence is the whole question here.
            string[] wanted =
            {
                "AgentEyes.App.HudSizeMemory::.ctor",
                "AgentEyes.App.Config::get_HudWidth",
                "AgentEyes.App.Config::get_HudHeight",
            };

            var inTheConstructor = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => wanted.Contains(c))
                .Where(s => s.Method.Contains("HudWindow::.ctor"))
                .Select(s => s.Callee)
                .Distinct()
                .ToList();

            var missing = wanted.Where(w => !inTheConstructor.Contains(w)).ToList();

            Assert.True(missing.Count == 0,
                "HudWindow's constructor never " + string.Join(" / ", missing)
                + ", so the size the last recording's HUD saved is not handed to the new HUD's "
                + "memory and the panel cannot come back where it was left (issue #33, AC7).");
        }

        /// <summary>
        /// THE GUARD ON ROUND 2'S DEFECT. HudWindow must not size itself: the whole reason the
        /// preview panel opened at the pill's 367x52 is that ApplyPreviewState drove SizeToContent,
        /// Width and Height by hand, in an order whose reports it then trusted. That sequence now
        /// lives in HudPreviewSizing, where a test can drive it against a real WPF window - and it
        /// must stay there. This fails the moment anyone puts the sizing back inline, whatever they
        /// name the variables.
        /// </summary>
        [Fact]
        public void ApplyPreviewState_DoesNotSizeTheWindowItself()
        {
            IReadOnlyList<string> calls =
                CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.HudWindow::ApplyPreviewState");

            string[] sizingTheWindowByHand =
            {
                "System.Windows.Window::set_SizeToContent",
                "System.Windows.FrameworkElement::set_Width",
                "System.Windows.FrameworkElement::set_Height",
            };

            var found = calls.Where(c => sizingTheWindowByHand.Contains(c)).Distinct().ToList();

            Assert.True(found.Count == 0,
                "HudWindow.ApplyPreviewState sizes the window by hand (" + string.Join(", ", found)
                + "). Opening the preview panel is a COMMAND with a source, not a naked assignment: "
                + "the size it opens at comes from HudSizeMemory and nothing about applying it may "
                + "be mistaken later for a size somebody chose (issue #33, AC1 and AC7). Go through "
                + "HudPreviewSizing, which is driven against a real WPF window by "
                + "HudPreviewSizingOrderTests.");

            Assert.Contains("AgentEyes.App.HudPreviewSizing::ShowPanel", calls);
            Assert.Contains("AgentEyes.App.HudPreviewSizing::HidePanel", calls);
        }

        /// <summary>
        /// The same guard on the stop path. SetStatus takes the panel down on every ordinary stop,
        /// and it is the auto-size that round 1's save was defeated by.
        /// </summary>
        [Fact]
        public void SetStatus_TakesThePanelDownThroughTheSharedSizingPath()
        {
            IReadOnlyList<string> calls =
                CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.HudWindow::SetStatus");

            Assert.DoesNotContain("System.Windows.Window::set_SizeToContent", calls);
            Assert.Contains("AgentEyes.App.HudPreviewSizing::HidePanel", calls);
        }

        /// <summary>
        /// THE CANARY REPORTS ITSELF, so that no caller can drop it (Review Gate round 2 on PR #39,
        /// defect 2).
        ///
        /// It used to be RETURNED for the caller to log, and one of the two callers did not. The
        /// explicit Show/Hide click logged it; <c>HudWindow.SetStatus</c> - the ordinary stop, the
        /// most common path there is - discarded the return value, so on the one route where an
        /// unrecorded size actually costs the person their layout, the canary reported to nobody. A
        /// warning whose delivery depends on each caller remembering to listen is not a warning.
        ///
        /// ORDER, not just presence: the canary must be asked for and reported BEFORE the window is
        /// auto-sized back to the pill, because that assignment is what destroys the evidence. And
        /// it goes through <c>PreviewLog</c>, not the shared logger, because this runs on the WPF
        /// dispatcher that serves the Stop button.
        /// </summary>
        [Fact]
        public void HidePanel_ReportsTheUnaccountedSizeItself_BeforeTheAutoSizeDestroysIt()
        {
            var calls = CompiledCode.CallsIn(CompiledCode.AppAssembly,
                                             "AgentEyes.App.HudPreviewSizing::HidePanel").ToList();

            int asked = calls.IndexOf("AgentEyes.App.HudSizeMemory::UnattributedSize");
            int reported = calls.IndexOf("AgentEyes.Preview.PreviewLog::Warn");
            int autoSized = calls.IndexOf("System.Windows.Window::set_SizeToContent");

            Assert.True(asked >= 0,
                "HudPreviewSizing.HidePanel no longer asks whether the HUD ended up at a size no "
                + "gesture claimed, so a missing resize route is invisible again (issue #33, AC7).");
            Assert.True(reported >= 0,
                "HudPreviewSizing.HidePanel computes the completeness canary and REPORTS IT NOWHERE. "
                + "That is Review Gate round 2's defect 2: it was returned for the caller to log, and "
                + "HudWindow.SetStatus - the ordinary stop - dropped the return value, so on the most "
                + "common path the canary reported to nobody. Log it here, where it is computed.");
            Assert.True(reported > asked && reported < autoSized,
                "The canary is reported outside the window between asking for it and auto-sizing the "
                + "window back to the pill. The auto-size is what destroys the size being reported on.");
        }

        /// <summary>The companion: the ordinary stop really does go through the method that reports
        /// it. Without this, the guard above is satisfied by a HidePanel nothing on the stop path
        /// calls.</summary>
        [Fact]
        public void TheOrdinaryStop_ReachesTheReportingHidePanel()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.AppAssembly,
                                       new[] { "AgentEyes.App.HudWindow::SetStatus" }),
                StringComparer.Ordinal);

            Assert.Contains("AgentEyes.App.HudPreviewSizing::HidePanel", reached);
            Assert.Contains("AgentEyes.App.HudSizeMemory::UnattributedSize", reached);
        }

        /// <summary>
        /// ORDER, not presence. The remembered size must be taken from the memory BEFORE the window
        /// is touched: round 2's defect was reading it two statements AFTER the switch to manual
        /// sizing had already poisoned it. A presence assertion cannot see the difference; this can.
        /// </summary>
        [Fact]
        public void ShowPanel_AsksTheMemoryForTheSizeBeforeItTouchesTheWindow()
        {
            IReadOnlyList<string> calls =
                CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.HudPreviewSizing::ShowPanel");

            int askedTheMemory = calls.ToList().IndexOf("AgentEyes.App.HudSizeMemory::PreferredSize");
            int touchedTheWindow = calls.ToList().IndexOf("System.Windows.Window::set_SizeToContent");

            Assert.True(askedTheMemory >= 0, "HudPreviewSizing.ShowPanel never asks the memory what "
                + "size to open at, so the HUD cannot come back at the size it was left at (AC7).");
            Assert.True(touchedTheWindow >= 0, "HudPreviewSizing.ShowPanel never switches the window "
                + "to manual sizing, so the preview panel cannot be sized or resized at all (AC1, AC7).");
            Assert.True(askedTheMemory < touchedTheWindow,
                "HudPreviewSizing.ShowPanel switches the window to manual sizing BEFORE it asks the "
                + "memory what size to open at. That is issue #33's round-2 defect exactly: the "
                + "switch re-lays the window out synchronously and reports the pill's size, so the "
                + "value read afterwards is the pill.");
        }
    }
}
