using System;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace AgentEyes.App
{
    /// <summary>
    /// Confirms, after the fact, that keystroke injection actually landed. Some
    /// remote-desktop software (TightVNC, issue #46) corrupts synthesized Unicode for
    /// the relayed foreground window - a leading chunk arrives, the rest turns to
    /// repeats or dots. The corruption is a timing race that cannot be predicted, so
    /// rather than guess up front we READ THE TARGET BACK and check our text is there.
    ///
    /// Reads via UI Automation off the focused element (ValuePattern, then TextPattern).
    /// Targets that expose neither cannot be confirmed - the caller then behaves exactly
    /// as before, so verification is a safety net that never makes injection worse.
    /// </summary>
    internal static class InjectionVerifier
    {
        /// <summary>
        /// Poll the focused control until it contains <paramref name="expected"/> or the
        /// timeout elapses. Returns whether the control's text could be read at all and
        /// the last content seen. A true CouldRead with content NOT containing the text
        /// means injection was corrupted.
        /// </summary>
        public static async Task<(bool CouldRead, string Content)> ConfirmAsync(string expected, int timeoutMs)
        {
            bool everRead = false;
            string last = "";
            for (int elapsed = 0; elapsed <= timeoutMs; elapsed += PollMs)
            {
                var (ok, content) = ReadFocused();
                if (ok)
                {
                    everRead = true;
                    last = content;
                    if (content.Contains(expected, StringComparison.Ordinal)) return (true, content);
                }
                await Task.Delay(PollMs);
            }
            return (everRead, last);
        }

        private const int PollMs = 80;

        private static (bool Ok, string Content) ReadFocused()
        {
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el == null) return (false, "");
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    return (true, ((ValuePattern)vp).Current.Value ?? "");
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                    return (true, ((TextPattern)tp).DocumentRange.GetText(8000) ?? "");
                return (false, "");
            }
            catch { return (false, ""); }
        }

        /// <summary>
        /// Does <paramref name="content"/> read like the issue #46 interception garble of
        /// <paramref name="expected"/> - a genuine leading fragment of the text followed by
        /// corruption - rather than a plain "nothing was typed" failure? Used by the QA
        /// gate to mark the host environment, not the product, when injection is mangled.
        /// </summary>
        public static bool LooksLikeInterceptionGarble(string content, string expected)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(expected)) return false;
            if (content.Contains(expected, StringComparison.Ordinal)) return false; // it landed
            return CommonPrefixLength(content, expected) >= 5;                       // real chunk, then diverged
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }
    }
}
