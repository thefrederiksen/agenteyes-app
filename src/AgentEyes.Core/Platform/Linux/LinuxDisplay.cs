using System;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// The X display AgentEyes records (issue #102). It is the session's own DISPLAY - under the
    /// systemd user service that is the value GNOME imports into the user manager at login. There is
    /// no guessed default: a process with no DISPLAY has no screen to record and says so.
    /// </summary>
    internal static class LinuxDisplay
    {
        /// <summary>
        /// The refusal for a Wayland session with no X display (issue #221). It names NO remedy, on
        /// purpose. It used to tell the user to log in with an X11 session by name, and that session is
        /// not installed by a default desktop install of Ubuntu 25.10 or 26.04 (measured at the package
        /// level, docs/cencon/proof/issue-221/), so the only instruction a blocked user got was one they
        /// could not follow. A session the code has not verified is
        /// present is never named here; README.md "Supported Linux sessions" is the supported-session
        /// statement this points at.
        /// </summary>
        internal const string WaylandRefusal =
            "this is a Wayland session with no X display. AgentEyes on Linux records an X11 desktop "
            + "session only; Wayland sessions are not supported. See 'Supported Linux sessions' in the "
            + "AgentEyes README.";

        internal const string NoDisplayRefusal =
            "DISPLAY is not set, so there is no X screen to record. Run AgentEyes inside the "
            + "desktop session; for the service, run 'systemctl --user import-environment DISPLAY XAUTHORITY' "
            + "and restart it.";

        public static string Require() =>
            Resolve(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        /// <summary>The display to record from the session's DISPLAY and WAYLAND_DISPLAY values, or the
        /// refusal for a session that has no X display. Split from <see cref="Require"/> so the refusal
        /// is tested without changing this process's environment.</summary>
        internal static string Resolve(string? display, string? waylandDisplay)
        {
            if (string.IsNullOrWhiteSpace(display))
            {
                if (!string.IsNullOrWhiteSpace(waylandDisplay))
                    throw new UsageException(WaylandRefusal);
                throw new UsageException(NoDisplayRefusal);
            }
            return display.Trim();
        }

        /// <summary>The x11grab input name for a region's top-left corner: "DISPLAY+X,Y".</summary>
        public static string GrabInput(string display, int x, int y)
        {
            if (string.IsNullOrWhiteSpace(display)) throw new ArgumentException("display is required", nameof(display));
            return $"{display}+{x},{y}";
        }
    }
}
