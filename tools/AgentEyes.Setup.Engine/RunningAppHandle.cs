using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// The real <see cref="IRunningAppHandle"/> (issue #86): finds the installed tray app by its process
/// name (via <see cref="RunningApp"/>), reads the command line it was started with so the relaunch can
/// repeat it verbatim, and stops it through the bounded graceful-then-force stop.
///
/// The command line is read from the process itself (Win32_Process.CommandLine) rather than guessed
/// from the shortcut or the Run key: the person may have started the app from a terminal, a script or
/// a scheduled task with arguments none of those know about, and an update must not rewrite how the app
/// starts (issue #61). When it cannot be read, <see cref="Find"/> throws with the reason BEFORE anything
/// is stopped or replaced - the update does not proceed on a guess.
/// </summary>
public sealed class RunningAppHandle : IRunningAppHandle
{
    private readonly InstallLayout _layout;
    private readonly Func<int, string?> _commandLineOf;

    public RunningAppHandle(InstallLayout layout) : this(layout, WmiCommandLine) { }

    /// <summary>Testable seam: how a process's command line is read.</summary>
    public RunningAppHandle(InstallLayout layout, Func<int, string?> commandLineOf)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _commandLineOf = commandLineOf ?? throw new ArgumentNullException(nameof(commandLineOf));
    }

    public RunningAppInstance? Find()
    {
        string name = RunningApp.ProcessName(_layout);
        var procs = Process.GetProcessesByName(name);
        try
        {
            if (procs.Length == 0)
            {
                EngineLog.Write($"[RunningAppHandle] Find: no {name} process");
                return null;
            }
            if (procs.Length > 1)
                EngineLog.Write($"[RunningAppHandle] Find: {procs.Length} {name} processes - the first (pid {procs[0].Id}) is the one relaunched; "
                                + "the stop covers all of them");
            var p = procs[0];
            string exe = p.MainModule?.FileName
                         ?? throw new InvalidOperationException($"the running AgentEyes (pid {p.Id}) did not say which exe it runs");
            var instance = Describe(p.Id, exe, _commandLineOf(p.Id));
            EngineLog.Write($"[RunningAppHandle] Find: pid {instance.Pid}, exe {instance.ExePath}, arguments: {instance.ArgumentsText}");
            return instance;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        // The stop blocks for up to the bound; off the caller's thread so a wizard stays responsive.
        return Task.Run(() => RunningApp.StopAndWait(_layout), ct);
    }

    /// <summary>
    /// The instance record for a process, from its command line. Pure apart from the argv split, so it is
    /// tested with made-up lines. Throws when the command line is missing - a relaunch with guessed
    /// arguments is not a relaunch of what was running.
    /// </summary>
    public static RunningAppInstance Describe(int pid, string exePath, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("exePath must not be empty.", nameof(exePath));
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new InvalidOperationException(
                $"the command line of the running AgentEyes (pid {pid}) could not be read, so it cannot be started again "
                + "the way it was running. Quit it (tray icon -> Quit) and run the update again.");
        var argv = SplitCommandLine(commandLine);
        var arguments = LaunchArguments.ToCarryAcrossRestart(argv);
        return new RunningAppInstance(pid, exePath, arguments);
    }

    /// <summary>A process's command line as Windows recorded it, or null when WMI has no answer.</summary>
    public static string? WmiCommandLine(int pid)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
        using var results = searcher.Get();
        foreach (ManagementBaseObject row in results)
        {
            using (row)
            {
                return row["CommandLine"] as string;
            }
        }
        return null;
    }

    /// <summary>
    /// Split a Windows command line into argv exactly as the started process saw it (the C runtime's
    /// rules, via shell32's CommandLineToArgvW): argv[0] is the executable as written on the line.
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.Trim().Length == 0) return Array.Empty<string>();
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CommandLineToArgvW failed");
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

