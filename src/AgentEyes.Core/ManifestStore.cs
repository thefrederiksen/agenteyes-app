using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentEyes
{
    /// <summary>
    /// The only place manifest.json is written (issue #155).
    ///
    /// Before this, every caller did its own <c>File.WriteAllText</c> over the live file through
    /// <c>Manifest.Save</c>, which had three defects:
    ///
    ///  - **Not atomic.** A crash, a kill, or a full disk mid-write left truncated JSON, and a
    ///    truncated manifest throws out of every reader - the Library skips the recording and every
    ///    recovery pass loses it.
    ///  - **Lost updates.** A caller that loaded a manifest, did minutes of work, and saved its own
    ///    in-memory copy silently erased anything another path wrote in between (the packaging pass
    ///    versus a rename in the Library was the concrete case).
    ///  - **No single mutation path.** Nothing could be enforced, because there was nowhere to
    ///    enforce it.
    ///
    /// The two operations are deliberately different, and picking the wrong one is the bug this
    /// class exists to prevent:
    ///
    ///  - <see cref="Update"/> - read-modify-write. The manifest is loaded INSIDE the lock,
    ///    immediately before the write, so the mutation applies to whatever is on disk now. Every
    ///    caller that changes SOME fields of an existing recording uses this.
    ///  - <see cref="Replace"/> - the caller's object IS the whole content. Only for a manifest that
    ///    is not derived from a concurrent on-disk read: a capture session writing its own record, an
    ///    import creating a directory, the reduced recovery record (issue #153).
    ///
    /// Scope of the guarantee: IN-PROCESS. The lock serializes writers inside one AgentEyesApp /
    /// agenteyes process; the app is single-instance (App.xaml.cs), but the CLI can run beside it, so
    /// two PROCESSES writing one recording are still uncoordinated. The atomic replace bounds what
    /// that can do - the loser's whole write is overwritten, but the file is never torn - and a
    /// cross-process lock is explicitly out of scope for #155.
    /// </summary>
    internal static class ManifestStore
    {
        /// <summary>The one file name. A recording IS a directory with this in it.</summary>
        public const string FileName = "manifest.json";

        /// <summary>
        /// One lock object per recording directory. Keyed on the full path, case-insensitively,
        /// because Windows paths are case-insensitive and two spellings of one directory must take
        /// the same lock. Entries are never removed: one small object per recording directory this
        /// process has written, which is bounded by the number of recordings it touches.
        /// (Canonical-path handling elsewhere in the app - RecordingWorkset still keys on the raw
        /// string - is issue #154 and is not touched here.)
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> Locks =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Test seam (issue #155): invoked after the temporary file holds the complete new manifest
        /// and BEFORE it replaces manifest.json. Null in production - the only code that sets it is
        /// the test proving an interrupted write leaves the original intact and parseable. Follows
        /// the replaceable-step pattern issue #152 used for the post-recording stages.
        /// </summary>
        internal static Action<string>? InterruptBeforeReplace;

        /// <summary>
        /// Load the manifest in <paramref name="dir"/>, apply <paramref name="mutate"/> to it, and
        /// write it back atomically - all under the directory's lock, so no other writer in this
        /// process can read, mutate or write the same manifest in between. THIS is the canonical
        /// mutation path: a caller changing an existing recording must not save an object it loaded
        /// earlier, because everything written to that manifest since would be erased.
        /// </summary>
        /// <returns>The manifest as it was written, so the caller can read what it now says.</returns>
        public static Manifest Update(string dir, Action<Manifest> mutate)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));

            Log.Info($"[ManifestStore] Update: dir={dir}");
            lock (LockFor(dir))
            {
                var manifest = Manifest.Load(dir);
                mutate(manifest);
                WriteAtomic(dir, manifest);
                // Exit is logged as well as entry, so the log shows which writes COMPLETED - an
                // entry with no exit is a write that threw or a process that died mid-write, and
                // telling those apart is the whole point of this class. There is deliberately no
                // catch here: this repo's standards put try-catch at the entry points, and each of
                // them - RecordingStopSequence, the two rename handlers, the repair passes - logs
                // the failure with the context this method does not have.
                Log.Info($"[ManifestStore] Update: dir={dir} written");
                return manifest;
            }
        }

        /// <summary>
        /// Write <paramref name="manifest"/> as the WHOLE content of the recording's manifest.json,
        /// atomically. Only for a manifest whose content this caller owns outright - a capture
        /// session's own record, a newly created import/bare-video directory, the reduced recovery
        /// record. Anything that only wants to change some fields of an existing recording must use
        /// <see cref="Update"/> instead, or it erases concurrent writes (and any unknown properties
        /// on disk that it never read).
        /// </summary>
        public static void Replace(string dir, Manifest manifest)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            Log.Info($"[ManifestStore] Replace: dir={dir}");
            lock (LockFor(dir))
            {
                WriteAtomic(dir, manifest);
            }
            Log.Info($"[ManifestStore] Replace: dir={dir} written");
        }

        /// <summary>
        /// Serialize to a temporary file in the SAME directory (so the replace is a rename inside one
        /// volume), flush it to the physical disk, then rename it over manifest.json.
        ///
        /// The rename is the atomic step: NTFS makes a replacing rename an all-or-nothing metadata
        /// operation, so a reader sees either the whole old file or the whole new one - never a
        /// truncated one. Flushing the temp before the rename is what makes that true after a power
        /// loss as well as after a process kill: the bytes are on the disk before anything points at
        /// them. <see cref="File.Move(string, string, bool)"/> rather than
        /// <see cref="File.Replace(string, string, string)"/> because Replace requires the destination
        /// to already exist, and the first write of a recording has no destination yet.
        ///
        /// The temporary name is unique per write, so a leftover temp from a killed process can never
        /// be picked up and completed by a later one, and two writers can never share a half-written
        /// temp. A failed rename deletes the temp it created and rethrows: the caller learns the write
        /// failed, the original file is untouched, and no litter is left behind.
        /// </summary>
        private static void WriteAtomic(string dir, Manifest manifest)
        {
            string path = Path.Combine(dir, FileName);
            string temp = Path.Combine(dir, $"{FileName}.{Guid.NewGuid():N}.tmp");

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Manifest.JsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            InterruptBeforeReplace?.Invoke(temp);

            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // Cleanup, not a fallback: the write has FAILED and the caller is told so. Deleting
                // the temp only stops a failed write from leaving a stray file in the recording.
                if (File.Exists(temp)) File.Delete(temp);
                throw;
            }
        }

        private static object LockFor(string dir) =>
            Locks.GetOrAdd(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), _ => new object());
    }
}
