using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AgentEyes.App
{
    internal sealed partial class LibraryCoherence
    {
    /// <summary>
    /// The library's rows: an <see cref="ObservableCollection{T}"/> that may only be changed from
    /// INSIDE the library's coherence model (issue #3), and only from the thread that created it.
    ///
    /// It is a PRIVATE NESTED type of <see cref="LibraryCoherence"/>, which is the second half of
    /// the gate below. It used to be an assembly-internal type of its own, and
    /// <see cref="LibraryCoherence.Rows"/> hands it out typed as its base class - so any code in
    /// this assembly could name the real type, cast the property back to it, open a scope itself and
    /// mutate freely with the model none the wiser (issue #3, QA round 1, finding 6a). Nested and
    /// private, the type cannot be NAMED outside the model, so that cast cannot be written.
    ///
    /// Its honest limit: reflection can still reach it. What reflection cannot do is reach it by
    /// accident, which is what a guard against a plain cast is actually for - and a divergence that
    /// arrives anyway is now repaired and logged rather than thrown onto the UI thread (see
    /// <see cref="LibraryCoherence.ReconcileFactsWithRows"/>).
    ///
    /// Why the gate. Before it, any route could reach in and mutate the collection - and several did,
    /// each with its own idea of what the library currently held. The loader cleared and repopulated
    /// wholesale, a screenshot inserted at 0, a delete removed rows, and nothing ordered any of them
    /// against each other. The result was the defect that started this: a recording that exists on
    /// disk missing from the library. A structural guard over the SOURCE could not close that, because
    /// a mutation can be spelled a dozen ways - the previous attempt recognized only
    /// Insert/Remove/Clear/Add and a direct <c>RemoveAt(0)</c> produced zero matcher hits.
    ///
    /// This gate is spelling-independent. <see cref="Collection{T}"/> routes EVERY mutation - Add,
    /// Insert, Remove, RemoveAt, Move, <c>this[i] =</c>, Clear, the non-generic <see cref="System.Collections.IList"/>
    /// members, and any wrapper written over them - through exactly five protected virtual methods,
    /// and all five are overridden here to demand an open <see cref="BeginCoherentUpdate"/> scope.
    /// A caller that has the collection reference and mutates it anyway gets an exception on the
    /// spot rather than a library that quietly disagrees with the disk.
    ///
    /// The scope also carries issue #178's fix forward. Notifications raised inside it are held back
    /// and settled ONCE when it closes: a whole reload is one Reset rather than one event per row
    /// (the handler on CollectionChanged re-walks the collection to total the AI spend, so per-row
    /// events were O(n squared) UI-thread work). A single-row change still reports itself precisely
    /// - re-raised verbatim - so inserting one screenshot does not reset the list and lose the user's
    /// selection. A reload that changes nothing now raises NOTHING, which is the common case.
    /// </summary>
    private sealed class RecentItemCollection : ObservableCollection<RecentItem>
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly List<NotifyCollectionChangedEventArgs> _held = new();
        private int _depth;

        /// <summary>True while a coherent-update scope is open on this collection.</summary>
        public bool IsUpdating => _depth > 0;

        /// <summary>
        /// Opens the window in which this collection may be changed. Dispose it to settle the held
        /// notifications. Nesting is allowed and only the outermost scope settles.
        /// </summary>
        public IDisposable BeginCoherentUpdate()
        {
            RequireOwningThread("BeginCoherentUpdate");
            _depth++;
            return new Scope(this);
        }

        protected override void InsertItem(int index, RecentItem item)
        {
            RequireOpenScope("insert a row");
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            RequireOpenScope("remove a row");
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, RecentItem item)
        {
            RequireOpenScope("replace a row");
            base.SetItem(index, item);
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            RequireOpenScope("move a row");
            base.MoveItem(oldIndex, newIndex);
        }

        protected override void ClearItems()
        {
            RequireOpenScope("clear the rows");
            base.ClearItems();
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (_depth > 0) { _held.Add(e); return; }
            base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            // Count and Item[] are settled together when the scope closes; holding them back here is
            // what keeps a 4,000-row reload from raising 8,000 property notifications.
            if (_depth > 0) return;
            base.OnPropertyChanged(e);
        }

        /// <summary>
        /// Every mutation lands here first. Two separate refusals, and neither is advisory: a change
        /// from another thread would corrupt a collection WPF is bound to, and a change outside the
        /// coherence model is a route that has no ordering against the library's other routes - the
        /// exact defect issue #3 exists to remove.
        /// </summary>
        private void RequireOpenScope(string what)
        {
            RequireOwningThread(what);
            if (_depth > 0) return;

            throw new InvalidOperationException(
                $"Something tried to {what} of the library from outside its coherence model. Every "
                + "change to the library's rows - insert, remove, move, replace, clear, however it is "
                + "spelled - goes through LibraryCoherence, which orders it against the reloads that "
                + "are in flight (issue #3). Call the matching LibraryCoherence route instead of "
                + "mutating the collection directly.");
        }

        private void RequireOwningThread(string what)
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId) return;

            throw new InvalidOperationException(
                $"Something tried to {what} of the library from thread "
                + $"{Environment.CurrentManagedThreadId}. The library's rows belong to the thread that "
                + $"created them ({_ownerThreadId}) - the UI thread in the running app. Marshal the "
                + "change with Dispatcher.BeginInvoke and let LibraryCoherence apply it there.");
        }

        /// <summary>Settles the notifications held back while the scope was open.</summary>
        private void Settle()
        {
            if (_held.Count == 0) return;

            var held = _held.ToArray();
            _held.Clear();

            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));

            // One change reports itself exactly - a Reset would drop the ListBox's selection for a
            // single inserted screenshot. Several changes coalesce into the Reset that a
            // ListCollectionView and an ItemsControl want for a wholesale swap (issue #178).
            base.OnCollectionChanged(held.Length == 1
                ? held[0]
                : new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private sealed class Scope : IDisposable
        {
            private RecentItemCollection? _owner;

            public Scope(RecentItemCollection owner) => _owner = owner;

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;   // Dispose is idempotent; a using block may run it twice
                _owner = null;

                owner.RequireOwningThread("close a coherent update");
                owner._depth--;
                if (owner._depth == 0) owner.Settle();
            }
        }
    }
    }
}
