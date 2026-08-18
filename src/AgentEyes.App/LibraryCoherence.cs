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
    /// newest evidence about that recording and whether it said Present or Removed. A landing
    /// snapshot is then MERGED, one recording at a time:
    ///
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
    internal sealed class LibraryCoherence
    {
        /// <summary>What the newest evidence about one recording said.</summary>
        private enum Evidence
        {
            /// <summary>The recording exists and its row is in the collection.</summary>
            Present,

            /// <summary>The recording is gone and must not be re-added by an older snapshot.</summary>
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

                    if (_facts.TryGetValue(fresh.Dir, out var fact) && fact.Epoch > epoch)
                    {
                        // Newer evidence about THIS recording already landed. The snapshot read the
                        // disk before it happened, so on this one recording the snapshot is stale.
                        if (fact.What == Evidence.Removed) { refusedResurrection++; continue; }

                        if (!byDir.ContainsKey(fresh.Dir))
                            throw new InvalidOperationException(
                                $"The library's fact table says {fresh.Dir} is present at epoch "
                                + $"{fact.Epoch} but there is no row for it. The fact table and the "
                                + "rows have diverged, which no route is allowed to do.");

                        keptLive++;
                        continue;
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
        /// Removes rows for recordings the user just deleted, and TOMBSTONES each directory. The
        /// directories themselves are deleted afterwards and off the UI thread, so a reload that
        /// started before this can still see their manifests - the tombstone is what stops it putting
        /// the rows back. Returns the directories to delete, in the order given.
        /// </summary>
        public IReadOnlyList<string> Delete(IEnumerable<RecentItem> items)
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

                    _facts[item.Dir] = new Fact(epoch, Evidence.Removed);
                    dirs.Add(item.Dir);
                }

            PruneTombstones();

            Log.Info($"[LibraryCoherence] Delete: {dirs.Count} recording(s), epoch={epoch}, "
                     + $"rows={_rows.Count}");
            return dirs;
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
        /// Forgets tombstones no snapshot can still be racing. A tombstone only has to outlive the
        /// reads that were already in flight when the delete happened; once the oldest of those has
        /// settled, nothing can arrive claiming the recording is still there.
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
                Log.Info($"[LibraryCoherence] PruneTombstones: dropped {expired.Count} tombstone(s) "
                         + $"below epoch {(horizon == long.MaxValue ? "(nothing in flight)" : horizon.ToString())}");
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
}
