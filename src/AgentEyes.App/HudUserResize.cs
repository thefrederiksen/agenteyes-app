using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentEyes;
using AgentEyes.Preview;

namespace AgentEyes.App
{
    /// <summary>
    /// THE ONLY WAY A SIZE EVER REACHES <see cref="HudSizeMemory"/>, and therefore the only way one
    /// ever reaches config.json (issue #33, AC7).
    ///
    /// WHY THIS CLASS EXISTS. Three rounds of this defect were shipped by asking the wrong question.
    /// The old code watched <c>SizeChanged</c> and tried to decide, report by report, which of the
    /// sizes WPF announced was a size a person had chosen - excluding the pill, then the panel-open
    /// transition, then the transition on an unshown window. That is a BLOCKLIST, and a blocklist
    /// can only ever exclude the transitions somebody has already been burned by. WPF raises
    /// layout-driven size changes for reasons nobody has enumerated: a DPI change, a monitor change,
    /// a restore from minimise, a theme change, whatever panel is added to the HUD next. Every one of
    /// them was one release away from being recorded as a deliberate size.
    ///
    /// So the polarity is inverted. NOTHING is recorded unless a person resizing this window has been
    /// POSITIVELY IDENTIFIED. Windows offers a person exactly two mechanisms for resizing somebody
    /// else's top-level window, plus the two this app supplies itself, and each arrives here as its
    /// own typed route:
    ///
    ///  1. <b>The Win32 move/size modal loop</b> - the person is dragging the window. Everything the
    ///     shell drives through the window's own sizing UI runs this loop: a sizing-border drag, an
    ///     Aero Snap to a screen edge, a drag to the top of the screen. It is recognised as a RESIZE
    ///     when either
    ///     <list type="bullet">
    ///     <item><description><c>WM_SIZING</c> arrived - sent ONLY while a sizing edge is being
    ///     dragged; a layout pass, a DPI change, a restore, a construction and a <c>Width =</c>
    ///     assignment all produce <c>WM_WINDOWPOSCHANGED</c> + <c>WM_SIZE</c> and NEVER
    ///     <c>WM_SIZING</c>; or</description></item>
    ///     <item><description>the window came OUT of the loop a different size than it went in.
    ///     That is how Aero Snap resizes a window: the person drags the title bar to a screen edge,
    ///     so the loop is a MOVE loop with no <c>WM_SIZING</c> in it, and the window is nevertheless
    ///     half the desktop by the end. Nothing in this app resizes the HUD during a modal drag, so a
    ///     size that changed across the loop is the person's - and a plain move, where the size is
    ///     identical, still records nothing.</description></item>
    ///     </list>
    ///  2. <b>A window-STATE command</b> - maximise, restore, snap-to-top. These do NOT run the modal
    ///     loop at all (measured: posting the user maximise command produces <c>WM_SYSCOMMAND</c>
    ///     0xF030, <c>WM_WINDOWPOSCHANGED</c> and <c>WM_SIZE</c>, and no <c>WM_ENTERSIZEMOVE</c>,
    ///     <c>WM_SIZING</c> or <c>WM_EXITSIZEMOVE</c>) - which is how the Review Gate found the HUD
    ///     dropping a maximise on round 1 of PR #34. They arrive as <see cref="Window.StateChanged"/>,
    ///     and THE IDENTIFICATION IS PROVABLE RATHER THAN ASSUMED: nothing in AgentEyesApp ever
    ///     assigns this window's <c>WindowState</c>, so every state change it sees came from outside
    ///     the app - the person, or the shell acting for them. <c>HudUserResizeTests</c> asserts that
    ///     against the compiled IL, so the day somebody adds an app-driven maximise, this route stops
    ///     being a positive identification and the suite says so.
    ///  3. <b>The panel's resize grip</b> - <c>Thumb.DragDelta</c>, a mouse gesture on a control.
    ///     The HUD is chromeless, so this is the affordance most people will actually find.
    ///  4. <b>UI Automation's TransformPattern</b> - an accessibility tool, or QA, commanding a
    ///     resize through a typed API. It arrives at <see cref="ByAutomation"/> only because
    ///     <see cref="HudWindowAutomationPeer"/> advertises the pattern; layout cannot call it.
    ///
    /// There is no fifth way FROM INSIDE THIS APP, because there is no subscription to
    /// <c>SizeChanged</c> or <c>LayoutUpdated</c> anywhere in the HUD any more. A panel added to this
    /// window next year cannot reintroduce the defect by resizing the window during its own layout,
    /// because resizing the window is not what records a size - a gesture is, and it has to come
    /// through here. <c>HudUserResizeTests</c> holds that shut against the compiled IL.
    ///
    /// WHAT THAT LIST STILL CANNOT PROVE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.5).
    /// It is an allowlist of routes, and an allowlist proves its members, not its own exhaustiveness:
    /// no test in this repository can prove that Windows has no fifth way to resize a window that
    /// produces neither a modal loop nor a state change. What the code does instead is DETECT its own
    /// incompleteness - <see cref="HudSizeMemory.UnattributedSize"/> is checked when the panel comes
    /// down and reports, by name and by number, a size the HUD ended up at that no gesture ever
    /// claimed. A missing route is then a WARNING in the log rather than a silent wrong size, which
    /// is the most an in-process check can honestly offer.
    /// </summary>
    internal sealed class HudUserResize
    {
        /// <summary>Sent to a window while the person is DRAGGING a sizing edge, once per mouse
        /// move, so that the window can adjust the drag rectangle. Not sent for a move, and not sent
        /// by any programmatic resize.</summary>
        private const int WM_SIZING = 0x0214;

        /// <summary>The person has entered the move-or-resize modal loop (a border drag, a title
        /// drag, or the Size/Move system commands). Which of the two it is is not yet known.</summary>
        private const int WM_ENTERSIZEMOVE = 0x0231;

        /// <summary>The modal loop is over and the window has its final size. WPF has already
        /// reported that size by now - WM_SIZE arrives before this - so the window's live
        /// ActualWidth/ActualHeight here is what the person let go of.</summary>
        private const int WM_EXITSIZEMOVE = 0x0232;

        /// <summary>How far a size may differ across a modal drag loop and still count as "the same
        /// size". A move produces an exactly identical size; this only absorbs sub-pixel layout
        /// rounding, and is deliberately far below any resize a person can perform with a mouse.</summary>
        private const double SamePixel = 0.5;

        private readonly Window _window;
        private readonly HudSizeMemory _memory;

        /// <summary>Whether the modal loop currently running has been positively identified as a
        /// RESIZE by a WM_SIZING. False until one arrives - but no longer the only evidence, because
        /// Aero Snap resizes a window through a MOVE loop that never sends one.</summary>
        private bool _draggingASizingEdge;

        /// <summary>Whether a modal loop is actually running. Without it a stray WM_EXITSIZEMOVE that
        /// never had a WM_ENTERSIZEMOVE would compare the window against a starting size of zero,
        /// find a difference, and record a size no loop ever produced.</summary>
        private bool _inAModalLoop;

        /// <summary>The size the window had when the modal loop started. A loop that ends at a
        /// different size resized the window, whatever messages it did or did not send.</summary>
        private double _widthWhenTheLoopBegan;
        private double _heightWhenTheLoopBegan;

        /// <summary>Whether the preview panel was up when the modal loop started. Read at the START
        /// of the gesture on purpose: dragging the PILL's border resizes the HUD, and Windows
        /// switches the window out of auto-sizing when it does, so by the end of the drag the window
        /// looks exactly like a panel that was open all along. The pill's dimensions are not the
        /// preview panel's size, and this is the only moment at which the two can still be told
        /// apart.</summary>
        private bool _thePanelWasUpWhenTheGestureBegan;

        /// <summary>The window state the last state change left behind, so a restore FROM minimised -
        /// which puts a size back rather than choosing one - can be told from a maximise.</summary>
        private WindowState _lastWindowState;

        public HudUserResize(Window window, HudSizeMemory memory)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _lastWindowState = window.WindowState;
        }

        /// <summary>
        /// Start listening for the window's resize-modal loop and for its window-state commands. Safe
        /// to call before the window has an HWND (the HUD calls this from its constructor) or after
        /// it has one.
        /// </summary>
        public void Watch()
        {
            PreviewLog.Info("hud: watching for user resizes (sizing border, snap, maximise, grip, UI Automation)");
            _window.StateChanged += (_, _) => ByWindowState();
            if (new WindowInteropHelper(_window).Handle != IntPtr.Zero) { HookTheWindowMessages(); return; }
            _window.SourceInitialized += (_, _) => HookTheWindowMessages();
        }

        private void HookTheWindowMessages()
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException(
                    "The HUD window has an HWND but no HwndSource, so the person dragging its sizing "
                    + "border cannot be seen and the size they leave it at cannot be remembered "
                    + "(issue #33, AC7).");
            source.AddHook(OnHwndMessage);
        }

        private IntPtr OnHwndMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            OnWindowMessage(message);
            return IntPtr.Zero;
        }

        /// <summary>
        /// The resize-modal-loop state machine, taken apart from the HWND so a test can drive it:
        /// a loop begins, the window is (or is not) resized during it, the loop ends. A loop that
        /// resized the window is a resize somebody performed; a loop that only moved it is not.
        /// </summary>
        internal void OnWindowMessage(int message)
        {
            switch (message)
            {
                case WM_ENTERSIZEMOVE:
                    // A modal loop is starting. Until something says otherwise this is a MOVE, and a
                    // move must leave the remembered size alone - the window's current size at the
                    // end of a drag across the desktop is whatever the panel happens to be, which is
                    // exactly the "size nobody chose" this class exists to keep out of config.
                    _draggingASizingEdge = false;
                    _inAModalLoop = true;
                    _widthWhenTheLoopBegan = _window.ActualWidth;
                    _heightWhenTheLoopBegan = _window.ActualHeight;
                    _thePanelWasUpWhenTheGestureBegan = ThePanelIsUp;
                    break;

                case WM_SIZING:
                    _draggingASizingEdge = true;
                    break;

                case WM_EXITSIZEMOVE:
                    if (!_inAModalLoop) return;
                    _inAModalLoop = false;
                    bool snapped = TheLoopChangedTheWindowsSize();
                    if (!_draggingASizingEdge && !snapped) return;
                    _draggingASizingEdge = false;
                    Record(_thePanelWasUpWhenTheGestureBegan, snapped ? "a snap or edge drag" : "the sizing border");
                    break;
            }
        }

        /// <summary>
        /// Whether the window is a different size than when the modal loop began - the evidence that
        /// identifies an Aero Snap, which resizes the window through a loop that sends no WM_SIZING
        /// at all. Nothing in this app resizes the HUD while a person is dragging it, so a size that
        /// changed across the loop was changed by the shell on the person's behalf.
        /// </summary>
        private bool TheLoopChangedTheWindowsSize() =>
            Math.Abs(_window.ActualWidth - _widthWhenTheLoopBegan) > SamePixel
         || Math.Abs(_window.ActualHeight - _heightWhenTheLoopBegan) > SamePixel;

        /// <summary>
        /// The person maximised, restored, or snapped the HUD to the top of the screen. None of those
        /// runs the modal loop, which is why the loop above cannot see them and why the Review Gate
        /// found a maximised HUD coming back at its old size.
        ///
        /// This is a POSITIVE identification and not a guess about layout: nothing in AgentEyesApp
        /// assigns this window's WindowState, so a state change here came from outside the app.
        /// <c>HudUserResizeTests.NothingInTheHudEverSetsItsOwnWindowState</c> asserts that against the
        /// compiled IL, so the claim fails loudly the day it stops being true.
        ///
        /// The size is read one dispatcher turn later, at Background priority: WPF's layout runs at
        /// Render priority, which is higher, so by then the window has the size the new state gave it
        /// rather than the size it is leaving.
        /// </summary>
        public void ByWindowState()
        {
            var was = _lastWindowState;
            var now = _window.WindowState;
            _lastWindowState = now;

            // Minimised is not a size anybody chose, and neither is the size a restore puts back.
            if (now == WindowState.Minimized || was == WindowState.Minimized) return;

            bool panelWasUp = ThePanelIsUp;
            PreviewLog.Info($"hud: window state -> {now}; reading the size it settles at");
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => Record(panelWasUp, $"the {now} window command")));
        }

        /// <summary>
        /// The person dragged the preview panel's resize grip by this much. Applies the resize and
        /// records it, in that order, so the recorded size is the one the window actually took
        /// rather than the one it was asked for.
        ///
        /// Deliberately not logged per call: Thumb.DragDelta fires once per mouse move throughout a
        /// drag, and a line each would bury the recording's own log. The size that survives the drag
        /// is logged once, by SavePosition, where it is written.
        /// </summary>
        public void ByGrip(double horizontalChange, double verticalChange)
        {
            bool panelWasUp = ThePanelIsUp;
            _window.Width = Math.Max(_window.MinWidth, _window.ActualWidth + horizontalChange);
            _window.Height = Math.Max(_window.MinHeight, _window.ActualHeight + verticalChange);
            Record(panelWasUp, null);
        }

        /// <summary>
        /// UI Automation has commanded a resize through TransformPattern - an accessibility tool, or
        /// QA driving AC7. An external command through a typed API, which is as positive an
        /// identification as a border drag: no layout pass can reach it.
        /// </summary>
        public void ByAutomation(double width, double height)
        {
            bool panelWasUp = ThePanelIsUp;
            PreviewLog.Info($"hud: UI Automation resize to {width:0.##}x{height:0.##}");
            _window.Width = width;
            _window.Height = height;
            Record(panelWasUp, "UI Automation");
        }

        /// <summary>
        /// The automation peer this window must use for the <see cref="ByAutomation"/> route to
        /// exist at all. Without it UI Automation serves TransformPattern from the default HWND
        /// provider, which resizes the window behind WPF's back - indistinguishable, from inside the
        /// app, from a layout pass.
        /// </summary>
        public AutomationPeer CreatePeer() => new HudWindowAutomationPeer(_window, this);

        /// <summary>Whether the window is in the manually-sized state the preview panel puts it in.
        /// In every other state it auto-sizes to the pill, and the pill's dimensions are not a size
        /// the preview panel can be restored to.</summary>
        private bool ThePanelIsUp => _window.SizeToContent == SizeToContent.Manual;

        /// <summary>
        /// ONE derivation, in one place: whatever the gesture was, the size it produced is the size
        /// the window now has.
        ///
        /// <paramref name="thePanelWasUpWhenTheGestureBegan"/> is a NARROWING, not a gate that can
        /// let something through. It can only ever suppress a recording, never authorise one - the
        /// authorisation is the caller, and there are exactly four callers, each a gesture no layout
        /// pass can perform.
        /// </summary>
        private void Record(bool thePanelWasUpWhenTheGestureBegan, string? gesture)
        {
            if (!thePanelWasUpWhenTheGestureBegan) return;

            _memory.RecordUserResize(_window.ActualWidth, _window.ActualHeight);
            if (gesture != null)
                PreviewLog.Info($"hud: resized by the person via {gesture} to "
                       + $"{_window.ActualWidth:0.##}x{_window.ActualHeight:0.##}");
        }
    }

    /// <summary>
    /// The HUD's UI Automation peer. It exists for ONE reason: to make a TransformPattern resize
    /// arrive in the app as a resize COMMAND rather than as a size change.
    ///
    /// WPF's own <see cref="WindowAutomationPeer"/> does not implement <see cref="ITransformProvider"/>
    /// (measured: <c>typeof(ITransformProvider).IsAssignableFrom(typeof(WindowAutomationPeer))</c> is
    /// false), so UI Automation falls back to the default HWND provider and resizes the window with
    /// SetWindowPos. That produces exactly the WM_WINDOWPOSCHANGED + WM_SIZE a layout pass produces,
    /// and nothing else - so the app cannot tell an accessibility tool's deliberate resize from its
    /// own layout, and the round-2 and round-3 defects were both attempts to tell them apart after
    /// the fact. Advertising the pattern here takes the route back: UI Automation calls
    /// <see cref="ITransformProvider.Resize"/> on this object instead, and the intent arrives typed.
    ///
    /// Everything except Transform is left to the base peer, so the HUD's UI Automation surface -
    /// the window's name, its buttons, the patterns gui-smoke.ps1 drives - is unchanged.
    /// </summary>
    internal sealed class HudWindowAutomationPeer : WindowAutomationPeer, ITransformProvider
    {
        private readonly Window _window;
        private readonly HudUserResize _userResize;

        public HudWindowAutomationPeer(Window window, HudUserResize userResize) : base(window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _userResize = userResize ?? throw new ArgumentNullException(nameof(userResize));
        }

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Transform ? this : base.GetPattern(patternInterface);

        /// <summary>Matches what the default HWND provider reported before this peer existed, so no
        /// UI Automation client sees the HUD become less capable.</summary>
        public bool CanResize => _window.ResizeMode != ResizeMode.NoResize;

        public bool CanMove => true;

        public bool CanRotate => false;

        public void Move(double x, double y)
        {
            PreviewLog.Info($"hud: UI Automation move to {x:0.##},{y:0.##}");
            _window.Left = x;
            _window.Top = y;
        }

        public void Resize(double width, double height) => _userResize.ByAutomation(width, height);

        public void Rotate(double degrees) =>
            throw new InvalidOperationException("The recording HUD cannot be rotated.");
    }
}
