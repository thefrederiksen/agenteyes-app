using System;
using System.IO;
using System.Threading.Tasks;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #128 round two (D1): the one-writer-per-output guard must hold across PROCESSES, so it
    /// is a lock on disk (an exclusive handle on &lt;recording&gt;\mux.lock), not an in-memory claim.
    /// Compiled into all three suites: on Windows the lock is a sharing violation, on macOS/Linux it
    /// is .NET's exclusive flock for FileShare.None - a different mechanism, so each platform proves
    /// its own. "Another process" is stood in for by a second, independent handle on the same file:
    /// both a sharing violation and a flock conflict are per open handle, not per process, so a
    /// second handle in this process is refused by the OS exactly as another process's would be.
    /// The genuinely cross-process case (and release on process death) is proven on Windows by
    /// MuxCoordinatorTests.MuxLock_HeldByAnotherProcess_IsRefusedUntilThatProcessDies.
    /// </summary>
    public sealed class MuxLockTests : IDisposable
    {
        private readonly string _dir;

        public MuxLockTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agenteyes-muxlock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        private string LockPath => Path.Combine(_dir, MuxLock.FileName);

        /// <summary>An independent exclusive handle on the lock file - what another process holds.</summary>
        private FileStream HoldLikeAnotherProcess() =>
            new(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        /// <summary>
        /// True when even the most PERMISSIVE open of the lock file (share read+write) is refused right
        /// now. Permissive on purpose: a strict probe is refused by ANY existing handle, so it could
        /// not tell an exclusive lock from a shareable one (that mutation survived the first version).
        /// </summary>
        private bool IsLockedOnDisk()
        {
            try
            {
                using var probe = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        [Fact]
        public void TryAcquire_LockFileHeldByAnotherHandle_ReturnsNull()
        {
            using var other = HoldLikeAnotherProcess();
            Assert.Null(MuxLock.TryAcquire(_dir, "test"));
        }

        [Fact]
        public void TryAcquire_Free_HoldsTheFileExclusivelyUntilDisposed()
        {
            Assert.False(IsLockedOnDisk(), "broken fixture: the lock file is locked before anything took it");
            var held = MuxLock.TryAcquire(_dir, "test");
            Assert.NotNull(held);
            Assert.True(IsLockedOnDisk(), "MuxLock was acquired but another handle could still open mux.lock exclusively");
            held!.Dispose();
            Assert.False(IsLockedOnDisk(), "MuxLock was released but mux.lock is still locked");
        }

        [Fact]
        public void TryAcquire_SameThreadTwice_IsReentrantAndReleasesOnTheOutermostDispose()
        {
            var outer = MuxLock.TryAcquire(_dir, "outer");
            Assert.NotNull(outer);
            var inner = MuxLock.TryAcquire(_dir, "inner");
            Assert.NotNull(inner);
            Assert.True(MuxLock.IsHeldByCurrentThread(_dir));

            inner!.Dispose();
            Assert.True(IsLockedOnDisk(), "releasing the inner level dropped the lock while the outer level still held it");
            outer!.Dispose();
            Assert.False(IsLockedOnDisk());
            Assert.False(MuxLock.IsHeldByCurrentThread(_dir));
        }

        [Fact]
        public async Task TryAcquire_AnotherThreadOfThisProcess_IsRefused()
        {
            using var held = MuxLock.TryAcquire(_dir, "first");
            Assert.NotNull(held);
            var fromOtherThread = await Task.Factory.StartNew(
                () => MuxLock.TryAcquire(_dir, "second"),
                System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.Null(fromOtherThread);
        }

        [Fact]
        public void Acquire_HeldElsewhere_ThrowsSayingASecondWriterWasRefused()
        {
            using var other = HoldLikeAnotherProcess();
            var ex = Assert.Throws<UsageException>(() => MuxLock.Acquire(_dir, "test"));
            Assert.Contains("already running in another AgentEyes process", ex.Message);
            Assert.Contains("refusing to start a second writer", ex.Message);
        }

        [Fact]
        public void TryAcquire_MissingDirectory_ThrowsInsteadOfReportingBusy()
        {
            string missing = Path.Combine(_dir, "no-such-recording");
            Assert.Throws<UsageException>(() => MuxLock.TryAcquire(missing, "test"));
        }
    }
}
