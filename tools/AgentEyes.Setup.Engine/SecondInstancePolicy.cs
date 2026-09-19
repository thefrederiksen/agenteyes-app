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
/// work. A modal dialog is only ever right when a person is standing in front of a window that did
/// not open. Two cases are certainly not that, and both were throwing dialogs at him:
///
///  - a launch that asked for a HIDDEN start. Nobody is waiting for a window.
///  - this code running inside SOME OTHER PROGRAM. The test suite builds the application object to
///    get at the styles in App.xaml, and that walks this refusal path inside the test runner - so
///    every test run on this machine, while his tray app was up, put a modal box on his screen.
///    There is no person waiting on a window there either; there is not even an application.
///
/// The second rule is deliberately the wider one: silence is the safe default, and it is decided by
/// asking whether this process IS the application, rather than by naming the programs it is not.
///
/// Kept as a pure function, like <see cref="UpdateRestartPolicy"/>, so the rule is unit-testable
/// without starting a second app.
/// </summary>
public static class SecondInstancePolicy
{
    /// <summary>
    /// Decide how a refused second instance should behave.
    /// </summary>
    /// <param name="args">The command line this process was started with.</param>
    /// <param name="hostProcessName">
    /// The running process's own file name without extension - <see cref="Environment.ProcessPath"/>
    /// through <see cref="Path.GetFileNameWithoutExtension(string)"/>. In the real application this
    /// is AgentEyesApp; in a test runner it is testhost or dotnet.
    /// </param>
    /// <param name="appProcessName">
    /// The application assembly's own name, which is what the running process is called when the
    /// application is the thing running. Derived by the caller, never typed as a literal.
    /// </param>
    public static SecondInstanceResponse Decide(IEnumerable<string>? args,
                                                string? hostProcessName,
                                                string? appProcessName)
    {
        // Unknown host, or a host that is not the application: this code is a guest inside somebody
        // else's process. Never put a dialog on a person's screen from there. Unknown counts as not
        // the application on purpose - the safe answer to "who is running me?" is to stay quiet.
        if (!ApplicationHost.IsTheApplication(hostProcessName, appProcessName))
        {
            EngineLog.Write($"[SecondInstancePolicy] Decide -> ExitQuietly (host '{hostProcessName}' "
                + $"is not the application '{appProcessName}')");
            return SecondInstanceResponse.ExitQuietly;
        }

        var decision = LaunchArguments.AsksForHiddenStart(args)
            ? SecondInstanceResponse.ExitQuietly
            : SecondInstanceResponse.TellThePerson;
        EngineLog.Write($"[SecondInstancePolicy] Decide -> {decision}");
        return decision;
    }
}
