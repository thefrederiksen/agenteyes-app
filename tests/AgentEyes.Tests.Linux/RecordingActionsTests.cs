using System.IO;
using AgentEyes.Headless;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class RecordingActionsTests
    {
        private static (string root, string id, string dir) NewRecording()
        {
            string root = LinuxTestIsolation.NewTempDir("lib");
            string id = "2026-10-15_120000_video";
            string dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"Mode\":\"video\",\"Title\":\"Old title\"}");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "x");
            return (root, id, dir);
        }

        [Fact]
        public void Rename_ExistingRecording_WritesDisplayNameToManifest()
        {
            var (root, id, dir) = NewRecording();

            RecordingActions.Rename(id, "  Demo for the customer  ", root);

            Assert.Equal("Demo for the customer", Manifest.Load(dir).DisplayName);
        }

        [Fact]
        public void Rename_EmptyName_Throws()
        {
            var (root, id, _) = NewRecording();

            Assert.Throws<UsageException>(() => RecordingActions.Rename(id, "   ", root));
        }

        [Fact]
        public void Delete_FreeRecording_RemovesTheFolder()
        {
            var (root, id, dir) = NewRecording();

            RecordingActions.Delete(id, root);

            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void Delete_RecordingBeingProcessed_IsRefusedAndKept()
        {
            var (root, id, dir) = NewRecording();
            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "test", out var ticket));
            try
            {
                Assert.Throws<RecordingBusyException>(() => RecordingActions.Delete(id, root));
                Assert.True(Directory.Exists(dir));
            }
            finally
            {
                RecordingWorkset.Release(ticket);
            }
        }

        [Theory]
        [InlineData("..")]
        [InlineData("../etc")]
        [InlineData("a/b")]
        [InlineData("")]
        public void RequireDir_PathLikeId_IsRefused(string id)
        {
            Assert.Throws<UsageException>(() => RecordingActions.RequireDir(id, LinuxTestIsolation.NewTempDir("lib")));
        }

        [Fact]
        public void RequireDir_UnknownId_IsNotFound()
        {
            Assert.Throws<RecordingNotFoundException>(() => RecordingActions.RequireDir("nope", LinuxTestIsolation.NewTempDir("lib")));
        }
    }
}
