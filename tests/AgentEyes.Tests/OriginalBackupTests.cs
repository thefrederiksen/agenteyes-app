using System;
using System.IO;
using System.Linq;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    // Issue #83: the pure raw->".original" name mapping that replaces the old TryDelete calls.
    // Each branch must map to exactly the names in the issue table. No ffmpeg needed.
    public class OriginalBackupTests
    {
        private static (string From, string To)[] Plan(string mode, AudioSourceKind src) =>
            OriginalBackup.Plan(mode, src).Select(r => (r.From, r.To)).ToArray();

        [Fact]
        public void Plan_audio_system_preserves_native_loopback_as_audio_original()
        {
            Assert.Equal(new[] { ("sys_native.wav", "audio.original.wav") }, Plan("audio", AudioSourceKind.System));
        }

        [Fact]
        public void Plan_audio_mixed_preserves_mic_and_system_originals()
        {
            Assert.Equal(
                new[] { ("mic.wav", "mic.original.wav"), ("sys_native.wav", "system.original.wav") },
                Plan("audio", AudioSourceKind.Mixed));
        }

        [Fact]
        public void Plan_video_mic_preserves_raw_as_recording_original()
        {
            Assert.Equal(new[] { ("raw.mp4", "recording.original.mp4") }, Plan("video", AudioSourceKind.Mic));
        }

        [Fact]
        public void Plan_video_mixed_preserves_raw_video_and_system_original()
        {
            Assert.Equal(
                new[] { ("raw.mp4", "recording.original.mp4"), ("sys_native.wav", "system.original.wav") },
                Plan("video", AudioSourceKind.Mixed));
        }

        [Fact]
        public void Plan_video_system_preserves_raw_video_and_system_original()
        {
            Assert.Equal(
                new[] { ("raw.mp4", "recording.original.mp4"), ("sys_native.wav", "system.original.wav") },
                Plan("video", AudioSourceKind.System));
        }

        // mic-only AUDIO writes audio.wav directly with no processing, so there is nothing to back up.
        [Fact]
        public void Plan_audio_mic_preserves_nothing()
        {
            Assert.Empty(OriginalBackup.Plan("audio", AudioSourceKind.Mic));
        }

        // Preserve renames the present raw files (overwrite-safe), never deletes them, and reports
        // the backups it created. A missing raw file is simply skipped.
        [Fact]
        public void Preserve_renames_present_originals_and_skips_absent()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-orig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "mic.wav"), "mic-bytes");
                // sys_native.wav intentionally absent -> that rename is skipped.

                var preserved = OriginalBackup.Preserve(dir, "audio", AudioSourceKind.Mixed);

                Assert.Equal(new[] { "mic.original.wav" }, preserved.ToArray());
                Assert.False(File.Exists(Path.Combine(dir, "mic.wav")));          // moved, not copied
                Assert.True(File.Exists(Path.Combine(dir, "mic.original.wav")));  // preserved
                Assert.Equal("mic-bytes", File.ReadAllText(Path.Combine(dir, "mic.original.wav")));
                Assert.False(File.Exists(Path.Combine(dir, "system.original.wav")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
