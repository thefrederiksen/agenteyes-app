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
    /// <summary>Where the user PATH and DOTNET_BUNDLE_EXTRACT_BASE_DIR are read and written. The
    /// user's registry in the product; a test host swaps in an in-memory store so a test run never
    /// changes the variables the installed app depends on (issue #78).</summary>
    internal static IUserEnvironment UserEnvironment { get; set; } = new RegistryUserEnvironment();

    /// <summary>Where the shortcuts, the run-at-login value and the Add/Remove Programs entry are read and
    /// written. The user's shell folders and HKCU in the product; a test host swaps in an in-memory store so
    /// no test - whatever the code under test does - can create, repoint or delete the real ones (issue #200).</summary>
    internal static IUserShell UserShell { get; set; } = new WindowsUserShell();

    // ---- PATH ---------------------------------------------------------------

    /// <summary>Add the app dir to the user PATH if not already present. Returns true if it changed.</summary>
    public static bool AddAppDirToPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = UserEnvironment.Get("Path") ?? "";
        var updated = ComputePathWith(current, layout.AppDir);
        if (updated == current) return false;
        // SetEnvironmentVariable(User) persists to the registry and broadcasts WM_SETTINGCHANGE, so new
        // processes pick it up (existing shells still need to be reopened).
        UserEnvironment.Set("Path", updated);
        EngineLog.Write($"[InstallFinalizer] added to PATH: {layout.AppDir}");
        return true;
    }

    /// <summary>Remove the app dir from the user PATH. Returns true if it changed.</summary>
    public static bool RemoveAppDirFromPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = UserEnvironment.Get("Path") ?? "";
        var updated = ComputePathWithout(current, layout.AppDir);
        if (updated == current) return false;
        UserEnvironment.Set("Path", updated);
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
        var current = UserEnvironment.Get("Path") ?? "";
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
        var current = UserEnvironment.Get(BundleExtractBaseDirVariable);
        if (IsSameDirectory(current, layout.BundleExtractDir))
        {
            EngineLog.Write($"[InstallFinalizer] {BundleExtractBaseDirVariable} already set: {layout.BundleExtractDir}");
            return false;
        }
        // SetEnvironmentVariable(User) persists to the registry and broadcasts WM_SETTINGCHANGE,
        // so Explorer-launched processes (shortcuts, the Run-key autostart) inherit it; a terminal
        // opened before the install still needs reopening, exactly as for the PATH entry above.
        UserEnvironment.Set(BundleExtractBaseDirVariable, layout.BundleExtractDir);
        EngineLog.Write($"[InstallFinalizer] set {BundleExtractBaseDirVariable}={layout.BundleExtractDir}");
        return true;
    }

    /// <summary>True when the user variable points at this install's bundle extraction dir.</summary>
    public static bool IsBundleExtractBaseDirSet(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = UserEnvironment.Get(BundleExtractBaseDirVariable);
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
        UserEnvironment.Set(BundleExtractBaseDirVariable, null);
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
    public static string StartMenuShortcutPath() => UserShell.StartMenuShortcutPath;

    /// <summary>Desktop shortcut path.</summary>
    public static string DesktopShortcutPath() => UserShell.DesktopShortcutPath;

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

        UserShell.WriteShortcut(lnk, exe);
        EngineLog.Write($"[InstallFinalizer] created shortcut: {lnk}");
        return true;
    }

    // ---- run at login --------------------------------------------------------

    /// <summary>Enable/disable run-at-login (per-user Run key; same value the app's own toggle uses).</summary>
    public static void SetAutostart(InstallLayout layout, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (enabled)
        {
            var exe = layout.PathFor(ComponentRegistry.App);
            UserShell.SetRunValue($"\"{exe}\" --tray");
            EngineLog.Write("[InstallFinalizer] autostart enabled");
        }
        else if (UserShell.GetRunValue() != null)
        {
            UserShell.SetRunValue(null);
            EngineLog.Write("[InstallFinalizer] autostart removed");
        }
    }

    /// <summary>True when the run-at-login Run key is set.</summary>
    public static bool IsAutostartEnabled() => UserShell.GetRunValue() != null;

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

        UserShell.WriteUninstallEntry(new UninstallEntry(
            DisplayName: "AgentEyes",
            DisplayVersion: version,
            Publisher: "Soren Frederiksen",
            InstallLocation: layout.AppDir,
            DisplayIcon: appExe,
            UninstallString: $"\"{setupCli}\" uninstall",
            QuietUninstallString: $"\"{setupCli}\" uninstall --json"));
        EngineLog.Write($"[InstallFinalizer] registered Add/Remove Programs entry (v{version})");
    }

    /// <summary>Remove the Add/Remove Programs entry (used on uninstall).</summary>
    public static bool RemoveUninstallEntry()
    {
        if (!UserShell.UninstallEntryExists()) return false;
        UserShell.DeleteUninstallEntry();
        EngineLog.Write("[InstallFinalizer] removed Add/Remove Programs entry");
        return true;
    }
}
