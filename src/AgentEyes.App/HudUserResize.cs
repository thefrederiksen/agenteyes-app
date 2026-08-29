using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using AgentEyes;

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
    /// POSITIVELY IDENTIFIED, and there are exactly three ways that can happen:
    ///
    ///  1. <b>The Win32 resize-modal loop</b> - the person drags the window's sizing border.
    ///     <c>WM_SIZING</c> is sent ONLY while a sizing edge is being dragged; it is not a
    ///     notification that the window's size changed (that is <c>WM_SIZE</c>), it is a
    ///     notification that a human is dragging one. A layout pass, a DPI change, a restore, a
    ///     construction and a <c>Width =</c> assignment all produce <c>WM_WINDOWPOSCHANGED</c> +
    ///     <c>WM_SIZE</c> and NEVER <c>WM_SIZING</c>. Measured, not assumed - see the ordering in
    ///     the handoff for issue #33 round 4.
    ///  2. <b>The panel's resize grip</b> - <c>Thumb.DragDelta</c>, a mouse gesture on a control.
    ///     The HUD is chromeless, so this is the affordance most people will actually find.
    ///  3. <b>UI Automation's TransformPattern</b> - an accessibility tool, or QA, commanding a
    ///     resize through a typed API. It arrives at <see cref="ByAutomation"/> only because
    ///     <see cref="HudWindowAutomationPeer"/> advertises the pattern; layout cannot call it.
    ///
    /// There is no fourth way, because there is no subscription to <c>SizeChanged</c> or
    /// <c>LayoutUpdated</c> anywhere in the HUD any more. A panel added to this window next year
    /// cannot reintroduce the defect by resizing the window during its own layout, because resizing
    /// the window is not what records a size - a gesture is, and it has to come through here.
    /// <c>HudUserResizeTests</c> holds that shut against the compiled IL.
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

        private readonly Window _window;
        private readonly HudSizeMemory _memory;

        /// <summary>Whether the modal loop currently running is a RESIZE. False until a WM_SIZING
        /// arrives, so a plain window move - which runs the same modal loop and ends with the same
        /// WM_EXITSIZEMOVE - records nothing.</summary>
        private bool _draggingASizingEdge;

        /// <summary>Whether the preview panel was up when the modal loop started. Read at the START
        /// of the gesture on purpose: dragging the PILL's border resizes the HUD, and Windows
        /// switches the window out of auto-sizing when it does, so by the end of the drag the window
        /// looks exactly like a panel that was open all along. The pill's dimensions are not the
        /// preview panel's size, and this is the only moment at which the two can still be told
        /// apart.</summary>
        private bool _thePanelWasUpWhenTheGestureBegan;

        public HudUserResize(Window window, HudSizeMemory memory)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        /// <summary>
        /// Start listening for the window's resize-modal loop. Safe to call before the window has an
        /// HWND (the HUD calls this from its constructor) or after it has one.
        /// </summary>
        public void Watch()
        {
            Log.Info("hud: watching for user resizes (sizing border, grip, UI Automation)");
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
        /// a loop begins, a sizing edge is (or is not) dragged, the loop ends. Only the combination
        /// "a loop that dragged a sizing edge, now finished" is a resize somebody performed.
        /// </summary>
        internal void OnWindowMessage(int message)
        {
            switch (message)
            {
                case WM_ENTERSIZEMOVE:
                    // A modal loop is starting. Until a WM_SIZING says otherwise this is a MOVE, and
                    // a move must leave the remembered size alone - the window's current size at the
                    // end of a drag across the desktop is whatever the panel happens to be, which is
                    // exactly the "size nobody chose" this class exists to keep out of config.
                    _draggingASizingEdge = false;
                    _thePanelWasUpWhenTheGestureBegan = ThePanelIsUp;
                    break;

                case WM_SIZING:
                    _draggingASizingEdge = true;
                    break;

                case WM_EXITSIZEMOVE:
                    if (!_draggingASizingEdge) return;
                    _draggingASizingEdge = false;
                    Record(_thePanelWasUpWhenTheGestureBegan, "the sizing border");
                    break;
            }
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
            Log.Info($"hud: UI Automation resize to {width:0.##}x{height:0.##}");
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
        /// authorisation is the caller, and there are exactly three callers, each a gesture no
        /// layout pass can perform.
        /// </summary>
        private void Record(bool thePanelWasUpWhenTheGestureBegan, string? gesture)
        {
            if (!thePanelWasUpWhenTheGestureBegan) return;

            _memory.RecordUserResize(_window.ActualWidth, _window.ActualHeight);
            if (gesture != null)
                Log.Info($"hud: resized by the person via {gesture} to "
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
            Log.Info($"hud: UI Automation move to {x:0.##},{y:0.##}");
            _window.Left = x;
            _window.Top = y;
        }

        public void Resize(double width, double height) => _userResize.ByAutomation(width, height);

        public void Rotate(double degrees) =>
            throw new InvalidOperationException("The recording HUD cannot be rotated.");
    }
}
