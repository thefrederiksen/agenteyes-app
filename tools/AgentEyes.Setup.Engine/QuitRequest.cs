using System.Threading;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// The planned-stop channel between the setup engine and a running AgentEyes (issue #86).
///
/// <see cref="RunningApp"/> used to ask the app to close with <c>Process.CloseMainWindow</c>. A tray
/// app started with <c>--tray</c> has no main window, and one whose window closes to the tray ignores
/// WM_CLOSE, so the "graceful" phase never reached the app and every update ended in the force kill:
/// ffmpeg died with its last piece half-written, the always-on engine never got to hand its open clip
/// over, and the recording HUD's state was lost. The app now creates a named event for its own pid and
/// waits on it; the engine sets that event to ask for a planned stop. The app then leaves through its
/// normal quit path (finishing a recording in progress, stopping always-on for a restart), exactly as
/// if Quit had been clicked.
///
/// The event is named per pid so the request reaches exactly the process the engine found, never a
/// second instance that is refusing the single-instance lock. A build that does not create the event
/// (anything before this issue) cannot be asked; <see cref="TrySignal"/> says so by returning false and
/// the engine falls through to the bounded force stop after the legacy grace period.
/// </summary>
public static class QuitRequest
{
    /// <summary>The named event a running AgentEyes with process id <paramref name="pid"/> waits on.</summary>
    public static string EventName(int pid) => $"AgentEyes-quit-{pid}";

    /// <summary>
    /// Create this process's quit event and wait on it on a background thread; <paramref name="onRequested"/>
    /// runs on that thread once, when the event is set. Dispose to stop listening (the event is then gone
    /// and a later <see cref="TrySignal"/> for this pid returns false).
    /// </summary>
    public static IDisposable Listen(Action onRequested) => Listen(Environment.ProcessId, onRequested);

    /// <summary>Testable seam: listen under an explicit pid.</summary>
    public static IDisposable Listen(int pid, Action onRequested)
    {
        ArgumentNullException.ThrowIfNull(onRequested);
        var listener = new Listener(EventName(pid), onRequested);
        EngineLog.Write($"[QuitRequest] Listen: waiting on {EventName(pid)}");
        return listener;
    }

    /// <summary>
    /// Ask the AgentEyes with process id <paramref name="pid"/> to stop. True when a listener exists and
    /// was signalled; false when that process does not listen (an older build, or no such process).
    /// </summary>
    public static bool TrySignal(int pid)
    {
        string name = EventName(pid);
        if (!EventWaitHandle.TryOpenExisting(name, out var handle))
        {
            EngineLog.Write($"[QuitRequest] TrySignal: pid={pid} does not listen for a quit request ({name} does not exist)");
            return false;
        }
        using (handle)
        {
            handle.Set();
        }
        EngineLog.Write($"[QuitRequest] TrySignal: quit requested from pid={pid} via {name}");
        return true;
    }

    private sealed class Listener : IDisposable
    {
        private readonly EventWaitHandle _quit;
        private readonly ManualResetEvent _stop = new(false);
        private readonly Thread _thread;
        private int _disposed;

        public Listener(string name, Action onRequested)
        {
            _quit = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            _thread = new Thread(() =>
            {
                // A thread entry point: the one place a failure is caught, and it is logged, never swallowed.
                try
                {
                    int signalled = WaitHandle.WaitAny(new WaitHandle[] { _quit, _stop });
                    if (signalled != 0) return;
                    EngineLog.Write($"[QuitRequest] {name}: a planned stop was requested");
                    onRequested();
                }
                catch (Exception ex)
                {
                    EngineLog.Write($"[QuitRequest] {name}: the listener FAILED: {ex}");
                }
            })
            {
                IsBackground = true,
                Name = "AgentEyes quit request",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _quit.Dispose();
            _stop.Dispose();
        }
    }
}
