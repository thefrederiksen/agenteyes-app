using System;
using System.IO;
using Xunit;
using AgentEyes;
using AgentEyes.Audio;

namespace AgentEyes.Tests
{
    public class RecordingServiceTests
    {
        // These run on the interactive machine (real monitors, ffmpeg on PATH), like MonitorsTests.
        // Regression: a preset can reference a microphone that no longer exists (e.g. "Remote Audio"
        // saved over RDP, then used on console). Start must throw a clear UsageException and must
        // NOT leave an empty recording folder behind.
        private const string BogusMic = "no-such-microphone-xyz";

        private static int RecordingDirCount() =>
            Directory.Exists(RecordingPaths.Root) ? Directory.GetDirectories(RecordingPaths.Root).Length : 0;

        [Fact]
        public void StartVideo_unknown_mic_throws_and_leaves_no_dir()
        {
            var svc = new RecordingService();
            int before = RecordingDirCount();
            Assert.Throws<UsageException>(() =>
                svc.StartVideo(1, AudioSourceKind.Mixed, BogusMic, null, new AudioMixOptions(), 30));
            Assert.Equal(before, RecordingDirCount());
            Assert.False(svc.IsRecording);
        }

        [Fact]
        public void StartAudio_unknown_mic_throws_and_leaves_no_dir()
        {
            var svc = new RecordingService();
            int before = RecordingDirCount();
            Assert.Throws<UsageException>(() =>
                svc.StartAudio(1, AudioSourceKind.Mic, BogusMic, new AudioMixOptions()));
            Assert.Equal(before, RecordingDirCount());
            Assert.False(svc.IsRecording);
        }

        [Fact]
        public void StartAudio_mixed_unknown_mic_throws_and_leaves_no_dir()
        {
            var svc = new RecordingService();
            int before = RecordingDirCount();
            Assert.Throws<UsageException>(() =>
                svc.StartAudio(1, AudioSourceKind.Mixed, BogusMic, new AudioMixOptions()));
            Assert.Equal(before, RecordingDirCount());
            Assert.False(svc.IsRecording);
        }

        [Fact]
        public void StartAudio_source_none_throws()
        {
            var svc = new RecordingService();
            Assert.Throws<UsageException>(() =>
                svc.StartAudio(1, AudioSourceKind.None, null, new AudioMixOptions()));
        }

        // ---- issue #77: deferred mux (FinalizePending) -----------------------

        // FinalizePending is a no-op when nothing was deferred (e.g. mic-only audio): it must not
        // throw on a missing/absent PendingMux block and must leave the manifest untouched.
        [Fact]
        public void FinalizePending_no_pending_mux_is_noop()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var manifest = new Manifest { Mode = "audio", AudioFile = "audio.wav", DurationSeconds = 12.5, PendingMux = null };
                ManifestStore.Replace(dir, manifest);

                RecordingService.FinalizePending(dir);   // must not throw

                var reloaded = Manifest.Load(dir);
                Assert.Null(reloaded.PendingMux);
                Assert.Equal(12.5, reloaded.DurationSeconds);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // The PendingMux block must survive a manifest save/load round-trip (it is the durable
        // description of the deferred work the background pass reads back after the HUD has closed).
        [Fact]
        public void PendingMux_round_trips_through_manifest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var manifest = new Manifest
                {
                    Mode = "video",
                    VideoFile = "recording.mp4",
                    PendingMux = new Manifest.PendingMuxInfo
                    {
                        Mode = "video",
                        Source = "mixed",
                        RawVideo = "raw.mp4",
                        SysWav = "sys_native.wav",
                        FinalFile = "recording.mp4",
                        RawDurationSeconds = 42.0,
                        Options = new AudioMixOptions { SystemGain = 0.5 },
                    },
                };
                ManifestStore.Replace(dir, manifest);

                var reloaded = Manifest.Load(dir);
                Assert.NotNull(reloaded.PendingMux);
                Assert.Equal("mixed", reloaded.PendingMux!.Source);
                Assert.Equal("raw.mp4", reloaded.PendingMux.RawVideo);
                Assert.Equal("sys_native.wav", reloaded.PendingMux.SysWav);
                Assert.Equal("recording.mp4", reloaded.PendingMux.FinalFile);
                Assert.Equal(42.0, reloaded.PendingMux.RawDurationSeconds);
                Assert.Equal(0.5, reloaded.PendingMux.Options.SystemGain);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
