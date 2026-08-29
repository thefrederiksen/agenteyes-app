using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, AC7 - THE INVERSION, and the guard that keeps it inverted.
    ///
    /// WHY THIS FILE EXISTS. Three fixes were shipped for one defect class: a layout event mistaken
    /// for a person's intent, so a size nobody chose was written to config.json. Each fix named a
    /// transition that produced a bogus size and suppressed it - the stop's auto-size, then the
    /// panel-open transition, then the same transition on an unshown window. That is a BLOCKLIST,
    /// and a blocklist can only ever exclude what somebody has already been burned by. WPF has an
    /// open-ended supply of layout-driven size changes nobody has enumerated: a DPI change, a
    /// monitor change, a restore from minimise, a theme change, the next panel added to the HUD.
    ///
    /// The round-4 design is an ALLOWLIST. A size is recorded only when a person resizing the window
    /// is POSITIVELY IDENTIFIED, and there are exactly three ways that can happen - the Win32
    /// resize-modal loop, the panel's resize grip, and a UI Automation TransformPattern command.
    /// Nothing observes layout at all.
    ///
    /// This file holds that shut in two layers:
    ///  - THE STATE MACHINE (below): what the modal loop's messages mean, including the case that
    ///    makes a naive version wrong - a window MOVE runs the same loop and ends with the same
    ///    WM_EXITSIZEMOVE.
    ///  - THE CALL GRAPH, read from the compiled IL: that nothing else can reach the memory. This is
    ///    the part that makes the class of defect impossible rather than merely absent. A future
    ///    panel that resizes the window during its own layout cannot record a size, because
    ///    recording is not something the window's size does - it is something a gesture does, and a
    ///    gesture has to come through HudUserResize. Adding a fourth writer turns these red.
    ///
    /// WHAT THIS CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): it does not
    /// prove that Windows sends WM_SIZING only for a user's border drag, or that UI Automation
    /// routes TransformPattern through the WPF peer. Those are facts about the platform; they were
    /// MEASURED on this machine and the ordering is recorded in
    /// docs/cencon/proof/issue-33/round4/window-message-evidence.md. What the suite does drive
    /// end to end is in <c>HudPreviewSizingOrderTests</c>, against a real WPF window.
    /// </summary>
    public class HudUserResizeTests
    {
        private const int WM_SIZING = 0x0214, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        private const int WM_SIZE = 0x0005, WM_WINDOWPOSCHANGED = 0x0047, WM_DPICHANGED = 0x02E0;

        // ---- the compiled call graph: nothing else can write a size -----------

        /// <summary>
        /// THE GUARD THAT MAKES THE DEFECT CLASS IMPOSSIBLE. HudSizeMemory has exactly one mutator,
        /// and only HudUserResize.Record may call it. Anything else that wants to remember a size -
        /// a new panel, a new transition, a helpful convenience on the window - has to add a call
        /// site, and this turns red the moment it does.
        /// </summary>
        [Fact]
        public void RecordUserResize_IsOnlyEverCalledByHudUserResize()
        {
            var writers = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                           c => c == "AgentEyes.App.HudSizeMemory::RecordUserResize")
                .Select(s => s.Method)
                .Distinct()
                .ToList();

            Assert.True(writers.Count > 0,
                "Nothing in AgentEyesApp calls HudSizeMemory.RecordUserResize, so no size a person "
                + "chooses can ever be remembered and the HUD cannot come back at the size it was "
                + "left at (issue #33, AC7).");

            var strangers = writers.Where(w => w != "AgentEyes.App.HudUserResize::Record").ToList();

            Assert.True(strangers.Count == 0,
                "A size is written to the HUD's memory from outside HudUserResize.Record: "
                + string.Join(", ", strangers)
                + ". That is issue #33's defect class returning. The memory is written ONLY where a "
                + "person resizing the window has been positively identified; every other caller is "
                + "claiming an intent it did not observe, and three releases were shipped that way "
                + "(the pill, the panel-open transition, the constructor path).");
        }

        /// <summary>
        /// And Record itself is reached only from the four gestures. Without this, the guard above
        /// could be satisfied by a Record that some layout handler calls.
        ///
        /// WHAT THIS CANNOT DO, stated rather than implied (DEVELOPMENT_METHOD.md 6c.5, and the
        /// Review Gate's round-1 finding on PR #34). It proves that nothing OUTSIDE this list reaches
        /// Record; it cannot prove the list is COMPLETE - that Windows has no further way for a
        /// person to resize a window. That is precisely how maximise was missed: it was a genuine
        /// route that was never listed, and no enumeration can report a member it does not have.
        /// Two things carry that weight instead, and neither is another hand-written list:
        ///  - <see cref="NothingInTheHudEverSetsItsOwnWindowState"/> turns the newest route's
        ///    identification from a claim about Windows into a claim about the COMPILED CODE, which
        ///    is checkable here: every WindowState change the HUD sees came from outside the app,
        ///    because the app never assigns one.
        ///  - The RUNTIME canary, <c>HudSizeMemory.UnattributedSize</c>, reports a size the HUD ended
        ///    up at that no gesture claimed - so a route that is still missing shows up as a WARNING
        ///    naming the size, instead of as a silently wrong config.
        /// </summary>
        [Fact]
        public void Record_IsOnlyEverReachedFromAPositivelyIdentifiedGesture()
        {
            string[] gestures =
            {
                "AgentEyes.App.HudUserResize::OnWindowMessage",   // the Win32 move/size modal loop: border drag, Aero Snap
                "AgentEyes.App.HudUserResize::ByWindowState",     // maximise / restore / snap to the top of the screen
                "AgentEyes.App.HudUserResize::ByGrip",            // the preview panel's resize grip
                "AgentEyes.App.HudUserResize::ByAutomation",      // UI Automation's TransformPattern
            };

            var callers = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.HudUserResize::Record")
                .Select(s => s.Method)
                .Distinct()
                .ToList();

            var missing = gestures.Where(g => !callers.Contains(g)).ToList();
            Assert.True(missing.Count == 0,
                "These gestures no longer record the size the person left the HUD at: "
                + string.Join(", ", missing) + " (issue #33, AC7).");

            var strangers = callers.Where(c => !gestures.Contains(c)).ToList();
            Assert.True(strangers.Count == 0,
                "HudUserResize.Record is reached from something that is not one of the four "
                + "gestures a person can resize this window with: " + string.Join(", ", strangers)
                + ". Whatever it is, it cannot prove a person did anything, and issue #33 has been "
                + "shipped three times on exactly that assumption.");
        }

        /// <summary>
        /// THE CODE THAT DECIDES SIZES DOES NOT WATCH LAYOUT. This is the fact the three shipped
        /// defects all depended on: they read the window's own size reports and tried to sort them
        /// into the person's and the layout's. There is nothing to sort any more.
        ///
        /// SCOPE, stated rather than implied. This covers the three sizing classes, not all of
        /// HudWindow: HudWindow legitimately watches its PREVIEW SURFACE's size to lay the camera
        /// inset out (issue #33 AC4, and issue #36 adds to that), and IL cannot tell which object an
        /// add_SizeChanged targets. That handler is harmless precisely because of
        /// <see cref="RecordUserResize_IsOnlyEverCalledByHudUserResize"/>: watching layout is not
        /// what caused three defects - watching layout AND being able to write a size is, and no
        /// layout handler anywhere in the app can do the second thing any more.
        /// </summary>
        [Fact]
        public void TheSizingCodeDoesNotSubscribeToLayoutOrSizeChanges()
        {
            string[] theSizingClasses =
            {
                "AgentEyes.App.HudSizeMemory::",
                "AgentEyes.App.HudPreviewSizing::",
                "AgentEyes.App.HudUserResize::",
            };

            var subscriptions = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                           c => c.EndsWith("::add_SizeChanged", StringComparison.Ordinal)
                             || c.EndsWith("::add_LayoutUpdated", StringComparison.Ordinal))
                .Where(s => theSizingClasses.Any(t => s.Method.StartsWith(t, StringComparison.Ordinal)))
                .Select(s => $"{s.Method} -> {s.Callee}")
                .Distinct()
                .ToList();

            Assert.True(subscriptions.Count == 0,
                "The HUD's sizing code subscribes to a layout or size-change event: "
                + string.Join(", ", subscriptions)
                + ". Every size WPF reports there is the window mid-layout - the pill it is leaving, "
                + "a half-applied width, a 0x0 window that has not been shown yet - and issue #33 was "
                + "shipped three times trying to tell those apart from a person's resize. If a size "
                + "needs remembering, identify the GESTURE (HudUserResize), not the size change.");
        }

        /// <summary>
        /// The four gestures are wired to the window. Each of them is a fact no behavioural test of
        /// HudUserResize can see, and all four are load-bearing: without the hook a border drag and a
        /// snap are invisible, without the StateChanged subscription a maximise is, without the peer
        /// UI Automation resizes the HUD behind WPF's back, and without the grip the chromeless
        /// window has no affordance anybody can find.
        /// </summary>
        [Fact]
        public void HudWindow_WiresUpEveryGestureRoute()
        {
            var wiring = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c.StartsWith("AgentEyes.App.HudUserResize::",
                                                                       StringComparison.Ordinal))
                .Select(s => $"{s.Method} -> {s.Callee}")
                .ToList();

            Assert.Contains("AgentEyes.App.HudWindow::.ctor -> AgentEyes.App.HudUserResize::Watch", wiring);
            Assert.Contains("AgentEyes.App.HudWindow::.ctor -> AgentEyes.App.HudUserResize::ByGrip", wiring);
            Assert.Contains("AgentEyes.App.HudUserResize::Watch "
                          + "-> AgentEyes.App.HudUserResize::ByWindowState", wiring);
            Assert.Contains("AgentEyes.App.HudWindow::OnCreateAutomationPeer "
                          + "-> AgentEyes.App.HudUserResize::CreatePeer", wiring);
            Assert.Contains("AgentEyes.App.HudWindowAutomationPeer::Resize "
                          + "-> AgentEyes.App.HudUserResize::ByAutomation", wiring);
        }

        /// <summary>
        /// WHAT MAKES THE MAXIMISE ROUTE A POSITIVE IDENTIFICATION RATHER THAN A GUESS, and the one
        /// piece of the completeness argument that IS checkable in-process (Review Gate round 1 on
        /// PR #34, DEVELOPMENT_METHOD.md 6c.5).
        ///
        /// A window-state change is treated as the person's doing. That is only sound while the app
        /// itself never changes this window's state: the moment somebody adds an app-driven maximise,
        /// restore or minimise to the HUD, a size the LAYOUT produced starts being recorded as a size
        /// somebody chose - which is issue #33's defect class, shipped three times already.
        ///
        /// So the claim is not left as a comment. It is read from the compiled IL, where a property
        /// assignment is one instruction carrying one metadata token however the C# was spelled.
        ///
        /// SCOPE, stated rather than implied: this covers the HUD's own classes. The launcher, the
        /// preset editor and the test panel legitimately set THEIR windows' states, and none of them
        /// is the HUD or writes the HUD's size memory.
        /// </summary>
        [Fact]
        public void NothingInTheHudEverSetsItsOwnWindowState()
        {
            var setters = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                           c => c.EndsWith("::set_WindowState", StringComparison.Ordinal))
                .Where(s => s.Method.StartsWith("AgentEyes.App.Hud", StringComparison.Ordinal))
                .Select(s => $"{s.Method} -> {s.Callee}")
                .Distinct()
                .ToList();

            Assert.True(setters.Count == 0,
                "The HUD sets its own WindowState: " + string.Join(", ", setters)
                + ". That breaks the only reason a window-state change can be attributed to a PERSON "
                + "(HudUserResize.ByWindowState): until now every state change this window saw came "
                + "from outside the app. An app-driven maximise or restore would be recorded as a "
                + "size somebody chose, which is issue #33's defect class - a layout event mistaken "
                + "for intent. Either remove the assignment, or give ByWindowState a way to tell the "
                + "app's own state changes from the person's before it records anything.");
        }

        /// <summary>
        /// The two call sites must share ONE memory instance, or the window saves a memory nothing
        /// ever wrote to. QA named this as an undefended blind spot in round 3 - a mutation that
        /// hands ShowPanel a different HudSizeMemory than the one the gestures write compiles and
        /// leaves the whole suite green. It is defended here: the HUD constructs exactly one.
        /// </summary>
        [Fact]
        public void HudWindow_ConstructsExactlyOneSizeMemory()
        {
            var constructions = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.HudSizeMemory::.ctor")
                .Where(s => s.Method.StartsWith("AgentEyes.App.HudWindow", StringComparison.Ordinal))
                .ToList();

            Assert.True(constructions.Count == 1,
                "HudWindow constructs " + constructions.Count + " HudSizeMemory instances ("
                + string.Join(", ", constructions.Select(c => c.Method))
                + "). With more than one, the memory the gestures write and the memory SavePosition "
                + "reads can be different objects - every size the person chooses is then dropped, "
                + "and no behavioural test can see it (issue #33, AC7).");
        }

        // ---- the resize-modal-loop state machine ------------------------------

        /// <summary>
        /// The gesture: a modal loop that dragged a sizing edge, now finished. WM_SIZING is what
        /// identifies it - Windows sends it only while a sizing edge is being dragged, and never for
        /// a programmatic resize, a layout pass, a DPI change or a restore.
        /// </summary>
        [Fact]
        public void ADragOfTheSizingBorder_RecordsTheSizeTheWindowWasLeftAt()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(1560, 400);

                rig.Send(WM_ENTERSIZEMOVE);
                rig.Send(WM_SIZING);
                rig.Send(WM_SIZING);
                rig.Send(WM_EXITSIZEMOVE);

                Assert.Equal(1560, rig.Memory.Width!.Value, 1);
                Assert.Equal(400, rig.Memory.Height!.Value, 1);
            });
        }

        /// <summary>
        /// THE CASE A NAIVE VERSION GETS WRONG. Dragging the window somewhere else runs the SAME
        /// modal loop and ends with the SAME WM_EXITSIZEMOVE, and the window's size at that moment
        /// is whatever the panel happens to be - a size nobody chose. Only WM_SIZING separates them.
        /// </summary>
        [Fact]
        public void AMoveOfTheWindow_RecordsNothing()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(1560, 400);

                rig.Send(WM_ENTERSIZEMOVE);
                rig.Send(WM_EXITSIZEMOVE);

                Assert.False(rig.Memory.HasSize,
                    $"Dragging the HUD across the desktop recorded {rig.Memory.Width}x"
                    + $"{rig.Memory.Height} as a size the person chose.");
            });
        }

        /// <summary>A resize followed by a move: the second loop must not inherit the first's
        /// evidence. WM_ENTERSIZEMOVE resets the claim for exactly this reason.</summary>
        [Fact]
        public void AMoveAfterAResize_DoesNotRecordAgain()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(1560, 400);
                rig.Send(WM_ENTERSIZEMOVE);
                rig.Send(WM_SIZING);
                rig.Send(WM_EXITSIZEMOVE);
                Assert.Equal(1560, rig.Memory.Width!.Value, 1);

                // Something else resizes the window - a layout pass, a new panel - and the person
                // then merely drags the HUD somewhere else.
                rig.TheWindowBecomes(900, 300);
                rig.Send(WM_ENTERSIZEMOVE);
                rig.Send(WM_EXITSIZEMOVE);

                Assert.Equal(1560, rig.Memory.Width!.Value, 1);
                Assert.Equal(400, rig.Memory.Height!.Value, 1);
            });
        }

        /// <summary>
        /// The messages a size change actually arrives on. Every layout pass, every Width
        /// assignment, every DPI change and every restore produces these and nothing else - and not
        /// one of them is evidence that a person did anything.
        /// </summary>
        [Theory]
        [InlineData(WM_SIZE)]
        [InlineData(WM_WINDOWPOSCHANGED)]
        [InlineData(WM_DPICHANGED)]
        [InlineData(WM_SIZING)]          // a sizing drag that never ended is not a size left anywhere
        public void TheMessagesALayoutDrivenResizeArrivesOn_RecordNothing(int message)
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(1560, 400);

                for (int i = 0; i < 5; i++) rig.Send(message);

                Assert.False(rig.Memory.HasSize,
                    $"Message 0x{message:X4} recorded {rig.Memory.Width}x{rig.Memory.Height} as a "
                    + "size the person chose. It is a notification that the window's size changed, "
                    + "not evidence that anybody changed it.");
            });
        }

        /// <summary>A whole recording's worth of size changes with no gesture anywhere in it - the
        /// hands-off case QA reproduced - leaves nothing behind.</summary>
        [Fact]
        public void AHandsOffRecording_RecordsNothing()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(1560, 400);

                foreach (int message in new[] { WM_WINDOWPOSCHANGED, WM_SIZE, WM_SIZE,
                                                WM_WINDOWPOSCHANGED, WM_DPICHANGED, WM_SIZE })
                    rig.Send(message);

                Assert.False(rig.Memory.HasSize);
            });
        }

        /// <summary>
        /// AERO SNAP, at the message level. The person drags the title bar to a screen edge: the same
        /// modal loop a move runs, no WM_SIZING anywhere in it, and the window is a different size by
        /// the end. Requiring WM_SIZING dropped this resize entirely (Review Gate round 1 on PR #34).
        /// </summary>
        [Fact]
        public void ALoopThatEndedAtADifferentSize_RecordsTheSizeItEndedAt()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(900, 300);

                rig.Send(WM_ENTERSIZEMOVE);
                rig.TheWindowBecomes(1560, 400);   // Windows snaps it, mid-loop
                rig.Send(WM_EXITSIZEMOVE);

                Assert.True(rig.Memory.HasSize,
                    "A modal loop the window came out of at a different size recorded nothing, so a "
                    + "Windows snap is dropped and the HUD comes back at its old size.");
                Assert.Equal(1560, rig.Memory.Width!.Value, 1);
                Assert.Equal(400, rig.Memory.Height!.Value, 1);
            });
        }

        /// <summary>
        /// And the size-difference evidence must not be readable outside a loop. A stray
        /// WM_EXITSIZEMOVE with no WM_ENTERSIZEMOVE before it has no starting size to compare
        /// against, and comparing against nothing would record whatever the window happens to be.
        /// </summary>
        [Fact]
        public void AnExitWithNoLoopBeforeIt_RecordsNothing()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(1560, 400);

                rig.Send(WM_EXITSIZEMOVE);
                rig.Send(WM_EXITSIZEMOVE);

                Assert.False(rig.Memory.HasSize,
                    $"An unpaired WM_EXITSIZEMOVE recorded {rig.Memory.Width}x{rig.Memory.Height} as a "
                    + "size the person chose.");
            });
        }

        /// <summary>
        /// A MAXIMISE, at the state-change level: no modal loop at all, and the size arrives one
        /// dispatcher turn later, once WPF's layout (which runs at the higher Render priority) has
        /// given the window the size its new state implies.
        /// </summary>
        [Fact]
        public void AWindowStateCommand_RecordsTheSizeTheWindowSettlesAt()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(520, 400);

                rig.TheWindowStateBecomes(WindowState.Maximized);

                Assert.True(rig.Memory.HasSize,
                    "Maximising the HUD recorded nothing, so the next recording opens at the old size "
                    + "(Review Gate round 1 on PR #34).");
                Assert.Equal(rig.Window.ActualWidth, rig.Memory.Width!.Value, 1);
                Assert.Equal(rig.Window.ActualHeight, rig.Memory.Height!.Value, 1);
                Assert.True(rig.Memory.Width!.Value > 520,
                    $"the window did not actually grow when maximised (recorded {rig.Memory.Width}), "
                    + "so this test measured nothing.");
            });
        }

        /// <summary>A restore from minimise puts a size BACK rather than choosing one - one of the
        /// layout transitions the three shipped defects were built on. Widening the allowlist must
        /// not widen it to this.</summary>
        [Fact]
        public void AMinimiseAndRestore_RecordsNothing()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowBecomes(520, 400);

                rig.TheWindowStateBecomes(WindowState.Minimized);
                rig.TheWindowStateBecomes(WindowState.Normal);

                Assert.False(rig.Memory.HasSize,
                    $"A minimise and restore recorded {rig.Memory.Width}x{rig.Memory.Height} as a size "
                    + "the person chose.");
            });
        }

        /// <summary>
        /// A gesture on a window that is auto-sizing to its content - the HUD with the preview panel
        /// down - leaves nothing behind. Whatever the pill is dragged to is discarded by the very
        /// next layout, so remembering it would remember a size that can never be restored.
        /// </summary>
        [Fact]
        public void ADragWhileTheWindowIsAutoSized_RecordsNothing()
        {
            RunOnRig(rig =>
            {
                rig.TheWindowAutoSizes();

                rig.Send(WM_ENTERSIZEMOVE);
                rig.Send(WM_SIZING);
                rig.Send(WM_EXITSIZEMOVE);

                Assert.False(rig.Memory.HasSize);
            });
        }

        // ---- the rig ---------------------------------------------------------

        /// <summary>
        /// The production <see cref="HudUserResize"/> against a real, laid-out WPF window, so what
        /// a recorded gesture stores is the size the window really has. Deliberately invisible
        /// (fully transparent, parked off the desktop, never activated) so `dotnet test` stays as
        /// quiet as it is today.
        /// </summary>
        private sealed class Rig
        {
            private readonly Window _window;
            private readonly HudUserResize _userResize;

            public readonly HudSizeMemory Memory = new(null, null);

            public Rig()
            {
                _window = new Window
                {
                    Title = "HUD user-resize rig",
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.CanResize,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Opacity = 0,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Topmost = false,
                    SizeToContent = SizeToContent.Manual,
                    MinWidth = 260,
                    MinHeight = 52,
                    Left = -8000,
                    Top = -8000,
                    Width = 520,
                    Height = 400,
                    Content = new Border { Width = 367, Height = 52, Background = Brushes.Black },
                };
                _userResize = new HudUserResize(_window, Memory);
                _userResize.Watch();
                _window.Show();
                Pump();
            }

            public Window Window => _window;

            /// <summary>The window ends up at this size - by whatever means, which is the point:
            /// no gesture is claimed for it.</summary>
            public void TheWindowBecomes(double width, double height)
            {
                _window.SizeToContent = SizeToContent.Manual;
                _window.Width = width;
                _window.Height = height;
                Pump();
            }

            /// <summary>The person maximises, restores or minimises the window. Nothing in the app
            /// does this to the HUD - which is exactly what makes the route a positive
            /// identification; see NothingInTheHudEverSetsItsOwnWindowState.</summary>
            public void TheWindowStateBecomes(WindowState state)
            {
                _window.WindowState = state;
                Pump();
            }

            /// <summary>The preview panel is down: the window auto-sizes to its content.</summary>
            public void TheWindowAutoSizes()
            {
                _window.SizeToContent = SizeToContent.WidthAndHeight;
                Pump();
            }

            /// <summary>Drive the production state machine with one window message.</summary>
            public void Send(int message)
            {
                _userResize.OnWindowMessage(message);
                Pump();
            }

            public void Pump()
            {
                for (int i = 0; i < 3; i++)
                {
                    var frame = new DispatcherFrame();
                    _window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
            }
        }

        /// <summary>
        /// Run a body against a fresh rig on a dedicated STA thread with its own dispatcher. The
        /// window is always closed and the dispatcher always shut down, pass or fail, so a failing
        /// assertion cannot leave a window behind for the rest of the suite.
        /// </summary>
        private static void RunOnRig(Action<Rig> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Rig? rig = null;
                try
                {
                    rig = new Rig();
                    body(rig);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { rig?.Window.Close(); } catch (Exception ex) { failure ??= ex; }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromSeconds(60)))
                throw new TimeoutException(
                    "The HUD user-resize rig did not finish within 60 s. This test needs an "
                    + "interactive desktop session: it creates a real (invisible, off-screen) window "
                    + "so the production code reads a real laid-out size.");

            if (failure != null)
                throw new Xunit.Sdk.XunitException(
                    failure is Xunit.Sdk.XunitException ? failure.Message : failure.ToString());
        }
    }
}
