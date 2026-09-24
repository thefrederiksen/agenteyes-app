using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEyes.AlwaysOn;
using AgentEyes.App;
using AgentEyes.Audio;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #77: the always-on history - the store and its 7-day trim, the filter, every event the
    /// engine records (start/stop with why, device facts, level lines with peak and average, KEEP and
    /// DELETE, clips written, capture failures and restarts), the silent-microphone rule as the tester
    /// redefined it (Windows mute state OR no loud second for ten minutes - NEVER the gated floor), the
    /// banner text in the status, GET /always-on/history over real HTTP, and the History tab's pure helpers.
    /// </summary>
    public class AlwaysOnHistoryTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private DateTime _now = T0;
        private readonly List<FakeRecorder> _recorders = new();
        private Func<FakeRecorder> _make = () => new FakeRecorder();
        private MicEndpointState _mic = new("Headset Microphone (USB Audio)", Muted: false, VolumePercent: 80);
        private Exception? _micError;
        private int _micReads;

        public AlwaysOnHistoryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private AlwaysOnHistory History(string name = "history.jsonl") => new(Path.Combine(_root, name), () => _now);

        private AlwaysOnOptions Options(SoundSource counts = SoundSource.Mic, double? threshold = -40, string? dshowMic = "Headset Microphone (USB Audio)") => new()
        {
            SetupName = "test",
            Counts = counts,
            ThresholdDb = threshold,
            DshowMic = dshowMic,
            MicLevelDevice = counts == SoundSource.System ? null : "Headset Microphone",
            RecordSystem = false,
            KeepBefore = TimeSpan.FromMinutes(2),
            KeepAfter = TimeSpan.FromMinutes(2),
            SilenceGap = TimeSpan.FromMinutes(5),
            CapBytes = 5L * 1024 * 1024 * 1024,
            ClipsFolder = Path.Combine(_root, "clips"),
            WorkFolder = Path.Combine(_root, "work"),
            PieceSeconds = 60,
        };

        private AlwaysOnEngine Engine(AlwaysOnHistory history) => new(() =>
        {
            var r = _make();
            _recorders.Add(r);
            return r;
        }, () => _now, ownTimer: false, history, name =>
        {
            _micReads++;
            if (_micError != null) throw _micError;
            return _mic;
        });

        /// <summary>The events oldest first - the order they happened in.</summary>
        private static List<AlwaysOnEvent> Oldest(AlwaysOnHistory h, HistoryFilter f = HistoryFilter.All)
        {
            var list = h.Events(null, f);
            list.Reverse();
            return list;
        }

        private static AlwaysOnEvent Ev(DateTime at, HistoryKind kind, HistorySeverity sev, string text, string? detail = null) =>
            new() { AtUtc = at, Kind = kind, Severity = sev, Text = text, Detail = detail };

        /// <summary>
        /// A piece FILE (a few bytes, not a video) named as ffmpeg names the piece that opened at
        /// <paramref name="startUtc"/>: enough for the supervisor to see the capture writing - a fake
        /// capture that never opens a piece is declared hung after 80 s and restarted, which resets the
        /// silent-microphone clock. Only for runs where no piece is kept (a kept piece is probed and joined).
        /// </summary>
        private static void WriteFakePiece(string folder, DateTime startUtc)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4"), new byte[] { 0, 0, 0, 1 });
        }

        /// <summary>One buffer per second at <paramref name="db"/> dBFS RMS, for <paramref name="seconds"/> seconds from <paramref name="from"/>.</summary>
        private static void Level(SoundLog log, DateTime from, int seconds, double db)
        {
            for (int s = 0; s < seconds; s++)
                log.Observe(SoundSource.Mic, from.AddSeconds(s), AudioLevel.FromDb(db, db + 8, 48000));
        }

        /// <summary>Four seconds of talking at -20 dBFS RMS from <paramref name="at"/>, then a quiet buffer so the last second is judged.</summary>
        private static void Speak(SoundLog log, DateTime at)
        {
            Level(log, at, 4, -20);
            log.Observe(SoundSource.Mic, at.AddSeconds(4), AudioLevel.FromDb(-80, -70, 48000));
        }

        /// <summary>A real two-second MP4, named as ffmpeg names a piece that opened at <paramref name="startUtc"/>.</summary>
        private static void WritePiece(string folder, DateTime startUtc)
        {
            Directory.CreateDirectory(folder);
            string name = "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4";
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "color=c=gray:s=160x90:r=10:d=2",
                "-f", "lavfi", "-i", "sine=f=300:d=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
                Path.Combine(folder, name),
            }, "test piece");
        }

        // ---- the store -----------------------------------------------------------------------------

        [Fact]
        public void Append_ThenEvents_ReturnsNewestFirst_AndWritesOneJsonLinePerEvent()
        {
            var h = History();
            h.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "Always-on started (test)"));
            h.Append(Ev(T0.AddMinutes(1), HistoryKind.Level, HistorySeverity.Info, "Levels: mic floor=-70.0dBFS"));
            h.Append(Ev(T0.AddMinutes(2), HistoryKind.Problem, HistorySeverity.Error, "Capture failed", detail: "line 1\nline 2"));

            var events = h.Events(null, HistoryFilter.All);

            Assert.Equal(new[] { "Capture failed", "Levels: mic floor=-70.0dBFS", "Always-on started (test)" }, events.Select(e => e.Text));
            var lines = File.ReadAllLines(h.Path);
            Assert.Equal(3, lines.Length);
            using var doc = JsonDocument.Parse(lines[2]);
            Assert.Equal("problem", doc.RootElement.GetProperty("kind").GetString());
            Assert.Equal("error", doc.RootElement.GetProperty("severity").GetString());
            Assert.Equal("line 1\nline 2", doc.RootElement.GetProperty("detail").GetString());
            Assert.Equal(T0.AddMinutes(2), doc.RootElement.GetProperty("atUtc").GetDateTime().ToUniversalTime());
            Assert.False(lines[0].Contains("detail"), "a null detail is not written");
        }

        [Fact]
        public void Events_ANewInstanceOnTheSameFile_ReadsWhatWasAppended()
        {
            // The app restarts: a new engine, a new history object, the same file.
            var first = History();
            first.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "Always-on started (test)"));
            first.Append(Ev(T0.AddMinutes(1), HistoryKind.Decision, HistorySeverity.Info, "KEEP piece_a (60s) -> clip_1"));

            var second = History();

            Assert.Equal(2, second.Count);
            var events = second.Events(null, HistoryFilter.All);
            Assert.Equal("KEEP piece_a (60s) -> clip_1", events[0].Text);
            Assert.Equal(HistoryKind.Decision, events[0].Kind);
            Assert.Equal(DateTimeKind.Utc, events[0].AtUtc.Kind);
            Assert.Equal(T0.AddMinutes(1), events[0].AtUtc);
        }

        [Fact]
        public void KeepFromUtc_IsMidnightLocalOfTheDaySevenDaysBeforeToday()
        {
            var keepFrom = AlwaysOnHistory.KeepFromUtc(_now);

            var local = keepFrom.ToLocalTime();
            Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
            Assert.Equal(_now.ToLocalTime().Date.AddDays(-7), local.Date);
        }

        [Fact]
        public void Load_EventsOlderThanSevenDays_AreDroppedAndTheFileRewritten()
        {
            string path = Path.Combine(_root, "history.jsonl");
            DateTime keepFrom = AlwaysOnHistory.KeepFromUtc(_now);
            File.WriteAllLines(path, new[]
            {
                AlwaysOnHistory.Serialize(Ev(keepFrom.AddDays(-3), HistoryKind.Level, HistorySeverity.Info, "ten days old")),
                AlwaysOnHistory.Serialize(Ev(keepFrom.AddSeconds(-1), HistoryKind.Level, HistorySeverity.Info, "a second too old")),
                AlwaysOnHistory.Serialize(Ev(keepFrom, HistoryKind.Level, HistorySeverity.Info, "exactly seven days ago")),
                AlwaysOnHistory.Serialize(Ev(_now, HistoryKind.Level, HistorySeverity.Info, "today")),
            });

            var h = History();
            var events = Oldest(h);

            Assert.Equal(new[] { "exactly seven days ago", "today" }, events.Select(e => e.Text));
            Assert.Equal(2, File.ReadAllLines(path).Length);        // rewritten, not merely filtered in memory
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void Append_OnANewDay_TrimsWhatFellOutOfTheRetention()
        {
            var h = History();
            DateTime keepFrom = AlwaysOnHistory.KeepFromUtc(_now);
            h.Append(Ev(keepFrom, HistoryKind.Level, HistorySeverity.Info, "oldest kept today"));
            h.Append(Ev(_now, HistoryKind.Level, HistorySeverity.Info, "today"));
            Assert.Equal(2, h.Count);

            // Tomorrow: the oldest line is now eight days old.
            _now = _now.AddDays(1);
            h.Append(Ev(_now, HistoryKind.Level, HistorySeverity.Info, "tomorrow"));

            Assert.Equal(new[] { "today", "tomorrow" }, Oldest(h).Select(e => e.Text));
            Assert.Equal(2, File.ReadAllLines(h.Path).Length);
        }

        [Fact]
        public void Load_ATornLine_IsSkippedAndTheRestKept()
        {
            string path = Path.Combine(_root, "history.jsonl");
            File.WriteAllLines(path, new[]
            {
                AlwaysOnHistory.Serialize(Ev(_now.AddMinutes(-2), HistoryKind.State, HistorySeverity.Info, "first")),
                "{\"atUtc\":\"2026-09-24T08:0",           // a write cut short
                "not json at all",
                AlwaysOnHistory.Serialize(Ev(_now.AddMinutes(-1), HistoryKind.State, HistorySeverity.Info, "second")),
            });

            var h = History();

            Assert.Equal(new[] { "first", "second" }, Oldest(h).Select(e => e.Text));
            Assert.Equal(2, File.ReadAllLines(path).Length);        // the torn lines are gone from the file too
            Assert.Contains("2 unreadable line(s) skipped", File.ReadAllText(Log.CurrentFile));
        }

        [Theory]
        [InlineData("Decision", "Info", true, true, false, false)]
        [InlineData("Clip", "Info", true, true, false, false)]
        [InlineData("Level", "Info", true, false, true, false)]
        [InlineData("Level", "Warning", true, false, true, true)]   // a flagged level line is a problem too
        [InlineData("State", "Info", true, false, false, false)]
        [InlineData("Device", "Warning", true, false, false, true)]
        [InlineData("Problem", "Error", true, false, false, true)]
        [InlineData("Problem", "Info", true, false, false, false)]  // a recovery is not a problem
        public void Matches_EachFilter_SelectsByKindOrSeverity(string kindName, string sevName, bool all, bool decisions, bool levels, bool problems)
        {
            var e = Ev(T0, Enum.Parse<HistoryKind>(kindName), Enum.Parse<HistorySeverity>(sevName), "x");

            Assert.Equal(all, e.Matches(HistoryFilter.All));
            Assert.Equal(decisions, e.Matches(HistoryFilter.Decisions));
            Assert.Equal(levels, e.Matches(HistoryFilter.Levels));
            Assert.Equal(problems, e.Matches(HistoryFilter.Problems));
        }

        [Fact]
        public void ParseFilter_AcceptsTheFourNamesAndRefusesOthers()
        {
            Assert.Equal(HistoryFilter.All, AlwaysOnHistory.ParseFilter(null));
            Assert.Equal(HistoryFilter.All, AlwaysOnHistory.ParseFilter(""));
            Assert.Equal(HistoryFilter.All, AlwaysOnHistory.ParseFilter("all"));
            Assert.Equal(HistoryFilter.Decisions, AlwaysOnHistory.ParseFilter("Decisions"));
            Assert.Equal(HistoryFilter.Levels, AlwaysOnHistory.ParseFilter(" levels "));
            Assert.Equal(HistoryFilter.Problems, AlwaysOnHistory.ParseFilter("PROBLEMS"));

            var ex = Assert.Throws<UsageException>(() => AlwaysOnHistory.ParseFilter("errors"));
            Assert.Contains("all, decisions, levels or problems", ex.Message);
            Assert.Contains("'errors'", ex.Message);
        }

        [Fact]
        public void Events_SinceAndFilter_ReturnOnlyWhatMatches()
        {
            var h = History();
            h.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "started"));
            h.Append(Ev(T0.AddMinutes(1), HistoryKind.Level, HistorySeverity.Info, "levels 1"));
            h.Append(Ev(T0.AddMinutes(2), HistoryKind.Decision, HistorySeverity.Info, "KEEP"));
            h.Append(Ev(T0.AddMinutes(3), HistoryKind.Level, HistorySeverity.Warning, "levels 2 flagged"));
            h.Append(Ev(T0.AddMinutes(4), HistoryKind.Clip, HistorySeverity.Info, "Clip saved"));

            Assert.Equal(new[] { "Clip saved", "levels 2 flagged", "KEEP" },
                h.Events(T0.AddMinutes(2), HistoryFilter.All).Select(e => e.Text));            // since is inclusive
            Assert.Equal(new[] { "Clip saved", "KEEP" }, h.Events(null, HistoryFilter.Decisions).Select(e => e.Text));
            Assert.Equal(new[] { "levels 2 flagged", "levels 1" }, h.Events(null, HistoryFilter.Levels).Select(e => e.Text));
            Assert.Equal(new[] { "levels 2 flagged" }, h.Events(null, HistoryFilter.Problems).Select(e => e.Text));
            Assert.Equal(new[] { "Clip saved" }, h.Events(T0.AddMinutes(4), HistoryFilter.Decisions).Select(e => e.Text));
            Assert.Empty(h.Events(T0.AddMinutes(5), HistoryFilter.All));
        }

        [Fact]
        public void Append_RaisesAppendedWithTheEvent()
        {
            var h = History();
            var seen = new List<AlwaysOnEvent>();
            h.Appended += seen.Add;

            h.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "started"));

            Assert.Single(seen);
            Assert.Equal("started", seen[0].Text);
        }

        [Fact]
        public void Append_FileCannotBeWritten_KeepsTheEventInMemoryAndLogsIt()
        {
            // A directory where the file should be: every write to it is refused.
            string path = Path.Combine(_root, "history.jsonl");
            Directory.CreateDirectory(path);
            var h = new AlwaysOnHistory(path, () => _now);

            h.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "started"));

            Assert.Equal(1, h.Count);
            Assert.Equal("started", h.Events(null, HistoryFilter.All)[0].Text);
            Assert.Contains("[AlwaysOnHistory] Append: the event could not be written", File.ReadAllText(Log.CurrentFile));
        }

        [Fact]
        public void Append_EmptyText_IsRefused()
        {
            var h = History();
            Assert.Throws<ArgumentException>(() => h.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "")));
        }

        // ---- the silent-microphone rule (pure) -----------------------------------------------------

        [Fact]
        public void Evaluate_WindowsMuted_IsSilentAtOnce_WhateverTheLevelsSay()
        {
            // Loud a second ago, listening for an hour - and still silent, because Windows says muted.
            Assert.Equal(SilentMicReason.Muted, SilentMicRule.Evaluate(true, T0.AddSeconds(-1), T0.AddHours(-1), T0));
            Assert.Equal(SilentMicReason.Muted, SilentMicRule.Evaluate(true, null, T0, T0));
        }

        [Fact]
        public void Evaluate_LoudSecondsPresent_IsNotSilent()
        {
            Assert.Null(SilentMicRule.Evaluate(false, T0.AddSeconds(-1), T0.AddHours(-1), T0));
            Assert.Null(SilentMicRule.Evaluate(false, T0.AddMinutes(-9).AddSeconds(-59), T0.AddHours(-1), T0));
        }

        [Fact]
        public void Evaluate_NoLoudSecondForTenMinutes_IsSilent()
        {
            Assert.Equal(TimeSpan.FromMinutes(10), SilentMicRule.NoSoundAfter);
            Assert.Equal(SilentMicReason.NoSound, SilentMicRule.Evaluate(false, T0.AddMinutes(-10), T0.AddHours(-1), T0));
            Assert.Equal(SilentMicReason.NoSound, SilentMicRule.Evaluate(false, T0.AddMinutes(-30), T0.AddHours(-1), T0));
        }

        [Fact]
        public void Evaluate_NoLoudSecondEver_CountsFromWhenListeningBegan()
        {
            Assert.Null(SilentMicRule.Evaluate(false, null, T0.AddMinutes(-9), T0));
            Assert.Equal(SilentMicReason.NoSound, SilentMicRule.Evaluate(false, null, T0.AddMinutes(-10), T0));
            // A loud second from BEFORE this capture began listening does not count for it.
            Assert.Equal(SilentMicReason.NoSound, SilentMicRule.Evaluate(false, T0.AddHours(-2), T0.AddMinutes(-10), T0));
        }

        [Fact]
        public void Evaluate_MuteStateUnknown_JudgesOnSoundAlone()
        {
            Assert.Null(SilentMicRule.Evaluate(null, T0.AddSeconds(-5), T0.AddHours(-1), T0));
            Assert.Equal(SilentMicReason.NoSound, SilentMicRule.Evaluate(null, null, T0.AddMinutes(-10), T0));
        }

        [Fact]
        public void Describe_BothReasons_StartWithTheIssuesSentence()
        {
            Assert.Equal("The microphone is sending silence - check it is not muted. Windows reports it muted.",
                SilentMicRule.Describe(SilentMicReason.Muted));
            Assert.Equal("The microphone is sending silence - check it is not muted. No sound above the line for 10 min.",
                SilentMicRule.Describe(SilentMicReason.NoSound));
        }

        // ---- the engine records ---------------------------------------------------------------------

        [Fact]
        public void Start_RecordsTheDeviceFactsAndTheStartWithItsReason()
        {
            var h = History();
            using var engine = Engine(h);

            engine.Start(Options(), why: "Always On page");

            var events = Oldest(h);
            Assert.Equal(2, events.Count);
            Assert.Equal(HistoryKind.Device, events[0].Kind);
            Assert.Equal(HistorySeverity.Info, events[0].Severity);
            Assert.Equal("Devices: microphone \"Headset Microphone (USB Audio)\" - Windows: not muted, volume 80%; "
                         + "system sound not recorded; microphone counts. Setup \"test\"", events[0].Text);
            Assert.Equal(HistoryKind.State, events[1].Kind);
            Assert.Equal("Always-on started (Always On page)", events[1].Text);
            Assert.Equal(T0, events[1].AtUtc);

            var s = engine.Status();
            Assert.Equal("Headset Microphone (USB Audio)", s.MicDevice);
            Assert.False(s.MicMuted);
            Assert.Equal(80, s.MicVolumePercent);
            Assert.Null(s.SilentMic);
            Assert.Equal(1, _micReads);
        }

        [Fact]
        public void Start_MicMutedInWindows_RaisesTheWarningAndTheBannerImmediately_AndClearsWhenUnmuted()
        {
            _mic = _mic with { Muted = true };
            var h = History();
            using var engine = Engine(h);

            engine.Start(Options(), why: "control api");

            var events = Oldest(h);
            Assert.Equal(HistorySeverity.Warning, events[0].Severity);                    // the device facts say MUTED
            Assert.Contains("Windows: MUTED, volume 80%", events[0].Text);
            var warning = Assert.Single(events, e => e.Kind == HistoryKind.Problem);
            Assert.Equal(HistorySeverity.Warning, warning.Severity);
            Assert.Equal(SilentMicRule.Describe(SilentMicReason.Muted), warning.Text);
            Assert.StartsWith(SilentMicRule.BannerText, warning.Text);
            Assert.Equal(warning.Text, engine.Status().SilentMic);                           // the banner, at once
            Assert.True(engine.Status().MicMuted);

            // Unmuted in Windows: the next minute line re-reads the endpoint and the banner goes.
            engine.Tick();
            _mic = _mic with { Muted = false };
            _now = T0.AddSeconds(60);
            engine.Tick();

            Assert.Null(engine.Status().SilentMic);
            Assert.False(engine.Status().MicMuted);
            var cleared = Assert.Single(Oldest(h), e => e.Text == SilentMicRule.ClearedText);
            Assert.Equal(HistorySeverity.Info, cleared.Severity);
            Assert.Equal(2, _micReads);
        }

        /// <summary>
        /// THE TESTER'S TEST. The owner's microphone floor reads -96.7 dBFS all day because the setup's
        /// noise gate zeroes the silence between words - while loud seconds arrive whenever the owner
        /// talks. Twelve minutes of exactly that: the floor is -96.7 on every level line, and neither the
        /// banner nor a single warning appears.
        /// </summary>
        [Fact]
        public void Tick_GatedFloorOfMinus96WithLoudSecondsPresent_DoesNotRaiseTheBanner()
        {
            var o = Options(threshold: null);                                // Auto line, as the owner runs it
            var h = History();
            using var engine = Engine(h);
            engine.Start(o, why: "test");
            engine.Tick();
            var sound = _recorders[0].Sound!;

            for (int m = 0; m < 12; m++)
            {
                WriteFakePiece(o.PieceFolder, T0.AddMinutes(m));
                for (int s = 0; s < 60; s++)
                {
                    var at = T0.AddMinutes(m).AddSeconds(s);
                    // Four seconds of talking each minute; the gate's output the rest of the time.
                    double db = s is >= 10 and < 14 ? -20 : -96.7;
                    sound.Observe(SoundSource.Mic, at, AudioLevel.FromDb(db, db + 8, 48000));
                    if ((s + 1) % 15 == 0)
                    {
                        _now = at.AddSeconds(1);
                        engine.Tick();
                        Assert.Null(engine.Status().SilentMic);
                    }
                }
            }

            var levels = Oldest(h, HistoryFilter.Levels);
            Assert.Equal(12, levels.Count);
            Assert.All(levels, e =>
            {
                Assert.Equal(HistorySeverity.Info, e.Severity);
                Assert.Contains("mic floor=-96.7dBFS line=-50.0dBFS (auto)", e.Text);   // the floor is still REPORTED
                Assert.Contains("loud=4", e.Text);
                Assert.DoesNotContain("WARNING", e.Text);
            });
            Assert.Empty(h.Events(null, HistoryFilter.Problems));
            Assert.DoesNotContain(Oldest(h), e => e.Text.Contains(SilentMicRule.BannerText));
            Assert.Equal(T0.AddMinutes(11).AddSeconds(13), sound.LastLoudUtc(SoundSource.Mic));
        }

        [Fact]
        public void Tick_NoLoudSecondForTenMinutes_RaisesTheBannerAndClearsWhenSoundReturns()
        {
            var o = Options(threshold: null);
            var h = History();
            using var engine = Engine(h);
            engine.Start(o, why: "test");
            engine.Tick();
            var sound = _recorders[0].Sound!;

            for (int m = 0; m < 10; m++)
            {
                WriteFakePiece(o.PieceFolder, T0.AddMinutes(m));
                Level(sound, T0.AddMinutes(m), 60, -96.7);
                for (int q = 1; q <= 4; q++)
                {
                    _now = T0.AddMinutes(m).AddSeconds(15 * q);
                    engine.Tick();
                    if (_now < T0.AddMinutes(10)) Assert.Null(engine.Status().SilentMic);
                }
            }

            // Ten minutes without one loud second: the banner, the warning, and the level line flagged.
            string expected = SilentMicRule.Describe(SilentMicReason.NoSound);
            Assert.Equal(expected, engine.Status().SilentMic);
            var warning = Assert.Single(Oldest(h), e => e.Kind == HistoryKind.Problem && e.Severity == HistorySeverity.Warning);
            Assert.Equal(expected, warning.Text);
            Assert.Equal(T0.AddMinutes(10), warning.AtUtc);
            var levels = Oldest(h, HistoryFilter.Levels);
            Assert.Equal(10, levels.Count);
            Assert.All(levels.Take(9), e => Assert.Equal(HistorySeverity.Info, e.Severity));
            Assert.Equal(HistorySeverity.Warning, levels[9].Severity);
            Assert.EndsWith(SilentMicRule.LevelFlag, levels[9].Text);
            Assert.Single(h.Events(null, HistoryFilter.Problems), e => e.Kind == HistoryKind.Level);

            // The owner speaks: cleared within a tick.
            Speak(sound, T0.AddMinutes(10).AddSeconds(1));
            _now = T0.AddMinutes(10).AddSeconds(15);
            engine.Tick();

            Assert.Null(engine.Status().SilentMic);
            var cleared = Assert.Single(Oldest(h), e => e.Text == SilentMicRule.ClearedText);
            Assert.Equal(HistorySeverity.Info, cleared.Severity);
            Assert.Equal(_now, cleared.AtUtc);
        }

        [Fact]
        public void Tick_EveryMinute_RecordsALevelLineWithPeakAndAverage()
        {
            var h = History();
            using var engine = Engine(h);
            engine.Start(Options(threshold: null), why: "test");
            engine.Tick();                                                   // the minute is measured from here
            var sound = _recorders[0].Sound!;
            for (int s = 0; s < 60; s++)
            {
                double db = s is >= 40 and < 44 ? -20 : -70;
                sound.Observe(SoundSource.Mic, T0.AddSeconds(s), AudioLevel.FromDb(db, db + 8, 48000));
            }
            sound.Observe(SoundSource.Mic, T0.AddSeconds(60), AudioLevel.FromDb(-70, -62, 48000));   // closes second 59

            _now = T0.AddSeconds(30);
            engine.Tick();
            Assert.Empty(h.Events(null, HistoryFilter.Levels));              // not a minute yet
            _now = T0.AddSeconds(60);
            engine.Tick();

            var line = Assert.Single(h.Events(null, HistoryFilter.Levels));
            // Source, floor, line (auto), loud seconds, sustained yes/no - then the minute's peak and its
            // power-average RMS: 56 s at -70 and 4 s at -20 average to -31.8 dBFS.
            Assert.Equal("Levels: mic floor=-70.0dBFS line=-50.0dBFS (auto); last 60s: loud=4 sustained=yes (4s); "
                         + "mic peak=-20.0dBFS avg=-31.8dBFS", line.Text);
            Assert.Equal(HistorySeverity.Info, line.Severity);
            Assert.Equal(T0.AddSeconds(60), line.AtUtc);
            Assert.Contains("[AlwaysOnEngine] levels: mic floor=-70.0dBFS line=-50.0dBFS (auto); last 60s: loud=4 sustained=yes (4s); "
                            + "mic peak=-20.0dBFS avg=-31.8dBFS", File.ReadAllText(Log.CurrentFile));
        }

        [Fact]
        public void Engine_KeepDeleteClipAndStop_AreRecorded()
        {
            var o = Options();
            var h = History();
            using var engine = Engine(h);
            engine.Start(o, why: "test");

            for (int m = 0; m <= 10; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                if (m == 5) Speak(_recorders[0].Sound!, T0.AddMinutes(4.5));
                engine.Tick();
            }
            engine.Stop("test");

            var clip = Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            var decisions = Oldest(h, HistoryFilter.Decisions);
            Assert.All(decisions, e => Assert.True(e.Kind is HistoryKind.Decision or HistoryKind.Clip));
            var keeps = decisions.Where(e => e.Text.StartsWith("KEEP piece_")).ToList();
            var deletes = decisions.Where(e => e.Text.StartsWith("DELETE piece_")).ToList();
            Assert.Equal(5, keeps.Count);                                    // the pieces from 2:00 to 6:00
            Assert.All(keeps, e => Assert.Contains(" -> clip_", e.Text));    // with the clip each joined
            Assert.Equal(6, deletes.Count);                                  // 0:00, 1:00, 7:00 .. 10:00
            Assert.All(deletes, e => Assert.Contains("- no sound within 2 min before it or 2 min after it", e.Text));
            var saved = Assert.Single(decisions, e => e.Kind == HistoryKind.Clip);
            Assert.Equal($"Clip saved: {clip} - 8 s, 0 MB, 4 piece(s)", saved.Text);
            var all = Oldest(h);
            Assert.Equal("Always-on stopped (test)", all[^1].Text);
            Assert.Equal(HistoryKind.State, all[^1].Kind);
            Assert.True(all.IndexOf(saved) < all.Count - 1);                 // the clip was written before the stop
        }

        [Fact]
        public void Tick_CaptureFails_RecordsTheErrorWithFfmpegsTail_TheRestart_AndTheRecovery()
        {
            var o = Options();
            var h = History();
            using var engine = Engine(h);
            engine.Start(o, why: "test");
            _recorders[0].Exited = true;
            _recorders[0].StderrTail = "line 1\n[in#0/gdigrab @ 0] Failed to capture image (error 5)";
            _recorders[0].ProcessState = "still running (pid 1) - an input died";

            _now = T0.AddSeconds(30);
            engine.Tick();

            var problems = Oldest(h).Where(e => e.Kind == HistoryKind.Problem).ToList();
            Assert.Equal(2, problems.Count);
            Assert.Equal(HistorySeverity.Error, problems[0].Severity);
            Assert.Equal("Capture failed: the capture stopped unexpectedly; ffmpeg still running (pid 1) - an input died. "
                         + "ffmpeg said: [in#0/gdigrab @ 0] Failed to capture image (error 5)", problems[0].Text);
            Assert.Equal(_recorders[0].StderrTail, problems[0].Detail);      // the whole tail rides along
            Assert.Equal(HistorySeverity.Info, problems[1].Severity);
            Assert.Equal("Capture restarted (attempt 1); it counts as recovered once it opens a piece", problems[1].Text);
            Assert.Equal(2, _recorders.Count);

            // The new capture opens a piece: recovered, and the history says since when.
            _now = T0.AddSeconds(60);
            WritePiece(o.PieceFolder, _now);
            _now = T0.AddSeconds(61);
            engine.Tick();

            var recovered = Assert.Single(Oldest(h), e => e.Text.StartsWith("Recording again since "));
            Assert.Equal(HistorySeverity.Info, recovered.Severity);
            Assert.Contains($"since {T0.AddSeconds(60).ToLocalTime():HH:mm:ss} (the capture failed at {T0.AddSeconds(30).ToLocalTime():HH:mm:ss})", recovered.Text);
        }

        [Fact]
        public void Pause_AndResume_AreRecorded_AndThePauseClearsTheBanner()
        {
            _mic = _mic with { Muted = true };
            var h = History();
            using var engine = Engine(h);
            engine.Start(Options(), why: "test");
            Assert.NotNull(engine.Status().SilentMic);

            engine.Pause("a normal recording is running");
            Assert.Null(engine.Status().SilentMic);                          // not judged while paused
            _now = T0.AddMinutes(1);
            engine.Resume();

            var states = Oldest(h).Where(e => e.Kind == HistoryKind.State).Select(e => e.Text).ToList();
            Assert.Equal(new[] { "Always-on started (test)", "Always-on paused: a normal recording is running", "Always-on resumed" }, states);
            Assert.Equal(SilentMicRule.Describe(SilentMicReason.Muted), engine.Status().SilentMic);   // judged again on resume
        }

        [Fact]
        public void Start_CaptureFails_RecordsTheErrorAndRethrows()
        {
            _make = () => new FakeRecorder { FailStart = "the monitor is gone" };
            var h = History();
            using var engine = Engine(h);

            var ex = Assert.Throws<UsageException>(() => engine.Start(Options(), why: "Always On page"));

            Assert.Equal("the monitor is gone", ex.Message);
            var error = Assert.Single(Oldest(h), e => e.Severity == HistorySeverity.Error);
            Assert.Equal(HistoryKind.Problem, error.Kind);
            Assert.Equal("Always-on could not start (Always On page): the monitor is gone", error.Text);
            Assert.DoesNotContain(Oldest(h), e => e.Text.StartsWith("Always-on started"));
            Assert.Equal(AlwaysOnState.Off, engine.State);
        }

        [Fact]
        public void Start_MicEndpointCannotBeRead_RecordsAWarningOnce_AndJudgesOnSoundAlone()
        {
            _micError = new UsageException("no default microphone is set in Windows");
            var h = History();
            using var engine = Engine(h);

            engine.Start(Options(), why: "test");
            engine.Tick();
            _now = T0.AddSeconds(60);
            engine.Tick();                                                   // the minute line re-reads: same error, not repeated

            var events = Oldest(h);
            var device = Assert.Single(events, e => e.Kind == HistoryKind.Device);
            Assert.Contains("Windows state unknown: no default microphone is set in Windows", device.Text);
            var warning = Assert.Single(events, e => e.Text.Contains("could not be read"));
            Assert.Equal(HistorySeverity.Warning, warning.Severity);
            Assert.Contains("judges on sound alone", warning.Text);
            Assert.Null(engine.Status().MicMuted);
            Assert.Null(engine.Status().SilentMic);                          // one minute of quiet is not ten
            Assert.Equal(2, _micReads);
        }

        [Fact]
        public void Start_OnlySystemSoundCounts_NeverAsksWindowsAboutTheMicrophone_AndNeverRaisesTheBanner()
        {
            _mic = _mic with { Muted = true };
            var h = History();
            using var engine = Engine(h);

            var o = Options(counts: SoundSource.System, dshowMic: null);
            engine.Start(o, why: "test");
            WriteFakePiece(o.PieceFolder, T0);
            engine.Tick();
            for (int m = 1; m <= 11; m++)
            {
                _now = T0.AddMinutes(m);
                WriteFakePiece(o.PieceFolder, _now);
                engine.Tick();
            }

            Assert.Equal(0, _micReads);
            var device = Assert.Single(Oldest(h), e => e.Kind == HistoryKind.Device);
            Assert.Equal("Devices: microphone not recorded and not listened to; system sound not recorded; system sound counts. Setup \"test\"", device.Text);
            Assert.Null(engine.Status().SilentMic);
            Assert.Null(engine.Status().MicDevice);
            Assert.Empty(h.Events(null, HistoryFilter.Problems));
        }

        /// <summary>
        /// Review finding: a capture restart used to reset the ten-minute clock and a capture that was
        /// down used to CLEAR the banner with a false "sending sound again". Now the silence is one
        /// silence across the restart: the banner stays up through the failure, the failed replacement
        /// and the recovery, and clears exactly once - when the owner speaks.
        /// </summary>
        [Fact]
        public void Tick_CaptureRestartsWhileTheMicIsSilent_TheBannerStaysAndNothingIsFalselyCleared()
        {
            var o = Options(threshold: null);
            var h = History();
            using var engine = Engine(h);
            engine.Start(o, why: "test");
            engine.Tick();
            var sound = _recorders[0].Sound!;
            for (int m = 0; m < 10; m++)
            {
                WriteFakePiece(o.PieceFolder, T0.AddMinutes(m));
                Level(sound, T0.AddMinutes(m), 60, -96.7);
                _now = T0.AddMinutes(m + 1);
                engine.Tick();
            }
            string banner = SilentMicRule.Describe(SilentMicReason.NoSound);
            Assert.Equal(banner, engine.Status().SilentMic);

            // The capture dies and its replacement fails to start: always-on is retrying, nothing is heard.
            _recorders[0].Exited = true;
            _make = () => new FakeRecorder { FailStart = "the monitor is gone" };
            _now = T0.AddMinutes(10).AddSeconds(15);
            engine.Tick();
            Assert.Equal(AlwaysOnState.Retrying, engine.State);
            Assert.Equal(banner, engine.Status().SilentMic);                 // still up: nothing said otherwise

            // The restart succeeds; the new capture hears the same silence.
            _make = () => new FakeRecorder();
            _now = T0.AddMinutes(10).AddSeconds(30);
            engine.Tick();
            Assert.Equal(AlwaysOnState.Listening, engine.State);
            WriteFakePiece(o.PieceFolder, _now);
            _now = T0.AddMinutes(11);
            engine.Tick();
            Assert.Equal(banner, engine.Status().SilentMic);                 // the restart did not reset the clock

            // The owner speaks: cleared once.
            Speak(_recorders[^1].Sound!, T0.AddMinutes(11).AddSeconds(1));
            _now = T0.AddMinutes(11).AddSeconds(15);
            engine.Tick();

            Assert.Null(engine.Status().SilentMic);
            var all = Oldest(h);
            Assert.Single(all, e => e.Text == banner);
            Assert.Single(all, e => e.Text == SilentMicRule.ClearedText);
            Assert.Equal(SilentMicRule.ClearedText, all[^1].Text);          // and it is the newest event
        }

        [Fact]
        public void Resume_ReReadsWindowsMuteState_SoAnUnmuteDuringThePauseRaisesNoFalseWarning()
        {
            _mic = _mic with { Muted = true };
            var h = History();
            using var engine = Engine(h);
            engine.Start(Options(), why: "test");
            Assert.Equal(SilentMicRule.Describe(SilentMicReason.Muted), engine.Status().SilentMic);

            engine.Pause("a normal recording is running");
            _mic = _mic with { Muted = false };                              // unmuted during the pause
            _now = T0.AddMinutes(30);
            engine.Resume();

            Assert.Null(engine.Status().SilentMic);
            Assert.False(engine.Status().MicMuted);
            Assert.Equal(2, _micReads);                                      // start, and again on resume
            var all = Oldest(h);
            Assert.Single(all, e => e.Kind == HistoryKind.Problem && e.Severity == HistorySeverity.Warning);   // the one at start
            Assert.DoesNotContain(all, e => e.Text == SilentMicRule.ClearedText);                              // nothing to clear
            Assert.Equal("Always-on resumed", all[^1].Text);
        }

        /// <summary>
        /// Review finding: a history file that could not be read was cached as EMPTY, and the next
        /// day-change rewrite would have written that emptiness over seven days of events on disk.
        /// Now a failed read caches nothing: that call answers empty, nothing is rewritten, and the next
        /// call reads the file.
        /// </summary>
        [Fact]
        public void Load_FileLocked_AnswersEmptyForThatCallOnly_NeverRewritesOverIt_AndReadsItNextTime()
        {
            string path = Path.Combine(_root, "history.jsonl");
            File.WriteAllLines(path, new[]
            {
                AlwaysOnHistory.Serialize(Ev(_now.AddMinutes(-2), HistoryKind.State, HistorySeverity.Info, "first")),
                AlwaysOnHistory.Serialize(Ev(_now.AddMinutes(-1), HistoryKind.Level, HistorySeverity.Info, "second")),
            });
            var h = History();

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Empty(h.Events(null, HistoryFilter.All));             // this call: nothing readable
                Assert.Equal(0, h.Count);
                // An append while locked cannot rewrite from the (unloaded) memory - and cannot write either.
                _now = _now.AddDays(1);
                h.Append(Ev(_now, HistoryKind.State, HistorySeverity.Info, "while locked"));
            }
            Assert.Equal(2, File.ReadAllLines(path).Length);                 // the file is intact
            string log = File.ReadAllText(Log.CurrentFile);
            Assert.Contains("[AlwaysOnHistory] Load: " + path + " could not be read; nothing is cached", log);
            Assert.Contains("[AlwaysOnHistory] Append: the event could not be written", log);

            // Unlocked: read afresh, and an append lands.
            Assert.Equal(new[] { "first", "second" }, Oldest(h).Select(e => e.Text));
            h.Append(Ev(_now, HistoryKind.State, HistorySeverity.Info, "after"));
            Assert.Equal(new[] { "first", "second", "after" }, Oldest(h).Select(e => e.Text));
            Assert.Equal(3, File.ReadAllLines(path).Length);
        }

        // ---- GET /always-on/history over real HTTP ------------------------------------------------------

        [Fact]
        public async System.Threading.Tasks.Task GetAlwaysOnHistory_ReturnsTheEventsAsJsonNewestFirst_FilteredByKindAndSince()
        {
            var h = History();
            h.Append(Ev(T0, HistoryKind.State, HistorySeverity.Info, "Always-on started (test)"));
            h.Append(Ev(T0.AddMinutes(1), HistoryKind.Level, HistorySeverity.Info, "Levels: mic floor=-70.0dBFS"));
            h.Append(Ev(T0.AddMinutes(2), HistoryKind.Decision, HistorySeverity.Info, "KEEP piece_a (60s) -> clip_1"));
            h.Append(Ev(T0.AddMinutes(3), HistoryKind.Problem, HistorySeverity.Error, "Capture failed", detail: "tail"));
            var engine = new AlwaysOnEngine(() => throw new InvalidOperationException("no recorder in this test"), () => _now, ownTimer: false, h);
            var svc = new RecordingService();
            using var ao = new AlwaysOnController(svc, new Config(), engine);
            int port = FreePort();
            using var server = new RestServer(svc, port) { AlwaysOn = ao };
            server.Start();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            string all = await http.GetStringAsync($"http://127.0.0.1:{port}/always-on/history");
            using (var doc = JsonDocument.Parse(all))
            {
                var root = doc.RootElement;
                Assert.Equal("all", root.GetProperty("kind").GetString());
                Assert.Equal(4, root.GetProperty("count").GetInt32());
                Assert.Equal(7, root.GetProperty("retentionDays").GetInt32());
                Assert.Equal(h.Path, root.GetProperty("file").GetString());
                var events = root.GetProperty("events").EnumerateArray().ToList();
                Assert.Equal(4, events.Count);
                Assert.Equal("Capture failed", events[0].GetProperty("text").GetString());          // newest first
                Assert.Equal("problem", events[0].GetProperty("kind").GetString());
                Assert.Equal("error", events[0].GetProperty("severity").GetString());
                Assert.Equal("tail", events[0].GetProperty("detail").GetString());
                Assert.Equal(T0.AddMinutes(3), events[0].GetProperty("atUtc").GetDateTime().ToUniversalTime());
                Assert.Equal(T0.AddMinutes(3).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), events[0].GetProperty("atLocal").GetString());
                Assert.Equal("Always-on started (test)", events[3].GetProperty("text").GetString());
                Assert.Equal(JsonValueKind.Null, events[3].GetProperty("detail").ValueKind);
            }

            string problems = await http.GetStringAsync($"http://127.0.0.1:{port}/always-on/history?kind=problems");
            using (var doc = JsonDocument.Parse(problems))
            {
                Assert.Equal("problems", doc.RootElement.GetProperty("kind").GetString());
                var texts = doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToList();
                Assert.Equal(new[] { "Capture failed" }, texts);
            }

            string since = await http.GetStringAsync($"http://127.0.0.1:{port}/always-on/history?since={Uri.EscapeDataString(T0.AddMinutes(1).ToString("o"))}&kind=all");
            using (var doc = JsonDocument.Parse(since))
            {
                var texts = doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToList();
                Assert.Equal(new[] { "Capture failed", "KEEP piece_a (60s) -> clip_1", "Levels: mic floor=-70.0dBFS" }, texts);
                Assert.Equal(T0.AddMinutes(1), doc.RootElement.GetProperty("since").GetDateTime().ToUniversalTime());
            }

            string decisions = await http.GetStringAsync($"http://127.0.0.1:{port}/always-on/history?kind=decisions&since={Uri.EscapeDataString(T0.AddMinutes(3).ToString("o"))}");
            using (var doc = JsonDocument.Parse(decisions))
            {
                Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
                Assert.Empty(doc.RootElement.GetProperty("events").EnumerateArray());
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task GetAlwaysOnHistory_BadKindOrSince_Is400WithTheReason()
        {
            var engine = new AlwaysOnEngine(() => throw new InvalidOperationException("no recorder in this test"), () => _now, ownTimer: false, History());
            var svc = new RecordingService();
            using var ao = new AlwaysOnController(svc, new Config(), engine);
            int port = FreePort();
            using var server = new RestServer(svc, port) { AlwaysOn = ao };
            server.Start();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var badKind = await http.GetAsync($"http://127.0.0.1:{port}/always-on/history?kind=errors");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badKind.StatusCode);
            using (var doc = JsonDocument.Parse(await badKind.Content.ReadAsStringAsync()))
            {
                Assert.Equal("bad_request", doc.RootElement.GetProperty("code").GetString());
                Assert.Contains("kind must be all, decisions, levels or problems", doc.RootElement.GetProperty("error").GetString());
            }

            var badSince = await http.GetAsync($"http://127.0.0.1:{port}/always-on/history?since=yesterday");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badSince.StatusCode);
            using (var doc = JsonDocument.Parse(await badSince.Content.ReadAsStringAsync()))
            {
                Assert.Contains("since must be an ISO 8601 date/time", doc.RootElement.GetProperty("error").GetString());
            }

            string discovery = await http.GetStringAsync($"http://127.0.0.1:{port}/");
            Assert.Contains("GET /always-on/history {since?, kind?:all|decisions|levels|problems}", discovery);
        }

        [Fact]
        public async System.Threading.Tasks.Task GetAlwaysOn_ReportsTheSilentMicBannerAndTheMicState()
        {
            _mic = _mic with { Muted = true };
            var engine = Engine(History());
            engine.Start(Options(), why: "test");
            var svc = new RecordingService();
            using var ao = new AlwaysOnController(svc, new Config(), engine);
            int port = FreePort();
            using var server = new RestServer(svc, port) { AlwaysOn = ao };
            server.Start();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            string body = await http.GetStringAsync($"http://127.0.0.1:{port}/always-on");

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status");
            Assert.Equal(SilentMicRule.Describe(SilentMicReason.Muted), status.GetProperty("SilentMic").GetString());
            Assert.True(status.GetProperty("MicMuted").GetBoolean());
            Assert.Equal(80, status.GetProperty("MicVolumePercent").GetDouble());
            Assert.Equal("Headset Microphone (USB Audio)", status.GetProperty("MicDevice").GetString());
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // ---- the History tab's pure helpers ------------------------------------------------------------

        [Fact]
        public void HistoryWhen_TodayShowsTheTime_OlderDaysShowTheDateToo()
        {
            var today = new DateTime(2026, 9, 24);
            Assert.Equal("09:03:17", MainWindow.HistoryWhen(new DateTime(2026, 9, 24, 9, 3, 17), today));
            Assert.Equal("2026-09-23 23:59:59", MainWindow.HistoryWhen(new DateTime(2026, 9, 23, 23, 59, 59), today));
        }

        [Fact]
        public void HistoryBadge_WarningsAndErrorsSayThePriority_InfoSaysTheKind()
        {
            Assert.Equal("WARNING", MainWindow.HistoryBadge(Ev(T0, HistoryKind.Level, HistorySeverity.Warning, "x")));
            Assert.Equal("ERROR", MainWindow.HistoryBadge(Ev(T0, HistoryKind.Problem, HistorySeverity.Error, "x")));
            Assert.Equal("decision", MainWindow.HistoryBadge(Ev(T0, HistoryKind.Decision, HistorySeverity.Info, "x")));
            Assert.Equal("clip", MainWindow.HistoryBadge(Ev(T0, HistoryKind.Clip, HistorySeverity.Info, "x")));
            Assert.Equal("state", MainWindow.HistoryBadge(Ev(T0, HistoryKind.State, HistorySeverity.Info, "x")));
        }

        [Fact]
        public void HistoryStatusText_SaysTheCountOrWhyThereIsNothing()
        {
            Assert.Equal("No events yet. Start always-on and what it does appears here, newest first.",
                MainWindow.HistoryStatusText(0, HistoryFilter.All));
            Assert.Equal("No problems in the last 7 days.", MainWindow.HistoryStatusText(0, HistoryFilter.Problems));
            Assert.Equal("1 event, newest first - today and the last 7 days.", MainWindow.HistoryStatusText(1, HistoryFilter.All));
            Assert.Equal("312 events, newest first - today and the last 7 days.", MainWindow.HistoryStatusText(312, HistoryFilter.Levels));
        }

        private sealed class FakeRecorder : IPieceRecorder
        {
            public SoundLog? Sound;
            public bool Stopped;
            public bool Exited;
            public string? FailStart;

            public void Start(AlwaysOnOptions options, SoundLog sound)
            {
                if (FailStart != null) throw new UsageException(FailStart);
                Sound = sound;
            }

            public void Stop() => Stopped = true;
            public bool HasExited => Exited;
            public string Encoder => "fake";
            public string StderrTail { get; set; } = "fake ffmpeg: device lost";
            public string ProcessState { get; set; } = "still running (pid 1)";
            public void Dispose() { }
        }
    }
}
