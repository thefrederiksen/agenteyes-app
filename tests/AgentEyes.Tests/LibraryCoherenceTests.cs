using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Xunit;
using AgentEyes.App;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #3 - the Library's coherence model: the ordering between the reloads that are in flight
    /// and the changes the user makes while they are.
    ///
    /// WHAT WENT WRONG. Reading the library is slow, so it happens on a worker; several reads overlap
    /// by design (the repair service asks for one after each of its stages, an import asks for one,
    /// the window asks for one at startup). Each one used to CLEAR the collection and reinstall its
    /// own answer, and the live routes - screenshot insert, saved-recording insert, rename,
    /// RefreshNaming, delete - mutated the same collection with no ordering against them. Six
    /// distinct failure modes came out of that, and the user-visible one is the defect this
    /// repository chased for weeks: a recording that exists on disk missing from the Library.
    ///
    /// WHAT A PREVIOUS ATTEMPT GOT WRONG. "Latest generation wins, drop the rest" was written on the
    /// archived repository's PR #179 and rejected twice by the independent review gate, because a
    /// snapshot that is DROPPED takes with it everything only it knew about. That is failure mode 2
    /// below, and it is why every test here checks what SURVIVED as well as what did not win.
    ///
    /// HOW THESE TESTS ARE WRITTEN. The interleavings are FORCED, not raced. Every test drives the
    /// model's own vocabulary - begin a snapshot, do something live, land the snapshot - in the exact
    /// order the failure needs, on one thread, with no timing involved. A racing test that "usually"
    /// reproduces the bug would certify nothing on the run where it did not.
    ///
    /// Each test states the three arms the CenCon method requires (DEVELOPMENT_METHOD.md 6c): the
    /// expected result, the result that is the defect, and what an empty or absent answer would mean.
    /// The structural guards at the bottom are all demonstrated FIRING against a compiled defect.
    /// </summary>
    public sealed class LibraryCoherenceTests : IDisposable
    {
        private readonly string _root;

        public LibraryCoherenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-coherence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>A recording folder with a manifest, so RecentItem.From builds a real card for it.
        /// <paramref name="displayName"/> is the user-visible title a rename writes.</summary>
        private string Recording(string leaf, string createdUtc, string? displayName = null)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(dir);

            var manifest = new Dictionary<string, object>
            {
                ["Tool"] = "AgentEyes",
                ["Mode"] = "video",
                ["Label"] = leaf,
                ["DurationSeconds"] = 12.5,
                ["CreatedUtc"] = createdUtc,
            };
            if (displayName != null) manifest["DisplayName"] = displayName;

            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return dir;
        }

        /// <summary>A snapshot list, exactly as the loader hands one to the model.</summary>
        private static List<RecentItem> Snapshot(params string[] dirs) =>
            dirs.Select(RecentItem.From).ToList();

        private static string[] Dirs(LibraryCoherence library) =>
            library.Rows.Select(row => Path.GetFileName(row.Dir)).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        /// <summary>A library already holding these recordings, loaded the way the window loads it.</summary>
        private static LibraryCoherence Loaded(params string[] dirs)
        {
            var library = new LibraryCoherence();
            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(dirs));
            return library;
        }

        // ---- failure mode 1: an older reload overwrites newer state -----------

        /// <summary>
        /// Criterion 1. Reload A is still decoding thumbnails when a recording is saved and reload B
        /// publishes the fuller list; A then finishes LAST carrying the list it read before the save.
        ///
        /// PASS: the saved recording is still there. DEFECT: A's stale list wins and it disappears -
        /// the exact user-visible symptom. EMPTY (no rows at all) would mean the fixture never
        /// loaded and the test proved nothing, which is why the row count is asserted too.
        /// </summary>
        [Fact]
        public void AnOlderReload_LandingLast_DoesNotInstallItsStaleSnapshot()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            string two = Recording("two_video", "2026-08-02T10:00:00.0000000Z");
            var library = Loaded(one, two);

            long a = library.BeginSnapshot();          // A starts reading: it will see one + two
            string saved = Recording("saved_video", "2026-08-03T10:00:00.0000000Z");
            library.Insert(saved);                     // the recording the user just made
            long b = library.BeginSnapshot();          // B starts reading: it sees all three

            library.ApplySnapshot(b, Snapshot(one, two, saved));
            library.ApplySnapshot(a, Snapshot(one, two));   // A finishes LAST, with the stale list

            Assert.Equal(new[] { "one_video", "saved_video", "two_video" }, Dirs(library));
        }

        /// <summary>
        /// The same ordering without the live insert: B reads a recording that A could not have seen,
        /// A lands last. A's silence about it is not evidence that it is gone - it is evidence about
        /// an earlier moment.
        /// </summary>
        [Fact]
        public void AnOlderReload_LandingLast_DoesNotRemoveARecordingOnlyTheNewerReloadSaw()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(one);

            long a = library.BeginSnapshot();
            string late = Recording("late_video", "2026-08-05T10:00:00.0000000Z");
            long b = library.BeginSnapshot();

            library.ApplySnapshot(b, Snapshot(one, late));
            library.ApplySnapshot(a, Snapshot(one));

            Assert.Equal(new[] { "late_video", "one_video" }, Dirs(library));
        }

        // ---- failure mode 2: a dropped snapshot loses its unique content ------

        /// <summary>
        /// Criterion 2, and the reason "newest wins, drop the rest" was rejected twice. An import
        /// writes recording I and triggers reload A; a screenshot S is saved while A is in flight.
        /// A's snapshot is the ONLY evidence that I exists; the live collection is the only evidence
        /// that S exists.
        ///
        /// PASS: both are in the library. DEFECT (the rejected design): A is invalidated by the live
        /// insert and dropped, so the Library shows S and never I. The opposite defect (A wins
        /// wholesale) loses S. Either single-winner answer fails this test, which is the point - the
        /// snapshot is merged per recording rather than accepted or discarded as a whole.
        /// </summary>
        [Fact]
        public void AnInsertDuringAnInFlightReload_LosesNeitherTheInsertedRowNorTheSnapshotOnlyRecording()
        {
            var library = new LibraryCoherence();
            string imported = Recording("imported_video", "2026-08-01T10:00:00.0000000Z");

            long a = library.BeginSnapshot();          // the import's reload: it sees the import
            string shot = Recording("shot_video", "2026-08-02T10:00:00.0000000Z");
            library.Insert(shot);                      // the screenshot lands mid-flight

            library.ApplySnapshot(a, Snapshot(imported));

            Assert.Equal(new[] { "imported_video", "shot_video" }, Dirs(library));
        }

        /// <summary>
        /// The same loss in its other clothes: what the in-flight snapshot uniquely carried is a
        /// repaired TITLE rather than a whole recording. Dropping the snapshot loses the repair.
        /// </summary>
        [Fact]
        public void AnInsertDuringAnInFlightReload_DoesNotLoseTheRepairedTitleThatOnlyTheSnapshotCarried()
        {
            string repaired = Recording("repaired_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(repaired);
            Assert.Equal("Monitor 0", library.Rows[0].Title);   // the pre-repair name, for contrast

            long a = library.BeginSnapshot();
            Recording("repaired_video", "2026-08-01T10:00:00.0000000Z", displayName: "Quarterly review");
            library.Insert(Recording("shot_video", "2026-08-02T10:00:00.0000000Z"));

            library.ApplySnapshot(a, Snapshot(repaired));

            // Both halves, in one test on purpose: dropping the snapshot loses the repaired title,
            // and accepting it wholesale loses the screenshot. Only a per-recording merge keeps both.
            Assert.Equal("Quarterly review", library.Find(repaired)!.Title);
            Assert.Equal(new[] { "repaired_video", "shot_video" }, Dirs(library));
        }

        // ---- failure mode 3: a failed or hung reload blanks or blocks ---------

        /// <summary>
        /// Criterion 3, first half. The loader used to catch its worker's exception, leave its list
        /// empty and install THAT - a blank library manufactured by a broken instrument.
        ///
        /// PASS: the rows are untouched. DEFECT: the library is blanked or truncated. An empty
        /// library here is the defect, not a clean run.
        /// </summary>
        [Fact]
        public void AReloadWhoseWorkerThrows_DoesNotBlankOrTruncateTheLibrary()
        {
            var library = Loaded(
                Recording("one_video", "2026-08-01T10:00:00.0000000Z"),
                Recording("two_video", "2026-08-02T10:00:00.0000000Z"));

            long failing = library.BeginSnapshot();
            library.AbandonSnapshot(failing, new IOException("the recordings folder is unreachable"));

            Assert.Equal(new[] { "one_video", "two_video" }, Dirs(library));
        }

        /// <summary>
        /// Criterion 3, second half - the half a generation gate gets wrong. A NEWER reload fails
        /// while an older, successful one is still in flight. Under "only the newest generation may
        /// land" the successful one is now blocked forever.
        ///
        /// PASS: the successful reload lands. DEFECT: the library is frozen at its old contents
        /// because a failed generation is still the newest.
        /// </summary>
        [Fact]
        public void AFailedReload_DoesNotBlockAnOlderSuccessfulReloadFromLanding()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(one);

            long good = library.BeginSnapshot();       // started first
            long doomed = library.BeginSnapshot();     // started later - the "newest generation"

            library.AbandonSnapshot(doomed, new IOException("the recordings folder is unreachable"));
            string fresh = Recording("fresh_video", "2026-08-04T10:00:00.0000000Z");
            library.ApplySnapshot(good, Snapshot(one, fresh));

            Assert.Equal(new[] { "fresh_video", "one_video" }, Dirs(library));
        }

        /// <summary>
        /// A reload that HANGS - begun and never settled - blocks nothing either. There is no wait
        /// anywhere in the model, so the hung read is simply a read that never lands.
        /// </summary>
        [Fact]
        public void AHungReload_NeverSettled_DoesNotStopLaterReloadsFromLanding()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(one);

            library.BeginSnapshot();                   // begun, and deliberately never settled

            string later = Recording("later_video", "2026-08-06T10:00:00.0000000Z");
            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(one, later));

            Assert.Equal(new[] { "later_video", "one_video" }, Dirs(library));
            Assert.Equal(1, library.InFlightSnapshots);   // the hung one is still counted, not lost
        }

        /// <summary>A successful read that finds nothing IS an empty library, and must still land.
        /// The model separates "the worker failed" from "the worker found no recordings" - failing to
        /// would turn a real deletion of everything into a library that never updates.</summary>
        [Fact]
        public void ASuccessfulReloadThatFoundNothing_EmptiesTheLibrary()
        {
            var library = Loaded(Recording("one_video", "2026-08-01T10:00:00.0000000Z"));

            library.ApplySnapshot(library.BeginSnapshot(), new List<RecentItem>());

            Assert.Empty(library.Rows);
        }

        // ---- failure mode 4: metadata changes are not ordered -----------------

        /// <summary>
        /// Criterion 4. The user renames a recording while a reload that read the OLD manifest is
        /// still in flight.
        ///
        /// PASS: the new name survives. DEFECT: the reload lands and the card silently reverts to the
        /// name the user just changed.
        /// </summary>
        [Fact]
        public void ARenameDuringAnInFlightReload_IsNotRevertedByThatReload()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var stale = Snapshot(dir);                 // what the reload read: the pre-rename manifest

            long a = library.BeginSnapshot();
            library.Rename(library.Rows[0], "Board deck walkthrough");

            library.ApplySnapshot(a, stale);

            Assert.Equal("Board deck walkthrough", library.Find(dir)!.Title);
        }

        /// <summary>
        /// The same ordering for the OTHER metadata route: a repair pass filled the generated title
        /// in and RefreshNaming read it, while a reload holding the pre-repair card was in flight.
        /// </summary>
        [Fact]
        public void ARefreshDuringAnInFlightReload_IsNotRevertedByThatReload()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var stale = Snapshot(dir);

            long a = library.BeginSnapshot();
            Recording("one_video", "2026-08-01T10:00:00.0000000Z", displayName: "Repaired title");
            library.Refresh(library.Rows[0]);
            Assert.Equal("Repaired title", library.Rows[0].Title);

            library.ApplySnapshot(a, stale);

            Assert.Equal("Repaired title", library.Find(dir)!.Title);
        }

        /// <summary>A rename is not immune forever - a reload that started AFTER it reads the
        /// manifest the rename wrote, and that reading is the newer evidence. Without this the guard
        /// above would be satisfied by a row nothing may ever update again.</summary>
        [Fact]
        public void ALaterReload_StillUpdatesARenamedRow()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            library.Rename(library.Rows[0], "Board deck walkthrough");

            Recording("one_video", "2026-08-01T10:00:00.0000000Z", displayName: "Renamed on disk");
            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(dir));

            Assert.Equal("Renamed on disk", library.Find(dir)!.Title);
        }

        // ---- failure mode 5: rows held across an await --------------------

        /// <summary>
        /// Criterion 5. The stop path and PackageDirAsync capture a row, await, then update it. A
        /// reload completing during that await used to replace every row object, so the update landed
        /// on an object that was no longer in the collection and the visible card kept the stale name.
        ///
        /// PASS: the held row IS still the row in the collection, and the update is visible on it.
        /// DEFECT: a reload swapped in a new object and the update goes nowhere the user can see.
        /// </summary>
        [Fact]
        public void ARowHeldAcrossAnAwait_IsStillTheLibrarysRowAfterAReloadCompleted()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var held = library.Rows[0];                // captured before the "await"

            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(dir));   // a reload lands mid-await

            Assert.Same(held, library.Rows[0]);

            Recording("one_video", "2026-08-01T10:00:00.0000000Z", displayName: "Packaged title");
            library.Refresh(held);                     // the work finishes and refreshes its row

            Assert.Equal("Packaged title", library.Rows[0].Title);
        }

        /// <summary>
        /// The harder half: the held row really IS detached - the recording was deleted and re-added
        /// while the work ran, so the collection holds a different object for the same directory.
        /// Writing on the held object would leave the visible card stale.
        ///
        /// PASS: the refresh lands on the row that is in the library. DEFECT: the visible row keeps
        /// the old title while the detached copy carries the new one.
        /// </summary>
        [Fact]
        public void ARowHeldAcrossAnAwait_UpdatesTheLibrarysRow_WhenTheHeldOneIsDetached()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var held = library.Rows[0];

            library.Delete(new[] { held });            // gone...
            library.Insert(dir);                       // ...and back, as a different object
            Assert.NotSame(held, library.Rows[0]);

            Recording("one_video", "2026-08-01T10:00:00.0000000Z", displayName: "Packaged title");
            library.Refresh(held);

            Assert.Equal("Packaged title", library.Rows[0].Title);
            Assert.Equal("Monitor 0", held.Title);     // the detached copy was NOT what got updated
        }

        /// <summary>Live progress text takes the same route, for the same reason: the stop path holds
        /// its row across every stage of the post-recording sequence.</summary>
        [Fact]
        public void StatusOnAHeldRow_LandsOnTheLibrarysRow_WhenTheHeldOneIsDetached()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var held = library.Rows[0];

            library.Delete(new[] { held });
            library.Insert(dir);

            library.SetStatus(held, "Transcribing...");

            Assert.Equal("Transcribing...", library.Rows[0].Status);
            Assert.Equal("", held.Status);
        }

        /// <summary>A row for a recording that was deleted while the work ran has nowhere to land, and
        /// the model says so instead of resurrecting it.</summary>
        [Fact]
        public void RefreshingARowForADeletedRecording_DoesNotPutItBack()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var held = library.Rows[0];

            library.Delete(new[] { held });
            Assert.False(library.Refresh(held));

            Assert.Empty(library.Rows);
        }

        /// <summary>A reload must not blank a live status either - it is what the running app is
        /// writing on the row, and no manifest on disk knows about it.</summary>
        [Fact]
        public void AReloadLandingOnARow_DoesNotWipeItsLiveStatus()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            library.SetStatus(library.Rows[0], "Transcribing...");

            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(dir));

            Assert.Equal("Transcribing...", library.Rows[0].Status);
        }

        // ---- failure mode 6: a delete undone by a reload ----------------------

        /// <summary>
        /// Criterion 6. The rows are dropped immediately so the list feels instant, and the (often
        /// large) directories are deleted afterwards off the UI thread. A reload that started in that
        /// gap still sees the manifests.
        ///
        /// PASS: the deleted recording stays gone. DEFECT: the reload re-adds it permanently, and the
        /// user watches a recording they deleted come back.
        /// </summary>
        [Fact]
        public void AReloadThatStartedBeforeTheDirectoryWasRemoved_DoesNotResurrectTheDeletedRow()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            string doomed = Recording("doomed_video", "2026-08-02T10:00:00.0000000Z");
            var library = Loaded(one, doomed);

            long a = library.BeginSnapshot();          // A reads the disk: both manifests are there
            library.Delete(new[] { library.Find(doomed)! });   // rows go now; the folder goes later

            library.ApplySnapshot(a, Snapshot(one, doomed));

            Assert.Equal(new[] { "one_video" }, Dirs(library));
        }

        /// <summary>
        /// The tombstone is bounded, not permanent. A reload that started AFTER the delete and STILL
        /// finds the recording is reporting a delete that failed - and the library has to show what
        /// is actually on disk. Without this the guard above would be satisfied by a directory that
        /// can never be shown again.
        /// </summary>
        [Fact]
        public void AReloadThatStartedAfterTheDelete_StillShowsARecordingWhoseDeletionFailed()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            string stubborn = Recording("stubborn_video", "2026-08-02T10:00:00.0000000Z");
            var library = Loaded(one, stubborn);

            library.Delete(new[] { library.Find(stubborn)! });
            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(one, stubborn));

            Assert.Equal(new[] { "one_video", "stubborn_video" }, Dirs(library));
        }

        // ---- the collection's gate: criterion 7, demonstrated ------------------

        /// <summary>
        /// The gate FIRING, on every spelling of a mutation there is. This is the negative control
        /// for criterion 7 and it is a DEMONSTRATION, not an assertion about source text: each line
        /// below is a compiled direct mutation of the library's rows from outside the model, and each
        /// one is observed to throw.
        ///
        /// The previous attempt's guard recognized only Insert/Remove/Clear/Add, and a direct
        /// RemoveAt(0) produced zero matcher hits. Collection&lt;T&gt; routes ALL of these through
        /// five protected virtual methods, so the spelling cannot be the thing that decides.
        /// </summary>
        [Fact]
        public void EveryDirectMutationOfTheLibrarysRows_IsRefused()
        {
            var library = Loaded(
                Recording("one_video", "2026-08-01T10:00:00.0000000Z"),
                Recording("two_video", "2026-08-02T10:00:00.0000000Z"));
            var rows = library.Rows;
            var row = RecentItem.From(Recording("three_video", "2026-08-03T10:00:00.0000000Z"));

            var refused = new Dictionary<string, Action>
            {
                ["Add"] = () => rows.Add(row),
                ["Insert"] = () => rows.Insert(0, row),
                ["Remove"] = () => rows.Remove(rows[0]),
                ["RemoveAt"] = () => rows.RemoveAt(0),
                ["Move"] = () => rows.Move(0, 1),
                ["indexer assignment"] = () => rows[0] = row,
                ["Clear"] = () => rows.Clear(),
                ["IList.Add through a cast"] = () => ((System.Collections.IList)rows).Add(row),
                ["IList.RemoveAt through a cast"] = () => ((System.Collections.IList)rows).RemoveAt(0),
                ["IList<RecentItem>.Insert through a cast"] = () => ((IList<RecentItem>)rows).Insert(0, row),
                ["ICollection<RecentItem>.Clear through a cast"] = () => ((ICollection<RecentItem>)rows).Clear(),
            };

            foreach (var (spelling, mutate) in refused)
            {
                var thrown = Record.Exception(mutate);

                Assert.True(thrown is InvalidOperationException,
                    $"Mutating the library's rows by '{spelling}' from outside the coherence model was "
                    + $"NOT refused (got {thrown?.GetType().Name ?? "no exception"}). Every spelling of "
                    + "a mutation has to be refused, or the model is advisory.");
                Assert.Contains("coherence model", thrown!.Message, StringComparison.Ordinal);
            }

            // ...and the library is exactly as it was: not one of them got through.
            Assert.Equal(new[] { "one_video", "two_video" }, Dirs(library));
        }

        /// <summary>
        /// The NARROWNESS control for the same gate. It must refuse the mutation, not the read - a
        /// gate that made the collection unusable would be removed within a week. Every read the
        /// window actually does (count, enumerate, index, contains) still works.
        /// </summary>
        [Fact]
        public void TheGate_RefusesMutationsOnly_AndLeavesEveryReadWorking()
        {
            string one = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(one, Recording("two_video", "2026-08-02T10:00:00.0000000Z"));
            var rows = library.Rows;

            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Count());
            Assert.Contains(rows, r => string.Equals(r.Dir, one, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(rows[0]);
            Assert.NotNull(library.Find(one));
        }

        /// <summary>
        /// The model's own state and the collection are one piece of state, and it belongs to the
        /// thread that created them. Demonstrated, not documented: the calls are made from another
        /// thread and observed to throw.
        /// </summary>
        [Fact]
        public void TheModelAndItsRows_RefuseEveryCallFromAnotherThread()
        {
            var library = Loaded(Recording("one_video", "2026-08-01T10:00:00.0000000Z"));
            var row = library.Rows[0];

            var offThread = new Dictionary<string, Action>
            {
                ["BeginSnapshot"] = () => library.BeginSnapshot(),
                ["ApplySnapshot"] = () => library.ApplySnapshot(1, new List<RecentItem>()),
                ["Insert"] = () => library.Insert(row.Dir),
                ["Delete"] = () => library.Delete(new[] { row }),
                ["Rename"] = () => library.Rename(row, "x"),
                ["Refresh"] = () => library.Refresh(row),
                ["SetStatus"] = () => library.SetStatus(row, "x"),
                ["Find"] = () => library.Find(row.Dir),
                ["a direct mutation of the rows"] = () => library.Rows.Clear(),
            };

            foreach (var (route, call) in offThread)
            {
                Exception? thrown = null;
                var thread = new Thread(() => thrown = Record.Exception(call));
                thread.Start();
                thread.Join();

                Assert.True(thrown is InvalidOperationException,
                    $"'{route}' did not refuse a call from another thread (got "
                    + $"{thrown?.GetType().Name ?? "no exception"}).");
                Assert.Contains("thread", thrown!.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>A snapshot may be settled exactly once, and only if it was begun. A caller that
        /// lost track of its own read is a bug in that caller, and it is reported rather than
        /// absorbed - an unknown epoch would otherwise be merged as if it were the very oldest.</summary>
        [Fact]
        public void ASnapshotEpoch_IsRefused_WhenItWasNeverBegunOrHasAlreadySettled()
        {
            var library = Loaded(Recording("one_video", "2026-08-01T10:00:00.0000000Z"));

            Assert.Throws<InvalidOperationException>(
                () => library.ApplySnapshot(9999, new List<RecentItem>()));

            long epoch = library.BeginSnapshot();
            library.ApplySnapshot(epoch, new List<RecentItem>());
            Assert.Throws<InvalidOperationException>(
                () => library.ApplySnapshot(epoch, new List<RecentItem>()));
            Assert.Throws<InvalidOperationException>(
                () => library.AbandonSnapshot(epoch, new IOException("x")));
        }

        /// <summary>A snapshot that lists one recording twice is a broken snapshot, not something to
        /// merge - the recording directory is the library's identity.</summary>
        [Fact]
        public void ASnapshotListingOneRecordingTwice_IsRefused()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = new LibraryCoherence();

            Assert.Throws<ArgumentException>(
                () => library.ApplySnapshot(library.BeginSnapshot(), Snapshot(dir, dir)));
        }

        // ---- notification behaviour (issue #178's O(n squared) fix, kept) ------

        /// <summary>A single live insert reports itself precisely rather than resetting the list, so
        /// the user's selection survives saving a screenshot.</summary>
        [Fact]
        public void ASingleLiveInsert_RaisesAnAddRatherThanAReset()
        {
            var library = Loaded(Recording("one_video", "2026-08-01T10:00:00.0000000Z"));
            var events = new List<NotifyCollectionChangedAction>();
            library.Rows.CollectionChanged += (_, e) => events.Add(e.Action);

            library.Insert(Recording("two_video", "2026-08-02T10:00:00.0000000Z"));

            Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, events);
        }

        /// <summary>A reload that changed nothing raises NOTHING - the common case, and the one that
        /// used to reset the list (and the selection) on every repair-service tick.</summary>
        [Fact]
        public void AReloadThatChangedNothing_RaisesNoCollectionEventAtAll()
        {
            string dir = Recording("one_video", "2026-08-01T10:00:00.0000000Z");
            var library = Loaded(dir);
            var events = new List<NotifyCollectionChangedAction>();
            library.Rows.CollectionChanged += (_, e) => events.Add(e.Action);

            library.ApplySnapshot(library.BeginSnapshot(), Snapshot(dir));

            Assert.Empty(events);
        }

        // ---- criterion 7 + 8: the structural guards ---------------------------

        /// <summary>The library's rows, wherever they are declared.</summary>
        private static bool IsTheRowsField(string field) =>
            field.EndsWith("::_rows", StringComparison.Ordinal);

        /// <summary>Every method that TOUCHES the library's rows field but is not part of the
        /// coherence model that owns it.</summary>
        private static IReadOnlyList<CompiledCode.FieldSite> RowsTouchedOutsideTheModel(string assembly, string ns)
        {
            var sites = CompiledCode.FieldAccesses(assembly, IsTheRowsField);

            if (sites.Count == 0)
                throw new InvalidOperationException(
                    $"No method in {Path.GetFileName(assembly)} touches a '_rows' field, so this guard "
                    + "would be scanning nothing and passing on absence.");

            return sites
                .Where(site => !site.Method.StartsWith(ns + "LibraryCoherence::", StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>
        /// Criterion 7's compiled half: the library's rows are reachable only from inside the model.
        ///
        /// Its LIMIT, stated rather than glossed: this sees who names the FIELD. A method handed the
        /// collection as an argument, or fetched through the public Rows property, does not name it
        /// and is invisible here. That hole is the one the RUNTIME gate closes - and
        /// <see cref="EveryDirectMutationOfTheLibrarysRows_IsRefused"/> demonstrates it closing on a
        /// caller doing exactly that, through the property, in eleven different spellings. Neither
        /// guard is sufficient alone; between them a mutation has nowhere to hide.
        /// </summary>
        [Fact]
        public void NoMethodOutsideTheCoherenceModel_TouchesTheLibrarysRows()
        {
            var offenders = RowsTouchedOutsideTheModel(CompiledCode.AppAssembly, "AgentEyes.App.");

            Assert.True(offenders.Count == 0,
                "A method outside LibraryCoherence reaches the library's rows directly, so it changes "
                + "them with no ordering against the reloads in flight (issue #3):" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders.Select(o => $"  {o.Method} -> {o.Field}")));
        }

        /// <summary>
        /// The negative control: the same scan, pointed at compiled bypasses in the test assembly.
        /// LibraryDefectDecoys writes the mutations the previous guard could not see - a direct
        /// RemoveAt, a Move, an indexer assignment, and one hidden behind a wrapper method - and the
        /// scan must report every one of them.
        /// </summary>
        [Fact]
        public void TheRowsScan_ReportsEveryBypassOfTheModel()
        {
            var reported = RowsTouchedOutsideTheModel(
                CompiledCode.TestAssembly, "AgentEyes.Tests.LibraryDefects.");

            foreach (string bypass in new[]
                     {
                         "AgentEyes.Tests.LibraryDefects.LibraryBypass::RemoveAtDirectly",
                         "AgentEyes.Tests.LibraryDefects.LibraryBypass::MoveDirectly",
                         "AgentEyes.Tests.LibraryDefects.LibraryBypass::AssignThroughTheIndexer",
                         "AgentEyes.Tests.LibraryDefects.LibraryBypass::ThroughAWrapper",
                     })
                Assert.True(reported.Any(site => site.Method == bypass),
                    $"The rows scan does not report the compiled bypass in '{bypass}'. A guard that "
                    + "cannot see the defect in a decoy cannot see it in the product either:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, reported.Select(o => $"  {o.Method} -> {o.Field}")));

            // ...and the model's own methods are NOT reported. A guard that punished the type that
            // owns the collection would report a defect on every correct route.
            Assert.DoesNotContain(reported,
                site => site.Method.StartsWith(
                    "AgentEyes.Tests.LibraryDefects.LibraryCoherence::", StringComparison.Ordinal));
        }

        /// <summary>
        /// Criterion 8: every route that reads or changes the Library, enumerated, with the model
        /// member each one must reach - read from the compiled app assembly, so a route that stops
        /// calling the model fails here whatever the source says.
        ///
        /// The three RepairService triggers are covered by the last two rows together: the repair
        /// service's ONLY channel into the library is its LibraryChanged callback (proved in
        /// <see cref="EveryRepairServiceTrigger_ReachesTheLibraryOnlyThroughLibraryChanged"/>), the
        /// window's only subscription to it is in the constructor, and that subscription calls
        /// LoadRecent - which is itself in this table.
        /// </summary>
        private static readonly (string Route, string[] MustReach)[] LibraryRoutes =
        {
            ("MainWindow::.ctor", new[]
            {
                "LibraryCoherence::get_Rows", "LibraryCoherence::set_SortKeyChanged",
                "MainWindow::LoadRecent",
            }),
            ("MainWindow::LoadRecent", new[]
            {
                "LibraryCoherence::BeginSnapshot", "LibraryCoherence::ApplySnapshot",
                "LibraryCoherence::AbandonSnapshot", "LibrarySnapshot::NewestFirst",
            }),
            ("MainWindow::Record_Click", new[] { "LibraryCoherence::Insert" }),
            ("MainWindow::StopAsync", new[]
            {
                "LibraryCoherence::Insert", "LibraryCoherence::SetStatus", "LibraryCoherence::Refresh",
            }),
            ("MainWindow::PackageDirAsync", new[]
            {
                "LibraryCoherence::Find", "LibraryCoherence::SetStatus", "LibraryCoherence::Refresh",
            }),
            ("MainWindow::RenameRecording_Click", new[] { "LibraryCoherence::Rename" }),
            ("MainWindow::DeleteRecordings", new[] { "LibraryCoherence::Delete" }),
            ("MainWindow::ImportVideo_Click", new[] { "MainWindow::LoadRecent" }),
            ("MainWindow::Search_TextChanged", new[] { "LibraryCoherence::get_Rows" }),
            ("MainWindow::ResortLibrary", new[] { "LibraryCoherence::get_Rows" }),
            ("MainWindow::UpdateLibraryTotal", new[] { "LibraryCoherence::get_Rows" }),
            ("MainWindow::UpdateEmptyState", new[] { "LibraryCoherence::get_Rows" }),
        };

        [Fact]
        public void EveryLibraryRoute_GoesThroughTheCoherenceModel()
        {
            const string ns = "AgentEyes.App.";
            var methods = CompiledCode.MethodNames(CompiledCode.AppAssembly);

            foreach (var (route, mustReach) in LibraryRoutes)
            {
                // Fail closed first: a renamed or deleted route would otherwise let this pass by
                // checking a method that is not in the binary.
                Assert.True(methods.Contains(ns + route, StringComparer.Ordinal),
                    $"'{ns + route}' is not in AgentEyesApp.dll, so this enumeration is checking a "
                    + "route that no longer exists and would pass by finding nothing.");

                var calls = CallsFrom(ns + route);

                foreach (string member in mustReach)
                    Assert.True(calls.Contains(ns + member, StringComparer.Ordinal),
                        $"The library route '{route}' does not call '{member}'. Every route that reads "
                        + "or changes the Library goes through LibraryCoherence (issue #3). It calls:"
                        + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", calls));
            }
        }

        /// <summary>Every call one method of the app makes, across every body the compiler split it
        /// into - an async method's state machine and its lambdas fold back onto their declaring
        /// method, and a constructor has as many bodies as the compiler chose to give it. Throws
        /// rather than returning nothing when the method makes no calls at all, so a rename cannot
        /// turn a Contains assertion into a check of an empty list.</summary>
        private static IReadOnlyList<string> CallsFrom(string method)
        {
            var calls = CompiledCode.CallSites(CompiledCode.AppAssembly, _ => true)
                .Where(site => site.Method == method)
                .Select(site => site.Callee)
                .ToList();

            if (calls.Count == 0)
                throw new InvalidOperationException(
                    $"'{method}' makes no calls in AgentEyesApp.dll - it has been renamed or removed, "
                    + "and this guard would otherwise pass by reading nothing.");
            return calls;
        }

        /// <summary>
        /// The repair service's only channel into the Library is its LibraryChanged callback. Read
        /// from Core's IL: the methods that raise it are enumerated, and the three repair STAGES -
        /// resume, titles, thumbnails - are each required to be among them.
        ///
        /// Its limit, stated: this proves those three stages signal through the callback, and the
        /// app-side half below proves the callback lands in the model. It does not and cannot prove
        /// that no future code in Core will find another way to a UI it cannot see.
        /// </summary>
        [Fact]
        public void EveryRepairServiceTrigger_ReachesTheLibraryOnlyThroughLibraryChanged()
        {
            var raisers = CompiledCode
                .CallSites(CompiledCode.CoreAssembly,
                    callee => callee.EndsWith("RepairService::get_LibraryChanged", StringComparison.Ordinal))
                .Select(site => site.Method)
                .Distinct(StringComparer.Ordinal)
                .Where(method => !method.EndsWith("::get_LibraryChanged", StringComparison.Ordinal))
                .ToList();

            Assert.True(raisers.Count > 0,
                "Nothing in agenteyes.dll reads RepairService.LibraryChanged, so this guard would "
                + "be passing on absence.");

            foreach (string stage in new[]
                     {
                         "AgentEyes.RepairService::ResumeAsync",
                         "AgentEyes.RepairService::TitleAsync",
                         "AgentEyes.RepairService::ThumbsAsync",
                     })
                Assert.True(raisers.Contains(stage, StringComparer.Ordinal),
                    $"The repair stage '{stage}' does not signal the library through LibraryChanged. "
                    + "It raises: " + string.Join(", ", raisers));
        }

        /// <summary>The app side of the same chain: the window subscribes to LibraryChanged in
        /// exactly one place, and that place is the constructor - which the route table above
        /// requires to call LoadRecent.</summary>
        [Fact]
        public void TheWindowSubscribesToLibraryChanged_InExactlyOnePlace()
        {
            var subscribers = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                    callee => callee.EndsWith("RepairService::set_LibraryChanged", StringComparison.Ordinal))
                .Select(site => site.Method)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "AgentEyes.App.MainWindow::.ctor" }, subscribers);
        }
    }
}
