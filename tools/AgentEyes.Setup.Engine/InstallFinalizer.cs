using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// The per-user finalization that turns placed files into a usable install: the
/// app dir on the user PATH (so "agenteyes" works in any terminal), the Start Menu /
/// desktop shortcuts, the run-at-login Run key, and the Add/Remove Programs
/// entry. Shared by the wizard and the CLI so both produce identical installs.
/// Idempotent; safe to call after any install/update.
/// </summary>
public static class InstallFinalizer
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AgentEyes";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentEyes";

    // ---- PATH ---------------------------------------------------------------

    /// <summary>Add the app dir to the user PATH if not already present. Returns true if it changed.</summary>
    public static bool AddAppDirToPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var updated = ComputePathWith(current, layout.AppDir);
        if (updated == current) return false;
        // SetEnvironmentVariable(User) persists to the registry and broadcasts WM_SETTINGCHANGE, so new
        // processes pick it up (existing shells still need to be reopened).
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
        EngineLog.Write($"[InstallFinalizer] added to PATH: {layout.AppDir}");
        return true;
    }

    /// <summary>Remove the app dir from the user PATH. Returns true if it changed.</summary>
    public static bool RemoveAppDirFromPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var updated = ComputePathWithout(current, layout.AppDir);
        if (updated == current) return false;
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
        EngineLog.Write($"[InstallFinalizer] removed from PATH: {layout.AppDir}");
        return true;
    }

    /// <summary>Return <paramref name="path"/> with <paramref name="dir"/> appended unless already present. Pure.</summary>
    public static string ComputePathWith(string path, string dir)
    {
        var entries = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => string.Equals(e.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return path ?? "";
        return string.IsNullOrEmpty(path) ? dir : path.TrimEnd(';') + ";" + dir;
    }

    /// <summary>Return <paramref name="path"/> with <paramref name="dir"/> removed (case-insensitive). Pure.</summary>
    public static string ComputePathWithout(string path, string dir)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var kept = path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !string.Equals(e.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        return string.Join(";", kept);
    }

    /// <summary>True when the app dir is on the user PATH.</summary>
    public static bool IsAppDirOnPath(InstallLayout layout)
    {
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        return ComputePathWithout(current, layout.AppDir) != current;
    }

    // ---- single-file native extraction dir ----------------------------------

    /// <summary>The per-user environment variable that moves single-file native extraction off %TEMP%.</summary>
    public const string BundleExtractBaseDirVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    /// <summary>
    /// Point the single-file host's native-DLL extraction at
    /// %LOCALAPPDATA%\AgentEyes\bundle instead of %TEMP%\.net, and create that directory.
    /// Every AgentEyes exe is published self-contained single-file with
    /// IncludeNativeLibrariesForSelfExtract, so the host unpacks wpfgfx_cor3.dll and
    /// friends on first launch; when a temp cleaner empties %TEMP% it deletes the ones no
    /// running process holds open, and the host does NOT re-extract them - the app is then
    /// permanently broken (issue #120). Returns true when the variable changed.
    /// Idempotent; touches no other user environment variable.
    /// </summary>
    public static bool SetBundleExtractBaseDir(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(layout.BundleExtractDir);
        var current = Environment.GetEnvironmentVariable(BundleExtractBaseDirVariable, EnvironmentVariableTarget.User);
        if (IsSameDirectory(current, layout.BundleExtractDir))
        {
            EngineLog.Write($"[InstallFinalizer] {BundleExtractBaseDirVariable} already set: {layout.BundleExtractDir}");
            return false;
        }
        // SetEnvironmentVariable(User) persists to the registry and broadcasts WM_SETTINGCHANGE,
        // so Explorer-launched processes (shortcuts, the Run-key autostart) inherit it; a terminal
        // opened before the install still needs reopening, exactly as for the PATH entry above.
        Environment.SetEnvironmentVariable(BundleExtractBaseDirVariable, layout.BundleExtractDir, EnvironmentVariableTarget.User);
        EngineLog.Write($"[InstallFinalizer] set {BundleExtractBaseDirVariable}={layout.BundleExtractDir}");
        return true;
    }

    /// <summary>True when the user variable points at this install's bundle extraction dir.</summary>
    public static bool IsBundleExtractBaseDirSet(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = Environment.GetEnvironmentVariable(BundleExtractBaseDirVariable, EnvironmentVariableTarget.User);
        return IsSameDirectory(current, layout.BundleExtractDir);
    }

    /// <summary>
    /// Clear the user variable, but only while it still names THIS install's bundle dir - a
    /// value the user pointed somewhere else is theirs, not ours. Returns true if it changed.
    /// </summary>
    public static bool RemoveBundleExtractBaseDir(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsBundleExtractBaseDirSet(layout)) return false;
        Environment.SetEnvironmentVariable(BundleExtractBaseDirVariable, null, EnvironmentVariableTarget.User);
        EngineLog.Write($"[InstallFinalizer] removed {BundleExtractBaseDirVariable}");
        return true;
    }

    /// <summary>True when both strings name the same directory (case- and trailing-slash-insensitive). Pure.</summary>
    public static bool IsSameDirectory(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    // ---- shortcuts ----------------------------------------------------------

    /// <summary>Start Menu shortcut path ("AgentEyes.lnk" - same name the Inno installer used).</summary>
    public static string StartMenuShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "AgentEyes.lnk");

    /// <summary>Desktop shortcut path.</summary>
    public static string DesktopShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AgentEyes.lnk");

    /// <summary>Create (or overwrite) the Start Menu shortcut for the app. No-op if the exe is absent.</summary>
    public static bool CreateStartMenuShortcut(InstallLayout layout) =>
        CreateShortcut(layout, StartMenuShortcutPath());

    /// <summary>Create (or overwrite) the desktop shortcut for the app. No-op if the exe is absent.</summary>
    public static bool CreateDesktopShortcut(InstallLayout layout) =>
        CreateShortcut(layout, DesktopShortcutPath());

    private static bool CreateShortcut(InstallLayout layout, string lnk)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var exe = layout.PathFor(ComponentRegistry.App);
        if (!File.Exists(exe)) return false;

        var dir = Path.GetDirectoryName(lnk);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM object not available.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        var shortcut = shell.GetType().InvokeMember("CreateShortcut",
            BindingFlags.InvokeMethod, null, shell, [lnk])
            ?? throw new InvalidOperationException("CreateShortcut returned null.");

        var t = shortcut.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [exe]);
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(exe)]);
        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{exe},0"]);
        t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["AgentEyes"]);
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

        Marshal.ReleaseComObject(shortcut);
        Marshal.ReleaseComObject(shell);

        EngineLog.Write($"[InstallFinalizer] created shortcut: {lnk}");
        return true;
    }

    // ---- run at login --------------------------------------------------------

    /// <summary>Enable/disable run-at-login (per-user Run key; same value the app's own toggle uses).</summary>
    public static void SetAutostart(InstallLayout layout, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(layout);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{RunKeyPath}.");
        if (enabled)
        {
            var exe = layout.PathFor(ComponentRegistry.App);
            key.SetValue(RunValueName, $"\"{exe}\" --tray");
            EngineLog.Write("[InstallFinalizer] autostart enabled");
        }
        else if (key.GetValue(RunValueName) != null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            EngineLog.Write("[InstallFinalizer] autostart removed");
        }
    }

    /// <summary>True when the run-at-login Run key is set.</summary>
    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    // ---- Add/Remove Programs entry --------------------------------------------

    /// <summary>
    /// Register the per-user Add/Remove Programs entry so Windows Settings lists
    /// the product with a working Uninstall button (it runs the installed setup
    /// CLI). This replaces what Inno Setup provided in v0.1.
    /// </summary>
    public static void RegisterUninstallEntry(InstallLayout layout, string version)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("version required", nameof(version));

        var appExe = layout.PathFor(ComponentRegistry.App);
        var setupCli = layout.PathFor(ComponentRegistry.SetupCli);

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create HKCU\\{UninstallKeyPath}.");
        key.SetValue("DisplayName", "AgentEyes");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Soren Frederiksen");
        key.SetValue("InstallLocation", layout.AppDir);
        key.SetValue("DisplayIcon", appExe);
        key.SetValue("UninstallString", $"\"{setupCli}\" uninstall");
        key.SetValue("QuietUninstallString", $"\"{setupCli}\" uninstall --json");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        EngineLog.Write($"[InstallFinalizer] registered Add/Remove Programs entry (v{version})");
    }

    /// <summary>Remove the Add/Remove Programs entry (used on uninstall).</summary>
    public static bool RemoveUninstallEntry()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(Path.GetDirectoryName(UninstallKeyPath)!, writable: true);
        if (parent?.OpenSubKey("AgentEyes") is not { } existing) return false;
        existing.Dispose();
        parent.DeleteSubKeyTree("AgentEyes", throwOnMissingSubKey: false);
        EngineLog.Write("[InstallFinalizer] removed Add/Remove Programs entry");
        return true;
    }
}
