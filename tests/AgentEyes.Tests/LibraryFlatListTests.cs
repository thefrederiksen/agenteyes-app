using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using AgentEyes.App;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #178 - the Library is ONE FLAT LIST, newest first, and the recording start time in
    /// manifest.json (CreatedUtc) is the only date or time it uses.
    ///
    /// What went wrong: three different notions of "when" shared one screen. The card label came
    /// from CreatedUtc (right), the day-group header came from CreatedUtc but fell back to the
    /// folder's filesystem creation time and then to DateTime.Now (wrong), and the ORDER came from
    /// the directory NAME string (not a date at all). The visible symptom was a "Today" header over
    /// July recordings while the recording actually made that morning was nowhere on screen.
    ///
    /// These tests hold both halves of the fix:
    ///
    /// * BEHAVIOUR - what the library actually does with a manifest: which date it derives, how it
    ///   orders two recordings, what it does with a recording that has no usable date, and what
    ///   order the loader's snapshot comes back in. Run, not read.
    /// * SOURCE / IL - what the library is structurally incapable of doing. "No filesystem date and
    ///   no DateTime.Now anywhere in the date path" is a statement about every branch, including the
    ///   ones no fixture reaches, so it is answered from the compiled IL (precedent:
    ///   ManifestWriterIlTests / CompiledCode) rather than from a fixture that could only ever miss
    ///   them. Grouping's absence is a XAML/wiring fact, answered from the source it lives in
    ///   (precedent: StopPathTests / RepoSource).
    ///
    /// Every structural guard here is negative-controlled: the defect it claims to catch is compiled
    /// into LibraryDefectDecoys.cs, and a test points the SAME guard at that assembly and requires it
    /// to report the defect. A guard that has never been seen to fail is not evidence.
    ///
    /// NOT COVERED HERE, and deliberately so: which of several overlapping asynchronous reloads is
    /// allowed to reach the screen. That is issue #180 (the Library's coherence model). The first
    /// attempt at it lived on this branch and was rejected twice by the independent review gate, so
    /// it was removed rather than half-built; the Library's asynchronous behaviour is exactly what
    /// v1.4.1 ships today.
    /// </summary>
    public sealed class LibraryFlatListTests : IDisposable
    {
        private const string Xaml = @"src\AgentEyes.App\MainWindow.xaml";
        private const string CodeBehind = @"src\AgentEyes.App\MainWindow.xaml.cs";
        private const string Coherence = @"src\AgentEyes.App\LibraryCoherence.cs";

        private readonly string _root;

        public LibraryFlatListTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>A recording folder holding a manifest.json with the given CreatedUtc. Pass null
        /// to leave the property out entirely (the "missing" case) - which is a different fixture
        /// from a present-but-unparseable value, and both have to be covered.</summary>
        private string Recording(string leaf, string? createdUtc)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(dir);

            var manifest = new Dictionary<string, object>
            {
                ["Tool"] = "AgentEyes",
                ["Mode"] = "video",
                ["Label"] = leaf,
                ["DurationSeconds"] = 12.5,
            };
            if (createdUtc != null) manifest["CreatedUtc"] = createdUtc;

            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return dir;
        }

        private static List<RecentItem> InLibraryOrder(params RecentItem[] items)
        {
            var list = items.ToList();
            list.Sort(RecentItem.NewestFirst);
            return list;
        }

        // ---- criterion 4: the order comes from CreatedUtc, not the folder name ----

        [Fact]
        public void NewestFirst_OrdersByCreatedUtc_NotByDirectoryName()
        {
            // The folder names say the opposite of the manifests on purpose: sorting the NAMES
            // descending (what the library used to do) puts "2026-12-31..." first, and sorting the
            // recording START descending puts "2026-01-01..." first. Only one of them is a date.
            var newest = RecentItem.From(Recording("2026-01-01_000000_video", "2026-08-14T14:44:00.0000000Z"));
            var oldest = RecentItem.From(Recording("2026-12-31_235959_video", "2025-03-02T09:00:00.0000000Z"));

            var ordered = InLibraryOrder(oldest, newest);

            Assert.Equal("2026-01-01_000000_video", Path.GetFileName(ordered[0].Dir));
            Assert.Equal("2026-12-31_235959_video", Path.GetFileName(ordered[1].Dir));
        }

        [Fact]
        public void NewestFirst_PutsTheMostRecentRecordingStartFirst()
        {
            var jan = RecentItem.From(Recording("a_video", "2026-01-05T08:00:00.0000000Z"));
            var aug = RecentItem.From(Recording("b_video", "2026-08-17T12:03:00.0000000Z"));
            var jul = RecentItem.From(Recording("c_video", "2026-07-07T23:59:00.0000000Z"));

            var ordered = InLibraryOrder(jan, aug, jul);

            Assert.Equal(new[] { "b_video", "c_video", "a_video" },
                ordered.Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        [Fact]
        public void NewestFirst_BreaksTiesDeterministically_SoAnEqualStartIsNotAnArbitraryOrder()
        {
            const string sameStart = "2026-08-17T08:03:32.0000000Z";
            var first = RecentItem.From(Recording("a_video", sameStart));
            var second = RecentItem.From(Recording("b_video", sameStart));

            Assert.Equal(new[] { "b_video", "a_video" },
                InLibraryOrder(first, second).Select(i => Path.GetFileName(i.Dir)).ToArray());
            Assert.Equal(new[] { "b_video", "a_video" },
                InLibraryOrder(second, first).Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        // ---- criterion 2 / review finding 1: the sort key is an INSTANT, not a wall clock ----

        /// <summary>
        /// The regression test for the defect the independent review of PR #179 reproduced: the card
        /// kept only the LOCAL reading of the recording start, and the comparer ordered those
        /// readings. Local wall-clock time is not monotonic - when the clocks go back in the autumn
        /// the same hour is read twice - so two recordings that straddle the transition came out in
        /// the wrong order, the older one first.
        ///
        /// The fixture is 45 minutes of real time, 05:30Z and 06:15Z on 2026-11-01, which in a zone
        /// that falls back at 06:00Z read 1:30 AM and 1:15 AM. The zone is named explicitly rather
        /// than taken from the machine, so the hazard is present no matter where this runs, and the
        /// first assertion proves the fixture really does straddle a transition rather than quietly
        /// testing nothing.
        /// </summary>
        [Fact]
        public void NewestFirst_DoesNotInvertAcrossTheAutumnDstTransition()
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var earlierUtc = new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc);
            var laterUtc = new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc);

            // Instrument check: the later INSTANT really does have the earlier local READING here.
            Assert.True(TimeZoneInfo.ConvertTimeFromUtc(laterUtc, zone)
                        < TimeZoneInfo.ConvertTimeFromUtc(earlierUtc, zone),
                "This fixture no longer straddles a fall-back transition, so it cannot catch the "
                + "wall-clock ordering defect it exists for.");

            var earlier = RecentItem.From(Recording("a_video", earlierUtc.ToString("O")));
            var later = RecentItem.From(Recording("b_video", laterUtc.ToString("O")));

            // The card keeps the instant, not a reading of it.
            Assert.Equal(DateTimeKind.Utc, later.StartedUtc!.Value.Kind);
            Assert.Equal(laterUtc, later.StartedUtc);
            Assert.Equal(earlierUtc, earlier.StartedUtc);

            // ...so the recording made 45 minutes later is listed first, whichever way the clocks went.
            Assert.Equal(new[] { "b_video", "a_video" },
                InLibraryOrder(earlier, later).Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        [Fact]
        public void StartUtc_NormalizesEveryManifestSpelling_ToTheSameInstant()
        {
            var instant = new DateTime(2026, 8, 14, 14, 44, 0, DateTimeKind.Utc);

            // Z, an explicit offset, and a bare date-time (the field's contract is UTC) - one instant.
            Assert.Equal(instant, RecentItem.StartUtc("d", "2026-08-14T14:44:00.0000000Z"));
            Assert.Equal(instant, RecentItem.StartUtc("d", "2026-08-14T16:44:00.0000000+02:00"));
            Assert.Equal(instant, RecentItem.StartUtc("d", "2026-08-14T14:44:00.0000000"));

            foreach (string spelling in new[]
                     {
                         "2026-08-14T14:44:00.0000000Z",
                         "2026-08-14T16:44:00.0000000+02:00",
                         "2026-08-14T14:44:00.0000000",
                     })
                Assert.Equal(DateTimeKind.Utc, RecentItem.StartUtc("d", spelling)!.Value.Kind);
        }

        /// <summary>
        /// The structural half of the same fix, and the one that fails if a future edit goes back to
        /// comparing local time: the ordering rule reads the UTC instant, and never the local
        /// projection or the clock. Read from IL, so it holds for every branch of the method.
        /// </summary>
        [Fact]
        public void TheOrderingRule_ReadsTheUtcInstant_AndNeverTheLocalReading()
        {
            var calls = CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.NewestFirstComparer::Compare");

            Assert.Contains("AgentEyes.App.RecentItem::get_StartedUtc", calls);
            Assert.DoesNotContain("AgentEyes.App.RecentItem::get_StartedLocal", calls);
            Assert.DoesNotContain("System.DateTime::ToLocalTime", calls);
        }

        /// <summary>Local time exists for the LABEL and nowhere else: it is derived from the instant
        /// at the moment it is displayed.</summary>
        [Fact]
        public void TheLocalReading_IsDerivedFromTheInstant_ForDisplayOnly()
        {
            var calls = CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.RecentItem::get_StartedLocal");

            Assert.Contains("System.DateTime::ToLocalTime", calls);
        }

        // ---- criterion 6: no usable CreatedUtc -> undated, last, logged ------

        [Theory]
        [InlineData(null)]          // the property is absent from the manifest
        [InlineData("")]            // present and empty - what a default-constructed manifest holds
        [InlineData("   ")]
        [InlineData("not-a-date")]  // present and unparseable
        public void MissingOrUnparseableCreatedUtc_IsUndated_AndIsNeverGivenTodaysDate(string? createdUtc)
        {
            var item = RecentItem.From(Recording("broken_video", createdUtc));

            Assert.Null(item.StartedLocal);
            Assert.Equal("Undated", item.DateText);

            // The specific failure this issue was raised over: an unknown date quietly becoming
            // today's, which is indistinguishable from a recording that really was made today.
            Assert.DoesNotContain(RecentItem.DateLabel(DateTime.Now), item.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(DateTime.Now.ToString("MMM d, yyyy"), item.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void AnUndatedRecording_SortsLast_EvenBehindTheOldestDatedOne()
        {
            var undated = RecentItem.From(Recording("undated_video", null));
            var ancient = RecentItem.From(Recording("ancient_video", "2019-02-03T04:05:06.0000000Z"));
            var recent = RecentItem.From(Recording("recent_video", "2026-08-17T08:03:32.0000000Z"));

            Assert.Equal(new[] { "recent_video", "ancient_video", "undated_video" },
                InLibraryOrder(undated, recent, ancient).Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        [Fact]
        public void SeveralUndatedRecordings_StillProduceOneDefinedOrder()
        {
            var a = RecentItem.From(Recording("a_video", null));
            var b = RecentItem.From(Recording("b_video", "nonsense"));

            Assert.Equal(new[] { "b_video", "a_video" },
                InLibraryOrder(a, b).Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        /// <summary>
        /// The other half of criterion 6: the failure is LOGGED. Read from the IL rather than from a
        /// log file, because the app under test shares one log file with the user's own running
        /// AgentEyes - a test that tailed it would be reading someone else's writes. StartUtc has
        /// exactly one branch that returns "no usable date", so proving that method logs is proving
        /// that branch logs.
        /// </summary>
        [Fact]
        public void TheUndatedPath_LogsWhyTheRecordingHasNoDate()
        {
            var calls = CompiledCode.CallsIn(CompiledCode.AppAssembly, "AgentEyes.App.RecentItem::StartUtc");

            Assert.Contains("AgentEyes.Log::Warn", calls);
        }

        // ---- criterion 7: the card's date label is the recording start, local ----

        [Fact]
        public void DateText_IsTheRecordingStartInLocalTime()
        {
            const string createdUtc = "2026-08-14T14:44:00.0000000Z";
            var expected = DateTime.Parse(createdUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();

            var item = RecentItem.From(Recording("2026-08-14_104400_video", createdUtc));

            Assert.Equal(expected, item.StartedLocal);
            Assert.Equal($"{expected:MMM d, yyyy}  {expected:h:mm tt}", item.DateText);
        }

        [Fact]
        public void TheCardShowsThatDateLabel_AndNoOtherDate()
        {
            var item = RecentItem.From(Recording("2026-08-14_104400_video", "2026-08-14T14:44:00.0000000Z"));

            // Detail is the line the card renders under the title; the date label leads it.
            Assert.StartsWith(item.DateText, item.Detail, StringComparison.Ordinal);

            // And it is stated absolutely - no "today", which is a date that changes overnight
            // without the recording changing at all.
            Assert.DoesNotContain("today", item.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Yesterday", item.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DateLabel_SaysUndated_RatherThanGuessing()
        {
            Assert.Equal("Undated", RecentItem.DateLabel(null));
        }

        // ---- review finding 3: a card whose start time changes has to MOVE ---

        /// <summary>
        /// The card that arrives undated because its manifest could not be read, and gains a real
        /// start time when a later pass reads it. It is placed LAST while it is undated, and the new
        /// date says it belongs first - but a ListCollectionView is not watching the field its
        /// CustomSort reads, so it stays where it was until the view is refreshed.
        ///
        /// This test drives the real WPF view with the real comparer, and asserts both halves: that
        /// RefreshNaming REPORTS the moved sort key, and that acting on that report is what actually
        /// moves the card. The middle assertion is the defect itself, pinned so it cannot be mistaken
        /// for something the view does on its own.
        ///
        /// Its limit, stated rather than hidden: this proves the card that is REFRESHED moves. A row
        /// captured before an await that a reload has since detached is failure mode 5 of issue #3
        /// and is covered in LibraryCoherenceTests, not here.
        /// </summary>
        [Fact]
        public void ARecordingThatGainsAStartTime_MovesToItsPlace_WhenTheViewIsResorted()
        {
            string undated = Recording("z_video", null);
            var library = new LibraryCoherence();
            library.ApplySnapshot(library.BeginSnapshot(), new List<RecentItem>
            {
                RecentItem.From(Recording("a_video", "2019-02-03T04:05:06.0000000Z")),
                RecentItem.From(undated),
            });
            var late = library.Find(undated)!;
            var view = new System.Windows.Data.ListCollectionView(library.Rows)
            {
                CustomSort = RecentItem.NewestFirst,
            };

            Assert.Equal(new[] { "a_video", "z_video" }, Rendered(view));

            // A later pass reads the manifest successfully - the card is now the NEWEST recording.
            Recording("z_video", "2026-08-17T08:03:32.0000000Z");
            Assert.True(late.RefreshNaming(), "RefreshNaming did not report that the sort key moved.");

            // The view has not noticed; nothing told it. This is the defect, not the fix.
            Assert.Equal(new[] { "a_video", "z_video" }, Rendered(view));

            // What ResortLibrary does, and the card lands where its date says it belongs.
            view.Refresh();
            Assert.Equal(new[] { "z_video", "a_video" }, Rendered(view));
        }

        [Fact]
        public void RefreshNaming_ReportsNoMove_WhenTheStartTimeIsUnchanged()
        {
            var item = RecentItem.From(Recording("a_video", "2026-08-17T08:03:32.0000000Z"));

            Assert.False(item.RefreshNaming());
        }

        private static string[] Rendered(System.Windows.Data.ListCollectionView view) =>
            view.Cast<RecentItem>().Select(i => Path.GetFileName(i.Dir)).ToArray();

        // ---- review finding 5: a full reload is ONE notification, not n ------

        /// <summary>
        /// Loading the library's rows raises a single Reset. Every Add used to raise its own
        /// CollectionChanged, and the handler on it re-walks the collection to total the AI spend -
        /// O(n squared) UI-thread work for a list with no cap.
        ///
        /// Issue #3 moved the wholesale swap behind the coherence model, and the coalescing came with
        /// it: the notifications raised while a snapshot is being merged are held back and settled
        /// once. This is the same claim measured on the route that now does it.
        /// </summary>
        [Fact]
        public void LoadingEveryRow_RaisesOneResetRatherThanOneEventPerRow()
        {
            var library = new LibraryCoherence();
            var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            library.Rows.CollectionChanged += (_, e) => events.Add(e.Action);

            library.ApplySnapshot(library.BeginSnapshot(), Enumerable.Range(0, 60)
                .Select(i => RecentItem.From(Recording($"r{i:D2}_video", "2026-08-17T08:03:32.0000000Z")))
                .ToList());

            Assert.Equal(60, library.Rows.Count);
            Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Reset }, events);
        }

        // ---- criterion 2: the loader's ORDER, read from what it returns ------

        /// <summary>
        /// The order of the library's snapshot, asserted from the list the loader actually produces.
        ///
        /// This replaces a source-text guard that required the words "list.Sort(RecentItem.NewestFirst)"
        /// and then searched the rest of the method for re-ordering verbs. The independent review of
        /// PR #179 walked straight past it by writing "Permute(list);" on the next line - a call that
        /// re-orders the list and matches none of the verbs. A test that reads the RETURNED ORDER
        /// cannot be evaded that way: whatever the method does last is what it is judged on.
        /// </summary>
        [Fact]
        public void TheLibrarySnapshot_IsNewestFirst_WhateverTheFolderNamesSay()
        {
            Recording("2026-12-31_235959_video", "2025-03-02T09:00:00.0000000Z");   // newest name, oldest start
            Recording("2026-01-01_000000_video", "2026-08-14T14:44:00.0000000Z");   // oldest name, newest start
            Recording("2026-06-06_120000_video", "2026-07-07T23:59:00.0000000Z");

            var snapshot = LibrarySnapshot.NewestFirst(_root);

            Assert.Equal(
                new[] { "2026-01-01_000000_video", "2026-06-06_120000_video", "2026-12-31_235959_video" },
                snapshot.Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        [Fact]
        public void TheLibrarySnapshot_PutsUndatedRecordingsLast()
        {
            Recording("a_video", null);
            Recording("b_video", "2019-02-03T04:05:06.0000000Z");
            Recording("c_video", "2026-08-17T08:03:32.0000000Z");

            Assert.Equal(new[] { "c_video", "b_video", "a_video" },
                LibrarySnapshot.NewestFirst(_root).Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        [Fact]
        public void TheLibrarySnapshot_IgnoresAFolderWithNoManifest()
        {
            Recording("real_video", "2026-08-17T08:03:32.0000000Z");
            Directory.CreateDirectory(Path.Combine(_root, "not-a-recording"));

            Assert.Equal(new[] { "real_video" },
                LibrarySnapshot.NewestFirst(_root).Select(i => Path.GetFileName(i.Dir)).ToArray());
        }

        [Fact]
        public void TheLibrarySnapshot_OfARootThatWasNeverCreated_IsEmpty()
        {
            Assert.Empty(LibrarySnapshot.NewestFirst(Path.Combine(_root, "no-such-root")));
        }

        /// <summary>The loader gets its order from that method rather than building one of its own.
        /// Read from IL: without this, the behavioural tests above could stay green while the window
        /// quietly went back to enumerating and ordering the directories itself.</summary>
        [Fact]
        public void TheLoader_TakesItsOrderFromTheLibrarySnapshot()
        {
            Assert.Contains("AgentEyes.App.LibrarySnapshot::NewestFirst",
                CallsFrom("AgentEyes.App.MainWindow::LoadRecent"));
        }

        // ---- criterion 5: the date path reaches no other source of "when" ----

        /// <summary>Every filesystem timestamp API. The library's day headers used to fall back to
        /// Directory.GetCreationTime, which is the date the FOLDER was made - a copy, a restore or a
        /// repair rewrites it, and it was never the date of the recording.</summary>
        private static readonly string[] FilesystemTimestampApis =
        {
            "System.IO.Directory::GetCreationTime",   "System.IO.Directory::GetCreationTimeUtc",
            "System.IO.Directory::GetLastWriteTime",  "System.IO.Directory::GetLastWriteTimeUtc",
            "System.IO.Directory::GetLastAccessTime", "System.IO.Directory::GetLastAccessTimeUtc",
            "System.IO.File::GetCreationTime",        "System.IO.File::GetCreationTimeUtc",
            "System.IO.File::GetLastWriteTime",       "System.IO.File::GetLastWriteTimeUtc",
            "System.IO.File::GetLastAccessTime",      "System.IO.File::GetLastAccessTimeUtc",
            "System.IO.FileSystemInfo::get_CreationTime",   "System.IO.FileSystemInfo::get_CreationTimeUtc",
            "System.IO.FileSystemInfo::get_LastWriteTime",  "System.IO.FileSystemInfo::get_LastWriteTimeUtc",
            "System.IO.FileSystemInfo::get_LastAccessTime", "System.IO.FileSystemInfo::get_LastAccessTimeUtc",
        };

        /// <summary>Every way of asking what time it is now.</summary>
        private static readonly string[] ClockApis =
        {
            "System.DateTime::get_Now", "System.DateTime::get_UtcNow", "System.DateTime::get_Today",
            "System.DateTimeOffset::get_Now", "System.DateTimeOffset::get_UtcNow",
        };

        private static bool IsAnotherSourceOfWhen(string callee) =>
            FilesystemTimestampApis.Contains(callee) || ClockApis.Contains(callee);

        /// <summary>
        /// SEEDS for the card's date path: deriving a recording's date, refreshing it, labelling it,
        /// ordering by it, and building the loader's snapshot of cards. The scan follows their calls,
        /// so a helper reached from any of them is covered WITHOUT being named here.
        ///
        /// That transitivity is the round-2 review's finding, answered. The previous scan inventoried
        /// a list of method names, and the reviewer defeated it by having RecentItem.From call a new
        /// LibraryDateFallback.For(dir) that read Directory.GetCreationTime: the offending method was
        /// not on the list, so both date guards stayed green over a live filesystem fallback.
        /// </summary>
        private static readonly string[] CardDatePathSeeds =
        {
            "RecentItem::From", "RecentItem::StartUtc", "RecentItem::RefreshNaming",
            "RecentItem::DateLabel", "RecentItem::get_StartedLocal", "RecentItem::get_DateText",
            "NewestFirstComparer::Compare", "LibrarySnapshot::NewestFirst",
        };

        /// <summary>
        /// The MainWindow routes that make or move a library row. They are checked for DIRECT calls
        /// only - deliberately, and this is the honest limit of this second guard: these are window
        /// event handlers that reach most of the application (recording, packaging, DevThrottle, the
        /// capture gallery), so following their calls would sweep in every clock in the product and
        /// report a defect for every legitimate one. What is claimed here is exactly what is
        /// measured: none of these methods CONTAINS a filesystem-timestamp or clock call itself.
        ///
        /// The card's own date path - where a date is actually derived - is covered transitively by
        /// <see cref="CardDatePathSeeds"/> above, which is the path a fallback would have to be
        /// reachable from to change what the library displays.
        /// </summary>
        private static readonly string[] LibraryRouteMethods =
        {
            "MainWindow::LoadRecent", "MainWindow::Record_Click", "MainWindow::StopAsync",
            "MainWindow::PackageDirAsync", "MainWindow::ResortLibrary",
        };

        /// <summary>Every method reachable from the card's date path inside one assembly.</summary>
        private static IReadOnlyList<string> CardDatePath(string assembly, string ns) =>
            CompiledCode.Reachable(assembly, CardDatePathSeeds.Select(seed => ns + seed));

        /// <summary>Every other-source-of-"when" call anywhere in the card's date path, transitively.</summary>
        private static IReadOnlyList<CompiledCode.CallSite> OtherDateSourcesReachableFrom(string assembly, string ns)
        {
            var path = new HashSet<string>(CardDatePath(assembly, ns), StringComparer.Ordinal);
            return CompiledCode.CallSites(assembly, IsAnotherSourceOfWhen)
                .Where(site => path.Contains(site.Method))
                .ToList();
        }

        [Fact]
        public void NothingReachableFromTheCardsDatePath_ReadsAClockOrAFilesystemTimestamp()
        {
            var offenders = OtherDateSourcesReachableFrom(CompiledCode.AppAssembly, "AgentEyes.App.");

            Assert.True(offenders.Count == 0,
                "Something the Library reaches while deriving a recording's date reads a filesystem "
                + "timestamp or asks the clock what time it is. manifest.json's CreatedUtc is the only "
                + "date a recording has (issue #178):" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        /// <summary>
        /// THE NEGATIVE CONTROL for the transitive scan, and the direct answer to the round-2 review
        /// finding. LibraryDefectDecoys compiles the reviewer's exact attack: RecentItem.From calls
        /// LibraryDateFallback.For, and THAT reads Directory.GetCreationTime. The scan is pointed at
        /// the test assembly and must report the helper - which no list of method names can do.
        /// </summary>
        [Fact]
        public void TheTransitiveDateScan_ReportsAFallbackHiddenBehindAHelper()
        {
            var reported = OtherDateSourcesReachableFrom(CompiledCode.TestAssembly, "AgentEyes.Tests.LibraryDefects.");

            Assert.Contains(reported, site => site.Method == "AgentEyes.Tests.LibraryDefects.LibraryDateFallback::For");

            // ...and the seeds' own direct defects are still reported.
            foreach (string route in new[]
                     {
                         "AgentEyes.Tests.LibraryDefects.RecentItem::StartUtc",
                         "AgentEyes.Tests.LibraryDefects.NewestFirstComparer::Compare",
                         "AgentEyes.Tests.LibraryDefects.LibrarySnapshot::NewestFirst",
                     })
                Assert.True(reported.Any(site => site.Method == route),
                    $"The transitive date scan does not report the compiled defect in '{route}':"
                    + Environment.NewLine + CompiledCode.Describe(reported));
        }

        /// <summary>Proves the transitive scan really did follow the call rather than happening to
        /// include the helper for some other reason: the helper is not a seed, and a DIRECT scan of
        /// the seeds alone cannot see it.</summary>
        [Fact]
        public void TheHelperTheTransitiveScanCatches_IsInvisibleToADirectScan()
        {
            var direct = CompiledCode.CallSites(CompiledCode.TestAssembly, IsAnotherSourceOfWhen)
                .Where(site => CardDatePathSeeds.Any(
                    seed => site.Method == "AgentEyes.Tests.LibraryDefects." + seed))
                .ToList();

            Assert.DoesNotContain(direct, site => site.Callee == "System.IO.Directory::GetCreationTime");
            Assert.Contains(CardDatePath(CompiledCode.TestAssembly, "AgentEyes.Tests.LibraryDefects."),
                method => method == "AgentEyes.Tests.LibraryDefects.LibraryDateFallback::For");
        }

        /// <summary>
        /// The window's library routes contain no DIRECT clock or filesystem-timestamp call. Narrow
        /// by design - see <see cref="LibraryRouteMethods"/> for why following these calls would
        /// report every legitimate clock in the product.
        /// </summary>
        [Fact]
        public void TheWindowsLibraryRoutes_ContainNoDirectClockOrFilesystemTimestampCall()
        {
            var offenders = CompiledCode.CallSites(CompiledCode.AppAssembly, IsAnotherSourceOfWhen)
                .Where(site => LibraryRouteMethods.Any(
                    name => site.Method.EndsWith(name, StringComparison.Ordinal)))
                .ToList();

            Assert.True(offenders.Count == 0,
                "A Library route in MainWindow dates a recording from the filesystem or the clock. A "
                + "recording's date is the time it was RECORDED (issue #178):" + Environment.NewLine
                + CompiledCode.Describe(offenders));
        }

        /// <summary>The negative control for the direct route scan: each guarded route has a decoy
        /// compiled under the same name, and the scan must report every one of them.</summary>
        [Fact]
        public void TheDirectRouteScan_ReportsAClockOrFilesystemFallbackOnEveryGuardedRoute()
        {
            var reported = CompiledCode.CallSites(CompiledCode.TestAssembly, IsAnotherSourceOfWhen)
                .Where(site => LibraryRouteMethods.Any(
                    name => site.Method == "AgentEyes.Tests.LibraryDefects." + name))
                .ToList();

            foreach (string route in LibraryRouteMethods)
                Assert.True(reported.Any(site => site.Method == "AgentEyes.Tests.LibraryDefects." + route),
                    $"The direct route scan does not report the compiled defect in '{route}'. A guard "
                    + "that cannot see the defect in a decoy cannot see it in the product either:"
                    + Environment.NewLine + CompiledCode.Describe(reported));
        }

        /// <summary>
        /// Proves both scans are pointed at real code. Both pass by finding nothing, so they would
        /// pass just as happily against a renamed method - this asserts every seed and every guarded
        /// route is actually in the binary they read.
        /// </summary>
        [Fact]
        public void TheDateScans_AreLookingAtMethodsThatExist()
        {
            var methods = CompiledCode.MethodNames(CompiledCode.AppAssembly);

            foreach (string method in CardDatePathSeeds.Concat(LibraryRouteMethods)
                         .Select(name => "AgentEyes.App." + name))
                Assert.True(methods.Contains(method, StringComparer.Ordinal),
                    $"'{method}' is not in AgentEyesApp.dll, so a date scan is filtering for a method "
                    + "that no longer exists and would pass by finding nothing.");
        }

        // ---- criteria 1 + 2: the LIBRARY is not grouped, and sorts explicitly ----

        /// <summary>
        /// The Library's list declares no grouping of any kind: no GroupStyle child, no
        /// GroupStyleSelector, no GroupDescription.
        ///
        /// Scoped to the RecentList element on purpose (round-2 review): the previous version banned
        /// grouping anywhere in MainWindow.xaml, which would fail a legitimate grouped view built for
        /// some other feature later. A guard that punishes unrelated work is a guard someone deletes.
        /// </summary>
        [Fact]
        public void TheRecentListElement_DeclaresNoGrouping()
        {
            string list = RecentListElement();

            // Proves the extraction found the real element rather than an empty string.
            Assert.Contains(@"x:Name=""RecentList""", list, StringComparison.Ordinal);

            Assert.False(DeclaresGrouping(list),
                "MainWindow.xaml declares grouping on RecentList. The Library is one flat list "
                + "(issue #178) - day headers are what rendered each group's cards under another "
                + "group's header.");
        }

        /// <summary>The negative control for the markup guard: it reports grouping put back on the
        /// element, as a child and as an attribute.</summary>
        [Fact]
        public void TheMarkupGroupingScan_ReportsGroupingPutBackOnRecentList()
        {
            string list = RecentListElement();

            Assert.True(DeclaresGrouping(list + Environment.NewLine
                + "<ListBox.GroupStyle><GroupStyle/></ListBox.GroupStyle>"));
            Assert.True(DeclaresGrouping(list.Replace(@"<ListBox x:Name=""RecentList""",
                @"<ListBox x:Name=""RecentList"" GroupStyleSelector=""{StaticResource Day}""",
                StringComparison.Ordinal)));

            // ...and a comment about grouping is still just a comment.
            Assert.False(DeclaresGrouping(WithoutXamlComments("<!-- do not add a GroupStyle back -->")));
        }

        /// <summary>
        /// The code side of the same claim: no method that HANDLES the Library groups it. "Handles
        /// the Library" is read from the IL as touching the Library's rows (_recent) or its list
        /// control (RecentList), which is what makes this narrower than the version round 2 rejected
        /// (grouping banned anywhere in the app assembly) and wider than the version round 1 rejected
        /// (the constructor only). ApplyLibraryMode, a Loaded handler, or any other helper that
        /// handles the Library is covered.
        ///
        /// Since issue #2 the scan also FOLLOWS THE CALLS such a method makes, transitively within
        /// the assembly - the round-3 gate proved the old one-body scan blind to grouping delegated
        /// to a helper that takes the view as an argument and never names a Library field - and,
        /// since the issue #2 fix pass, follows VIRTUAL AND INTERFACE DISPATCH conservatively: a
        /// call through an in-assembly interface or virtual method reaches every in-assembly
        /// implementation and override of it, which is the round-1 gate's construction (an
        /// implementation instantiated through an interface, Configure(view) called through the
        /// interface). The remaining limits are stated on <see cref="LibraryGroupingIn"/>.
        /// </summary>
        [Fact]
        public void NoMethodThatHandlesTheLibrary_GroupsIt()
        {
            var offenders = LibraryGroupingIn(CompiledCode.AppAssembly);

            Assert.True(offenders.Count == 0,
                "A method that handles the Library groups its collection view. The Library is one "
                + "flat list (issue #178):" + Environment.NewLine + CompiledCode.Describe(offenders));
        }

        /// <summary>
        /// The negative control, and the NARROWNESS control in the same test. The scan must report
        /// grouping added by a method that handles the Library - outside the constructor, through the
        /// items control, through ICollectionView, (issue #2, item 1) DELEGATED to a helper that
        /// takes the view as an argument and never names a Library field, which is the exact shape
        /// the round-3 gate used to restore real day groups with every guard green, and (issue #2,
        /// fix pass) hidden behind INTERFACE DISPATCH - the handler instantiates an in-assembly
        /// implementation through an interface and calls Configure(view) through that interface, so
        /// the call site names only the abstract method and a body-only walk never reaches the
        /// implementation. And it must stay silent about grouping that has nothing to do with the
        /// Library - including an unrelated feature's own interface-dispatched configurer - which is
        /// the false alarm that would eventually get this guard deleted.
        /// </summary>
        [Fact]
        public void TheGroupingScan_ReportsLibraryGrouping_AndIgnoresEveryoneElses()
        {
            var reported = LibraryGroupingIn(CompiledCode.TestAssembly);

            foreach (string route in new[]
                     {
                         "AgentEyes.Tests.LibraryDefects.LibraryWindow::ApplyLibraryMode",
                         "AgentEyes.Tests.LibraryDefects.LibraryWindow::OnLoaded",
                         "AgentEyes.Tests.LibraryDefects.LibraryWindow::ConfigureLibraryView",
                         "AgentEyes.Tests.LibraryDefects.DayGroupConfigurer::Configure",
                         // Round-2 review, finding 1: the implementation the interface hides is
                         // INHERITED - the InterfaceImpl row is on the derived type, the body on
                         // its base, and only a base-chain-aware dispatch map connects them.
                         "AgentEyes.Tests.LibraryDefects.DayGroupConfigurerBase::Configure",
                         // Round-2 review, finding 2: grouping hidden in a static constructor,
                         // which no call instruction targets - the runtime invokes it, so the
                         // walk must add implicit-invocation edges for touched types.
                         "AgentEyes.Tests.LibraryDefects.CctorDayGroupConfigurer::.cctor",
                         // Round 3: one implementation per remaining dispatch shape - the gate's
                         // inherited interface declaration, explicit implementation, generic
                         // interface and generic method, default interface method (both ends),
                         // virtual and generic-virtual through base references, delegate over an
                         // interface method, and a static abstract member.
                         "AgentEyes.Tests.LibraryDefects.InheritedDeclarationDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.ExplicitDayGroupConfigurer::AgentEyes.Tests.LibraryDefects.IExplicitBaseConfigurer.Configure",
                         "AgentEyes.Tests.LibraryDefects.GenericDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.GenericMethodDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.IDefaultViewConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.DimOverrideDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.OverrideDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.GenericOverrideDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.DelegateDayGroupConfigurer::Configure",
                         "AgentEyes.Tests.LibraryDefects.StaticDayGroupConfigurer::Configure",
                     })
                Assert.True(reported.Any(site => site.Method == route),
                    $"The grouping scan does not report the compiled grouping in '{route}':"
                    + Environment.NewLine + CompiledCode.Describe(reported));

            Assert.DoesNotContain(reported,
                site => site.Method.StartsWith("AgentEyes.Tests.LibraryDefects.Grouping::", StringComparison.Ordinal));
            Assert.DoesNotContain(reported,
                site => site.Method.StartsWith("AgentEyes.Tests.LibraryDefects.PanelGroupConfigurer::", StringComparison.Ordinal));
        }

        /// <summary>
        /// The issue #2 FIX-PASS regression, at the level of the instrument itself. Seeded with the
        /// handler alone, the walk must reach <c>DayGroupConfigurer.Configure</c> - a method no body
        /// in the assembly calls by its concrete type; the only route to it is the dispatch edge
        /// behind <c>ILibraryViewConfigurer.Configure</c>. Under the pre-fix walk (calls into bodies
        /// only) this assertion fails, which is exactly how the round-1 gate's attack stayed
        /// invisible. And the fan-out must stay per-declaration: the same-signature, same-method-name
        /// implementation of the UNRELATED <c>IPanelConfigurer</c> must NOT be dragged in, or the
        /// conservative direction would fail legitimate future work.
        /// </summary>
        [Fact]
        public void TheReachabilityWalk_FollowsInterfaceDispatch_ToTheInAssemblyImplementation()
        {
            var reached = CompiledCode.Reachable(CompiledCode.TestAssembly,
                new[] { "AgentEyes.Tests.LibraryDefects.LibraryWindow::ApplyLibraryModeThroughAnInterface" });

            Assert.Contains("AgentEyes.Tests.LibraryDefects.DayGroupConfigurer::Configure", reached);
            Assert.DoesNotContain("AgentEyes.Tests.LibraryDefects.PanelGroupConfigurer::Configure", reached);
        }

        /// <summary>
        /// Round-2 review, finding 1: the implementation the interface hides is INHERITED. The
        /// derived type carries the InterfaceImpl row and no Configure of its own; the body lives
        /// on a base class that never names the interface. A dispatch map that matches interface
        /// methods only against the implementing type's OWN methods has no edge here - empirically
        /// demonstrated fail-open - so the map must search the implementing type's in-assembly
        /// base chain for the body.
        /// </summary>
        [Fact]
        public void TheReachabilityWalk_FollowsInterfaceDispatch_ToAnInheritedImplementation()
        {
            var reached = CompiledCode.Reachable(CompiledCode.TestAssembly,
                new[] { "AgentEyes.Tests.LibraryDefects.LibraryWindow::ApplyLibraryModeThroughAnInheritedImplementation" });

            Assert.Contains("AgentEyes.Tests.LibraryDefects.DayGroupConfigurerBase::Configure", reached);
            Assert.DoesNotContain("AgentEyes.Tests.LibraryDefects.PanelGroupConfigurer::Configure", reached);
        }

        /// <summary>
        /// Round-2 review, finding 2: a STATIC CONSTRUCTOR is invoked by the runtime, never by a
        /// call instruction, so it can only be an IMPLICIT edge: touching any member of a type
        /// makes its .cctor (and its finalizer) reachable. Without that edge, work hidden in a
        /// .cctor passes every reachability guard silently.
        /// </summary>
        [Fact]
        public void TheReachabilityWalk_ReachesTheStaticConstructor_OfATouchedType()
        {
            var reached = CompiledCode.Reachable(CompiledCode.TestAssembly,
                new[] { "AgentEyes.Tests.LibraryDefects.LibraryWindow::ApplyLibraryModeThroughAStaticConstructor" });

            Assert.Contains("AgentEyes.Tests.LibraryDefects.CctorDayGroupConfigurer::.cctor", reached);
            // ...and an untouched type's cctor is not dragged in: implicit edges are per touched
            // type, not a blanket sweep.
            Assert.DoesNotContain("AgentEyes.Tests.LibraryDefects.PanelGroupConfigurer::Configure", reached);
        }

        // ---- issue #2, round 3: the dispatch-shape inventory, one regression each ----
        // The round-2 gate found dispatch shapes seriatim, so round 3 enumerates them
        // systematically: every shape below is pinned by a walk-level regression, and what none
        // of them can cover is stated verbatim in the limits on LibraryGroupingIn. The decoys
        // live in LibraryDefectDecoys.cs, one handler per shape.

        /// <summary>Reaches into the decoy assembly's walk from one LibraryWindow handler and
        /// asserts the implementation only that shape's dispatch can reach IS reached - and that
        /// the unrelated IPanelConfigurer implementation is NOT, so the conservative fan-out
        /// stays per-declaration for every shape.</summary>
        private static void AssertShapeReached(string handler, string implementation)
        {
            var reached = CompiledCode.Reachable(CompiledCode.TestAssembly,
                new[] { "AgentEyes.Tests.LibraryDefects.LibraryWindow::" + handler });

            Assert.Contains("AgentEyes.Tests.LibraryDefects." + implementation, reached);
            Assert.DoesNotContain("AgentEyes.Tests.LibraryDefects.PanelGroupConfigurer::Configure", reached);
        }

        /// <summary>
        /// The round-2 GATE's construction (issue #2, round 3): IChildViewConfigurer :
        /// IBaseViewConfigurer, the class implements the child, the call goes through the BASE.
        ///
        /// Honesty note about this pin: Roslyn FLATTENS a class's InterfaceImpl rows (the class
        /// here is emitted with rows for both interfaces - verified empirically), so this
        /// C#-compiled construction was reachable even before the interface-inheritance traversal
        /// existed, through the direct IBase row. It is kept as the permanent pin of the gate's
        /// exact C# shape; the regression that actually exercises the traversal - and that was
        /// RED before it existed - is the hand-written-metadata one below, where no flattened row
        /// can rescue the map.
        /// </summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAnInheritedInterfaceDeclaration() =>
            AssertShapeReached("ApplyLibraryModeThroughAnInheritedInterfaceDeclaration",
                "InheritedDeclarationDayGroupConfigurer::Configure");

        /// <summary>
        /// The round-3 fix, exercised for real: metadata WITHOUT compiler flattening. The
        /// hand-emitted assembly's Impl lists ONLY IChild; the call is through IBase::Configure.
        /// Under the round-2 map (interface methods read from the row-named interface only) there
        /// is no edge and this test FAILS - demonstrated red-first on this branch. Only the full
        /// interface-inheritance-graph traversal connects the two.
        /// </summary>
        [Fact]
        public void TheDispatchMap_TraversesTheInterfaceInheritanceGraph_WithoutCompilerFlattening()
        {
            string probe = HandWrittenDispatchAssembly.Emit();
            try
            {
                // Instrument check: the fixture still poses the hazard. If Impl's rows ever came
                // back flattened, this test would pass through the direct row and pin nothing.
                Assert.Equal(new[] { "IChild" },
                    HandWrittenDispatchAssembly.DirectInterfaceRowsOf(probe, "Impl"));

                var reached = CompiledCode.Reachable(probe, new[] { "Probe.Handler::Run" });

                Assert.Contains("Probe.Impl::Configure", reached);
            }
            finally
            {
                File.Delete(probe);
            }
        }

        /// <summary>
        /// The one OTHER IL instruction that transfers control to a method token: <c>jmp</c>
        /// (ECMA-335 III.3.37). C# never emits it, so it can only be pinned from hand-written
        /// metadata. Before round 3 the token collector did not read jmp operands - demonstrated
        /// red-first on this branch - which was a silent gap in the "every call shape" claim.
        /// </summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAJmpInstruction()
        {
            string probe = HandWrittenDispatchAssembly.Emit();
            try
            {
                var reached = CompiledCode.Reachable(probe, new[] { "Probe.Handler::RunJmp" });

                Assert.Contains("Probe.Impl::Configure", reached);
            }
            finally
            {
                File.Delete(probe);
            }
        }

        /// <summary>EXPLICIT interface implementation - and of an INHERITED declaration at that:
        /// the only metadata connecting the private body to IExplicitBaseConfigurer::Configure is
        /// its MethodImpl row. The compiled body name is the dotted interface name.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAnExplicitInterfaceImplementation() =>
            AssertShapeReached("ApplyLibraryModeThroughAnExplicitImplementation",
                "ExplicitDayGroupConfigurer::AgentEyes.Tests.LibraryDefects.IExplicitBaseConfigurer.Configure");

        /// <summary>GENERIC INTERFACE INSTANTIATION: callee token parent and InterfaceImpl row are
        /// both TypeSpecs and must fold onto the same open generic type.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAGenericInterfaceInstantiation() =>
            AssertShapeReached("ApplyLibraryModeThroughAGenericInterface",
                "GenericDayGroupConfigurer::Configure");

        /// <summary>CONSTRUCTED GENERIC METHOD: the call site's MethodSpec must resolve onto the
        /// open generic declaration before the dispatch edge can be looked up.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAConstructedGenericMethod() =>
            AssertShapeReached("ApplyLibraryModeThroughAGenericMethod",
                "GenericMethodDayGroupConfigurer::Configure");

        /// <summary>DEFAULT INTERFACE METHOD: both ends must be reached - the interface's own
        /// default body directly (the callee HAS IL), and the class override by dispatch.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsADefaultInterfaceMethod_AndItsOverride()
        {
            AssertShapeReached("ApplyLibraryModeThroughADefaultInterfaceMethod",
                "IDefaultViewConfigurer::Configure");
            AssertShapeReached("ApplyLibraryModeThroughADefaultInterfaceMethod",
                "DimOverrideDayGroupConfigurer::Configure");
        }

        /// <summary>VIRTUAL CALL THROUGH A BASE-CLASS REFERENCE: the callee is the base's benign
        /// virtual method; only the override edge reaches the grouping body.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAVirtualCallThroughABaseReference() =>
            AssertShapeReached("ApplyLibraryModeThroughAVirtualBaseReference",
                "OverrideDayGroupConfigurer::Configure");

        /// <summary>...and through a GENERIC base-class reference, where the callee parent and the
        /// derived type's BaseType are both TypeSpecs.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAVirtualCallThroughAGenericBaseReference() =>
            AssertShapeReached("ApplyLibraryModeThroughAGenericBaseReference",
                "GenericOverrideDayGroupConfigurer::Configure");

        /// <summary>DELEGATE BUILT FROM AN INTERFACE METHOD GROUP: no call instruction ever
        /// targets the implementation - the ldvirtftn token names the interface declaration, and
        /// the dispatch fan-out is the only route to the body.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsADelegateBuiltFromAnInterfaceMethod() =>
            AssertShapeReached("ApplyLibraryModeThroughADelegate",
                "DelegateDayGroupConfigurer::Configure");

        /// <summary>STATIC ABSTRACT INTERFACE MEMBER: the constrained call names the interface
        /// declaration; the implementing static method is reached by the same name-matched
        /// InterfaceImpl edge as an instance implementation.</summary>
        [Fact]
        public void TheReachabilityWalk_FollowsAStaticAbstractInterfaceMember() =>
            AssertShapeReached("ApplyLibraryModeThroughAStaticAbstract",
                "StaticDayGroupConfigurer::Configure");

        [Fact]
        public void TheLibraryView_SortsNewestFirstExplicitly()
        {
            // The order is DECLARED on the view, not inherited from whatever order the loader
            // happened to append in.
            Assert.Contains("CustomSort = RecentItem.NewestFirst", Constructor(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A card whose start time changed has moved in the sort order, and a collection view does
        /// not re-sort because a field changed - so every RefreshNaming call site must act on what it
        /// reports (issue #178, review finding 3).
        ///
        /// Issue #3 moved that call site: RefreshNaming is now reached through
        /// <c>LibraryCoherence.Refresh</c>, which is the only route the window has to it, so this
        /// guard reads the file that owns the call. It is a LITERAL STRING MATCH over that source and
        /// claims nothing more: it sees that the one call site keeps the answer in <c>moved</c> and
        /// raises <c>SortKeyChanged</c> on it. It cannot see a second route added through reflection
        /// or a delegate, and the fail-closed half below is what stops it certifying a file that no
        /// longer calls RefreshNaming at all.
        /// </summary>
        [Fact]
        public void EveryRefreshNamingCallSite_ReSortsTheLibraryWhenTheStartTimeMoved()
        {
            var offenders = RefreshNamingCallSitesThatIgnoreTheResort(RepoSource.Read(Coherence));

            Assert.True(offenders.Count == 0,
                "A RefreshNaming call site ignores the fact that the recording's start time changed, "
                + "so the card keeps a position the new date does not justify (issue #178):"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>The negative control: a bare RefreshNaming() call is reported.</summary>
        [Fact]
        public void TheResortGuard_ReportsARefreshThatDoesNotResort()
        {
            string code = RepoSource.Read(Coherence);

            Assert.NotEmpty(RefreshNamingCallSitesThatIgnoreTheResort(
                code.Replace("bool moved = row.RefreshNaming();", "row.RefreshNaming();",
                    StringComparison.Ordinal)));
        }

        /// <summary>
        /// One apply, ONE total (issue #2, item 3). A reload's apply settles as one coalesced
        /// collection event, and the constructor's CollectionChanged handler re-totals the AI spend
        /// on it - so the loader itself may re-total ONLY when the apply raised no event at all
        /// (values adopted into existing rows change the total without one). This is a literal
        /// source match on that one guarded call, the same instrument as the resort guard above,
        /// and it claims no more: it cannot see a second total added through another handler or
        /// another method, and the fail-closed extraction below stops it certifying a loader that
        /// no longer settles the empty state and the total anywhere.
        /// </summary>
        [Fact]
        public void TheLoader_RetotalsTheLibrary_OnlyWhenTheApplyRaisedNoEvent()
        {
            var offenders = UnguardedRetotalsIn(
                RepoSource.MethodBody(RepoSource.Read(CodeBehind), "private async void LoadRecent()"));

            Assert.True(offenders.Count == 0,
                "LoadRecent re-totals the Library unconditionally, so a changing reload walks the "
                + "collection twice per apply - once from the CollectionChanged handler and once "
                + "here - despite the coalesced Reset existing to make it once (issue #2):"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>The negative control: an unconditional UpdateEmptyState() in the loader is
        /// reported.</summary>
        [Fact]
        public void TheOnceGuard_ReportsAnUnconditionalRetotal()
        {
            string body = RepoSource.MethodBody(RepoSource.Read(CodeBehind), "private async void LoadRecent()");

            Assert.NotEmpty(UnguardedRetotalsIn(
                body.Replace("if (!notified) UpdateEmptyState();", "UpdateEmptyState();",
                    StringComparison.Ordinal)));
        }

        [Fact]
        public void TheDayGroupMachineryIsGone()
        {
            string code = RepoSource.Read(CodeBehind);

            foreach (string gone in new[] { "DayGroup", "DayGroupFor", "WhenLabel" })
                Assert.False(code.Contains(gone, StringComparison.Ordinal),
                    $"MainWindow.xaml.cs still carries '{gone}'. The day grouping and its relative "
                    + "\"today\" label were deleted, not disabled (issue #178).");
        }

        // ---- criterion 2's proof surface: every rendered row has an identity ----

        /// <summary>
        /// Criterion 2 is verified by comparing the RENDERED order against the manifests on disk, so
        /// each rendered row has to be identifiable. The round-2 review showed that title plus a
        /// minute-rounded date label is not an identity: the owner's own library holds three pairs of
        /// recordings whose rendered labels are identical, so a swapped pair compared equal. Each
        /// Library row therefore carries the recording's DIRECTORY NAME as its UI Automation id,
        /// which is unique by construction, and the QA comparator matches on that.
        ///
        /// The id is the LEAF and never the full path: the UI Automation tree is readable by every
        /// process on the desktop, so an absolute path there publishes the user's home directory out
        /// of a privacy-sensitive recorder. This guard therefore fails BOTH ways - if the setter is
        /// removed, and if the binding goes back to the full-path <c>Dir</c>.
        /// </summary>
        [Fact]
        public void EveryLibraryRow_CarriesTheRecordingDirectoryName_AsItsUiAutomationId()
        {
            string xaml = RepoSource.Read(Xaml);

            foreach (string style in new[] { "LibraryCardItem", "LibraryRowItem" })
            {
                int at = xaml.IndexOf($@"<Style x:Key=""{style}""", StringComparison.Ordinal);
                Assert.True(at >= 0, $"The Library container style '{style}' is gone from MainWindow.xaml.");

                int end = xaml.IndexOf("</Style>", at, StringComparison.Ordinal);
                string body = xaml.Substring(at, end - at);
                Assert.Contains(@"AutomationProperties.AutomationId"" Value=""{Binding DirName}""", body,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(@"AutomationProperties.AutomationId"" Value=""{Binding Dir}""", body,
                    StringComparison.Ordinal);
            }

            // ...and the list is actually given those containers.
            string applyMode = RepoSource.MethodBody(RepoSource.Read(CodeBehind), "private void ApplyLibraryMode()");
            Assert.Contains("LibraryCardItem", applyMode, StringComparison.Ordinal);
            Assert.Contains("LibraryRowItem", applyMode, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other half of the same guard, and the half a markup check cannot make: the property
        /// the rows bind to has to BE the leaf. Binding to a correctly-named property that returned
        /// the full path anyway would leak exactly what the rename was meant to stop, so this runs
        /// the property on a real recording folder and requires the value to be the folder name, to
        /// carry no path separator, and not to be the path itself.
        /// </summary>
        [Fact]
        public void DirName_IsTheRecordingFolderNameOnly_NeverTheAbsolutePath()
        {
            string dir = Recording("2026-08-17_080332_video", "2026-08-17T08:03:32.0000000Z");

            var item = RecentItem.From(dir);

            Assert.Equal("2026-08-17_080332_video", item.DirName);
            Assert.Equal(dir, item.Dir);                       // the full path is still there for the app's own use
            Assert.NotEqual(item.Dir, item.DirName);
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), item.DirName, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), item.DirName, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetDirectoryName(dir)!, item.DirName, StringComparison.OrdinalIgnoreCase);
        }

        // ---- extraction helpers (each throws rather than returning nothing) ---

        /// <summary>The whole RecentList element, opening tag through its closing tag - a GroupStyle
        /// is a CHILD element, so scoping to the opening tag alone would miss it.</summary>
        private static string RecentListElement()
        {
            string xaml = RepoSource.Read(Xaml);
            int start = xaml.IndexOf(@"<ListBox x:Name=""RecentList""", StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("The RecentList ListBox is not in MainWindow.xaml any more.");

            int end = xaml.IndexOf("</ListBox>", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("The RecentList ListBox is unterminated.");
            return xaml.Substring(start, end - start);
        }

        private static string Constructor() =>
            RepoSource.MethodBody(RepoSource.Read(CodeBehind),
                "internal MainWindow(RecordingService svc, Config cfg, Action showTests, RepairService repair)");

        /// <summary>Every call made by one method of the app, across every body the compiler split it
        /// into - an async method's state machine and its lambdas all fold back onto their declaring
        /// method, so LoadRecent alone is several compiled bodies. Throws rather than returning
        /// nothing when the method has vanished, so a rename cannot turn a Contains assertion into a
        /// check of an empty list.</summary>
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

        // ---- the guards' own logic, so a negative control can run them on attacked text ----

        /// <summary>Any way of declaring grouping in markup. Substring matching on purpose: the
        /// spelling can be an element, an attached property, a style selector or a binding, and every
        /// one of them names GroupStyle or GroupDescription somewhere.</summary>
        private static bool DeclaresGrouping(string markup) =>
            markup.Contains("GroupStyle", StringComparison.Ordinal)
            || markup.Contains("GroupDescription", StringComparison.Ordinal);

        private static string WithoutXamlComments(string markup) =>
            Regex.Replace(markup, "<!--.*?-->", "", RegexOptions.Singleline);

        /// <summary>Any call that groups a collection view, read from IL. The declaring type of
        /// GroupDescriptions differs between ICollectionView, CollectionView and ListCollectionView
        /// depending on how the call was written, so the member NAME is what is matched.</summary>
        private static bool IsGroupingApi(string callee) =>
            callee.Contains("GroupDescription", StringComparison.Ordinal)
            || callee.Contains("GroupStyle", StringComparison.Ordinal);

        /// <summary>The Library's own state: its rows and the list control that renders them.</summary>
        private static bool IsALibraryField(string field) =>
            field.EndsWith("::_recent", StringComparison.Ordinal)
            || field.EndsWith("::RecentList", StringComparison.Ordinal);

        /// <summary>
        /// Every grouping call made by a method that handles the Library - or by anything such a
        /// method CALLS, transitively, within the assembly. The closure is what catches grouping
        /// DELEGATED to a helper (issue #2, item 1): the round-3 gate restored real day groups by
        /// having ApplyLibraryMode hand the Library's view to a helper whose body did the grouping,
        /// and this scan, then confined to the handler's own body, stayed green. The walk is the
        /// same instrument the date guard already stands on (CompiledCode.Reachable), and it fails
        /// closed twice - no handlers found throws, and a seed that stopped existing throws inside
        /// Reachable itself. Since the issue #2 fix pass the walk also follows VIRTUAL AND
        /// INTERFACE DISPATCH conservatively: a call that targets an in-assembly interface,
        /// abstract or virtual method reaches EVERY in-assembly implementation and override of it,
        /// whether or not that concrete type can flow to the call site - the round-1 gate had
        /// restored day groups through exactly that seam (an implementation instantiated through an
        /// interface, Configure(view) called through the interface), with every guard green.
        ///
        /// The dispatch fan-out finds implementations a type INHERITS from an in-assembly base
        /// class (the round-2 review's finding 1 - the InterfaceImpl row on the derived type, the
        /// body on a base that never names the interface), and the walk also carries IMPLICIT
        /// RUNTIME INVOCATIONS (finding 2): touching any member of a type - a call, a
        /// construction, or a static field read/write - reaches its static constructor and its
        /// finalizer, which no call instruction anywhere targets.
        ///
        /// Its limits, stated honestly:
        /// - the ASSEMBLY BOUNDARY, in both of its forms: a callee whose body lives in another
        ///   assembly is not walked into, and a dispatch seam DECLARED in another assembly (a BCL
        ///   or WPF interface or base class, e.g. IObserver&lt;T&gt;.OnNext) has no edge to its
        ///   in-assembly implementations, because the declaration is not in this assembly's
        ///   metadata tables;
        /// - REFLECTION: a method reached only via reflection is not an edge in the call graph;
        /// - a DELEGATE INVOKED BY CODE THAT DID NOT BUILD IT: building a delegate (ldftn /
        ///   ldvirtftn) is an edge from the builder to the target, but Invoke on the delegate type
        ///   connects to nothing, so a target handed in from outside the closure is not followed;
        /// - RUNTIME-GENERATED CODE (Reflection.Emit, expression compilation) has no IL here to
        ///   walk (the product contains none - and a calli, the one call shape that names no
        ///   target, is counted and pinned by CompiledCode.IndirectCalls).
        /// And it is a REACHED-FROM claim, not proved dataflow, twice over: a helper that a Library
        /// handler calls but that groups some OTHER feature's view would be reported too, and the
        /// dispatch fan-out reports every implementation of a called interface, not just the one
        /// constructed. Both err toward a false alarm rather than a silent pass; the narrowness
        /// control keeps them honest for grouped views nothing in the Library's call graph touches.
        /// </summary>
        private static IReadOnlyList<CompiledCode.CallSite> LibraryGroupingIn(string assembly)
        {
            var handlers = new HashSet<string>(
                CompiledCode.FieldAccesses(assembly, IsALibraryField).Select(site => site.Method),
                StringComparer.Ordinal);

            if (handlers.Count == 0)
                throw new InvalidOperationException(
                    $"No method in {Path.GetFileName(assembly)} touches _recent or RecentList, so this "
                    + "guard would be scanning nothing and passing on absence.");

            var reached = new HashSet<string>(
                CompiledCode.Reachable(assembly, handlers), StringComparer.Ordinal);

            return CompiledCode.CallSites(assembly, IsGroupingApi)
                .Where(site => reached.Contains(site.Method))
                .ToList();
        }

        /// <summary>Every RefreshNaming call site that throws away the "the sort key moved" answer
        /// instead of re-sorting on it. A LITERAL match on the one shape the model uses - the answer
        /// is kept in <c>moved</c> and <c>SortKeyChanged</c> is raised on it - and it claims no
        /// more than that.</summary>
        private static IReadOnlyList<string> RefreshNamingCallSitesThatIgnoreTheResort(string code)
        {
            var sites = Regex.Matches(code, @"\.RefreshNaming\(\)");
            if (sites.Count == 0)
                throw new InvalidOperationException(
                    "Nothing calls RefreshNaming any more, so this guard would pass by finding nothing.");

            bool actsOnIt = code.Contains("if (moved) SortKeyChanged?.Invoke();", StringComparison.Ordinal);

            return sites
                .Where(site => !actsOnIt
                               || !LineAt(code, site.Index)
                                       .StartsWith("bool moved = ", StringComparison.Ordinal))
                .Select(site => $"RefreshNaming at offset {site.Index}: {LineAt(code, site.Index)}")
                .ToList();
        }

        /// <summary>Every UpdateEmptyState call in the loader that is not the one guarded by
        /// "the apply raised no collection event". Literal on purpose, like the resort scan.</summary>
        private static IReadOnlyList<string> UnguardedRetotalsIn(string loadRecent)
        {
            var sites = Regex.Matches(loadRecent, @"UpdateEmptyState\(\)");
            if (sites.Count == 0)
                throw new InvalidOperationException(
                    "LoadRecent no longer calls UpdateEmptyState, so this guard would pass by "
                    + "finding nothing - the loader has to settle the empty state and the total "
                    + "somewhere.");

            return sites
                .Where(site => !LineAt(loadRecent, site.Index)
                    .StartsWith("if (!notified) UpdateEmptyState();", StringComparison.Ordinal))
                .Select(site => $"UpdateEmptyState at offset {site.Index}: {LineAt(loadRecent, site.Index)}")
                .ToList();
        }

        private static string LineAt(string text, int offset)
        {
            int start = text.LastIndexOf('\n', Math.Min(offset, text.Length - 1)) + 1;
            int end = text.IndexOf('\n', offset);
            return text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
        }
    }
}
