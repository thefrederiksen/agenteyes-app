using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AgentEyes.DesktopApp;
using Avalonia.Controls;
using Xunit;

// Issue #116 (Windows QA, criterion 11): the desktop app's tray Quit crashed the app on Windows 3 of 3.
// ONE source file, compiled into AgentEyes.Tests.DesktopApp (in AgentEyes.sln, so the Windows gate runs
// it) and AgentEyes.Tests.Linux (CI ubuntu-latest), the same way tests/Shared/CliDispatcherTests.cs is.
//
// The crash itself happens inside Avalonia's Win32 message loop and cannot run in a unit test. What CAN
// be pinned is the shape of the fix, so a refactor cannot quietly undo it:
//   1. TrayPopupCloseGuard - the re-entrant second close of Avalonia's Windows tray popup is cancelled
//      (that double teardown is the crash), the guard targets a type that still exists, and the app
//      installs it before the tray icon exists;
//   2. DeferredQuit - the tray's Quit never shuts the app down inside the menu's click handling.
// Nothing here starts the app, opens a window or touches the network.
namespace AgentEyes.Tests.Shared
{
    public sealed class DesktopAppQuitTests
    {
        // ---- 1. TrayPopupCloseGuard ----

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

        // ---- 2. DeferredQuit ----

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
}
