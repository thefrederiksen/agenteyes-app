using System;

namespace AgentEyes.DevThrottle
{
    /// <summary>
    /// The app-wide, live view of DevThrottle sign-in state (issue #129).
    ///
    /// <see cref="DevThrottleAccount"/> answers "is a credential stored?" by reading disk. That is
    /// not enough for an always-visible indicator: a credential can be present but STALE, which is
    /// exactly the 2026-08-10 outage - a key minted 2026-07-07 sat on disk while every transcription
    /// failed with a 401 and the app looked healthy.
    ///
    /// This type layers the missing fact on top: a 401 seen from any DevThrottle call marks the
    /// session unauthorized until the next successful sign-in. It is the one thing the rail
    /// indicator and the Control API both read, so they can never disagree.
    ///
    /// Deliberately NOT done here: probing the key against the server on startup. That would spend
    /// a request on every launch (issue #129, "Assumptions").
    /// </summary>
    internal static class AccountState
    {
        private static readonly object Gate = new();
        private static bool _unauthorized;
        private static bool _loaded;
        private static bool _hasKey;
        private static string? _email;

        /// <summary>
        /// Raised whenever the observable state changes. Subscribers are called on whatever thread
        /// caused the change - a UI subscriber must marshal to its own dispatcher.
        /// </summary>
        public static event Action? Changed;

        /// <summary>True when a credential with a non-empty dt_ key is stored AND no 401 has been
        /// seen since the last successful sign-in.</summary>
        public static bool IsSignedIn
        {
            get { lock (Gate) { EnsureLoaded(); return _hasKey && !_unauthorized; } }
        }

        /// <summary>The signed-in account email, or null when not signed in.</summary>
        public static string? Email
        {
            get { lock (Gate) { EnsureLoaded(); return _hasKey && !_unauthorized ? _email : null; } }
        }

        /// <summary>
        /// Records that a DevThrottle call came back 401. The stored key is present but not
        /// accepted, so the app is effectively signed out until the user reconnects.
        /// </summary>
        public static void NoteUnauthorized()
        {
            bool changed;
            lock (Gate)
            {
                EnsureLoaded();
                changed = !_unauthorized;
                _unauthorized = true;
            }

            if (changed)
            {
                Log.Info("[AccountState] NoteUnauthorized: 401 seen; state is now not-signed-in");
                Raise();
            }
        }

        /// <summary>
        /// Re-reads the stored credential and clears the unauthorized flag. Call after a sign-in,
        /// a reconnect, or a sign-out completes.
        /// </summary>
        public static void Refresh()
        {
            bool wasSignedIn = IsSignedIn;
            string? wasEmail = Email;

            lock (Gate)
            {
                _unauthorized = false;
                Load();
            }

            Log.Info($"[AccountState] Refresh: signedIn={IsSignedIn}, email={Email ?? "(none)"}");
            if (wasSignedIn != IsSignedIn || wasEmail != Email) Raise();
        }

        private static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        // Caller holds Gate.
        private static void Load()
        {
            var cred = DevThrottleAccount.Load();
            _hasKey = cred?.ApiKey is { Length: > 0 };
            _email = cred?.Email;
            _loaded = true;
        }

        private static void Raise() => Changed?.Invoke();

        /// <summary>
        /// The text an indicator shows for a given state. Pure so it can be unit-tested without
        /// touching disk or the credential store.
        /// </summary>
        public static AccountDisplay Describe(bool signedIn, string? email) =>
            signedIn
                ? new AccountDisplay(
                    "Signed in",
                    string.IsNullOrWhiteSpace(email) ? "Signed in to DevThrottle" : $"Signed in as {email}",
                    $"DevThrottle account: Signed in{(string.IsNullOrWhiteSpace(email) ? "" : $" as {email}")}")
                : new AccountDisplay(
                    "Not signed in",
                    "Not signed in - transcription and AI are disabled. Click to sign in.",
                    "DevThrottle account: Not signed in");
    }

    /// <summary>Display strings for one account state: the short label, the tooltip, and the
    /// UI-automation name QA asserts against.</summary>
    internal sealed record AccountDisplay(string Label, string ToolTip, string AutomationName);
}
