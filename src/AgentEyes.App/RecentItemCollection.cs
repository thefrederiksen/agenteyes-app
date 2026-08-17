using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AgentEyes.App
{
    /// <summary>
    /// The library's rows. An <see cref="ObservableCollection{T}"/> that can also be replaced
    /// WHOLESALE in one notification.
    ///
    /// Why (issue #178): the loader used to clear the collection and then Add every row one at a
    /// time. Each Add raises CollectionChanged, and the library's handler walks the whole collection
    /// to re-total the AI spend - so replacing n rows cost O(n squared) UI-thread work, plus one
    /// incremental insertion into the sorted view per row. Invisible at 44 recordings; a refresh
    /// storm at 4,400. <see cref="ReplaceAll"/> swaps the contents underneath and raises ONE Reset,
    /// which is exactly what a ListCollectionView and an ItemsControl want for a full swap.
    ///
    /// This type says WHAT the collection holds and how cheaply it is replaced. It deliberately says
    /// nothing about WHICH of several overlapping snapshots is allowed to reach the screen - that
    /// ordering question is issue #180 (the library's coherence model) and has no answer here.
    /// </summary>
    internal sealed class RecentItemCollection : ObservableCollection<RecentItem>
    {
        /// <summary>Replaces every row with <paramref name="items"/>, raising a single Reset.
        /// UI thread only - it is the same collection WPF is bound to.</summary>
        public void ReplaceAll(IReadOnlyList<RecentItem> items)
        {
            if (items is null) throw new ArgumentNullException(nameof(items));

            Items.Clear();
            foreach (var item in items) Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
