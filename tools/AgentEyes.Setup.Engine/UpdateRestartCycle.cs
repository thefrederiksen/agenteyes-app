namespace AgentEyes.Setup.Engine;

/// <summary>The AgentEyes process an update found running: its pid, the exe it runs, and the
/// arguments it was started with (without the exe path), so it can be started again the same way.</summary>
public sealed record RunningAppInstance(int Pid, string ExePath, IReadOnlyList<string> Arguments)
{
    /// <summary>The arguments as one line for the log and the console: "(none)" when there are none.</summary>
    public string ArgumentsText => Arguments.Count == 0 ? "(none)" : string.Join(" ", Arguments);
}

/// <summary>
/// The running AgentEyes as the update sees it (issue #86): find it, stop it. The real one is
/// <see cref="RunningAppHandle"/>; tests substitute one that reports a made-up instance and answers
/// the stop as they choose, so the whole prepare -> stop -> swap -> relaunch decision runs without an app.
/// </summary>
public interface IRunningAppHandle
{
    /// <summary>The running instance, or null when AgentEyes is not running.</summary>
    RunningAppInstance? Find();

    /// <summary>Bounded graceful-then-force stop, CONFIRMED by a final re-query. True when no instance
    /// remains; false when the app is still running after the bound - the caller must then not
    /// replace anything.</summary>
    Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct);
}

/// <summary>Starts the app exe. The real one is <see cref="ProcessAppLauncher"/>; tests record the call.</summary>
public interface IAppLauncher
{
    /// <summary>Start <paramref name="exePath"/> with <paramref name="arguments"/> and return the new pid.</summary>
    int Launch(string exePath, IReadOnlyList<string> arguments);
}

/// <summary>What the cycle did about the running app, for the console line and the log.</summary>
/// <param name="Stopped">The instance that was running and was stopped, or null.</param>
/// <param name="NewPid">The pid the app was started again as, or null.</param>
/// <param name="StartedExe">The exe that was started - always the INSTALLED app (issue #86 review, N2).</param>
public sealed record AppRestartReport(RunningAppInstance? Stopped, int? NewPid, string? StartedExe = null)
{
    /// <summary>True when an instance was running, was stopped, and was started again.</summary>
    public bool Restarted => Stopped != null && NewPid.HasValue;

    /// <summary>True when the stopped process was NOT running from the installed exe (a build started
    /// from a bin folder, say): the installed build was started in its place, and this is why the
    /// report and the app's /health could disagree if that other build is started again.</summary>
    public bool StoppedExeDiffers =>
        Stopped != null && StartedExe != null
        && !string.Equals(Path.GetFullPath(Stopped.ExePath), Path.GetFullPath(StartedExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>The one line the update prints: the pids when the app was restarted, otherwise that
    /// it was not running and was not started.</summary>
    public string Describe() => Restarted
        ? $"restarted the running app (pid {Stopped!.Pid} -> pid {NewPid})"
        : "AgentEyes was not running - it was not started";

    /// <summary>A second line when the stopped process ran from somewhere other than the installed
    /// exe; null otherwise.</summary>
    public string? Note => StoppedExeDiffers
        ? $"note: the stopped process (pid {Stopped!.Pid}) was running from {Stopped.ExePath}, not from the installed "
          + $"{StartedExe}; the installed build was started. If that other build is started again it will not report "
          + "this update's version."
        : null;
}

/// <summary>
/// The running app could not be stopped within the bound. Thrown BEFORE any file is replaced, so the
/// installed version is intact; the message says what to do.
/// </summary>
public sealed class AppStopFailedException : Exception
{
    public AppStopFailedException(RunningAppInstance instance)
        : base($"AgentEyes (pid {instance.Pid}) is running and could not be stopped. Nothing was replaced - "
               + "the installed version is unchanged. Quit it (tray icon -> Quit) and run the update again.")
    {
        Instance = instance;
    }

    public RunningAppInstance Instance { get; }
}

/// <summary>
/// The app this cycle stopped could not be started again (issue #86 review, N4). The files on disk are
/// whatever the swap step left - the message says which exe to start by hand.
/// </summary>
public sealed class AppRelaunchFailedException : Exception
{
    public AppRelaunchFailedException(string exePath, Exception inner)
        : base($"AgentEyes was stopped for the update but could not be started again from {exePath} ({inner.Message}). "
               + "Start it by hand (Start Menu -> AgentEyes).", inner)
    {
        ExePath = exePath;
    }

    public string ExePath { get; }
}

/// <summary>
/// The result of one cycle: what the swap step returned, and what happened to the app.
/// </summary>
public sealed record UpdateRestartOutcome<T>(T Result, AppRestartReport Restart);

/// <summary>
/// The ONE find -> download+verify -> stop -> swap -> relaunch decision every update path takes (issue
/// #86): the setup CLI's update and install, the wizard, and the app's own AutoUpdate (which hands
/// over to the CLI so that it too goes through here). Before this, `agenteyes-setup update` replaced
/// the files and left the running process on the old build until somebody happened to restart it.
///
/// The order is the point (revised after the review of PR #92):
///  1. find the running app (an unreadable command line fails HERE, before anything is downloaded).
///  2. PREPARE: download and verify every component. A failure here leaves the app running and no
///     installed file touched - the exception propagates and nothing else happens.
///  3. if the app is running, STOP it and confirm it is gone. When it cannot be stopped the cycle
///     throws <see cref="AppStopFailedException"/> HERE, before the swap step, so the installed files
///     are byte-for-byte what they were - no half state.
///  4. SWAP the prepared files in (the caller's step is all-or-nothing, see <see cref="UpdateRunner.Swap"/>).
///  5. if the app was running, START THE INSTALLED APP with the arguments the stopped one was running
///     with (a `--tray` app comes back as a `--tray` app, issue #61) - whatever the swap step returned,
///     and even when it threw: an app this cycle stopped is never left stopped. When the swap threw AND
///     the relaunch fails, both are reported (an <see cref="AggregateException"/>).
/// When the app was not running, it is not started.
/// </summary>
public sealed class UpdateRestartCycle
{
    private readonly IRunningAppHandle _app;
    private readonly IAppLauncher _launcher;
    private readonly string _installedAppExe;

    /// <param name="installedAppExe">The installed app exe (<c>layout.PathFor(ComponentRegistry.App)</c>):
    /// the one that is started again, whatever exe the stopped process ran from (issue #86 review, N2).</param>
    public UpdateRestartCycle(IRunningAppHandle app, IAppLauncher launcher, string installedAppExe)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        if (string.IsNullOrWhiteSpace(installedAppExe)) throw new ArgumentException("installedAppExe must not be empty.", nameof(installedAppExe));
        _installedAppExe = installedAppExe;
    }

    /// <summary>
    /// Run <paramref name="prepare"/> (download + verify) with the app still running, then stop the app,
    /// run <paramref name="swap"/> on what was prepared, and start the installed app again when one was
    /// running. A prepared value that is <see cref="IDisposable"/> is disposed when the cycle is over,
    /// whichever way it ended. Throws <see cref="AppStopFailedException"/> without calling
    /// <paramref name="swap"/> when the app cannot be stopped.
    /// </summary>
    public async Task<UpdateRestartOutcome<T>> RunAsync<TPrepared, T>(
        Func<CancellationToken, Task<TPrepared>> prepare,
        Func<TPrepared, CancellationToken, Task<T>> swap,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(swap);
        EngineLog.Write("[UpdateRestartCycle] RunAsync: looking for a running AgentEyes");

        var running = _app.Find();
        EngineLog.Write(running == null
            ? "[UpdateRestartCycle] RunAsync: AgentEyes is not running"
            : $"[UpdateRestartCycle] RunAsync: AgentEyes is running (pid {running.Pid}, {running.ExePath}, arguments: {running.ArgumentsText}) "
              + "- it keeps running while the update is downloaded and verified");

        EngineLog.Write("[UpdateRestartCycle] RunAsync: preparing (download + verify) before anything is stopped or replaced");
        TPrepared prepared = await prepare(ct);
        try
        {
            if (running == null)
            {
                EngineLog.Write("[UpdateRestartCycle] RunAsync: prepared; replacing without a restart");
                var only = await swap(prepared, ct);
                return new UpdateRestartOutcome<T>(only, new AppRestartReport(null, null));
            }

            EngineLog.Write($"[UpdateRestartCycle] RunAsync: prepared; stopping pid {running.Pid} before anything is replaced");
            bool stopped = await _app.StopAsync(running, ct);
            if (!stopped)
            {
                EngineLog.Write($"[UpdateRestartCycle] RunAsync FAILED: pid {running.Pid} could not be stopped; nothing was replaced");
                throw new AppStopFailedException(running);
            }
            EngineLog.Write($"[UpdateRestartCycle] RunAsync: pid {running.Pid} is gone - replacing");

            T result;
            try
            {
                result = await swap(prepared, ct);
            }
            catch (Exception swapEx)
            {
                // The swap step failed with the app stopped. Start the installed app again - whatever is
                // on disk now is what the person has - and let the failure propagate with the relaunch
                // on record. A relaunch that fails too must not mask the swap failure: both travel.
                EngineLog.Write($"[UpdateRestartCycle] RunAsync: the swap step FAILED ({swapEx.Message}); starting the app again anyway");
                try
                {
                    Relaunch(running);
                }
                catch (AppRelaunchFailedException relaunchEx)
                {
                    throw new AggregateException(
                        $"the update failed twice over: {swapEx.Message} AND {relaunchEx.Message}", swapEx, relaunchEx);
                }
                throw;
            }

            int newPid = Relaunch(running);
            var report = new AppRestartReport(running, newPid, _installedAppExe);
            EngineLog.Write($"[UpdateRestartCycle] RunAsync: {report.Describe()}");
            if (report.Note != null) EngineLog.Write($"[UpdateRestartCycle] RunAsync: {report.Note}");
            return new UpdateRestartOutcome<T>(result, report);
        }
        finally
        {
            (prepared as IDisposable)?.Dispose();
        }
    }

    private int Relaunch(RunningAppInstance running)
    {
        bool samePath = string.Equals(Path.GetFullPath(running.ExePath), Path.GetFullPath(_installedAppExe), StringComparison.OrdinalIgnoreCase);
        EngineLog.Write($"[UpdateRestartCycle] Relaunch: starting the installed {_installedAppExe} with argument(s): {running.ArgumentsText}"
                        + (samePath ? "" : $" (the stopped process ran from {running.ExePath})"));
        int pid;
        try
        {
            pid = _launcher.Launch(_installedAppExe, running.Arguments);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[UpdateRestartCycle] Relaunch FAILED: {ex.Message}");
            throw new AppRelaunchFailedException(_installedAppExe, ex);
        }
        EngineLog.Write($"[UpdateRestartCycle] Relaunch: started pid {pid}");
        return pid;
    }
}
