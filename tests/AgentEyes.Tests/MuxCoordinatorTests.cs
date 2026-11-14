using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #128 round two. QA measured, on Windows:
    ///  - D1 (criteria 12, 13): the app's mux and an `agenteyes package` mux writing ONE
    ///    recording.mp4 at the same instant, and the package attempt not counted.
    ///  - D2 (criterion 11): a mux ended by process death leaves the manifest at "running" with no
    ///    error forever and a moov-less fragment at the FINAL path, which the thumbnail stage then
    ///    ran on ("moov atom not found") and a transcribe attempt was burned on.
    /// The post-recording stages are injectable, so the sequencing here needs no real mux; the
    /// tests that need ffmpeg to fail use a raw.mp4 that is not a video at all.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class MuxCoordinatorTests : IDisposable
    {
        private readonly string _root;

        public MuxCoordinatorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-128r2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            PostRecording.RestoreDefaultSteps();
            PostRecording.AfterPackaging = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        // ---- fixtures ---------------------------------------------------------------------------

        /// <summary>A mic-only video recording whose deferred mux is pending, with a raw.mp4.</summary>
        private string PendingVideo(string name, string? muxState = null, int attempts = 0, bool fragment = false)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "raw.mp4"), "not a video - any mux of this fails");
            if (fragment) File.WriteAllBytes(Path.Combine(dir, "recording.mp4"), new byte[4096]);

            var m = new Manifest
            {
                Mode = "video",
                Label = "video",
                CreatedUtc = "2026-11-14T17:00:00Z",
                VideoFile = "recording.mp4",
                DurationSeconds = 90,
                PendingMux = new Manifest.PendingMuxInfo
                {
                    Mode = "video", Source = "mic", RawVideo = "raw.mp4", FinalFile = "recording.mp4",
                    RawDurationSeconds = 90,
                    Options = new AudioMixOptions { NoiseSuppression = false, NoiseGate = false, VoiceLeveling = false },
                },
            };
            if (muxState != null)
                m.PostProcessing[PostStage.Mux] = new Manifest.PostStageRecord
                {
                    State = muxState, Attempts = attempts, LastAttemptUtc = new DateTime(2026, 11, 14, 17, 43, 2, DateTimeKind.Utc),
                };
            ManifestStore.Replace(dir, m);
            return dir;
        }

        private static Manifest.PostStageRecord? MuxRecord(string dir) =>
            PostRecordingState.Get(Manifest.Load(dir), PostStage.Mux);

        private static FileStream HoldLockLikeAnotherProcess(string dir) =>
            new(Path.Combine(dir, MuxLock.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // ---- D1: one writer, across processes --------------------------------------------------

        [Fact]
        public void MuxLock_HeldByAnotherProcess_IsRefusedUntilThatProcessDies()
        {
            // The real thing: a separate OS process takes the lock exactly as MuxLock does
            // (exclusive, no sharing), then is killed - the restart case. Its death must free the
            // lock with nothing left to clean up, which is what makes Reconcile's "nobody holds the
            // lock, so a 'running' record is dead" sound.
            string dir = Path.Combine(_root, "xproc");
            Directory.CreateDirectory(dir);
            string lockPath = Path.Combine(dir, MuxLock.FileName);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         $"$s=[IO.File]::Open('{lockPath}','OpenOrCreate','ReadWrite','None'); 'LOCKED'; [Console]::Out.Flush(); Start-Sleep 60" })
                psi.ArgumentList.Add(a);
            using var holder = Process.Start(psi)!;
            try
            {
                string? line = holder.StandardOutput.ReadLine();
                Assert.True(line == "LOCKED", $"broken fixture: the holder process did not report holding the lock (got '{line}')");

                Assert.Null(MuxLock.TryAcquire(dir, "test while another process holds it"));
                var refused = Assert.Throws<UsageException>(() => MuxLock.Acquire(dir, "test"));
                Assert.Contains("already running in another AgentEyes process", refused.Message);

                holder.Kill(entireProcessTree: true);
                holder.WaitForExit();

                using var after = MuxLock.TryAcquire(dir, "test after the holder died");
                Assert.True(after != null, "the lock was still held after the process holding it was killed");
            }
            finally
            {
                if (!holder.HasExited) holder.Kill(entireProcessTree: true);
            }
        }

        [Fact]
        public void FinalizePending_LockHeldByAnotherProcess_RefusesWithoutStartingASecondWriter()
        {
            string dir = PendingVideo("2026-11-14_130000_d1finalize");
            using (HoldLockLikeAnotherProcess(dir))
            {
                var ex = Assert.Throws<UsageException>(() => RecordingService.FinalizePending(dir));
                // Not an ffmpeg failure: no ffmpeg was started at all.
                Assert.Contains("refusing to start a second writer", ex.Message);
            }
            Assert.False(File.Exists(Path.Combine(dir, "recording.mp4")), "a refused mux wrote to the final path");
        }

        [Fact]
        public void RunForPackage_FailedAttempt_IsCountedAndJournaled()
        {
            // The package path used to run an UNCOUNTED mux (Package.cs FinalizePending with no
            // NoteStarted). Measured: Attempts stayed at 2 across a CLI mux the watchdog killed.
            string dir = PendingVideo("2026-11-14_130000_d1count", PostStageState.Failed, attempts: 1);

            var ex = Assert.Throws<UsageException>(() => MuxCoordinator.RunForPackage(dir));

            var rec = MuxRecord(dir)!;
            Assert.Equal(2, rec.Attempts);
            Assert.Equal(PostStageState.Failed, rec.State);
            Assert.Contains("ffmpeg failed during", ex.Message);
            Assert.Contains("ffmpeg failed during", rec.Error);
        }

        [Fact]
        public void PackageRun_PendingMuxThatFails_CountsTheAttempt()
        {
            // The real `agenteyes package` entry, not the coordinator alone: if Package.RunAsync goes
            // back to calling FinalizePending directly, this attempt is uncounted again and this fails.
            string dir = PendingVideo("2026-11-14_130000_d1packagerun", PostStageState.Failed, attempts: 1);

            Assert.Throws<UsageException>(() => Package.Run(dir));

            var rec = MuxRecord(dir)!;
            Assert.Equal(2, rec.Attempts);
            Assert.Equal(PostStageState.Failed, rec.State);
        }

        [Fact]
        public void RunForPackage_LockHeldByAnotherProcess_RefusesAndDoesNotCount()
        {
            string dir = PendingVideo("2026-11-14_130000_d1busy", PostStageState.Failed, attempts: 1);
            using (HoldLockLikeAnotherProcess(dir))
            {
                var ex = Assert.Throws<UsageException>(() => MuxCoordinator.RunForPackage(dir));
                Assert.Contains("refusing to start a second writer", ex.Message);
            }
            Assert.Equal(1, MuxRecord(dir)!.Attempts);
        }

        [Fact]
        public void PostRecordingRun_LockHeldByAnotherProcess_LeavesTheMuxToItAndCountsNothing()
        {
            string dir = PendingVideo("2026-11-14_130000_d1runner");
            var ran = new List<string>();
            PostRecording.MuxStep = _ => ran.Add(PostStage.Mux);
            PostRecording.ThumbnailStep = _ => ran.Add(PostStage.Thumbnail);
            PostRecording.PackageStep = _ => ran.Add(PostStage.Package);

            PostRecordingOutcome outcome;
            using (HoldLockLikeAnotherProcess(dir))
                outcome = PostRecording.Resume(dir, hostedWorkAllowed: true);

            // A clean skip, not a pass that blew up before any stage could run (which would also run nothing).
            Assert.True(outcome.Error == null, "the pass failed instead of leaving the mux to its owner: " + outcome.Error);
            Assert.Empty(ran);
            Assert.Null(MuxRecord(dir));
        }

        // ---- D2: a mux ended by process death -------------------------------------------------

        [Fact]
        public void PostRecordingRun_HardKillOnTheLastAttempt_RecordsInterruptedRemovesTheFragmentAndRunsNothingOnIt()
        {
            // QA's L3 -> L4: the last attempt was hard-killed. Before: State stayed "running" with no
            // error forever, the 18 MB fragment stayed at the final path, and the thumbnail stage ran
            // on it twice and a transcribe attempt was burned.
            string dir = PendingVideo("2026-11-14_130000_d2last", PostStageState.Running,
                attempts: PostRecordingState.MaxMuxAttempts, fragment: true);
            var ran = new List<string>();
            PostRecording.MuxStep = _ => ran.Add(PostStage.Mux);
            PostRecording.ThumbnailStep = _ => ran.Add(PostStage.Thumbnail);
            PostRecording.PackageStep = _ => ran.Add(PostStage.Package);

            PostRecording.Run(dir);

            var rec = MuxRecord(dir)!;
            Assert.Equal(PostStageState.Failed, rec.State);
            Assert.Equal(PostRecordingState.MaxMuxAttempts, rec.Attempts);
            Assert.StartsWith(MuxCoordinator.InterruptedPrefix, rec.Error);
            Assert.Contains($"mux attempt {PostRecordingState.MaxMuxAttempts}", rec.Error);
            Assert.False(File.Exists(Path.Combine(dir, "recording.mp4")), "the dead attempt's fragment is still at the final path");
            Assert.Empty(ran);

            // And packaging now reports a real reason instead of "(no error recorded)".
            var refused = Assert.Throws<UsageException>(() => Package.RefuseExhaustedMux(dir));
            Assert.Contains(MuxCoordinator.InterruptedPrefix, refused.Message);
        }

        [Fact]
        public void PostRecordingRun_HardKillWithAttemptsLeft_TheRetryNeverSeesTheFragmentAndIsCounted()
        {
            string dir = PendingVideo("2026-11-14_130000_d2retry", PostStageState.Running, attempts: 1, fragment: true);
            bool fragmentVisibleToMux = true;
            int attemptsSeenByMux = -1;
            PostRecording.MuxStep = d =>
            {
                fragmentVisibleToMux = File.Exists(Path.Combine(d, "recording.mp4"));
                attemptsSeenByMux = MuxRecord(d)!.Attempts;
                throw new UsageException("test mux failure");
            };

            PostRecording.Run(dir);

            Assert.False(fragmentVisibleToMux, "the retry started with the dead attempt's fragment still at the final path");
            Assert.Equal(2, attemptsSeenByMux);
            Assert.Equal(PostStageState.Failed, MuxRecord(dir)!.State);
        }

        [Fact]
        public void Reconcile_FinalFilePresentButRawInputGone_KeepsTheFile()
        {
            // A death between the mux completing (raw renamed to the .original backup) and the
            // manifest update: the final file IS complete and must not be deleted.
            string dir = PendingVideo("2026-11-14_130000_d2keep", PostStageState.Running, attempts: 1, fragment: true);
            File.Move(Path.Combine(dir, "raw.mp4"), Path.Combine(dir, "recording.original.mp4"));

            using (MuxLock.Acquire(dir, "test"))
                MuxCoordinator.Reconcile(dir);

            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")), "a completed mux output was deleted");
            Assert.StartsWith(MuxCoordinator.InterruptedPrefix, MuxRecord(dir)!.Error);
        }

        [Fact]
        public void Reconcile_WithoutHoldingTheLock_Throws()
        {
            string dir = PendingVideo("2026-11-14_130000_d2nolock", PostStageState.Running, attempts: 1, fragment: true);
            Assert.Throws<InvalidOperationException>(() => MuxCoordinator.Reconcile(dir));
            Assert.True(File.Exists(Path.Combine(dir, "recording.mp4")));
        }

        [Fact]
        public void NeedsThumb_PendingMuxWithAFileAtTheFinalPath_IsFalse()
        {
            string dir = PendingVideo("2026-11-14_130000_d2thumb", fragment: true);
            Assert.False(Thumbnails.NeedsThumb(dir, respectClaim: false));
        }

        [Fact]
        public void NeedsTranscription_PendingMuxWithAFileAtTheFinalPath_IsFalse()
        {
            string dir = PendingVideo("2026-11-14_130000_d2transcribe", fragment: true);
            Assert.False(TranscriptionBacklog.NeedsTranscription(dir));
        }

        [Fact]
        public void NeedsThumbAndTranscription_MuxDone_AreTrue()
        {
            // The guard above must not have made the predicates blind: the same recording with its
            // mux finished needs both.
            string dir = PendingVideo("2026-11-14_130000_d2control", fragment: true);
            ManifestStore.Update(dir, m => m.PendingMux = null);
            Assert.True(Thumbnails.NeedsThumb(dir, respectClaim: false));
            Assert.True(TranscriptionBacklog.NeedsTranscription(dir));
        }
    }
}
