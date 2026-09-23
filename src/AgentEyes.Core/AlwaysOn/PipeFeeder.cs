using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// Carries the system sound from the WASAPI callback to always-on's ffmpeg WITHOUT EVER BLOCKING
    /// THE CALLBACK (issue #66, review finding 4). The callback hands each buffer to a bounded queue
    /// and returns; a thread of its own writes the queue into the pipe.
    ///
    /// A reader that is alive but has stopped reading - an ffmpeg that hangs - fills the queue. The
    /// feeder then drops what does not fit and says so through <see cref="Stalled"/>, and the always-on
    /// supervisor restarts the capture. Before this, the callback wrote the pipe itself: a stalled
    /// ffmpeg filled the 1 MiB pipe in under three seconds and blocked the audio thread for good, the
    /// level meter went silent, and nothing noticed because the process was still running.
    /// </summary>
    internal sealed class PipeFeeder : Stream
    {
        private readonly Stream _pipe;
        private readonly long _maxQueuedBytes;
        private readonly BlockingCollection<byte[]> _queue = new();
        private readonly Thread _thread;
        private long _queuedBytes;
        private volatile bool _stalled;
        private volatile bool _broken;

        /// <param name="pipe">The connected pipe ffmpeg reads.</param>
        /// <param name="maxQueuedBytes">How much audio may wait before the reader counts as stalled.</param>
        public PipeFeeder(Stream pipe, long maxQueuedBytes)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            if (maxQueuedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
            _maxQueuedBytes = maxQueuedBytes;
            _thread = new Thread(Drain) { IsBackground = true, Name = "AgentEyes-alwayson-pipe" };
            _thread.Start();
        }

        /// <summary>True once the reader stopped taking audio for longer than the queue holds.</summary>
        public bool Stalled => _stalled;

        /// <summary>True once the pipe itself failed (the reader went away).</summary>
        public bool Broken => _broken;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_broken || count <= 0 || _queue.IsAddingCompleted) return;
            if (Interlocked.Read(ref _queuedBytes) + count > _maxQueuedBytes)
            {
                if (!_stalled)
                {
                    _stalled = true;
                    Log.Warn($"[PipeFeeder] Write: ffmpeg has not taken the system sound for "
                             + $"{_maxQueuedBytes / 1024} KB - it is stalled; dropping audio until it is restarted");
                }
                return;
            }
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            Interlocked.Add(ref _queuedBytes, count);
            try { _queue.Add(copy); }
            catch (InvalidOperationException) { /* stopping: the queue was closed under us */ }
        }

        private void Drain()
        {
            // Thread entry point: the failure is recorded, never thrown into nowhere.
            try
            {
                foreach (var chunk in _queue.GetConsumingEnumerable())
                {
                    _pipe.Write(chunk, 0, chunk.Length);
                    Interlocked.Add(ref _queuedBytes, -chunk.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _broken = true;
                Log.Warn($"[PipeFeeder] Drain: the system-sound pipe closed ({ex.Message})");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _queue.CompleteAdding();
                // The drain may be blocked in a write to a reader that stopped; do not wait long for
                // it. Disposing the pipe (the owner does that) ends that write.
                _thread.Join(TimeSpan.FromSeconds(2));
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
