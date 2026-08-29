using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AgentEyes.Preview
{
    /// <summary>
    /// The live preview frame source for ONE recorded track (issue #33): it drains the preview
    /// stream ffmpeg writes to its stdout and publishes the newest whole frame where the HUD can
    /// read it.
    ///
    /// THE CAMERA CONSTRAINT IS WHY THIS EXISTS (issue #33, assumption C1). While a recording runs,
    /// ffmpeg holds the DirectShow camera EXCLUSIVELY. A preview that opened the device a second time
    /// would either fail outright or fight the recorder for it, so preview frames must come from the
    /// RECORDING PIPELINE - a second, low-resolution output on the recorder's own ffmpeg. One device
    /// open, one process, two outputs.
    ///
    /// THE PREVIEW IS STRICTLY SUBORDINATE TO THE RECORDING (issue #33, AC10), and the shape of this
    /// class is that rule rather than a comment about it:
    ///
    ///  - THE DRAIN IS UNCONDITIONAL AND NEVER WAITS FOR ANYTHING. ffmpeg's stdout is read for as
    ///    long as the process lives, whether or not anything is showing the preview and whether or
    ///    not publishing works. It has to be: an anonymous pipe nobody reads fills, and a full pipe
    ///    BLOCKS the ffmpeg that is writing the recording.
    ///  - PUBLISHING IS ALLOWED TO FAIL AND TO STALL, AND ONLY PUBLISHING. Writing the frame out is
    ///    downstream of the drain and of ffmpeg. If the preview directory is removed, made read-only,
    ///    filled, or replaced by a reparse point onto a share that never answers, the write fails or
    ///    hangs, a WARNING is logged, the HUD goes stale and says so - and the recording is not
    ///    touched, because ffmpeg is still being read.
    ///
    /// WHY THE HANDOFF EXISTS (Review Gate round 1 on PR #34, 2026-08-29). The first implementation
    /// called Publish INLINE from the drain loop, so the drain performed synchronous filesystem
    /// calls between two pipe reads. A caught exception did not save it: a catch runs only after the
    /// blocking call RETURNS or throws, and an NTFS stall, a filter driver, or a directory reparse
    /// point onto an unavailable share does neither. Draining is the one thing here that is not
    /// allowed to stop, so it is the one thing that never depends on anything else - and "publishing
    /// does not throw" was never the same claim as "publishing cannot stop the drain".
    ///
    /// So the drain and the publisher are TWO THREADS joined by a BOUNDED LATEST-FRAME SLOT:
    ///
    ///     ffmpeg stdout --[drain thread]--&gt; one-frame slot --[publisher thread]--&gt; preview.jpg
    ///
    /// The drain's whole interaction with publishing is an interlocked pointer swap into that slot
    /// and one event set: no lock, no I/O, and no bound on how long the publisher may take. The slot
    /// holds ONE frame, so a publisher that falls behind costs DROPPED FRAMES (counted in
    /// <see cref="FramesDropped"/>) and never memory - the right trade for a monitor whose next frame
    /// is 100ms away, and the wrong one for a queue that would grow for the length of a recording.
    ///
    /// That boundary is not theoretical. Handing the SECOND OUTPUT TO FFMPEG AS A FILE - the obvious
    /// implementation - was measured on 2026-08-28 and rejected: removing the preview directory
    /// mid-run made ffmpeg's image2 muxer fail, and ffmpeg terminated the WHOLE process, cutting a
    /// 15-second recording to 5.1 seconds. A preview that can truncate the recording it monitors is
    /// worse than no preview. Routing the frames through this drain is what moves that failure out of
    /// ffmpeg and into a component whose failure costs a picture.
    ///
    /// Frames are published by writing a temporary file and RENAMING it over the target, so a reader
    /// never sees a half-written image; the reader still verifies the two JPEG markers
    /// (<see cref="JpegFrame"/>), because an instrument that assumes its own preconditions is not an
    /// instrument.
    /// </summary>
    internal sealed class PreviewTap : IDisposable
    {
        /// <summary>How long the pump thread is given to finish after the pipe closes. It ends on
        /// its own when ffmpeg exits and the stream reaches end of file; this only bounds a stop.</summary>
        private const int JoinTimeoutMs = 3000;

        /// <summary>How long the PUBLISHER thread is given to finish at a stop. Deliberately bounded:
        /// the whole point of this thread is that it is ALLOWED to be stuck in a filesystem call, so
        /// a stop must never wait on it indefinitely. It is a background thread and ends with the
        /// process.</summary>
        private const int PublisherJoinTimeoutMs = 3000;

        /// <summary>How often the publisher wakes with no work. It exists only so a Dispose that
        /// races the signal is still noticed promptly; the normal wake-up is the drain's set.</summary>
        private const int PublisherIdleWakeMs = 250;

        private const int ReadBufferBytes = 64 * 1024;

        private readonly string _track;
        private readonly string _framePath;
        private readonly string _tempPath;
        private readonly MjpegFramer _framer = new();

        /// <summary>Writes one frame where the HUD reads it. A seam, and a narrow one: the default is
        /// <see cref="WriteFrameToDisk"/> and the only other implementation is a test's, because the
        /// behaviour worth proving here is what the DRAIN does while this is STALLED - and a stall is
        /// something no test can produce on a real filesystem.</summary>
        private readonly Action<byte[]> _writeFrame;

        /// <summary>The newest frame waiting to be published, or null. Read and written ONLY through
        /// <see cref="Interlocked"/>: the drain must never take a lock that the publisher could be
        /// holding while it sits inside a filesystem call.</summary>
        private byte[]? _latest;

        /// <summary>1 when the published frame file should be deleted. Set by the Publishing setter -
        /// which is called from the WPF UI thread - so the delete happens on the publisher thread and
        /// never on the caller's.</summary>
        private int _removeFrameFile;

        /// <summary>
        /// What the DRAIN wants said in the log, waiting for the publisher to say it.
        ///
        /// The shared logger appends to a file under a process-wide lock
        /// (<see cref="Log"/>), so a <c>Log.Info</c> on the drain thread is a filesystem call AND a
        /// lock some other thread may be holding inside one. Either can stop the drain, and a stopped
        /// drain fills the pipe and blocks the ffmpeg writing the recording. So the drain writes its
        /// lines HERE - an enqueue and nothing else - and the publisher, the thread that is allowed
        /// to block, does the logging.
        ///
        /// Bounded, and the bound is counted: a publisher wedged in a stalled filesystem call must
        /// not let this grow for the length of a recording.
        /// </summary>
        private readonly ConcurrentQueue<(string Level, string Message)> _notes = new();

        /// <summary>Ceiling on unlogged notes. The drain writes about three lines in a whole
        /// recording, so reaching this at all means the publisher has stopped.</summary>
        private const int MaxPendingNotes = 64;

        private long _notesDropped;

        private readonly AutoResetEvent _publisherWork = new(false);

        /// <summary>Set while the publisher has nothing pending and nothing in hand. Used by
        /// <see cref="WaitForPublisher"/> and by <see cref="Dispose"/> - never by the drain.</summary>
        private readonly ManualResetEventSlim _publisherIdle = new(initialState: true);

        private Thread? _pump;
        private Thread? _publisher;
        private volatile bool _publishing;
        private volatile bool _disposed;
        private volatile bool _publishFailed;
        private long _framesRead;
        private long _framesPublished;
        private long _framesDropped;
        private bool _firstFrameLogged;

        private PreviewTap(string track, string framePath, Action<byte[]>? writeFrame)
        {
            _track = track;
            _framePath = framePath;
            _tempPath = framePath + ".tmp";
            _writeFrame = writeFrame ?? WriteFrameToDisk;
        }

        /// <summary>The file the HUD reads. It exists only while frames are being published.</summary>
        public string FramePath => _framePath;

        /// <summary>Which recorded track this previews ("screen", "camera").</summary>
        public string Track => _track;

        /// <summary>Whole frames taken off the pipe since the recording started.</summary>
        public long FramesRead => Interlocked.Read(ref _framesRead);

        /// <summary>Frames actually written to <see cref="FramePath"/>.</summary>
        public long FramesPublished => Interlocked.Read(ref _framesPublished);

        /// <summary>
        /// Frames that were handed to the publisher and superseded before it could write them.
        /// A PRESENCE, not an error: it is the measured cost of never letting the drain wait, and on
        /// a healthy filesystem it stays at or near zero. While publishing is on,
        /// <see cref="FramesPublished"/> + <see cref="FramesDropped"/> accounts for every frame the
        /// drain offered - so a frame can be late or dropped, but it cannot vanish unrecorded.
        /// </summary>
        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        /// <summary>True once a publish has failed and not yet succeeded again. The HUD does not read
        /// this - it judges the frame file itself - but the log and /status do.</summary>
        public bool PublishFailed => _publishFailed;

        /// <summary>
        /// Whether frames are being written out. False is the DEFAULT and the cheap state: the pipe
        /// is still drained (it must be), the frames are still parsed, and then discarded. Flipping
        /// this is the whole cost of showing or hiding the preview mid-recording - no ffmpeg is
        /// restarted, no output is added, and the recording never learns that anything changed.
        ///
        /// SETTING IT DOES NO I/O. It is called from the HUD's click handler on the WPF UI thread
        /// (Review Gate round 1 on PR #34), so removing the published frame is REQUESTED here and
        /// PERFORMED on the publisher thread. The caller returns immediately whatever the filesystem
        /// happens to be doing.
        /// </summary>
        public bool Publishing
        {
            get => _publishing;
            set
            {
                if (_publishing == value) return;
                _publishing = value;
                // Not Log.Info: this setter runs on the WPF UI thread (the HUD's Show/Hide preview
                // click), and the shared logger is a synchronous file append under a global lock.
                Note("INFO", $"[PreviewTap] Publishing({_track}) -> {value}");
                if (!value)
                {
                    // A frame queued for a preview nobody is looking at is not published.
                    Interlocked.Exchange(ref _latest, null);
                    Interlocked.Exchange(ref _removeFrameFile, 1);
                    _publisherIdle.Reset();
                    _publisherWork.Set();
                }
            }
        }

        /// <summary>
        /// Prepare a tap for one track, or return NULL when the machine cannot host one - the
        /// preview directory cannot be created or cleaned. Null is not an error the caller has to
        /// handle beyond recording WITHOUT a preview: it is the subordination rule at start time, and
        /// it is why a broken preview cannot stop a recording from starting (AC10).
        /// </summary>
        public static PreviewTap? TryCreate(string track) => TryCreateAt(track, PreviewPaths.Frame(track));

        /// <summary>
        /// The same tap publishing to a supplied path - the seam the tests drive (mirroring the
        /// camera recorder's <c>CreateOver</c>). It exists because the behaviour worth testing here
        /// is what happens when publishing FAILS OR STALLS, and reaching that on the real path would
        /// mean deleting a directory inside the user's own %LOCALAPPDATA% (or producing a filesystem
        /// hang, which nothing in a unit test can do at all).
        /// </summary>
        internal static PreviewTap? TryCreateAt(string track, string framePath, Action<byte[]>? writeFrame = null)
        {
            if (string.IsNullOrWhiteSpace(track))
                throw new ArgumentException("a preview track must be named", nameof(track));
            if (string.IsNullOrWhiteSpace(framePath))
                throw new ArgumentException("a preview tap must be told where to publish", nameof(framePath));

            Log.Info($"[PreviewTap] TryCreate: track={track} frame={framePath}");
            try
            {
                string? dir = Path.GetDirectoryName(framePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // A frame left by a previous recording is a LIE the moment this one starts: it is a
                // picture of something else that the staleness watchdog would need seconds to catch.
                if (File.Exists(framePath)) File.Delete(framePath);
                string temp = framePath + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex)
            {
                Log.Warn($"[PreviewTap] TryCreate: no preview for the {track} track - {ex.Message}. "
                         + "The recording is unaffected and proceeds without a preview.");
                return null;
            }

            Log.Info($"[PreviewTap] TryCreate: track={track} ready");
            return new PreviewTap(track, framePath, writeFrame);
        }

        /// <summary>
        /// Start draining <paramref name="stdout"/> on a dedicated background thread, and start the
        /// publisher that serves the frames it hands over. Called by the recorder the instant ffmpeg
        /// is running - never later, because the pipe starts filling then.
        /// </summary>
        public void Pump(Stream stdout)
        {
            if (stdout == null) throw new ArgumentNullException(nameof(stdout));
            if (_pump != null) throw new InvalidOperationException($"the {_track} preview tap is already pumping");

            Log.Info($"[PreviewTap] Pump: track={_track} starting");

            // The publisher starts FIRST. It owns every fallible and every unbounded operation this
            // class performs, so it has to exist before a frame can be offered to it.
            _publisher = new Thread(PublishLoop)
            {
                IsBackground = true,
                Name = $"AgentEyes preview publisher ({_track})",
            };
            _publisher.Start();

            _pump = new Thread(() => Drain(stdout))
            {
                IsBackground = true,
                Name = $"AgentEyes preview tap ({_track})",
            };
            _pump.Start();
        }

        /// <summary>
        /// Wait for the pump to reach the end of its stream. For TESTS only: production never waits
        /// on this - the pump ends when ffmpeg closes the pipe, and <see cref="Dispose"/> is where a
        /// bounded join belongs. Returns false when the pump is still running.
        /// </summary>
        internal bool WaitForDrain(int milliseconds)
        {
            var pump = _pump;
            return pump == null || !pump.IsAlive || pump.Join(milliseconds);
        }

        /// <summary>
        /// Wait until the publisher has nothing pending and nothing in hand. For TESTS only, and the
        /// reason a test can still assert on the published FILE after the drain is over: publishing
        /// is asynchronous by design now, so "the drain finished" no longer implies "the frame is on
        /// disk". Returns false when the publisher is still working - a failed assertion rather than
        /// a longer wait.
        /// </summary>
        internal bool WaitForPublisher(int milliseconds)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (true)
            {
                bool nothingPending = Volatile.Read(ref _latest) == null
                                   && Volatile.Read(ref _removeFrameFile) == 0
                                   && _notes.IsEmpty;
                if (nothingPending && _publisherIdle.IsSet) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// The drain loop, and the one method here that is not allowed to wait for anything. This is
        /// a THREAD ENTRY POINT, which is why it carries a try/catch at all - and the catch is the
        /// last line of the recording's defence: an exception escaping this thread would stop the
        /// pipe being read, and a full pipe blocks the ffmpeg writing the recording. So a failure in
        /// framing drops this to a READ-AND-DISCARD loop and keeps going.
        ///
        /// NOTHING REACHABLE FROM HERE TOUCHES THE FILESYSTEM. That is not a convention: it is
        /// asserted against the compiled IL in <c>PreviewTapTests</c>, because the round-1 defect was
        /// exactly a filesystem call sitting on this path behind a catch that could not help.
        /// </summary>
        private void Drain(Stream stdout)
        {
            var buffer = new byte[ReadBufferBytes];
            bool interpreting = true;
            try
            {
                while (true)
                {
                    int read = stdout.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;   // ffmpeg closed the pipe: this track has ended
                    if (!interpreting) continue;

                    try
                    {
                        foreach (var frame in _framer.Append(buffer, read))
                        {
                            Interlocked.Increment(ref _framesRead);
                            if (!_firstFrameLogged)
                            {
                                _firstFrameLogged = true;
                                Note("INFO", $"[PreviewTap] Drain: track={_track} first frame, {frame.Length} bytes");
                            }
                            if (_publishing) Offer(frame);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Keep reading, stop interpreting. The recording outranks the picture.
                        interpreting = false;
                        Note("ERROR", $"[PreviewTap] Drain: track={_track} stopped interpreting preview "
                                      + "frames; the pipe is still being drained so the recording is "
                                      + $"unaffected{Environment.NewLine}{ex}");
                    }
                }
                Note("INFO", $"[PreviewTap] Drain: track={_track} ended at end of stream "
                             + $"(framesRead={FramesRead} framesPublished={FramesPublished} "
                             + $"framesDropped={FramesDropped} oversizeDrops={_framer.OversizeDrops})");
            }
            catch (Exception ex)
            {
                Note("ERROR", $"[PreviewTap] Drain FAILED: track={_track} - the preview stream is "
                              + $"over; the recording continues{Environment.NewLine}{ex}");
            }
        }

        /// <summary>
        /// Hand the newest frame to the publisher. THE WHOLE OF THE DRAIN'S CONTACT WITH PUBLISHING,
        /// and every operation in it is wait-free: an interlocked pointer swap and a kernel event set.
        /// There is no lock the publisher could be holding while it is stuck in a filesystem call,
        /// and there is no queue that can grow.
        ///
        /// A frame superseded before the publisher takes it is COUNTED, not silently lost: a drop
        /// count that climbs says the filesystem is slow, and a preview that quietly showed every
        /// third frame with no number anywhere would say nothing at all.
        /// </summary>
        private void Offer(byte[] frame)
        {
            _publisherIdle.Reset();
            if (Interlocked.Exchange(ref _latest, frame) != null)
                Interlocked.Increment(ref _framesDropped);
            _publisherWork.Set();
        }

        /// <summary>
        /// Say something in the log WITHOUT touching the log. Callable from the drain thread and from
        /// the WPF UI thread, because all it does is enqueue: the publisher thread - the one that is
        /// allowed to block - does the appending.
        ///
        /// Over the ceiling the note is dropped and COUNTED, never silently discarded: a non-zero
        /// drop count in the flush line says the publisher stopped, which is the only way this bound
        /// can be reached at three lines a recording.
        /// </summary>
        private void Note(string level, string message)
        {
            if (_notes.Count >= MaxPendingNotes)
            {
                Interlocked.Increment(ref _notesDropped);
                return;
            }
            _publisherIdle.Reset();
            _notes.Enqueue((level, message));
            _publisherWork.Set();
        }

        /// <summary>Write out whatever the drain asked to be logged. Runs on the publisher thread,
        /// and on the disposing thread once the publisher has stopped.</summary>
        private void FlushNotes()
        {
            while (_notes.TryDequeue(out var note))
            {
                if (note.Level == "ERROR") Log.Error(note.Message);
                else if (note.Level == "WARN") Log.Warn(note.Message);
                else Log.Info(note.Message);
            }

            long dropped = Interlocked.Exchange(ref _notesDropped, 0);
            if (dropped > 0)
                Log.Warn($"[PreviewTap] {dropped} log line(s) from the {_track} preview drain were "
                         + "dropped because the publisher could not keep up. The drain itself never "
                         + "waited for it, so the recording is unaffected.");
        }

        /// <summary>
        /// The publisher loop: everything that can fail, block, or take an unbounded amount of time.
        /// A THREAD ENTRY POINT, hence the try/catch - and unlike the drain's, this catch really is
        /// the end of the line for the preview: if this thread dies the HUD goes stale and says so,
        /// and the recording is untouched, because the drain does not know this thread exists.
        /// </summary>
        private void PublishLoop()
        {
            try
            {
                while (true)
                {
                    _publisherWork.WaitOne(PublisherIdleWakeMs);

                    bool remove = Interlocked.Exchange(ref _removeFrameFile, 0) == 1;
                    var frame = Interlocked.Exchange(ref _latest, null);

                    FlushNotes();
                    if (remove) RemoveFrameFile();
                    if (frame != null) Publish(frame);

                    if (Volatile.Read(ref _latest) == null && Volatile.Read(ref _removeFrameFile) == 0
                        && _notes.IsEmpty)
                    {
                        _publisherIdle.Set();
                        if (_disposed) break;
                    }
                }
                Log.Info($"[PreviewTap] PublishLoop: track={_track} ended "
                         + $"(framesPublished={FramesPublished} framesDropped={FramesDropped})");
            }
            catch (Exception ex)
            {
                Log.Error($"[PreviewTap] PublishLoop FAILED: track={_track} - the preview stops "
                          + "updating and the HUD will say so; the recording is unaffected", ex);
            }
        }

        /// <summary>
        /// Write one frame where the HUD reads it. Runs on the publisher thread and nowhere else.
        /// A failure here is the preview's own and is reported as a WARNING - never rethrown, and
        /// never reaching the drain, because the drain belongs to the recording (AC10).
        /// </summary>
        private void Publish(byte[] frame)
        {
            try
            {
                _writeFrame(frame);
                Interlocked.Increment(ref _framesPublished);
                if (_publishFailed)
                {
                    _publishFailed = false;
                    Log.Info($"[PreviewTap] Publish: track={_track} recovered - frames are being written again");
                }
            }
            catch (Exception ex)
            {
                if (!_publishFailed)
                {
                    _publishFailed = true;
                    Log.Warn($"[PreviewTap] Publish FAILED: track={_track} frame={_framePath} - {ex.Message}. "
                             + "The preview will go stale and say so; the recording is unaffected.");
                }
            }
        }

        /// <summary>A temporary file and a rename over the target, so a reader never sees a
        /// half-written image.</summary>
        private void WriteFrameToDisk(byte[] frame)
        {
            File.WriteAllBytes(_tempPath, frame);
            File.Move(_tempPath, _framePath, overwrite: true);
        }

        private void RemoveFrameFile()
        {
            try
            {
                if (File.Exists(_framePath)) File.Delete(_framePath);
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"[PreviewTap] RemoveFrameFile: track={_track} - {ex.Message}");
            }
        }

        /// <summary>
        /// Stop previewing. Safe to call from the stop path and safe to call twice. It does NOT stop
        /// ffmpeg and it waits on nothing unbounded: the pump ends by itself when ffmpeg closes the
        /// pipe, the publisher ends when it sees the disposal, and BOTH joins are bounded - a
        /// publisher stuck in a filesystem call is the exact scenario this design exists for.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _publishing = false;
            Interlocked.Exchange(ref _latest, null);
            Interlocked.Exchange(ref _removeFrameFile, 1);
            _publisherIdle.Reset();
            _disposed = true;
            _publisherWork.Set();
            Log.Info($"[PreviewTap] Dispose: track={_track} framesRead={FramesRead} "
                     + $"framesPublished={FramesPublished} framesDropped={FramesDropped}");

            var publisher = _publisher;
            if (publisher != null && publisher.IsAlive && !publisher.Join(PublisherJoinTimeoutMs))
                Log.Warn($"[PreviewTap] Dispose: the {_track} preview publisher did not finish within "
                         + $"{PublisherJoinTimeoutMs}ms - it is a background thread and ends with the "
                         + "process. The recording is unaffected: the drain never waited for it.");

            var pump = _pump;
            if (pump != null && pump.IsAlive && !pump.Join(JoinTimeoutMs))
                Log.Warn($"[PreviewTap] Dispose: the {_track} preview pump did not finish within "
                         + $"{JoinTimeoutMs}ms - it is a background thread and ends with the process");

            // Whatever the drain asked to have logged and the publisher never reached. This thread is
            // the stop path, not the drain and not the UI, so it may block on the logger.
            FlushNotes();
            RemoveFrameFile();
        }
    }
}
