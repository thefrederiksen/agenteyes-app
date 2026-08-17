using System.IO;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class ManifestTests
    {
        [Fact]
        public void Save_then_Load_roundtrips_fields()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var m = new Manifest
                {
                    Mode = "video",
                    Label = "demo",
                    MonitorIndex = 2,
                    MonitorName = "DISPLAY2",
                    Microphone = "Yeti",
                    DurationSeconds = 12.5,
                    Region = new[] { 10, 20, 640, 480 },
                    VideoFile = "recording.mp4",
                };
                m.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 7, File = "shots/00m07s.png" });
                ManifestStore.Replace(dir, m);

                var loaded = Manifest.Load(dir);
                Assert.Equal("video", loaded.Mode);
                Assert.Equal("demo", loaded.Label);
                Assert.Equal(2, loaded.MonitorIndex);
                Assert.Equal(12.5, loaded.DurationSeconds);
                Assert.NotNull(loaded.Region);
                Assert.Equal(640, loaded.Region![2]);
                Assert.Single(loaded.Shots);
                Assert.Equal("shots/00m07s.png", loaded.Shots[0].File);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Null_region_is_omitted_from_json()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ManifestStore.Replace(dir, new Manifest { Mode = "shot", Region = null });
                string json = File.ReadAllText(Path.Combine(dir, "manifest.json"));
                Assert.DoesNotContain("\"Region\"", json);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Load_missing_directory_throws_usage()
        {
            Assert.Throws<UsageException>(() => Manifest.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-xyz")));
        }

        [Fact]
        public void Transcripts_map_roundtrips()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var m = new Manifest { Mode = "video", Label = "demo", Transcript = "transcript.json" };
                m.Transcripts["en"] = "transcript.en.vtt";
                ManifestStore.Replace(dir, m);

                var loaded = Manifest.Load(dir);
                Assert.Equal("transcript.en.vtt", Assert.Contains("en", loaded.Transcripts));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Load_old_manifest_without_transcripts_map_does_not_throw_and_map_is_empty()
        {
            // Issue #98 backward compatibility: an OLD manifest.json written before the per-language
            // transcript map existed has no "Transcripts" property. Loading it must not throw, the
            // map must be empty, and the legacy transcript.json is still identified.
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string oldJson =
                    "{\"Tool\":\"AgentEyes\",\"Mode\":\"video\",\"Label\":\"legacy\"," +
                    "\"CreatedUtc\":\"2026-01-01T00:00:00.0000000Z\",\"DurationSeconds\":10.0," +
                    "\"VideoFile\":\"recording.mp4\",\"Transcript\":\"transcript.json\"}";
                File.WriteAllText(Path.Combine(dir, "manifest.json"), oldJson);

                var loaded = Manifest.Load(dir);   // must not throw
                Assert.NotNull(loaded.Transcripts);
                Assert.Empty(loaded.Transcripts);
                Assert.Equal("transcript.json", loaded.Transcript);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Last_title_attempt_stamp_roundtrips()
        {
            // Issue #148: the stamp is what turns TitleAttempts into a per-window budget.
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var when = new System.DateTime(2026, 8, 11, 15, 11, 33, System.DateTimeKind.Utc);
                ManifestStore.Replace(dir, new Manifest { Mode = "video", TitleAttempts = 3, LastTitleAttemptUtc = when });

                var loaded = Manifest.Load(dir);
                Assert.Equal(3, loaded.TitleAttempts);
                Assert.Equal(when, loaded.LastTitleAttemptUtc);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Load_old_manifest_without_title_attempt_stamp_reads_as_no_window()
        {
            // Issue #148 backward compatibility: the two recordings stranded on 2026-08-11 carry
            // TitleAttempts but no stamp. That must load as null, i.e. no window in progress.
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string oldJson =
                    "{\"Tool\":\"AgentEyes\",\"Mode\":\"video\",\"Label\":\"video\"," +
                    "\"CreatedUtc\":\"2026-08-11T15:11:33.2673728Z\",\"DurationSeconds\":2202.13," +
                    "\"VideoFile\":\"recording.mp4\",\"TranscribeAttempts\":0,\"TitleAttempts\":3}";
                File.WriteAllText(Path.Combine(dir, "manifest.json"), oldJson);

                var loaded = Manifest.Load(dir);   // must not throw
                Assert.Equal(3, loaded.TitleAttempts);
                Assert.Null(loaded.LastTitleAttemptUtc);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
