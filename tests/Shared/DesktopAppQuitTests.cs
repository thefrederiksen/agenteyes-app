using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AgentEyes.DesktopApp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Xunit;

// Issue #116 (Windows QA, criterion 11): the desktop app's tray Quit crashed the app on Windows 3 of 3.
// ONE source file, compiled into AgentEyes.Tests.DesktopApp (in AgentEyes.sln, so the Windows gate runs
// it) and AgentEyes.Tests.Linux (CI ubuntu-latest), the same way tests/Shared/CliDispatcherTests.cs is.
//
// THE FIX IS TrayPopupCloseGuard: the re-entrant second close of Avalonia's Windows tray popup is
// cancelled in Closing, so the popup is torn down once (the double teardown is the crash). Pass 3 measured
// it on Windows: builds without the guard's cancel crashed 7 of 71 runs, guarded builds 0 of 62.
//
// The crash itself happens inside Avalonia's Win32 message loop and cannot run in a unit test. What these
// tests pin:
//   1. TrayPopupCloseGuardWindowTests (below) - THE regression test for the fix. The installed guard is
//      driven through real Avalonia windows' Closing events on the headless platform: a re-entrant close
//      of the tray popup is cancelled, and a re-entrant close of any other window is not. It fails if the
//      guard's "e.Cancel = true" is removed, and if the guard attaches to the wrong windows (pass 4: the
//      Review Gate's mutations G1 and G2).
//   2. TrayPopupCloseGuard's decision table (CloseState), the popup's type name, and that the app installs
//      the guard before the tray icon exists.
//   3. DeferredQuit - the tray's Quit never shuts the app down inside the menu's click handling. This is
//      NOT the fix and does not prevent the crash: pass 3 measured a build that keeps the deferral and
//      drops the guard's cancel (it crashed) and one that drops the deferral and keeps the guard (it did
//      not). The shape is pinned because it is still wanted, not because the crash depends on it.
// Nothing here starts the app, shows an on-screen window or touches the network.
namespace AgentEyes.Tests.Shared
{
    public sealed class DesktopAppQuitTests
    {
        // ---- 2. TrayPopupCloseGuard: the decision table, the type name, the install order ----

        [Fact]
        public void CancelThisClose_FirstClose_IsAllowedAndMarksClosing()
        {
            var state = new TrayPopupCloseGuard.CloseState();

            bool cancel = state.CancelThisClose(cancelledByOthers: false);

            Assert.False(cancel);
            Assert.True(state.Closing);
        }

        [Fact]
        public void CancelThisClose_ReentrantSecondAndThirdClose_AreCancelled()
        {
            // The measured sequence: CloseMenu closes the popup (1st), then HandleClosed ->
            // HandleDeactivated -> TrayPopupRoot_Deactivated closes it again (2nd), and the menu
            // handler's own deactivation handlers try a 3rd and 4th time.
            var state = new TrayPopupCloseGuard.CloseState();
            state.CancelThisClose(cancelledByOthers: false);

            Assert.True(state.CancelThisClose(cancelledByOthers: false));
            Assert.True(state.CancelThisClose(cancelledByOthers: false));
            Assert.True(state.CancelThisClose(cancelledByOthers: true));
        }

        [Fact]
        public void CancelThisClose_CloseAlreadyCancelledByOthers_DoesNotMarkClosing()
        {
            var state = new TrayPopupCloseGuard.CloseState();

            bool cancel = state.CancelThisClose(cancelledByOthers: true);

            Assert.False(cancel);
            Assert.False(state.Closing);
            Assert.False(state.CancelThisClose(cancelledByOthers: false));   // the real first close still goes through
            Assert.True(state.Closing);
        }

        [Fact]
        public void ReopenIfNotClosed_FirstCloseCancelledElsewhere_LetsTheNextCloseThrough()
        {
            // Review finding: a later Closing handler cancelling the first close must not leave the
            // popup impossible to dismiss.
            var state = new TrayPopupCloseGuard.CloseState();
            state.CancelThisClose(cancelledByOthers: false);

            bool reopened = state.ReopenIfNotClosed();

            Assert.True(reopened);
            Assert.False(state.CancelThisClose(cancelledByOthers: false));
        }

        [Fact]
        public void ReopenIfNotClosed_PopupClosed_KeepsCancellingLaterCloses()
        {
            var state = new TrayPopupCloseGuard.CloseState();
            state.CancelThisClose(cancelledByOthers: false);
            state.MarkClosed();

            bool reopened = state.ReopenIfNotClosed();

            Assert.False(reopened);
            Assert.True(state.CancelThisClose(cancelledByOthers: false));
        }

        [Fact]
        public void TrayPopupTypeName_NamesAvaloniasWindowsTrayPopup_WhichIsAWindow()
        {
            // If an Avalonia upgrade renames or replaces the popup, the guard would silently match
            // nothing and the crash would return. This fails first.
            Type? popup = Type.GetType(TrayPopupCloseGuard.TrayPopupTypeName + ", Avalonia.Win32");

            Assert.NotNull(popup);
            Assert.True(typeof(Window).IsAssignableFrom(popup));
            Assert.True(TrayPopupCloseGuard.IsTrayPopup(popup!));
        }

        [Fact]
        public void IsTrayPopup_AnyOtherWindow_IsFalse()
        {
            Assert.False(TrayPopupCloseGuard.IsTrayPopup(typeof(Window)));
            Assert.False(TrayPopupCloseGuard.IsTrayPopup(typeof(AgentEyes.DesktopApp.Views.MainWindow)));
        }

        [Fact]
        public void App_InstallsTheGuard_BeforeTheTrayIconExists()
        {
            string app = ReadSource("App.axaml.cs");

            int install = app.IndexOf("TrayPopupCloseGuard.Install();", StringComparison.Ordinal);
            int tray = app.IndexOf("new TrayController(", StringComparison.Ordinal);

            Assert.True(install >= 0, "App.axaml.cs no longer installs TrayPopupCloseGuard");
            Assert.True(tray > install, "TrayPopupCloseGuard must be installed before the TrayController is created");
        }

        // ---- 3. DeferredQuit: a pinned shape, NOT the crash fix (see the header) ----

        [Fact]
        public void Request_ReturnsWithoutRunningTheShutdown()
        {
            var posted = new List<Action>();
            int shutdowns = 0;
            var quit = new DeferredQuit(posted.Add, () => shutdowns++);

            quit.Request();

            Assert.Equal(0, shutdowns);
            Assert.Single(posted);
            Assert.True(quit.IsRequested);
        }

        [Fact]
        public void Request_PostedWorkRuns_ShutsDownOnce()
        {
            var posted = new List<Action>();
            int shutdowns = 0;
            var quit = new DeferredQuit(posted.Add, () => shutdowns++);

            quit.Request();
            posted[0]();

            Assert.Equal(1, shutdowns);
        }

        [Fact]
        public void Request_Twice_PostsOneShutdown()
        {
            var posted = new List<Action>();
            var quit = new DeferredQuit(posted.Add, () => { });

            quit.Request();
            quit.Request();

            Assert.Single(posted);
        }

        [Fact]
        public void TrayController_QuitItem_TakesADeferredQuitNotAnAction()
        {
            // The tray cannot be handed a plain callback again: its constructor only accepts a DeferredQuit.
            var ctor = Assert.Single(typeof(TrayController).GetConstructors(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
            var parameters = ctor.GetParameters();

            Assert.Contains(parameters, p => p.ParameterType == typeof(DeferredQuit));
            Assert.DoesNotContain(parameters, p => p.Name == "quit" && p.ParameterType == typeof(Action));
        }

        [Fact]
        public void DesktopApp_LifetimeShutdown_IsReachedOnlyThroughDeferredQuit()
        {
            // A source fact, not a runtime one (as in StopPathTests, issue #151): exactly one call site
            // shuts the lifetime down, inside App.ShutdownNow, and ShutdownNow is only ever handed to
            // DeferredQuit, whose production constructor posts it to the UI dispatcher.
            var sources = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "AgentEyes.DesktopApp"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

            var shutdownCalls = sources.SelectMany(kv => Regex.Matches(kv.Value, @"\.Shutdown\(").Select(_ => kv.Key)).ToList();
            Assert.Equal(new[] { "App.axaml.cs" }, shutdownCalls);

            string app = sources["App.axaml.cs"];
            var uses = Regex.Matches(app, @"ShutdownNow\(").Count;
            Assert.Equal(2, uses);   // the method itself, and the one use below
            Assert.Contains("new DeferredQuit(() => ShutdownNow(desktop))", app);

            Assert.Contains("Dispatcher.UIThread.Post(work, DispatcherPriority.Background)", sources["DeferredQuit.cs"]);
        }

        // ---- helpers ----

        private static string ReadSource(string file) =>
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "AgentEyes.DesktopApp", file));

        /// <summary>The repository root: the nearest directory above the test binary that holds AgentEyes.sln.</summary>
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentEyes.sln"))) return dir.FullName;
            }
            throw new InvalidOperationException($"AgentEyes.sln not found above {AppContext.BaseDirectory} - these tests read the desktop app's source and must run from a build inside the repository");
        }
    }

    /// <summary>
    /// THE regression test for the issue #116 fix (pass 4). Drives the INSTALLED TrayPopupCloseGuard through
    /// real Avalonia windows on the headless platform: TrayPopupCloseGuard.Install registers its class
    /// handler on Window.WindowOpenedEvent exactly as the app does, the window is shown, and the window
    /// closes itself a second time from inside its first close - the re-entrant shape that crashes on
    /// Windows (Close -> Dispose -> HandleClosed -> ... -> Close again). The window records each close's
    /// FINAL Cancel, after every Closing handler (the guard's included) has run.
    /// </summary>
    public sealed class TrayPopupCloseGuardWindowTests : IClassFixture<GuardHeadlessSession>
    {
        private readonly GuardHeadlessSession _session;

        public TrayPopupCloseGuardWindowTests(GuardHeadlessSession session) => _session = session;

        [Fact]
        public void TrayPopup_ClosedReentrantly_SecondCloseIsCancelledByTheGuard()
        {
            List<bool> verdicts = _session.CloseReentrantly(() => new Avalonia.Win32.TrayIconImpl.TrayPopupRoot());

            // The instrument must have run - two closes - or the assertions below prove nothing.
            Assert.True(verdicts.Count == 2,
                $"expected the first close and one re-entrant close, recorded {verdicts.Count} close(s)");
            Assert.False(verdicts[0],
                "the guard cancelled the tray popup's FIRST close - the popup could never be dismissed");
            Assert.True(verdicts[1],
                "the tray popup's re-entrant second close was NOT cancelled - TrayPopupCloseGuard did not stop the double teardown that crashes the app on Windows (issue #116)");
        }

        [Fact]
        public void OtherWindow_ClosedReentrantly_IsNotTouchedByTheGuard()
        {
            List<bool> verdicts = _session.CloseReentrantly(() => new ReentrantCloseWindow());

            Assert.True(verdicts.Count == 2,
                $"expected the first close and one re-entrant close, recorded {verdicts.Count} close(s)");
            Assert.False(verdicts[0] || verdicts[1],
                "TrayPopupCloseGuard cancelled a close of a window that is not the tray popup - it must guard Avalonia's Windows tray popup and nothing else");
        }
    }

    /// <summary>
    /// A window that closes itself again from inside its own first close, once, and records the final
    /// Cancel of every close. The re-close is made from OnClosed, which Avalonia runs while the first
    /// close is still disposing the window - where the real tray popup's Deactivated handler re-closes it.
    /// </summary>
    public class ReentrantCloseWindow : Window
    {
        private bool _reclosed;

        /// <summary>The final Cancel of each close, in order.</summary>
        public List<bool> Verdicts { get; } = new List<bool>();

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);   // raises Closing: every handler, the guard's included, has run after this
            Verdicts.Add(e.Cancel);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (!_reclosed)
            {
                _reclosed = true;
                Close();
            }
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// One headless Avalonia session for the guard tests, with the guard installed once, as App does.
    /// Avalonia can be set up once per process, and TrayPopupCloseGuard.Install refuses a second call.
    /// </summary>
    public sealed class GuardHeadlessSession : IDisposable
    {
        private readonly HeadlessUnitTestSession _session;

        public GuardHeadlessSession()
        {
            _session = HeadlessUnitTestSession.StartNew(typeof(GuardTestApp), AvaloniaTestIsolationLevel.PerAssembly);
            _session.Dispatch(() => TrayPopupCloseGuard.Install(), CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>Show the window, close it (it re-closes itself once), and return each close's final Cancel.</summary>
        public List<bool> CloseReentrantly(Func<ReentrantCloseWindow> create) =>
            _session.Dispatch(() =>
            {
                ReentrantCloseWindow window = create();
                window.Show();
                window.Close();
                return window.Verdicts;
            }, CancellationToken.None).GetAwaiter().GetResult();

        public void Dispose() => _session.Dispose();
    }

    /// <summary>The headless application the guard tests run in: no theme, no drawing, nothing on screen.</summary>
    public sealed class GuardTestApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<GuardTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }
}

// A stand-in for Avalonia's Windows tray popup. TrayPopupCloseGuard selects windows by FULL TYPE NAME
// (TrayPopupCloseGuard.TrayPopupTypeName), and the real Avalonia.Win32.TrayIconImpl+TrayPopupRoot is
// internal to Avalonia.Win32 and opens only on the Win32 platform, so the test declares a window with the
// same full name. That the real type still exists under that name is pinned separately, by
// DesktopAppQuitTests.TrayPopupTypeName_NamesAvaloniasWindowsTrayPopup_WhichIsAWindow. Like the real
// popup, it closes itself again while its first close is still tearing it down.
namespace Avalonia.Win32
{
    internal sealed class TrayIconImpl
    {
        internal sealed class TrayPopupRoot : AgentEyes.Tests.Shared.ReentrantCloseWindow
        {
        }
    }
}
