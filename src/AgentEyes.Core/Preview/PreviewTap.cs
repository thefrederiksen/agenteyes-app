using System;
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
    ///  - THE DRAIN IS UNCONDITIONAL. ffmpeg's stdout is read for as long as the process lives,
    ///    whether or not anything is showing the preview and whether or not publishing works. It has
    ///    to be: an anonymous pipe nobody reads fills, and a full pipe BLOCKS the ffmpeg that is
    ///    writing the recording. Draining is the one thing here that is not allowed to stop, so it is
    ///    the one thing that never depends on anything else succeeding.
    ///  - PUBLISHING IS ALLOWED TO FAIL, AND ONLY PUBLISHING. Writing the frame out is downstream of
    ///    the drain and of ffmpeg. If the preview directory is removed, made read-only or filled, the
    ///    write fails, a WARNING is logged, the HUD goes stale and says so - and the recording is not
    ///    touched, because ffmpeg is still being read.
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

        private const int ReadBufferBytes = 64 * 1024;

        private readonly string _track;
        private readonly string _framePath;
        private readonly string _tempPath;
        private readonly MjpegFramer _framer = new();

        private Thread? _pump;
        private volatile bool _publishing;
        private volatile bool _disposed;
        private volatile bool _publishFailed;
        private long _framesRead;
        private long _framesPublished;
        private bool _firstFrameLogged;

        private PreviewTap(string track, string framePath)
        {
            _track = track;
            _framePath = framePath;
            _tempPath = framePath + ".tmp";
        }

        /// <summary>The file the HUD reads. It exists only while frames are being published.</summary>
        public string FramePath => _framePath;

        /// <summary>Which recorded track this previews ("screen", "camera").</summary>
        public string Track => _track;

        /// <summary>Whole frames taken off the pipe since the recording started.</summary>
        public long FramesRead => Interlocked.Read(ref _framesRead);

        /// <summary>Frames actually written to <see cref="FramePath"/>. Lower than
        /// <see cref="FramesRead"/> by exactly the frames that arrived while the preview was hidden.</summary>
        public long FramesPublished => Interlocked.Read(ref _framesPublished);

        /// <summary>True once a publish has failed and not yet succeeded again. The HUD does not read
        /// this - it judges the frame file itself - but the log and /status do.</summary>
        public bool PublishFailed => _publishFailed;

        /// <summary>
        /// Whether frames are being written out. False is the DEFAULT and the cheap state: the pipe
        /// is still drained (it must be), the frames are still parsed, and then discarded. Flipping
        /// this is the whole cost of showing or hiding the preview mid-recording - no ffmpeg is
        /// restarted, no output is added, and the recording never learns that anything changed.
        /// </summary>
        public bool Publishing
        {
            get => _publishing;
            set
            {
                if (_publishing == value) return;
                _publishing = value;
                Log.Info($"[PreviewTap] Publishing({_track}) -> {value}");
                if (!value) RemoveFrameFile();
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
        /// is what happens when publishing FAILS, and reaching that on the real path would mean
        /// deleting a directory inside the user's own %LOCALAPPDATA%.
        /// </summary>
        internal static PreviewTap? TryCreateAt(string track, string framePath)
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
            return new PreviewTap(track, framePath);
        }

        /// <summary>
        /// Start draining <paramref name="stdout"/> on a dedicated background thread. Called by the
        /// recorder the instant ffmpeg is running - never later, because the pipe starts filling then.
        /// </summary>
        public void Pump(Stream stdout)
        {
            if (stdout == null) throw new ArgumentNullException(nameof(stdout));
            if (_pump != null) throw new InvalidOperationException($"the {_track} preview tap is already pumping");

            Log.Info($"[PreviewTap] Pump: track={_track} starting");
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
        /// The drain loop. This is a THREAD ENTRY POINT, which is why it carries a try/catch at all -
        /// and the catch is the last line of the recording's defence: an exception escaping this
        /// thread would stop the pipe being read, and a full pipe blocks the ffmpeg writing the
        /// recording. So a failure in framing or publishing drops this to a READ-AND-DISCARD loop and
        /// keeps going.
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
                                Log.Info($"[PreviewTap] Drain: track={_track} first frame, {frame.Length} bytes");
                            }
                            if (_publishing) Publish(frame);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Keep reading, stop interpreting. The recording outranks the picture.
                        interpreting = false;
                        Log.Error($"[PreviewTap] Drain: track={_track} stopped interpreting preview frames; "
                                  + "the pipe is still being drained so the recording is unaffected", ex);
                    }
                }
                Log.Info($"[PreviewTap] Drain: track={_track} ended at end of stream "
                         + $"(framesRead={FramesRead} framesPublished={FramesPublished})");
            }
            catch (Exception ex)
            {
                Log.Error($"[PreviewTap] Drain FAILED: track={_track} - the preview stream is over; "
                          + "the recording continues", ex);
            }
        }

        /// <summary>
        /// Write one frame where the HUD reads it: a temporary file, then a rename over the target.
        /// A failure here is the preview's own and is reported as a WARNING - never rethrown into the
        /// drain, because the drain belongs to the recording (AC10).
        /// </summary>
        private void Publish(byte[] frame)
        {
            try
            {
                File.WriteAllBytes(_tempPath, frame);
                File.Move(_tempPath, _framePath, overwrite: true);
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
        /// pipe, and this only joins it and removes the published frame.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _publishing = false;
            Log.Info($"[PreviewTap] Dispose: track={_track} framesRead={FramesRead} framesPublished={FramesPublished}");

            var pump = _pump;
            if (pump != null && pump.IsAlive && !pump.Join(JoinTimeoutMs))
                Log.Warn($"[PreviewTap] Dispose: the {_track} preview pump did not finish within "
                         + $"{JoinTimeoutMs}ms - it is a background thread and ends with the process");

            RemoveFrameFile();
        }
    }
}
