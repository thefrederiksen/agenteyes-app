using System;
using System.IO;
using System.Linq;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #154, round 3: a capture must OWN the directory it records into, and a release must only
    /// remove the caller's OWN claim.
    ///
    /// THE DEFECT THESE EXIST FOR. <c>RecordingService.BeginSession</c> read the result of
    /// <c>TryClaim</c>, logged the refusal, and carried on anyway - bumping the capture epoch,
    /// replacing the owner's manifest and starting writers into a directory that belonged to the
    /// previous session's post-recording pipeline. Worse, the stop then ran an unconditional
    /// <c>Release(dir)</c>, and releasing by NAME removes whichever claim is there: the capture that
    /// never had a claim tore down the claim of the pipeline that had refused it, and every automatic
    /// pass in the app was then free to write that recording underneath it.
    ///
    /// It needs a directory-name collision to happen at all (<c>RecordingPaths.NewDir</c> stamps to
    /// the second with no collision suffix). That NAME defect is issue #169 and is deliberately NOT
    /// fixed here: these tests assume names can still collide and pin that the claim logic is correct
    /// anyway.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class CaptureClaimOwnershipTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dir;

        public CaptureClaimOwnershipTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-own-" + Guid.NewGuid().ToString("N"));
            _dir = Path.Combine(_root, "2026-08-12_130000_audio");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            RecordingWorkset.ReleaseForTests(_dir);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- the release is ownership-specific ---------------------------------

        [Fact]
        public void Release_ATicketFromARefusedClaim_RemovesNothing()
        {
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.FullPipeline, "post-recording", out var owner));
            try
            {
                Assert.False(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "capture session", out var refused));
                Assert.False(refused.Held);

                RecordingWorkset.Release(refused);   // what the failed capture's stop used to do

                Assert.True(RecordingWorkset.IsClaimed(_dir), "a refused claimant must not free the owner's claim");
                Assert.Equal(RecordingWorkKind.FullPipeline, RecordingWorkset.OwnerKind(_dir));
            }
            finally { RecordingWorkset.Release(owner); }
        }

        [Fact]
        public void Release_AStaleTicket_DoesNotRemoveTheClaimThatCameAfterIt()
        {
            // The second half of the same rule: a ticket is proof of ONE claim, not of the directory.
            // Releasing twice - or late - must not remove whoever has it now.
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Stage, "title repair", out var first));
            RecordingWorkset.Release(first);

            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.FullPipeline, "post-recording", out var second));
            try
            {
                RecordingWorkset.Release(first);   // the stale one, again

                Assert.True(RecordingWorkset.IsClaimed(_dir));
                Assert.Equal(RecordingWorkKind.FullPipeline, RecordingWorkset.OwnerKind(_dir));
            }
            finally { RecordingWorkset.Release(second); }
        }

        // ---- a start that does not own the directory ---------------------------

        [Fact]
        public void AStartRefusedTheClaim_PublishesNothing_AndLeavesTheOwnersRecordingIntact()
        {
            // The whole failure, driven through the REAL start sequence with BeginSession's shape:
            // claim first, fail the start when the claim is refused, and let the rollback run.
            //
            // The incumbent here is the previous session's full pipeline, exactly as in the
            // same-second collision: it holds the claim AND its media is in the directory.
            File.WriteAllText(Path.Combine(_dir, "audio.wav"), "the owner's media");
            ManifestStore.Replace(_dir, new Manifest
            {
                Mode = "audio",
                Label = "audio",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                AudioFile = "audio.wav",
                DurationSeconds = 30.0,
            });

            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.FullPipeline, "post-recording", out var owner));
            string ownerDescription = RecordingWorkset.OwnerDescription(_dir)!;
            int epochBefore = CaptureSignal.Epoch;
            bool published = false;
            RecordingClaimTicket mine = default;

            try
            {
                Assert.Throws<UsageException>(() => RecordingStartSequence.Run(
                    _dir,
                    publish: () =>
                    {
                        // BeginSession, as it now is: no claim, no capture.
                        if (!RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "capture session", out mine))
                            throw new UsageException("the recording folder is already in use");
                        CaptureSignal.CaptureStarted();
                        published = true;
                    },
                    steps: new[] { new RecordingStartStep("microphone", () => throw new InvalidOperationException("must never be reached")) },
                    startedWriters: Array.Empty<RecordingStopStep>,
                    releaseSession: () => RecordingStartSequence.Discard(_dir, mine)));

                Assert.False(published, "nothing may be published without the claim");
                Assert.False(mine.Held);
                Assert.Equal(epochBefore, CaptureSignal.Epoch);
                Assert.False(RecordingWorkset.CaptureInProgress, "the refused capture must not look live");

                // The owner is untouched: same claim, same files, same manifest.
                Assert.True(RecordingWorkset.IsClaimed(_dir), "the owner's claim must survive somebody else's failed start");
                Assert.Equal(ownerDescription, RecordingWorkset.OwnerDescription(_dir));
                Assert.True(Directory.Exists(_dir), "the rollback must not remove a directory this start did not create");
                Assert.Equal("the owner's media", File.ReadAllText(Path.Combine(_dir, "audio.wav")));
                Assert.Equal(30.0, Manifest.Load(_dir).DurationSeconds);
            }
            finally { RecordingWorkset.Release(owner); }
        }

        [Fact]
        public void Discard_AStartThatOwnedTheDirectory_StillReleasesAndRemovesIt()
        {
            // The control for the case above: when the start DID own the directory, the rollback must
            // behave exactly as issue #155 built it - release the claim and remove a directory that
            // captured nothing. Without this, "nothing was removed" above could be a Discard that
            // never removes anything.
            string mineDir = Path.Combine(_root, "2026-08-12_130001_audio");
            Directory.CreateDirectory(mineDir);
            Assert.True(RecordingWorkset.TryClaim(mineDir, RecordingWorkKind.Capture, "capture session", out var mine));

            RecordingStartSequence.Discard(mineDir, mine);

            Assert.False(RecordingWorkset.IsClaimed(mineDir));
            Assert.False(Directory.Exists(mineDir), "a directory that captured nothing is removed");
        }

        // ---- the shape of the fix, read from the compiled code ------------------

        [Fact]
        public void BeginSession_FailsOnARefusedClaim_BeforeItAnnouncesOrPublishes_InTheCompiledCode()
        {
            // The behavioral cases above run BeginSession's SHAPE, because the real one needs a sound
            // card. This reads the real method: the failure is constructed before the epoch bump and
            // before the manifest write, so there is no order in which a capture without a claim can
            // announce itself or replace another recording's manifest.
            var calls = CompiledCode.CallsIn(CompiledCode.CoreAssembly, "RecordingService::BeginSession").ToList();

            int claim = calls.IndexOf("AgentEyes.RecordingWorkset::TryClaim");
            int fail = calls.IndexOf("AgentEyes.UsageException::.ctor");
            int announce = calls.IndexOf("AgentEyes.CaptureSignal::CaptureStarted");
            int publish = calls.IndexOf("AgentEyes.ManifestStore::Replace");

            Assert.True(claim >= 0, "BeginSession must claim the directory for the capture");
            Assert.True(fail > claim,
                "BeginSession must FAIL the start when the claim is refused, not log it and carry on. Order was: "
                + string.Join(" -> ", calls));
            Assert.True(fail < announce && fail < publish,
                "the refusal must be raised before the capture is announced and before the manifest is "
                + "replaced. Order was: " + string.Join(" -> ", calls));
        }

        [Fact]
        public void TheStopReleasesThisSessionsOwnClaim_NotWhateverHoldsTheDirectory()
        {
            // Read from the source because the ticket is a local, and IL cannot say WHICH claim a
            // release names. What it can say - and what the defect was - is that Stop no longer hands
            // a directory to Release at all.
            string body = RepoSource.MethodBody(
                RepoSource.Read("src/AgentEyes.Core/RecordingService.cs"),
                "public RecordResult Stop()");

            Assert.Contains("RecordingWorkset.Release(claim)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("RecordingWorkset.Release(dir", body, StringComparison.Ordinal);
        }
    }
}
