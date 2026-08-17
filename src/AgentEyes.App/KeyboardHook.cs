using System;
using System.Runtime.InteropServices;
using AgentEyes.Input;

namespace AgentEyes.App
{
    /// <summary>
    /// Global low-level keyboard hook (WH_KEYBOARD_LL) for the capture triggers.
    /// Two trigger kinds (issue #36): the classic double-tap of a modifier, or a
    /// custom hotkey (modifiers + one key). The hook callback runs on the
    /// installing thread (the WPF UI thread); subscribers dispatch real work
    /// asynchronously - the callback itself must stay fast (issue #35).
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

        private readonly LowLevelKeyboardProc _proc;   // kept alive to prevent GC of the delegate
        private readonly TapDetector? _detector;       // doubletap mode
        private readonly TriggerSpec _spec;
        private bool _hotkeyHeld;                      // hotkey mode: suppress auto-repeat
        private IntPtr _hook = IntPtr.Zero;

        public event Action? Activated;

        public KeyboardHook(TriggerSpec spec, int windowMs = TapDetector.WindowMs)
        {
            _spec = spec;
            _proc = HookProc;
            if (spec.IsDoubleTap)
            {
                _detector = new TapDetector(windowMs);
                _detector.Triggered += () => Activated?.Invoke();
                _detector.Diagnostic += msg => Log.Info("trigger: " + msg);
            }
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException("could not install the keyboard hook (error " + Marshal.GetLastWin32Error() + ")");
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam);

                if (_spec.IsDoubleTap) HandleDoubleTap(msg, vk);
                else HandleHotkey(msg, vk);
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ---- doubletap mode -------------------------------------------------

        private void HandleDoubleTap(int msg, int vk)
        {
            long now = Environment.TickCount64;
            bool isTrigger = IsTapKey(vk);
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (isTrigger) _detector!.TriggerDown(now);
                else _detector!.OtherKey();
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                if (isTrigger) _detector!.TriggerUp(now);
            }
        }

        private bool IsTapKey(int vk) => _spec.TapKey switch
        {
            TriggerKey.Ctrl => vk is 0x11 or 0xA2 or 0xA3,    // VK_CONTROL / L / R
            TriggerKey.Shift => vk is 0x10 or 0xA0 or 0xA1,   // VK_SHIFT / L / R
            TriggerKey.Alt => vk is 0x12 or 0xA4 or 0xA5,     // VK_MENU / L / R
            _ => false,
        };

        // ---- hotkey mode ----------------------------------------------------

        private void HandleHotkey(int msg, int vk)
        {
            if (vk != _spec.MainVk)
            {
                return;   // modifier state is read live below; other keys are irrelevant
            }
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (_hotkeyHeld) return;   // auto-repeat while held
                _hotkeyHeld = true;
                if (ModifiersMatch())
                {
                    Log.Info($"trigger: hotkey {_spec.Label()} -> TRIGGER");
                    Activated?.Invoke();
                }
                else
                {
                    Log.Info($"trigger: {_spec.MainKeyName} down but modifiers do not match {_spec.Label()}");
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                _hotkeyHeld = false;
            }
        }

        /// <summary>Exact modifier match: required ones down, others up - so
        /// Ctrl+Space does not also fire on Ctrl+Shift+Space. GetAsyncKeyState, not
        /// GetKeyState: inside a WH_KEYBOARD_LL callback the thread queue has not
        /// processed the modifier events yet, so the synchronous state is stale.</summary>
        private bool ModifiersMatch()
        {
            static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
            return Down(0x11) == _spec.Ctrl       // VK_CONTROL
                && Down(0x12) == _spec.Alt        // VK_MENU
                && Down(0x10) == _spec.Shift      // VK_SHIFT
                && (Down(0x5B) || Down(0x5C)) == _spec.Win;   // VK_LWIN / VK_RWIN
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }

        // ---- interop ------------------------------------------------------
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int nVirtKey);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
