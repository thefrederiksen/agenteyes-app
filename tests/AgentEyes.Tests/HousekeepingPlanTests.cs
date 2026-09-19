using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes;
using AgentEyes.Housekeeping;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The whole housekeeping POLICY, proven without a disk and without ffmpeg (issues #55, #56).
    ///
    /// These are the tests that matter most in this feature. A destructive pass has to be provable
    /// before it is run, and the planner exists precisely so it can be: every condition that spares or
    /// condemns a file is a pure function of a manifest, a file list, an age and the settings.
    /// </summary>
    public class HousekeepingPlanTests
    {
        private static HousekeepingSettings Settings(Action<HousekeepingSettings>? tweak = null)
        {
            var s = new HousekeepingSettings();
            tweak?.Invoke(s);
            return s;
        }

        /// <summary>A packaged recording: it has a transcript, and the transcript is on disk.</summary>
        private static Manifest Packaged(Action<Manifest>? tweak = null)
        {
            var m = new Manifest
            {
                Mode = "video",
                CreatedUtc = "2026-09-01T10:00:00Z",
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                Files = new List<string> { "recording.mp4", "recording.original.mp4", "system.original.wav" },
                OriginalFiles = new List<string> { "recording.original.mp4", "system.original.wav" },
            };
            tweak?.Invoke(m);
            return m;
        }

        private static Dictionary<string, long> Disk(params (string Name, long Size)[] files) =>
            files.ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, long> TypicalDisk() => Disk(
            ("transcript.json", 100_000),
            ("manifest.json", 60_000),
            ("recording.mp4", 134_000_000),
            ("recording.original.mp4", 120_000_000),
            ("system.original.wav", 1_355_000_000),
            ("audio_16k.wav", 113_000_000),
            ("raw.mp4.ffmpeg.log", 922_000));

        // ---- Tier 0 --------------------------------------------------------------------------

        [Fact]
        public void For_PackagedRecording_DeletesTheTranscriberInput()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 1, Settings(), out string? skip);

            Assert.Null(skip);
            var step = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.DeleteDerived));
            Assert.Equal("audio_16k.wav", step.File);
            Assert.Equal(113_000_000, step.Bytes);
        }

        [Fact]
        public void For_TranscriptNotOnDisk_KeepsTheTranscriberInput()
        {
            // The manifest NAMES a transcript that is not there: transcription has not finished, so its
            // input is still needed. This is the case that makes the gate the artifact and not the
            // journal - PostProcessing could say "done" while the file is missing.
            var disk = TypicalDisk();
            disk.Remove("transcript.json");

            var steps = HousekeepingPlan.For(Packaged(), disk, 1, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeleteDerived);
        }

        [Fact]
        public void For_MuxStillPending_TouchesNothingButTheLogs()
        {
            // Issue #77: a deferred mux means the raw capture files are the durable artifact.
            var manifest = Packaged(m => m.PendingMux = new Manifest.PendingMuxInfo
            {
                Mode = "video", Source = "mixed", FinalFile = "recording.mp4",
            });

            var steps = HousekeepingPlan.For(manifest, TypicalDisk(), 1, Settings(), out _);

            Assert.All(steps, s => Assert.Equal(HousekeepingKind.TruncateLog, s.Kind));
        }

        [Fact]
        public void For_AFileTheManifestClaims_IsNeverTreatedAsDerived()
        {
            // If a future manifest starts naming audio_16k.wav as a kept file, it is no longer derived.
            var manifest = Packaged(m => m.Files.Add("audio_16k.wav"));

            var steps = HousekeepingPlan.For(manifest, TypicalDisk(), 1, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeleteDerived);
        }

        [Fact]
        public void For_ALargeFfmpegLog_IsTruncatedToItsTail()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 1, Settings(), out _);

            var step = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.TruncateLog));
            Assert.Equal("raw.mp4.ffmpeg.log", step.File);
            Assert.Equal(40 * 1024, step.KeepBytes);
        }

        [Fact]
        public void For_ASmallFfmpegLog_IsLeftAlone()
        {
            var disk = TypicalDisk();
            disk["raw.mp4.ffmpeg.log"] = 1024;

            var steps = HousekeepingPlan.For(Packaged(), disk, 1, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.TruncateLog);
        }

        /// <summary>
        /// A log that is only a little over the tail is left alone, and that is what makes the pass
        /// settle. Truncating writes a short banner, so a log truncated to exactly the tail is still
        /// bigger than the tail - and a condition of "bigger than the tail" re-truncated the same file
        /// on every one of an always-on app's 15-minute ticks. Caught by the two-passes-in-a-row test.
        /// </summary>
        [Theory]
        [InlineData(40 * 1024 + 80, false)]         // just truncated: the banner alone puts it over
        [InlineData(40 * 1024 * 2 - 1, false)]      // under double: not worth a rewrite
        [InlineData(40 * 1024 * 2, true)]           // double: one truncation reclaims a full tail
        public void For_ALogNearTheTailSize_IsOnlyTruncatedWhenItIsWorthIt(long size, bool expected)
        {
            var disk = TypicalDisk();
            disk["raw.mp4.ffmpeg.log"] = size;

            var steps = HousekeepingPlan.For(Packaged(), disk, 1, Settings(), out _);

            Assert.Equal(expected, steps.Any(s => s.Kind == HousekeepingKind.TruncateLog));
        }

        // ---- Tier 1 --------------------------------------------------------------------------

        [Fact]
        public void For_PreservedWav_IsTranscodedNotDeleted()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 1, Settings(), out _);

            var step = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.TranscodePreservedAudio));
            Assert.Equal("system.original.wav", step.File);
            Assert.Equal("system.original.wv", step.Output);
            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeletePreservedOriginal);
        }

        [Fact]
        public void For_PreservedVideo_IsNotTranscodedByThisSlice()
        {
            // recording.original.mp4 is already compressed video; slice 1 leaves it entirely alone
            // until Tier 3 ages it out.
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 1, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.File == "recording.original.mp4");
        }

        [Fact]
        public void For_SmallerCodecChosen_TargetsFlacInstead()
        {
            var steps = HousekeepingPlan.For(
                Packaged(), TypicalDisk(), 1, Settings(s => s.PreservedAudioMustBeBitExact = false), out _);

            var step = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.TranscodePreservedAudio));
            Assert.Equal("system.original.flac", step.Output);
            Assert.Contains("NOT bit-exact", step.Reason);
        }

        // ---- Tier 3, and the boundary on both sides ------------------------------------------

        [Fact]
        public void For_OneDayInsideTheWindow_KeepsThePreservedOriginals()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 29, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.DeletePreservedOriginal);
        }

        [Fact]
        public void For_OnTheWindowBoundary_DeletesThePreservedOriginals()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 30, Settings(), out _);

            var deletes = steps.Where(s => s.Kind == HousekeepingKind.DeletePreservedOriginal)
                               .Select(s => s.File).ToList();
            Assert.Equal(2, deletes.Count);
            Assert.Contains("recording.original.mp4", deletes);
            Assert.Contains("system.original.wav", deletes);
        }

        [Fact]
        public void For_PastTheWindow_DoesNotAlsoTranscodeWhatItIsAboutToDelete()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 40, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.TranscodePreservedAudio);
        }

        [Fact]
        public void For_PastTheWindow_TouchesNothingOutsideOriginalFiles()
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), 40, Settings(), out _);

            var deleted = steps.Where(s => s.Kind == HousekeepingKind.DeletePreservedOriginal)
                               .Select(s => s.File);
            Assert.All(deleted, f => Assert.Contains(f, Packaged().OriginalFiles));
            Assert.DoesNotContain(steps, s => s.File == "recording.mp4");
            Assert.DoesNotContain(steps, s => s.File == "transcript.json");
            Assert.DoesNotContain(steps, s => s.File == "manifest.json");
        }

        // ---- The exemptions ------------------------------------------------------------------

        [Fact]
        public void For_PinnedRecording_IsExemptFromEveryTier()
        {
            var manifest = Packaged(m => m.Keep = true);

            var steps = HousekeepingPlan.For(manifest, TypicalDisk(), 900, Settings(), out string? skip);

            Assert.Empty(steps);
            Assert.Contains("pinned", skip!);
        }

        [Fact]
        public void For_HousekeepingOff_PlansNothing()
        {
            var steps = HousekeepingPlan.For(
                Packaged(), TypicalDisk(), 900, Settings(s => s.Enabled = false), out string? skip);

            Assert.Empty(steps);
            Assert.Contains("turned off", skip!);
        }

        // ---- The cases where the record and the disk disagree --------------------------------

        [Fact]
        public void For_OriginalFileNotOnDisk_PlansNothingForIt()
        {
            // The manifest names a preserved original that is gone - deleted by hand, or by an earlier
            // pass whose record was lost. There is nothing to do, and NO step may be emitted for a
            // file this pass cannot see.
            var disk = TypicalDisk();
            disk.Remove("system.original.wav");
            disk.Remove("recording.original.mp4");

            var steps = HousekeepingPlan.For(Packaged(), disk, 40, Settings(), out _);

            Assert.DoesNotContain(steps, s => s.File.Contains(".original."));
        }

        [Fact]
        public void For_EmptyDirectory_PlansNothing()
        {
            var steps = HousekeepingPlan.For(Packaged(), Disk(), 40, Settings(), out string? skip);

            Assert.Empty(steps);
            Assert.Null(skip);
        }

        [Fact]
        public void For_ARecordingWithNoPreservedOriginals_StillDoesTierZero()
        {
            var manifest = Packaged(m =>
            {
                m.OriginalFiles.Clear();
                m.Files = new List<string> { "recording.mp4" };
            });

            var steps = HousekeepingPlan.For(manifest, TypicalDisk(), 1, Settings(), out _);

            Assert.Contains(steps, s => s.Kind == HousekeepingKind.DeleteDerived);
            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.TranscodePreservedAudio);
        }

        /// <summary>
        /// The property that matters more than any single case: whatever the age, the settings or the
        /// state, the planner NEVER proposes touching a file the recording actually keeps.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(29)]
        [InlineData(30)]
        [InlineData(400)]
        public void For_AnyAge_NeverTouchesTheDurableArtifacts(int ageDays)
        {
            var steps = HousekeepingPlan.For(Packaged(), TypicalDisk(), ageDays, Settings(), out _);

            foreach (string durable in new[] { "recording.mp4", "transcript.json", "manifest.json" })
            {
                Assert.DoesNotContain(steps, s => string.Equals(s.File, durable, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
