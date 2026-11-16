using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AgentEyes
{
    /// <summary>What <see cref="CaptureLock.Inspect"/> found on a recording directory (issue #156).</summary>
    internal sealed class CaptureLiveness
    {
        public CaptureLiveness(bool live, string reason)
        {
            Live = live;
            Reason = reason;
        }

        /// <summary>True when a capture is (or may still be) writing into the directory, so nothing
        /// else may mux, rename or publish its files.</summary>
        public bool Live { get; }

        /// <summary>Why - naming the holder when there is one. Always set, so a decline and a
        /// takeover both say what they saw.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Issue #156: a capture's claim on its recording directory, visible to OTHER PROCESSES - and to
    /// other machines on a shared library, within the limits stated below.
    ///
    /// <see cref="RecordingWorkset"/>'s capture claim is in-memory, so a second AgentEyes process (the
    /// installed service beside the CLI, a second service) saw "no claim" on a directory that was
    /// still being captured into. The repair pass then admitted it, and `agenteyes package` muxed it:
    /// one burned attempt on a plain MP4, and - once #113's fragmented container is readable mid-
    /// capture - a published prefix, <c>raw.mp4</c> renamed out from under the live ffmpeg, and the
    /// owner's path-based watchdog killing the healthy capture 45 s later.
    ///
    /// THE MECHANISM. Two files in the recording directory:
    ///
    ///  - <see cref="FileName"/> (<c>capture.lock</c>): an exclusive handle (<see cref="FileShare.None"/>)
    ///    held by the capturing process from the moment the capture claims the directory until the
    ///    claim is released. The same primitive <see cref="MuxLock"/> uses: on Windows a second open is
    ///    a sharing violation; on macOS and Linux .NET takes an exclusive flock, which every AgentEyes
    ///    process honours. The OS drops it when the holder dies, however it dies.
    ///
    ///  - <see cref="OwnerFileName"/> (<c>capture.owner.json</c>): who holds it - host, pid, process start
    ///    time - and the pid and start time of every ffmpeg WRITER the capture started. Written by the
    ///    holder only, atomically (temp file + rename), and read by anyone. It is the description, not
    ///    the claim: a held lock is live whatever the record says.
    ///
    /// WHY THE WRITERS ARE PART OF THE CLAIM. A lock held by the AgentEyes process proves only that
    /// THAT process is alive. Measured on macOS (issue #156): a capture ffmpeg whose parent is
    /// SIGKILLed is reparented to pid 1 and keeps writing - 3.6 MB to 13.1 MB in the 8 s after its
    /// owner died. A lock-only guard would hand that still-growing file to the next repair pass, which
    /// is the gate's defect by another route. So when the lock is free, the claim is still LIVE while
    /// any recorded writer on this host is provably the same process (pid AND start time, the test
    /// <see cref="Video.FfmpegChildRegistry"/> uses). Every writer is also registered with that
    /// registry, so the next service start reaps an ownerless capture ffmpeg - which is what lets a
    /// crashed host's recording be recovered rather than declined forever.
    ///
    /// WHAT MAKES A CLAIM STALE, RATHER THAN MERELY OLD. Age plays no part - a capture may run for
    /// hours. A claim is stale exactly when (1) <c>capture.lock</c> can be taken, which the OS allows
    /// only once its holder's handle is closed, i.e. the holder released it or died, and (2) no writer
    /// recorded for this host is still the same running process. Both are proofs, not timeouts.
    ///
    /// ACROSS TWO HOSTS ON A SHARED LIBRARY. The lock is honoured across machines where the file
    /// system carries the lock: SMB enforces the Windows share mode server-side for every client, and
    /// flock over NFS is mapped to NFS byte-range locks on Linux. A file system that does not carry
    /// locks across clients reduces this to the per-machine guarantee, and that limit is not
    /// detectable from here. A writer recorded by ANOTHER host cannot be probed, so a free lock with a
    /// foreign writer is reported LIVE with the exact fix rather than guessed dead - and that host's
    /// own repair pass, which can probe it, takes it over once the writer is gone.
    ///
    /// Not re-entrant: a capture holds it for its whole life, and any second acquisition - another
    /// thread of this process included - is refused.
    /// </summary>
    internal sealed class CaptureLock : IDisposable
    {
        /// <summary>The lock file's name inside the recording directory.</summary>
        public const string FileName = "capture.lock";

        /// <summary>The holder's description, beside the lock.</summary>
        public const string OwnerFileName = "capture.owner.json";

        /// <summary>The holder's description, as written to <see cref="OwnerFileName"/>.</summary>
        internal sealed class OwnerRecord
        {
            public string Host { get; set; } = "";
            /// <summary>This machine's AgentEyes host id (<see cref="ThisHostId"/>). Survives a
            /// hostname change, which macOS makes when the network changes. Null in a record written
            /// by something that has no id; the name alone decides then.</summary>
            public string? HostId { get; set; }
            public int Pid { get; set; }
            public DateTime ProcessStartUtc { get; set; }
            public string What { get; set; } = "";
            public DateTime StartedUtc { get; set; }
            public List<WriterRecord> Writers { get; set; } = new();

            public override string ToString() =>
                $"{What} of pid {Pid} on host {Host} (started {StartedUtc:o})";
        }

        /// <summary>One ffmpeg the capture started, which writes straight into the directory.</summary>
        internal sealed class WriterRecord
        {
            public string Label { get; set; } = "";
            public int Pid { get; set; }
            public DateTime StartUtc { get; set; }
        }

        /// <summary>TEST SEAM: true when <c>pid</c> is still the process that started at the given
        /// UTC time. Production: <see cref="Video.FfmpegChildRegistry.IsSameProcess"/>.</summary>
        internal static Func<int, DateTime, bool> IsSameProcess = Video.FfmpegChildRegistry.IsSameProcess;

        /// <summary>TEST SEAM: this machine's name, compared with a record's <see cref="OwnerRecord.Host"/>.</summary>
        internal static Func<string> HostName = () => Environment.MachineName;

        /// <summary>TEST SEAM: this machine's host id. Production: <see cref="ReadOrCreateHostId"/>.</summary>
        internal static Func<string> ThisHostId = ReadOrCreateHostId;

        /// <summary>The file under <see cref="AppDataPaths.Root"/> holding this machine's host id.</summary>
        public const string HostIdFileName = "host-id";

        private static readonly object Gate = new();
        private static readonly HashSet<string> HeldKeys = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _key;
        private readonly FileStream _stream;
        private readonly OwnerRecord _owner;
        private bool _disposed;

        private CaptureLock(string key, FileStream stream, OwnerRecord owner)
        {
            _key = key;
            _stream = stream;
            _owner = owner;
        }

        /// <summary>The directory this lock is on (normalized).</summary>
        public string Directory => _key;

        /// <summary>
        /// Issue #156 (G1): the holder as the manifest records it - host, pid, process start time -
        /// taken from the same record <see cref="OwnerFileName"/> publishes, so the two never differ.
        /// </summary>
        public Manifest.CaptureOwnerInfo OwnerForManifest()
        {
            var info = new Manifest.CaptureOwnerInfo { Host = _owner.Host, Pid = _owner.Pid, ProcessStartUtc = _owner.ProcessStartUtc };
            Log.Info($"[CaptureLock] OwnerForManifest: {Path.GetFileName(_key)} - pid {info.Pid} on {info.Host} (process started {info.ProcessStartUtc:o})");
            return info;
        }

        /// <summary>
        /// The capture side: take the claim for <paramref name="dir"/> and publish who holds it.
        /// Returns null - with <paramref name="refusal"/> naming the holder - when the claim is live
        /// elsewhere (another process holds the lock, or a dead owner's writer is still running).
        /// Throws when the directory does not exist or the lock cannot be opened for a reason other
        /// than being held.
        /// </summary>
        public static CaptureLock? TryAcquire(string dir, string what, out string refusal)
        {
            string key = RecordingWorkset.Key(dir);
            Log.Info($"[CaptureLock] TryAcquire: {Path.GetFileName(key)} for {what}");
            if (!System.IO.Directory.Exists(key))
                throw new UsageException($"cannot claim {key} for a capture: the recording directory does not exist");

            lock (Gate)
            {
                if (HeldKeys.Contains(key))
                {
                    refusal = $"{Path.GetFileName(key)} is already claimed by a capture in this process (pid {Environment.ProcessId})";
                    Log.Info($"[CaptureLock] TryAcquire REFUSED: {refusal}");
                    return null;
                }

                FileStream? stream = OpenExclusive(key, FileMode.OpenOrCreate);
                if (stream == null)
                {
                    refusal = HeldElsewhereReason(key);
                    Log.Info($"[CaptureLock] TryAcquire REFUSED: {refusal}");
                    return null;
                }

                // Until the claim is published the handle is not owned by anything, so every exit
                // below - a refusal or a throw - must close it: a leaked handle would be reported to
                // every other process as "held, owner unknown" until a garbage collection.
                bool published = false;
                try
                {
                    // The lock was free, so no live process holds it - but a dead owner's ffmpeg may
                    // still be writing here (see the class comment). A capture refused for that
                    // reason is the same safety rule as a finalize refused for it.
                    var stale = FreeLockLiveness(key);
                    if (stale.Live)
                    {
                        refusal = stale.Reason;
                        Log.Info($"[CaptureLock] TryAcquire REFUSED: {refusal}");
                        return null;
                    }

                    using var me = Process.GetCurrentProcess();
                    var owner = new OwnerRecord
                    {
                        Host = HostName(),
                        HostId = ThisHostId(),
                        Pid = me.Id,
                        ProcessStartUtc = me.StartTime.ToUniversalTime(),
                        What = what,
                        StartedUtc = DateTime.UtcNow,
                    };
                    WriteOwner(key, owner);

                    HeldKeys.Add(key);
                    published = true;
                    refusal = "";
                    Log.Info($"[CaptureLock] TryAcquire: {Path.GetFileName(key)} claimed by {owner}");
                    return new CaptureLock(key, stream, owner);
                }
                finally
                {
                    if (!published) stream.Dispose();
                }
            }
        }

        /// <summary>
        /// Records an ffmpeg this capture started that writes straight into the directory, so the
        /// claim stays live while it runs even if this process dies, and registers it with
        /// <see cref="Video.FfmpegChildRegistry"/> so the next service start can reap it once this
        /// process is gone. Call right after the writer starts.
        /// </summary>
        public void NoteWriter(string label, int pid)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("a writer needs a label", nameof(label));
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureLock));
            Log.Info($"[CaptureLock] NoteWriter: {Path.GetFileName(_key)} {label} pid {pid}");

            DateTime startUtc;
            try
            {
                using var p = Process.GetProcessById(pid);
                startUtc = p.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // GetProcessById: "not running"; StartTime: exited between the two calls. Either way
                // the writer died the moment it started - say THAT, not the framework's wording.
                throw new UsageException(
                    $"the {label} ffmpeg (pid {pid}) exited immediately after it started, so the capture cannot " +
                    $"start. Its ffmpeg log in {_key} says why.", ex);
            }

            lock (Gate)
            {
                _owner.Writers.Add(new WriterRecord { Label = label, Pid = pid, StartUtc = startUtc });
                WriteOwner(_key, _owner);
            }
            Video.FfmpegChildRegistry.RegisterCaptureWriter(pid, startUtc, $"capture {label} ({Path.GetFileName(_key)})");
            Log.Info($"[CaptureLock] NoteWriter: {Path.GetFileName(_key)} {label} pid {pid} recorded (started {startUtc:o})");
        }

        /// <summary>
        /// The finalize side: is a capture still writing into <paramref name="dir"/>? Never takes the
        /// claim - it probes the lock (open and close at once) and reads the description. A directory
        /// that no capture of this version ever claimed is not live.
        /// </summary>
        public static CaptureLiveness Inspect(string dir)
        {
            string key = RecordingWorkset.Key(dir);

            lock (Gate)
            {
                if (HeldKeys.Contains(key))
                    return new CaptureLiveness(true,
                        $"{Path.GetFileName(key)} is being captured by this process (pid {Environment.ProcessId})");
            }

            if (File.Exists(Path.Combine(key, FileName)))
            {
                using var probe = OpenShared(key);
                if (probe == null) return new CaptureLiveness(true, HeldElsewhereReason(key));
            }

            return FreeLockLiveness(key);
        }

        /// <summary>
        /// Throws a <see cref="UsageException"/> naming the holder when a capture is still writing
        /// into <paramref name="dir"/>; logs the takeover of a stale claim otherwise.
        /// </summary>
        public static void ThrowIfLive(string dir, string purpose)
        {
            var live = Inspect(dir);
            if (!live.Live)
            {
                Log.Info($"[CaptureLock] ThrowIfLive: {Path.GetFileName(dir)} ({purpose}) - {live.Reason}");
                return;
            }

            string msg = $"refusing to {purpose} {Path.GetFileName(RecordingWorkset.Key(dir))}: {live.Reason}. " +
                         "Nothing was muxed, renamed or counted as an attempt; the recording is finalized when its capture ends.";
            Log.Info($"[CaptureLock] ThrowIfLive DECLINED: {msg}");
            throw new UsageException(msg);
        }

        /// <summary>
        /// Releases the claim: the description goes first, then the lock.
        ///
        /// UNLESS a recorded writer is still running. The stop sequence stops every writer before the
        /// claim is released, so this is a writer the stop could not end (a wedged ffmpeg the kill
        /// did not take). The lock is still released - this process is done with the recording - but
        /// the description and that writer's reap record are KEPT, so the claim stays live to every
        /// other process through the dead-owner writer check, and the next service start can still
        /// reap the writer. Deleting them would publish a file that is still being written.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var running = new List<WriterRecord>();
            foreach (var w in _owner.Writers)
                if (IsSameProcess(w.Pid, w.StartUtc)) running.Add(w);

            lock (Gate)
            {
                string ownerPath = Path.Combine(_key, OwnerFileName);
                if (running.Count == 0)
                {
                    // Deleted BEFORE the handle closes: a reader that finds the lock free must not
                    // find a description of a holder that has just finished cleanly. A failure is
                    // logged and the lock still released - a description whose writers have all
                    // exited reads as "not live" (FreeLockLiveness), so it strands nothing.
                    try
                    {
                        if (File.Exists(ownerPath)) File.Delete(ownerPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"[CaptureLock] Release: could not remove {ownerPath}; its writers have exited, " +
                                  "so it reads as a released claim", ex);
                    }
                }
                else
                {
                    foreach (var w in running)
                        Log.Error($"[CaptureLock] Release: {Path.GetFileName(_key)} - the {w.Label} ffmpeg pid {w.Pid} is STILL " +
                                  "RUNNING after its capture stopped. The owner record is KEPT so no other process finalizes " +
                                  "a file that is still being written; the next service start reaps it.");
                }
                _stream.Dispose();
                HeldKeys.Remove(_key);
            }

            foreach (var w in _owner.Writers)
                if (!running.Contains(w)) Video.FfmpegChildRegistry.Unregister(w.Pid);
            Log.Info($"[CaptureLock] Release: {Path.GetFileName(_key)} released by pid {_owner.Pid}"
                + (running.Count > 0 ? $" ({running.Count} writer(s) still running - claim stays live)" : ""));
        }

        /// <summary>
        /// Issue #156 pass 2 (D2): remove the owner record of a claim that is PROVABLY stale - its
        /// holder released or died, and every writer it recorded on this host has exited - so a
        /// recording a crashed capture left behind is not refused for ever by every other host.
        ///
        /// Why it is needed: a clean release deletes the record (<see cref="Dispose"/>), but a crash
        /// leaves it, and a stale record is only provable stale ON THE HOST THAT WROTE IT (another
        /// host cannot probe the writer pids, so <see cref="FreeLockLiveness"/> reports it live
        /// there, correctly). Before this, the takeover finished the recording and kept the record,
        /// so a COMPLETED recording was refused by every other host, its repair and thumbnail
        /// passes included - a permanent strand of the #128 class.
        ///
        /// Called by the paths that take a crashed recording over - <c>PostRecording.Execute</c>
        /// (the repair and post-recording passes), <c>RecordingService.FinalizePending</c> and
        /// <c>agenteyes package</c> - AFTER their own live-claim decline has passed, so it changes
        /// nothing about what is declined or counted.
        ///
        /// Decided under a SHARED hold of the lock, and re-decided there: the finalize side's own
        /// probe (<see cref="OpenShared"/>) is held open while the liveness is computed again and the
        /// record removed, so no capture can take the lock - an exclusive claim - in between. It is
        /// deliberately NOT the exclusive lock: other finalizers probe with the same shared open, so
        /// they never see this as a holder. An exclusive hold would, for its few milliseconds, read
        /// to another process's <c>FinalizePending</c> - which checks AFTER its attempt is counted -
        /// as a live capture by the dead owner, failing that attempt and spending the budget this
        /// issue protects (self-review of pass 2). Anything else - the lock held by a capture, this
        /// process capturing it, a writer still running, a writer on another host - keeps the record
        /// and says why. Returns true when a record was removed.
        /// </summary>
        public static bool ClearStaleClaim(string dir, string purpose)
        {
            string key = RecordingWorkset.Key(dir);
            string name = Path.GetFileName(key);
            string ownerPath = Path.Combine(key, OwnerFileName);
            if (!File.Exists(ownerPath))
            {
                Log.Info($"[CaptureLock] ClearStaleClaim: {name} ({purpose}) - no owner record; nothing to clear");
                return false;
            }

            lock (Gate)
            {
                if (HeldKeys.Contains(key))
                {
                    Log.Info($"[CaptureLock] ClearStaleClaim: {name} ({purpose}) - KEPT: this process (pid {Environment.ProcessId}) is capturing it");
                    return false;
                }

                // A record with no lock file beside it was never held by a capture of this version
                // (TryAcquire creates the lock before it writes the record): nothing to hold open.
                bool hasLock = File.Exists(Path.Combine(key, FileName));
                using FileStream? probe = hasLock ? OpenShared(key) : null;
                if (hasLock && probe == null)
                {
                    Log.Info($"[CaptureLock] ClearStaleClaim: {name} ({purpose}) - KEPT: {HeldElsewhereReason(key)}");
                    return false;
                }

                var live = FreeLockLiveness(key);
                if (live.Live)
                {
                    Log.Info($"[CaptureLock] ClearStaleClaim: {name} ({purpose}) - KEPT: {live.Reason}");
                    return false;
                }

                if (!File.Exists(ownerPath))
                {
                    Log.Info($"[CaptureLock] ClearStaleClaim: {name} ({purpose}) - the owner record was removed by another process first");
                    return false;
                }
                File.Delete(ownerPath);
                Log.Info($"[CaptureLock] ClearStaleClaim: {name} ({purpose}) - CLEARED {OwnerFileName} (lock held shared): {live.Reason}. " +
                         "The capture's owner stays on record in the manifest (CaptureOwner)");
                return true;
            }
        }

        /// <summary>
        /// The lock is free (or was never created). Live only while a recorded writer is still
        /// running, or cannot be checked from this host.
        /// </summary>
        private static CaptureLiveness FreeLockLiveness(string key)
        {
            string ownerPath = Path.Combine(key, OwnerFileName);
            if (!File.Exists(ownerPath))
                return new CaptureLiveness(false, "no capture holds it");

            OwnerRecord? read = ReadOwner(ownerPath, out bool vanished);
            // Removed between the existence check and the read: its holder released it cleanly (the
            // record is deleted before the lock closes), which is "not live", not an error.
            if (vanished) return new CaptureLiveness(false, "no capture holds it (its claim was released while being read)");
            OwnerRecord owner = read
                ?? throw new UsageException(
                    $"{ownerPath} is unreadable, so whether a capture is still writing into {Path.GetFileName(key)} cannot be " +
                    $"decided. Check that no ffmpeg is writing into {key}, then delete {OwnerFileName} and run the repair again.");

            string here = HostName();
            bool sameHost = IsThisHost(owner);
            foreach (var w in owner.Writers)
            {
                if (!sameHost)
                    return new CaptureLiveness(true,
                        $"its capture owner ({owner}) is gone, but its {w.Label} ffmpeg pid {w.Pid} ran on host {owner.Host} and " +
                        $"cannot be checked from {here}. Finalize it from {owner.Host}, or - once you have checked nothing is " +
                        $"writing into {key} - delete {OwnerFileName}");

                if (IsSameProcess(w.Pid, w.StartUtc))
                    return new CaptureLiveness(true,
                        $"its capture owner ({owner}) is gone but its {w.Label} ffmpeg pid {w.Pid} is STILL RUNNING and writing " +
                        $"into it. Stop that process, or restart the AgentEyes service, which reaps an ffmpeg whose owner is gone");
            }

            return new CaptureLiveness(false,
                $"stale capture claim taken over: its owner ({owner}) is gone and its {owner.Writers.Count} writer(s) have exited");
        }

        /// <summary>
        /// True when <paramref name="owner"/> was written on this machine: the same host id, OR the
        /// same machine name. Either alone is not enough - macOS renames the host when the network
        /// changes (the id survives that), and a second user account on the same machine has its own
        /// id (the name covers that). Only a record that differs in BOTH is another machine's.
        /// </summary>
        private static bool IsThisHost(OwnerRecord owner)
        {
            if (!string.IsNullOrEmpty(owner.HostId) && string.Equals(owner.HostId, ThisHostId(), StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(owner.Host, HostName(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// This machine's AgentEyes host id: a GUID kept in <see cref="HostIdFileName"/> under
        /// <see cref="AppDataPaths.Root"/>, created on first use. Two processes creating it at once
        /// both write a temp file and the first rename wins; the loser reads the winner's id.
        /// </summary>
        internal static string ReadOrCreateHostId()
        {
            string path = Path.Combine(AppDataPaths.Root, HostIdFileName);
            if (!File.Exists(path))
            {
                System.IO.Directory.CreateDirectory(AppDataPaths.Root);
                string tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, Guid.NewGuid().ToString("N"));
                try
                {
                    File.Move(tmp, path, overwrite: false);
                    Log.Info($"[CaptureLock] ReadOrCreateHostId: created {path}");
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process created it first; its id is the machine's id.
                    File.Delete(tmp);
                }
            }
            string id = File.ReadAllText(path).Trim();
            if (!Guid.TryParse(id, out _))
                throw new UsageException($"{path} does not hold a valid AgentEyes host id ('{id}'). Delete it and AgentEyes will create a new one.");
            return id;
        }

        /// <summary>The refusal text for a lock another process holds, naming it from the description.</summary>
        private static string HeldElsewhereReason(string key)
        {
            string ownerPath = Path.Combine(key, OwnerFileName);
            OwnerRecord? owner = File.Exists(ownerPath) ? ReadOwner(ownerPath, out _) : null;
            return owner != null
                ? $"it is being captured by the {owner}"
                : $"another AgentEyes process holds its capture lock ({FileName}) and has not yet published who it is";
        }

        /// <summary>
        /// The finalize side's probe: a SHARED open (a shared flock on macOS/Linux; read+write sharing
        /// on Windows). It is refused only by the capture's exclusive handle, and two probes - the
        /// service's repair pass and an `agenteyes package` checking the same recording at the same
        /// instant - never see each other as a holder. An exclusive probe did, which would have made
        /// a finished recording read as live and failed the second finalizer mid-attempt.
        /// </summary>
        private static FileStream? OpenShared(string key)
        {
            try
            {
                return new FileStream(Path.Combine(key, FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
            }
            catch (IOException ex) when (MuxLock.IsHeldElsewhere(ex))
            {
                return null;
            }
        }

        /// <summary>Opens the lock exclusively, or returns null when another handle holds it.</summary>
        private static FileStream? OpenExclusive(string key, FileMode mode)
        {
            try
            {
                return new FileStream(Path.Combine(key, FileName), mode, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex) when (MuxLock.IsHeldElsewhere(ex))
            {
                return null;
            }
        }

        /// <summary>Atomic write of the description: temp file in the same directory, then a rename.</summary>
        private static void WriteOwner(string key, OwnerRecord owner)
        {
            string path = Path.Combine(key, OwnerFileName);
            string tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(owner, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>Reads the description, or null when it is not valid JSON. <paramref name="vanished"/>
        /// is true when it was deleted before it could be opened (its holder released it). Opened with
        /// every share mode so the holder's atomic replace is never blocked by a reader.</summary>
        private static OwnerRecord? ReadOwner(string path, out bool vanished)
        {
            vanished = false;
            string text;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                text = reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                vanished = true;
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<OwnerRecord>(text);
            }
            catch (JsonException ex)
            {
                Log.Error($"[CaptureLock] ReadOwner: {path} is not a valid capture owner record", ex);
                return null;
            }
        }
    }
}
