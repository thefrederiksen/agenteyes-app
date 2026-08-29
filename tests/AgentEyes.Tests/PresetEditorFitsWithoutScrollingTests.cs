using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #35 asked for one thing in the user's own words - "the settings box is really bad with
    /// the scroll bar on the right side. It's just ugly. Why don't we just make it wider so it
    /// doesn't have to scroll?" - and its AC1/AC3 said it twice more: no vertical scrollbar at the
    /// default window size, on ANY tab.
    ///
    /// NOTHING IN THE SUITE HELD THAT SHUT. <see cref="PresetEditorLayoutTests"/> reads the markup
    /// and can only see that a ScrollViewer EXISTS; it says so itself ("WHAT THESE TESTS CANNOT SEE:
    /// whether the rendered content actually fits without scrolling at the default window size"),
    /// and #35 left that fact to a one-off manual probe that no later change ever re-ran. So when
    /// issue #43 added a schematic to the Camera tab, the scrollbar came straight back and every
    /// test stayed green.
    ///
    /// This file closes that hole. It is a MEASURED RUNTIME FACT, not a source fact: the real
    /// <see cref="PresetEditor"/> is built, laid out at the client size its own default
    /// Width/Height give it on this machine, and asked what its ScrollViewer decided.
    ///
    /// THE DIALOG IS NEVER SHOWN, deliberately. <c>PresetEditor</c> enumerates cameras from
    /// <c>Window.Loaded</c>, which launches ffmpeg; an unshown window never raises Loaded, so the
    /// suite stays as fast and as silent as it is today - no camera device is opened and no ffmpeg
    /// process is started. Layout is driven by hand instead, which is all a ScrollViewer needs to
    /// decide whether its content fits.
    ///
    /// WHAT THIS CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): how the
    /// dialog LOOKS - that the panes are where a person would want them, and that the schematic is
    /// large enough to read. That is the screenshot evidence in docs/cencon/proof/issue-43/shots.
    /// It also cannot see a display whose DPI or text scaling differs from this machine's; it
    /// measures the machine it runs on, in the same device-independent pixels WPF lays out in.
    /// </summary>
    public class PresetEditorFitsWithoutScrollingTests
    {
        /// <summary>
        /// Every tab, by the x:Name of the tab and of the ScrollViewer inside it. A tab missing from
        /// here would be a tab nobody measures, so the list is asserted complete below.
        /// </summary>
        public static IEnumerable<object[]> EveryTab() => new[]
        {
            new object[] { "CaptureTab", "CaptureScroll" },
            new object[] { "AudioTab", "AudioScroll" },
            new object[] { "CameraTab", "CameraScroll" },
        };

        [Theory]
        [MemberData(nameof(EveryTab))]
        public void ATab_AtTheDefaultWindowSize_ShowsNoVerticalScrollBar(string tabName, string scrollName)
        {
            var m = TheEditorUnderTest.LayOutTab(tabName, scrollName);

            // An empty measurement is a broken instrument, never a clean run: a ScrollViewer that was
            // never laid out reports Collapsed and zero for everything, which would pass silently.
            Assert.True(m.ViewportHeight > 100 && m.ExtentHeight > 100,
                $"The {tabName} was not laid out - viewport {m.ViewportHeight:F0}, extent {m.ExtentHeight:F0}. "
                + "This measures nothing; fix the rig before trusting the verdict.");

            Assert.True(m.VerticalScrollBar == Visibility.Collapsed,
                $"The {tabName} scrolls at the editor's default {m.WindowWidth:F0}x{m.WindowHeight:F0} window "
                + $"(client {m.ClientWidth:F0}x{m.ClientHeight:F0}): the content is {m.ExtentHeight:F0} px tall "
                + $"in a {m.ViewportHeight:F0} px viewport, {m.ExtentHeight - m.ViewportHeight:F0} px too much. "
                + "Issue #35 AC1/AC3 say there is no vertical scrollbar on any tab at the default size - "
                + "find the room or enlarge the window, but do not give the scrollbar back.");
        }

        [Fact]
        public void TheHorizontalDirection_NeverScrolls_Either()
        {
            // The editor's own answer to "why don't we just make it wider" is to use the width, so a
            // panel that overflows sideways would be the same defect turned ninety degrees.
            foreach (var row in EveryTab())
            {
                var m = TheEditorUnderTest.LayOutTab((string)row[0], (string)row[1]);
                Assert.True(m.ExtentWidth <= m.ViewportWidth + 0.5,
                    $"The {row[0]} is {m.ExtentWidth:F0} px wide in a {m.ViewportWidth:F0} px viewport.");
            }
        }

        [Fact]
        public void ASizeRememberedUnderAnOlderLayout_IsNotRestored()
        {
            // The fix above only reaches a NEW installation unless this holds. Everyone who has ever
            // opened this dialog already has the old panel's 1000x760 in their config, and the editor
            // restores a remembered size in preference to its own default - so without a stamp the
            // scrollbar would come back for exactly the people who reported it.
            var stale = new Config
            {
                PresetEditorWidth = 1000,
                PresetEditorHeight = 760,
                PresetEditorLayout = PresetEditor.LayoutVersion - 1,
            };

            Size opened = TheEditorUnderTest.OpensAt(stale);
            Size fresh = TheEditorUnderTest.OpensAt(new Config());

            Assert.True(fresh.Width > 0 && fresh.Height > 0, "The rig read no default size at all.");
            Assert.Equal(fresh.Width, opened.Width, 3);
            Assert.Equal(fresh.Height, opened.Height, 3);
        }

        [Fact]
        public void ASizeRememberedUnderThisLayout_IsStillRestored()
        {
            // The other half, so the stamp cannot pass by throwing every remembered size away:
            // issue #35 AC10 is that the editor comes back the size it was left at.
            var mine = new Config
            {
                PresetEditorWidth = 1400,
                PresetEditorHeight = 900,
                PresetEditorLayout = PresetEditor.LayoutVersion,
            };

            Size opened = TheEditorUnderTest.OpensAt(mine);

            Assert.Equal(1400, opened.Width, 3);
            Assert.Equal(900, opened.Height, 3);
        }

        [Fact]
        public void EveryTabInTheEditor_IsMeasuredHere()
        {
            // The list above is a hand-written allowlist, so a tab added later would otherwise be a
            // tab this file quietly stops guarding.
            int tabsInTheDialog = TheEditorUnderTest.TabCount();
            Assert.True(tabsInTheDialog > 0, "The preset editor reported no tabs at all - the rig is broken.");
            Assert.True(tabsInTheDialog == 3,
                $"The preset editor now has {tabsInTheDialog} tabs but only 3 are measured for scrolling. "
                + "Add the new tab to EveryTab().");
        }
    }

    /// <summary>
    /// THE REAL PRESET EDITOR, LAID OUT OFFSCREEN AND NEVER SHOWN.
    ///
    /// WPF is thread-affine and <see cref="Application"/> is a per-process singleton, so one STA
    /// thread owns both for the life of the test run: the dialog's markup resolves brushes and
    /// styles out of <c>App.xaml</c> through <c>Application.Current.Resources</c> and cannot be
    /// parsed without them. The thread is a background thread with a running dispatcher, so the test
    /// process still exits on its own.
    /// </summary>
    internal static class TheEditorUnderTest
    {
        /// <summary>What one tab's ScrollViewer decided, and the sizes it decided it from.</summary>
        internal readonly struct Fit
        {
            public Visibility VerticalScrollBar { get; init; }
            public double ExtentHeight { get; init; }
            public double ViewportHeight { get; init; }
            public double ExtentWidth { get; init; }
            public double ViewportWidth { get; init; }
            public double WindowWidth { get; init; }
            public double WindowHeight { get; init; }
            public double ClientWidth { get; init; }
            public double ClientHeight { get; init; }
        }

        private static readonly object Gate = new();
        private static Dispatcher? _dispatcher;

        /// <summary>
        /// The one STA thread every measurement runs on. It creates the WPF Application once - a
        /// second one anywhere in the process would throw - and then just pumps.
        /// </summary>
        private static Dispatcher TheUiThread()
        {
            lock (Gate)
            {
                if (_dispatcher != null) return _dispatcher;

                Dispatcher? started = null;
                Exception? failure = null;
                using var ready = new ManualResetEventSlim(false);

                var thread = new Thread(() =>
                {
                    try
                    {
                        if (Application.Current == null)
                        {
                            var app = new AgentEyes.App.App();
                            app.InitializeComponent();   // App.xaml resources only - OnStartup never runs

                            // The rig opens and closes windows. WPF's default ShutdownMode ends the
                            // Application when the LAST one closes, and a shut-down Application can no
                            // longer serve the BAML the next PresetEditor is parsed from - so the
                            // second measurement would die with "The Application object is being shut
                            // down." rather than report a size.
                            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        }
                        started = Dispatcher.CurrentDispatcher;
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { ready.Set(); }

                    if (failure == null) Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "preset-editor-layout-rig";
                thread.Start();

                if (!ready.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException(
                        "The preset editor layout rig did not start within 60 s. These tests need an "
                        + "interactive desktop session: they build a real (never shown) WPF window so "
                        + "the ScrollViewer makes a real decision.");

                if (failure != null)
                    throw new InvalidOperationException(
                        "The preset editor layout rig could not start a WPF Application, so the dialog's "
                        + "markup cannot be parsed: " + failure, failure);

                return _dispatcher = started!;
            }
        }

        private static T OnTheUiThread<T>(Func<T> work)
        {
            var dispatcher = TheUiThread();
            T result = default!;
            Exception? failure = null;
            dispatcher.Invoke(() =>
            {
                try { result = work(); }
                catch (Exception ex) { failure = ex; }
            }, DispatcherPriority.Normal);

            if (failure != null)
                throw new InvalidOperationException("The preset editor layout rig failed: " + failure, failure);
            return result;
        }

        /// <summary>The size the editor opens at when it is handed this config.</summary>
        internal static Size OpensAt(Config cfg) => OnTheUiThread(() =>
        {
            var win = NewEditor(cfg);
            try { return new Size(win.Width, win.Height); }
            finally { win.Close(); }
        });

        internal static int TabCount() => OnTheUiThread(() =>
        {
            var win = NewEditor();
            try { return ((TabControl)win.FindName("EditorTabs")!).Items.Count; }
            finally { win.Close(); }
        });

        /// <summary>
        /// Build the editor, select one tab, lay the whole dialog out at the client size its default
        /// Width/Height give it here, and report what the tab's ScrollViewer made of that.
        /// </summary>
        internal static Fit LayOutTab(string tabName, string scrollName) => OnTheUiThread(() =>
        {
            var win = NewEditor();
            try
            {
                var tabs = (TabControl)win.FindName("EditorTabs")!;
                var tab = win.FindName(tabName) as TabItem
                          ?? throw new InvalidOperationException($"{tabName} is not a TabItem in PresetEditor.xaml.");
                var scroll = win.FindName(scrollName) as ScrollViewer
                             ?? throw new InvalidOperationException($"{scrollName} is not a ScrollViewer in PresetEditor.xaml.");
                tabs.SelectedItem = tab;

                Size chrome = TheWindowChrome();
                double clientWidth = win.Width - chrome.Width;
                double clientHeight = win.Height - chrome.Height;

                var root = (FrameworkElement)win.Content;
                var client = new Size(clientWidth, clientHeight);
                for (int pass = 0; pass < 3; pass++)
                {
                    root.Measure(client);
                    root.Arrange(new Rect(client));
                    root.UpdateLayout();
                    Pump();
                }

                return new Fit
                {
                    VerticalScrollBar = scroll.ComputedVerticalScrollBarVisibility,
                    ExtentHeight = scroll.ExtentHeight,
                    ViewportHeight = scroll.ViewportHeight,
                    ExtentWidth = scroll.ExtentWidth,
                    ViewportWidth = scroll.ViewportWidth,
                    WindowWidth = win.Width,
                    WindowHeight = win.Height,
                    ClientWidth = clientWidth,
                    ClientHeight = clientHeight,
                };
            }
            finally { win.Close(); }
        });

        /// <summary>
        /// A fresh editor at its XAML default size. The Config is a NEW one, never loaded from disk,
        /// so the developer's remembered window size cannot decide what "the default size" means.
        /// </summary>
        private static PresetEditor NewEditor() => NewEditor(new Config());

        private static PresetEditor NewEditor(Config cfg)
        {
            var preset = new CapturePreset { Name = "layout rig", Mode = "video" };
            return new PresetEditor(preset, cfg);
        }

        /// <summary>
        /// HOW MUCH OF A WINDOW IS FRAME RATHER THAN CONTENT, on the machine running the suite.
        ///
        /// Measured rather than named, for the reason <see cref="TheDisplayUnderTest"/> exists: a
        /// title bar and a resize border are a system metric, they differ with DPI and with the
        /// Windows version, and a literal here would make the test pass or fail on the runner's
        /// theme rather than on the dialog's layout. A plain window in the editor's own shape is
        /// shown far off-screen, asked how big its content ended up, and closed.
        /// </summary>
        private static Size TheWindowChrome()
        {
            if (_chrome is { } cached) return cached;

            var content = new Grid();
            var probe = new Window
            {
                Title = "preset editor chrome probe",
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.Manual,
                Left = -8000,
                Top = -8000,
                Width = 1000,
                Height = 760,
                Content = content,
            };

            try
            {
                probe.Show();
                for (int i = 0; i < 3; i++) Pump();

                var chrome = new Size(probe.ActualWidth - content.ActualWidth,
                                      probe.ActualHeight - content.ActualHeight);

                if (!(chrome.Width >= 0) || !(chrome.Height > 0) || chrome.Height > 200 || chrome.Width > 200)
                    throw new InvalidOperationException(
                        $"The chrome probe measured a {chrome.Width}x{chrome.Height} frame around a "
                        + $"{probe.ActualWidth}x{probe.ActualHeight} window. That is not a window frame, so "
                        + "every fit measured from it would be measuring nothing.");

                return (_chrome = chrome).Value;
            }
            finally { probe.Close(); }
        }

        private static Size? _chrome;

        /// <summary>Let everything WPF deferred to the dispatcher run before anything is read.</summary>
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
