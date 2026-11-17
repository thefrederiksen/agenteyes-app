using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// Refused because an isolated install (<c>agenteyes-setup --root</c>) would otherwise have to touch something
/// outside its root, or cannot tell whether it would (issue #200). Nothing has been stopped, started or replaced
/// when this is thrown.
/// </summary>
public sealed class IsolatedRootRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// The running-app handle for an ISOLATED layout (<see cref="InstallLayout.IsIsolated"/>, the setup CLI's
/// <c>--root</c>, issue #200).
///
/// The handle every other install uses (<see cref="RunningAppHandle"/>) finds AgentEyes by PROCESS NAME, so
/// with <c>--root &lt;scratch&gt;</c> it found the person's REAL app, stopped it, and started the scratch copy
/// in its place with the real app's arguments - measured on 16 and 17 November 2026: the real tray app's pid
/// was gone and a scratch build owned port 7882, ran its repair pass on the real library, or crashed. An
/// isolated install must touch nothing outside its root, and a running AgentEyes cannot be scoped to one: the
/// app keeps its config, log and recordings under the REAL per-user root and the REAL port whatever exe it
/// was started from. So this handle NEVER stops anything and the isolated cycle never starts anything:
///
///  * an AgentEyes running from OUTSIDE the root is somebody else's - it is logged and left alone, and
///    <see cref="Find"/> answers "not running" so the update replaces files without a stop or a restart;
///  * an AgentEyes running from INSIDE the root holds the files the update would replace, and stopping it
///    would end a recorder - so the install REFUSES before anything is downloaded, and says which pid to quit;
///  * a process whose exe path cannot be read cannot be told apart, so the install refuses as well.
/// </summary>
public sealed class IsolatedRootAppHandle : IRunningAppHandle
{
    private readonly InstallLayout _layout;
    private readonly Func<string, Process[]> _provider;
    private readonly Func<Process, string?> _exePathOf;

    public IsolatedRootAppHandle(InstallLayout layout) : this(layout, Process.GetProcessesByName, ExePathOf) { }

    /// <summary>Testable seam: the process provider and how a process's exe path is read.</summary>
    public IsolatedRootAppHandle(InstallLayout layout, Func<string, Process[]> provider, Func<Process, string?> exePathOf)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        if (!layout.IsIsolated)
            throw new ArgumentException("IsolatedRootAppHandle is only for an isolated layout (--root).", nameof(layout));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _exePathOf = exePathOf ?? throw new ArgumentNullException(nameof(exePathOf));
    }

    /// <summary>
    /// Always null - an isolated install never stops or restarts an app. Throws
    /// <see cref="IsolatedRootRefusedException"/> when an AgentEyes runs from inside the root, or when a
    /// process's exe path cannot be read.
    /// </summary>
    public RunningAppInstance? Find()
    {
        string name = RunningApp.ProcessName(_layout);
        EngineLog.Write($"[IsolatedRootAppHandle] Find: looking for {name} processes; root={_layout.LocalRoot}");
        var procs = _provider(name);
        try
        {
            var inside = new List<string>();
            var unreadable = new List<int>();
            foreach (var p in procs)
            {
                string? exe = _exePathOf(p);
                if (exe is null)
                {
                    EngineLog.Write($"[IsolatedRootAppHandle] Find: pid {p.Id} - its exe path could not be read");
                    unreadable.Add(p.Id);
                }
                else if (_layout.Contains(exe))
                {
                    EngineLog.Write($"[IsolatedRootAppHandle] Find: pid {p.Id} runs from INSIDE the root: {exe}");
                    inside.Add($"pid {p.Id} ({exe})");
                }
                else
                {
                    EngineLog.Write($"[IsolatedRootAppHandle] Find: pid {p.Id} runs from outside the root ({exe}) - not this install's; left alone");
                }
            }

            if (inside.Count > 0)
                throw Refuse($"AgentEyes is running from inside the --root install: {string.Join(", ", inside)}. "
                             + "An isolated install never stops or starts an app. Nothing was downloaded, stopped or replaced. "
                             + "Quit that copy and run the command again.");
            if (unreadable.Count > 0)
                throw Refuse($"could not read which exe AgentEyes pid {string.Join(", pid ", unreadable)} runs, so it cannot be told "
                             + $"apart from a copy inside {_layout.LocalRoot}. Nothing was downloaded, stopped or replaced.");

            EngineLog.Write($"[IsolatedRootAppHandle] Find: no AgentEyes runs from the root ({procs.Length} outside it, untouched)");
            return null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>Never legitimate: <see cref="Find"/> never reports an instance, so nothing is ever stopped.</summary>
    public Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct)
    {
        EngineLog.Write($"[IsolatedRootAppHandle] StopAsync REFUSED: pid {instance?.Pid}");
        throw Refuse("an isolated (--root) install never stops an app.");
    }

    private IsolatedRootRefusedException Refuse(string message)
    {
        EngineLog.Write($"[IsolatedRootAppHandle] REFUSED: {message}");
        return new IsolatedRootRefusedException(message);
    }

    /// <summary>
    /// The full path of the exe a process runs, or null when Windows will not say. Uses
    /// PROCESS_QUERY_LIMITED_INFORMATION, which a normal process is granted even on an elevated one, rather
    /// than <see cref="Process.MainModule"/> (which needs VM read access and throws across that boundary).
    /// </summary>
    public static string? ExePathOf(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        const uint ProcessQueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            EngineLog.Write($"[IsolatedRootAppHandle] ExePathOf: OpenProcess pid {process.Id} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return null;
        }
        try
        {
            var buffer = new StringBuilder(32768);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                EngineLog.Write($"[IsolatedRootAppHandle] ExePathOf: QueryFullProcessImageName pid {process.Id} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return null;
            }
            return buffer.ToString(0, size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// The launcher an isolated (<c>--root</c>) install is given (issue #200): it never starts anything. A started
/// AgentEyes cannot be scoped to the root - it uses the real per-user data and the real port - so starting one
/// is refused outright rather than trusted never to be reached.
/// </summary>
public sealed class RefusingAppLauncher : IAppLauncher
{
    public int Launch(string exePath, IReadOnlyList<string> arguments)
    {
        EngineLog.Write($"[RefusingAppLauncher] Launch REFUSED: {exePath}");
        throw new IsolatedRootRefusedException($"an isolated (--root) install never starts an app (asked to start {exePath}).");
    }
}
