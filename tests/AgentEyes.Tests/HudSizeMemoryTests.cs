using System.Collections.Generic;
using System.Linq;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, AC7 - the persistence half. "After stopping and starting a new recording, the HUD
    /// returns at the size and screen position it was left at."
    ///
    /// QA reproduced the defect on 2026-08-28: the HUD resized to 1600x760 came back at 520x400 on
    /// the next recording, because the only writer of HudWidth/HudHeight read the window's LIVE
    /// sizing state in the Closed handler - and the stop had already put SizeToContent back to
    /// WidthAndHeight before Closed fired, so the guard was false and nothing was written.
    ///
    /// THE BUG IS AN ORDERING BUG. That is what these tests are shaped around. A test that only
    /// asked "does the save write the size when the window is manually sized?" would have passed
    /// against the broken code and proved nothing at all - the broken code writes the size perfectly
    /// well in the one state it is never in when it matters. So the tests below replay the SEQUENCE
    /// a real stop performs, auto-sizing report and all, and then ask what the next recording opens
    /// at.
    ///
    /// WHAT THESE TESTS CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): they
    /// drive <see cref="HudSizeMemory"/>, not a WPF window - no unit test in this repo starts one,
    /// and one that did would write the developer's real %LOCALAPPDATA% config. The two facts that
    /// bridge the gap - that HudWindow.SavePosition no longer reads the window's live size, and that
    /// the window actually feeds its sizes to the memory - are asserted against the COMPILED IL of
    /// AgentEyesApp at the bottom of this file. What remains outside all of it is whether WPF raises
    /// SizeChanged for a given layout; that is QA's runtime check, and the AC7 reproduction is the
    /// instrument for it.
    /// </summary>
    public class HudSizeMemoryTests
    {
        // The window's own numbers, so a reader can see the reproduction rather than infer it.
        private const double PillWidth = 367, PillHeight = 52;          // observed by QA on the pill HUD
        private const double DefaultPreviewWidth = 520, DefaultPreviewHeight = 400;
        private const double ResizedWidth = 1600, ResizedHeight = 760;  // the size QA left the HUD at

        // ---- the reproduction ------------------------------------------------

        /// <summary>
        /// The exact sequence QA ran. The load-bearing line is the auto-sized report in the middle:
        /// that is HudWindow.SetStatus taking the panel down on an ordinary stop, and it happens
        /// BEFORE the window closes. The remembered size must survive it.
        /// </summary>
        [Fact]
        public void Observe_StopAutoSizesBeforeClose_StillRemembersTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(manuallySized: true, DefaultPreviewWidth, DefaultPreviewHeight); // panel opens
            memory.Observe(manuallySized: true, ResizedWidth, ResizedHeight);               // person resizes
            memory.Observe(manuallySized: false, PillWidth, PillHeight);                    // stop: SetStatus

            Assert.True(memory.HasSize);
            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>
        /// The whole criterion end to end: resize, stop, save, start a new recording. The second
        /// memory is seeded the way the next HUD is seeded - from the config the first one wrote.
        /// </summary>
        [Fact]
        public void Observe_ResizedThenStoppedThenReopened_NextRecordingOpensAtTheResizedSize()
        {
            // Recording 1: fresh config, panel opens at the default, person resizes, stop.
            var first = new HudSizeMemory(null, null);
            first.Observe(manuallySized: true, DefaultPreviewWidth, DefaultPreviewHeight);
            first.Observe(manuallySized: true, ResizedWidth, ResizedHeight);
            first.Observe(manuallySized: false, PillWidth, PillHeight);

            // What SavePosition persists on Closed.
            double? savedWidth = first.Width, savedHeight = first.Height;

            // Recording 2: a new HUD seeded from that config.
            var second = new HudSizeMemory(savedWidth, savedHeight);

            Assert.Equal(ResizedWidth, second.Width);
            Assert.Equal(ResizedHeight, second.Height);
        }

        /// <summary>
        /// The second shape of the same bug: "resize -> hide the preview -> stop". Hiding the panel
        /// auto-sizes the window too, so a memory that forgot on an auto-sized report would lose the
        /// size here even if it survived the stop.
        /// </summary>
        [Fact]
        public void Observe_PreviewHiddenAfterResizeThenStopped_StillRemembersTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(manuallySized: true, ResizedWidth, ResizedHeight);   // resized with the panel up
            memory.Observe(manuallySized: false, PillWidth, PillHeight);        // "Hide preview"
            memory.Observe(manuallySized: false, PillWidth, PillHeight);        // stop: SetStatus

            Assert.Equal(ResizedWidth, memory.Width);
            Assert.Equal(ResizedHeight, memory.Height);
        }

        /// <summary>Show, resize, hide, show again inside ONE recording: the panel comes back at the
        /// size it was left at, not at the default.</summary>
        [Fact]
        public void Observe_HiddenAndShownAgainInOneRecording_ReopensAtTheResizedSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(manuallySized: true, DefaultPreviewWidth, DefaultPreviewHeight);
            memory.Observe(manuallySized: true, ResizedWidth, ResizedHeight);
            memory.Observe(manuallySized: false, PillWidth, PillHeight);   // hidden

            Assert.Equal(ResizedWidth, memory.Width);   // what the re-show opens at
            Assert.Equal(ResizedHeight, memory.Height);
        }

        // ---- the pill is never a remembered size -----------------------------

        [Fact]
        public void Observe_OnlyAutoSizedReports_RemembersNothing()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(manuallySized: false, PillWidth, PillHeight);
            memory.Observe(manuallySized: false, PillWidth, PillHeight);

            Assert.False(memory.HasSize);
            Assert.Null(memory.Width);
            Assert.Null(memory.Height);
        }

        [Fact]
        public void Observe_AutoSizedAfterAResize_DoesNotOverwriteTheRememberedSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(manuallySized: true, ResizedWidth, ResizedHeight);
            memory.Observe(manuallySized: false, PillWidth, PillHeight);

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

            memory.Observe(manuallySized: true, width, height);

            Assert.False(memory.HasSize);
        }

        [Fact]
        public void Observe_NonPositiveSize_DoesNotDestroyAnEarlierSize()
        {
            var memory = new HudSizeMemory(null, null);

            memory.Observe(manuallySized: true, ResizedWidth, ResizedHeight);
            memory.Observe(manuallySized: true, 0, 0);

            Assert.Equal(ResizedWidth, memory.Width);
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
        // These two are the bridge between the decisions above and the window that has to make them.
        // They are read from the built AgentEyesApp.dll rather than from source text, because a
        // source scan is defeated by an alias, a helper or a different spelling (CompiledCode's own
        // reasoning, issue #155). Both are PRESENCE assertions and CallsIn/CallSites THROW rather
        // than return empty when the method or the assembly is missing, so neither can pass by
        // finding nothing.

        /// <summary>
        /// SavePosition runs from the Closed handler, by which point the window has already
        /// auto-sized back to the pill. It must therefore persist the REMEMBERED size, and must not
        /// consult the window's live size or sizing mode at all - reading them there is the defect.
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
        /// A memory nothing feeds is an empty memory. The window must offer every size it takes,
        /// which is what makes the resized size present at close time in the first place.
        /// </summary>
        [Fact]
        public void HudWindow_OffersEverySizeItTakesToTheMemory()
        {
            var observers = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.HudSizeMemory::Observe")
                .ToList();

            Assert.True(observers.Count > 0,
                "Nothing in AgentEyesApp calls HudSizeMemory.Observe, so the memory can never hold a "
                + "size and the HUD cannot come back at the size it was left at (issue #33, AC7).");

            Assert.Contains(observers, o => o.Method.Contains("HudWindow"));
        }
    }
}
