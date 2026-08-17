using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.Input;

namespace AgentEyes.App
{
    /// <summary>
    /// A global shortcut trigger, parsed from its config string (issue #36).
    /// Two kinds:
    ///   "doubletap:ctrl|shift|alt"  - the classic double-tap presets
    ///   "hotkey:ctrl+alt+d"         - a custom combination (modifiers + one key)
    /// Centralizes parsing, validation and the human-readable descriptions the
    /// Capture view renders.
    /// </summary>
    internal sealed class TriggerSpec
    {
        public bool IsDoubleTap { get; private init; }
        public TriggerKey TapKey { get; private init; }          // doubletap only

        public bool Ctrl { get; private init; }                  // hotkey only
        public bool Alt { get; private init; }
        public bool Shift { get; private init; }
        public bool Win { get; private init; }
        public int MainVk { get; private init; }                 // virtual-key code
        public string MainKeyName { get; private init; } = "";   // display name (e.g. "Space", "F9", "D")

        public static TriggerSpec Parse(string trigger)
        {
            string t = (trigger ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("hotkey:"))
            {
                var spec = TryParseHotkey(t["hotkey:".Length..], out string? error);
                if (spec == null) throw new FormatException($"invalid trigger '{trigger}': {error}");
                return spec;
            }
            // Default family: doubletap:<modifier> (also the fallback for legacy values).
            TriggerKey key = t.Contains("shift") ? TriggerKey.Shift
                : t.Contains("alt") ? TriggerKey.Alt
                : TriggerKey.Ctrl;
            return new TriggerSpec { IsDoubleTap = true, TapKey = key };
        }

        /// <summary>Parse "ctrl+alt+d" style. Returns null with a reason on invalid input.</summary>
        public static TriggerSpec? TryParseHotkey(string combo, out string? error)
        {
            error = null;
            bool ctrl = false, alt = false, shift = false, win = false;
            int vk = 0;
            string name = "";
            foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl" or "control": ctrl = true; break;
                    case "alt": alt = true; break;
                    case "shift": shift = true; break;
                    case "win" or "windows": win = true; break;
                    default:
                        if (vk != 0) { error = "more than one non-modifier key"; return null; }
                        if (!Enum.TryParse<System.Windows.Forms.Keys>(raw, true, out var parsed))
                        {
                            error = $"unknown key '{raw}'";
                            return null;
                        }
                        vk = (int)parsed;
                        name = KeyDisplayName(parsed);
                        break;
                }
            }
            if (vk == 0) { error = "no main key (modifiers alone cannot be a hotkey - use a double-tap preset for that)"; return null; }

            var spec = new TriggerSpec
            {
                IsDoubleTap = false,
                Ctrl = ctrl, Alt = alt, Shift = shift, Win = win,
                MainVk = vk, MainKeyName = name,
            };
            if (!spec.IsGloballyUsable(out error)) return null;
            return spec;
        }

        /// <summary>Combinations that would fire constantly while typing are rejected
        /// with the reason (no silent acceptance - issue #36).</summary>
        public bool IsGloballyUsable(out string? reason)
        {
            reason = null;
            if (IsDoubleTap) return true;
            bool hasModifier = Ctrl || Alt || Shift || Win;
            // Keys that work alone: F-keys, and non-typing keys (PrintScreen, Pause, ...) that
            // insert no character so they cannot fire "while you type". Everything else (letters,
            // digits, Space) inserts a character and needs a modifier.
            bool isFunctionKey = MainVk >= 0x70 && MainVk <= 0x87;   // F1..F24
            bool standalone = isFunctionKey || IsStandaloneKey(MainVk);
            if (!hasModifier && !standalone)
            {
                reason = $"'{MainKeyName}' alone would trigger every time you type it. "
                    + "Add a modifier (Ctrl/Alt/Shift/Win) or use a function key.";
                return false;
            }
            // Shift alone as the only modifier on a character key = still types the character.
            if (!Ctrl && !Alt && !Win && Shift && !standalone)
            {
                reason = $"Shift+{MainKeyName} is just how '{MainKeyName}' is typed in caps. "
                    + "Use Ctrl or Alt in the combination.";
                return false;
            }
            return true;
        }

        /// <summary>Non-typing keys that are safe as a standalone global hotkey: they emit no
        /// character, so they will not fire while the user types. PrintScreen is the Capture
        /// default (issue #64).</summary>
        private static bool IsStandaloneKey(int vk) => vk switch
        {
            0x2C => true,   // VK_SNAPSHOT (PrintScreen)
            0x13 => true,   // VK_PAUSE
            0x91 => true,   // VK_SCROLL (Scroll Lock)
            _ => false,
        };

        /// <summary>Back to the config string.</summary>
        public string Serialize()
        {
            if (IsDoubleTap) return "doubletap:" + TapKey.ToString().ToLowerInvariant();
            var parts = new List<string>();
            if (Ctrl) parts.Add("ctrl");
            if (Alt) parts.Add("alt");
            if (Shift) parts.Add("shift");
            if (Win) parts.Add("win");
            parts.Add(((System.Windows.Forms.Keys)MainVk).ToString().ToLowerInvariant());
            return "hotkey:" + string.Join("+", parts);
        }

        /// <summary>Short label, e.g. "Ctrl+Space" or "double-tap Ctrl".</summary>
        public string Label()
        {
            if (IsDoubleTap) return "double-tap " + TapKey;
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(MainKeyName);
            return string.Join("+", parts);
        }

        /// <summary>Instruction sentence opener: "Double-tap Ctrl" / "Press Ctrl+Space".</summary>
        public string Instruction() => IsDoubleTap ? "Double-tap " + TapKey : "Press " + Label();

        private static string KeyDisplayName(System.Windows.Forms.Keys key) => key switch
        {
            System.Windows.Forms.Keys.Space => "Space",
            System.Windows.Forms.Keys.Oemtilde => "`",
            System.Windows.Forms.Keys.OemMinus => "-",
            System.Windows.Forms.Keys.Oemplus => "=",
            _ when key >= System.Windows.Forms.Keys.D0 && key <= System.Windows.Forms.Keys.D9
                => ((char)('0' + (key - System.Windows.Forms.Keys.D0))).ToString(),
            _ => key.ToString(),
        };
    }
}
