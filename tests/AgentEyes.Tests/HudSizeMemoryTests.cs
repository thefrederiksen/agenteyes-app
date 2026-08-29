using System.Collections.Generic;
using System.Linq;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, AC7 (the HUD comes back at the size it was left at) and AC1 (toggling the preview
    /// shows the panel), at the level of the DECISION: which of the sizes a window reports is a size
    /// a person actually chose.
    ///
    /// TWO defects have now been shipped on this one question, and both were ordering, not arithmetic:
    ///
    ///  1. 2026-08-28, round 1 - the save read the window's live size in the Closed handler, by which
    ///     point the stop had already auto-sized the HUD back to the pill. A HUD left at 1600x760
    ///     came back at 520x400.
    ///  2. 2026-08-28, round 2 - the fix for (1) recorded every size the window reported while
    ///     manually sized. Switching to manual sizing re-lays the window out SYNCHRONOUSLY and
    ///     reports the pill's 367x52 from inside the assignment, so the pill became the remembered
    ///     size, was read straight back two statements later, and was written to config: the preview
    ///     panel opened at pill size with a zero-sized picture, for that recording and every one
    ///     after it.
    ///
    /// So these tests replay SEQUENCES, never single calls. A test that only asked "does the memory
    /// keep a manually-sized report?" passes against both shipped defects and proves nothing.
    ///
    /// WHAT THESE TESTS CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): they
    /// write the sequence themselves, so they can only prove what the memory does with a given
    /// order - never which order WPF actually produces. That question is what defeated round 2's
    /// suite, and it is answered separately in <c>HudPreviewSizingOrderTests</c>, which drives a real
    /// WPF window through this same production code and reads the order WPF really raises. The IL
    /// assertions at the bottom of this file are the third leg: they hold HudWindow to reaching this
    /// logic through <see cref="HudPreviewSizing"/> and to owning no sizing decisions of its own.
    /// </summary>
    public class HudSizeMemoryTests
    {
        // The window's own numbers, so a reader can see the reproduction rather than infer it.
        private const double PillWidth = 367, PillHeight = 52;          // observed by QA on the pill HUD
        private const double DefaultPreviewWidth = 520, DefaultPreviewHeight = 400;
        private const double ResizedWidth = 1600, ResizedHeight = 760;  // the size QA left the HUD at

        /// <summary>The reports WPF makes while the panel is being opened, in the order it makes
        /// them - the pill it is leaving, the width-applied-but-not-height size in between, and the
        /// commanded size on arrival. Measured in HudPreviewSizingOrderTests, replayed here.</summary>
        private static void ReplayTheOpeningTransition(HudSizeMemory memory, double commandedWidth, double commandedHeight)
        {
            memory.Observe(panelVisible: true, manuallySized: true, PillWidth, PillHeight);
            memory.Observe(panelVisible: true, manuallySized: true, commandedWidth, PillHeight);
            memory.Observe(panelVisible: true, manuallySized: true, commandedWidth, commandedHeight);
        }

        // ---- round 2's defect: the transition is not a choice ------------------

        /// <summary>
        /// THE SHIPPED DEFECT. The panel is opened on a HUD that is already on screen at the pill's
        /// size. Every size WPF reports on the way belongs to the transition, so when it is over the
        /// memory must still hold nothing at all - and the panel must have been commanded to the
        /// default, not to the pill.
        /// </summary>
        [Fact]
        public void OpenPanel_OnAPillSizedWindow_CommandsTheDefaultAndRemembersNothing()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);

            Assert.Equal(DefaultPreviewWidth, width);
            Assert.Equal(DefaultPreviewHeight, height);
            Assert.False(memory.HasSize);
            Assert.Null(memory.Width);
            Assert.Null(memory.Height);
        }

        /// <summary>The pill can no longer become the panel's size even if the window reports it
        /// LAST - the transition is over only when the commanded size arrives.</summary>
        [Fact]
        public void Observe_PillReportedThroughoutTheTransition_IsNeverRemembered()
        {
            var memory = new HudSizeMemory(null, null);

            memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            memory.Observe(panelVisible: true, manuallySized: true, PillWidth, PillHeight);
            memory.Observe(panelVisible: true, manuallySized: true, PillWidth, PillHeight);

            Assert.False(memory.HasSize);
        }

        /// <summary>Once the commanded size has arrived, the window is standing still and the next
        /// size it reports can only be the person's.</summary>
        [Fact]
        public void Observe_AfterTheTransition_RemembersTheResize()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);
            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);

            Assert.True(memory.HasSize);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>Re-opening the panel at a REMEMBERED size is the same transition, and its reports
        /// are no more a choice than the default's were - including the pill on the way through.
        /// </summary>
        [Fact]
        public void OpenPanel_WithARememberedSize_CommandsItAndDoesNotRerecordTheTransition()
        {
            var memory = new HudSizeMemory(ResizedWidth, ResizedHeight);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);

            Assert.Equal(ResizedWidth, width);
            Assert.Equal(ResizedHeight, height);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>A commanded size that comes back a hair different - a window's size makes a round
        /// trip through physical pixels on a scaled display - still counts as arrival, or the
        /// transition would never end and the person's later resizes would all be discarded.
        /// </summary>
        [Fact]
        public void Observe_CommandedSizeArrivesSlightlyRounded_StillEndsTheTransition()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            memory.Observe(panelVisible: true, manuallySized: true, width - 0.4, height + 0.3);

            Assert.False(memory.Settling);
            Assert.False(memory.HasSize);   // arrival is still not a choice

            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);
            Assert.Equal(ResizedWidth, memory.Width);
        }

        /// <summary>
        /// The wedge that must not exist. If the commanded size never arrives at all - clamped, or
        /// applied in a way that reports something else - the window still tells the memory when its
        /// layout has finished, and the person's resizes are honoured from there.
        /// </summary>
        [Fact]
        public void Settled_EndsATransitionTheCommandedSizeNeverArrivedFor()
        {
            var memory = new HudSizeMemory(null, null);

            memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            memory.Observe(panelVisible: true, manuallySized: true, PillWidth, PillHeight);
            Assert.True(memory.Settling);

            memory.Settled();

            Assert.False(memory.Settling);
            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);
            Assert.Equal(ResizedWidth, memory.Width);
        }

        /// <summary>Taking the panel down ends the transition too - the reports that follow are the
        /// pill, and the pill is excluded on its own merits.</summary>
        [Fact]
        public void PanelClosed_EndsTheTransitionWithoutForgettingTheSize()
        {
            var memory = new HudSizeMemory(ResizedWidth, ResizedHeight);

            memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            memory.PanelClosed();

            Assert.False(memory.Settling);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        // ---- round 1's defect: the size is gone by closing time ----------------

        /// <summary>
        /// The exact sequence QA ran in round 1. The load-bearing line is the auto-sized report at
        /// the end: that is HudWindow.SetStatus taking the panel down on an ordinary stop, and it
        /// happens BEFORE the window closes. The remembered size must survive it.
        /// </summary>
        [Fact]
        public void Observe_StopAutoSizesBeforeClose_StillRemembersTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);
            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);   // person resizes
            memory.PanelClosed();
            memory.Observe(panelVisible: false, manuallySized: false, PillWidth, PillHeight);       // stop: SetStatus

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
        public void Observe_ResizedThenStoppedThenReopened_NextRecordingOpensAtTheResizedSize()
        {
            var first = new HudSizeMemory(null, null);
            var (width, height) = first.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(first, width, height);
            first.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);
            first.PanelClosed();
            first.Observe(panelVisible: false, manuallySized: false, PillWidth, PillHeight);

            // What SavePosition persists on Closed.
            double? savedWidth = first.Width, savedHeight = first.Height;

            // Recording 2: a new HUD seeded from that config.
            var second = new HudSizeMemory(savedWidth, savedHeight);
            var (nextWidth, nextHeight) = second.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.Equal(ResizedWidth, nextWidth);
            Assert.Equal(ResizedHeight, nextHeight);
        }

        /// <summary>
        /// The second shape of round 1's bug: "resize -> hide the preview -> stop". Hiding the panel
        /// auto-sizes the window too, so a memory that forgot on an auto-sized report would lose the
        /// size here even if it survived the stop.
        /// </summary>
        [Fact]
        public void Observe_PreviewHiddenAfterResizeThenStopped_StillRemembersTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);
            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);
            memory.PanelClosed();
            memory.Observe(panelVisible: false, manuallySized: false, PillWidth, PillHeight);   // "Hide preview"
            memory.Observe(panelVisible: false, manuallySized: false, PillWidth, PillHeight);   // stop: SetStatus

            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>Show, resize, hide, show again inside ONE recording: the panel comes back at the
        /// size it was left at, not at the default, with nothing written to disk in between.</summary>
        [Fact]
        public void OpenPanel_HiddenAndShownAgainInOneRecording_ReopensAtTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);
            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);
            memory.PanelClosed();
            memory.Observe(panelVisible: false, manuallySized: false, PillWidth, PillHeight);   // hidden

            var (again, andAgain) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);

            Assert.Equal(ResizedWidth, again);
            Assert.Equal(ResizedHeight, andAgain);
        }

        // ---- the pill is never a remembered size -----------------------------

        [Fact]
        public void Observe_OnlyAutoSizedReports_RemembersNothing()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(panelVisible: true, manuallySized: false, PillWidth, PillHeight);
            memory.Observe(panelVisible: true, manuallySized: false, PillWidth, PillHeight);

            Assert.False(memory.HasSize);
            Assert.Null(memory.Width);
            Assert.Null(memory.Height);
        }

        /// <summary>A manually-sized report with the panel DOWN is not a panel size either. The two
        /// facts are kept separate on purpose: one of them going wrong must not be enough.</summary>
        [Fact]
        public void Observe_ManuallySizedWhileThePanelIsDown_IsNotAPanelSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(panelVisible: false, manuallySized: true, PillWidth, PillHeight);

            Assert.False(memory.HasSize);
        }

        [Fact]
        public void Observe_AutoSizedAfterAResize_DoesNotOverwriteTheRememberedSize()
        {
            var memory = new HudSizeMemory(null, null);

            var (width, height) = memory.OpenPanel(DefaultPreviewWidth, DefaultPreviewHeight);
            ReplayTheOpeningTransition(memory, width, height);
            memory.Observe(panelVisible: true, manuallySized: true, ResizedWidth, ResizedHeight);
            memory.Observe(panelVisible: true, manuallySized: false, PillWidth, PillHeight);

            Assert.NotEqual(PillWidth, memory.Width);
            Assert.Equal(ResizedWidth, memory.Width);
        }

        // ---- degenerate layouts are not sizes --------------------------------

        [Theory]
        [InlineData(0, 400)]
        [InlineData(520, 0)]
        [InlineData(-1, 400)]
        [InlineData(520, -1)]
        public void Observe_NonPositiveSize_IsNotASize(double width, double height)
        {
            var memory = new HudSizeMemory(null, null);
            memory.Settled();   // not in a transition; the size is rejected on its own merits

            memory.Observe(panelVisible: true, manuallySized: true, width, height);

            Assert.False(memory.HasSize);
        }

        [Fact]
        public void Observe_NonPositiveSize_DoesNotDestroyAnEarlierSize()
        {
            var memory = new HudSizeMemory(ResizedWidth, ResizedHeight);
            memory.Settled();

            memory.Observe(panelVisible: true, manuallySized: true, 0, 0);

            Assert.Equal(ResizedWidth, memory.Width);
        }

        [Theory]
        [InlineData(0, 400)]
        [InlineData(520, 0)]
        [InlineData(-1, -1)]
        public void OpenPanel_WithANonPositiveDefault_Throws(double defaultWidth, double defaultHeight)
        {
            var memory = new HudSizeMemory(null, null);

            // No fallback: a caller that cannot say how big the panel should be has a bug, and a
            // silently substituted number would put an arbitrary size on the person's screen.
            Assert.ThrowsAny<System.ArgumentException>(() => memory.OpenPanel(defaultWidth, defaultHeight));
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
        /// A memory nothing feeds is an empty memory. Something must offer the window's sizes, and
        /// the window must be wired to it - which is a different fact from "the code exists".
        /// </summary>
        [Fact]
        public void TheWindowsSizesAreOfferedToTheMemory()
        {
            var observers = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.HudSizeMemory::Observe")
                .ToList();

            Assert.True(observers.Count > 0,
                "Nothing in AgentEyesApp calls HudSizeMemory.Observe, so the memory can never hold a "
                + "size and the HUD cannot come back at the size it was left at (issue #33, AC7).");
            Assert.Contains(observers, o => o.Method.Contains("HudPreviewSizing::Attach"));

            var attached = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.HudPreviewSizing::Attach")
                .ToList();

            Assert.True(attached.Any(a => a.Method.Contains("HudWindow")),
                "HudWindow never attaches its size memory, so no size it takes is ever offered.");
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
                + "). Opening the preview panel is a TRANSITION, not an assignment: WPF reports the "
                + "window's size from inside those assignments, half-applied, and trusting those "
                + "reports is what made the panel open at the pill's 367x52 with a zero-sized picture "
                + "(issue #33, AC1). Go through HudPreviewSizing, which is driven against a real WPF "
                + "window by HudPreviewSizingOrderTests.");

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
        /// ORDER, not presence. The remembered size must be taken from the memory BEFORE the window
        /// is touched: round 2's defect was reading it two statements AFTER the switch to manual
        /// sizing had already poisoned it. A presence assertion cannot see the difference; this can.
        /// </summary>
        [Fact]
        public void ShowPanel_AsksTheMemoryForTheSizeBeforeItTouchesTheWindow()
        {
            IReadOnlyList<string> calls =
                CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.HudPreviewSizing::ShowPanel");

            int askedTheMemory = calls.ToList().IndexOf("AgentEyes.App.HudSizeMemory::OpenPanel");
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
