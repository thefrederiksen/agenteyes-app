using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// One-time rename-era migration: the product used to be "qa-record" and stored its state under
    /// qa-record-named folders. On startup, if an old folder exists and the new one does not, the old
    /// folder is MOVED (config, presets, logs, the downloaded Whisper model, and recordings all carry
    /// over - no re-download, no lost presets). If both exist the new one wins and the old is left
    /// alone for manual cleanup. A failed move throws - the caller surfaces it loudly.
    /// </summary>
    internal static class StorageMigration
    {
        public static void Run()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            Migrate(Path.Combine(local, "qa-record"), Path.Combine(local, "AgentEyes"));
            Migrate(Path.Combine(videos, "qa-record"), Path.Combine(videos, "AgentEyes"));
        }

        private static void Migrate(string oldDir, string newDir)
        {
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
            Directory.Move(oldDir, newDir);
            Log.Info($"storage migrated: {oldDir} -> {newDir}");
        }
    }
}
