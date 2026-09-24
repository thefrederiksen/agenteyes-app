using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AgentEyes.AlwaysOn;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The app's side of always-on (issue #66): the saved defaults, the mapping from a recording setup
    /// to engine options, and what the tray says. Since issue #79 also the three keep settings - keep
    /// before the speech, keep after the speech, the silence that closes a clip: their defaults, their
    /// validation, how they persist, how a v1.11.x config.json is brought forward, and how
    /// GET /always-on reports them.
    /// </summary>
    public class AlwaysOnAppTests
    {
        [Fact]
        public void Config_Defaults_AreTheOwnersDecisions()
        {
            var c = new Config();

            Assert.False(c.AlwaysOnEnabled);
            Assert.Equal(10, c.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(10, c.AlwaysOnKeepAfterSeconds);
            Assert.Equal(300, c.AlwaysOnSilenceGapSeconds);
            Assert.Null(c.AlwaysOnBeforeMinutes);          // v1.11.x's fields: read only to migrate them
            Assert.Null(c.AlwaysOnAfterMinutes);
            Assert.Equal(5, c.AlwaysOnCapGb);
            Assert.Equal("mic", c.AlwaysOnCounts);
            Assert.Null(c.AlwaysOnThresholdDb);
            Assert.Null(c.AlwaysOnClipsFolder);
        }

        [Fact]
        public void Config_OldFileWithoutAlwaysOn_ReadsTheDefaults()
        {
            var c = Config.FromJson("{\"Port\":7882}");

            Assert.False(c.AlwaysOnEnabled);
            Assert.Equal(5, c.AlwaysOnCapGb);
            Assert.Equal(10, c.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(10, c.AlwaysOnKeepAfterSeconds);
            Assert.Equal(300, c.AlwaysOnSilenceGapSeconds);
        }

        // ---- issue #79: the keep settings persist in seconds --------------------------------------

        [Fact]
        public void Config_KeepSettings_RoundTripInSeconds_AndTheOldMinuteFieldsAreNotWritten()
        {
            var saved = new Config { AlwaysOnKeepBeforeSeconds = 30, AlwaysOnKeepAfterSeconds = 15, AlwaysOnSilenceGapSeconds = 600 };

            // The same serializer options Config.Save uses.
            string json = JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true });
            var back = Config.FromJson(json);

            Assert.Equal(30, back.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(15, back.AlwaysOnKeepAfterSeconds);
            Assert.Equal(600, back.AlwaysOnSilenceGapSeconds);
            Assert.Contains("\"AlwaysOnKeepBeforeSeconds\": 30", json);
            Assert.Contains("\"AlwaysOnKeepAfterSeconds\": 15", json);
            Assert.Contains("\"AlwaysOnSilenceGapSeconds\": 600", json);
            Assert.DoesNotContain("AlwaysOnBeforeMinutes", json);
            Assert.DoesNotContain("AlwaysOnAfterMinutes", json);
        }

        [Fact]
        public void FromJson_CurrentConfig_IsNotMigrated()
        {
            var c = Config.FromJson("{\"AlwaysOnKeepBeforeSeconds\": 30, \"AlwaysOnKeepAfterSeconds\": 20, \"AlwaysOnSilenceGapSeconds\": 120}");

            Assert.Equal(30, c.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(20, c.AlwaysOnKeepAfterSeconds);
            Assert.Equal(120, c.AlwaysOnSilenceGapSeconds);
        }

        // ---- issue #79: migration from a v1.11.x config.json --------------------------------------

        /// <summary>A config.json as v1.11.2 wrote it: whole minutes either side of the sound, 5 and 5 by
        /// default, and "keep after" was also the silence that closed a clip.</summary>
        private const string V111Config = @"{
  ""Port"": 7882,
  ""ApiEnabled"": true,
  ""RunAtLogin"": true,
  ""AutoUpdate"": true,
  ""LastUsedPresetId"": ""screen-1"",
  ""HudLeft"": null,
  ""HudTop"": null,
  ""PresetEditorTab"": 0,
  ""HudPreviewVisible"": false,
  ""HudPreviewMode"": ""screen"",
  ""CaptureRegionTrigger"": ""hotkey:printscreen"",
  ""HousekeepingEnabled"": true,
  ""HousekeepingReportOnly"": true,
  ""HousekeepingKeepVideoDays"": 30,
  ""AlwaysOnEnabled"": true,
  ""AlwaysOnHandPaused"": false,
  ""AlwaysOnPresetId"": ""screen-1"",
  ""AlwaysOnCounts"": ""mic"",
  ""AlwaysOnThresholdDb"": null,
  ""AlwaysOnBeforeMinutes"": 5,
  ""AlwaysOnAfterMinutes"": 5,
  ""AlwaysOnCapGb"": 5,
  ""AlwaysOnClipsFolder"": null
}";

        [Fact]
        public void FromJson_V111Config_MigratesKeepAfterToTheSilenceGapAndTakesTheNewSecondDefaults()
        {
            var c = Config.FromJson(V111Config);

            // "keep after" was what closed a clip: 5 min becomes the silence gap. The lead-in and the tail
            // take the new defaults - the old minute values meant "a whole piece either side".
            Assert.Equal(300, c.AlwaysOnSilenceGapSeconds);
            Assert.Equal(10, c.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(10, c.AlwaysOnKeepAfterSeconds);
            // The old fields are cleared, so the next save does not write them and the migration runs once.
            Assert.Null(c.AlwaysOnBeforeMinutes);
            Assert.Null(c.AlwaysOnAfterMinutes);
            string rewritten = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });
            Assert.DoesNotContain("AlwaysOnBeforeMinutes", rewritten);
            Assert.DoesNotContain("AlwaysOnAfterMinutes", rewritten);
            Assert.Contains("\"AlwaysOnSilenceGapSeconds\": 300", rewritten);
            // Everything else came through untouched.
            Assert.True(c.AlwaysOnEnabled);
            Assert.Equal("screen-1", c.AlwaysOnPresetId);
            Assert.Equal("mic", c.AlwaysOnCounts);
            Assert.Equal(5, c.AlwaysOnCapGb);
            Assert.True(c.RunAtLogin);
            // The migration is on record in the log.
            Assert.Contains("[Config] MigrateAlwaysOnKeepSettings: v1.11 keep settings (before=5 min, after=5 min) -> "
                            + "keep before 10s, keep after 10s, silence gap 300s",
                File.ReadAllText(AgentEyes.Log.CurrentFile));
        }

        [Theory]
        [InlineData(1, 60)]
        [InlineData(2.5, 150)]
        [InlineData(10, 600)]
        [InlineData(30, 1800)]
        public void FromJson_V111ConfigWithAChosenKeepAfter_TheGapIsThatManyMinutes(double afterMinutes, double expectedGapSeconds)
        {
            string json = V111Config.Replace("\"AlwaysOnAfterMinutes\": 5", "\"AlwaysOnAfterMinutes\": " + afterMinutes.ToString(CultureInfo.InvariantCulture));

            var c = Config.FromJson(json);

            Assert.Equal(expectedGapSeconds, c.AlwaysOnSilenceGapSeconds);
            Assert.Equal(10, c.AlwaysOnKeepAfterSeconds);
            Assert.Equal(10, c.AlwaysOnKeepBeforeSeconds);
        }

        [Theory]
        [InlineData(0.25, 30)]      // hand-edited below the gap's range (the page offered 1-30 min): the nearest end
        [InlineData(45, 1800)]
        public void FromJson_V111ConfigWithAKeepAfterOutsideTheGapsRange_IsBroughtToTheNearestEnd(double afterMinutes, double expectedGapSeconds)
        {
            string json = V111Config.Replace("\"AlwaysOnAfterMinutes\": 5", "\"AlwaysOnAfterMinutes\": " + afterMinutes.ToString(CultureInfo.InvariantCulture));

            var c = Config.FromJson(json);

            Assert.Equal(expectedGapSeconds, c.AlwaysOnSilenceGapSeconds);
            Assert.Null(AlwaysOnKeepSettings.Problem(TimeSpan.FromSeconds(c.AlwaysOnKeepBeforeSeconds),
                TimeSpan.FromSeconds(c.AlwaysOnKeepAfterSeconds), TimeSpan.FromSeconds(c.AlwaysOnSilenceGapSeconds)));
        }

        [Fact]
        public void FromJson_V111ConfigWithOnlyKeepBefore_TheGapStaysTheDefault()
        {
            var c = Config.FromJson("{\"AlwaysOnBeforeMinutes\": 2}");

            Assert.Equal(300, c.AlwaysOnSilenceGapSeconds);
            Assert.Equal(10, c.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(10, c.AlwaysOnKeepAfterSeconds);
            Assert.Null(c.AlwaysOnBeforeMinutes);
        }

        [Fact]
        public void MigrateAlwaysOnKeepSettings_RunsOnce_ASecondCallLeavesLaterChoicesAlone()
        {
            var c = Config.FromJson(V111Config);
            c.AlwaysOnKeepBeforeSeconds = 45;               // the owner's later choice on the page

            c.MigrateAlwaysOnKeepSettings();

            Assert.Equal(45, c.AlwaysOnKeepBeforeSeconds);
            Assert.Equal(300, c.AlwaysOnSilenceGapSeconds);
        }

        // ---- issue #79: the controller refuses settings out of range --------------------------------

        [Fact]
        public void KeepSettings_InRange_ReturnsTheThreeAsTimeSpans()
        {
            var cfg = new Config { AlwaysOnKeepBeforeSeconds = 30, AlwaysOnKeepAfterSeconds = 20, AlwaysOnSilenceGapSeconds = 120 };

            var (before, after, gap) = AlwaysOnController.KeepSettings(cfg);

            Assert.Equal(TimeSpan.FromSeconds(30), before);
            Assert.Equal(TimeSpan.FromSeconds(20), after);
            Assert.Equal(TimeSpan.FromSeconds(120), gap);
        }

        [Theory]
        [InlineData(121, 10, 300, "Keep before the speech must be 0 s to 2 min, not 2 min 1 s.")]
        [InlineData(-1, 10, 300, "Keep before the speech must be 0 s to 2 min, not -1 s.")]
        [InlineData(10, 10, 29, "Close the clip after silence must be 30 s to 30 min, not 29 s.")]
        [InlineData(10, 10, 1801, "Close the clip after silence must be 30 s to 30 min, not 30 min 1 s.")]
        [InlineData(10, 301, 300, "Keep after the speech must be 0 s to the silence gap (5 min), not 5 min 1 s.")]
        public void KeepSettings_ConfigOutOfRange_RefusesWithTheReasonAndWhereToFixIt(double before, double after, double gap, string reason)
        {
            var cfg = new Config { AlwaysOnKeepBeforeSeconds = before, AlwaysOnKeepAfterSeconds = after, AlwaysOnSilenceGapSeconds = gap };

            var ex = Assert.Throws<UsageException>(() => AlwaysOnController.KeepSettings(cfg));

            Assert.Equal(reason + " Change it on the Always On page.", ex.Message);
        }

        [Fact]
        public void BuildOptions_SystemOnlySetup_RecordsSystemAndNoMic()
        {
            var preset = new CapturePreset { Name = "Movies", MonitorIndex = 1, Source = "system", SysVol = 50, Mode = "video" };
            var cfg = new Config
            {
                AlwaysOnCounts = "system", AlwaysOnKeepBeforeSeconds = 20, AlwaysOnKeepAfterSeconds = 30, AlwaysOnSilenceGapSeconds = 120,
                AlwaysOnCapGb = 1, AlwaysOnThresholdDb = -45,
            };

            var o = AlwaysOnController.BuildOptions(cfg, preset, @"C:\clips");

            Assert.Equal("Movies", o.SetupName);
            Assert.True(o.RecordSystem);
            Assert.Null(o.DshowMic);
            Assert.Null(o.MicLevelDevice);
            Assert.Equal(SoundSource.System, o.Counts);
            Assert.Equal(-45, o.ThresholdDb);
            Assert.Equal(TimeSpan.FromSeconds(20), o.KeepBefore);
            Assert.Equal(TimeSpan.FromSeconds(30), o.KeepAfter);
            Assert.Equal(TimeSpan.FromSeconds(120), o.SilenceGap);
            Assert.Equal(new KeepWindows(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), o.Windows);
            Assert.Equal(AlwaysOnArgs.DefaultKeyframeSeconds, o.KeyframeSeconds);
            Assert.Equal(1L * 1024 * 1024 * 1024, o.CapBytes);
            Assert.Equal(0.5, o.SystemGain);
            Assert.Equal(@"C:\clips", o.ClipsFolder);
            Assert.Equal(Monitors.Require(1).Bounds, o.Capture);
        }

        [Fact]
        public void BuildOptions_FreshConfig_UsesTheIssue79Defaults()
        {
            var preset = new CapturePreset { MonitorIndex = 1, Source = "system", Mode = "video" };

            var o = AlwaysOnController.BuildOptions(new Config { AlwaysOnCounts = "system" }, preset, @"C:\clips");

            Assert.Equal(TimeSpan.FromSeconds(10), o.KeepBefore);
            Assert.Equal(TimeSpan.FromSeconds(10), o.KeepAfter);
            Assert.Equal(TimeSpan.FromMinutes(5), o.SilenceGap);
            Assert.Equal(2, o.KeyframeSeconds);
            Assert.Equal(60, o.PieceSeconds);
        }

        [Fact]
        public void BuildOptions_KeepSettingsOutOfRange_RefusesToBuild()
        {
            var preset = new CapturePreset { MonitorIndex = 1, Source = "system", Mode = "video" };
            var cfg = new Config { AlwaysOnCounts = "system", AlwaysOnKeepBeforeSeconds = 200 };

            var ex = Assert.Throws<UsageException>(() => AlwaysOnController.BuildOptions(cfg, preset, @"C:\clips"));

            Assert.StartsWith("Keep before the speech must be 0 s to 2 min, not 3 min 20 s.", ex.Message);
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

        // ---- issue #79: the Always On page's sentence -------------------------------------------------

        [Fact]
        public void RuleInWords_StatesTheThreeKeepSettingsAsThePageShowsThem()
        {
            Assert.Equal("The rule in words: record the whole time. A clip starts 10 s before the microphone sound begins "
                         + "and ends 10 s after it stops; a pause shorter than 5 min stays inside the clip, "
                         + "and 5 min of quiet closes it. Everything else is deleted within minutes.",
                MainWindow.RuleInWords("microphone sound", 10, 10, 300));

            string custom = MainWindow.RuleInWords("system sound", 90, 0, 60);
            Assert.Contains("starts 1 min 30 s before the system sound begins", custom);
            Assert.Contains("ends 0 s after it stops", custom);
            Assert.Contains("a pause shorter than 1 min stays inside the clip", custom);
        }

        // ---- issue #79: GET /always-on reports the saved keep settings ----------------------------------

        [Fact]
        public async System.Threading.Tasks.Task GetAlwaysOn_ReturnsTheSavedKeepSettingsInSeconds()
        {
            // The three settings as saved - what the next start uses - in one "settings" object.
            var cfg = new Config { AlwaysOnKeepBeforeSeconds = 20, AlwaysOnKeepAfterSeconds = 15, AlwaysOnSilenceGapSeconds = 90 };
            var engine = new AlwaysOnEngine(() => throw new InvalidOperationException("no recorder in this test"),
                () => DateTime.UtcNow, ownTimer: false);
            var svc = new RecordingService();
            using var ao = new AlwaysOnController(svc, cfg, engine);
            int port = FreePort();
            using var server = new RestServer(svc, port) { AlwaysOn = ao };
            server.Start();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            string body = await http.GetStringAsync($"http://127.0.0.1:{port}/always-on");

            using var doc = JsonDocument.Parse(body);
            var settings = doc.RootElement.GetProperty("settings");
            Assert.Equal(20, settings.GetProperty("keepBeforeSeconds").GetDouble());
            Assert.Equal(15, settings.GetProperty("keepAfterSeconds").GetDouble());
            Assert.Equal(90, settings.GetProperty("silenceGapSeconds").GetDouble());
            Assert.Equal("off", doc.RootElement.GetProperty("status").GetProperty("State").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("openClip").ValueKind);
            // The minute fields of v1.11.x are gone from the API too.
            Assert.DoesNotContain("afterMinutes", body);
            Assert.DoesNotContain("beforeMinutes", body);
            Assert.DoesNotContain("KeepAfterMinutes", body);
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // ---- the tray --------------------------------------------------------------------------------

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

        // ---- issue #70: the clip in progress on the tray and in the Control API ----

        private static readonly DateTime ClipStart = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);

        private static AlwaysOnStatus Keeping(string folder) => new()
        {
            State = AlwaysOnState.Keeping, Counts = "mic", ClipsToday = 2, DiskUsedBytes = 13L * 1024 * 1024, CapGb = 5,
            ClipsFolder = folder, SilenceGapSeconds = 300, OpenClipStartUtc = ClipStart,
        };

        [Fact]
        public void Tooltip_KeepingWithAClipInProgress_SaysHowLongAndWhere()
        {
            string tip = TrayDot.Tooltip(Keeping(@"C:\AgentEyes"), ClipStart.AddMinutes(12).AddSeconds(40));
            Assert.Equal("AgentEyes - Always-on recording\nClip in progress, 12 min so far - saved to C:\\AgentEyes after 5 min quiet.", tip);
            Assert.True(tip.Length <= TrayDot.MaxTooltip);
        }

        [Fact]
        public void Tooltip_KeepingWithALongFolder_KeepsTheInProgressFactAndCutsAtTheLimit()
        {
            string folder = @"C:\Users\someone\Videos\AgentEyes\Always on clips";
            string tip = TrayDot.Tooltip(Keeping(folder), ClipStart.AddMinutes(3));
            Assert.StartsWith("AgentEyes - Always-on recording\nClip in progress, 3 min so far - saved to C:\\Users\\someone", tip);
            Assert.EndsWith("...", tip);
            Assert.Equal(TrayDot.MaxTooltip, tip.Length);
        }

        [Fact]
        public void InProgressLine_StatesInProgressElapsedAndDestination()
        {
            var s = Keeping(@"C:\AgentEyes");
            Assert.Equal(@"Recording a clip now - 12 min so far. Saved to C:\AgentEyes after 5 min of quiet.",
                s.InProgressLine(ClipStart.AddMinutes(12)));
            // Issue #79: the silence gap is in seconds and is said as the page says it.
            s.SilenceGapSeconds = 150;
            Assert.Equal(@"Recording a clip now - 1 h 5 min so far. Saved to C:\AgentEyes after 2 min 30 s of quiet.",
                s.InProgressLine(ClipStart.AddMinutes(65)));
            s.OpenClipStartUtc = null;
            Assert.Null(s.InProgressLine(ClipStart.AddMinutes(65)));
            Assert.Null(s.InProgressShort(ClipStart.AddMinutes(65)));
        }

        [Theory]
        [InlineData(0, "under 1 min")]
        [InlineData(59.9, "under 1 min")]
        [InlineData(60, "1 min")]
        [InlineData(12 * 60 + 59, "12 min")]
        [InlineData(3600, "1 h 0 min")]
        [InlineData(2 * 3600 + 5 * 60, "2 h 5 min")]
        public void ClipDuration_RoundsDownToWholeMinutes(double seconds, string expected)
        {
            Assert.Equal(expected, AlwaysOnStatus.ClipDuration(seconds));
        }

        [Fact]
        public void OpenClip_ForTheApi_IsNullWithNoClipAndFreshElapsedWithOne()
        {
            var s = Keeping(@"C:\AgentEyes");
            s.OpenClipElapsedSeconds = 60;                  // as of the last keeper pass
            var open = RestServer.OpenClip(s, ClipStart.AddSeconds(95));
            Assert.NotNull(open);
            Assert.Equal(ClipStart, open!.StartUtc);
            Assert.Equal(95.0, open.ElapsedSeconds);        // as of the request, not the pass
            Assert.Equal(@"C:\AgentEyes", open.SavedTo);
            Assert.Equal(300.0, open.SilenceGapSeconds);    // issue #79: the gap that will close it, in seconds

            s.OpenClipStartUtc = null;
            Assert.Null(RestServer.OpenClip(s, ClipStart.AddSeconds(95)));
        }

        [Fact]
        public void RevealClip_ClipNoLongerThere_ThrowsWithTheReasonInsteadOfOpeningAnotherFolder()
        {
            string gone = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agenteyes-gone-" + Guid.NewGuid().ToString("N") + ".mp4");
            var engine = new AlwaysOnEngine(() => throw new InvalidOperationException("no recorder in this test"),
                () => DateTime.UtcNow, ownTimer: false);
            using var ao = new AlwaysOnController(new RecordingService(), new Config(), engine);

            var ex = Assert.Throws<UsageException>(() => ao.RevealClip(gone));

            Assert.Contains(System.IO.Path.GetFileName(gone) + " is no longer in", ex.Message);
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
