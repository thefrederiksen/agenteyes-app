namespace AgentEyes.Setup.Engine;

/// <summary>
/// What the tray app's own command line means, in ONE place (issue #61).
///
/// Two callers need the same knowledge and used to carry their own copy of it: the app decides
/// whether it was asked to start hidden, and the auto-update restart has to hand the same arguments
/// to the exe it starts. When the restart dropped them, an always-on tray app came back with a
/// window on screen - the update had silently rewritten how the app starts.
/// </summary>
public static class LaunchArguments
{
    /// <summary>Start in the tray with no window.</summary>
    public const string Tray = "--tray";

    /// <summary>Start minimized - the older spelling of the same intent, still accepted.</summary>
    public const string Minimized = "--minimized";

    /// <summary>The flags that mean "start me with no window on screen".</summary>
    public static readonly string[] HiddenStartFlags = { Tray, Minimized };

    /// <summary>
    /// True when the command line asks for a hidden start. Matching is case-insensitive because a
    /// shortcut, a scheduled task and a script all spell it differently and all mean the same thing.
    /// </summary>
    public static bool AsksForHiddenStart(IEnumerable<string>? args)
    {
        if (args is null) return false;
        foreach (var a in args)
        {
            if (a is null) continue;
            foreach (var flag in HiddenStartFlags)
                if (string.Equals(a.Trim(), flag, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// The arguments a restart must hand to the new process, taken from an argv in the shape of
    /// <see cref="Environment.GetCommandLineArgs"/> - the app's own, or (issue #86) the running app's
    /// command line as the setup engine read it. That array's FIRST element is the executable
    /// path, not an argument - passing it on would hand the app its own path as an argument - so it
    /// is dropped here rather than at each call site. Everything after it is carried verbatim: the
    /// app understood those arguments when it was launched and must understand them again.
    /// </summary>
    public static IReadOnlyList<string> ToCarryAcrossRestart(IReadOnlyList<string>? commandLineArgs)
    {
        if (commandLineArgs is null || commandLineArgs.Count <= 1)
            return Array.Empty<string>();

        var carried = new List<string>(commandLineArgs.Count - 1);
        for (int i = 1; i < commandLineArgs.Count; i++)
        {
            var a = commandLineArgs[i];
            if (!string.IsNullOrWhiteSpace(a)) carried.Add(a);
        }
        return carried;
    }
}
