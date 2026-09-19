namespace AgentEyes.Setup.Engine;

/// <summary>
/// Whether the AgentEyes application object is actually the application running, or is being built
/// inside some other program (issue #61).
///
/// This is not a hypothetical. The test suite constructs the WPF application object to reach the
/// brushes and styles in App.xaml, because the preset editor's markup cannot be parsed without
/// them. WPF runs the application's startup when it does - so the whole product started up inside
/// the test runner: the single-instance lock, the tray icon, the control interface on the live
/// port, the repair and housekeeping timers, the update checker, all against the person's real
/// configuration and real recordings. When the person's own copy was already running it also threw
/// a modal "AgentEyes is already running" dialog onto their screen, every time the suite ran.
///
/// The question is answered by asking whether this PROCESS is the application, never by naming the
/// programs it is not - a list of those would be out of date the first time somebody used a
/// different test runner. An unknown host counts as "not the application", because the safe answer
/// to "who is running me?" is to do nothing.
/// </summary>
public static class ApplicationHost
{
    /// <summary>
    /// True only when the running process IS the application.
    /// </summary>
    /// <param name="hostProcessName">
    /// The running process's file name without its extension - <c>Environment.ProcessPath</c>
    /// through <c>Path.GetFileNameWithoutExtension</c>. AgentEyesApp when the application is what
    /// is running; testhost, dotnet or similar when it is not.
    /// </param>
    /// <param name="appProcessName">
    /// The application assembly's own name, which is what the process is called when the
    /// application is the thing running. Derived by the caller, never typed as a literal - deriving
    /// it is what kept the installer's identical defect (#95) from coming back.
    /// </param>
    public static bool IsTheApplication(string? hostProcessName, string? appProcessName)
    {
        if (string.IsNullOrWhiteSpace(hostProcessName)) return false;
        if (string.IsNullOrWhiteSpace(appProcessName)) return false;
        return string.Equals(hostProcessName, appProcessName, StringComparison.OrdinalIgnoreCase);
    }
}
