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
/// Shutdown is a BOUNDED graceful-then-force stop: a planned-stop request through
/// <see cref="QuitRequest"/> (issue #86 - the app leaves through its own quit path, finishing
/// its always-on piece and handing its open clip over) plus a best-effort CloseMainWindow, then
/// a forced Kill(entireProcessTree) on whatever remains, waiting up to a timeout and finally
/// re-querying to CONFIRM no matching process is left. It never fires-and-forgets:
/// the caller learns whether the app is actually gone and can fail explicitly.
///
/// The grace period depends on whether the request was DELIVERED (<see cref="GracePeriod"/>): a build
/// that listens gets the whole <see cref="DefaultGraceful"/> to stop ffmpeg and write its handover; a
/// build that does not listen (before #86, or a window that closes to the tray) cannot use it, so the
/// force stop follows after <see cref="LegacyGrace"/> instead of making the person wait for nothing.
/// </summary>
public static class RunningApp
{
    /// <summary>How long a build that took the quit request may take to leave: ffmpeg finishes its
    /// piece (up to 15 s), then the always-on handover and the rest of App.OnExit.</summary>
    public static readonly TimeSpan DefaultGraceful = TimeSpan.FromSeconds(30);

    /// <summary>The grace period when no quit request could be delivered: CloseMainWindow alone.</summary>
    public static readonly TimeSpan LegacyGrace = TimeSpan.FromSeconds(3);

    /// <summary>The bound on the whole stop, force phase included.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

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
        StopAndWait(ProcessName(layout), DefaultGraceful, DefaultTimeout, Process.GetProcessesByName, QuitRequest.TrySignal);

    /// <summary>
    /// Bounded graceful-then-force stop, confirmed by a final re-query. Testable seam:
    /// the process provider and the quit-request delivery are injected so the mechanism can be
    /// exercised without the app. <paramref name="requestQuit"/> null means no planned-stop channel
    /// (CloseMainWindow only), so the grace period is <see cref="LegacyGrace"/> at most.
    /// </summary>
    public static bool StopAndWait(string processName, TimeSpan graceful, TimeSpan timeout,
        Func<string, Process[]> provider, Func<int, bool>? requestQuit = null)
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

        // Phase 1: the planned-stop request (issue #86), then a best-effort graceful close. A build
        // that listens leaves through its own quit path; one that does not may ignore both - that is
        // fine, phase 2 guarantees the stop.
        bool delivered = false;
        foreach (var p in initial)
        {
            delivered |= TryRequestQuit(p, requestQuit);
            TryGracefulClose(p);
        }
        var grace = GracePeriod(delivered, graceful);
        EngineLog.Write($"[RunningApp] StopAndWait: quit request delivered={delivered}; waiting up to {grace.TotalSeconds}s for a clean exit");
        WaitForExit(initial, DateTime.UtcNow + grace);
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

    /// <summary>
    /// How long the graceful phase waits (issue #86), pure so both arms are tested: the whole
    /// <paramref name="graceful"/> when a quit request was delivered to at least one instance, else
    /// the shorter of it and <see cref="LegacyGrace"/> - nobody is going to honour a longer wait.
    /// </summary>
    public static TimeSpan GracePeriod(bool quitRequestDelivered, TimeSpan graceful)
    {
        if (graceful < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(graceful), "the grace period cannot be negative");
        if (quitRequestDelivered) return graceful;
        return graceful < LegacyGrace ? graceful : LegacyGrace;
    }

    // These per-process operations touch a live external process and can legitimately race
    // (the process may exit between the check and the call). A race here MEANS success (the
    // instance is gone), so the exception is logged - never swallowed silently - and the
    // final re-query in StopAndWait is what actually decides the outcome.

    private static bool TryRequestQuit(Process p, Func<int, bool>? requestQuit)
    {
        if (requestQuit == null) return false;
        try { return !p.HasExited && requestQuit(p.Id); }
        catch (Exception ex)
        {
            EngineLog.Write($"[RunningApp] quit request failed for pid={SafePid(p)}: {ex.Message}");
            return false;
        }
    }

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
