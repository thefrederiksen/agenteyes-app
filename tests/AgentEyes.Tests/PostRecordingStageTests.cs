using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #152: the post-recording sequence must SURVIVE - one stage failing must not cost the
    /// stages after it, and an interrupted sequence must be resumable rather than abandoned.
    ///
    /// The two defects these tests pin down:
    ///  - The stages sat in ONE try block, so a transient ffmpeg error extracting a poster frame
    ///    skipped packaging entirely: a thumbnail cost the transcript, permanently, because the
    ///    repair passes retried the thumbnail and the title but never the transcription.
    ///  - Stage progress lived only in memory, so a crash or an update restart left a recording
    ///    half-processed with nothing on disk saying how far it got.
    ///
    /// The stages are injectable steps, so all of this is exercised with no ffmpeg, no network and no
    /// wallet. Every test restores the production steps.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class PostRecordingStageTests : IDisposable
    {
        private readonly string _root;

        public PostRecordingStageTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-stages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            PostRecording.RestoreDefaultSteps();
            PostRecording.AfterPackaging = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>
        /// A stopped audio recording with its media on disk and nothing else: no thumbnail, no
        /// transcript. The shape every stop path produces.
        ///
        /// With <paramref name="pendingMux"/> it is the shape a stop leaves BEFORE the deferred mux
        /// (issue #77) has run: the raw capture files exist but the final audio.wav does not, which
        /// is exactly why no existing backlog could see such a recording.
        /// </summary>
        private string MakeRecording(string name, bool pendingMux = false)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            if (pendingMux) File.WriteAllText(Path.Combine(dir, "sys_native.wav"), "raw loopback capture");
            else File.WriteAllText(Path.Combine(dir, "audio.wav"), "not really a wav");

            var manifest = new Manifest
            {
                Mode = "audio",
                Label = "audio",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                AudioFile = "audio.wav",
                DurationSeconds = 12.0,
            };
            if (pendingMux)
            {
                manifest.PendingMux = new Manifest.PendingMuxInfo
                {
                    Mode = "audio",
                    Source = "mixed",
                    MicWav = "mic.wav",
                    SysWav = "sys_native.wav",
                    FinalFile = "audio.wav",
                    RawDurationSeconds = 12.0,
                };
            }
            ManifestStore.Replace(dir, manifest);
            return dir;
        }

        private static string TranscriptPath(string dir) => Path.Combine(dir, "transcript.json");

        /// <summary>A packaging step that succeeds the way the real one does - by leaving a
        /// transcript on disk.</summary>
        private static Action<string> WritesTranscript(List<string> ran) => dir =>
        {
            ran.Add(PostStage.Package);
            File.WriteAllText(TranscriptPath(dir), "[]");
        };

        /// <summary>A thumbnail step that succeeds by leaving a poster on disk.</summary>
        private static Action<string> WritesThumbnail(List<string> ran) => dir =>
        {
            ran.Add(PostStage.Thumbnail);
            File.WriteAllText(Path.Combine(dir, "thumb.png"), "poster");
        };

        // ---- criterion 1: a thumbnail failure must not cost the transcript ----

        [Fact]
        public void Run_ThumbnailStageThrows_TheRecordingIsStillTranscribed()
        {
            // THE defect. Thumbnails.Ensure shells out to ffmpeg and throws on a non-zero exit; that
            // used to abort the whole sequence, so the recording was never transcribed and never
            // titled - and nothing ever retried the transcription.
            string dir = MakeRecording("2026-08-11_100000_audio");
            var ran = new List<string>();

            PostRecording.ThumbnailStep = _ =>
            {
                ran.Add(PostStage.Thumbnail);
                throw new InvalidOperationException("ffmpeg exited 1 (poster thumbnail)");
            };
            PostRecording.PackageStep = WritesTranscript(ran);

            PostRecording.Run(dir);

            Assert.Equal(new[] { PostStage.Thumbnail, PostStage.Package }, ran);
            Assert.True(File.Exists(TranscriptPath(dir)), "the transcript must survive a failed thumbnail");
            Assert.Null(Thumbnails.PathFor(dir));

            var manifest = Manifest.Load(dir);
            Assert.Equal(PostStageState.Failed, manifest.PostProcessing[PostStage.Thumbnail].State);
            Assert.Contains("ffmpeg exited 1", manifest.PostProcessing[PostStage.Thumbnail].Error);
            Assert.Equal(PostStageState.Done, manifest.PostProcessing[PostStage.Package].State);
        }

        [Fact]
        public void Run_ThumbnailStageThrows_TheRecordingStillLeavesTheTranscriptionBacklog()
        {
            // The user-visible half of the same criterion: after the pass the recording is no longer
            // waiting for a transcript, so no repair pass has to rescue it.
            string dir = MakeRecording("2026-08-11_100100_audio");
            PostRecording.ThumbnailStep = _ => throw new InvalidOperationException("poster failed");
            PostRecording.PackageStep = WritesTranscript(new List<string>());

            PostRecording.Run(dir);

            Assert.False(TranscriptionBacklog.NeedsTranscription(dir));
        }

        // ---- criterion 2: durable outcomes, and resuming at the next stage ----

        [Fact]
        public void Run_EveryStage_RecordsItsOutcomeInTheManifest()
        {
            string dir = MakeRecording("2026-08-11_100200_audio");
            PostRecording.ThumbnailStep = WritesThumbnail(new List<string>());
            PostRecording.PackageStep = WritesTranscript(new List<string>());

            PostRecording.Run(dir);

            var manifest = Manifest.Load(dir);
            Assert.Equal(PostStageState.Done, manifest.PostProcessing[PostStage.Thumbnail].State);
            Assert.Equal(PostStageState.Done, manifest.PostProcessing[PostStage.Package].State);
            Assert.Equal(1, manifest.PostProcessing[PostStage.Package].Attempts);
            Assert.NotNull(manifest.PostProcessing[PostStage.Package].LastAttemptUtc);
        }

        [Fact]
        public void Resume_InterruptedAfterTheThumbnail_StartsAtPackagingInsteadOfTheBeginning()
        {
            // The shape a killed process leaves: media muxed, poster written, no transcript. The
            // recovery pass must pick up at the packaging stage - not redo the earlier ones, and not
            // walk away from the recording.
            string dir = MakeRecording("2026-08-11_100300_audio");
            File.WriteAllText(Path.Combine(dir, "thumb.png"), "poster from the interrupted pass");

            Assert.Equal(new[] { PostStage.Package }, PostRecordingPlan.Outstanding(dir).ToArray());

            var ran = new List<string>();
            PostRecording.MuxStep = _ => ran.Add(PostStage.Mux);
            PostRecording.ThumbnailStep = _ => ran.Add(PostStage.Thumbnail);
            PostRecording.PackageStep = WritesTranscript(ran);

            var outcome = PostRecording.Resume(dir, hostedWorkAllowed: true);

            Assert.Equal(new[] { PostStage.Package }, ran);
            Assert.Equal(new[] { PostStage.Package }, outcome.Completed.ToArray());
            Assert.False(outcome.AnyFailed);
            Assert.True(File.Exists(TranscriptPath(dir)));
        }

        [Fact]
        public void Resume_NothingOutstanding_DoesNoWorkAtAll()
        {
            // Idempotence: a finished recording must not be re-transcribed by the periodic pass -
            // that would spend credits on every tick, forever.
            string dir = MakeRecording("2026-08-11_100400_audio");
            File.WriteAllText(Path.Combine(dir, "thumb.png"), "poster");
            File.WriteAllText(TranscriptPath(dir), "[]");

            var ran = new List<string>();
            PostRecording.MuxStep = _ => ran.Add(PostStage.Mux);
            PostRecording.ThumbnailStep = _ => ran.Add(PostStage.Thumbnail);
            PostRecording.PackageStep = WritesTranscript(ran);

            var outcome = PostRecording.Resume(dir, hostedWorkAllowed: true);

            Assert.Empty(ran);
            Assert.Empty(outcome.Completed);
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void Resume_SignedOut_LeavesPackagingOutstandingInsteadOfBurningAnAttempt()
        {
            // Hosted work cannot succeed signed out, and attempting it would spend one of the
            // recording's three transcription attempts to prove it.
            string dir = MakeRecording("2026-08-11_100500_audio");
            var ran = new List<string>();
            PostRecording.ThumbnailStep = WritesThumbnail(ran);
            PostRecording.PackageStep = WritesTranscript(ran);

            PostRecording.Resume(dir, hostedWorkAllowed: false);

            Assert.Equal(new[] { PostStage.Thumbnail }, ran);
            Assert.Contains(PostStage.Package, PostRecordingPlan.Outstanding(dir));
            Assert.Equal(0, Manifest.Load(dir).TranscribeAttempts);
        }

        // ---- the dependency rule --------------------------------------------

        [Fact]
        public void Run_MuxStageThrows_LeavesTheDependentStagesOutstandingForTheNextPass()
        {
            // A mux that wrote its output file and then failed is the nastiest case: the later
            // stages LOOK runnable. They are not - the recording is not known to be finalized - so
            // they must stay outstanding rather than run against a half-finished state.
            string dir = MakeRecording("2026-08-11_100600_audio", pendingMux: true);
            var ran = new List<string>();

            PostRecording.MuxStep = d =>
            {
                ran.Add(PostStage.Mux);
                File.WriteAllText(Path.Combine(d, "audio.wav"), "muxed, but the manifest never landed");
                throw new IOException("the manifest could not be written");
            };
            PostRecording.ThumbnailStep = WritesThumbnail(ran);
            PostRecording.PackageStep = WritesTranscript(ran);

            PostRecording.Run(dir);

            Assert.Equal(new[] { PostStage.Mux }, ran);
            var manifest = Manifest.Load(dir);
            Assert.Equal(PostStageState.Failed, manifest.PostProcessing[PostStage.Mux].State);
            Assert.Equal(1, manifest.PostProcessing[PostStage.Mux].Attempts);
            Assert.True(PostRecordingPlan.NeedsMux(dir), "the mux must still be outstanding");

            // The next pass, with a mux that works, finishes the whole recording.
            ran.Clear();
            PostRecording.MuxStep = d =>
            {
                ran.Add(PostStage.Mux);
                var m = Manifest.Load(d);
                m.PendingMux = null;
                ManifestStore.Replace(d, m);
            };

            PostRecording.Resume(dir, hostedWorkAllowed: true);

            Assert.Equal(new[] { PostStage.Mux, PostStage.Thumbnail, PostStage.Package }, ran);
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void Resume_MuxKeepsFailing_DropsOutOfTheAutomaticPassAtItsCeiling()
        {
            // Bounded unattended recovery: a raw capture ffmpeg can never mux must not run ffmpeg on
            // every 15-minute tick for the life of the machine.
            string dir = MakeRecording("2026-08-11_100700_audio", pendingMux: true);
            int attempts = 0;
            PostRecording.MuxStep = _ => { attempts++; throw new InvalidOperationException("ffmpeg exited 1"); };

            for (int i = 0; i < PostRecordingState.MaxMuxAttempts + 2; i++)
                PostRecording.Resume(dir, hostedWorkAllowed: true);

            Assert.Equal(PostRecordingState.MaxMuxAttempts, attempts);
            Assert.False(PostRecordingPlan.NeedsMux(dir));
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void Run_PackagingFails_SkipsThePluginsThatConsumeItsArtifacts()
        {
            string dir = MakeRecording("2026-08-11_100800_audio");
            bool pluginsRan = false;
            PostRecording.ThumbnailStep = WritesThumbnail(new List<string>());
            PostRecording.PackageStep = _ => throw new InvalidOperationException("out of credits");
            PostRecording.AfterPackaging = (_, _) => pluginsRan = true;

            PostRecording.Run(dir);

            Assert.False(pluginsRan);
        }

        [Fact]
        public void Run_PackagingSucceeds_RunsThePluginStepAndRecordsIt()
        {
            string dir = MakeRecording("2026-08-11_100900_audio");
            int pluginRuns = 0;
            PostRecording.ThumbnailStep = WritesThumbnail(new List<string>());
            PostRecording.PackageStep = WritesTranscript(new List<string>());
            PostRecording.AfterPackaging = (_, _) => pluginRuns++;

            PostRecording.Run(dir);

            Assert.Equal(1, pluginRuns);
            Assert.Equal(PostStageState.Done, Manifest.Load(dir).PostProcessing[PostStage.Plugins].State);
        }

        [Fact]
        public void Run_TheRecordingHasNoManifest_ReportsItInsteadOfQuietlyDoingNothing()
        {
            // Every stage asks the manifest what it needs to do, so "no manifest" must not read as
            // "no work to do" - it is a stop that failed to write the recording, and the repo
            // standard is to fail explicitly, never silently.
            string dir = Path.Combine(_root, "2026-08-11_102000_audio");
            Directory.CreateDirectory(dir);

            Exception? reported = null;
            void OnFailed(string d, Exception ex)
            {
                if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) reported = ex;
            }

            PostRecording.Failed += OnFailed;
            try { PostRecording.Run(dir); }
            finally { PostRecording.Failed -= OnFailed; }

            Assert.IsType<UsageException>(reported);
            Assert.Contains("no manifest.json", reported!.Message, StringComparison.Ordinal);
        }

        // ---- criterion 6: post-processing counts as an active session --------

        [Fact]
        public void IsBusy_WhileAStageRuns_ReportsTheAppAsNotIdle()
        {
            // Capture-idle but post-processing-busy: the state an update restart used to fire into.
            string dir = MakeRecording("2026-08-11_101000_audio");
            bool busyDuringPackaging = false;

            PostRecording.ThumbnailStep = WritesThumbnail(new List<string>());
            PostRecording.PackageStep = d =>
            {
                busyDuringPackaging = PostRecording.IsBusy;
                File.WriteAllText(TranscriptPath(d), "[]");
            };

            PostRecording.Run(dir);

            Assert.True(busyDuringPackaging, "post-recording work in flight must read as busy");
            Assert.False(PostRecording.IsBusy);
            Assert.Equal(0, PostRecording.WorkInFlight);
        }

        [Fact]
        public void WorkIdle_FiresOnlyWhenTheLastJobFinishes()
        {
            // The signal a deferred update restart waits for. A nested job must not announce idle
            // while the outer one is still running - that is the gap the old wiring restarted into.
            string dir = MakeRecording("2026-08-11_101100_audio");
            int idle = 0;
            void OnIdle() => idle++;

            PostRecording.ThumbnailStep = WritesThumbnail(new List<string>());
            PostRecording.PackageStep = WritesTranscript(new List<string>());
            PostRecording.WorkIdle += OnIdle;
            try
            {
                using (PostRecording.TrackWork("the stop that decided to keep this recording"))
                {
                    PostRecording.Run(dir);
                    Assert.Equal(0, idle);
                }
                Assert.Equal(1, idle);
            }
            finally
            {
                PostRecording.WorkIdle -= OnIdle;
            }
        }

        [Fact]
        public void TrackWork_ReleasedTwice_DoesNotMakeTheAppLookIdle()
        {
            var first = PostRecording.TrackWork("first");
            var second = PostRecording.TrackWork("second");

            first.Dispose();
            first.Dispose();   // a double release must not decrement twice

            Assert.True(PostRecording.IsBusy);
            second.Dispose();
            Assert.False(PostRecording.IsBusy);
        }

        // ---- scanning: one damaged recording must not stop the others --------

        [Fact]
        public void FindUnfinished_ACorruptManifest_IsSkippedAndTheOthersAreStillFound()
        {
            // manifest.json is not written atomically yet (tracked separately), so a torn file is a
            // real possibility. It must cost that ONE recording, never the recovery of the rest.
            string good = MakeRecording("2026-08-11_101200_audio");
            string bad = Path.Combine(_root, "2026-08-11_101300_audio");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "audio.wav"), "media");
            File.WriteAllText(Path.Combine(bad, "manifest.json"), "{ \"Mode\": \"audio\", ");   // torn write

            var found = PostRecordingPlan.FindUnfinished(_root).Select(Path.GetFileName).ToArray();

            Assert.Equal(new[] { Path.GetFileName(good) }, found);
        }

        [Fact]
        public void FindUnfinished_WorkAlreadyInFlight_LeavesThatRecordingToItsOwner()
        {
            string dir = MakeRecording("2026-08-11_101400_audio");
            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "a stand-in owner", out _));
            try
            {
                Assert.Empty(PostRecordingPlan.FindUnfinished(_root));
            }
            finally
            {
                RecordingWorkset.ReleaseForTests(dir);
            }

            Assert.Single(PostRecordingPlan.FindUnfinished(_root));
        }

        [Fact]
        public void FindUnfinished_PendingMuxWithNoFinalMedia_IsPickedUp()
        {
            // The case no existing backlog could see: TranscriptionBacklog and Thumbnails both
            // require final media on disk, and a leftover pending mux is exactly the recording that
            // has none. Nothing finalized it before this pass existed.
            string dir = Path.Combine(_root, "2026-08-11_101500_video");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "raw.mp4"), "raw capture");
            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                Label = "video",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                VideoFile = "recording.mp4",
                PendingMux = new Manifest.PendingMuxInfo
                {
                    Mode = "video",
                    Source = "mixed",
                    RawVideo = "raw.mp4",
                    SysWav = "sys_native.wav",
                    FinalFile = "recording.mp4",
                    RawDurationSeconds = 30,
                },
            });

            Assert.False(TranscriptionBacklog.NeedsTranscription(dir));
            Assert.False(Thumbnails.NeedsThumb(dir));
            Assert.Equal(new[] { dir }, PostRecordingPlan.FindUnfinished(_root).ToArray());
        }

        // ---- the durable record itself ---------------------------------------

        [Fact]
        public void PostProcessing_AnOldManifestWithoutTheField_ReadsAsNothingReportedYet()
        {
            // Backward compatibility: 33 recordings on the author's machine predate this field.
            string dir = Path.Combine(_root, "2026-08-11_101600_audio");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{ \"Tool\": \"AgentEyes\", \"Mode\": \"audio\", \"AudioFile\": \"audio.wav\" }");

            var manifest = Manifest.Load(dir);

            Assert.Empty(manifest.PostProcessing);
            Assert.Equal(0, PostRecordingState.Attempts(manifest, PostStage.Package));
            Assert.False(PostRecordingState.IsDone(manifest, PostStage.Package));
        }

        [Fact]
        public void NoteStarted_ThenNoteFailed_KeepsTheAttemptAndRecordsTheReason()
        {
            string dir = MakeRecording("2026-08-11_101700_audio");

            PostRecordingState.NoteStarted(dir, PostStage.Mux);
            PostRecordingState.NoteFailed(dir, PostStage.Mux, "ffmpeg exited 1");

            var record = Manifest.Load(dir).PostProcessing[PostStage.Mux];
            Assert.Equal(PostStageState.Failed, record.State);
            Assert.Equal(1, record.Attempts);
            Assert.Equal("ffmpeg exited 1", record.Error);
        }

        [Fact]
        public void NoteFailed_AVeryLongMessage_IsTruncatedInTheManifest()
        {
            string dir = MakeRecording("2026-08-11_101800_audio");

            PostRecordingState.NoteFailed(dir, PostStage.Package, new string('x', 5000));

            Assert.Equal(PostRecordingState.MaxErrorChars,
                Manifest.Load(dir).PostProcessing[PostStage.Package].Error!.Length);
        }

        [Fact]
        public void NoteDone_AfterAFailure_ClearsTheRecordedError()
        {
            string dir = MakeRecording("2026-08-11_101900_audio");
            PostRecordingState.NoteFailed(dir, PostStage.Thumbnail, "ffmpeg exited 1");

            PostRecordingState.NoteDone(dir, PostStage.Thumbnail);

            var record = Manifest.Load(dir).PostProcessing[PostStage.Thumbnail];
            Assert.Equal(PostStageState.Done, record.State);
            Assert.Null(record.Error);
        }

        // ---- the wiring that has to exist outside this assembly ---------------

        [Fact]
        public void TranscriptionBackfill_NoLongerHangsOffTheWindow()
        {
            // Criterion 5. The window's private backfill was the ONLY transcription recovery, and
            // --tray never builds the window - so in the app's normal mode nothing recovered.
            string window = RepoSource.Read(@"src\AgentEyes.App\MainWindow.xaml.cs");

            Assert.DoesNotContain("RunTranscriptionBackfillAsync", window, StringComparison.Ordinal);
            Assert.DoesNotContain("BackfillOneAsync", window, StringComparison.Ordinal);
        }

        [Fact]
        public void RecoveryPass_RunsFromTheAppLevelRepairService()
        {
            // The pass must run where there is no window: RepairService is constructed in
            // App.OnStartup and ticks on its own timer.
            string repair = RepoSource.Read(@"src\AgentEyes.Core\RepairService.cs");
            string runAsync = RepoSource.MethodBody(repair, "public async Task RunAsync(string trigger)");

            Assert.Contains("await ResumeUnfinishedAsync(", runAsync, StringComparison.Ordinal);
            Assert.Contains("PostRecording.Resume(dir, hostedWorkAllowed)", repair, StringComparison.Ordinal);
            Assert.Contains("_repair = new RepairService(", RepoSource.Read(@"src\AgentEyes.App\App.xaml.cs"),
                StringComparison.Ordinal);
        }
    }
}
