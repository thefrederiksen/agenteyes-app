using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #153: a stop-time failure must not lose the recording.
    ///
    /// The defect: the stop stopped and disposed the audio capture, the loopback capture and the
    /// video writer in ONE try block and saved the manifest at the end of it. The first throw
    /// abandoned every writer after it AND the manifest save - and the finally block still cleared
    /// the session and reported the service idle. The recording was gone, a writer could still be
    /// open, and the callers hid it (an empty catch in tray Quit, an unlogged one in the window).
    ///
    /// Every position is injectable here - audio stop, loopback stop, video stop, each dispose, and
    /// the manifest save - so a failure at any of them is exercised with no sound card, no ffmpeg and
    /// no full disk.
    /// </summary>
    public sealed class RecordingStopSequenceTests : IDisposable
    {
        private readonly string _root;

        public RecordingStopSequenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-stop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>A writer that records whether it was stopped and disposed, and can be told to
        /// fail at either point - the injected-failure seam these tests exist for.</summary>
        private sealed class FakeWriter
        {
            private readonly Exception? _stopError;
            private readonly Exception? _disposeError;

            public FakeWriter(string name, Exception? stopError = null, Exception? disposeError = null)
            {
                Name = name;
                _stopError = stopError;
                _disposeError = disposeError;
            }

            public string Name { get; }
            public bool Stopped { get; private set; }
            public bool Disposed { get; private set; }

            public RecordingStopStep Step => new RecordingStopStep(
                Name,
                () => { Stopped = true; if (_stopError != null) throw _stopError; },
                () => { Disposed = true; if (_disposeError != null) throw _disposeError; });
        }

        /// <summary>A recording directory holding raw capture bytes - what is on disk by the time the
        /// writers have been stopped, and what a failed stop must not orphan.</summary>
        private string MakeRawRecording(string name, params string[] files)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            foreach (string file in files) File.WriteAllText(Path.Combine(dir, file), "raw capture bytes");
            return dir;
        }

        private static Manifest DeferredMuxManifest() => new()
        {
            Mode = "video",
            Label = "video",
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            MonitorIndex = 1,
            MonitorName = "TEST",
            VideoFile = "recording.mp4",
            FfmpegCommand = "ffmpeg -f gdigrab ...",
            Shots = { new Manifest.ShotEntry { OffsetSeconds = 1.5, File = "shots/000001.png" } },
            PostProcessing = { ["mux"] = new Manifest.PostStageRecord { State = "running", Attempts = 1 } },
            PendingMux = new Manifest.PendingMuxInfo
            {
                Mode = "video",
                Source = "system",
                RawVideo = "raw.mp4",
                SysWav = "sys_native.wav",
                FinalFile = "recording.mp4",
                RawDurationSeconds = 20.0,
            },
        };

        private static RecordingStopReport Run(string dir, IReadOnlyList<RecordingStopStep> steps,
            Action saveManifest, Action? saveRecovery = null) =>
            RecordingStopSequence.Run(dir, steps, saveManifest, saveRecovery ?? (() => { }));

        // ---- criterion 1: an injected failure at each position ----------------

        [Fact]
        public void Run_AudioStopThrows_LaterWritersAreStillStoppedAndDisposed()
        {
            string dir = MakeRawRecording("audio-stop-throws", "mic.wav");
            var audio = new FakeWriter("audio", stopError: new IOException("audio stop blew up"));
            var loop = new FakeWriter("loopback");
            var video = new FakeWriter("video");
            bool saved = false;

            var report = Run(dir, new[] { audio.Step, loop.Step, video.Step }, () => saved = true);

            Assert.True(audio.Disposed, "the writer that failed to stop must still be disposed");
            Assert.True(loop.Stopped && loop.Disposed, "the loopback writer must not be abandoned");
            Assert.True(video.Stopped && video.Disposed, "the video writer must not be abandoned");
            Assert.True(saved, "the manifest must still be saved - the raw bytes are already on disk");
            Assert.Equal(new[] { "audio stop" }, report.Failures.Select(f => f.Stage));
            Assert.True(report.ManifestSaved);
        }

        [Fact]
        public void Run_LoopbackStopThrows_LaterWritersAreStillStoppedAndDisposed()
        {
            string dir = MakeRawRecording("loopback-stop-throws", "sys_native.wav");
            var audio = new FakeWriter("audio");
            var loop = new FakeWriter("loopback", stopError: new InvalidOperationException("loopback stop blew up"));
            var video = new FakeWriter("video");
            bool saved = false;

            var report = Run(dir, new[] { audio.Step, loop.Step, video.Step }, () => saved = true);

            Assert.True(audio.Stopped && audio.Disposed);
            Assert.True(loop.Disposed, "the writer that failed to stop must still be disposed");
            Assert.True(video.Stopped && video.Disposed, "the video writer must not be abandoned");
            Assert.True(saved);
            Assert.Equal(new[] { "loopback stop" }, report.Failures.Select(f => f.Stage));
        }

        [Fact]
        public void Run_VideoStopThrows_ItIsStillDisposedAndTheManifestIsStillSaved()
        {
            string dir = MakeRawRecording("video-stop-throws", "raw.mp4");
            var audio = new FakeWriter("audio");
            var loop = new FakeWriter("loopback");
            var video = new FakeWriter("video", stopError: new IOException("ffmpeg would not shut down"));
            bool saved = false;

            var report = Run(dir, new[] { audio.Step, loop.Step, video.Step }, () => saved = true);

            Assert.True(audio.Stopped && audio.Disposed);
            Assert.True(loop.Stopped && loop.Disposed);
            Assert.True(video.Disposed, "an undisposed ffmpeg recorder keeps its handles for the life of the process");
            Assert.True(saved, "the manifest save came AFTER the failing writer and must still have run");
            Assert.Equal(new[] { "video stop" }, report.Failures.Select(f => f.Stage));
        }

        [Fact]
        public void Run_AudioDisposeThrows_LaterWritersAreStillStoppedAndDisposed()
        {
            string dir = MakeRawRecording("audio-dispose-throws", "mic.wav");
            var audio = new FakeWriter("audio", disposeError: new ObjectDisposedException("wave-in"));
            var loop = new FakeWriter("loopback");
            var video = new FakeWriter("video");
            bool saved = false;

            var report = Run(dir, new[] { audio.Step, loop.Step, video.Step }, () => saved = true);

            Assert.True(audio.Stopped);
            Assert.True(loop.Stopped && loop.Disposed);
            Assert.True(video.Stopped && video.Disposed);
            Assert.True(saved);
            Assert.Equal(new[] { "audio dispose" }, report.Failures.Select(f => f.Stage));
        }

        [Fact]
        public void Run_ManifestSaveThrows_EveryWriterWasStillStoppedAndDisposed()
        {
            string dir = MakeRawRecording("manifest-save-throws", "audio.wav");
            var audio = new FakeWriter("audio");
            var loop = new FakeWriter("loopback");
            var video = new FakeWriter("video");

            var report = Run(dir, new[] { audio.Step, loop.Step, video.Step },
                () => throw new IOException("There is not enough space on the disk."));

            Assert.True(audio.Stopped && audio.Disposed);
            Assert.True(loop.Stopped && loop.Disposed);
            Assert.True(video.Stopped && video.Disposed);
            Assert.False(report.ManifestSaved);
            Assert.Contains("manifest save", report.Failures.Select(f => f.Stage));
        }

        // ---- criterion 2: a manifest is written whenever raw artifacts exist --

        [Fact]
        public void Run_ManifestSaveFailsWithRawArtifacts_WritesTheRecoveryRecord()
        {
            // The raw capture of a video + system-audio take: the final recording.mp4 does not exist
            // yet because the mux is deferred (issue #77), so without a manifest NOTHING can find it.
            string dir = MakeRawRecording("recovery-written", "raw.mp4", "sys_native.wav");
            var source = DeferredMuxManifest();

            var report = Run(dir, Array.Empty<RecordingStopStep>(),
                () => throw new IOException("the manifest write failed"),
                () => RecoveryManifest.Save(source, 20.0, dir));

            Assert.False(report.ManifestSaved);
            Assert.True(report.RecoveryManifestSaved, "raw artifacts exist, so a manifest must be written");
            Assert.True(report.HasManifest);
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));

            // Recoverable by the EXISTING artifact-based detection (issue #152), not by anything new.
            Assert.True(PostRecordingPlan.NeedsMux(dir), "the deferred mux must still be outstanding");
            Assert.True(PostRecordingPlan.HasUnfinishedWork(dir));
            Assert.Contains(dir, PostRecordingPlan.FindUnfinished(_root));
        }

        [Fact]
        public void Run_ManifestSaveFailsWithNoArtifacts_WritesNothingToRecover()
        {
            // An empty directory has no bytes worth pointing at; inventing a manifest for it would
            // only add a phantom recording to the library.
            string dir = MakeRawRecording("recovery-pointless");
            bool recoveryAttempted = false;

            var report = Run(dir, Array.Empty<RecordingStopStep>(),
                () => throw new IOException("the manifest write failed"),
                () => recoveryAttempted = true);

            Assert.False(recoveryAttempted);
            Assert.False(report.HasManifest);
            Assert.True(report.Failed);
        }

        [Fact]
        public void Run_RecoveryRecordAlsoFails_ReportsBothFailuresAndNoManifest()
        {
            string dir = MakeRawRecording("recovery-fails", "audio.wav");

            var report = Run(dir, Array.Empty<RecordingStopStep>(),
                () => throw new IOException("the manifest write failed"),
                () => throw new IOException("so did the recovery write"));

            Assert.False(report.HasManifest);
            Assert.Equal(new[] { "manifest save", "recovery manifest save" }, report.Failures.Select(f => f.Stage));
        }

        [Fact]
        public void RecoveryManifest_KeepsWhatRecoveryNeeds_AndDropsTheRegenerableParts()
        {
            string dir = MakeRawRecording("recovery-shape", "raw.mp4", "sys_native.wav");
            var recovery = RecoveryManifest.From(DeferredMuxManifest(), 20.0, dir);

            // Identity + media + the deferred mux: without PendingMux a raw.mp4 is unrecoverable.
            Assert.Equal("video", recovery.Mode);
            Assert.Equal("recording.mp4", recovery.VideoFile);
            Assert.Equal(20.0, recovery.DurationSeconds);
            Assert.NotNull(recovery.PendingMux);
            Assert.Equal("raw.mp4", recovery.PendingMux!.RawVideo);
            Assert.Equal(new[] { "raw.mp4", "sys_native.wav" }, recovery.Files);

            // Dropped: the regenerable / diagnostic parts, so the smallest possible record is written
            // on the path where the full one could not be.
            Assert.Empty(recovery.PostProcessing);
            Assert.Empty(recovery.Shots);
            Assert.Null(recovery.FfmpegCommand);
        }

        // ---- criterion 3: a failed stop is distinguishable from a clean one ---

        [Fact]
        public void Run_EverythingSucceeds_ReportsACleanStop()
        {
            string dir = MakeRawRecording("clean", "audio.wav");
            var audio = new FakeWriter("audio");

            var report = Run(dir, new[] { audio.Step }, () => { });

            Assert.False(report.Failed);
            Assert.Empty(report.Failures);
            Assert.True(report.ManifestSaved);
            Assert.False(report.RecoveryManifestSaved);
            Assert.Equal("", report.Summary());
        }

        [Fact]
        public void RecordingStopFailedException_NamesTheDirectoryAndEveryFailure()
        {
            string dir = MakeRawRecording("reported", "audio.wav");
            var audio = new FakeWriter("audio", stopError: new IOException("audio stop blew up"));

            var report = Run(dir, new[] { audio.Step }, () => throw new IOException("no space"));
            var ex = new RecordingStopFailedException(report);

            Assert.Same(report, ex.Report);
            Assert.Equal(dir, ex.Dir);
            Assert.Contains(dir, ex.Message);
            Assert.Contains("audio stop", ex.Message);
            Assert.Contains("manifest save", ex.Message);
            Assert.IsType<IOException>(ex.InnerException);
        }

        // ---- criterion 4: every failure is logged, not just the first ---------

        [Fact]
        public void Run_TwoSimultaneousFailures_BothAreReportedAndBothReachTheLog()
        {
            string dir = MakeRawRecording("two-failures", "raw.mp4");
            var audio = new FakeWriter("audio", stopError: new IOException("audio stop blew up"));
            var loop = new FakeWriter("loopback");
            var video = new FakeWriter("video", stopError: new IOException("ffmpeg would not shut down"));
            long from = LogLength();

            var report = Run(dir, new[] { audio.Step, loop.Step, video.Step }, () => { });

            // BOTH, in the order they happened - not just the first, which is all the old stop could
            // ever report because the first one ended it.
            Assert.Equal(new[] { "audio stop", "video stop" }, report.Failures.Select(f => f.Stage));
            Assert.Contains("audio stop blew up", report.Summary());
            Assert.Contains("ffmpeg would not shut down", report.Summary());

            string log = LogSince(from);
            Assert.Contains($"audio stop FAILED: dir={dir}", log);
            Assert.Contains($"video stop FAILED: dir={dir}", log);
        }

        [Fact]
        public void Run_ManifestSaveFails_TheFailureReachesTheLogWithTheDirectory()
        {
            string dir = MakeRawRecording("logged-manifest", "audio.wav");
            long from = LogLength();

            Run(dir, Array.Empty<RecordingStopStep>(), () => throw new IOException("no space"),
                () => RecoveryManifest.Save(new Manifest { Mode = "audio", AudioFile = "audio.wav" }, 3.0, dir));

            string log = LogSince(from);
            Assert.Contains($"manifest save FAILED: dir={dir}", log);
            Assert.Contains(dir, log);
        }

        // ---- the raw-artifact predicate --------------------------------------

        [Fact]
        public void HasRawArtifacts_ManifestOnly_IsNotWorthRecovering()
        {
            string dir = MakeRawRecording("manifest-only");
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
            Assert.False(RecordingStopSequence.HasRawArtifacts(dir));
        }

        [Fact]
        public void HasRawArtifacts_AShotInASubfolder_Counts()
        {
            string dir = MakeRawRecording("shots-only");
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            File.WriteAllText(Path.Combine(dir, "shots", "000001.png"), "not really a png");
            Assert.True(RecordingStopSequence.HasRawArtifacts(dir));
        }

        [Fact]
        public void HasRawArtifacts_NoDirectory_IsFalse() =>
            Assert.False(RecordingStopSequence.HasRawArtifacts(Path.Combine(_root, "never-created")));

        // ---- log reading ------------------------------------------------------

        /// <summary>Where the log file ends right now, so a test reads only what IT wrote.</summary>
        private static long LogLength() =>
            File.Exists(Log.CurrentFile) ? new FileInfo(Log.CurrentFile).Length : 0;

        /// <summary>The log written since <paramref name="from"/>. Shared with the live app's logger
        /// on purpose - the criterion is that these failures reach Log.Error, so the assertion reads
        /// the real log rather than a stand-in that could pass while the app logs nothing.</summary>
        private static string LogSince(long from)
        {
            Assert.True(File.Exists(Log.CurrentFile), "nothing was logged at all - the log file does not exist");
            using var stream = new FileStream(Log.CurrentFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            Assert.False(text.Length == 0, "the log gained nothing while the stop ran");
            return text;
        }
    }
}
