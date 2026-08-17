namespace AgentEyes.Setup.Engine;

/// <summary>
/// Places a staged build over an install target, keeping the previous build as a
/// "<c>.old</c>" backup, and rolls that backup back on demand. A running exe can
/// be swapped this way on Windows (the replace is rename-based; the running image
/// stays mapped), which is how the in-app updater applies itself.
///
/// All operations are plain file moves/replaces, so they are fully testable in a
/// temp directory.
/// </summary>
public static class InstallSwapper
{
    /// <summary>The backup path kept alongside a target ("&lt;target&gt;.old").</summary>
    public static string BackupPathFor(string targetPath) => targetPath + ".old";

    /// <summary>
    /// Make <paramref name="stagedSource"/> become <paramref name="targetPath"/>,
    /// keeping the previous target as "<c>.old</c>". The staged file is copied
    /// onto the target's volume first (as "<c>.new</c>") so the install is never
    /// left without a file if the copy fails. Returns the backup path, or null
    /// when there was no prior target to back up.
    /// </summary>
    public static string? Place(string targetPath, string stagedSource)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath required", nameof(targetPath));
        if (!File.Exists(stagedSource)) throw new FileNotFoundException("Staged source not found.", stagedSource);

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var newPath = targetPath + ".new";
        var old = BackupPathFor(targetPath);

        if (File.Exists(newPath)) File.Delete(newPath);
        File.Copy(stagedSource, newPath);

        if (!File.Exists(targetPath))
        {
            File.Move(newPath, targetPath);
            EngineLog.Write($"[InstallSwapper] Place: fresh install at {targetPath}");
            return null;
        }

        if (File.Exists(old)) File.Delete(old);
        File.Replace(newPath, targetPath, old); // target <- new, old <- previous target
        EngineLog.Write($"[InstallSwapper] Place: swapped {targetPath} (backup {old})");
        return old;
    }

    /// <summary>
    /// Restore the "<c>.old</c>" backup over <paramref name="targetPath"/>.
    /// Returns true if a backup existed and was restored; false if there was
    /// nothing to roll back to.
    /// </summary>
    public static bool Rollback(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath required", nameof(targetPath));
        var old = BackupPathFor(targetPath);
        if (!File.Exists(old))
        {
            EngineLog.Write($"[InstallSwapper] Rollback: no backup at {old}");
            return false;
        }

        if (File.Exists(targetPath))
            File.Replace(old, targetPath, null); // target <- old, old consumed
        else
            File.Move(old, targetPath);

        EngineLog.Write($"[InstallSwapper] Rollback: restored {targetPath} from backup");
        return true;
    }
}
