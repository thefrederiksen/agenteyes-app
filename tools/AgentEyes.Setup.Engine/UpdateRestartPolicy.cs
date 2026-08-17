namespace AgentEyes.Setup.Engine;

/// <summary>
/// What the running app must do once an in-place AutoUpdate has swapped the on-disk exe (issue #107).
/// There is deliberately NO "keep running" option: AgentEyes ships as a single-file self-contained
/// host that reads its managed assemblies out of AgentEyesApp.exe lazily, the first time each is
/// needed. Once that exe is replaced on disk, the still-running pre-update process can no longer load
/// any assembly it had not already loaded, so every not-yet-exercised feature dies on first use with
/// System.IO.FileNotFoundException. The only safe end states are therefore "restart now" or "defer the
/// restart" - never "continue serving from the replaced bundle".
/// </summary>
public enum UpdateApplyDecision
{
    /// <summary>No recording session is active: restart the process into the fresh exe now.</summary>
    RestartNow,

    /// <summary>A recording session is active: defer the restart until that session ends
    /// (or until the next clean launch) so no in-flight capture is truncated.</summary>
    DeferSessionActive,
}

/// <summary>
/// The single decision the running app makes AFTER an AutoUpdate has swapped the on-disk exe
/// (issue #107): restart into the new exe now, or defer that restart because an active
/// recording session must not be interrupted. Kept as a pure, side-effect-free function
/// so the "an applied update never leaves the process serving from a replaced bundle" invariant is
/// unit-testable without launching the app (mirrors the injected-seam style of <see cref="RunningApp"/>).
/// </summary>
public static class UpdateRestartPolicy
{
    /// <summary>
    /// Decide what to do once the update has been applied on disk. When a session is active the
    /// restart is deferred (the caller re-invokes when the session ends); otherwise the process
    /// restarts immediately. In neither case does the process keep serving from the stale bundle.
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
