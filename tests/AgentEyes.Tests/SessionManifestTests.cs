using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #155, the FIRST manifest write of a recording.
    ///
    /// The atomic write protects an UPDATE: the original file survives a kill between the flushed
    /// temp and the rename. That says nothing about the write that CREATES the file, and a capture
    /// session used to have exactly one - it kept its manifest in memory for the whole recording and
    /// wrote it once, at stop. A process death in that window left raw media plus a
    /// manifest.json.&lt;id&gt;.tmp and NO manifest.json: the Library excludes such a directory,
    /// PostRecordingPlan sees no manifest and concludes there is no work, and the recording is
    /// stranded as raw bytes nothing will ever look at again - the exact outcome issues #151, #152
    /// and #153 were written to eliminate.
    ///
    /// The fix is that <see cref="RecordingService"/> writes a valid manifest BEFORE any capture
    /// starts, so the stop is an update of an existing record. These tests prove both halves: what
    /// the interrupted first write used to leave behind (<see cref="TheOldShape_NoRecordUntilStop_StrandsTheRecording"/>,
    /// the counterfactual - without it the regression below could pass for the wrong reason), and
    /// that with the start record on disk the next process finds a live, parseable, recoverable
    /// recording.
    /// </summary>
    /// <summary>
    /// <see cref="ManifestStore.InterruptBeforeReplace"/> is ONE static seam for the whole process,
    /// so every test class that installs it belongs to this collection. Without it xUnit runs those
    /// classes in parallel and one class's teardown clears another's interrupt mid-test - a flake
    /// that looks like the interruption simply not happening.
    /// </summary>
    [CollectionDefinition(ManifestSeamCollection.Name, DisableParallelization = true)]
    public sealed class ManifestSeamCollection
    {
        public const string Name = "manifest write seam";
    }

    [Collection(ManifestSeamCollection.Name)]
    public sealed class SessionManifestTests : IDisposable
    {
        private readonly string _root;

        public SessionManifestTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AgentEyes-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            ManifestStore.InterruptBeforeReplace = null;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        // ---- fixtures: one mixed-source video session, the shape that defers its mux ----

        /// <summary>The recording directory a capture session creates, with the raw bytes ffmpeg and
        /// the loopback writer have already put in it.</summary>
        private string NewSessionDir(string leaf = "2026-08-11_120000_video")
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            File.WriteAllText(Path.Combine(dir, "raw.mp4"), "raw video bytes");
            File.WriteAllText(Path.Combine(dir, "sys_native.wav"), "system audio bytes");
            return dir;
        }

        /// <summary>What <see cref="RecordingService.BeginSession"/> writes before the first byte is
        /// captured: identity, the media the session will produce, and the deferred-mux plan.</summary>
        private static Manifest StartRecord() => new()
        {
            Mode = "video",
            Label = "video",
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            MonitorIndex = 1,
            MonitorName = "Monitor 1",
            Microphone = "Test mic + (system)",
            VideoFile = "recording.mp4",
            PendingMux = new Manifest.PendingMuxInfo
            {
                Mode = "video",
                Source = "mixed",
                RawVideo = "raw.mp4",
                SysWav = "sys_native.wav",
                FinalFile = "recording.mp4",
                RawDurationSeconds = 0,
                Options = new AudioMixOptions(),
            },
        };

        /// <summary>Kill the process between the complete temp and the rename - the window the
        /// atomic write is defined by.</summary>
        private void InterruptTheNextWrite() =>
            ManifestStore.InterruptBeforeReplace = temp =>
            {
                if (temp.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("simulated kill between the temp write and the replace");
            };

        // ---- the counterfactual: no record until stop ----

        [Fact]
        public void TheOldShape_NoRecordUntilStop_StrandsTheRecording()
        {
            // The session kept its manifest in memory and wrote it once, at stop. Interrupt THAT
            // write and this is what the next process finds. Asserted so the regression below cannot
            // pass by testing a case that was never broken.
            string dir = NewSessionDir();
            InterruptTheNextWrite();

            Assert.Throws<IOException>(() => ManifestStore.Replace(dir, StartRecord()));
            ManifestStore.InterruptBeforeReplace = null;

            Assert.False(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.Single(Directory.GetFiles(dir, "manifest.json.*.tmp"));   // a temp nothing adopts
            Assert.Empty(RecordingLibrary.List(50, 0, _root).Items);          // invisible in the Library
            Assert.False(PostRecordingPlan.NeedsMux(dir));                    // no mux work to be seen
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));           // nothing will pick it up
            // ... while the capture bytes it was supposed to describe are sitting right there.
            Assert.True(RecordingStopSequence.HasRawArtifacts(dir));
        }

        // ---- the regression: the start record makes the interrupted stop recoverable ----

        [Fact]
        public void InterruptedStopWrite_LeavesALiveParseableRecoverableRecording()
        {
            string dir = NewSessionDir();

            // 1. The session publishes its record before capture starts (BeginSession).
            ManifestStore.Replace(dir, StartRecord());

            // 2. The stop applies what it owns - and the process dies between the temp and the
            //    rename, so this write never lands.
            InterruptTheNextWrite();
            Assert.Throws<IOException>(() => ManifestStore.Update(dir, m =>
            {
                m.DurationSeconds = 20.7;
                m.PendingMux!.RawDurationSeconds = 20.7;
            }));
            ManifestStore.InterruptBeforeReplace = null;

            // 3. What the NEXT process finds.
            string manifestPath = Path.Combine(dir, "manifest.json");
            Assert.True(File.Exists(manifestPath));                 // live
            var loaded = Manifest.Load(dir);                        // parseable
            Assert.Equal("video", loaded.Mode);
            Assert.Equal("recording.mp4", loaded.VideoFile);

            // Listed by the Library rather than excluded from it.
            var listed = RecordingLibrary.List(50, 0, _root).Items;
            Assert.Equal(new[] { Path.GetFileName(dir) }, listed.Select(i => i.Id).ToArray());

            // Recoverable: the deferred mux is still described, so the raw files can still be turned
            // into recording.mp4. Without the start record this is the knowledge that died with the
            // process, and raw.mp4 + sys_native.wav became bytes nothing knew what to do with.
            Assert.True(PostRecordingPlan.NeedsMux(dir));
            Assert.Contains(PostStage.Mux, PostRecordingPlan.Outstanding(dir));
            Assert.Equal("raw.mp4", loaded.PendingMux!.RawVideo);
            Assert.Equal("sys_native.wav", loaded.PendingMux.SysWav);
            Assert.Equal("recording.mp4", loaded.PendingMux.FinalFile);

            // The abandoned temp is left behind (nothing is running to clean it up) and must not be
            // mistaken for the manifest by anything.
            Assert.Single(Directory.GetFiles(dir, "manifest.json.*.tmp"));
        }

        [Fact]
        public void InterruptedFirstWrite_BeforeAnyCapture_StrandsNothing()
        {
            // The first write now happens before a single byte is captured, so an interruption THERE
            // loses an empty directory - there is no media to strand.
            string dir = Path.Combine(_root, "2026-08-11_120001_video");
            Directory.CreateDirectory(dir);
            InterruptTheNextWrite();

            Assert.Throws<IOException>(() => ManifestStore.Replace(dir, StartRecord()));
            ManifestStore.InterruptBeforeReplace = null;

            Assert.False(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.mp4"));
            Assert.Empty(Directory.GetFiles(dir, "*.wav"));
            Assert.Empty(RecordingLibrary.List(50, 0, _root).Items);
        }

        [Fact]
        public void TheStopUpdate_KeepsARenameMadeWhileTheRecordingWasRunning()
        {
            // A consequence of the start record: the recording is in the Library while it runs, so it
            // can be renamed while it runs. The stop is a read-modify-write, so the rename survives -
            // where a whole-content write of the session's in-memory copy would have erased it.
            string dir = NewSessionDir();
            ManifestStore.Replace(dir, StartRecord());

            ManifestStore.Update(dir, m => m.DisplayName = "Renamed mid-recording");
            ManifestStore.Update(dir, m =>
            {
                m.DurationSeconds = 20.7;
                m.PendingMux!.RawDurationSeconds = 20.7;
            });

            var loaded = Manifest.Load(dir);
            Assert.Equal("Renamed mid-recording", loaded.DisplayName);
            Assert.Equal(20.7, loaded.DurationSeconds);
        }

        // ---- the engine wiring, which a unit test cannot drive (no monitors, no ffmpeg) ----

        /// <summary>
        /// The publish-before-any-writer order is no longer a property of the TEXT of StartAudio /
        /// StartVideo. <see cref="RecordingStartSequence.Run"/> publishes and only then runs the
        /// steps, and <c>RecordingStartSequenceTests</c> proves that behaviourally, with an injected
        /// failure at every position. What these two tests assert is the WIRING that puts the order
        /// there: every writer start is a step handed to the sequence, so it cannot run before the
        /// publish - and cannot run outside the rollback that stops the writers already started
        /// (issue #155).
        /// </summary>
        [Fact]
        public void StartAudio_StartsEveryWriterInsideTheSequence()
        {
            string body = RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"),
                "public void StartAudio(");

            Assert.Contains("StartSession(steps)", body, StringComparison.Ordinal);
            foreach (string writerStart in new[] { "_audio.Start(", "_loop.Start(" })
            {
                Assert.True(EveryOccurrenceIsAStartStep(body, writerStart),
                    $"StartAudio calls {writerStart} outside a RecordingStartStep: it would run before the "
                    + "manifest reaches disk, and outside the rollback that stops writers already started");
            }
        }

        [Fact]
        public void StartVideo_StartsEveryWriterInsideTheSequence()
        {
            string body = RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"),
                "public void StartVideo(");

            Assert.Contains("StartSession(steps)", body, StringComparison.Ordinal);
            foreach (string writerStart in new[] { "FfmpegRecorder.Start(", "_loop.Start(", "_audio.StartMonitor(" })
            {
                Assert.True(EveryOccurrenceIsAStartStep(body, writerStart),
                    $"StartVideo calls {writerStart} outside a RecordingStartStep: it would run before the "
                    + "manifest reaches disk, and outside the rollback that stops writers already started");
            }
        }

        [Fact]
        public void StartSession_PublishesThroughBeginSessionAndRollsBackThroughReleaseSession()
        {
            string code = RepoSource.Read("src/AgentEyes.Core/RecordingService.cs");
            string body = RepoSource.MethodBody(code, "private void StartSession(");

            // The one line that binds the three halves together: BeginSession is the publish (so the
            // record and the claim are INSIDE the failure boundary), LiveWriters is what gets stopped
            // and disposed, and ReleaseSession runs only after they are down.
            Assert.Contains("RecordingStartSequence.Run(_dir!, BeginSession, steps, LiveWriters, ReleaseSession)",
                            body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The rollback can only stop the writers <c>LiveWriters</c> knows about, so a writer field it
        /// does not name is a writer a failed start leaves CAPTURING while the service reports idle -
        /// the exact defect of issue #155. This finds the writer fields by reflecting the compiled
        /// service rather than by listing them, so ADDING a fourth one fails here until the rollback
        /// is taught about it.
        ///
        /// "A capture writer" is defined by what the rollback has to do to it: a field whose type has
        /// both a no-argument <c>Stop()</c> and a no-argument <c>Dispose()</c>. That deliberately
        /// excludes the stopwatch (no Dispose) and every plain state field.
        /// </summary>
        [Fact]
        public void LiveWriters_NamesEveryCaptureWriterFieldOnTheService()
        {
            var writerFields = typeof(RecordingService)
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Where(f => f.FieldType.GetMethod("Stop", Type.EmptyTypes) != null
                         && f.FieldType.GetMethod("Dispose", Type.EmptyTypes) != null)
                .Select(f => f.Name)
                .ToList();

            // Instrument check: an empty list would make every assertion below vacuously true, which
            // is what a reflection filter that quietly stopped matching anything looks like.
            Assert.NotEmpty(writerFields);

            string body = RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"),
                "private IReadOnlyList<RecordingStopStep> LiveWriters(");

            foreach (string field in writerFields)
            {
                Assert.True(body.Contains($"{field} != null", StringComparison.Ordinal),
                    $"LiveWriters does not test {field}: a start failure would leave that writer running "
                    + "while the service reports idle (issue #155)");
                Assert.True(body.Contains($"{field}.Stop, {field}.Dispose", StringComparison.Ordinal),
                    $"LiveWriters does not both stop AND dispose {field}: an undisposed writer holds its "
                    + "device and its file handle for the life of the process");
            }
        }

        /// <summary>
        /// True when every occurrence of <paramref name="needle"/> in <paramref name="body"/> lies
        /// inside a <c>new RecordingStartStep(...)</c> argument list. The range is found by matching
        /// parentheses rather than by looking for a closing spelling, so reformatting the steps
        /// cannot quietly turn this check off.
        /// </summary>
        private static bool EveryOccurrenceIsAStartStep(string body, string needle)
        {
            var steps = StartStepRanges(body).ToList();
            Assert.NotEmpty(steps);   // instrument check: no steps at all means this proves nothing

            bool found = false;
            for (int i = body.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = body.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                found = true;
                if (!steps.Any(r => i > r.Start && i < r.End)) return false;
            }
            return found;   // an occurrence that vanished entirely is a finding, not a pass
        }

        private static IEnumerable<(int Start, int End)> StartStepRanges(string body)
        {
            const string opener = "new RecordingStartStep(";
            for (int i = body.IndexOf(opener, StringComparison.Ordinal); i >= 0;
                 i = body.IndexOf(opener, i + 1, StringComparison.Ordinal))
            {
                int depth = 0;
                int j = i + opener.Length - 1;
                for (; j < body.Length; j++)
                {
                    if (body[j] == '(') depth++;
                    else if (body[j] == ')' && --depth == 0) break;
                }
                yield return (i, j);
            }
        }

        [Fact]
        public void BeginSession_WritesTheRecordAndClaimsTheDirectory()
        {
            string body = RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"),
                "private void BeginSession()");

            Assert.Contains("ManifestStore.Replace(", body, StringComparison.Ordinal);
            // A directory with a manifest.json IS a recording to every scan in the app, and this one
            // is still being captured into. The claim is what keeps the repair passes off it.
            Assert.Contains("RecordingWorkset.TryClaim(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void Stop_UpdatesTheExistingRecordInsteadOfReplacingIt()
        {
            string body = RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"),
                "public RecordResult Stop()");

            Assert.Contains("ManifestStore.Update(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ManifestStore.Replace(", body, StringComparison.Ordinal);
            // The claim taken at start is handed back, or the post-recording sequence can never
            // claim the recording it is meant to finish.
            //
            // STRENGTHENED, NOT WEAKENED (issue #28, AC16). The release now goes through
            // StrandedCameraOwner, which releases the claim UNLESS a camera ffmpeg nobody could kill
            // is still writing into that directory - releasing then would publish a live writer's
            // directory to the post-recording pipeline, which is a worse outcome than holding it.
            // Pinning the old spelling would have pinned a call site; what this pins is that the
            // stop still hands its ticket to a route that releases it.
            Assert.Contains("ReleaseClaimUnlessStranded(camera, claim, dir)", body, StringComparison.Ordinal);
            Assert.Contains("RecordingWorkset.Release(claim)", RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/StrandedCameraOwner.cs"),
                "public bool ReleaseClaimUnlessStranded("), StringComparison.Ordinal);
        }

        [Fact]
        public void OnePlaceBuildsTheDeferredMuxPlan_SoTheStartAndStopRecordsCannotDisagree()
        {
            string code = ManifestWriterTests.StripComments(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"));

            // The plan is written twice for one recording - at start with no duration yet, at stop
            // with the measured one. Two constructions would be two chances to describe different
            // work, and the start record is only worth having if it describes the same mux the stop
            // would have.
            Assert.Single(System.Text.RegularExpressions.Regex
                .Matches(code, @"new Manifest\.PendingMuxInfo"));

            // The definition plus its three callers: StartAudio, StartVideo, and the stop.
            Assert.Equal(4, System.Text.RegularExpressions.Regex
                .Matches(code, @"BuildPendingMux\(").Count);
        }
    }
}
