using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, the responsive-UI half - and the Review Gate's third blocking defect on round 1 of
    /// PR #34.
    ///
    /// WHAT WAS WRONG. The recording HUD's Show/Hide-preview and mode/corner buttons persisted the
    /// person's choice by calling <c>Config.Save</c> straight from the click handler, and
    /// <c>Config.Save</c> was a synchronous <c>File.WriteAllText</c>. Under disk pressure, an
    /// antivirus scan or a filter driver, the WPF dispatcher is then blocked INSIDE that write - and
    /// that dispatcher is what serves the HUD's STOP button. The HUD's whole reason to exist is that
    /// a person can stop a recording; a settings file must never be able to take that away. The
    /// constructor made it worse by passing <c>fromUser: true</c>, so every HUD ever built rewrote
    /// config.json while it was being put on screen, on the very path that has to be quick.
    ///
    /// Two layers here, and they answer different questions:
    ///  - THE BEHAVIOUR: a queued save returns at once even while the write is stalled, and the
    ///    newest state still reaches the file. Measured against an injected stall, because a real
    ///    filesystem cannot be made to hang inside a unit test.
    ///  - THE STRUCTURE, read from the compiled IL: no product file is written anywhere the HUD's
    ///    UI thread can reach. That is the part a behavioural test cannot give, because it holds for
    ///    code nobody has written yet.
    /// </summary>
    public class HudResponsivenessTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "agenteyes-bgwriter-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a test that left a handle open must not fail the run here */ }
        }

        private string FilePath => Path.Combine(_dir, "config.json");

        // ---- the behaviour ---------------------------------------------------

        /// <summary>
        /// THE DEFECT, as a number. Saving while the filesystem does not answer must return in the
        /// time an interlocked swap takes, not in the time a disk takes.
        ///
        /// All three arms: a fast return is the pass; a slow one is the defect; and a run in which
        /// the write was never actually stalled is a BROKEN INSTRUMENT and fails too, rather than
        /// passing by proving nothing.
        /// </summary>
        [Fact]
        public void Queue_WhileTheWriteIsStalled_ReturnsAtOnce()
        {
            using var stalled = new ManualResetEventSlim(false);
            int writesEntered = 0;

            var writer = new BackgroundFileWriter(FilePath, (_, _) =>
            {
                Interlocked.Increment(ref writesEntered);
                stalled.Wait(TimeSpan.FromSeconds(30));   // a filesystem that does not answer
            });
            writer.Start();

            writer.Queue("{ \"first\": true }");
            Assert.True(SpinUntil(() => Volatile.Read(ref writesEntered) >= 1, 5000),
                "The stalled write was never entered, so nothing was blocked and this test measured "
                + "nothing.");

            var clock = Stopwatch.StartNew();
            writer.Queue("{ \"second\": true }");
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 200,
                $"Saving took {clock.ElapsedMilliseconds}ms while the filesystem was stalled. On the "
                + "recording HUD that thread is the WPF dispatcher, so for that whole time the person "
                + "cannot stop the recording either (issue #33; repo coding standard 1).");

            stalled.Set();
            writer.Stop(5000);
        }

        /// <summary>
        /// The file ends up holding the NEWEST state. Latest-wins is not a shortcut here: the file
        /// only ever holds one state anyway, so a save superseded before it was written is not a lost
        /// change - and it is COUNTED rather than silently dropped.
        /// </summary>
        [Fact]
        public void Queue_TwiceInARow_WritesTheNewestStateAndCountsTheOneItSuperseded()
        {
            using var letGo = new ManualResetEventSlim(false);
            int writesEntered = 0;

            var writer = new BackgroundFileWriter(FilePath, (path, text) =>
            {
                if (Interlocked.Increment(ref writesEntered) == 1) letGo.Wait(TimeSpan.FromSeconds(30));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            });
            writer.Start();

            writer.Queue("one");
            Assert.True(SpinUntil(() => Volatile.Read(ref writesEntered) >= 1, 5000),
                "the first write never started, so the supersede this test is about cannot happen");

            writer.Queue("two");
            writer.Queue("three");
            letGo.Set();

            Assert.True(writer.Flush(5000), "the writer never finished what it was holding");
            Assert.Equal("three", File.ReadAllText(FilePath));
            Assert.Equal(1, writer.Superseded);
            Assert.Equal(2, writer.Writes);
        }

        /// <summary>
        /// THE MIXED-WRITER ORDERING DEFECT (Review Gate round 2 on PR #39, defect 3), and the fix
        /// that made it possible, reproduced and then closed in one test.
        ///
        /// WHAT WENT WRONG. Making the HUD's save non-blocking gave config.json TWO kinds of writer:
        /// the HUD queued a full-document snapshot for a background thread, while the launcher, the
        /// settings window, the tray and the preset and plugin managers still wrote a full document
        /// straight from their own thread. Both went through the same lock - AND A LOCK SAYS WHO GOES
        /// FIRST, NOT WHO GOES LAST. The writer thread takes its pending value BEFORE it waits for
        /// the lock, so a newer direct write could take the lock, land, and then be overwritten by
        /// the OLDER queued snapshot. Because each is the whole document, the person's newer choice -
        /// their capture folder, a shortcut, a plugin, run-at-login, the last preset - was silently
        /// reverted on disk. The window widened under exactly the disk stalls the background writer
        /// exists to tolerate.
        ///
        /// THE KNOWN-BAD ARM reproduces it deterministically: the writer is held inside the older
        /// snapshot while the newer one is written directly, exactly as the shipped launcher paths
        /// did. THE SHIPPED ARM sends the newer save through the same one writer, so there is one
        /// order and the last save made is the last one written. Both arms are asserted, because the
        /// first is what makes the second a check rather than a hope.
        /// </summary>
        [Fact]
        public void ANewerBlockingSaveAfterAQueuedOne_LandsLast_AndTheOldShapeDoesNot()
        {
            Assert.Equal("older queued snapshot", RaceTheTwoWriters(throughTheWriter: false));
            Assert.Equal("newer blocking snapshot", RaceTheTwoWriters(throughTheWriter: true));
        }

        /// <summary>
        /// Queue an older snapshot, hold the writer inside it, make a NEWER save while it is held,
        /// then let go and report what the file ends up holding.
        ///
        /// <paramref name="throughTheWriter"/> false is the shape this branch shipped: the newer save
        /// writes the file itself. True is the shipped fix: it goes through the one writer.
        /// </summary>
        private string RaceTheTwoWriters(bool throughTheWriter)
        {
            string path = Path.Combine(_dir, throughTheWriter ? "shipped.json" : "knownbad.json");
            using var entered = new ManualResetEventSlim(false);
            using var letGo = new ManualResetEventSlim(false);
            int writes = 0;

            var writer = new BackgroundFileWriter(path, (p, t) =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    entered.Set();
                    letGo.Wait(TimeSpan.FromSeconds(30));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, t);
            });
            writer.Start();

            writer.Queue("older queued snapshot");          // the HUD's preview/position change
            Assert.True(entered.Wait(5000),
                "The writer never took the older snapshot, so the ordering this test is about cannot "
                + "arise and the run measured nothing.");

            if (throughTheWriter)
            {
                // The shipped Config.Save: queue it and wait for it. One writer, one order.
                var newer = new Thread(() => writer.WriteNow("newer blocking snapshot", 10000));
                newer.Start();
                Thread.Sleep(50);                            // it is now waiting on the held writer
                letGo.Set();
                Assert.True(newer.Join(10000), "the blocking save never returned");
            }
            else
            {
                // The shipped-on-this-branch Config.Save: it writes the file itself, ordered against
                // the queued snapshot by nothing at all.
                Directory.CreateDirectory(_dir);
                File.WriteAllText(path, "newer blocking snapshot");
                letGo.Set();
            }

            Assert.True(writer.Flush(5000), "the writer never finished what it was holding");
            writer.Stop(5000);
            return File.ReadAllText(path);
        }

        /// <summary>A write that throws is COUNTED, not swallowed. An absence would let a writer
        /// that never wrote anything look exactly like a healthy one.</summary>
        [Fact]
        public void Queue_WhenTheWriteThrows_CountsTheFailureAndKeepsWorking()
        {
            int attempts = 0;
            var writer = new BackgroundFileWriter(FilePath, (path, text) =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("the disk said no");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            });
            writer.Start();

            writer.Queue("one");
            Assert.True(SpinUntil(() => writer.Failures == 1, 5000), "the failing write was not counted");

            writer.Queue("two");
            Assert.True(writer.Flush(5000));
            Assert.Equal("two", File.ReadAllText(FilePath));
            Assert.Equal(1, writer.Writes);
            Assert.Equal(1, writer.Failures);
            writer.Stop(5000);
        }

        // ---- the structure ---------------------------------------------------

        /// <summary>
        /// Everything the recording HUD does on the WPF UI thread, transitively: constructing the
        /// window, every preview button, the timer tick, and the Closed handler that saves the
        /// position.
        ///
        /// Fail-closed by construction: <c>CompiledCode.Reachable</c> throws when a seed is not a
        /// method in the assembly, so renaming one of these turns the guard red rather than quietly
        /// shrinking the closure to nothing.
        /// </summary>
        private static readonly string[] UiThreadSeeds =
        {
            "AgentEyes.App.HudWindow::.ctor",
            "AgentEyes.App.HudWindow::TogglePreview",
            "AgentEyes.App.HudWindow::ChooseMode",
            "AgentEyes.App.HudWindow::ChooseCorner",
            "AgentEyes.App.HudWindow::ApplyPreviewState",
            "AgentEyes.App.HudWindow::ApplyAndRememberPreviewChoice",
            "AgentEyes.App.HudWindow::SavePreviewChoices",
            "AgentEyes.App.HudWindow::SavePosition",
            "AgentEyes.App.HudWindow::ClosePreview",
            "AgentEyes.App.HudWindow::OnTick",
            "AgentEyes.App.HudWindow::ShowFrames",
            "AgentEyes.App.HudUserResize::OnWindowMessage",
            "AgentEyes.App.HudUserResize::ByWindowState",
            "AgentEyes.App.HudUserResize::ByGrip",
            "AgentEyes.App.HudUserResize::ByAutomation",
            "AgentEyes.App.HudPreviewSizing::ShowPanel",
            "AgentEyes.App.HudPreviewSizing::HidePanel",
        };

        /// <summary>
        /// THE STRUCTURAL FORM OF DEFECT 3. No product file is written anywhere the HUD's UI thread
        /// can reach - transitively, through whatever helper anybody adds next, and independently of
        /// how the C# is spelled. The one write that used to be here, <c>Config.Save</c>, is now
        /// <c>Config.SaveWithoutBlockingTheUiThread</c>: it serialises on this thread (in-memory,
        /// microseconds) and hands the bytes to a writer thread the application started at startup.
        ///
        /// WHAT IT CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6):
        ///  - The closure stops at the ASSEMBLY BOUNDARY. A file write performed inside
        ///    AgentEyes.Core by something the HUD calls is invisible here. The one that matters is
        ///    named and covered separately: <c>PreviewTapTests.NothingTurningThePreviewOffCanReach_
        ///    TouchesTheFilesystem</c> holds the preview toggle's Core path shut.
        ///  - THE SHARED LOGGER IS A KNOWN, UNFIXED EXCEPTION. <c>AgentEyes.Log</c> appends to a file
        ///    synchronously under a process-wide lock, and the HUD calls it - as does every other
        ///    window in this app, on every UI thread, and has since long before this issue. It lives
        ///    in AgentEyes.Core, so it is outside this closure; that is a real limit of this check and
        ///    NOT a claim that it is safe. Making the logger non-blocking is app-wide work with its
        ///    own risks (a line lost at a crash is a line lost from the crash report) and belongs in
        ///    its own issue.
        ///  - It sees file WRITES, not every blocking call. A UI-thread network call or an unbounded
        ///    lock would not appear here.
        /// </summary>
        [Fact]
        public void NothingTheHudsUiThreadCanReach_WritesAFile()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.AppAssembly, UiThreadSeeds), StringComparer.Ordinal);

            var offenders = CompiledCode
                .CallSites(CompiledCode.AppAssembly, CompiledCode.IsFileWriteApi)
                .Where(site => reached.Contains(site.Method))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Something the recording HUD does on the WPF UI thread writes a file. That dispatcher "
                + "is the one that serves the STOP button, so a slow disk, an antivirus scan or a "
                + "filter driver makes the recording unstoppable for as long as the write takes "
                + "(issue #33; repo coding standard 1). Serialise on this thread and hand the bytes "
                + "to a background writer, the way Config.SaveWithoutBlockingTheUiThread does:"
                + Environment.NewLine + CompiledCode.Describe(offenders));
        }

        /// <summary>
        /// And the HUD really does still persist the choice - the guard above would be satisfied just
        /// as well by a HUD that saved NOTHING, which is an absence certifying a feature that does not
        /// work. Both halves are named here: the click path reaches the non-blocking save, and it does
        /// not reach the blocking one.
        /// </summary>
        [Fact]
        public void TheHudSavesItsChoices_ThroughTheNonBlockingPathAndOnlyThat()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.AppAssembly, UiThreadSeeds), StringComparer.Ordinal);

            Assert.Contains("AgentEyes.App.Config::SaveWithoutBlockingTheUiThread", reached);
            Assert.DoesNotContain("AgentEyes.App.Config::Save", reached);
        }

        /// <summary>
        /// APPLYING THE PREVIEW STATE IS NOT A PERSON CHOOSING ANYTHING. The apply used to take a
        /// <c>fromUser</c> flag and persist when it was set - and the constructor passed TRUE, so
        /// every HUD ever built rewrote config.json while it was being put on screen, on the one path
        /// that has to be quick and against what the adjacent comment said (Review Gate round 1 on
        /// PR #34).
        ///
        /// The flag is now two methods: <c>ApplyPreviewState</c> pushes the decisions into the
        /// window, the feed and the service, and <c>ApplyAndRememberPreviewChoice</c> is the one a
        /// click calls. This asserts the split is real - the bare apply, which is what the
        /// constructor calls, cannot reach a config write however it is edited later.
        ///
        /// WHAT THIS CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.5/6c.6): it
        /// does not prove the CONSTRUCTOR calls the bare apply rather than the remembering one. It
        /// cannot: every HUD button's Click handler is a lambda declared in the constructor, and the
        /// IL folds a lambda back into its declaring method, so everything any button can do is
        /// "reachable from .ctor" by construction. What is proven is the property that makes the
        /// constructor's choice safe - that one of the two methods has no path to a save at all -
        /// together with <see cref="EveryPreviewButton_RemembersTheChoice"/>, which proves the other
        /// one does.
        /// </summary>
        [Fact]
        public void ApplyingThePreviewState_NeverRemembersAChoiceByItself()
        {
            var reached = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.AppAssembly,
                                       new[] { "AgentEyes.App.HudWindow::ApplyPreviewState" }),
                StringComparer.Ordinal);

            Assert.DoesNotContain("AgentEyes.App.HudWindow::SavePreviewChoices", reached);
            Assert.DoesNotContain("AgentEyes.App.Config::SaveWithoutBlockingTheUiThread", reached);
            Assert.DoesNotContain("AgentEyes.App.Config::Save", reached);
        }

        /// <summary>The companion presence: a person's click DOES remember it. Without this, the
        /// guard above is satisfied by a HUD that never saves anything at all.</summary>
        [Fact]
        public void EveryPreviewButton_RemembersTheChoice()
        {
            foreach (string button in new[] { "TogglePreview", "ChooseMode", "ChooseCorner" })
            {
                var reached = new HashSet<string>(
                    CompiledCode.Reachable(CompiledCode.AppAssembly,
                                           new[] { "AgentEyes.App.HudWindow::" + button }),
                    StringComparer.Ordinal);

                Assert.True(reached.Contains("AgentEyes.App.Config::SaveWithoutBlockingTheUiThread"),
                    $"HudWindow.{button} no longer persists what the person chose, so the preview "
                    + "settings silently stop surviving a restart (issue #33, AC8).");
            }
        }

        /// <summary>
        /// The background writer's thread is started by <c>Config.Load</c> - at application startup,
        /// before any window exists - and never lazily from a UI path. That is not decoration: it is
        /// what keeps the writer's loop out of the closure above, so the guard is measuring the UI
        /// thread's own work rather than a thread body it happens to name.
        /// </summary>
        [Fact]
        public void TheConfigWritersThread_IsStartedFromLoadAndNotFromAUiPath()
        {
            var starts = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                           c => c == "AgentEyes.App.BackgroundFileWriter::Start")
                .Select(s => s.Method)
                .Distinct()
                .ToList();

            Assert.Equal(new[] { "AgentEyes.App.Config::Load" }, starts);
        }

        /// <summary>
        /// THE SAME RULE APPLIED TO THE PREVIEW'S OWN UI-THREAD CODE, and the guard that keeps the
        /// round-2 audit from being re-lost.
        ///
        /// Every defect this feature has had is one sentence: PREVIEW WORK BLOCKS SOMETHING THAT MUST
        /// NOT BLOCK. <c>AgentEyes.Log</c> is a <c>Directory.CreateDirectory</c> plus a
        /// <c>File.AppendAllText</c> under a PROCESS-WIDE lock, so a log line on this dispatcher is
        /// both a filesystem call and a lock another thread may be holding inside one - and this
        /// dispatcher serves the STOP button. So the preview's own App-side code says everything
        /// through <c>PreviewLog</c>, which hands the line to its own thread and returns.
        ///
        /// WHAT THIS DELIBERATELY DOES NOT COVER, stated rather than implied
        /// (DEVELOPMENT_METHOD.md 6c.5): the HUD's NON-preview lines - the status label, the discard
        /// click, the saved position, the window styles - and every other window in this app still
        /// call the shared logger directly, exactly as they did before this feature existed. Making
        /// the whole app's logger non-blocking is app-wide work with its own risks (a line lost at a
        /// crash is a line lost from the crash report) and belongs in its own issue; this issue owns
        /// the preview, and the preview is clean.
        /// </summary>
        [Fact]
        public void ThePreviewsOwnUiThreadCode_NeverCallsTheSharedLoggerDirectly()
        {
            string[] previewTypes =
            {
                "AgentEyes.App.HudPreviewSizing::",
                "AgentEyes.App.PreviewFrameFeed::",
                "AgentEyes.App.HudUserResize::",
                "AgentEyes.App.HudWindowAutomationPeer::",
            };
            string[] previewClickHandlers =
            {
                "AgentEyes.App.HudWindow::TogglePreview",
                "AgentEyes.App.HudWindow::ChooseMode",
                "AgentEyes.App.HudWindow::ChooseCorner",
            };

            var offenders = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                           c => c.StartsWith("AgentEyes.Log::", StringComparison.Ordinal))
                .Where(site => previewTypes.Any(t => site.Method.StartsWith(t, StringComparison.Ordinal))
                            || previewClickHandlers.Contains(site.Method))
                .ToList();

            Assert.True(offenders.Count == 0,
                "The preview calls the shared logger on the WPF dispatcher. That logger is a "
                + "synchronous file append under a process-wide lock, and that dispatcher is what "
                + "serves the HUD's Stop button (issue #33; Review Gate round 2 on PR #39). Say it "
                + "through PreviewLog, which appends on its own thread:" + Environment.NewLine
                + CompiledCode.Describe(offenders));

            // THE COMPANION PRESENCE, because "calls no logger" is satisfied just as well by a
            // preview that says NOTHING - an absence certifying silence. Every one of these types
            // still reports what it does; it reports it through the lane that does not wait.
            // (Counted as call sites rather than through Reachable: PreviewLog lives in
            // AgentEyes.Core, and a reachability closure over the App assembly stops at that
            // boundary.)
            var speaking = CompiledCode
                .CallSites(CompiledCode.AppAssembly,
                           c => c.StartsWith("AgentEyes.Preview.PreviewLog::", StringComparison.Ordinal))
                .Select(s => s.Method)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string type in previewTypes.Where(t => t != "AgentEyes.App.HudWindowAutomationPeer::"))
                Assert.True(speaking.Any(m => m.StartsWith(type, StringComparison.Ordinal)),
                    $"{type} no longer reports anything at all, which is not the same thing as no "
                    + "longer blocking. It must still say what it does - through PreviewLog.");
        }

        /// <summary>
        /// ONE WRITER, THEREFORE ONE ORDER - the structural form of the mixed-writer defect above,
        /// and the part a behavioural test cannot give: it holds for whatever save anybody adds next.
        ///
        /// Every save in the process, blocking or not, goes through the same background writer.
        /// <c>Config.WriteJson</c> - the method that actually puts bytes on disk - is named by
        /// exactly one thing, the field initializer that hands it to that writer. The moment a save
        /// path calls it again the two kinds of writer stop being ordered, and the older snapshot can
        /// land on top of the newer one.
        /// </summary>
        [Fact]
        public void EverySaveGoesThroughTheOneWriter_SoTheLastSaveMadeIsTheLastWritten()
        {
            var direct = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.Config::WriteJson")
                .Select(s => s.Method)
                .Distinct()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

            Assert.True(direct.Count == 1 && direct[0] == "AgentEyes.App.Config::.cctor",
                "config.json is written by something other than the one background writer ("
                + string.Join(", ", direct) + "). Both kinds of save serialise the WHOLE document, so "
                + "the file is only correct if the LAST save made is the last one written - and a "
                + "lock cannot give that, because a lock decides who goes first, not who goes last. "
                + "A newer direct write lands and is then overwritten by an older queued snapshot, "
                + "silently reverting the person's newer choice (Review Gate round 2 on PR #39, "
                + "defect 3). Queue it instead; wait for it if the caller must.");

            var blocking = new HashSet<string>(
                CompiledCode.Reachable(CompiledCode.AppAssembly, new[] { "AgentEyes.App.Config::Save" }),
                StringComparer.Ordinal);
            Assert.Contains("AgentEyes.App.BackgroundFileWriter::WriteNow", blocking);
            Assert.Contains("AgentEyes.App.BackgroundFileWriter::Queue", blocking);
        }

        /// <summary>A queued save that is still in flight when the process exits is flushed, so the
        /// last thing a person chose is not lost to the very design that made choosing it quick.</summary>
        [Fact]
        public void ApplicationExit_FlushesAPendingConfigSave()
        {
            var flushes = CompiledCode
                .CallSites(CompiledCode.AppAssembly, c => c == "AgentEyes.App.Config::FlushPendingSave")
                .Select(s => s.Method)
                .Distinct()
                .ToList();

            Assert.Equal(new[] { "AgentEyes.App.App::OnExit" }, flushes);
        }

        private static bool SpinUntil(Func<bool> condition, int milliseconds)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }
    }
}
