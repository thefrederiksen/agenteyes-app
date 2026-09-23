using System;
using System.IO;
using System.Threading;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Writes one file's whole contents on a background thread, so that a UI-thread caller never
    /// waits for a disk (issue #33; Review Gate round 1 on PR #34).
    ///
    /// WHY IT EXISTS. The recording HUD's Show/Hide-preview buttons persisted the person's choice by
    /// calling <c>Config.Save</c> straight from the click handler, and <c>Config.Save</c> is a
    /// synchronous <c>File.WriteAllText</c>. Under disk pressure, an antivirus scan or a filter
    /// driver, the WPF dispatcher is then blocked INSIDE that write - and the dispatcher is what
    /// serves the HUD's STOP button. A person who cannot stop a recording because a settings file is
    /// being written has lost the thing the HUD is for. The repo's first coding standard says it
    /// plainly: never block the UI thread with synchronous I/O.
    ///
    /// THE SHAPE, and it is the same one <c>PreviewTap</c> uses for exactly the same reason: a
    /// LATEST-WINS SLOT and a separate writer thread.
    ///
    ///  - <see cref="Queue"/> takes FINISHED TEXT. Serialising happens on the caller's thread on
    ///    purpose - it is microseconds of in-memory work, and it means the writer can never observe
    ///    a half-changed object while the UI thread is still editing it.
    ///  - Everything <see cref="Queue"/> does is an interlocked pointer swap and one event set. No
    ///    lock the writer could be holding while it sits in a filesystem call, and no queue that can
    ///    grow: a second change that arrives before the first is written REPLACES it, because the
    ///    file only ever holds the newest state anyway. Superseded writes are counted rather than
    ///    silently dropped (<see cref="Superseded"/>).
    ///  - <see cref="Flush"/> is the bounded wait for a shutdown, and it is BOUNDED on purpose: this
    ///    thread is allowed to be stuck in a filesystem call, so exit must not be.
    ///
    /// The thread is started by whoever owns the file, at application startup - never lazily from a
    /// UI path. That is not a style choice: it is what keeps the writer's loop OUT of the call graph
    /// reachable from the HUD's click handlers, which is the property
    /// <c>HudResponsivenessTests</c> asserts against the compiled IL.
    /// </summary>
    internal sealed class BackgroundFileWriter
    {
        /// <summary>How often the writer wakes with nothing to do, so a stop that races the signal is
        /// still noticed promptly. The normal wake-up is a <see cref="Queue"/>.</summary>
        private const int IdleWakeMs = 250;

        private readonly string _path;
        private readonly Action<string, string> _write;
        private readonly AutoResetEvent _work = new(false);
        private readonly ManualResetEventSlim _idle = new(initialState: true);

        private string? _pending;
        private Thread? _thread;
        private volatile bool _stopping;
        private long _writes;
        private long _superseded;
        private long _failures;

        /// <param name="path">The file to write.</param>
        /// <param name="write">How to write it, as (path, text). The default writes to disk; the only
        /// other implementation is a test's, because what has to be proven here is what a CALLER does
        /// while the write is stalled, and a stall is something no test can produce on a real
        /// filesystem.</param>
        public BackgroundFileWriter(string path, Action<string, string>? write = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("a background file writer must be told what file to write", nameof(path));
            _path = path;
            _write = write ?? WriteToDisk;
        }

        /// <summary>Whole writes that reached the file.</summary>
        public long Writes => Interlocked.Read(ref _writes);

        /// <summary>Queued texts replaced by a newer one before the writer took them. A PRESENCE, not
        /// an error: the file only ever holds the newest state, so this is the measured cost of never
        /// making the caller wait.</summary>
        public long Superseded => Interlocked.Read(ref _superseded);

        /// <summary>Writes that threw. Reported here as well as logged, so a caller (or a test) can
        /// see a broken instrument rather than an absence.</summary>
        public long Failures => Interlocked.Read(ref _failures);

        /// <summary>Start the writer thread. Idempotent. Called once by the owner of the file at
        /// application startup.</summary>
        public void Start()
        {
            if (_thread != null) return;
            Log.Info($"[BackgroundFileWriter] Start: path={_path}");
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "AgentEyes background file writer",
            };
            _thread.Start();
        }

        /// <summary>
        /// Hand the writer the file's whole new contents. RETURNS IMMEDIATELY, whatever the
        /// filesystem is doing - an interlocked swap and an event set, and no I/O at all. Safe from
        /// the WPF UI thread, which is the entire point.
        /// </summary>
        public void Queue(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            _idle.Reset();
            if (Interlocked.Exchange(ref _pending, text) != null)
                Interlocked.Increment(ref _superseded);
            _work.Set();
        }

        /// <summary>
        /// Hand the writer the file's whole new contents AND WAIT, at most
        /// <paramref name="milliseconds"/>, for it to reach the disk. For a caller that has always
        /// blocked on its save and whose window is modal anyway - the launcher's dialogs.
        ///
        /// WHY IT GOES THROUGH THE QUEUE INSTEAD OF WRITING DIRECTLY (Review Gate round 2 on PR #39,
        /// defect 3). Every save writes the WHOLE document, so the only correct file content is the
        /// one the newest save produced. A caller that wrote the file itself while a queued snapshot
        /// was still waiting was ordered only by a mutex, and a mutex says who goes first, not who
        /// goes LAST: the direct write took the lock, landed, and the older queued snapshot was then
        /// written on top of it. The person changed their capture folder and watched it revert.
        ///
        /// ONE WRITER, ONE ORDER. Every save in the process is queued, and one thread takes them in
        /// the order they were made, so the last save made is the last save written - whichever kind
        /// of caller made it. Blocking is then only about WAITING for the write, never about
        /// performing it.
        ///
        /// Bounded like everything else here: false means the write had not landed in time, which is
        /// reported rather than waited out. The value is not lost - it is still the newest thing in
        /// the writer's hands, and <see cref="Flush"/> at exit gives it one more chance.
        /// </summary>
        public bool WriteNow(string text, int milliseconds)
        {
            Queue(text);
            return Flush(milliseconds);
        }

        /// <summary>
        /// Wait, at most <paramref name="milliseconds"/>, for the writer to have nothing left in
        /// hand. Returns false when it is still working - which at a shutdown means the write did not
        /// land, and is reported rather than waited out. Used at application exit and by tests.
        /// </summary>
        public bool Flush(int milliseconds)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (true)
            {
                if (Volatile.Read(ref _pending) == null && _idle.IsSet) return true;
                if (Environment.TickCount64 >= deadline)
                {
                    Log.Warn($"[BackgroundFileWriter] Flush: {_path} still had a pending write after "
                             + $"{milliseconds}ms; it was not waited out. The last change may not be on disk.");
                    return false;
                }
                Thread.Sleep(5);
            }
        }

        /// <summary>Stop the writer after it has finished what it holds. Bounded, like everything
        /// else here.</summary>
        public void Stop(int milliseconds)
        {
            _stopping = true;
            _work.Set();
            var thread = _thread;
            if (thread != null && thread.IsAlive && !thread.Join(milliseconds))
                Log.Warn($"[BackgroundFileWriter] Stop: the writer for {_path} did not finish within "
                         + $"{milliseconds}ms - it is a background thread and ends with the process.");
        }

        /// <summary>
        /// The writer loop. A THREAD ENTRY POINT, hence the try/catch: nothing it writes to is under
        /// its control, and an exception escaping here would silently end all further saving.
        /// </summary>
        private void Loop()
        {
            try
            {
                while (true)
                {
                    _work.WaitOne(IdleWakeMs);

                    var text = Interlocked.Exchange(ref _pending, null);
                    if (text != null) WriteOnce(text);

                    if (Volatile.Read(ref _pending) == null)
                    {
                        _idle.Set();
                        if (_stopping) break;
                    }
                }
                Log.Info($"[BackgroundFileWriter] Loop: ended for {_path} "
                         + $"(writes={Writes} superseded={Superseded} failures={Failures})");
            }
            catch (Exception ex)
            {
                Log.Error($"[BackgroundFileWriter] Loop FAILED for {_path} - nothing further will be "
                          + "written to this file until the app is restarted", ex);
            }
        }

        private void WriteOnce(string text)
        {
            try
            {
                _write(_path, text);
                Interlocked.Increment(ref _writes);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failures);
                Log.Warn($"[BackgroundFileWriter] WriteOnce FAILED: {_path} - {ex.Message}. "
                         + "The change is held in memory for this session but is not on disk.");
            }
        }

        private static void WriteToDisk(string path, string text)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text);
        }
    }
}
