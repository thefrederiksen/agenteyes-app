using System;

namespace AgentEyes.Input
{
    /// <summary>The keys we can use as a double-tap trigger.</summary>
    internal enum TriggerKey { Ctrl, Shift, Alt }

    /// <summary>
    /// Pure double-tap state machine for a global shortcut trigger. It is fed low-level key events
    /// (the trigger key down/up, and "some other key was pressed") and reports when a clean
    /// double-tap occurs. Kept free of any Win32 so it can be unit-tested deterministically.
    ///
    /// A "clean tap" is the trigger key going down then up with no other key pressed in between
    /// (so Ctrl+C / Ctrl+V / holding Ctrl as a modifier never count). Two clean taps whose
    /// down-edges fall within <see cref="WindowMs"/> fire the trigger.
    /// </summary>
    internal sealed class TapDetector
    {
        public const int WindowMs = 500;   // forgiving enough for a natural double-tap

        private readonly int _windowMs;
        private bool _down;             // trigger key currently held
        private bool _dirty;            // another key was pressed during this hold -> not a clean tap
        private long _downMs;           // when the current hold started
        private long _lastTapMs = long.MinValue;   // down-edge time of the previous clean tap

        public TapDetector(int windowMs = WindowMs) => _windowMs = windowMs;

        /// <summary>Raised when a clean double-tap is detected.</summary>
        public event Action? Triggered;

        /// <summary>Diagnostics (issue #35): every decision the state machine takes,
        /// e.g. "tap gap=312ms -> TRIGGER" or "tap dirty (modifier use)".</summary>
        public event Action<string>? Diagnostic;

        /// <summary>The trigger key was pressed down. <paramref name="nowMs"/> is a monotonic millisecond clock.</summary>
        public void TriggerDown(long nowMs)
        {
            if (_down) return;          // auto-repeat while held - ignore
            _down = true;
            _dirty = false;
            _downMs = nowMs;
        }

        /// <summary>The trigger key was released.</summary>
        public void TriggerUp(long nowMs)
        {
            if (!_down) return;
            _down = false;
            if (_dirty)
            {
                _dirty = false;
                _lastTapMs = long.MinValue;
                Diagnostic?.Invoke("tap dirty (other key during hold) - not a tap");
                return;
            }

            // The gap is DOWN-edge to DOWN-edge (the documented contract): how fast the
            // taps started, so a slow second release cannot eat the window (issue #35).
            if (_lastTapMs != long.MinValue)
            {
                long gap = _downMs - _lastTapMs;
                if (gap <= _windowMs)
                {
                    _lastTapMs = long.MinValue;     // consume; a third tap starts fresh
                    Diagnostic?.Invoke($"tap gap={gap}ms (window {_windowMs}ms) -> TRIGGER");
                    Triggered?.Invoke();
                    return;
                }
                Diagnostic?.Invoke($"tap gap={gap}ms exceeds window {_windowMs}ms - starting a new pair");
            }
            else
            {
                Diagnostic?.Invoke("first clean tap");
            }
            _lastTapMs = _downMs;
        }

        /// <summary>Any non-trigger key was pressed. Marks the current hold dirty and breaks a pending tap pair.</summary>
        public void OtherKey()
        {
            if (_down) _dirty = true;
            else _lastTapMs = long.MinValue;    // typing between taps breaks the double-tap
        }
    }
}
