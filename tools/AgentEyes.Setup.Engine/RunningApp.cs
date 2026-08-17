using System.Diagnostics;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// Running-instance awareness for the installer: the single, shared way to detect
/// the AgentEyes tray app and to stop it (bounded + confirmed) before an update or
/// a launch. The wizard and the headless CLI both go through here so detection and
/// shutdown behave identically (issue #95).
///
/// Detection targets the INSTALLED app exe name (AgentEyesApp.exe -> process name
/// "AgentEyesApp"), derived from the layout, not a hard-coded literal. The prior code
/// searched for "AgentEyes", which never matched the real process name.
///
/// Shutdown is a BOUNDED graceful-then-force stop: a best-effort CloseMainWindow
/// request first (a tray app that closes-to-tray may ignore it), then a forced
/// Kill(entireProcessTree) on whatever remains, waiting up to a timeout and finally
/// re-querying to CONFIRM no matching process is left. It never fires-and-forgets:
/// the caller learns whether the app is actually gone and can fail explicitly.
/// </summary>
public static class RunningApp
{
    private static readonly TimeSpan DefaultGraceful = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The tray app's process name (no extension), derived from the installed exe.</summary>
    public static string ProcessName(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return ProcessNameFromExe(layout.PathFor(ComponentRegistry.App));
    }

    /// <summary>The process name (no extension) for an app exe path, e.g. AgentEyesApp.exe -> AgentEyesApp.</summary>
    public static string ProcessNameFromExe(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("exePath must not be empty.", nameof(exePath));
        return Path.GetFileNameWithoutExtension(exePath);
    }

    /// <summary>True when an AgentEyes tray instance is running (per the installed layout).</summary>
    public static bool IsRunning(InstallLayout layout) =>
        IsRunning(ProcessName(layout), Process.GetProcessesByName);

    /// <summary>True when an AgentEyes tray instance is running (per an app exe path).</summary>
    public static bool IsRunningForExe(string exePath) =>
        IsRunning(ProcessNameFromExe(exePath), Process.GetProcessesByName);

    /// <summary>Testable seam: detect by process name via an injected process provider.</summary>
    public static bool IsRunning(string processName, Func<string, Process[]> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var procs = provider(processName);
        try { return procs.Length > 0; }
        finally { DisposeAll(procs); }
    }

    /// <summary>
    /// Stop the running AgentEyes tray instance and CONFIRM it exited (default bounds).
    /// Returns true when no matching process remains (including "was not running").
    /// </summary>
    public static bool StopAndWait(InstallLayout layout) =>
        StopAndWait(ProcessName(layout), DefaultGraceful, DefaultTimeout, Process.GetProcessesByName);

    /// <summary>
    /// Bounded graceful-then-force stop, confirmed by a final re-query. Testable seam:
    /// the process provider is injected so the mechanism can be exercised without the app.
    /// </summary>
    public static bool StopAndWait(string processName, TimeSpan graceful, TimeSpan timeout,
        Func<string, Process[]> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        EngineLog.Write($"[RunningApp] StopAndWait: name={processName}, graceful={graceful.TotalSeconds}s, timeout={timeout.TotalSeconds}s");

        var deadline = DateTime.UtcNow + timeout;

        var initial = provider(processName);
        if (initial.Length == 0)
        {
            EngineLog.Write("[RunningApp] StopAndWait: no running instance - nothing to stop");
            return true;
        }
        EngineLog.Write($"[RunningApp] StopAndWait: {initial.Length} instance(s) running - stopping");

        // Phase 1: best-effort graceful close. A tray app that closes-to-tray may ignore
        // this; that is fine, phase 2 guarantees the stop.
        foreach (var p in initial)
            TryGracefulClose(p);
        WaitForExit(initial, DateTime.UtcNow + graceful);
        DisposeAll(initial);

        // Phase 2: force-kill whatever is still alive, then wait up to the deadline.
        var remaining = provider(processName);
        foreach (var p in remaining)
            TryKill(p);
        WaitForExit(remaining, deadline);
        DisposeAll(remaining);

        // Phase 3: the source of truth - re-query and report honestly.
        var leftover = provider(processName);
        bool gone = leftover.Length == 0;
        DisposeAll(leftover);
        EngineLog.Write($"[RunningApp] StopAndWait: gone={gone}");
        return gone;
    }

    // These per-process operations touch a live external process and can legitimately race
    // (the process may exit between the check and the call). A race here MEANS success (the
    // instance is gone), so the exception is logged - never swallowed silently - and the
    // final re-query in StopAndWait is what actually decides the outcome.

    private static void TryGracefulClose(Process p)
    {
        try { if (!p.HasExited) p.CloseMainWindow(); }
        catch (Exception ex) { EngineLog.Write($"[RunningApp] CloseMainWindow failed for pid={SafePid(p)}: {ex.Message}"); }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (p.HasExited) return;
            EngineLog.Write($"[RunningApp] force-killing pid={SafePid(p)}");
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { EngineLog.Write($"[RunningApp] Kill failed for pid={SafePid(p)}: {ex.Message}"); }
    }

    private static void WaitForExit(Process[] procs, DateTime deadline)
    {
        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited) continue;
                var remaining = deadline - DateTime.UtcNow;
                var millis = remaining <= TimeSpan.Zero ? 0 : (int)Math.Min(remaining.TotalMilliseconds, int.MaxValue);
                p.WaitForExit(millis);
            }
            catch (Exception ex) { EngineLog.Write($"[RunningApp] WaitForExit failed: {ex.Message}"); }
        }
    }

    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(); } catch { return "?"; }
    }

    private static void DisposeAll(Process[] procs)
    {
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }
    }
}
