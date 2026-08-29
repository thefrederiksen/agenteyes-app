using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #33, AC1 + AC7 - THE ORDERING, DRIVEN THROUGH A REAL WPF WINDOW.
    ///
    /// WHY THIS FILE EXISTS. Every unit test in this repo was green while the HUD shipped a defect
    /// that made the preview panel unusable: toggling "Show preview" on an ALREADY-SHOWN HUD opened
    /// the panel at the collapsed pill's 367x52 with a zero-sized picture, and wrote 367x52 to
    /// config so every later recording started broken too. The cause was pure ordering -
    /// <c>SizeToContent = Manual</c> re-lays the window out SYNCHRONOUSLY and raises SizeChanged
    /// while ActualWidth/ActualHeight are still the pill's, so the pill was recorded as a deliberate
    /// size and read straight back. No test could see it, because no test drove a WPF window: the
    /// suite could only ask <see cref="HudSizeMemory"/> what it does with an argument, never what
    /// that argument is worth AT THE MOMENT WPF hands it over.
    ///
    /// So these tests put a REAL <see cref="Window"/> on a real STA dispatcher, in the HUD's own
    /// sizing shape (auto-sized to a pill, a collapsed panel below it), and drive it with the SAME
    /// production code the HUD uses - <see cref="HudPreviewSizing"/> and <see cref="HudSizeMemory"/>,
    /// not a copy of them. The window's real SizeChanged sequence is what reaches the memory, in the
    /// order WPF actually raises it.
    ///
    /// The user resize is performed with SetWindowPos on the real HWND, which is exactly what UI
    /// Automation's TransformPattern.Resize does - the path QA drives AC7 with - rather than a
    /// property assignment that would flatter the code.
    ///
    /// WHAT THIS STILL CANNOT SEE, stated rather than implied (DEVELOPMENT_METHOD.md 6c.6): the
    /// window here carries the HUD's sizing shape, not the HUD's controls, because HudWindow needs a
    /// running Application's resources, a RecordingService and the developer's real config file. So
    /// this proves the sizing decisions under real WPF layout; that HudWindow makes those decisions
    /// through this same code, and no other way, is asserted against the compiled IL in
    /// <see cref="HudSizeMemoryTests"/>.
    /// </summary>
    public class HudPreviewSizingOrderTests
    {
        // The HUD's own numbers, so a reader sees the reproduction rather than infers it.
        private const double PillWidth = 367, PillHeight = 52;                 // QA's measured pill
        private const double DefaultPreviewWidth = 520, DefaultPreviewHeight = 400;
        private const double ResizedWidth = 1560, ResizedHeight = 400;         // 3x the default width (AC7)

        /// <summary>
        /// AC1, and the shipped defect. The panel is toggled on while the window is already on
        /// screen and auto-sized to the pill - the one path the round-2 fix broke. It must open at
        /// 520x400 with a picture area big enough to draw in, and nothing about the transition may
        /// be recorded as a size the person chose.
        /// </summary>
        [Fact]
        public void ShowPanel_OnAnAlreadyShownPillHud_OpensAtTheDefaultAndRemembersNothing()
        {
            RunOnHudRig(rig =>
            {
                Assert.Equal(PillWidth, rig.Window.ActualWidth, 1);      // the pill, as opened
                Assert.Equal(PillHeight, rig.Window.ActualHeight, 1);

                rig.ShowPanel();

                Assert.Equal(DefaultPreviewWidth, rig.Window.ActualWidth, 1);
                Assert.Equal(DefaultPreviewHeight, rig.Window.ActualHeight, 1);
                Assert.True(rig.Surface.ActualHeight > 100,
                    $"The preview surface is {rig.Surface.ActualWidth}x{rig.Surface.ActualHeight} - "
                    + "there is nothing left to draw a frame in. " + rig.Trace());
                Assert.False(rig.Memory.HasSize,
                    $"Nobody resized anything, yet {rig.Memory.Width}x{rig.Memory.Height} was "
                    + "recorded as a deliberate size and would be written to config. " + rig.Trace());
            });
        }

        /// <summary>
        /// AC7 end to end, at the sizes the criterion names: show the panel, take it to 3x the
        /// default width by resizing the real window, take the panel down the way a stop does, and
        /// ask what the next recording opens at.
        /// </summary>
        [Fact]
        public void ResizeToThreeTimesTheDefault_ThenStop_IsWhatTheNextRecordingOpensAt()
        {
            double? savedWidth = null, savedHeight = null;

            RunOnHudRig(rig =>
            {
                rig.ShowPanel();
                rig.UserResizesTheWindow(ResizedWidth, ResizedHeight);

                Assert.Equal(ResizedWidth, rig.Window.ActualWidth, 1);
                Assert.True(rig.Surface.ActualWidth > DefaultPreviewWidth,
                    "the preview surface did not grow with the window: " + rig.Trace());

                rig.HidePanel();          // what SetStatus does on an ordinary stop
                rig.Pump();

                // What SavePosition persists from the Closed handler, after the auto-size back.
                savedWidth = rig.Memory.Width;
                savedHeight = rig.Memory.Height;

                Assert.Equal(ResizedWidth, savedWidth!.Value, 1);
                Assert.Equal(ResizedHeight, savedHeight!.Value, 1);
            });

            // Recording 2: a new HUD seeded from that config opens the panel where it was left.
            RunOnHudRig(rig =>
            {
                rig.ShowPanel();

                Assert.Equal(ResizedWidth, rig.Window.ActualWidth, 1);
                Assert.Equal(ResizedHeight, rig.Window.ActualHeight, 1);
                Assert.Equal(ResizedWidth, rig.Memory.Width!.Value, 1);
            }, savedWidth, savedHeight);
        }

        /// <summary>
        /// The panel is hidden and shown again inside ONE recording, with no config write in
        /// between. Hiding auto-sizes the window back to the pill, so this is the second place the
        /// pill could poison the memory.
        /// </summary>
        [Fact]
        public void HidingAndShowingAgainInOneRecording_ReopensAtTheResizedSize()
        {
            RunOnHudRig(rig =>
            {
                rig.ShowPanel();
                rig.UserResizesTheWindow(ResizedWidth, ResizedHeight);

                rig.HidePanel();
                rig.Pump();
                Assert.Equal(PillWidth, rig.Window.ActualWidth, 1);   // back to the pill

                rig.ShowPanel();

                Assert.Equal(ResizedWidth, rig.Window.ActualWidth, 1);
                Assert.Equal(ResizedHeight, rig.Window.ActualHeight, 1);
            });
        }

        /// <summary>
        /// A HUD whose preview is never resized must leave no size behind at all - a size in config
        /// is a claim that the person chose one.
        /// </summary>
        [Fact]
        public void ShowThenHideWithoutResizing_LeavesNoSizeToPersist()
        {
            RunOnHudRig(rig =>
            {
                rig.ShowPanel();
                rig.HidePanel();
                rig.Pump();
                rig.ShowPanel();
                rig.HidePanel();
                rig.Pump();

                Assert.False(rig.Memory.HasSize,
                    $"{rig.Memory.Width}x{rig.Memory.Height} would be written to config, but the "
                    + "person never resized the HUD. " + rig.Trace());
            });
        }

        /// <summary>
        /// The stop path, which is where round 1's defect lived and where the pill gets its last
        /// chance at the config. The recording ends while the preview is still switched on, the
        /// window auto-sizes back to the pill, and what must be left to save is the size the person
        /// resized it to - not 367x52.
        /// </summary>
        [Fact]
        public void StoppingWhileThePreviewIsStillOn_LeavesTheResizedSizeToSave()
        {
            RunOnHudRig(rig =>
            {
                rig.ShowPanel();
                rig.UserResizesTheWindow(ResizedWidth, ResizedHeight);

                rig.StopRecording();

                Assert.Equal(PillWidth, rig.Window.ActualWidth, 1);   // the window IS the pill now
                Assert.Equal(ResizedWidth, rig.Memory.Width!.Value, 1);
                Assert.Equal(ResizedHeight, rig.Memory.Height!.Value, 1);
            });
        }

        // ---- the rig ---------------------------------------------------------

        /// <summary>
        /// A real WPF window in the HUD's sizing shape, driven by the HUD's own sizing code.
        /// Deliberately invisible (fully transparent, parked off the desktop, never activated) so
        /// `dotnet test` stays as quiet as it is today.
        /// </summary>
        private sealed class HudRig
        {
            public readonly Window Window;
            public readonly HudSizeMemory Memory;
            public readonly Border Surface;
            private readonly Grid _panel;
            private readonly List<string> _log = new();
            private bool _panelVisible;

            public HudRig(double? savedWidth, double? savedHeight)
            {
                Memory = new HudSizeMemory(savedWidth, savedHeight);

                Surface = new Border { Background = Brushes.Black };
                _panel = new Grid { Visibility = Visibility.Collapsed };
                _panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var strip = new Border { Height = 30 };            // the control strip
                Grid.SetRow(strip, 0);
                Grid.SetRow(Surface, 1);
                _panel.Children.Add(strip);
                _panel.Children.Add(Surface);

                var pill = new Border { Width = PillWidth, Height = PillHeight };
                var body = new Grid();
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(pill, 0);
                Grid.SetRow(_panel, 1);
                body.Children.Add(pill);
                body.Children.Add(_panel);

                Window = new Window
                {
                    Title = "HUD sizing rig",
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.CanResize,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Opacity = 0,                  // never seen by anyone running the suite
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Topmost = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    MinWidth = 260,
                    MinHeight = 52,
                    Left = -8000,
                    Top = -8000,
                    Content = body,
                };
                // Attached FIRST so the trace below reports the memory as it is AFTER the production
                // handler has had the report - the trace is evidence, and evidence that lags by one
                // event is worse than none.
                HudPreviewSizing.Attach(Window, Memory, () => _panelVisible);
                Window.SizeChanged += (_, _) => _log.Add(
                    $"SizeChanged stc={Window.SizeToContent} {Window.ActualWidth:0.##}x{Window.ActualHeight:0.##}"
                    + $" panel={_panelVisible} -> memory {Describe(Memory)}");
                Window.Show();
                Pump();
            }

            public void ShowPanel()
            {
                _panelVisible = true;
                _panel.Visibility = Visibility.Visible;
                _log.Add("-- show preview --");
                HudPreviewSizing.ShowPanel(Window, Memory, DefaultPreviewWidth, DefaultPreviewHeight);
                Pump();
            }

            public void HidePanel()
            {
                _panelVisible = false;
                _panel.Visibility = Visibility.Collapsed;
                _log.Add("-- hide preview --");
                HudPreviewSizing.HidePanel(Window, Memory);
            }

            /// <summary>
            /// What HudWindow.SetStatus does on an ordinary stop, faithfully: the panel is taken
            /// down and the window auto-sizes back to the pill, but the HUD's own "preview is on"
            /// flag is deliberately NOT cleared - the recording is simply over. So this is the one
            /// path on which the pill is reported while the preview still counts as visible, and it
            /// is the last chance for the pill to become the remembered size before it is saved.
            /// </summary>
            public void StopRecording()
            {
                _panel.Visibility = Visibility.Collapsed;
                _log.Add("-- stop (SetStatus) --");
                HudPreviewSizing.HidePanel(Window, Memory);
                Pump();
            }

            /// <summary>
            /// Resize the real HWND, which is what UI Automation's TransformPattern.Resize does and
            /// what a drag of the window border does - not a Width/Height assignment, which would
            /// only ever test the one path the code sets itself.
            /// </summary>
            public void UserResizesTheWindow(double width, double height)
            {
                _log.Add($"-- user resizes to {width}x{height} --");
                var hwnd = new System.Windows.Interop.WindowInteropHelper(Window).Handle;
                var dpi = VisualTreeHelper.GetDpi(Window);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                    (int)Math.Round(width * dpi.DpiScaleX), (int)Math.Round(height * dpi.DpiScaleY),
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                Pump();
            }

            /// <summary>Let WPF finish every layout, render and input-priority callback it has
            /// queued - the same pumping a live app does between two user actions.</summary>
            public void Pump()
            {
                for (int i = 0; i < 3; i++)
                {
                    var frame = new DispatcherFrame();
                    Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
            }

            public string Trace() =>
                Environment.NewLine + "Ordering:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", _log);

            private static string Describe(HudSizeMemory m) =>
                m.HasSize ? $"{m.Width:0.##}x{m.Height:0.##}" : "nothing";

            private const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
                int x, int y, int cx, int cy, uint flags);
        }

        /// <summary>
        /// Run a body against a fresh rig on a dedicated STA thread with its own dispatcher. The
        /// window is always closed and the dispatcher always shut down, pass or fail, so a failing
        /// assertion cannot leave a window behind for the rest of the suite.
        /// </summary>
        private static void RunOnHudRig(Action<HudRig> body, double? savedWidth = null, double? savedHeight = null)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                HudRig? rig = null;
                try
                {
                    rig = new HudRig(savedWidth, savedHeight);
                    body(rig);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { rig?.Window.Close(); } catch (Exception ex) { failure ??= ex; }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromSeconds(60)))
                throw new TimeoutException(
                    "The WPF HUD sizing rig did not finish within 60 s. This test needs an interactive "
                    + "desktop session: it creates a real (invisible, off-screen) window so the real "
                    + "SizeChanged ordering can be observed.");

            if (failure != null)
                throw new Xunit.Sdk.XunitException(
                    failure is Xunit.Sdk.XunitException
                        ? failure.Message
                        : failure.ToString());
        }
    }
}
