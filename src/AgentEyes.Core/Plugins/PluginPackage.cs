using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentEyes.Plugins
{
    /// <summary>
    /// Pure plugin install/remove logic, shared by registry installs and local
    /// "upload a file" installs (issue #61). No UI, no network, no config - just: take a
    /// plugin package (zip bytes or an unpacked folder), validate it (plugin.json present,
    /// no zip-slip, optional SHA-256), and place it at pluginsRoot\&lt;id&gt;. Lives in Core
    /// so it is unit-testable. Throws with an exact reason on any failure (no-fallback rule).
    /// </summary>
    internal static class PluginPackage
    {
        /// <summary>
        /// Install from zip bytes. When <paramref name="expectedSha256"/> is non-null the
        /// bytes must hash to it (registry installs verify; local file installs pass null).
        /// Returns the installed plugin id (read from plugin.json, not the file name).
        /// </summary>
        public static string InstallZip(byte[] zipBytes, string pluginsRoot, string? expectedSha256 = null)
        {
            if (expectedSha256 != null)
            {
                string actual = Convert.ToHexString(SHA256.HashData(zipBytes));
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"SHA-256 mismatch: expected {expectedSha256}, the file is {actual}. Refusing to install.");
            }

            Directory.CreateDirectory(pluginsRoot);
            string staging = Path.Combine(pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                string root = Path.GetFullPath(staging);
                using (var ms = new MemoryStream(zipBytes))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    // Reject entries that escape the staging folder (zip-slip) BEFORE extracting.
                    foreach (var entry in archive.Entries)
                    {
                        string full = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"zip entry escapes the plugin folder: {entry.FullName}");
                    }
                    archive.ExtractToDirectory(staging);
                }

                string source = ResolveManifestRoot(staging);
                string id = ReadId(source);
                return PlaceFrom(source, pluginsRoot, id, copy: false);
            }
            finally
            {
                TryDelete(staging);
            }
        }

        /// <summary>Install from a .zip on disk (the "Install from file" path).</summary>
        public static string InstallZipFile(string zipPath, string pluginsRoot)
            => InstallZip(File.ReadAllBytes(zipPath), pluginsRoot);

        /// <summary>
        /// Install from an unpacked folder (must contain plugin.json at its root or one
        /// level down). The user's source is copied, never moved. Returns the installed id.
        /// </summary>
        public static string InstallFolder(string sourceDir, string pluginsRoot)
        {
            if (!Directory.Exists(sourceDir))
                throw new InvalidOperationException($"folder not found: {sourceDir}");
            string source = ResolveManifestRoot(sourceDir);
            string id = ReadId(source);
            Directory.CreateDirectory(pluginsRoot);
            return PlaceFrom(source, pluginsRoot, id, copy: true);
        }

        /// <summary>Delete an installed plugin folder and its per-machine settings file.</summary>
        public static void Remove(string pluginsRoot, string id)
        {
            string dir = Path.Combine(pluginsRoot, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            string settings = Path.Combine(pluginsRoot, id + ".settings.json");
            if (File.Exists(settings)) File.Delete(settings);
        }

        // ---- helpers -------------------------------------------------------

        /// <summary>The directory that actually holds plugin.json: the given dir if it has
        /// one, else its single subdirectory if THAT has one - so a zip of the folder (the
        /// common mistake) installs as cleanly as a zip of its contents. Throws if neither.</summary>
        internal static string ResolveManifestRoot(string dir)
        {
            if (File.Exists(Path.Combine(dir, "plugin.json"))) return dir;
            var subs = Directory.GetDirectories(dir);
            if (subs.Length == 1 && File.Exists(Path.Combine(subs[0], "plugin.json"))) return subs[0];
            throw new InvalidOperationException(
                "the package has no plugin.json at its root - a plugin folder must contain plugin.json.");
        }

        /// <summary>Read and folder-name-validate the id from a manifest directory's plugin.json.</summary>
        internal static string ReadId(string manifestDir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifestDir, "plugin.json")));
            string id = doc.RootElement.TryGetProperty("id", out var v) ? (v.GetString() ?? "").Trim() : "";
            if (id.Length == 0)
                throw new InvalidOperationException("plugin.json has no \"id\".");
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                    throw new InvalidOperationException(
                        $"plugin id \"{id}\" has characters that are not allowed in a folder name.");
            return id;
        }

        private static string PlaceFrom(string source, string pluginsRoot, string id, bool copy)
        {
            string dest = Path.Combine(pluginsRoot, id);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            if (copy) CopyDir(source, dest);
            else Directory.Move(source, dest);
            return id;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
