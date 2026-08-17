using System.IO.Compression;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// Installs an archive component: extracts a verified .zip to a temp folder and
/// swaps each contained file into the app dir (flat - the ffmpeg bundle is just
/// ffmpeg.exe + ffprobe.exe), keeping per-file ".old" backups via
/// <see cref="InstallSwapper"/>. cc-director's generic runner skips archives
/// (its only zip is gateway-side); AgentEyes's ffmpeg bundle is a first-class
/// per-user component, so the engine handles it here.
/// </summary>
public static class ArchiveInstaller
{
    /// <summary>
    /// Extract <paramref name="stagedZip"/> and place every file entry into
    /// <paramref name="targetDir"/>. Nested directories inside the zip are
    /// flattened deliberately: the bundle contract is "loose exes next to ours".
    /// Throws on the first failure - a half-placed archive must surface, not pass.
    /// </summary>
    public static void Place(string targetDir, string stagedZip)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) throw new ArgumentException("targetDir required", nameof(targetDir));
        if (!File.Exists(stagedZip)) throw new FileNotFoundException("Staged archive not found.", stagedZip);

        var extractDir = Path.Combine(Path.GetTempPath(), $"agenteyes-setup-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            ZipFile.ExtractToDirectory(stagedZip, extractDir);
            var files = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
                throw new InvalidDataException($"Archive contains no files: {stagedZip}");

            Directory.CreateDirectory(targetDir);
            foreach (var file in files)
            {
                var target = Path.Combine(targetDir, Path.GetFileName(file));
                InstallSwapper.Place(target, file);
            }
            EngineLog.Write($"[ArchiveInstaller] placed {files.Length} file(s) into {targetDir}");
        }
        finally
        {
            try { Directory.Delete(extractDir, recursive: true); }
            catch (Exception ex) { EngineLog.Write($"[ArchiveInstaller] temp cleanup failed: {ex.Message}"); }
        }
    }
}
