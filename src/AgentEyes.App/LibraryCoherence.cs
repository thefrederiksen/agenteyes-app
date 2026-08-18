using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AgentEyes.App
{
    /// <summary>
    /// The library's coherence model (issue #3): the ONE place the library's rows change, and the
    /// ordering between the reloads that are in flight and the changes the user makes while they are.
    ///
    /// THE PROBLEM. Reading the library is slow (a directory walk, a manifest per recording, a
    /// thumbnail decode per recording) so it happens on a worker; several reads overlap, because the
    /// repair service asks for one after each of its stages, an import asks for one, and the window
    /// asks for one at startup. Meanwhile the user saves a recording, takes a screenshot, renames a
    /// row or deletes one. Before this type each read CLEARED the collection and reinstalled its own
    /// answer, so the last read to finish won - even when it was the oldest, and even when it had
    /// been started before half of what it was overwriting existed. The visible result was the defect
    /// this repository chased for weeks: a recording that is on disk missing from the library.
    ///
    /// WHY NOT "NEWEST WINS". That was tried, on the archived repository's PR #179, and the
    /// independent review gate rejected it twice for the same reason: a snapshot that is dropped for
    /// being stale takes with it everything only IT knew about. Fixing "stale wins" by discarding the
    /// stale snapshot just renames the bug to "newest never lands". A whole-snapshot decision is the
    /// wrong granularity, because a snapshot is not one fact - it is one fact per recording, and only
    /// some of them are stale.
    ///
    /// THE MODEL. One monotonic counter, read and written only on the owning thread. A snapshot takes
    /// its START epoch before its worker touches the disk; every live change takes its own epoch as
    /// it happens. For each recording DIRECTORY the model keeps exactly one fact - the epoch of the
    /// newest evidence about that recording and what it said (<see cref="Evidence"/>). A landing
    /// snapshot is then MERGED, one recording at a time:
    ///
    /// * the snapshot has it and its deletion is STILL RUNNING -> refused, at any epoch (see below).
    /// * the snapshot has it, and the fact is NEWER than the snapshot's start -> the live state wins:
    ///   Present leaves the row untouched, Removed refuses to resurrect it.
    /// * the snapshot has it, with no newer fact -> the fresh values are adopted INTO the existing row
    ///   object, or the row is added when the recording is genuinely new.
    /// * the snapshot lacks it, and the fact is NEWER than the snapshot's start -> the row stays. The
    ///   snapshot simply read the disk before that recording existed.
    /// * the snapshot lacks it, with no newer fact -> the row goes, tombstoned at the snapshot's epoch
    ///   so an even older snapshot cannot bring it back.
    ///
    /// No snapshot is ever dropped, so there is nothing to merge back afterwards and nothing to
    /// retry. "Newest wins" still holds - PER RECORDING, which is the granularity at which the
    /// evidence actually differs.
    ///
    /// AN EPOCH CANNOT EXPRESS "THE DISK HAS NOT CAUGHT UP". Deleting is the one case where a NEWER
    /// snapshot is not better informed: the rows go immediately, the folders are removed afterwards
    /// on a worker, and for that whole window the manifest is still on disk, so a reload begun after
    /// the delete honestly reports the recording - and, having the higher epoch, outranks the
    /// tombstone. That is precisely how the first round of this issue resurrected deleted rows. A
    /// deletion is therefore bounded by its OUTCOME and not by an epoch: it sits in
    /// <see cref="Evidence.Removing"/>, which beats every snapshot at any epoch, until
    /// <see cref="CompleteDelete"/> reports that the folders are gone (or could not be removed), at
    /// which point epoch ordering resumes and a failed deletion is free to reappear.
    ///
    /// A FAILED READ IS NOT AN EMPTY LIBRARY. A worker that throws reports through
    /// <see cref="AbandonSnapshot"/> and changes nothing. The loader used to catch the failure, leave
    /// its list empty, and install that empty list - a blank library produced by a broken instrument.
    /// Nor can a failing or hanging read block a good one: nothing here waits on a generation, so a
    /// snapshot that never lands is simply a snapshot that never lands.
    ///
    /// ROWS ARE UPDATED, NOT REPLACED. A reload reuses the existing <see cref="RecentItem"/> for a
    /// directory it already has. That is what makes a row captured before an await still be the row
    /// on screen after it, and it keeps bindings, thumbnails, live status and the user's selection
    /// alive across a reload. <see cref="Refresh"/> and <see cref="SetStatus"/> re-resolve by
    /// directory anyway, so even a row that WAS detached (deleted and re-added) updates what is
    /// actually visible.
    ///
    /// THREAD AFFINITY. The counter, the fact table and the collection are one piece of state and it
    /// belongs to the thread that created this object - the UI thread in the running app. Every
    /// public route asserts that before it touches anything.
    /// </summary>
    internal sealed partial class LibraryCoherence
    {
        /// <summary>What the newest evidence about one recording said.</summary>
        private enum Evidence
        {
            /// <summary>The recording exists and its row is in the collection.</summary>
            Present,

            /// <summary>
            /// The user deleted it and the recursive folder delete is STILL RUNNING. The manifest is
            /// therefore still on disk, so a snapshot can honestly report the recording as present
            /// however NEW that snapshot is - a higher epoch does not make it better informed about a
            /// directory the filesystem has not finished removing. This state outranks EVERY
            /// snapshot, at any epoch, until <see cref="CompleteDelete"/> says the deletion settled.
            /// </summary>
            Removing,

            /// <summary>
            /// The deletion has SETTLED, at <see cref="Fact.Epoch"/>. From that instant the disk tells
            /// the truth again, so ordinary epoch ordering resumes: a snapshot that started earlier
            /// still loses, and one that started later is believed - which is what lets a recording
            /// whose deletion FAILED come back, and keeps one whose deletion succeeded gone.
            /// </summary>
            Removed,
        }

        /// <summary>The newest evidence about one recording, and when it was obtained.</summary>
        private readonly record struct Fact(long Epoch, Evidence What);

        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly RecentItemCollection _rows = new();
        private readonly Dictionary<string, Fact> _facts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _inFlight = new();
        private long _clock;

        /// <summary>The library's rows, for binding and for reading. Changing them from anywhere but
        /// this type throws - see <see cref="RecentItemCollection"/>.</summary>
        public ObservableCollection<RecentItem> Rows => _rows;

        /// <summary>
        /// Raised when a change moved a row's SORT KEY, so the view has to be re-sorted. A collection
        /// view does not re-sort because a field it sorts on changed, and this type does not know
        /// which view it is in - so it says so and the window refreshes the view (issue #178).
        /// </summary>
        public Action? SortKeyChanged { get; set; }

        /// <summary>Number of snapshots begun and not yet landed or abandoned. Diagnostic.</summary>
        public int InFlightSnapshots
        {
            get { RequireOwningThread(); return _inFlight.Count; }
        }

        /// <summary>
        /// How many times the rows and the ordering state were found DIVERGED and had to be
        /// reconciled - see <see cref="ReconcileFactsWithRows"/>. It should be zero for the life of
        /// the process: anything else means something changed the rows without telling this model.
        ///
        /// It is here so the repair is observable rather than merely logged. A silent self-heal is
        /// indistinguishable from a self-heal that never runs, and both the test that proves a forced
        /// divergence IS repaired and the test that proves a legitimate sequence does NOT trip the
        /// alarm need to read this number to be capable of failing at all.
        /// </summary>
        public int RepairedDivergences { get; private set; }

        /// <summary>
        /// Claims the epoch for a library read that is ABOUT to start. Call it on the owning thread
        /// before the worker touches the disk: everything that happens after this point is newer
        /// information than anything the worker can return, and that is precisely what the epoch
        /// records.
        /// </summary>
        public long BeginSnapshot()
        {
            RequireOwningThread();
            long epoch = ++_clock;
            _inFlight.Add(epoch);
            Log.Info($"[LibraryCoherence] BeginSnapshot: epoch={epoch}, in flight={_inFlight.Count}, "
                     + $"rows={_rows.Count}");
            return epoch;
        }

        /// <summary>
        /// Merges a completed snapshot into the library, one recording at a time, under the rules in
        /// the type comment. Never clears, never replaces the collection, and never drops the
        /// snapshot.
        /// </summary>
        public void ApplySnapshot(long epoch, IReadOnlyList<RecentItem> snapshot)
        {
            RequireOwningThread();
            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
            RetireSnapshot(epoch, nameof(ApplySnapshot));

            int added = 0, updated = 0, removed = 0, keptLive = 0, refusedResurrection = 0;
            bool resort = false;

            ReconcileFactsWithRows();

            using (_rows.BeginCoherentUpdate())
            {
                var byDir = new Dictionary<string, RecentItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in _rows) byDir[row.Dir] = row;

                var inSnapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var fresh in snapshot)
                {
                    if (fresh is null)
                        throw new ArgumentException("The snapshot contains a null row.", nameof(snapshot));
                    if (!inSnapshot.Add(fresh.Dir))
                        throw new ArgumentException(
                            $"The snapshot lists {fresh.Dir} twice; a recording directory is the "
                            + "library's identity and cannot appear more than once.", nameof(snapshot));

                    if (_facts.TryGetValue(fresh.Dir, out var fact))
                    {
                        // A deletion that is STILL RUNNING outranks every snapshot at ANY epoch. The
                        // snapshot is not wrong about the disk - the manifest really is still there -
                        // it is reading a directory the filesystem has not finished removing, and a
                        // higher epoch buys it nothing about that (issue #3, failure mode 6). The
                        // first round bounded this on the epoch, so a reload begun AFTER the delete
                        // always outranked the tombstone and the row came back.
                        if (fact.What == Evidence.Removing) { refusedResurrection++; continue; }

                        if (fact.Epoch > epoch)
                        {
                            // Newer evidence about THIS recording already landed. The snapshot read
                            // the disk before it happened, so on this recording it is stale.
                            if (fact.What == Evidence.Removed) { refusedResurrection++; continue; }
                            keptLive++;
                            continue;
                        }
                    }

                    if (byDir.TryGetValue(fresh.Dir, out var existing))
                    {
                        if (existing.AdoptFrom(fresh)) resort = true;
                        updated++;
                    }
                    else
                    {
                        _rows.Add(fresh);
                        added++;
                    }

                    _facts[fresh.Dir] = new Fact(epoch, Evidence.Present);
                }

                foreach (var row in _rows.ToArray())
                {
                    if (inSnapshot.Contains(row.Dir)) continue;

                    if (_facts.TryGetValue(row.Dir, out var fact) && fact.Epoch > epoch)
                    {
                        // The row was created or touched after this snapshot started reading, so the
                        // snapshot's silence about it is not evidence that it is gone. THIS is the
                        // arm that stops a reload swallowing a recording that only the live
                        // collection knew about.
                        keptLive++;
                        continue;
                    }

                    _rows.Remove(row);
                    _facts[row.Dir] = new Fact(epoch, Evidence.Removed);
                    removed++;
                }
            }

            PruneTombstones();

            Log.Info($"[LibraryCoherence] ApplySnapshot: epoch={epoch}, snapshot={snapshot.Count}, "
                     + $"added={added}, updated={updated}, removed={removed}, kept live={keptLive}, "
                     + $"refused resurrection={refusedResurrection}, rows={_rows.Count}");

            if (resort) SortKeyChanged?.Invoke();
        }

        /// <summary>
        /// The snapshot's worker failed. Nothing is applied - an empty list produced by a thrown
        /// exception is a broken instrument, not a library with no recordings in it - and no other
        /// snapshot is held up, because nothing here waits on a generation.
        /// </summary>
        public void AbandonSnapshot(long epoch, Exception error)
        {
            RequireOwningThread();
            if (error is null) throw new ArgumentNullException(nameof(error));
            RetireSnapshot(epoch, nameof(AbandonSnapshot));

            Log.Error($"[LibraryCoherence] AbandonSnapshot: epoch={epoch} failed to read the library; "
                      + $"its {_rows.Count} row(s) are left exactly as they are", error);

            PruneTombstones();
        }

        /// <summary>
        /// A recording just appeared and the library must show it NOW - a saved recording, a
        /// screenshot, an import. Returns the row that is actually in the library, which is the
        /// existing row when the recording is already there (a re-save must not produce a second
        /// card for one directory).
        /// </summary>
        public RecentItem Insert(string dir)
        {
            RequireOwningThread();
            if (string.IsNullOrWhiteSpace(dir))
                throw new ArgumentException("A library row needs a recording directory.", nameof(dir));

            long epoch = ++_clock;
            var fresh = RecentItem.From(dir);
            var existing = Find(dir);
            bool resort = false;

            using (_rows.BeginCoherentUpdate())
            {
                if (existing != null) resort = existing.AdoptFrom(fresh);
                else _rows.Insert(0, fresh);
            }

            _facts[dir] = new Fact(epoch, Evidence.Present);

            Log.Info($"[LibraryCoherence] Insert: dir={dir}, epoch={epoch}, "
                     + $"{(existing != null ? "updated the existing row" : "added a row")}, "
                     + $"rows={_rows.Count}");

            if (resort) SortKeyChanged?.Invoke();
            return existing ?? fresh;
        }

        /// <summary>
        /// Removes rows for recordings the user just deleted, and marks each directory as a deletion
        /// IN PROGRESS. The rows go now so the list feels instant; the directories are removed
        /// afterwards and off the UI thread, and that gap is the whole of failure mode 6.
        ///
        /// The caller MUST report the outcome with <see cref="CompleteDelete"/> when the recursive
        /// delete has finished - see <see cref="LibraryDeletion"/> for why the outcome, and not an
        /// epoch, is what bounds this.
        /// </summary>
        public LibraryDeletion Delete(IEnumerable<RecentItem> items)
        {
            RequireOwningThread();
            if (items is null) throw new ArgumentNullException(nameof(items));

            long epoch = ++_clock;
            var dirs = new List<string>();

            using (_rows.BeginCoherentUpdate())
                foreach (var item in items)
                {
                    if (item is null) throw new ArgumentException("A row to delete is null.", nameof(items));

                    // By directory, not by reference: the caller may be holding a row from before a
                    // reload, and it is the row in the collection that has to go.
                    var row = Find(item.Dir);
                    if (row != null) _rows.Remove(row);

                    _facts[item.Dir] = new Fact(epoch, Evidence.Removing);
                    dirs.Add(item.Dir);
                }

            PruneTombstones();

            Log.Info($"[LibraryCoherence] Delete: {dirs.Count} recording(s) now REMOVING, "
                     + $"epoch={epoch}, rows={_rows.Count}");
            return new LibraryDeletion(epoch, dirs);
        }

        /// <summary>
        /// The recursive folder delete has finished. <paramref name="failed"/> names the directories
        /// that could NOT be removed.
        ///
        /// This is what bounds the tombstone, and it is deliberately not an epoch. Until this point
        /// the manifests may still be on disk, so a snapshot reporting the recording is neither wrong
        /// nor stale - it is early, and no epoch can express that. From this point the disk tells the
        /// truth again and ordinary epoch ordering resumes, which gives both halves for free:
        ///
        /// * the deletion SUCCEEDED -> no later snapshot lists the recording, so the row stays gone;
        /// * the deletion FAILED -> a later snapshot does list it, and the row comes back, which is
        ///   the property that stops a failed deletion hiding a recording forever.
        ///
        /// A snapshot that began BEFORE this point still loses, because its epoch is lower: it read
        /// the disk while the delete was still running.
        /// </summary>
        public void CompleteDelete(LibraryDeletion deletion, IReadOnlyCollection<string> failed)
        {
            RequireOwningThread();
            if (deletion is null) throw new ArgumentNullException(nameof(deletion));
            if (failed is null) throw new ArgumentNullException(nameof(failed));

            long epoch = ++_clock;
            foreach (string dir in deletion.Directories)
            {
                // Only if this deletion is still the newest thing that happened to the directory. A
                // recording re-imported to the same folder while the delete ran is live evidence and
                // must not be tombstoned by the delete it outlived.
                if (_facts.TryGetValue(dir, out var fact)
                    && fact.What == Evidence.Removing && fact.Epoch == deletion.Epoch)
                    _facts[dir] = new Fact(epoch, Evidence.Removed);
            }

            PruneTombstones();

            Log.Info($"[LibraryCoherence] CompleteDelete: {deletion.Directories.Count} recording(s) "
                     + $"settled at epoch={epoch} ({failed.Count} could not be removed), "
                     + $"rows={_rows.Count}");
        }

        /// <summary>
        /// The user renamed a recording. The new name is newer information than any reload that is
        /// still in flight, so it is stamped as such and cannot be overwritten by one of them.
        /// </summary>
        public void Rename(RecentItem held, string title)
        {
            RequireOwningThread();
            if (held is null) throw new ArgumentNullException(nameof(held));
            if (title is null) throw new ArgumentNullException(nameof(title));

            var row = Resolve(held, nameof(Rename));
            if (row == null) return;

            long epoch = ++_clock;
            row.Title = title;
            _facts[row.Dir] = new Fact(epoch, Evidence.Present);
            Log.Info($"[LibraryCoherence] Rename: dir={row.Dir}, epoch={epoch}, title=\"{title}\"");
        }

        /// <summary>
        /// Re-reads a recording's manifest into its visible row - packaging or a repair pass just
        /// filled in the generated title, description, cost or artifacts. Re-sorts the view when the
        /// recording's START TIME moved, which is the only thing that changes where the card belongs.
        /// Returns true when it did.
        /// </summary>
        public bool Refresh(RecentItem held)
        {
            RequireOwningThread();
            if (held is null) throw new ArgumentNullException(nameof(held));

            var row = Resolve(held, nameof(Refresh));
            if (row == null) return false;

            bool moved = row.RefreshNaming();
            _facts[row.Dir] = new Fact(++_clock, Evidence.Present);
            Log.Info($"[LibraryCoherence] Refresh: dir={row.Dir}, epoch={_clock}, sort key moved={moved}");

            if (moved) SortKeyChanged?.Invoke();
            return moved;
        }

        /// <summary>
        /// Live progress text on a row ("Transcribing...", then empty). Resolved by directory like
        /// every other held-row route, so a stop path that captured its row before an await still
        /// writes on the row the user can see.
        /// </summary>
        public void SetStatus(RecentItem held, string status)
        {
            RequireOwningThread();
            if (held is null) throw new ArgumentNullException(nameof(held));
            if (status is null) throw new ArgumentNullException(nameof(status));

            var row = Resolve(held, nameof(SetStatus));
            if (row == null) return;
            row.Status = status;
        }

        /// <summary>The library's row for a recording directory, or null when it has none.</summary>
        public RecentItem? Find(string dir)
        {
            RequireOwningThread();
            if (dir is null) throw new ArgumentNullException(nameof(dir));

            foreach (var row in _rows)
                if (string.Equals(row.Dir, dir, StringComparison.OrdinalIgnoreCase))
                    return row;
            return null;
        }

        /// <summary>
        /// The row in the collection for a row the caller is holding. Detachment is REPORTED rather
        /// than worked around silently: a caller holding a row that is no longer the library's row
        /// for that recording has been through a reload or a delete, and writing on its copy is how a
        /// visible card keeps a stale name after a rename or a repair.
        /// </summary>
        private RecentItem? Resolve(RecentItem held, string route)
        {
            var row = Find(held.Dir);
            if (row == null)
            {
                Log.Info($"[LibraryCoherence] {route}: {held.Dir} is no longer in the library "
                         + "(deleted while the work was running) - there is nothing to update.");
                return null;
            }

            if (!ReferenceEquals(row, held))
                Log.Warn($"[LibraryCoherence] {route}: the caller's row for {held.Dir} is detached "
                         + "from the library; updating the row that is actually in it.");
            return row;
        }

        /// <summary>Takes a snapshot out of the in-flight set, refusing an epoch that was never begun
        /// or has already been settled - either would mean a caller lost track of its own read.</summary>
        private void RetireSnapshot(long epoch, string route)
        {
            if (_inFlight.Remove(epoch)) return;

            throw new InvalidOperationException(
                $"LibraryCoherence.{route} was given epoch {epoch}, which is not a snapshot that is "
                + "in flight. Every snapshot is begun exactly once with BeginSnapshot and settled "
                + "exactly once with ApplySnapshot or AbandonSnapshot.");
        }

        /// <summary>
        /// Forgets tombstones nothing can still be racing.
        ///
        /// A SETTLED tombstone (<see cref="Evidence.Removed"/>) only has to outlive the reads that
        /// were in flight when the deletion settled: once the oldest of those is gone, every future
        /// snapshot starts after the settlement and reads the disk as it now is. With nothing in
        /// flight there is no such read at all, so it can go immediately.
        ///
        /// A deletion still RUNNING is never pruned, at any horizon. That is the correction to the
        /// first round, where a delete made with no reload in flight had its tombstone dropped by
        /// the very call that created it - and, worse, a snapshot landing correctly was itself the
        /// event that drained the in-flight set and let the NEXT one resurrect the row.
        /// </summary>
        private void PruneTombstones()
        {
            long horizon = _inFlight.Count == 0 ? long.MaxValue : _inFlight.Min();

            var expired = _facts
                .Where(fact => fact.Value.What == Evidence.Removed && fact.Value.Epoch < horizon)
                .Select(fact => fact.Key)
                .ToList();

            foreach (string dir in expired) _facts.Remove(dir);

            if (expired.Count > 0)
                Log.Info($"[LibraryCoherence] PruneTombstones: dropped {expired.Count} settled "
                         + $"tombstone(s) below epoch "
                         + $"{(horizon == long.MaxValue ? "(nothing in flight)" : horizon.ToString())}");
        }

        /// <summary>
        /// Re-derives the fact table from the rows when the two have diverged, and says so loudly.
        ///
        /// Divergence is not something any route here can produce - it means something reached the
        /// collection without telling the model. This used to THROW from inside the merge, which put
        /// an exception on the path of <c>async void LoadRecent</c> where nothing catches it: a
        /// bypass did not merely evade the model, it could take the window down with it (issue #3,
        /// QA round 1, finding N9). Killing the app is a strictly worse answer than repairing a
        /// derived index and reporting it.
        ///
        /// And it IS a derived index. The rows are what the user is looking at; the fact table is
        /// bookkeeping ABOUT those rows. Rebuilding the bookkeeping from the thing it describes is a
        /// repair, not a fallback - it hides nothing, because every correction is logged as an error
        /// naming the directory.
        /// </summary>
        private void ReconcileFactsWithRows()
        {
            var rowDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows) rowDirs.Add(row.Dir);

            // A fact says a row is there and it is not.
            var orphaned = _facts
                .Where(fact => fact.Value.What == Evidence.Present && !rowDirs.Contains(fact.Key))
                .Select(fact => fact.Key)
                .ToList();

            // A row is there and the facts do not say so - unknown, or contradicted by a deletion.
            var unexplained = rowDirs
                .Where(dir => !_facts.TryGetValue(dir, out var fact) || fact.What != Evidence.Present)
                .ToList();

            if (orphaned.Count == 0 && unexplained.Count == 0) return;

            RepairedDivergences++;
            Log.Error("[LibraryCoherence] ReconcileFactsWithRows: the library's rows and its ordering "
                      + $"state have DIVERGED - {orphaned.Count} recording(s) recorded as present with "
                      + $"no row ({string.Join(", ", orphaned)}), {unexplained.Count} row(s) with no "
                      + $"matching record ({string.Join(", ", unexplained)}). Something changed the "
                      + "rows without going through this model. Re-deriving the state from the rows.",
                      new InvalidOperationException("library rows and ordering state diverged"));

            foreach (string dir in orphaned) _facts.Remove(dir);
            foreach (string dir in unexplained) _facts[dir] = new Fact(++_clock, Evidence.Present);
        }

        private void RequireOwningThread([CallerMemberName] string route = "")
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId) return;

            throw new InvalidOperationException(
                $"LibraryCoherence.{route} was called from thread "
                + $"{Environment.CurrentManagedThreadId}. The library's ordering state and its rows "
                + $"are one piece of state and belong to the thread that created them "
                + $"({_ownerThreadId}) - the UI thread in the running app. Marshal the call with "
                + "Dispatcher.BeginInvoke.");
        }
    }

    /// <summary>
    /// One delete the user asked for, from the moment its rows leave the Library until the moment
    /// its folders are actually gone.
    ///
    /// It exists because those are two different instants and the gap between them is real: the rows
    /// go immediately so the list feels instant, and a multi-gigabyte recording folder then takes a
    /// noticeable beat to remove on a worker. For that whole window the manifest is STILL on disk,
    /// so a reload - however new - honestly reports the recording as present. An epoch cannot express
    /// "the disk has not caught up yet"; only the deletion's own outcome can, which is why the caller
    /// hands this back to <see cref="LibraryCoherence.CompleteDelete"/> when the work is done.
    /// </summary>
    internal sealed class LibraryDeletion
    {
        internal LibraryDeletion(long epoch, IReadOnlyList<string> directories)
        {
            Epoch = epoch;
            Directories = directories ?? throw new ArgumentNullException(nameof(directories));
        }

        /// <summary>When the rows were removed - identifies THIS deletion, so a recording re-created
        /// in the same folder while the delete ran is not tombstoned by the delete it outlived.</summary>
        internal long Epoch { get; }

        /// <summary>The recording folders to remove, in the order the user's selection gave them.</summary>
        public IReadOnlyList<string> Directories { get; }
    }
}
