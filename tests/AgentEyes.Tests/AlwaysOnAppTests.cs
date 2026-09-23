using System;
using AgentEyes.AlwaysOn;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>The app's side of always-on (issue #66): the saved defaults, the mapping from a
    /// recording setup to engine options, and what the tray says.</summary>
    public class AlwaysOnAppTests
    {
        [Fact]
        public void Config_Defaults_AreTheOwnersDecisions()
        {
            var c = new Config();

            Assert.False(c.AlwaysOnEnabled);
            Assert.Equal(5, c.AlwaysOnBeforeMinutes);
            Assert.Equal(5, c.AlwaysOnAfterMinutes);
            Assert.Equal(5, c.AlwaysOnCapGb);
            Assert.Equal("mic", c.AlwaysOnCounts);
            Assert.Null(c.AlwaysOnThresholdDb);
            Assert.Null(c.AlwaysOnClipsFolder);
        }

        [Fact]
        public void Config_OldFileWithoutAlwaysOn_ReadsTheDefaults()
        {
            var c = System.Text.Json.JsonSerializer.Deserialize<Config>("{\"Port\":7882}")!;

            Assert.False(c.AlwaysOnEnabled);
            Assert.Equal(5, c.AlwaysOnCapGb);
        }

        [Fact]
        public void BuildOptions_SystemOnlySetup_RecordsSystemAndNoMic()
        {
            var preset = new CapturePreset { Name = "Movies", MonitorIndex = 1, Source = "system", SysVol = 50, Mode = "video" };
            var cfg = new Config { AlwaysOnCounts = "system", AlwaysOnBeforeMinutes = 2, AlwaysOnAfterMinutes = 3, AlwaysOnCapGb = 1, AlwaysOnThresholdDb = -45 };

            var o = AlwaysOnController.BuildOptions(cfg, preset, @"C:\clips");

            Assert.Equal("Movies", o.SetupName);
            Assert.True(o.RecordSystem);
            Assert.Null(o.DshowMic);
            Assert.Null(o.MicLevelDevice);
            Assert.Equal(SoundSource.System, o.Counts);
            Assert.Equal(-45, o.ThresholdDb);
            Assert.Equal(TimeSpan.FromMinutes(2), o.KeepBefore);
            Assert.Equal(TimeSpan.FromMinutes(3), o.KeepAfter);
            Assert.Equal(1L * 1024 * 1024 * 1024, o.CapBytes);
            Assert.Equal(0.5, o.SystemGain);
            Assert.Equal(@"C:\clips", o.ClipsFolder);
            Assert.Equal(Monitors.Require(1).Bounds, o.Capture);
        }

        [Fact]
        public void BuildOptions_RegionSetup_RecordsTheRegion()
        {
            var preset = new CapturePreset { MonitorIndex = 1, Source = "system", UseRegion = true, Region = new[] { 10, 20, 640, 360 }, Mode = "video" };
            var o = AlwaysOnController.BuildOptions(new Config { AlwaysOnCounts = "system" }, preset, @"C:\clips");

            Assert.Equal(new System.Drawing.Rectangle(10, 20, 640, 360), o.Capture);
        }

        [Fact]
        public void BuildOptions_ZeroCap_MeansNoCap()
        {
            var preset = new CapturePreset { MonitorIndex = 1, Source = "system", Mode = "video" };
            var o = AlwaysOnController.BuildOptions(new Config { AlwaysOnCounts = "system", AlwaysOnCapGb = 0 }, preset, @"C:\clips");

            Assert.Equal(0, o.CapBytes);
        }

        [Fact]
        public void ParseCounts_ReadsTheSavedValue()
        {
            Assert.Equal(SoundSource.Mic, AlwaysOnController.ParseCounts("mic"));
            Assert.Equal(SoundSource.System, AlwaysOnController.ParseCounts("system"));
            Assert.Equal(SoundSource.Both, AlwaysOnController.ParseCounts("both"));
        }

        private static AlwaysOnStatus Status(string state, int clips = 7, long bytes = 2_200_000_000) => new()
        {
            State = state, Counts = "mic", ClipsToday = clips, DiskUsedBytes = bytes, CapGb = 5,
            PausedReason = AlwaysOnController.PausedForRecording, LastError = "the monitor is gone",
        };

        [Fact]
        public void Tooltip_Listening_SaysSoWithTodaysTotals()
        {
            Assert.Equal("AgentEyes - Always-on recording\nListening (microphone). Today: 7 clips, 2.0 GB of 5 GB",
                TrayDot.Tooltip(Status(AlwaysOnState.Listening)));
        }

        [Fact]
        public void Tooltip_Keeping_Paused_Retrying_Off()
        {
            Assert.StartsWith("AgentEyes - Always-on recording\nKeeping a clip", TrayDot.Tooltip(Status(AlwaysOnState.Keeping)));
            Assert.StartsWith("AgentEyes - Always-on paused\nA normal recording is running.", TrayDot.Tooltip(Status(AlwaysOnState.Paused)));
            Assert.Equal("AgentEyes - Always-on NOT recording\nthe monitor is gone - retrying", TrayDot.Tooltip(Status(AlwaysOnState.Retrying)));
            Assert.Equal("AgentEyes", TrayDot.Tooltip(Status(AlwaysOnState.Off)));
        }

        [Fact]
        public void Compose_DrawsTheDotInTheCorner_RedGreyOrWithAWhiteCentre()
        {
            using var baseIcon = System.Drawing.SystemIcons.Application;
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agenteyes-traydot");
            System.IO.Directory.CreateDirectory(dir);

            System.Drawing.Color Centre(System.Drawing.Icon icon, string name)
            {
                using var bmp = icon.ToBitmap();
                bmp.Save(System.IO.Path.Combine(dir, name + ".png"));
                float d = bmp.Width * 0.56f;
                int c = (int)(bmp.Width - d / 2 - 0.5f);
                return bmp.GetPixel(c, c);
            }
            // Solid red: the centre of the dot is the dot's colour.
            using (var listening = TrayDot.Compose(baseIcon, TrayDot.Red, centre: null))
                Assert.Equal(TrayDot.Red.ToArgb(), Centre(listening, "listening").ToArgb());
            // Keeping: red with a white centre.
            using (var keeping = TrayDot.Compose(baseIcon, TrayDot.Red, centre: System.Drawing.Color.White))
                Assert.Equal(System.Drawing.Color.White.ToArgb(), Centre(keeping, "keeping").ToArgb());
            using (var paused = TrayDot.Compose(baseIcon, TrayDot.Grey, centre: null))
                Assert.Equal(TrayDot.Grey.ToArgb(), Centre(paused, "paused").ToArgb());
        }

        [Fact]
        public void Tooltip_NeverExceedsWhatWindowsShows()
        {
            var s = Status(AlwaysOnState.Retrying);
            s.LastError = new string('x', 400);

            Assert.True(TrayDot.Tooltip(s).Length <= TrayDot.MaxTooltip);
        }

        [Fact]
        public void Config_HandPause_DefaultsToNotPausedAndRoundTrips()
        {
            Assert.False(new Config().AlwaysOnHandPaused);
            var back = System.Text.Json.JsonSerializer.Deserialize<Config>(
                System.Text.Json.JsonSerializer.Serialize(new Config { AlwaysOnHandPaused = true }))!;
            Assert.True(back.AlwaysOnHandPaused);
        }

        [Fact]
        public void ReconcileAction_NormalRecordingStarts_PausesAlwaysOn()
        {
            Assert.Equal(AlwaysOnController.ReconcileStep.Pause,
                AlwaysOnController.ReconcileAction(true, AlwaysOnState.Listening, null, recording: true));
            Assert.Equal(AlwaysOnController.ReconcileStep.Pause,
                AlwaysOnController.ReconcileAction(true, AlwaysOnState.Keeping, null, recording: true));
        }

        [Fact]
        public void ReconcileAction_RecordingEnded_ResumesOnlyAPauseForTheRecording()
        {
            Assert.Equal(AlwaysOnController.ReconcileStep.Resume,
                AlwaysOnController.ReconcileAction(true, AlwaysOnState.Paused, AlwaysOnController.PausedForRecording, recording: false));
        }

        [Fact]
        public void ReconcileAction_HandPause_IsNeverResumedOrRepaused()
        {
            Assert.Equal(AlwaysOnController.ReconcileStep.None,
                AlwaysOnController.ReconcileAction(true, AlwaysOnState.Paused, AlwaysOnController.PausedByHand, recording: false));
            Assert.Equal(AlwaysOnController.ReconcileStep.None,
                AlwaysOnController.ReconcileAction(true, AlwaysOnState.Paused, AlwaysOnController.PausedByHand, recording: true));
        }

        [Fact]
        public void ReconcileAction_Off_DoesNothing()
        {
            Assert.Equal(AlwaysOnController.ReconcileStep.None,
                AlwaysOnController.ReconcileAction(false, AlwaysOnState.Off, null, recording: true));
            Assert.Equal(AlwaysOnController.ReconcileStep.None,
                AlwaysOnController.ReconcileAction(false, AlwaysOnState.Off, null, recording: false));
        }
    }
}
