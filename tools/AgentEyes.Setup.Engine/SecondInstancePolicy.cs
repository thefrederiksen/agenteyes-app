namespace AgentEyes.Setup.Engine;

/// <summary>What a refused second instance does about it (issue #61).</summary>
public enum SecondInstanceResponse
{
    /// <summary>
    /// A person launched this by hand, so say why nothing appeared and point at the tray. A dialog
    /// is the right answer here: somebody is sitting there waiting for the app to open.
    /// </summary>
    TellThePerson,

    /// <summary>
    /// The launch asked to start hidden, so nobody is waiting on a dialog and a modal box would
    /// simply land on top of whatever the person is actually doing - and block until it is clicked
    /// away. Log the refusal and exit.
    /// </summary>
    ExitQuietly,
}

/// <summary>
/// What the app does when the single-instance lock refuses it (issue #61).
///
/// The owner kept getting "AgentEyes is already running (see the system tray)" thrown on top of his
/// work by tooling that launched a second copy. A modal dialog is only ever right when a person is
/// waiting for a window; a launch that asked for a hidden start has nobody to tell.
///
/// Kept as a pure function, like <see cref="UpdateRestartPolicy"/>, so the rule is unit-testable
/// without starting a second app.
/// </summary>
public static class SecondInstancePolicy
{
    /// <summary>Decide how a refused second instance should behave, from its own command line.</summary>
    public static SecondInstanceResponse Decide(IEnumerable<string>? args)
    {
        var decision = LaunchArguments.AsksForHiddenStart(args)
            ? SecondInstanceResponse.ExitQuietly
            : SecondInstanceResponse.TellThePerson;
        EngineLog.Write($"[SecondInstancePolicy] Decide -> {decision}");
        return decision;
    }
}
