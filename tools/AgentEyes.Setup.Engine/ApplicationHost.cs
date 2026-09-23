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
/// The question is answered by asking which assembly's entry point started this process - NOT what
/// the file on disk happens to be called. An earlier attempt compared the process's FILE NAME with
/// the application's name, and that was wrong in a way that mattered: the release is published as
/// AgentEyesApp-win-x64.exe, so anybody who downloaded that file and ran it got an application that
/// started and did nothing at all. A person may also rename the executable, or a browser may save
/// it as "AgentEyesApp (1).exe". None of that changes which program is running.
///
/// An unknown entry assembly counts as "not the application", because the safe answer to "who is
/// running me?" is to do nothing.
/// </summary>
public static class ApplicationHost
{
    /// <summary>
    /// True only when the running process IS the application.
    /// </summary>
    /// <param name="entryAssemblyName">
    /// The name of the assembly whose entry point started this process -
    /// <c>Assembly.GetEntryAssembly()?.GetName().Name</c>. This is the application's assembly name
    /// whatever the executable file is called, and it is the test runner's name when a test runner
    /// is what is running. Null when the platform will not say, which counts as not the application.
    /// </param>
    /// <param name="appAssemblyName">
    /// The application assembly's own name. Derived by the caller from the application type, never
    /// typed as a literal - deriving it is what kept the installer's identical defect (#95) from
    /// coming back.
    /// </param>
    public static bool IsTheApplication(string? entryAssemblyName, string? appAssemblyName)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyName)) return false;
        if (string.IsNullOrWhiteSpace(appAssemblyName)) return false;
        return string.Equals(entryAssemblyName, appAssemblyName, StringComparison.OrdinalIgnoreCase);
    }
}
