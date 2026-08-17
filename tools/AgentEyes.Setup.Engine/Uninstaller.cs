namespace AgentEyes.Setup.Engine;

/// <summary>The kind of thing an uninstall step removes.</summary>
public enum UninstallKind { Directory, PathEntry, Shortcut, RunKey, ArpEntry, SetupState, EnvVar }

/// <summary>One thing the uninstaller would remove, with whether it is currently present.</summary>
public sealed record UninstallTarget(UninstallKind Kind, string Description, string Path, bool Present);

/// <summary>Result of an uninstall run.</summary>
public sealed record UninstallReport(bool Success, IReadOnlyList<string> Steps, IReadOnlyList<string> Errors);

/// <summary>
/// Removes exactly the files the installer creates - and nothing else: the app
/// dir, the setup bookkeeping, the native extraction cache and its
/// DOTNET_BUNDLE_EXTRACT_BASE_DIR variable, the PATH entry, the shortcuts, the
/// run-at-login key, and the Add/Remove Programs entry. It NEVER deletes the per-user root
/// itself, so user data that lives alongside the install (config.json, presets,
/// recordings metadata, Whisper models) is preserved.
/// </summary>
public sealed class Uninstaller
{
    private readonly InstallLayout _layout;

    public Uninstaller(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    /// <summary>What an uninstall would remove (existence-checked). Pure: no side effects.</summary>
    public IReadOnlyList<UninstallTarget> Plan()
    {
        var startMenu = InstallFinalizer.StartMenuShortcutPath();
        var desktop = InstallFinalizer.DesktopShortcutPath();
        return
        [
            new UninstallTarget(UninstallKind.Directory, "App binaries", _layout.AppDir, Directory.Exists(_layout.AppDir)),
            new UninstallTarget(UninstallKind.SetupState, "Setup bookkeeping", _layout.SetupStateDir, Directory.Exists(_layout.SetupStateDir)),
            new UninstallTarget(UninstallKind.Directory, "Native extraction cache", _layout.BundleExtractDir, Directory.Exists(_layout.BundleExtractDir)),
            new UninstallTarget(UninstallKind.PathEntry, "PATH entry", _layout.AppDir, InstallFinalizer.IsAppDirOnPath(_layout)),
            new UninstallTarget(UninstallKind.EnvVar, $"{InstallFinalizer.BundleExtractBaseDirVariable} variable", _layout.BundleExtractDir, InstallFinalizer.IsBundleExtractBaseDirSet(_layout)),
            new UninstallTarget(UninstallKind.Shortcut, "Start Menu shortcut", startMenu, File.Exists(startMenu)),
            new UninstallTarget(UninstallKind.Shortcut, "Desktop shortcut", desktop, File.Exists(desktop)),
            new UninstallTarget(UninstallKind.RunKey, "Run-at-login entry", @"HKCU\...\Run\AgentEyes", InstallFinalizer.IsAutostartEnabled()),
            new UninstallTarget(UninstallKind.ArpEntry, "Add/Remove Programs entry", @"HKCU\...\Uninstall\AgentEyes", true),
        ];
    }

    /// <summary>Remove everything in scope. Best-effort: collects per-step errors.</summary>
    public UninstallReport Apply()
    {
        var steps = new List<string>();
        var errors = new List<string>();
        EngineLog.Write("[Uninstaller] Apply");

        RemoveAppDir(steps, errors);
        RemoveSetupState(steps, errors);

        Try(steps, errors, "PATH entry", () =>
            InstallFinalizer.RemoveAppDirFromPath(_layout) ? $"removed PATH entry: {_layout.AppDir}" : "PATH entry: not present");

        // Clear the variable BEFORE deleting the directory it points at, so nothing this
        // uninstall launches afterwards re-populates it.
        Try(steps, errors, $"{InstallFinalizer.BundleExtractBaseDirVariable} variable", () =>
            InstallFinalizer.RemoveBundleExtractBaseDir(_layout)
                ? $"removed {InstallFinalizer.BundleExtractBaseDirVariable} variable"
                : $"{InstallFinalizer.BundleExtractBaseDirVariable} variable: not set to this install");
        DeleteDirectoryWithRetry(_layout.BundleExtractDir, "Native extraction cache", steps, errors);

        RemoveShortcut(InstallFinalizer.StartMenuShortcutPath(), "Start Menu shortcut", steps, errors);
        RemoveShortcut(InstallFinalizer.DesktopShortcutPath(), "desktop shortcut", steps, errors);

        Try(steps, errors, "run-at-login entry", () =>
        {
            InstallFinalizer.SetAutostart(_layout, enabled: false);
            return "removed run-at-login entry (if present)";
        });

        Try(steps, errors, "Add/Remove Programs entry", () =>
            InstallFinalizer.RemoveUninstallEntry() ? "removed Add/Remove Programs entry" : "Add/Remove Programs entry: not present");

        var ok = errors.Count == 0;
        EngineLog.Write($"[Uninstaller] Apply done: success={ok}, errors={errors.Count}");
        return new UninstallReport(ok, steps, errors);
    }

    /// <summary>
    /// Delete the app dir, retrying briefly: when the uninstall is driven by a
    /// relaunched temp copy of agenteyes-setup.exe, the original exe inside the dir
    /// needs a moment to exit before it can be deleted.
    /// </summary>
    private void RemoveAppDir(List<string> steps, List<string> errors)
    {
        var path = _layout.AppDir;

        // Hard guard: never delete the per-user root itself.
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_layout.LocalRoot), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"refused to delete the per-user root ({path})");
            return;
        }
        DeleteDirectoryWithRetry(path, "App binaries", steps, errors);
    }

    /// <summary>
    /// Delete a directory, retrying briefly: files inside can be held open for a
    /// moment by a process that is on its way out (the original agenteyes-setup.exe
    /// exiting after it relaunched a temp copy of itself to run this uninstall).
    /// </summary>
    private static void DeleteDirectoryWithRetry(string path, string desc, List<string> steps, List<string> errors)
    {
        if (!Directory.Exists(path)) { steps.Add($"{desc}: not present ({path})"); return; }

        const int attempts = 10;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                steps.Add($"removed {desc}: {path}");
                return;
            }
            catch (Exception ex) when (i < attempts)
            {
                EngineLog.Write($"[Uninstaller] {desc} delete attempt {i} failed: {ex.Message}; retrying");
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                errors.Add($"{desc} ({path}): {ex.Message}");
                return;
            }
        }
    }

    private void RemoveSetupState(List<string> steps, List<string> errors)
    {
        var path = _layout.SetupStateDir;
        if (!Directory.Exists(path)) { steps.Add($"Setup bookkeeping: not present ({path})"); return; }
        try
        {
            Directory.Delete(path, recursive: true);
            steps.Add($"removed Setup bookkeeping: {path}");
        }
        catch (Exception ex) { errors.Add($"Setup bookkeeping ({path}): {ex.Message}"); }
    }

    private static void RemoveShortcut(string lnk, string desc, List<string> steps, List<string> errors)
    {
        if (!File.Exists(lnk)) { steps.Add($"{desc}: not present"); return; }
        try { File.Delete(lnk); steps.Add($"removed {desc}: {lnk}"); }
        catch (Exception ex) { errors.Add($"{desc} ({lnk}): {ex.Message}"); }
    }

    private static void Try(List<string> steps, List<string> errors, string what, Func<string> action)
    {
        try { steps.Add(action()); }
        catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
    }
}
