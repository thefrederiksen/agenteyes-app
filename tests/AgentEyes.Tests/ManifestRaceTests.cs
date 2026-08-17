using System;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Ai;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The concrete race issue #155 names: the packaging pass loads a manifest, spends minutes
    /// transcribing and naming, and the user renames the recording in the Library meanwhile. The
    /// packaging save used to write its stale copy over the rename, and the rename was gone.
    ///
    /// These tests drive the REAL packaging write (<see cref="Package.FinalizeManifest"/>) and the
    /// REAL rename path (<see cref="ManifestStore.Update"/>, which is what both rename handlers in
    /// the WPF app now call) - no ffmpeg, no network, because the manifest write is the part that
    /// was broken.
    /// </summary>
    public sealed class ManifestRaceTests : IDisposable
    {
        private readonly string _dir;

        public ManifestRaceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AgentEyes-race-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ManifestStore.Replace(_dir, new Manifest
            {
                Mode = "video",
                Label = "video",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                DurationSeconds = 61.4,
                VideoFile = "recording.mp4",
                TranscribeAttempts = 1,
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static WalkthroughShot Frame(double offset, int n) =>
            new() { OffsetSeconds = offset, RelativePath = $"shots/frame_{n:000}.png" };

        private static TitleGenerator.TitleResult Named() =>
            new("An example title", "One line of description.", new AiUsage(100, 20), "example-model");

        [Fact]
        public void Packaging_DoesNotEraseARenameMadeWhileItWasWorking()
        {
            // 1. Packaging starts and takes its copy of the manifest (Package.RunAsync does this
            //    before transcription, which is the minutes-long part).
            var stalePackagingCopy = Manifest.Load(_dir);
            Assert.Null(stalePackagingCopy.DisplayName);

            // 2. The user renames the recording in the Library while transcription runs.
            ManifestStore.Update(_dir, m => m.DisplayName = "Renamed while packaging ran");

            // 3. Packaging finishes and records what it produced.
            Package.FinalizeManifest(_dir, new[] { Frame(0, 1), Frame(5, 2) }, Named());

            var loaded = Manifest.Load(_dir);
            Assert.Equal("Renamed while packaging ran", loaded.DisplayName);   // the rename survived
            Assert.Equal("transcript.json", loaded.Transcript);                // and packaging landed
            Assert.Equal("walkthrough.html", loaded.Walkthrough);
            Assert.Equal("An example title", loaded.Title);
            Assert.Equal(2, loaded.Shots.Count);
        }

        [Fact]
        public void Packaging_DoesNotEraseAnAttemptCounterWrittenWhileItWasWorking()
        {
            // The same defect, on the field that decides whether unattended work runs again. A lost
            // counter is a recording that retries forever, so this is not a cosmetic case.
            _ = Manifest.Load(_dir);   // packaging's stale copy: TranscribeAttempts = 1

            ManifestStore.Update(_dir, m => m.ThumbAttempts = 3);
            TranscriptionBacklog.NoteAttempt(_dir);

            Package.FinalizeManifest(_dir, Array.Empty<WalkthroughShot>(), null);

            var loaded = Manifest.Load(_dir);
            Assert.Equal(3, loaded.ThumbAttempts);
            Assert.Equal(2, loaded.TranscribeAttempts);
        }

        [Fact]
        public void Packaging_WithoutANamingResult_LeavesTheExistingTitleAlone()
        {
            ManifestStore.Update(_dir, m => { m.Title = "titled by an earlier pass"; m.Description = "earlier description"; });

            Package.FinalizeManifest(_dir, Array.Empty<WalkthroughShot>(), null);

            var loaded = Manifest.Load(_dir);
            Assert.Equal("titled by an earlier pass", loaded.Title);
            Assert.Equal("earlier description", loaded.Description);
        }

        [Fact]
        public void Packaging_KeepsManualMarkerShotsThatOnlyTheCurrentManifestHas()
        {
            // The frames are rebuilt from what this pass extracted; markers recorded during the
            // recording are read from disk, not from a copy the pass has been carrying.
            ManifestStore.Update(_dir, m =>
                m.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = 1.5, File = "shots/00m01s.png" }));

            Package.FinalizeManifest(_dir, new[] { Frame(0, 1) }, null);

            var loaded = Manifest.Load(_dir);
            Assert.Contains(loaded.Shots, s => s.File == "shots/00m01s.png");
            Assert.Contains(loaded.Shots, s => s.File == "shots/frame_001.png");
        }

        [Fact]
        public void AWholeContentWriteOfAStaleCopy_IsWhatUsedToEraseTheRename()
        {
            // Why Replace is not the tool for this job, stated as a test so the distinction cannot
            // rot: writing a stale copy back IS the old bug, and Replace is the only API that still
            // does it (deliberately - a capture session's own record, an import, the #153 recovery
            // record). Any read-modify-write must use Update.
            var stale = Manifest.Load(_dir);
            ManifestStore.Update(_dir, m => m.DisplayName = "Renamed while packaging ran");

            stale.Transcript = "transcript.json";
            ManifestStore.Replace(_dir, stale);

            Assert.Null(Manifest.Load(_dir).DisplayName);
        }
    }
}
