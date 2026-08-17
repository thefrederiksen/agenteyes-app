using System;
using System.Collections.Generic;
using System.IO;

namespace AgentEyes.App
{
    /// <summary>
    /// Builds the library's list of recordings: every recording folder under a root, newest first by
    /// RECORDING START (issue #178).
    ///
    /// It lives outside MainWindow so the ORDER is a fact a test can read directly. The order used to
    /// be produced inside the window's loader lambda, where the only thing a guard could do was read
    /// the source for the words "list.Sort" - and the independent review of PR #179 defeated exactly
    /// that guard by writing "Permute(list);" on the next line. A method that RETURNS the list can be
    /// called with a fixture of recordings whose folder names contradict their manifests, and its
    /// answer either is newest-first or is not; nothing spliced in afterwards can hide from that.
    ///
    /// The enumeration is file I/O and the thumbnails are decoded here too, so this runs on a worker
    /// thread - the caller awaits it and only swaps the finished list on the UI thread.
    /// </summary>
    internal static class LibrarySnapshot
    {
        /// <summary>
        /// Every recording under <paramref name="root"/> that has a manifest.json, as library cards,
        /// newest first with undated recordings last.
        ///
        /// There is no cap: the library is where recordings get cleaned up (issue #11), so the oldest
        /// ones have to be reachable. A root that does not exist yet is an empty library, not an
        /// error - that is the state of a machine that has never recorded.
        /// </summary>
        public static List<RecentItem> NewestFirst(string root)
        {
            if (root is null) throw new ArgumentNullException(nameof(root));
            Log.Info($"[LibrarySnapshot] NewestFirst: reading {root}");

            var list = new List<RecentItem>();
            if (!Directory.Exists(root))
            {
                Log.Info($"[LibrarySnapshot] NewestFirst: {root} does not exist - the library is empty.");
                return list;
            }

            foreach (string dir in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(dir, "manifest.json")))
                {
                    var item = RecentItem.From(dir);
                    item.LoadThumb();
                    list.Add(item);
                }

            // Newest first by RECORDING START, using the same comparer the collection view sorts
            // with, so the library has exactly one ordering rule. The directory name used to stand in
            // for a date here ("2026-08-17_080332_video" ordered as a string); it is not a date, and
            // it is no longer consulted. This sort is the LAST word on the order - nothing may
            // re-order the list after it.
            list.Sort(RecentItem.NewestFirst);

            Log.Info($"[LibrarySnapshot] NewestFirst: {list.Count} recording(s), newest first.");
            return list;
        }
    }
}
