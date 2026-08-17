using System;
using System.IO;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #132: which recordings the automatic backfill picks up, and the attempt cap that stops
    /// a permanently-broken file burning credits on every launch.
    /// </summary>
    public class TranscriptionBacklogTests : IDisposable
    {
        private readonly string _root;

        public TranscriptionBacklogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-backlog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }

        private string MakeDir(string name, bool media = true, bool transcript = false,
            bool manifest = true, int attempts = 0, string mediaName = "recording.mp4")
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            if (media) File.WriteAllText(Path.Combine(dir, mediaName), "x");
            if (transcript) File.WriteAllText(Path.Combine(dir, "transcript.json"), "{}");
            if (manifest) ManifestStore.Replace(dir, new Manifest { Mode = "video", TranscribeAttempts = attempts });
            return dir;
        }

        [Fact]
        public void NeedsTranscription_MediaWithoutTranscript_True()
        {
            Assert.True(TranscriptionBacklog.NeedsTranscription(MakeDir("2026-08-10_100000_video")));
        }

        [Fact]
        public void NeedsTranscription_AudioOnlyRecording_True()
        {
            string dir = MakeDir("2026-08-10_100001_audio", mediaName: "audio.wav");
            Assert.True(TranscriptionBacklog.NeedsTranscription(dir));
        }

        [Fact]
        public void NeedsTranscription_TranscriptPresent_False()
        {
            Assert.False(TranscriptionBacklog.NeedsTranscription(
                MakeDir("2026-08-10_100002_video", transcript: true)));
        }

        [Fact]
        public void NeedsTranscription_ScreenshotFolderWithNoMedia_False()
        {
            // The library holds "_shot" folders with only a PNG. Picking one up would try to
            // transcribe a screenshot.
            Assert.False(TranscriptionBacklog.NeedsTranscription(
                MakeDir("2026-08-10_100003_shot", media: false)));
        }

        [Fact]
        public void NeedsTranscription_AttemptsExhausted_False()
        {
            Assert.False(TranscriptionBacklog.NeedsTranscription(
                MakeDir("2026-08-10_100004_video", attempts: TranscriptionBacklog.MaxTranscribeAttempts)));
        }

        [Fact]
        public void NeedsTranscription_AttemptsBelowCap_True()
        {
            Assert.True(TranscriptionBacklog.NeedsTranscription(
                MakeDir("2026-08-10_100005_video", attempts: TranscriptionBacklog.MaxTranscribeAttempts - 1)));
        }

        [Fact]
        public void NeedsTranscription_NoManifest_TreatedAsNeverAttempted()
        {
            Assert.True(TranscriptionBacklog.NeedsTranscription(
                MakeDir("2026-08-10_100006_video", manifest: false)));
        }

        [Fact]
        public void NeedsTranscription_MissingDirectory_False()
        {
            Assert.False(TranscriptionBacklog.NeedsTranscription(Path.Combine(_root, "nope")));
        }

        [Fact]
        public void FindPending_ReturnsOnlyPending_OldestFirst()
        {
            MakeDir("2026-08-10_100200_video");                      // pending
            MakeDir("2026-08-10_100100_video");                      // pending, older
            MakeDir("2026-08-10_100300_video", transcript: true);    // done
            MakeDir("2026-08-10_100400_shot", media: false);         // screenshot

            var pending = TranscriptionBacklog.FindPending(_root);

            Assert.Equal(2, pending.Count);
            Assert.Equal("2026-08-10_100100_video", Path.GetFileName(pending[0]));
            Assert.Equal("2026-08-10_100200_video", Path.GetFileName(pending[1]));
        }

        [Fact]
        public void FindPending_MissingRoot_EmptyNotThrow()
        {
            Assert.Empty(TranscriptionBacklog.FindPending(Path.Combine(_root, "does-not-exist")));
        }

        [Fact]
        public void NoteAttempt_IncrementsAndPersists()
        {
            string dir = MakeDir("2026-08-10_100500_video");

            TranscriptionBacklog.NoteAttempt(dir);
            Assert.Equal(1, TranscriptionBacklog.AttemptsSoFar(dir));

            TranscriptionBacklog.NoteAttempt(dir);
            Assert.Equal(2, TranscriptionBacklog.AttemptsSoFar(dir));
        }

        [Fact]
        public void NoteAttempt_ThreeTimes_DropsOutOfThePass()
        {
            string dir = MakeDir("2026-08-10_100600_video");
            for (int i = 0; i < TranscriptionBacklog.MaxTranscribeAttempts; i++) TranscriptionBacklog.NoteAttempt(dir);

            Assert.False(TranscriptionBacklog.NeedsTranscription(dir));
            Assert.DoesNotContain(dir, TranscriptionBacklog.FindPending(_root));
        }

        [Fact]
        public void NoteAttempt_NoManifest_DoesNotThrow()
        {
            string dir = MakeDir("2026-08-10_100700_video", manifest: false);
            TranscriptionBacklog.NoteAttempt(dir);   // must not throw
            Assert.Equal(0, TranscriptionBacklog.AttemptsSoFar(dir));
        }

        // ---- title backfill (issue #138) ----------------------------------

        private string MakeTitled(string name, string title, int titleAttempts = 0,
            DateTime? lastTitleAttemptUtc = null)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "x");
            File.WriteAllText(Path.Combine(dir, "transcript.json"), "[]");
            File.WriteAllText(Path.Combine(dir, "transcript.txt"),
                "[00:00] This is a real recording with enough spoken content to name properly.");
            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Title = title,
                TitleAttempts = titleAttempts,
                LastTitleAttemptUtc = lastTitleAttemptUtc,
            });
            return dir;
        }

        [Fact]
        public void NeedsTitle_TranscriptButEmptyTitle_True()
        {
            Assert.True(TranscriptionBacklog.NeedsTitle(MakeTitled("2026-08-10_110000_video", "")));
        }

        [Fact]
        public void NeedsTitle_WhitespaceTitle_True()
        {
            Assert.True(TranscriptionBacklog.NeedsTitle(MakeTitled("2026-08-10_110001_video", "   ")));
        }

        [Fact]
        public void NeedsTitle_TitlePresent_False()
        {
            Assert.False(TranscriptionBacklog.NeedsTitle(MakeTitled("2026-08-10_110002_video", "A real title")));
        }

        [Fact]
        public void NeedsTitle_NoTranscript_False()
        {
            // Nothing to title from yet - the transcription pass owns this one.
            string dir = MakeDir("2026-08-10_110003_video");
            Assert.False(TranscriptionBacklog.NeedsTitle(dir));
        }

        [Fact]
        public void NeedsTitle_AttemptsExhausted_False()
        {
            var now = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-10_110004_video", "",
                TranscriptionBacklog.MaxTitleAttempts, now.AddMinutes(-15));

            Assert.False(TranscriptionBacklog.NeedsTitle(dir, now));
        }

        [Fact]
        public void NoteTitleAttempt_IsSeparateFromTranscribeAttempts()
        {
            // A recording can transcribe first time and still fail to title; the two budgets
            // must not consume each other.
            string dir = MakeTitled("2026-08-10_110005_video", "");
            TranscriptionBacklog.NoteTitleAttempt(dir);

            var m = Manifest.Load(dir);
            Assert.Equal(1, m.TitleAttempts);
            Assert.Equal(0, m.TranscribeAttempts);
        }

        [Fact]
        public void FindMissingTitles_ReturnsOnlyUntitled_OldestFirst()
        {
            MakeTitled("2026-08-10_110200_video", "");
            MakeTitled("2026-08-10_110100_video", "");
            MakeTitled("2026-08-10_110300_video", "Already named");
            MakeDir("2026-08-10_110400_video");   // no transcript yet

            var pending = TranscriptionBacklog.FindMissingTitles(_root);

            Assert.Equal(2, pending.Count);
            Assert.Equal("2026-08-10_110100_video", Path.GetFileName(pending[0]));
            Assert.Equal("2026-08-10_110200_video", Path.GetFileName(pending[1]));
        }


        [Fact]
        public void NeedsTitle_TranscriptTooShortToName_False()
        {
            // A few seconds of silence still produces a transcript ("...", a stray hallucinated
            // word). Naming that spends a credit to get nonsense back - the first run of this pass
            // titled 11 characters of dots as "Missing Transcript".
            string dir = MakeTitled("2026-08-10_120000_video", "");
            File.WriteAllText(Path.Combine(dir, "transcript.txt"), "[00:00] ...");

            Assert.False(TranscriptionBacklog.NeedsTitle(dir));
        }

        [Fact]
        public void HasTitleableContent_IgnoresTimestampsWhenMeasuring()
        {
            // Timestamps are formatting, not speech - they must not push a silent clip over the bar.
            string dir = MakeTitled("2026-08-10_120001_video", "");
            File.WriteAllText(Path.Combine(dir, "transcript.txt"),
                "[00:00] [00:05] [00:10] [00:15] [00:20] [00:25] hi");

            Assert.False(TranscriptionBacklog.HasTitleableContent(dir));
        }

        [Fact]
        public void HasTitleableContent_RealSpeech_True()
        {
            string dir = MakeTitled("2026-08-10_120002_video", "");
            Assert.True(TranscriptionBacklog.HasTitleableContent(dir));
        }

        [Fact]
        public void NeedsTitle_TitleCeilingUsedInsideTheWindow_NotRetriedByThePeriodicPass()
        {
            // Issue #142: the title pass runs every 15 minutes for as long as the app is up, so the
            // attempt ceiling is what stops a permanently-failing recording asking for a name four
            // times an hour forever. Issue #148 only raised the ceiling and made it a per-window
            // budget - inside the window it still bites.
            var now = new DateTime(2026, 8, 11, 13, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-11_130000_video", "",
                TranscriptionBacklog.MaxTitleAttempts, now.AddMinutes(-15));

            Assert.False(TranscriptionBacklog.NeedsTitle(dir, now));
            Assert.DoesNotContain(dir, TranscriptionBacklog.FindMissingTitles(_root, now));
        }

        [Fact]
        public void NeedsTitle_NoTranscriptTxt_False()
        {
            string dir = MakeTitled("2026-08-10_120003_video", "");
            File.Delete(Path.Combine(dir, "transcript.txt"));
            Assert.False(TranscriptionBacklog.NeedsTitle(dir));
        }

        // ---- titling gets its own ceiling and a cooling-off window (issue #148) ----

        [Fact]
        public void TitleCeiling_IsHigherThanTheTranscriptionCeiling()
        {
            // The two operations have wildly different costs: transcription uploads 70-85 MB of
            // 16 kHz WAV, titling sends a few thousand tokens of text. One shared ceiling let the
            // expensive budget starve the cheap one.
            Assert.True(TranscriptionBacklog.MaxTitleAttempts > TranscriptionBacklog.MaxTranscribeAttempts,
                "titling is orders of magnitude cheaper than transcription, so its ceiling must be higher");
        }

        [Fact]
        public void NeedsTranscription_TranscribeAttemptsAtItsCeiling_StillNotPickedUp()
        {
            // No regression on the issue #132 guarantee: raising the TITLE ceiling must not raise
            // the transcription one.
            string dir = MakeDir("2026-08-11_140000_video",
                attempts: TranscriptionBacklog.MaxTranscribeAttempts);

            Assert.False(TranscriptionBacklog.NeedsTranscription(dir));
            Assert.DoesNotContain(dir, TranscriptionBacklog.FindPending(_root));
        }

        [Fact]
        public void NeedsTitle_TitleAttemptsAtTheOldSharedCeilingOfThree_EligibleAgain()
        {
            // The exact state of 2026-08-11_111133_video and 2026-08-11_130814_video: a complete
            // transcript, no Title, TitleAttempts 3 - stranded forever under the preset name
            // "Monitor 1" while the old shared ceiling was 3.
            var now = new DateTime(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-11_111133_video", "", titleAttempts: 3,
                lastTitleAttemptUtc: now.AddMinutes(-5));

            Assert.True(TranscriptionBacklog.NeedsTitle(dir, now));
            Assert.Contains(dir, TranscriptionBacklog.FindMissingTitles(_root, now));
        }

        [Fact]
        public void NeedsTitle_StrandedManifestWithNoAttemptStamp_EligibleAgain()
        {
            // Those two manifests were written before this issue, so they carry no
            // LastTitleAttemptUtc at all. They must still come back into the pass.
            string dir = MakeTitled("2026-08-11_130814_video", "", titleAttempts: 3);

            Assert.True(TranscriptionBacklog.NeedsTitle(dir));
        }

        [Fact]
        public void NeedsTitle_CeilingReachedAndTheWindowCooledOff_EligibleAgain()
        {
            var now = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-11_150000_video", "",
                TranscriptionBacklog.MaxTitleAttempts,
                now - TranscriptionBacklog.TitleAttemptCooldown);

            Assert.True(TranscriptionBacklog.NeedsTitle(dir, now));
            Assert.Contains(dir, TranscriptionBacklog.FindMissingTitles(_root, now));
        }

        [Fact]
        public void IsTitleEligible_BelowTheCeiling_TrueWhateverTheClockSays()
        {
            var now = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(TranscriptionBacklog.IsTitleEligible(
                TranscriptionBacklog.MaxTitleAttempts - 1, now.AddSeconds(-1), now));
        }

        [Fact]
        public void IsTitleEligible_AtTheCeilingInsideTheWindow_False()
        {
            var now = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            Assert.False(TranscriptionBacklog.IsTitleEligible(
                TranscriptionBacklog.MaxTitleAttempts,
                now - TranscriptionBacklog.TitleAttemptCooldown + TimeSpan.FromMinutes(1),
                now));
        }

        [Fact]
        public void IsTitleEligible_AtTheCeilingAfterTheCooldown_True()
        {
            var now = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(TranscriptionBacklog.IsTitleEligible(
                TranscriptionBacklog.MaxTitleAttempts,
                now - TranscriptionBacklog.TitleAttemptCooldown,
                now));
        }

        [Fact]
        public void NoteTitleAttempt_InsideTheWindow_KeepsCountingAndStampsTheClock()
        {
            var start = new DateTime(2026, 8, 11, 10, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-11_160000_video", "");

            TranscriptionBacklog.NoteTitleAttempt(dir, start);
            TranscriptionBacklog.NoteTitleAttempt(dir, start.AddMinutes(15));

            var m = Manifest.Load(dir);
            Assert.Equal(2, m.TitleAttempts);
            Assert.Equal(start.AddMinutes(15), m.LastTitleAttemptUtc);
        }

        [Fact]
        public void NoteTitleAttempt_AfterTheCooldown_StartsAFreshWindowAtOne()
        {
            var start = new DateTime(2026, 8, 11, 10, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-11_160001_video", "",
                TranscriptionBacklog.MaxTitleAttempts, start);

            TranscriptionBacklog.NoteTitleAttempt(dir, start + TranscriptionBacklog.TitleAttemptCooldown);

            Assert.Equal(1, Manifest.Load(dir).TitleAttempts);
        }

        [Fact]
        public void NoteTitleAttempt_RepeatedFailures_CannotExceedTheCeilingInOneWindow()
        {
            // The bound that keeps a permanently un-titleable recording cheap: the periodic pass
            // firing every 15 minutes for a whole day still spends at most MaxTitleAttempts calls.
            var start = new DateTime(2026, 8, 11, 10, 0, 0, DateTimeKind.Utc);
            string dir = MakeTitled("2026-08-11_170000_video", "");

            int calls = 0;
            for (int tick = 0; tick < 96; tick++)   // 96 * 15 min = the full 24h window
            {
                DateTime now = start.AddMinutes(15 * tick);
                if (!TranscriptionBacklog.NeedsTitle(dir, now)) continue;
                TranscriptionBacklog.NoteTitleAttempt(dir, now);   // the naming call fails every time
                calls++;
            }

            Assert.Equal(TranscriptionBacklog.MaxTitleAttempts, calls);
            Assert.Equal(TranscriptionBacklog.MaxTitleAttempts, Manifest.Load(dir).TitleAttempts);
        }

        [Fact]
        public void NoteTitleAttempt_NoManifest_DoesNotThrow()
        {
            string dir = MakeDir("2026-08-11_170001_video", manifest: false);
            TranscriptionBacklog.NoteTitleAttempt(dir);   // must not throw
        }
    }
}
