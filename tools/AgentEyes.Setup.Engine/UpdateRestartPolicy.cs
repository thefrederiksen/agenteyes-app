namespace AgentEyes.Setup.Engine;

/// <summary>
/// What the running app does about an available AutoUpdate (issues #107, #86). There is deliberately
/// NO "keep running on the new files" option: AgentEyes ships as a single-file self-contained host that
/// reads its managed assemblies out of AgentEyesApp.exe lazily, the first time each is needed. If that
/// exe were replaced on disk under the running process, every not-yet-exercised feature would die on
/// first use with System.IO.FileNotFoundException (#107). So since #86 the app never replaces its own
/// files at all: it either hands the update to the setup engine NOW - which stops this process,
/// replaces the files and starts the app again (<see cref="UpdateRestartCycle"/>) - or DEFERS that
/// handover while a recording session is active.
/// </summary>
public enum UpdateApplyDecision
{
    /// <summary>No recording session is active: hand over to the setup engine now (stop, replace, relaunch).</summary>
    RestartNow,

    /// <summary>A recording session is active: defer the handover until that session ends
    /// (or until the person asks from the tray) so no in-flight capture is truncated.</summary>
    DeferSessionActive,
}

/// <summary>
/// The single decision the running app makes when an AutoUpdate is available (issues #107, #86):
/// hand over to the stop/replace/relaunch cycle now, or defer because an active recording session
/// must not be interrupted. The decision is made BEFORE any file is replaced. Kept as a pure,
/// side-effect-free function so the "the running process never serves from replaced files" invariant
/// is unit-testable without launching the app (mirrors the injected-seam style of <see cref="RunningApp"/>).
/// </summary>
public static class UpdateRestartPolicy
{
    /// <summary>
    /// Decide what to do about an available update. When a session is active the handover is deferred
    /// (the caller re-invokes when the session ends); otherwise the app hands over immediately. In
    /// neither case are files replaced under the running process.
    /// </summary>
    /// <param name="sessionActive">True when a recording session is in progress.</param>
    public static UpdateApplyDecision Decide(bool sessionActive)
    {
        EngineLog.Write($"[UpdateRestartPolicy] Decide: sessionActive={sessionActive}");
        var decision = sessionActive ? UpdateApplyDecision.DeferSessionActive : UpdateApplyDecision.RestartNow;
        EngineLog.Write($"[UpdateRestartPolicy] Decide -> {decision}");
        return decision;
    }
}
