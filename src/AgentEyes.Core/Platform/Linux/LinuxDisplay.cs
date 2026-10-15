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
        public static string Require()
        {
            string? display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrWhiteSpace(display))
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    throw new UsageException(
                        "this is a Wayland session with no X display. AgentEyes on Linux records an X11 "
                        + "session only - log in with 'Ubuntu on Xorg'.");
                throw new UsageException(
                    "DISPLAY is not set, so there is no X screen to record. Run AgentEyes inside the "
                    + "desktop session; for the service, run 'systemctl --user import-environment DISPLAY XAUTHORITY' "
                    + "and restart it.");
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
