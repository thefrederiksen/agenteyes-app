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
/// the stop as they choose, so the whole stop -> replace -> relaunch decision runs without an app.
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
public sealed record AppRestartReport(RunningAppInstance? Stopped, int? NewPid)
{
    /// <summary>True when an instance was running, was stopped, and was started again.</summary>
    public bool Restarted => Stopped != null && NewPid.HasValue;

    /// <summary>The one line the update prints: the pids when the app was restarted, otherwise that
    /// it was not running and was not started.</summary>
    public string Describe() => Restarted
        ? $"restarted the running app (pid {Stopped!.Pid} -> pid {NewPid})"
        : "AgentEyes was not running - it was not started";
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
/// The result of one cycle: what the replace step returned, and what happened to the app.
/// </summary>
public sealed record UpdateRestartOutcome<T>(T Result, AppRestartReport Restart);

/// <summary>
/// The ONE stop -> replace -> relaunch decision every update path takes (issue #86): the setup CLI's
/// update and install, the wizard, and the app's own AutoUpdate (which hands over to the CLI so that
/// it too goes through here). Before this, `agenteyes-setup update` replaced the files and left the
/// running process on the old build until somebody happened to restart it.
///
/// The order is the point:
///  1. find the running app; if it is running, STOP it and confirm it is gone. When it cannot be
///     stopped the cycle throws <see cref="AppStopFailedException"/> HERE, before the replace step has
///     run, so the installed files are byte-for-byte what they were - no half state.
///  2. run the replace step.
///  3. if the app was running, START IT AGAIN with the arguments it was running with (a `--tray` app
///     comes back as a `--tray` app, issue #61) - whatever the replace step returned, and even when it
///     threw: an app this cycle stopped is never left stopped.
/// When the app was not running, it is not started.
/// </summary>
public sealed class UpdateRestartCycle
{
    private readonly IRunningAppHandle _app;
    private readonly IAppLauncher _launcher;

    public UpdateRestartCycle(IRunningAppHandle app, IAppLauncher launcher)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    /// <summary>
    /// Run <paramref name="replace"/> with the app stopped, and start the app again afterwards when
    /// it was running. Throws <see cref="AppStopFailedException"/> without calling <paramref name="replace"/>
    /// when the app cannot be stopped.
    /// </summary>
    public async Task<UpdateRestartOutcome<T>> RunAsync<T>(Func<CancellationToken, Task<T>> replace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replace);
        EngineLog.Write("[UpdateRestartCycle] RunAsync: looking for a running AgentEyes");

        var running = _app.Find();
        if (running == null)
        {
            EngineLog.Write("[UpdateRestartCycle] RunAsync: AgentEyes is not running - replacing without a restart");
            var only = await replace(ct);
            return new UpdateRestartOutcome<T>(only, new AppRestartReport(null, null));
        }

        EngineLog.Write($"[UpdateRestartCycle] RunAsync: AgentEyes is running (pid {running.Pid}, {running.ExePath}, "
                        + $"arguments: {running.ArgumentsText}) - stopping it before anything is replaced");
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
            result = await replace(ct);
        }
        catch (Exception ex)
        {
            // The replace step failed with the app stopped. Start the app again - whatever is on disk
            // now is what the person has - and let the failure propagate with the relaunch on record.
            EngineLog.Write($"[UpdateRestartCycle] RunAsync: the replace step FAILED ({ex.Message}); starting the app again anyway");
            Relaunch(running);
            throw;
        }

        int newPid = Relaunch(running);
        var report = new AppRestartReport(running, newPid);
        EngineLog.Write($"[UpdateRestartCycle] RunAsync: {report.Describe()}");
        return new UpdateRestartOutcome<T>(result, report);
    }

    private int Relaunch(RunningAppInstance running)
    {
        EngineLog.Write($"[UpdateRestartCycle] Relaunch: starting {running.ExePath} with argument(s): {running.ArgumentsText}");
        int pid = _launcher.Launch(running.ExePath, running.Arguments);
        EngineLog.Write($"[UpdateRestartCycle] Relaunch: started pid {pid}");
        return pid;
    }
}
