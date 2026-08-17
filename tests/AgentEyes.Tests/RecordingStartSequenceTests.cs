using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #155: a capture session that FAILS TO START must not leave a writer running.
    ///
    /// THE DEFECT. A session starts more than one writer - mixed audio starts the microphone and
    /// then the system loopback, system/mixed video starts ffmpeg and then the loopback. The
    /// rollback released the directory claim and cleared the session fields but left the writer
    /// already started ALIVE. The caller saw a failed start, the service reported itself idle,
    /// another recording could be started, and the first writer went on capturing the microphone and
    /// the speakers with nothing on screen saying so. The unclaimed directory could then be entered
    /// by a recovery pass while that writer still had it open. For a recorder whose whole posture is
    /// "visible, controllable", capture continuing after the app reports idle is the worst failure it
    /// has. The publish was outside the boundary too, so a failed first manifest write left the
    /// directory, a write-temp and the claim behind.
    ///
    /// Every position is injectable here - the publish, the first writer, the second writer, the
    /// video writer, and the stop and dispose of a writer being rolled back - so a failure at any of
    /// them is exercised with no sound card, no ffmpeg and no monitors. The publish, the rollback and
    /// the directory disposal are the REAL production code
    /// (<see cref="ManifestStore"/>, <see cref="RecordingWorkset"/>,
    /// <see cref="RecordingStartSequence.Discard"/>); only the writers are fakes, because a fake is
    /// the only way to make a writer fail on demand.
    ///
    /// The wiring of <see cref="RecordingService"/> onto this sequence is asserted in
    /// <see cref="SessionManifestTests"/>, which a unit test cannot drive directly.
    /// </summary>
    [Collection(ManifestSeamCollection.Name)]
    public sealed class RecordingStartSequenceTests : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _claimed = new();

        public RecordingStartSequenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-start-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            ManifestStore.InterruptBeforeReplace = null;
            foreach (string dir in _claimed) RecordingWorkset.ReleaseForTests(dir);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>
        /// A capture writer that records whether it was started, stopped and disposed, and can be
        /// told to fail at any of the three - the injected-failure seam these tests exist for.
        /// </summary>
        private sealed class FakeWriter
        {
            private readonly Exception? _startError;
            private readonly Exception? _stopError;
            private readonly Exception? _disposeError;

            public FakeWriter(string name, Exception? startError = null, Exception? stopError = null,
                Exception? disposeError = null)
            {
                Name = name;
                _startError = startError;
                _stopError = stopError;
                _disposeError = disposeError;
            }

            public string Name { get; }
            public bool Started { get; private set; }
            public bool Stopped { get; private set; }
            public bool Disposed { get; private set; }

            /// <summary>Live once construction has happened - the writer field in the service is set
            /// the moment the writer exists, which is exactly what makes a writer whose Start threw
            /// still reachable by the rollback.</summary>
            public bool Live { get; private set; }

            public RecordingStartStep StartStep => new RecordingStartStep(Name, () =>
            {
                Live = true;                                   // constructed: it now owns a device
                Started = true;
                if (_startError != null) throw _startError;
            });

            public RecordingStopStep StopStep => new RecordingStopStep(
                Name,
                () => { Stopped = true; if (_stopError != null) throw _stopError; },
                () => { Disposed = true; if (_disposeError != null) throw _disposeError; });

            public void AssertShutDown() =>
                Assert.True(Stopped && Disposed,
                    $"the {Name} writer was left running after a failed start (stopped={Stopped}, disposed={Disposed})");
        }

        /// <summary>The session under test: the real claim, the real first manifest write, and the
        /// real rollback - the same three things <see cref="RecordingService"/> does.</summary>
        private sealed class FakeSession
        {
            private readonly RecordingStartSequenceTests _owner;
            private readonly FakeWriter[] _writers;

            public FakeSession(RecordingStartSequenceTests owner, string leaf, params FakeWriter[] writers)
            {
                _owner = owner;
                _writers = writers;
                Dir = Path.Combine(owner._root, leaf);
                Directory.CreateDirectory(Dir);
                owner._claimed.Add(Dir);
            }

            public string Dir { get; }
            public bool Released { get; private set; }

            public void Start() => RecordingStartSequence.Run(
                Dir,
                Publish,
                _writers.Select(w => w.StartStep).ToList(),
                () => _writers.Where(w => w.Live).Select(w => w.StopStep).ToList(),
                Release);

            /// <summary>This session's own capture claim - what RecordingService._captureClaim is,
            /// and the only thing that may release the directory (issue #154, round 3).</summary>
            private RecordingClaimTicket _claim;

            /// <summary>What BeginSession does: claim the directory - failing the start if it is
            /// already owned - then publish the record.</summary>
            private void Publish()
            {
                if (!RecordingWorkset.TryClaim(Dir, RecordingWorkKind.Capture, "capture session", out _claim))
                    throw new UsageException($"{Dir} is already in use");
                ManifestStore.Replace(Dir, StartRecord());
            }

            /// <summary>What ReleaseSession does: clear the session, then give up the directory -
            /// through this session's own ticket.</summary>
            private void Release()
            {
                Released = true;
                var claim = _claim;
                _claim = default;
                RecordingStartSequence.Discard(Dir, claim);
            }
        }

        private static Manifest StartRecord() => new()
        {
            Mode = "audio",
            Label = "audio",
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            MonitorIndex = 1,
            MonitorName = "TEST",
            AudioFile = "audio.wav",
        };

        /// <summary>Kill the process between the complete temp and the rename - the shape of a first
        /// manifest write that fails.</summary>
        private void InterruptTheNextWrite() =>
            ManifestStore.InterruptBeforeReplace = temp =>
            {
                if (temp.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("simulated kill between the temp write and the replace");
            };

        private static void AssertNothingIsLeftBehind(string dir)
        {
            Assert.False(RecordingWorkset.IsClaimed(dir), $"{dir} is still claimed after a failed start");
            Assert.False(Directory.Exists(dir), $"{dir} survived a start that captured nothing");
        }

        // ---- the defect: a writer left running by a later writer's failure ----

        [Fact]
        public void Run_TheSecondAudioWriterFails_TheFirstIsStoppedAndDisposed()
        {
            // Mixed audio: the microphone is already capturing when the loopback fails. This is the
            // exact case that used to leave the microphone recording while the service said idle.
            var mic = new FakeWriter("microphone");
            var loop = new FakeWriter("system loopback", startError: new InvalidOperationException("no loopback device"));
            var session = new FakeSession(this, "mixed-audio", mic, loop);

            Assert.Throws<InvalidOperationException>(session.Start);

            mic.AssertShutDown();
            loop.AssertShutDown();      // it was constructed, so it too is shut down
            Assert.True(session.Released);
            AssertNothingIsLeftBehind(session.Dir);
        }

        [Fact]
        public void Run_TheLoopbackFailsAfterFfmpeg_TheVideoWriterIsStoppedAndDisposed()
        {
            // System/mixed video: ffmpeg is already writing frames when the loopback fails. Without
            // this, the screen keeps being recorded after the start reports failure.
            var video = new FakeWriter("video");
            var meter = new FakeWriter("mic level meter");
            var loop = new FakeWriter("system loopback", startError: new IOException("loopback init failed"));
            var session = new FakeSession(this, "mixed-video", video, meter, loop);

            Assert.Throws<IOException>(session.Start);

            video.AssertShutDown();
            meter.AssertShutDown();
            loop.AssertShutDown();
            AssertNothingIsLeftBehind(session.Dir);
        }

        [Fact]
        public void Run_TheVideoWriterFailsFirst_NothingIsStartedAndTheDirectoryIsRemoved()
        {
            var video = new FakeWriter("video", startError: new UsageException("ffmpeg is not on PATH"));
            var loop = new FakeWriter("system loopback");
            var session = new FakeSession(this, "video-first", video, loop);

            Assert.Throws<UsageException>(session.Start);

            video.AssertShutDown();
            Assert.False(loop.Started, "a writer after the failing one must never be started");
            Assert.False(loop.Stopped, "a writer that was never constructed is not in the rollback");
            AssertNothingIsLeftBehind(session.Dir);
        }

        // ---- the publish is inside the same boundary --------------------------

        [Fact]
        public void Run_TheFirstManifestWriteFails_TheClaimTheTempAndTheDirectoryAllGo()
        {
            // BeginSession used to sit OUTSIDE the start try, so a failed first write left the
            // directory, a manifest.json.<id>.tmp and the claim behind.
            var mic = new FakeWriter("microphone");
            var session = new FakeSession(this, "publish-fails", mic);
            InterruptTheNextWrite();

            Assert.Throws<IOException>(session.Start);
            ManifestStore.InterruptBeforeReplace = null;

            Assert.False(mic.Started, "no writer may start when the record never reached disk");
            AssertNothingIsLeftBehind(session.Dir);   // the directory goes, and the write-temp with it
        }

        [Fact]
        public void HasRawArtifacts_AManifestWriteTemp_IsNotCaptureBytes()
        {
            // The reason the temp above does not save the directory from removal: a temp is the
            // litter of a write that failed, not something that was captured.
            string dir = Path.Combine(_root, "temp-only");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ManifestStore.FileName), "{}");
            File.WriteAllText(Path.Combine(dir, ManifestStore.FileName + ".deadbeef.tmp"), "{}");

            Assert.False(RecordingStopSequence.HasRawArtifacts(dir));

            File.WriteAllText(Path.Combine(dir, "mic.wav"), "raw capture bytes");
            Assert.True(RecordingStopSequence.HasRawArtifacts(dir));   // the check still sees real bytes
        }

        // ---- the rollback is failure-isolated ---------------------------------

        [Fact]
        public void Run_AWriterThatAlsoFailsToStop_IsStillDisposedAndTheSessionIsStillReleased()
        {
            // The rollback must not be all-or-nothing either: a writer whose Stop throws is still
            // disposed (or its device stays held for the life of the process), the writers after it
            // are still stopped, and the claim is still released.
            var mic = new FakeWriter("microphone", stopError: new InvalidOperationException("stop blew up"));
            var loop = new FakeWriter("system loopback", startError: new IOException("no loopback"));
            var session = new FakeSession(this, "stop-throws", mic, loop);

            Assert.Throws<IOException>(session.Start);   // the ORIGINAL failure reaches the caller

            Assert.True(mic.Stopped && mic.Disposed, "a writer whose Stop threw must still be disposed");
            loop.AssertShutDown();
            AssertNothingIsLeftBehind(session.Dir);
        }

        [Fact]
        public void Abandon_ReportsEveryFailureItHitWhileRollingBack()
        {
            string dir = Path.Combine(_root, "abandon-report");
            Directory.CreateDirectory(dir);
            var mic = new FakeWriter("microphone", stopError: new IOException("stop failed"),
                                     disposeError: new IOException("dispose failed"));

            var failures = RecordingStartSequence.Abandon(
                dir, new[] { mic.StopStep }, () => throw new IOException("release failed"));

            Assert.Equal(new[] { "microphone stop", "microphone dispose", "session release" },
                         failures.Select(f => f.Stage).ToArray());
        }

        // ---- what a failed start leaves the NEXT recording ---------------------

        [Fact]
        public void Run_AfterAFailedStart_TheSameDirectoryCanBeClaimedAndRecordedAgain()
        {
            var loop = new FakeWriter("system loopback", startError: new IOException("no loopback"));
            var failed = new FakeSession(this, "then-retry", new FakeWriter("microphone"), loop);
            Assert.Throws<IOException>(failed.Start);

            // A claim that was not released is a recording nothing can ever touch again.
            Assert.True(RecordingWorkset.TryClaim(failed.Dir, RecordingWorkKind.Capture, "capture session", out _));
            RecordingWorkset.ReleaseForTests(failed.Dir);

            // And the service is genuinely able to record again: a clean session over the same path.
            var mic = new FakeWriter("microphone");
            var retry = new FakeSession(this, "then-retry", mic);
            retry.Start();

            Assert.True(mic.Started);
            Assert.False(mic.Stopped, "a start that succeeded must not be rolled back");
            Assert.True(File.Exists(Path.Combine(retry.Dir, ManifestStore.FileName)));
            Assert.True(RecordingWorkset.IsClaimed(retry.Dir), "a live session keeps its claim");
            RecordingWorkset.ReleaseForTests(retry.Dir);
        }

        // ---- a session that DID capture something is kept ----------------------

        [Fact]
        public void Run_TheDirectoryAlreadyHoldsCaptureBytes_IsKeptButTheClaimIsStillReleased()
        {
            // Bytes on disk plus the start manifest are a recoverable recording - that is the whole
            // reason the manifest is written first - so the directory stays. The claim does not: the
            // session is over, and the recovery passes are what finish it.
            var mic = new FakeWriter("microphone");
            var loop = new FakeWriter("system loopback", startError: new IOException("no loopback"));
            var session = new FakeSession(this, "kept", mic, loop);
            Directory.CreateDirectory(session.Dir);
            File.WriteAllText(Path.Combine(session.Dir, "mic.wav"), "raw capture bytes");

            Assert.Throws<IOException>(session.Start);

            mic.AssertShutDown();
            Assert.True(Directory.Exists(session.Dir), "a directory holding capture bytes must be kept");
            Assert.True(File.Exists(Path.Combine(session.Dir, ManifestStore.FileName)),
                        "the start record must still be beside those bytes");
            Assert.False(RecordingWorkset.IsClaimed(session.Dir), "the claim must be released either way");
        }

        // ---- the ordering the whole fix rests on -------------------------------

        [Fact]
        public void Run_PublishesBeforeItStartsAnyWriter()
        {
            var order = new List<string>();
            string dir = Path.Combine(_root, "order");
            Directory.CreateDirectory(dir);

            RecordingStartSequence.Run(
                dir,
                () => order.Add("publish"),
                new[]
                {
                    new RecordingStartStep("first", () => order.Add("first")),
                    new RecordingStartStep("second", () => order.Add("second")),
                },
                Array.Empty<RecordingStopStep>,
                () => order.Add("release"));

            Assert.Equal(new[] { "publish", "first", "second" }, order);
        }

        [Fact]
        public void Abandon_StopsEveryWriterBeforeItReleasesTheSession()
        {
            // The order IS the fix: releasing the claim first would publish a directory that a live
            // writer still has open to every automatic repair pass in the app.
            var order = new List<string>();
            string dir = Path.Combine(_root, "rollback-order");
            Directory.CreateDirectory(dir);

            RecordingStartSequence.Abandon(
                dir,
                new[]
                {
                    new RecordingStopStep("audio", () => order.Add("audio stop"), () => order.Add("audio dispose")),
                    new RecordingStopStep("loopback", () => order.Add("loopback stop"), () => order.Add("loopback dispose")),
                },
                () => order.Add("release"));

            Assert.Equal(
                new[] { "audio stop", "audio dispose", "loopback stop", "loopback dispose", "release" },
                order);
        }
    }
}
