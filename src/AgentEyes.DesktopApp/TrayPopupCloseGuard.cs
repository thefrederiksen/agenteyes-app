using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentEyes.DesktopApp
{
    /// <summary>
    /// Stops Avalonia's Windows tray-menu popup from closing itself twice (issue #116).
    ///
    /// THE DEFECT (Avalonia 11.3.22, unchanged in 11.3.23 and 12.1.6). On Windows the tray menu is
    /// drawn in a popup window, Avalonia.Win32.TrayIconImpl+TrayPopupRoot, which closes itself on
    /// Deactivated. Closing it while it is the active window - after ANY item is clicked, and when it is
    /// closed with WM_CLOSE while it holds the focus - re-enters:
    ///
    ///   Close() -> CloseInternal -> WindowImpl.Dispose -> DestroyWindow -> WM_DESTROY
    ///     -> Window.HandleClosed -> HandleDeactivated -> TrayPopupRoot_Deactivated -> Close()   (again)
    ///
    /// The nested Close runs while the first is still inside DestroyWindow (WindowImpl clears its HWND
    /// only after DestroyWindow returns), so the popup is torn down a second time and its menu presenter
    /// is detached twice. The second detach throws NotSupportedException "DefaultMenuInteractionHandler
    /// is not attached to the menu" out of the message loop and the process dies. Measured on SOREN_NORTH
    /// with a Closing/Closed trace: Closing #1 from DefaultMenuInteractionHandler.CloseMenu, Closing #2
    /// from TrayPopupRoot_Deactivated under DestroyWindow, then the exception.
    ///
    /// THE FIX. A close that starts while that popup is already closing is cancelled in Closing. The first
    /// close is never touched and always completes; only the re-entrant duplicate is dropped, so the popup
    /// is torn down exactly once. Only that one Avalonia type is affected - every other window closes as
    /// before. On Linux (D-Bus menu) and macOS (native menu) the type never appears and this does nothing.
    ///
    /// If something else cancels that first close (nothing does today - the popup is Avalonia's own and
    /// only this guard subscribes to its Closing), the popup stays open; a check posted after the close
    /// sees it never reached Closed and lets the next close through again, so the menu can still be
    /// dismissed. Every re-entrant close happens synchronously inside the first one, before that check runs.
    /// </summary>
    internal static class TrayPopupCloseGuard
    {
        /// <summary>The full name of Avalonia's Windows tray popup. DesktopAppQuitTests pins that it still exists.</summary>
        internal const string TrayPopupTypeName = "Avalonia.Win32.TrayIconImpl+TrayPopupRoot";

        private static bool _installed;

        /// <summary>Install once, before the tray icon exists: every TrayPopupRoot opened afterwards is guarded.</summary>
        public static void Install()
        {
            if (_installed) throw new InvalidOperationException("TrayPopupCloseGuard.Install called twice");
            _installed = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OnWindowOpened(window));
            Log.Info($"[TrayPopupCloseGuard] Install: guarding {TrayPopupTypeName}");
        }

        /// <summary>True for Avalonia's Windows tray popup and nothing else.</summary>
        internal static bool IsTrayPopup(Type windowType) => windowType.FullName == TrayPopupTypeName;

        private static void OnWindowOpened(Window window)
        {
            if (!IsTrayPopup(window.GetType())) return;
            var state = new CloseState();
            window.Closed += (_, _) => state.MarkClosed();
            window.Closing += (_, e) =>
            {
                if (state.CancelThisClose(e.Cancel))
                {
                    e.Cancel = true;
                    Log.Info($"[TrayPopupCloseGuard] cancelled a re-entrant close of tray popup '{window.Name}' (reason={e.CloseReason}, active={window.IsActive})");
                    return;
                }
                if (e.Cancel) return;
                Log.Info($"[TrayPopupCloseGuard] tray popup '{window.Name}' closing (reason={e.CloseReason}, active={window.IsActive})");
                Dispatcher.UIThread.Post(() =>
                {
                    if (state.ReopenIfNotClosed())
                        Log.Warn($"[TrayPopupCloseGuard] the close of tray popup '{window.Name}' was cancelled elsewhere and it is still open - its next close is allowed");
                }, DispatcherPriority.Background);
            };
            Log.Info($"[TrayPopupCloseGuard] guarding tray popup '{window.Name}'");
        }

        /// <summary>The per-popup decision, separate from Avalonia so it can be unit-tested.</summary>
        internal sealed class CloseState
        {
            /// <summary>True once a close of this popup has begun.</summary>
            public bool Closing { get; private set; }

            /// <summary>
            /// Whether a close that is starting now must be cancelled. The first close goes through and marks
            /// the popup closing; every close after it is cancelled. A close someone else already cancelled
            /// (<paramref name="cancelledByOthers"/>) has not begun, so it marks nothing. Pure, unit-tested.
            /// </summary>
            public bool CancelThisClose(bool cancelledByOthers)
            {
                if (Closing) return true;
                if (!cancelledByOthers) Closing = true;
                return false;
            }

            /// <summary>True once the popup has really closed (Window.Closed).</summary>
            public bool Closed { get; private set; }

            /// <summary>Record that the popup has closed.</summary>
            public void MarkClosed() => Closed = true;

            /// <summary>
            /// Run after a close has begun and every re-entrant close it caused has returned. If the popup
            /// never reached Closed, that close was cancelled by someone else and the popup is still open:
            /// clear Closing so its next close goes through, and return true. Otherwise change nothing.
            /// </summary>
            public bool ReopenIfNotClosed()
            {
                if (!Closing || Closed) return false;
                Closing = false;
                return true;
            }
        }
    }
}
