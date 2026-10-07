using System;

namespace AgentEyes.App
{
    /// <summary>
    /// Issue #98: whether the recording HUD is on screen or hidden by the person, shared between the
    /// window that owns the HUD (MainWindow) and the tray that offers to bring it back. One instance
    /// for the app, created in App.
    ///
    /// The HUD is handed in as two delegates rather than as a Window so the rule - attach resets
    /// hidden, a stale detach is ignored, the tray item shows only while hidden - is testable without
    /// a running WPF Application.
    ///
    /// Hiding is for ONE recording: every Attach starts shown. Nothing is persisted, so a recording
    /// can never begin with its indicator already off the screen. UI thread only.
    /// </summary>
    internal sealed class RecordingIndicator
    {
        private object? _owner;
        private Action? _hide;
        private Action? _show;

        /// <summary>The attached HUD is hidden by the person. False when no HUD is attached.</summary>
        public bool IsHidden { get; private set; }

        /// <summary>The tray's "Show recording indicator" item is offered exactly when this is true.</summary>
        public bool CanShow => _owner != null && IsHidden;

        /// <summary>Raised after any change, so the tray can redraw its icon and tooltip at once.</summary>
        public event Action? Changed;

        /// <summary>A new HUD went on screen for a new recording. It starts shown, whatever the last
        /// recording's HUD was doing.</summary>
        public void Attach(object owner, Action hide, Action show)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(hide);
            ArgumentNullException.ThrowIfNull(show);
            Log.Info("[RecordingIndicator] Attach: a new recording indicator is on screen");
            _owner = owner;
            _hide = hide;
            _show = show;
            IsHidden = false;
            Changed?.Invoke();
        }

        /// <summary>The HUD <paramref name="owner"/> closed. Ignored when a newer HUD has already been
        /// attached, so a late Closed from the previous recording cannot clear the current one.</summary>
        public void Detach(object owner)
        {
            if (!ReferenceEquals(owner, _owner))
            {
                Log.Info("[RecordingIndicator] Detach: ignored, not the current indicator");
                return;
            }
            Log.Info($"[RecordingIndicator] Detach: indicator closed (wasHidden={IsHidden})");
            _owner = null;
            _hide = null;
            _show = null;
            IsHidden = false;
            Changed?.Invoke();
        }

        /// <summary>The HUD's Hide button. The recording is not touched.</summary>
        public void Hide()
        {
            if (_hide == null || IsHidden) return;
            Log.Info("[RecordingIndicator] Hide: the person hid the recording indicator; recording continues");
            _hide();
            IsHidden = true;
            Changed?.Invoke();
        }

        /// <summary>The tray's "Show recording indicator".</summary>
        public void Show()
        {
            if (_show == null || !IsHidden) return;
            Log.Info("[RecordingIndicator] Show: the recording indicator is back on screen");
            _show();
            IsHidden = false;
            Changed?.Invoke();
        }
    }
}
