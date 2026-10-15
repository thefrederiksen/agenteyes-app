using System;
using System.IO;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// The Linux protection for the stored DevThrottle credential (issue #102, assumption A3): a
    /// file only its owner can read or write (0600), in a folder only its owner can enter (0700).
    /// There is no DPAPI on Linux; the file mode is the protection, the same contract ssh keeps for
    /// ~/.ssh/id_*. It is written to a temporary name created 0600 from the first byte and renamed
    /// into place, so the key is never on disk under a wider mode even for an instant.
    /// </summary>
    internal static class CredentialFile
    {
        public const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        public const UnixFileMode OwnerOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        private const UnixFileMode GroupOrOther =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        public static void Write(string path, byte[] content)
        {
            Log.Info($"[CredentialFile] Write: {path}");
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, File.GetUnixFileMode(dir) & ~GroupOrOther);
            }

            string tmp = path + ".tmp";
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var fs = new FileStream(tmp, options))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            Log.Info($"[CredentialFile] Write: done, mode={Describe(path)}");
        }

        /// <summary>Throws when the file is readable or writable by anyone but its owner.</summary>
        public static void RequirePrivate(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            var mode = File.GetUnixFileMode(path);
            if ((mode & GroupOrOther) != 0)
                throw new InvalidOperationException(
                    $"{path} is readable by other users (mode {Describe(path)}). It holds your DevThrottle key, so it "
                    + $"is not used. Fix it with: chmod 600 \"{path}\" (or sign in again: agenteyes signin).");
        }

        /// <summary>The file's permission bits in octal, e.g. "600".</summary>
        public static string Describe(string path)
        {
            if (OperatingSystem.IsWindows()) return "(windows acl)";
            return Convert.ToString((int)File.GetUnixFileMode(path) & 0x1FF, 8);
        }
    }
}
