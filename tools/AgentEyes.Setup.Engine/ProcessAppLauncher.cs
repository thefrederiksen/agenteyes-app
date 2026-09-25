using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// The real <see cref="IAppLauncher"/>: starts the app exe with the given arguments, DETACHED from the
/// updater (issue #94). The single-file host's native-extraction variable is set explicitly from the
/// layout because the setup CLI runs its update from a temp copy that deliberately DROPS that variable
/// from its own environment (see the CLI's relaunch-from-temp); an app inheriting that environment
/// would unpack its native DLLs into %TEMP% and break the way issue #120 describes.
///
/// Why this is a CreateProcess call and not <see cref="Process.Start(ProcessStartInfo)"/> (issue #94):
/// Process.Start with UseShellExecute=false always passes bInheritHandles=TRUE, so the started app
/// received a copy of every inheritable handle the updater held - including the stdout/stderr pipe a
/// script gave the updater when it captured its output (PowerShell's "$o = &amp; agenteyes-setup update 2>&amp;1").
/// That pipe then stayed open for as long as the app ran, and the script never returned.
/// UseShellExecute=true would not inherit handles either, but .NET refuses to combine it with the
/// environment variable above. So the app is started with bInheritHandles=FALSE (no handle of the
/// updater's reaches it, standard handles included), no STARTF_USESTDHANDLES, and CREATE_NO_WINDOW: a
/// console exe gets a hidden console of its own instead of the updater's; the tray app is a GUI exe and
/// the flag is ignored for it.
/// </summary>
public sealed class ProcessAppLauncher : IAppLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    private readonly InstallLayout _layout;

    public ProcessAppLauncher(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public int Launch(string exePath, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            EngineLog.Write("[ProcessAppLauncher] Launch FAILED: exePath is empty");
            throw new ArgumentException("exePath must not be empty.", nameof(exePath));
        }
        if (arguments is null)
        {
            EngineLog.Write($"[ProcessAppLauncher] Launch FAILED: arguments is null for {exePath}");
            throw new ArgumentNullException(nameof(arguments));
        }
        if (!File.Exists(exePath))
        {
            EngineLog.Write($"[ProcessAppLauncher] Launch FAILED: the exe to start is not there: {exePath}");
            throw new FileNotFoundException("The app exe to start again is not there.", exePath);
        }

        string commandLine = BuildCommandLine(exePath, arguments);
        // The exe's own directory, from its full path: Path.GetDirectoryName of a bare "AgentEyesApp.exe"
        // is "" (not null), and CreateProcessW rejects "" as a directory (review of PR #95). Of a full
        // path it is null only for a drive root, which no file can be - so that is an error, not a case.
        string fullExePath = Path.GetFullPath(exePath);
        string workingDirectory = Path.GetDirectoryName(fullExePath)
                                  ?? throw new InvalidOperationException($"{fullExePath} has no parent directory to start the app in.");
        EngineLog.Write($"[ProcessAppLauncher] Launch: starting detached (no inherited handles, own console) in {workingDirectory}: {commandLine}");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string key && key.Length > 0 && e.Value is string value)
                environment[key] = value;
        }
        environment[InstallFinalizer.BundleExtractBaseDirVariable] = _layout.BundleExtractDir;

        int pid = StartDetached(commandLine, workingDirectory, environment);
        EngineLog.Write($"[ProcessAppLauncher] Launch: started {exePath} as pid {pid}");
        return pid;
    }

    /// <summary>
    /// The one command line the new process gets: the exe (quoted) followed by each argument quoted by
    /// the C runtime's rules, so that the process's own argv split (CommandLineToArgvW, what
    /// <see cref="RunningAppHandle.SplitCommandLine"/> uses) yields exactly <paramref name="arguments"/>
    /// again - an argument with spaces, quotes or a trailing backslash comes through verbatim.
    /// </summary>
    public static string BuildCommandLine(string exePath, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            EngineLog.Write("[ProcessAppLauncher] BuildCommandLine FAILED: exePath is empty");
            throw new ArgumentException("exePath must not be empty.", nameof(exePath));
        }
        if (arguments is null)
        {
            EngineLog.Write($"[ProcessAppLauncher] BuildCommandLine FAILED: arguments is null for {exePath}");
            throw new ArgumentNullException(nameof(arguments));
        }
        var sb = new StringBuilder();
        AppendArgument(sb, exePath);
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is null)
            {
                EngineLog.Write($"[ProcessAppLauncher] BuildCommandLine FAILED: argument {i} is null for {exePath}");
                throw new ArgumentException($"argument {i} must not be null.", nameof(arguments));
            }
            AppendArgument(sb, arguments[i]);
        }
        EngineLog.Write($"[ProcessAppLauncher] BuildCommandLine: {arguments.Count} argument(s) -> {sb}");
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (sb.Length > 0) sb.Append(' ');
        if (argument.Length > 0 && !NeedsQuoting(argument))
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int i = 0;
        while (i < argument.Length)
        {
            char c = argument[i++];
            if (c == '\\')
            {
                int backslashes = 1;
                while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }
                if (i == argument.Length)
                {
                    sb.Append('\\', backslashes * 2);                  // before the closing quote: doubled
                }
                else if (argument[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');  // before a quote: doubled, and the quote escaped
                    i++;
                }
                else
                {
                    sb.Append('\\', backslashes);                      // anywhere else: literal
                }
            }
            else if (c == '"')
            {
                sb.Append('\\').Append('"');
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
    }

    private static bool NeedsQuoting(string argument)
    {
        foreach (char c in argument)
            if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"') return true;
        return false;
    }

    /// <summary>
    /// CreateProcessW with bInheritHandles=FALSE: the new process gets none of this process's handles,
    /// standard handles included, and a hidden console of its own if it is a console exe. Returns the pid.
    /// </summary>
    private static int StartDetached(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var startup = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        IntPtr env = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));
        try
        {
            // CreateProcessW may write into the command line buffer, so it gets a mutable copy.
            var mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
            bool ok = CreateProcessW(null, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false,
                CreateUnicodeEnvironment | CreateNoWindow, env, workingDirectory, ref startup, out PROCESS_INFORMATION info);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                string reason = new Win32Exception(error).Message;
                EngineLog.Write($"[ProcessAppLauncher] StartDetached FAILED: CreateProcess error {error} ({reason}) for {commandLine}");
                throw new Win32Exception(error, $"CreateProcess failed for {commandLine}: {reason}");
            }
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(env);
        }
    }

    /// <summary>The Unicode environment block: NAME=value pairs, each NUL-terminated, sorted the way
    /// Windows keeps them, ending in an extra NUL (the marshaller adds the final one).</summary>
    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var key in environment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(environment[key]).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
