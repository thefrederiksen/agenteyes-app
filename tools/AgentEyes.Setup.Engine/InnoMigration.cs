namespace AgentEyes.Setup.Engine;

/// <summary>
/// One-time takeover from the Inno Setup v0.1 install. The Inno install lived in
/// the SAME app dir this engine installs to, but as a multi-file publish (~200
/// loose DLLs) plus an unins000.exe/.dat pair and an "_is1" uninstall registry
/// key. When the marker (unins000.exe) is present we remove the Inno uninstall
/// entry and wipe the app dir so the new single-file layout starts clean - no
/// stale runtime DLLs, no dead uninstaller. User data outside app\ is untouched.
/// </summary>
public static class InnoMigration
{
    /// <summary>True when the app dir holds an Inno v0.1 install.</summary>
    public static bool IsInnoInstall(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return File.Exists(Path.Combine(layout.AppDir, "unins000.exe"));
    }

    /// <summary>
    /// Remove the Inno install (registry entry + the whole app dir). Throws when
    /// the dir cannot be deleted (e.g. the app is running) - the caller must stop
    /// the app first; a half-migrated install must not pass silently.
    /// </summary>
    public static void RemoveInnoInstall(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsInnoInstall(layout)) return;

        InstallFinalizer.UserShell.DeleteInnoUninstallEntry();
        EngineLog.Write("[InnoMigration] removed Inno uninstall registry entry");

        Directory.Delete(layout.AppDir, recursive: true);
        EngineLog.Write($"[InnoMigration] removed Inno install dir: {layout.AppDir}");
    }
}
