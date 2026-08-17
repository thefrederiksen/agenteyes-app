using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #142: the process-wide claim on a recording directory. It used to be a HashSet private
    /// to MainWindow, which meant the REST API stop path claimed nothing at all and the thumbnail
    /// repair pass could not see the set - two writers on one manifest.json.
    ///
    /// Issue #154 added the two things that made the exclusion real: claims carry the KIND of work
    /// that holds them (a title-only repair is not the whole post-recording sequence), and the key is
    /// the CANONICAL path, so three spellings of one directory are no longer three claims.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public class RecordingWorksetTests
    {
        private static string Dir(string name) =>
            Path.Combine(Path.GetTempPath(), "agenteyes-workset-" + name + "-" + Guid.NewGuid().ToString("N"));

        /// <summary>A directory under the process's working directory, so a genuinely relative path
        /// to it can be formed (the temp folder is usually on another drive, where a relative path
        /// does not exist at all).</summary>
        private static string DirUnderCwd(string name) =>
            Path.Combine(Environment.CurrentDirectory, "agenteyes-workset-" + name + "-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void TryClaim_FreeDirectory_ClaimsIt()
        {
            string dir = Dir("free");
            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "post-recording", out _));
                Assert.True(RecordingWorkset.IsClaimed(dir));
                Assert.Equal(RecordingWorkKind.FullPipeline, RecordingWorkset.OwnerKind(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void TryClaim_AlreadyClaimed_ReturnsFalse()
        {
            string dir = Dir("taken");
            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));

                Assert.False(RecordingWorkset.TryClaim(dir, RecordingWorkKind.FullPipeline, "post-recording", out _));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void TryClaim_DiffersOnlyByCase_IsTheSameRecording()
        {
            // Windows paths are case-insensitive; the API path and the UI path do not agree on
            // casing, and they must still be recognized as one recording.
            string dir = Dir("case");
            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir.ToLowerInvariant(), RecordingWorkKind.Stage, "first", out _));

                Assert.False(RecordingWorkset.TryClaim(dir.ToUpperInvariant(), RecordingWorkKind.Stage, "second", out _));
                Assert.True(RecordingWorkset.IsClaimed(dir.ToUpperInvariant()));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        // ---- issue #154 AC3: one directory is one claim, however it is spelled ----

        [Fact]
        public void TryClaim_TrailingSeparatorVariant_IsTheSameRecording()
        {
            // C:\x\y and C:\x\y\ used to take two independent claims, so the mutual exclusion
            // between two writers on the same recording silently did not apply.
            string dir = Dir("trailing");
            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "first", out _));

                Assert.False(RecordingWorkset.TryClaim(dir + Path.DirectorySeparatorChar, RecordingWorkKind.Stage, "second", out _));
                Assert.False(RecordingWorkset.TryClaim(dir + Path.AltDirectorySeparatorChar, RecordingWorkKind.Stage, "third", out _));
                Assert.True(RecordingWorkset.IsClaimed(dir + Path.DirectorySeparatorChar));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void TryClaim_RelativePathToTheSameDirectory_IsTheSameRecording()
        {
            // The CLI runs with its own working directory and can be handed a relative path; the app
            // always has an absolute one. They are the same recording and must be one claim.
            string dir = DirUnderCwd("relative");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, dir);
            Assert.False(Path.IsPathRooted(relative), "the test needs a genuinely relative path to be meaningful");

            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "absolute", out _));

                Assert.False(RecordingWorkset.TryClaim(relative, RecordingWorkKind.FullPipeline, "relative", out _));
                Assert.True(RecordingWorkset.IsClaimed(relative));
                Assert.Equal(RecordingWorkKind.Stage, RecordingWorkset.OwnerKind(relative));
            }
            finally { RecordingWorkset.ReleaseForTests(relative); }
        }

        [Fact]
        public void Release_ThroughADifferentSpelling_ReallyReleases()
        {
            // The mirror of the claim: a release that normalized differently from its claim would
            // leave a recording claimed forever - nothing could ever process it again.
            string dir = Dir("release-spelling");
            Assert.True(RecordingWorkset.TryClaim(dir + Path.DirectorySeparatorChar, RecordingWorkKind.Stage, "claimed with a trailing separator", out _));

            RecordingWorkset.ReleaseForTests(dir);

            Assert.False(RecordingWorkset.IsClaimed(dir));
            Assert.False(RecordingWorkset.IsClaimed(dir + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void Key_CollapsesDotSegmentsAndTrailingSeparators()
        {
            string dir = Dir("key");
            string awkward = Path.Combine(dir, "..", Path.GetFileName(dir)) + Path.DirectorySeparatorChar;

            Assert.Equal(RecordingWorkset.Key(dir), RecordingWorkset.Key(awkward));
        }

        [Fact]
        public void Key_ASpellingOnlyTheFilesystemCanResolve_IsADIFFERENTKey_TheDocumentedLimit()
        {
            // The edge of the normalizer, pinned rather than left to be discovered (issue #154,
            // independent review, non-blocking 1). Key is LEXICAL identity plus case-insensitive
            // comparison: it folds case, separators, dot segments and relative paths, and it does NOT
            // fold spellings that only the filesystem knows are the same object - a device-syntax
            // path here, and equally 8.3 short names, junction/symlink aliases and a mapped drive
            // versus its UNC share. Those take independent claims.
            //
            // Accepted and out of scope: every production caller derives its directory from
            // RecordingPaths.Root or from a scan of it, so they all spell it the same way, and true
            // filesystem identity means opening a handle per directory on a path that scans walk
            // hundreds of times. If that ever changes, this test is the one that has to change with
            // it - which is the point of writing the limit down as an assertion.
            string dir = Dir("device-syntax");
            string device = @"\\?\" + Path.GetFullPath(dir);

            Assert.NotEqual(RecordingWorkset.Key(dir), RecordingWorkset.Key(device));
        }

        [Fact]
        public void ReleaseAndTheReadPaths_MalformedPath_ReportItInsteadOfThrowing()
        {
            // Release is called from finally blocks all over the app - the stop sequence, the start
            // rollback, every repair loop - and before the key was normalized it could not fail at
            // all. A throw here would REPLACE the exception the caller is already carrying.
            const string malformed = "a\0b";

            RecordingWorkset.ReleaseForTests(malformed);   // an escaping exception fails the test

            Assert.False(RecordingWorkset.IsClaimed(malformed));
            Assert.Null(RecordingWorkset.OwnerKind(malformed));
            Assert.Null(RecordingWorkset.OwnerDescription(malformed));

            // A CLAIM is different: it must fail loudly rather than pretend to own nothing.
            Assert.ThrowsAny<ArgumentException>(
                () => RecordingWorkset.TryClaim(malformed, RecordingWorkKind.Stage, "malformed", out _));
        }

        [Fact]
        public void CaptureInProgress_IsTrueOnlyWhileACaptureSessionHoldsARecording()
        {
            // The queued-retry drain reads this on paths that have no IsRecording delegate.
            string capture = Dir("capturing");
            string stage = Dir("repairing");

            Assert.False(RecordingWorkset.CaptureInProgress);

            Assert.True(RecordingWorkset.TryClaim(stage, RecordingWorkKind.Stage, "title repair", out _));
            try
            {
                Assert.False(RecordingWorkset.CaptureInProgress, "a repair stage is not a capture");

                Assert.True(RecordingWorkset.TryClaim(capture, RecordingWorkKind.Capture, "capture session", out _));
                try
                {
                    Assert.True(RecordingWorkset.CaptureInProgress);
                }
                finally { RecordingWorkset.ReleaseForTests(capture); }

                Assert.False(RecordingWorkset.CaptureInProgress);
            }
            finally { RecordingWorkset.ReleaseForTests(stage); }
        }

        [Fact]
        public void Key_NoDirectory_Throws()
        {
            // No fallback: a claim silently taken under an invented key is the failure this removes.
            Assert.Throws<ArgumentException>(() => RecordingWorkset.Key(""));
            Assert.Throws<ArgumentException>(() => RecordingWorkset.Key("   "));
        }

        // ---- issue #154: the kind of work that holds a claim ----------------

        [Fact]
        public void OwnerKind_ReportsWhatIsHoldingTheRecording()
        {
            // The whole point: a refused caller has to be able to tell "the whole job is already
            // being done" from "one stage is being done and mine still has to happen".
            string dir = Dir("ownerkind");
            try
            {
                Assert.Null(RecordingWorkset.OwnerKind(dir));

                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "thumbnail repair", out _));
                Assert.Equal(RecordingWorkKind.Stage, RecordingWorkset.OwnerKind(dir));
                Assert.Contains("thumbnail repair", RecordingWorkset.OwnerDescription(dir));
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }

            Assert.Null(RecordingWorkset.OwnerKind(dir));
            Assert.Null(RecordingWorkset.OwnerDescription(dir));
        }

        [Fact]
        public void Released_AnnouncesTheNormalizedKey()
        {
            // The queued-retry path (PostRecordingQueue) keys on the normalized directory, so the
            // announcement has to carry that spelling and not whatever the releaser typed.
            string dir = Dir("released-event");
            string? announced = null;
            void Watch(string key) { if (string.Equals(key, RecordingWorkset.Key(dir), StringComparison.OrdinalIgnoreCase)) announced = key; }

            RecordingWorkset.Released += Watch;
            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
                RecordingWorkset.ReleaseForTests(dir + Path.DirectorySeparatorChar);
            }
            finally { RecordingWorkset.Released -= Watch; }

            Assert.Equal(RecordingWorkset.Key(dir), announced);
        }

        [Fact]
        public void Released_OneSubscriberThrows_TheReleaseStillCompletes()
        {
            // Release runs inside somebody's finally block. A listener blowing up must not leave the
            // recording claimed forever.
            string dir = Dir("released-throws");
            void Faulty(string key) => throw new InvalidOperationException("subscriber blew up");

            RecordingWorkset.Released += Faulty;
            try
            {
                Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "title repair", out _));
                RecordingWorkset.ReleaseForTests(dir);   // an escaping exception fails the test
            }
            finally { RecordingWorkset.Released -= Faulty; }

            Assert.False(RecordingWorkset.IsClaimed(dir));
        }

        [Fact]
        public void Release_FreesItForTheNextClaim()
        {
            string dir = Dir("release");
            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "first", out _));

            RecordingWorkset.ReleaseForTests(dir);

            Assert.False(RecordingWorkset.IsClaimed(dir));
            Assert.True(RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "second", out _));
            RecordingWorkset.ReleaseForTests(dir);
        }

        [Fact]
        public void Release_NotClaimed_DoesNotThrow()
        {
            RecordingWorkset.ReleaseForTests(Dir("never-claimed"));   // a finally that never claimed
        }

        [Fact]
        public void TryClaim_ConcurrentCallers_ExactlyOneWins()
        {
            // The claim is taken from the UI thread, a REST worker thread and a repair timer
            // thread, so it must be atomic rather than a read-then-write.
            string dir = Dir("race");
            int winners = 0;
            try
            {
                Parallel.For(0, 64, i =>
                {
                    if (RecordingWorkset.TryClaim(dir, RecordingWorkKind.Stage, "racer " + i, out _)) Interlocked.Increment(ref winners);
                });

                Assert.Equal(1, winners);
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void TryClaim_ConcurrentCallersOnDifferentSpellings_ExactlyOneWins()
        {
            // Same race, spelled three ways - the case that used to hand out three claims for one
            // recording (issue #154).
            string dir = Dir("race-spellings");
            int winners = 0;
            try
            {
                Parallel.For(0, 64, i =>
                {
                    string spelling = (i % 3) switch
                    {
                        0 => dir,
                        1 => dir + Path.DirectorySeparatorChar,
                        _ => Path.Combine(dir, "..", Path.GetFileName(dir)),
                    };
                    if (RecordingWorkset.TryClaim(spelling, RecordingWorkKind.Stage, "racer " + i, out _)) Interlocked.Increment(ref winners);
                });

                Assert.Equal(1, winners);
            }
            finally { RecordingWorkset.ReleaseForTests(dir); }
        }

        [Fact]
        public void IsClaimed_EmptyPath_False()
        {
            Assert.False(RecordingWorkset.IsClaimed(""));
            Assert.False(RecordingWorkset.TryClaim("", RecordingWorkKind.Stage, "nothing", out _));
            Assert.Null(RecordingWorkset.OwnerKind(""));
        }
    }
}
