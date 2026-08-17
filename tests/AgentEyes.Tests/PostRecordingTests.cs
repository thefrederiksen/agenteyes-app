using System;
using System.Collections.Generic;
using System.IO;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The serialization point for the recorder's PROCESS-WIDE state, of which there are now three
    /// pieces: <see cref="PostRecording.Completed"/> (a static event), the
    /// <see cref="RecordingWorkset"/> claim set, and the <see cref="PostRecordingQueue"/>.
    ///
    /// xUnit runs test classes in parallel by default, and each of those is shared mutable state a
    /// second class can perturb: a NotifyCompleted from one class was landing in another's
    /// subscriber list (an intermittent red on 2026-08-11); a capture claim taken by one class would
    /// make another's <see cref="RecordingWorkset.CaptureInProgress"/> assertion wrong; a release in
    /// one class fires the queue's retry in another (issue #154). Every class that touches any of
    /// them carries this collection name, which serializes them.
    /// </summary>
    public static class PostRecordingCollection
    {
        /// <summary>
        /// Deliberately the SAME collection as <see cref="ManifestSeamCollection"/> rather than a
        /// second one. Two collections run in PARALLEL with each other, and these classes share the
        /// same statics: a capture claim taken by a manifest-seam test is visible to
        /// <see cref="RecordingWorkset.CaptureInProgress"/> here, and its release fires the queue's
        /// retry here (issue #154). One name means one serialized group.
        /// </summary>
        public const string Name = ManifestSeamCollection.Name;
    }

    /// <summary>
    /// Issue #142: the single announcement every stop path makes when a recording has finished
    /// post-processing. AgentEyes stops recordings two ways - the window's Stop button and
    /// POST /record/stop - and twice now a post-stop step was wired into only one of them
    /// (issues #141 and #142). Anything that must happen after a recording listens here.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public class PostRecordingTests
    {
        private static string MissingDir() =>
            Path.Combine(Path.GetTempPath(), "agenteyes-postrec-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void NotifyCompleted_TellsEverySubscriber_WithTheRecordingDirectory()
        {
            string dir = MissingDir();
            var seen = new List<string>();
            void First(string d) => seen.Add("first:" + d);
            void Second(string d) => seen.Add("second:" + d);

            PostRecording.Completed += First;
            PostRecording.Completed += Second;
            try
            {
                PostRecording.NotifyCompleted(dir);
            }
            finally
            {
                PostRecording.Completed -= First;
                PostRecording.Completed -= Second;
            }

            Assert.Equal(new[] { "first:" + dir, "second:" + dir }, seen);
        }

        [Fact]
        public void NotifyCompleted_OneSubscriberThrows_TheOthersStillHear()
        {
            // The announcement runs on the background stop pass. A subscriber blowing up must not
            // take that pass down, and must not rob the repair passes of the notification.
            string dir = MissingDir();
            bool healthyRan = false;
            void Faulty(string d) => throw new InvalidOperationException("subscriber blew up");
            void Healthy(string d) => healthyRan = true;

            PostRecording.Completed += Faulty;
            PostRecording.Completed += Healthy;
            try
            {
                PostRecording.NotifyCompleted(dir);   // an escaping exception fails the test
            }
            finally
            {
                PostRecording.Completed -= Faulty;
                PostRecording.Completed -= Healthy;
            }

            Assert.True(healthyRan);
        }

        [Fact]
        public void NotifyCompleted_NoSubscribers_DoesNotThrow()
        {
            PostRecording.NotifyCompleted(MissingDir());
        }

        [Fact]
        public void Run_PostProcessingFails_StillAnnouncesCompletionAndReleasesTheClaim()
        {
            // A recording whose packaging FAILED is exactly the one that needs repairing - a lost
            // title, a poster ffmpeg could not write. The announcement must not be conditional on
            // success. A directory with no manifest.json fails on the first step.
            string dir = MissingDir();
            int notifications = 0;
            void Count(string d) { if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) notifications++; }

            PostRecording.Completed += Count;
            try
            {
                PostRecording.Run(dir);   // an escaping exception fails the test
            }
            finally
            {
                PostRecording.Completed -= Count;
            }

            Assert.Equal(1, notifications);
            Assert.False(RecordingWorkset.IsClaimed(dir));
        }

        [Fact]
        public void Run_AnotherFullPipelineOwnsTheRecording_DoesNothing()
        {
            // Another path already owns this recording's post-processing and will announce it
            // itself; running the sequence twice would race its manifest writes.
            //
            // Issue #154 narrowed this to a FULL PIPELINE owner. It used to apply to ANY claim, so a
            // title-only repair claim also made this return for good and the recording was never
            // packaged - see PostRecordingQueueTests for that case, which is now queued instead.
            string dir = MissingDir();
            int notifications = 0;
            void Count(string d) => notifications++;

            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "post-recording", out _));
            PostRecording.Completed += Count;
            try
            {
                PostRecording.Run(dir);
            }
            finally
            {
                PostRecording.Completed -= Count;
                RecordingWorkset.ReleaseForTests(dir);
            }

            Assert.Equal(0, notifications);
        }

        [Fact]
        public void Run_NoDirectory_ThrowsInsteadOfSilentlyDoingNothing()
        {
            Assert.Throws<ArgumentException>(() => PostRecording.Run(""));
        }
    }
}
