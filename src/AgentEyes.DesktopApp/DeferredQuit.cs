using System;
using Avalonia.Threading;

namespace AgentEyes.DesktopApp
{
    /// <summary>
    /// The tray menu's Quit, run AFTER the menu has finished with the click (issue #116).
    ///
    /// WHY IT IS DEFERRED. On Windows the tray menu is not a native menu: Avalonia draws it in its own
    /// popup window (Avalonia.Win32.TrayIconImpl+TrayPopupRoot), which is an ordinary Window in the
    /// application lifetime's window list and which closes itself when it is deactivated. Shutting
    /// down synchronously inside the item's Click closed that popup from inside its own click handling,
    /// while it was still the active window: DestroyWindow deactivated it, its Deactivated handler
    /// closed it again re-entrantly (Win32 WindowImpl.Dispose clears its HWND only after DestroyWindow
    /// returns), the popup was torn down twice, and the second teardown threw
    /// NotSupportedException "DefaultMenuInteractionHandler is not attached to the menu" out of the
    /// message loop - so Program.Main's finally never ran (3 of 3 in Windows QA). Posting the shutdown
    /// lets the menu close its popup itself, once, as it does for every other item; by the time the
    /// shutdown runs the popup is gone from the window list. On Linux the menu is exported over D-Bus
    /// and there is no popup window, so deferring changes only when the shutdown runs, not what it does.
    ///
    /// The tray takes this type, not an Action, so its Quit item cannot be wired straight to a
    /// synchronous shutdown again. DesktopAppQuitTests pins both halves.
    /// </summary>
    internal sealed class DeferredQuit
    {
        private readonly Action<Action> _post;
        private readonly Action _shutdown;

        /// <summary>The production quit: <paramref name="shutdown"/> runs on the UI thread at Background priority,
        /// after the input that requested it (the menu click and the popup's own close) has been handled.</summary>
        public DeferredQuit(Action shutdown)
            : this(work => Dispatcher.UIThread.Post(work, DispatcherPriority.Background), shutdown)
        {
        }

        /// <summary>For tests: <paramref name="post"/> stands in for the dispatcher.</summary>
        internal DeferredQuit(Action<Action> post, Action shutdown)
        {
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        }

        /// <summary>True once Quit has been asked for; the shutdown itself may not have run yet.</summary>
        public bool IsRequested { get; private set; }

        /// <summary>Ask to quit. Returns at once - the shutdown is posted, never run inside the caller.
        /// A second request while one is pending is logged and ignored: one shutdown, not two.</summary>
        public void Request()
        {
            if (IsRequested)
            {
                Log.Info("[DeferredQuit] Request: already requested - ignored");
                return;
            }
            IsRequested = true;
            Log.Info("[DeferredQuit] Request: shutdown posted to run after the menu has closed");
            _post(Run);
        }

        private void Run()
        {
            Log.Info("[DeferredQuit] Run: shutting down");
            _shutdown();
            Log.Info("[DeferredQuit] Run: shutdown returned");
        }
    }
}
