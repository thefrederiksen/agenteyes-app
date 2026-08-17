using System;
using System.IO;
using System.Threading.Tasks;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The one place manifest.json is written (issue #155): atomic replacement, and a
    /// read-modify-write that reads what is on disk NOW rather than what the caller saw earlier.
    /// </summary>
    [Collection(ManifestSeamCollection.Name)]   // shares the static InterruptBeforeReplace seam
    public sealed class ManifestStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public ManifestStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AgentEyes-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "manifest.json");
        }

        public void Dispose()
        {
            ManifestStore.InterruptBeforeReplace = null;
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static Manifest Seed(string label = "original") => new()
        {
            Mode = "video",
            Label = label,
            DurationSeconds = 12.5,
            VideoFile = "recording.mp4",
        };

        private string[] TempFiles() => Directory.GetFiles(_dir, "manifest.json.*.tmp");

        // ---- criterion 2: an interrupted write never damages the original ----

        [Fact]
        public void InterruptedWrite_LeavesTheOriginalManifestIntactAndParseable()
        {
            ManifestStore.Replace(_dir, Seed());
            string before = File.ReadAllText(_path);

            // Simulate the process dying after the new content is written but before it replaces the
            // live file - the exact window that used to leave truncated JSON behind.
            ManifestStore.InterruptBeforeReplace = temp =>
            {
                if (temp.StartsWith(_dir, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("simulated kill between the temp write and the replace");
            };

            Assert.Throws<IOException>(() => ManifestStore.Update(_dir, m => m.Label = "clobbered"));
            ManifestStore.InterruptBeforeReplace = null;

            Assert.Equal(before, File.ReadAllText(_path));     // byte for byte, untouched
            Assert.Equal("original", Manifest.Load(_dir).Label); // and still parseable
        }

        [Fact]
        public void InterruptedWrite_TheNextWriteStillSucceeds()
        {
            ManifestStore.Replace(_dir, Seed());
            ManifestStore.InterruptBeforeReplace = temp =>
            {
                if (temp.StartsWith(_dir, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("simulated kill between the temp write and the replace");
            };
            Assert.Throws<IOException>(() => ManifestStore.Update(_dir, m => m.Label = "clobbered"));
            ManifestStore.InterruptBeforeReplace = null;

            // A killed write leaves its temp behind (nothing is running to clean it up). The next
            // write must not trip over it: each write uses its own name and never adopts a temp it
            // did not create.
            Assert.Single(TempFiles());

            ManifestStore.Update(_dir, m => m.Label = "after the interruption");
            Assert.Equal("after the interruption", Manifest.Load(_dir).Label);
        }

        [Fact]
        public void ATruncatedManifest_IsNotReadAsAnEmptyRecording()
        {
            // Why the atomic write matters: a half-written manifest is not a manifest with missing
            // fields, it is unreadable - the reader throws and every pass over that recording has to
            // deal with it. This pins that a truncated file is never quietly accepted.
            ManifestStore.Replace(_dir, Seed());
            string whole = File.ReadAllText(_path);
            File.WriteAllText(_path, whole.Substring(0, whole.Length / 2));

            Assert.ThrowsAny<Exception>(() => Manifest.Load(_dir));
        }

        [Fact]
        public void ASuccessfulWrite_LeavesNoTemporaryFileBehind()
        {
            ManifestStore.Replace(_dir, Seed());
            ManifestStore.Update(_dir, m => m.Label = "changed");

            Assert.Empty(TempFiles());
            Assert.Equal("changed", Manifest.Load(_dir).Label);
        }

        // ---- the canonical mutation path ----

        [Fact]
        public void Update_AppliesToWhatIsOnDiskNow_NotToWhatTheCallerLastSaw()
        {
            ManifestStore.Replace(_dir, Seed());
            var stale = Manifest.Load(_dir);           // a copy taken before other work happened
            ManifestStore.Update(_dir, m => m.DisplayName = "renamed by someone else");

            // The stale copy has no DisplayName; updating through the store must not push that
            // absence back over the rename.
            Assert.Null(stale.DisplayName);
            ManifestStore.Update(_dir, m => m.Title = "titled later");

            var loaded = Manifest.Load(_dir);
            Assert.Equal("renamed by someone else", loaded.DisplayName);
            Assert.Equal("titled later", loaded.Title);
        }

        [Fact]
        public void Update_ReturnsTheManifestAsItWasWritten()
        {
            ManifestStore.Replace(_dir, Seed());
            var written = ManifestStore.Update(_dir, m => m.ThumbAttempts = 2);

            Assert.Equal(2, written.ThumbAttempts);
            Assert.Equal(2, Manifest.Load(_dir).ThumbAttempts);
        }

        [Fact]
        public void Update_TwoWritersAtOnce_NeitherLosesTheOthersField()
        {
            // The load-mutate-save race, run for real: two counters incremented concurrently. Every
            // increment must land, which is only true because the load happens inside the lock.
            ManifestStore.Replace(_dir, Seed());
            const int rounds = 50;

            Parallel.Invoke(
                () => { for (int i = 0; i < rounds; i++) ManifestStore.Update(_dir, m => m.TranscribeAttempts++); },
                () => { for (int i = 0; i < rounds; i++) ManifestStore.Update(_dir, m => m.ThumbAttempts++); });

            var loaded = Manifest.Load(_dir);
            Assert.Equal(rounds, loaded.TranscribeAttempts);
            Assert.Equal(rounds, loaded.ThumbAttempts);
        }

        [Fact]
        public void Update_NoManifestThere_FailsLoudly()
        {
            // No silent create: a caller that thinks it is changing a recording, and is not, hears
            // about it (the same UsageException Manifest.Load has always thrown).
            string empty = Path.Combine(_dir, "empty");
            Directory.CreateDirectory(empty);

            Assert.Throws<UsageException>(() => ManifestStore.Update(empty, m => m.Label = "x"));
        }

        [Fact]
        public void Update_TheMutationThrows_LeavesTheManifestUntouched()
        {
            ManifestStore.Replace(_dir, Seed());
            string before = File.ReadAllText(_path);

            Assert.Throws<InvalidOperationException>(() =>
                ManifestStore.Update(_dir, _ => throw new InvalidOperationException("the caller's own failure")));

            Assert.Equal(before, File.ReadAllText(_path));
            Assert.Empty(TempFiles());
        }

        // ---- Replace: whole-content writes ----

        [Fact]
        public void Replace_WritesTheWholeContent()
        {
            ManifestStore.Replace(_dir, Seed());
            ManifestStore.Replace(_dir, new Manifest { Mode = "audio", Label = "second" });

            var loaded = Manifest.Load(_dir);
            Assert.Equal("audio", loaded.Mode);
            Assert.Equal("second", loaded.Label);
            Assert.Null(loaded.VideoFile);     // the first manifest is gone, as Replace promises
        }

        [Fact]
        public void Replace_FirstWriteOfARecording_CreatesTheFile()
        {
            Assert.False(File.Exists(_path));
            ManifestStore.Replace(_dir, Seed());

            Assert.True(File.Exists(_path));
            Assert.Equal("original", Manifest.Load(_dir).Label);
        }

        // ---- guards ----

        [Fact]
        public void Update_NullMutation_Throws() =>
            Assert.Throws<ArgumentNullException>(() => ManifestStore.Update(_dir, null!));

        [Fact]
        public void Replace_NullManifest_Throws() =>
            Assert.Throws<ArgumentNullException>(() => ManifestStore.Replace(_dir, null!));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Update_NoDirectory_Throws(string dir) =>
            Assert.Throws<ArgumentException>(() => ManifestStore.Update(dir, _ => { }));
    }
}
