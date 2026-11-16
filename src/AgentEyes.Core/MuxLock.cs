using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AgentEyes
{
    /// <summary>
    /// Issue #128 round two (D1): ONE writer per recording's final media, across PROCESSES.
    ///
    /// <see cref="RecordingWorkset"/> claims are in-memory, so they cannot see another process.
    /// QA measured exactly that hole: the app's mux (ffmpeg 15332) and an `agenteyes package` mux
    /// (ffmpeg 12196) both writing the same recording.mp4 at the same instant. This lock lives ON
    /// DISK: an exclusive handle (<see cref="FileShare.None"/>) on <see cref="FileName"/> in the
    /// recording directory, held for the whole mux. On Windows a second open is a sharing violation;
    /// on macOS/Linux .NET takes an exclusive advisory flock for FileShare.None, which every AgentEyes
    /// process honours. The OS drops the handle when the holder dies, however it dies, so a killed
    /// mux can never leave a stale lock behind - which is also what makes "nobody holds the lock" a
    /// sound proof that a recorded running attempt is dead (<see cref="MuxCoordinator.Reconcile"/>).
    ///
    /// Re-entrant on the holding thread only, so the coordinator can hold it around a mux while
    /// <see cref="RecordingService.FinalizePending"/> - which takes it for any caller - runs inside.
    /// Another thread in the same process is refused exactly like another process.
    ///
    /// The lock file is deliberately left in place after release: deleting it would reopen the
    /// classic flock race on Unix (a waiter locking an unlinked inode while a third party creates a
    /// fresh file). It is a zero-length file, one per recording that ever muxed.
    /// </summary>
    internal sealed class MuxLock : IDisposable
    {
        /// <summary>The lock file's name inside the recording directory.</summary>
        public const string FileName = "mux.lock";

        private sealed class Held
        {
            public required FileStream Stream { get; init; }
            public required int ThreadId { get; init; }
            public int Depth;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Held> HeldByKey = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _key;
        private bool _disposed;

        private MuxLock(string key) { _key = key; }

        /// <summary>
        /// Takes the lock, or returns null when another process (or another thread of this one)
        /// holds it. Throws when the directory does not exist or the lock file cannot be opened for
        /// any reason other than being held.
        /// </summary>
        public static MuxLock? TryAcquire(string dir, string purpose)
        {
            string key = RecordingWorkset.Key(dir);
            if (!Directory.Exists(key))
                throw new UsageException($"cannot lock the mux for {key}: the recording directory does not exist");

            int me = Environment.CurrentManagedThreadId;
            lock (Gate)
            {
                if (HeldByKey.TryGetValue(key, out var held))
                {
                    if (held.ThreadId != me)
                    {
                        Log.Info($"[MuxLock] TryAcquire: {Path.GetFileName(key)} ({purpose}) - held by another thread of this process");
                        return null;
                    }
                    held.Depth++;
                    Log.Info($"[MuxLock] TryAcquire: {Path.GetFileName(key)} ({purpose}) - re-entered, depth {held.Depth}");
                    return new MuxLock(key);
                }

                FileStream stream;
                try
                {
                    stream = new FileStream(Path.Combine(key, FileName), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                }
                catch (IOException ex) when (IsHeldElsewhere(ex))
                {
                    Log.Info($"[MuxLock] TryAcquire: {Path.GetFileName(key)} ({purpose}) - held by another process: {ex.Message}");
                    return null;
                }

                HeldByKey[key] = new Held { Stream = stream, ThreadId = me, Depth = 1 };
                Log.Info($"[MuxLock] TryAcquire: {Path.GetFileName(key)} ({purpose}) - acquired by pid {Environment.ProcessId}");
                return new MuxLock(key);
            }
        }

        /// <summary>Takes the lock or throws a <see cref="UsageException"/> saying it is held.</summary>
        public static MuxLock Acquire(string dir, string purpose)
        {
            return TryAcquire(dir, purpose) ?? throw new UsageException(
                $"the audio mux for {Path.GetFileName(RecordingWorkset.Key(dir))} is already running in another " +
                $"AgentEyes process (or another task of this one) - refusing to start a second writer on the same " +
                $"output. Wait for it to finish, or for it to fail, then try again.");
        }

        /// <summary>True when THIS thread holds the lock for <paramref name="dir"/>.</summary>
        public static bool IsHeldByCurrentThread(string dir)
        {
            string key = RecordingWorkset.Key(dir);
            lock (Gate)
                return HeldByKey.TryGetValue(key, out var held) && held.ThreadId == Environment.CurrentManagedThreadId;
        }

        /// <summary>
        /// A sharing/lock violation, as opposed to a real I/O failure (a missing path, a full disk),
        /// which must surface rather than read as "busy". Windows: ERROR_SHARING_VIOLATION (32) or
        /// ERROR_LOCK_VIOLATION (33). Unix: .NET reports a held flock as a plain IOException whose
        /// HResult is EWOULDBLOCK (11 on Linux, 35 on macOS).
        /// </summary>
        private static bool IsHeldElsewhere(IOException ex)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) return false;
            int code = ex.HResult & 0xFFFF;
            if (OperatingSystem.IsWindows()) return code == 32 || code == 33;
            return code == 11 || code == 35;
        }

        /// <summary>Releases one level of the lock; the handle closes when the outermost level is released.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Gate)
            {
                if (!HeldByKey.TryGetValue(_key, out var held)) return;
                held.Depth--;
                if (held.Depth > 0) return;
                HeldByKey.Remove(_key);
                held.Stream.Dispose();
                Log.Info($"[MuxLock] Release: {Path.GetFileName(_key)} - released by pid {Environment.ProcessId}");
            }
        }
    }
}
