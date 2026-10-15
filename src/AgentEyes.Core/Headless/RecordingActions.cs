using System;
using System.IO;

namespace AgentEyes.Headless
{
    /// <summary>
    /// The library changes the headless Control API makes for a desktop client (issue #107): rename and
    /// delete a recording. The service is the only process that writes recording folders on Linux, so
    /// the Linux app sends these here instead of touching the folders itself - the same guard the
    /// Windows app applies in-process (RecordingWorkset) then covers capture and post-processing.
    /// </summary>
    internal static class RecordingActions
    {
        /// <summary>Set the recording's display name in its manifest. Returns the folder.</summary>
        public static string Rename(string id, string name, string? root = null)
        {
            Log.Info($"[RecordingActions] Rename: id={id} name={name}");
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) throw new UsageException("the new name is empty");
            string dir = RequireDir(id, root);
            ManifestStore.Update(dir, m => m.DisplayName = trimmed);
            Log.Info($"[RecordingActions] Rename: done dir={dir}");
            return dir;
        }

        /// <summary>
        /// Delete the recording folder and everything in it. Refused while the recording is being
        /// captured or processed: those steps hold files open inside it and a delete would half-fail.
        /// </summary>
        public static string Delete(string id, string? root = null)
        {
            Log.Info($"[RecordingActions] Delete: id={id}");
            string dir = RequireDir(id, root);
            if (RecordingWorkset.IsClaimed(dir))
            {
                Log.Info($"[RecordingActions] Delete: refused, held by {RecordingWorkset.OwnerDescription(dir)}");
                throw new RecordingBusyException("this recording is still being recorded or processed - try again when it is done");
            }
            Directory.Delete(dir, recursive: true);
            Log.Info($"[RecordingActions] Delete: done dir={dir}");
            return dir;
        }

        /// <summary>The folder of a recording id, which must be a plain folder name under the root.</summary>
        internal static string RequireDir(string id, string? root = null)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id == "." || id == "..")
                throw new UsageException("not a recording id: " + id);
            string dir = Path.Combine(root ?? RecordingPaths.Root, id);
            if (!File.Exists(Path.Combine(dir, "manifest.json")))
                throw new RecordingNotFoundException("no recording with id: " + id);
            return dir;
        }
    }

    /// <summary>The recording is held by a capture or a post-processing step (HTTP 409).</summary>
    internal sealed class RecordingBusyException : Exception
    {
        public RecordingBusyException(string message) : base(message) { }
    }

    /// <summary>No recording folder with that id (HTTP 404).</summary>
    internal sealed class RecordingNotFoundException : Exception
    {
        public RecordingNotFoundException(string message) : base(message) { }
    }
}
