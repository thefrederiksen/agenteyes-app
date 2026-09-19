using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Housekeeping;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The Housekeeper against real files and the real encoder (issues #55, #56).
    ///
    /// <see cref="HousekeepingPlanTests"/> proves the POLICY without a disk. This proves the part that
    /// only real bytes can: that the transcode is verified against the source before anything is
    /// deleted, that a report-only pass changes nothing at all, and that what happened is written into
    /// the manifest.
    ///
    /// It BUILDS its own recording rather than reading one of the machine's, and it builds it with the
    /// same shape the real thing has - a 48 kHz stereo 32-bit float preserved WAV, which is the exact
    /// format that made FLAC the wrong answer here. A test that needed the owner's own recordings to be
    /// present would skip on every other machine, and a skip is indistinguishable from a pass in every
    /// report this repository produces.
    /// </summary>
    public class HousekeeperEndToEndTests : IDisposable
    {
        private readonly string _root;

        public HousekeeperEndToEndTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-housekeeping-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp directory that outlives the test is not a test failure */ }
        }

        private static HousekeepingSettings Live(Action<HousekeepingSettings>? tweak = null)
        {
            var s = new HousekeepingSettings { ReportOnly = false };
            tweak?.Invoke(s);
            return s;
        }

        /// <summary>
        /// A recording directory shaped like a real one: a transcript, a preserved 32-bit float WAV, a
        /// derived transcriber input, and an oversized ffmpeg log.
        /// </summary>
        private string MakeRecording(string name, DateTime createdUtc, bool pinned = false)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);

            // The preserved original, in the real format: 48 kHz stereo 32-bit float PCM. Noise rather
            // than a tone, so it cannot compress to nothing and hide a difference.
            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "anoisesrc=d=3:c=pink:r=48000:a=0.5",
                "-ac", "2", "-c:a", "pcm_f32le",
                Path.Combine(dir, "system.original.wav"),
            }, "test fixture preserved wav");

            // The derived transcriber input: 16 kHz mono, as Package writes it.
            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-i", Path.Combine(dir, "system.original.wav"),
                "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                Path.Combine(dir, "audio_16k.wav"),
            }, "test fixture 16k wav");

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), new string('v', 4096));
            File.WriteAllText(Path.Combine(dir, "raw.mp4.ffmpeg.log"),
                string.Join("\r\n", Enumerable.Range(0, 40_000).Select(i => $"frame={i} progress line")));

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = name,
                CreatedUtc = createdUtc.ToString("o"),
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                DurationSeconds = 3,
                Keep = pinned,
                Files = new List<string> { "recording.mp4", "system.original.wav" },
                OriginalFiles = new List<string> { "system.original.wav" },
            });

            return dir;
        }

        /// <summary>Every file in a directory with its size - the before-and-after instrument.</summary>
        private static Dictionary<string, long> Snapshot(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'),
                              p => new FileInfo(p).Length,
                              StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void ReportOnly_ChangesNothingOnDisk()
        {
            MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var before = Snapshot(_root);

            var report = Housekeeper.Run(
                _root, new HousekeepingSettings { ReportOnly = true }, "test",
                () => false, new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

            var after = Snapshot(_root);

            // The pass found real work...
            Assert.True(report.BytesPlanned > 0, "a report-only pass over a real recording found nothing to plan");
            Assert.Contains(report.Recordings.SelectMany(r => r.Steps), s => s.Outcome == "planned");
            // ...and did none of it. Compared as a whole map, so an added file fails as loudly as a
            // removed or resized one.
            Assert.Equal(before.OrderBy(k => k.Key), after.OrderBy(k => k.Key));
        }

        [Fact]
        public void ALivePass_DeletesTheDerivedInput_AndRecordsIt()
        {
            string dir = MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            long derivedSize = new FileInfo(Path.Combine(dir, "audio_16k.wav")).Length;

            Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

            Assert.False(File.Exists(Path.Combine(dir, "audio_16k.wav")));
            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")), "the keeper was deleted");
            Assert.True(File.Exists(Path.Combine(dir, "transcript.json")), "the transcript was deleted");

            var record = Assert.Single(Manifest.Load(dir).Housekeeping
                .Where(h => h.File == "audio_16k.wav"));
            Assert.Equal(nameof(HousekeepingKind.DeleteDerived), record.Kind);
            Assert.Equal("done", record.Outcome);
            Assert.Equal(derivedSize, record.BytesReclaimed);
        }

        [Fact]
        public void ALivePass_TranscodesThePreservedWav_BitExactly_AndRenamesItInTheManifest()
        {
            string dir = MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            string wav = Path.Combine(dir, "system.original.wav");
            long wavSize = new FileInfo(wav).Length;

            Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

            string output = Path.Combine(dir, "system.original.wv");
            Assert.True(File.Exists(output), "the transcode produced nothing");
            Assert.False(File.Exists(wav), "the source WAV survived a verified transcode");
            Assert.True(new FileInfo(output).Length < wavSize, "the transcode did not make it smaller");

            var manifest = Manifest.Load(dir);
            Assert.Equal(new[] { "system.original.wv" }, manifest.OriginalFiles.ToArray());
            Assert.Contains("system.original.wv", manifest.Files);
            Assert.DoesNotContain("system.original.wav", manifest.OriginalFiles);

            var record = Assert.Single(manifest.Housekeeping
                .Where(h => h.Kind == nameof(HousekeepingKind.TranscodePreservedAudio)));
            Assert.Equal("done", record.Outcome);
            Assert.True(record.BitExact, "the transcode was not bit-exact, so it should never have replaced the source");
            Assert.True(record.BytesReclaimed > 0);

            // The record is the thing that makes the bit-exact claim answerable months later, so the
            // hashes it promises have to actually be in it - not declared on the type and left null.
            Assert.False(string.IsNullOrWhiteSpace(record.SourceHash), "the source hash never reached the record");
            Assert.False(string.IsNullOrWhiteSpace(record.OutputHash), "the output hash never reached the record");
            Assert.Equal(record.SourceHash, record.OutputHash);   // bit-exact means the decoded streams are identical
        }

        [Fact]
        public void TheVerification_RejectsADifferentFile_SoNothingWouldBeDeleted()
        {
            // The negative control for the one check the whole feature rests on. Two files that are
            // both valid audio and are NOT the same audio must not pass verification - otherwise the
            // hash comparison is decoration and a transcode could replace an original with anything.
            string dir = MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            string wav = Path.Combine(dir, "system.original.wav");
            string other = Path.Combine(dir, "different.wav");

            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "anoisesrc=d=3:c=white:r=48000:a=0.2",
                "-ac", "2", "-c:a", "pcm_f32le", other,
            }, "test fixture a different take of the same length");

            var result = PreservedAudioTranscode.Verify(wav, other, requireBitExact: true);

            Assert.False(result.Verified);
            Assert.False(result.BitExact);
            Assert.NotNull(result.Error);
            Assert.NotEqual(result.SourceHash, result.OutputHash);
        }

        [Fact]
        public void TheVerification_RejectsATruncatedFile()
        {
            string dir = MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            string wav = Path.Combine(dir, "system.original.wav");
            string shortened = Path.Combine(dir, "short.wv");

            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y", "-t", "1", "-i", wav, "-c:a", "wavpack", shortened,
            }, "test fixture a truncated transcode");

            var result = PreservedAudioTranscode.Verify(wav, shortened, requireBitExact: true);

            Assert.False(result.Verified);
            Assert.Contains("duration differs", result.Error);
        }

        [Fact]
        public void APinnedRecording_IsUntouchedByALivePass()
        {
            string dir = MakeRecording(
                "2026-01-01_120000_video", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), pinned: true);
            var before = Snapshot(dir);

            // Nine months old, so every tier would otherwise reach it.
            var report = Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(before.OrderBy(k => k.Key), Snapshot(dir).OrderBy(k => k.Key));
            Assert.Equal(0, report.BytesReclaimed);
            Assert.Contains(report.Recordings, r => r.Skipped != null && r.Skipped.Contains("pinned"));
        }

        [Fact]
        public void APassPastTheWindow_DeletesThePreservedOriginal_AndNothingElse()
        {
            string dir = MakeRecording("2026-01-01_120000_video", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

            Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

            Assert.False(File.Exists(Path.Combine(dir, "system.original.wav")));
            Assert.False(File.Exists(Path.Combine(dir, "system.original.wv")),
                "a recording past the window should never have been transcoded first");
            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")));
            Assert.True(File.Exists(Path.Combine(dir, "transcript.json")));
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));

            var manifest = Manifest.Load(dir);
            Assert.Empty(manifest.OriginalFiles);
            Assert.Contains(manifest.Housekeeping,
                h => h.Kind == nameof(HousekeepingKind.DeletePreservedOriginal) && h.Outcome == "done");
        }

        [Fact]
        public void AnOversizedLog_KeepsItsTail_AndStaysReadable()
        {
            string dir = MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            string log = Path.Combine(dir, "raw.mp4.ffmpeg.log");
            long before = new FileInfo(log).Length;
            string lastLine = File.ReadLines(log).Last();

            Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

            long after = new FileInfo(log).Length;
            Assert.True(after < before);
            string text = File.ReadAllText(log);
            Assert.Contains("truncated by AgentEyes housekeeping", text);
            Assert.Contains(lastLine, text);   // the tail is what a failure would be in
        }

        [Fact]
        public void APassThatYields_StopsAndSaysItsReportIsPartial()
        {
            // Capture wins. The yield is asked before every action, so a capture that starts during a
            // pass stops it - and the report has to SAY it stopped, or a partial result reads as a
            // complete one.
            MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var before = Snapshot(_root);

            var report = Housekeeper.Run(_root, Live(), "test",
                shouldYield: () => true,
                new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

            Assert.True(report.YieldedToCapture);
            Assert.Equal(0, report.BytesReclaimed);
            Assert.Equal(before.OrderBy(k => k.Key), Snapshot(_root).OrderBy(k => k.Key));
        }

        [Fact]
        public void ADirectoryWithNoManifest_IsNotARecording_AndIsLeftEntirelyAlone()
        {
            string stray = Path.Combine(_root, "not-a-recording");
            Directory.CreateDirectory(stray);
            File.WriteAllText(Path.Combine(stray, "audio_16k.wav"), "this is not ours");
            File.WriteAllText(Path.Combine(stray, "system.original.wav"), "nor is this");
            var before = Snapshot(stray);

            var report = Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(before.OrderBy(k => k.Key), Snapshot(stray).OrderBy(k => k.Key));
            Assert.Equal(0, report.RecordingsExamined);
        }

        [Fact]
        public void ARecordingWithAnUnreadableDate_IsSkippedRatherThanGuessedAt()
        {
            string dir = MakeRecording("2026-01-01_120000_video", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            ManifestStore.Update(dir, m => m.CreatedUtc = "some time last year");
            var before = Snapshot(dir);

            var report = Housekeeper.Run(_root, Live(), "test", () => false,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

            // No age means no tier can be applied. Guessing one would mean deleting a preserved
            // original on the strength of a date this code invented.
            Assert.Equal(before.OrderBy(k => k.Key), Snapshot(dir).OrderBy(k => k.Key));
            Assert.Equal(0, report.RecordingsExamined);
        }

        [Fact]
        public void TwoPassesInARow_TheSecondFindsNothingLeftToDo()
        {
            MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

            var first = Housekeeper.Run(_root, Live(), "test", () => false, now);
            var second = Housekeeper.Run(_root, Live(), "test", () => false, now);

            Assert.True(first.BytesReclaimed > 0);
            Assert.Equal(0, second.BytesReclaimed);
            Assert.DoesNotContain(second.Recordings.SelectMany(r => r.Steps), s => s.Outcome == "done");
        }
    }

    /// <summary>
    /// The ordering law for the AUDIO tier, on the same seam the frame tier uses: the manifest is
    /// repointed BEFORE the source is deleted. The review of this change found the audio tier still
    /// deleting first - the exact order the frame tier's own history shows destroyed a walkthrough.
    /// It lives in this collection because <see cref="ManifestStore.InterruptBeforeReplace"/> is a
    /// process-wide static and the rest of the end-to-end class runs parallel.
    /// </summary>
    [Collection(ManifestSeamCollection.Name)]
    public class HousekeeperTranscodeOrderTests : IDisposable
    {
        private readonly string _root;

        public HousekeeperTranscodeOrderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-transcodeorder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            ManifestStore.InterruptBeforeReplace = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>One recording with a real preserved WAV in the real format, and nothing else.</summary>
        private string MakeRecording(string name, DateTime createdUtc)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);

            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "anoisesrc=d=3:c=pink:r=48000:a=0.5",
                "-ac", "2", "-c:a", "pcm_f32le",
                Path.Combine(dir, "system.original.wav"),
            }, "test fixture preserved wav");

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), new string('v', 4096));

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = name,
                CreatedUtc = createdUtc.ToString("o"),
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                DurationSeconds = 3,
                Files = new List<string> { "recording.mp4", "system.original.wav" },
                OriginalFiles = new List<string> { "system.original.wav" },
            });

            return dir;
        }

        [Fact]
        public void ATranscodeInterruptedAtTheRepoint_LeavesTheSourceOnDiskAndStillNamed()
        {
            string dir = MakeRecording("2026-09-01_120000_video", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));

            ManifestStore.InterruptBeforeReplace = _ => throw new IOException("interrupted on purpose");
            try
            {
                Housekeeper.Run(_root, new HousekeepingSettings { ReportOnly = false }, "test", () => false,
                    new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));
            }
            finally
            {
                ManifestStore.InterruptBeforeReplace = null;
            }

            // The delete comes AFTER the repoint, so an interrupted repoint means nothing was removed:
            // the manifest still names the WAV, and the WAV is still on disk. The other order leaves the
            // manifest naming a deleted file with an orphaned output beside it that no tier can reclaim.
            var manifest = Manifest.Load(dir);
            Assert.Contains("system.original.wav", manifest.OriginalFiles);
            Assert.True(File.Exists(Path.Combine(dir, "system.original.wav")),
                "the source WAV was deleted before the manifest was repointed - the ordering is wrong");
        }
    }
}
