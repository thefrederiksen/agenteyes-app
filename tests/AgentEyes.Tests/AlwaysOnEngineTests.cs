using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes.AlwaysOn;
using AgentEyes.Audio;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The always-on ENGINE end to end (issue #66) with the screen taken out: a fake recorder writes
    /// real, short MP4 pieces with the names ffmpeg would give them, a fake clock moves the day on,
    /// and the real keeper, joiner and cap do the rest - so what survives on disk is what the product
    /// would leave.
    /// </summary>
    public class AlwaysOnEngineTests : IDisposable
    {
        private readonly string _root;
        private DateTime _now = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
        private readonly List<FakeRecorder> _recorders = new();
        private Func<FakeRecorder> _make;

        public AlwaysOnEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-alwayson-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _make = () => new FakeRecorder();
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private AlwaysOnOptions Options(long capBytes = 5L * 1024 * 1024 * 1024, double? threshold = -40) => new()
        {
            SetupName = "test",
            Counts = SoundSource.Mic,
            ThresholdDb = threshold,
            // Two-minute margins either side of the speech (issue #79: seconds; 2 min is the most keep-before
            // allows) and the 5 min silence gap that closes a clip.
            KeepBefore = TimeSpan.FromMinutes(2),
            KeepAfter = TimeSpan.FromMinutes(2),
            SilenceGap = TimeSpan.FromMinutes(5),
            CapBytes = capBytes,
            ClipsFolder = Path.Combine(_root, "clips"),
            WorkFolder = Path.Combine(_root, "work"),
            PieceSeconds = 60,
        };

        private AlwaysOnEngine Engine() => new(() =>
        {
            var r = _make();
            _recorders.Add(r);
            return r;
        }, () => _now, ownTimer: false);

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

        /// <summary>
        /// A real SIX-second MP4 with a keyframe every 2 s - the grid the always-on capture forces (issue
        /// #79) - named as ffmpeg names a piece that opened at <paramref name="startUtc"/>. Encoded once per
        /// test run and copied.
        /// </summary>
        private static void WriteKeyframedPiece(string folder, DateTime startUtc)
        {
            Directory.CreateDirectory(folder);
            string name = "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4";
            File.Copy(KeyframedTemplate.Value, Path.Combine(folder, name), overwrite: true);
        }

        private static readonly Lazy<string> KeyframedTemplate = new(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "agenteyes-alwayson-keyframed-" + Guid.NewGuid().ToString("N") + ".mp4");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "color=c=gray:s=160x90:r=10:d=6",
                "-f", "lavfi", "-i", "sine=f=300:d=6",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-force_key_frames", $"expr:gte(t,n_forced*{AlwaysOnArgs.DefaultKeyframeSeconds})",
                "-c:a", "aac", "-shortest",
                path,
            }, "test keyframed piece template");
            return path;
        });

        /// <summary>
        /// Four seconds of talking at -20 dBFS RMS starting at <paramref name="atUtc"/>, then one buffer
        /// of the next second so the last spoken second is closed and judged. Since issue #72 a single
        /// loud buffer is not sound: it takes 3 loud seconds within 10.
        /// </summary>
        private static void Speak(SoundLog log, DateTime atUtc)
        {
            for (int s = 0; s < 4; s++)
                log.Observe(SoundSource.Mic, atUtc.AddSeconds(s), AudioLevel.FromDb(-20, -10, 48000));
            log.Observe(SoundSource.Mic, atUtc.AddSeconds(4), AudioLevel.FromDb(-80, -70, 48000));
        }

        [Fact]
        public void Engine_SoundInTheMiddle_KeepsOneClipWithItsMarginsAndDeletesTheRest()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);

            for (int m = 0; m <= 10; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);   // the piece being written now; earlier ones are finished
                if (m == 5) Speak(_recorders[0].Sound!, t0.AddMinutes(4.5));
                engine.Tick();
            }
            engine.Stop();

            // The clip's span is 2:30 (2 min before the 4:30 speech) to 6:34 (2 min after its last second,
            // 4:33). The keeper keeps the pieces from 2:00 to 6:00; every piece is a 2-second test file, so
            // the piece from 2:00 holds only 2:00-2:02 - ahead of the lead-in - and the trim leaves it out (a
            // real minute-long piece would be cut at 2:30 instead). The clip is the pieces from 3:00 to 6:00.
            var clips = Directory.GetFiles(o.ClipsFolder, "*.mp4");
            Assert.Single(clips);
            Assert.Equal(t0.AddMinutes(3).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clips[0]));
            Assert.Equal(4 * 2, MediaProbe.DurationSeconds(clips[0]), 0);
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Empty(Directory.GetDirectories(o.PendingFolder));

            var s = engine.Status();
            Assert.Equal(AlwaysOnState.Off, s.State);
            Assert.Equal(1, s.ClipsToday);
            Assert.True(s.DiscardedSecondsToday > 0);
            Assert.True(_recorders[0].Stopped);
        }

        [Fact]
        public void Engine_ClipIsWrittenOnceTheSilenceGapPasses_WhileStillRecording()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);

            for (int m = 0; m <= 8; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                if (m == 1) Speak(_recorders[0].Sound!, t0.AddSeconds(30));
                engine.Tick();
                if (m == 3) Assert.Equal(AlwaysOnState.Keeping, engine.State);
            }

            // Sound at 0:30 with a 5 min silence gap: the clip closed at 6:00, while always-on is still on.
            Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.Equal(AlwaysOnState.Listening, engine.State);
        }

        [Fact]
        public void Engine_ClipIsTrimmedToItsSpan_FirstPieceCutOnAKeyframeAndLastPieceCutAtTheTail()
        {
            // Issue #79 end to end on real files: 6 s pieces with a keyframe every 2 s (as the capture forces
            // them), 2 s before, 2 s after, a 30 s silence gap. Talking from 0:16 to 0:19 makes the span
            // 0:14 - 0:22: the piece from 0:12 is cut at its 2 s keyframe (0:14) and the piece from 0:18 is
            // cut to end at 0:22 - both stream copies. Three whole pieces would be 12 s; the clip is 8 s.
            var o = new AlwaysOnOptions
            {
                SetupName = "test",
                Counts = SoundSource.Mic,
                ThresholdDb = -40,
                KeepBefore = TimeSpan.FromSeconds(2),
                KeepAfter = TimeSpan.FromSeconds(2),
                SilenceGap = TimeSpan.FromSeconds(30),
                CapBytes = 5L * 1024 * 1024 * 1024,
                ClipsFolder = Path.Combine(_root, "clips"),
                WorkFolder = Path.Combine(_root, "work"),
                PieceSeconds = 6,
                KeyframeSeconds = 2,
            };
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int s = 0; s <= 36; s += 6)
            {
                _now = t0.AddSeconds(s);
                WriteKeyframedPiece(o.PieceFolder, _now);
                if (s == 24) Speak(_recorders[0].Sound!, t0.AddSeconds(16));
                engine.Tick();
            }
            _now = t0.AddSeconds(70);
            engine.Stop();

            var clip = Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            // Named for where the kept video starts: the keyframe at 0:14, not the piece's 0:12.
            Assert.Equal(t0.AddSeconds(14).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clip));
            // 4 s of the first piece + 4 s of the last. A cut that missed the keyframe (landing on 0:12)
            // would make this 10; no trim at all would make it 12.
            Assert.Equal(8, MediaProbe.DurationSeconds(clip), 0);
            Assert.Equal(8, engine.Status().KeptSecondsToday, 0);
            Assert.Equal(1, engine.Status().ClipsToday);
            Assert.Empty(Directory.GetDirectories(o.PendingFolder));
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            string log = TestRunIsolation.ReadLog();
            Assert.Contains($"trimmed piece_{t0.AddSeconds(12).ToString(AlwaysOnArgs.PieceStampFormat)}.mp4 (stream copy) - kept 2s to ", log);
            Assert.Contains($"trimmed piece_{t0.AddSeconds(18).ToString(AlwaysOnArgs.PieceStampFormat)}.mp4 (stream copy) - kept 0s to 4s of ", log);
        }

        [Fact]
        public void Start_KeepSettingsOutOfRange_ThrowsWithTheReasonAndLeavesAlwaysOnOff()
        {
            using var engine = Engine();
            var o = new AlwaysOnOptions
            {
                SetupName = "test", Counts = SoundSource.Mic, ThresholdDb = -40,
                KeepBefore = TimeSpan.FromSeconds(121), KeepAfter = TimeSpan.FromSeconds(10), SilenceGap = TimeSpan.FromMinutes(5),
                ClipsFolder = Path.Combine(_root, "clips"), WorkFolder = Path.Combine(_root, "work"),
            };

            var ex = Assert.Throws<UsageException>(() => engine.Start(o));

            Assert.Equal("Keep before the speech must be 0 s to 2 min, not 2 min 1 s.", ex.Message);
            Assert.Equal(AlwaysOnState.Off, engine.State);
            Assert.Empty(_recorders);                       // refused before any capture was started
        }

        [Fact]
        public void Start_PieceNotAWholeNumberOfKeyframes_ThrowsAndLeavesAlwaysOnOff()
        {
            using var engine = Engine();
            var o = new AlwaysOnOptions
            {
                SetupName = "test", Counts = SoundSource.Mic, ThresholdDb = -40,
                ClipsFolder = Path.Combine(_root, "clips"), WorkFolder = Path.Combine(_root, "work"),
                PieceSeconds = 61, KeyframeSeconds = 2,
            };

            var ex = Assert.Throws<UsageException>(() => engine.Start(o));

            Assert.Contains("whole number of keyframe intervals", ex.Message);
            Assert.Equal(AlwaysOnState.Off, engine.State);
            Assert.Empty(_recorders);
        }

        // ---- issue #70: the clip in progress, and where today's clips are -----

        [Fact]
        public void Status_NoClipInProgress_OpenClipIsNullAndNothingListed()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int m = 0; m <= 3; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                engine.Tick();
            }

            var s = engine.Status();
            Assert.Equal(AlwaysOnState.Listening, s.State);
            Assert.Null(s.OpenClipStartUtc);
            Assert.Null(s.OpenClipElapsedSeconds);
            Assert.Null(s.InProgressLine(_now));
            Assert.Empty(s.ClipsKeptToday);
        }

        [Fact]
        public void Status_WhileKeeping_ReportsTheOpenClipsStartAndElapsedSeconds()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int m = 0; m <= 3; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                if (m == 1) Speak(_recorders[0].Sound!, t0.AddSeconds(30));
                engine.Tick();
            }

            // Sound at 0:30, 2 min before-window: the clip began with the first piece, at t0.
            var s = engine.Status();
            Assert.Equal(AlwaysOnState.Keeping, s.State);
            Assert.Equal(t0, s.OpenClipStartUtc);
            Assert.Equal(180.0, s.OpenClipElapsedSeconds);
            Assert.Equal(240.0, s.OpenClipElapsedAt(t0.AddMinutes(4)));
            Assert.Equal($"Recording a clip now - 3 min so far. Saved to {o.ClipsFolder} after 5 min of quiet.",
                s.InProgressLine(_now));
            Assert.Empty(s.ClipsKeptToday);                 // nothing written yet: the clip is still pieces
        }

        [Fact]
        public void Status_SoundHeardBeforeAnyPieceIsDecided_ClipStartsAtTheOldestWaitingPiece()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            WritePiece(o.PieceFolder, t0);                  // the only piece, still being written
            Speak(_recorders[0].Sound!, t0.AddSeconds(10));
            _now = t0.AddSeconds(20);
            engine.Tick();

            var s = engine.Status();
            Assert.Equal(AlwaysOnState.Keeping, s.State);
            Assert.Equal(t0, s.OpenClipStartUtc);
            Assert.Equal(20.0, s.OpenClipElapsedSeconds);
            Assert.Equal($"Recording a clip now - under 1 min so far. Saved to {o.ClipsFolder} after 5 min of quiet.",
                s.InProgressLine(_now));
        }

        [Fact]
        public void Status_AfterTheClipCloses_OpenClipIsNullAndTheClipIsListedWithItsFullPath()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int m = 0; m <= 8; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                if (m == 1) Speak(_recorders[0].Sound!, t0.AddSeconds(30));
                engine.Tick();
            }

            var s = engine.Status();
            Assert.Equal(AlwaysOnState.Listening, s.State);
            Assert.Null(s.OpenClipStartUtc);
            Assert.Null(s.OpenClipElapsedSeconds);
            var written = Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            var clip = Assert.Single(s.ClipsKeptToday);
            Assert.Equal(written, clip.Path);
            Assert.Equal(Path.GetFileName(written), clip.File);
            Assert.Equal(o.ClipsFolder, clip.Folder);
            Assert.True(clip.Exists);
            Assert.Equal($"{Path.GetFileName(written)} in {o.ClipsFolder}", clip.Label);
            Assert.Equal(s.ClipsToday, s.ClipsKeptToday.Count);
        }

        [Fact]
        public void Status_TodaysClips_SurviveARestartAndSayWhenAClipIsGone()
        {
            var o = Options();
            string written;
            using (var engine = Engine())
            {
                var t0 = _now;
                engine.Start(o);
                for (int m = 0; m <= 3; m++)
                {
                    _now = t0.AddMinutes(m);
                    WritePiece(o.PieceFolder, _now);
                    if (m == 1) Speak(_recorders[0].Sound!, t0.AddSeconds(30));
                    engine.Tick();
                }
                engine.Stop();                              // the final pass writes the clip
                written = Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            }

            File.Delete(written);                           // the owner moved it, or the cap took it
            using var again = Engine();
            again.Start(o);
            var clip = Assert.Single(again.Status().ClipsKeptToday);
            Assert.Equal(written, clip.Path);
            Assert.False(clip.Exists);
            Assert.EndsWith("(no longer there)", clip.Label);
        }

        [Fact]
        public void Tick_WhileKeeping_RaisesChangedEveryPassSoTheRunningTimeStaysLive()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            WritePiece(o.PieceFolder, t0);
            Speak(_recorders[0].Sound!, t0.AddSeconds(5));
            _now = t0.AddSeconds(15);
            engine.Tick();
            Assert.Equal(AlwaysOnState.Keeping, engine.State);

            int changed = 0;
            engine.Changed += () => changed++;
            _now = t0.AddSeconds(30);
            engine.Tick();                                  // same state, same counters - still a change
            Assert.Equal(1, changed);
            Assert.Equal(30.0, engine.Status().OpenClipElapsedSeconds);
        }

        [Fact]
        public void Day_NewDate_ClearsTheClipList()
        {
            var day = new AlwaysOnDay();
            day.EnsureDay(_now);
            day.Clips = 1;
            day.ClipPaths.Add(@"C:\AgentEyes\2026-09-23_09-00-00.mp4");
            day.EnsureDay(_now.AddDays(1));
            Assert.Equal(0, day.Clips);
            Assert.Empty(day.ClipPaths);
        }

        [Fact]
        public void Day_SaveAndLoad_KeepsTheClipList_AndAFileFromBeforeTheListLoadsEmpty()
        {
            string path = Path.Combine(_root, "today.json");
            var day = new AlwaysOnDay { Date = "2026-09-23", Clips = 2 };
            day.ClipPaths.Add(@"C:\AgentEyes\a.mp4");
            day.ClipPaths.Add(@"D:\Old\b.mp4");
            day.Save(path);
            Assert.Equal(new[] { @"C:\AgentEyes\a.mp4", @"D:\Old\b.mp4" }, AlwaysOnDay.Load(path).ClipPaths);

            File.WriteAllText(path, "{\"Date\":\"2026-09-23\",\"Clips\":2,\"KeptSeconds\":240,\"KeptBytes\":13000000,\"DiscardedSeconds\":0}");
            var old = AlwaysOnDay.Load(path);
            Assert.Equal(2, old.Clips);
            Assert.Empty(old.ClipPaths);

            File.WriteAllText(path, "{\"Date\":\"2026-09-23\",\"Clips\":1,\"ClipPaths\":null}");
            Assert.Empty(AlwaysOnDay.Load(path).ClipPaths);
        }

        [Fact]
        public void Status_Auto_ReportsTheLineAndTheFloorItCameFrom()
        {
            var o = Options(threshold: null);
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            var s0 = engine.Status();
            Assert.Null(s0.FloorDb);       // nothing heard yet: no floor, no line
            Assert.Null(s0.ThresholdDb);

            // Thirty seconds of room noise at -65 dBFS RMS: floor -65, line max(-65 + 15, -50) = -50.
            for (int s = 0; s <= 30; s++)
                _recorders[0].Sound!.Observe(SoundSource.Mic, t0.AddSeconds(s), AudioLevel.FromDb(-65, -55, 48000));
            _now = t0.AddSeconds(31);
            engine.Tick();

            var st = engine.Status();
            Assert.True(st.ThresholdAuto);
            Assert.Equal(-65.0, st.FloorDb);
            Assert.Equal(SoundLog.AutoMinLineDb, st.ThresholdDb);

            // A louder room lifts the line above the minimum: floor -40 -> line -25.
            for (int s = 31; s <= 700; s++)
                _recorders[0].Sound!.Observe(SoundSource.Mic, t0.AddSeconds(s), AudioLevel.FromDb(-40, -30, 48000));
            _now = t0.AddSeconds(701);
            engine.Tick();
            st = engine.Status();
            Assert.Equal(-40.0, st.FloorDb);
            Assert.Equal(-40.0 + SoundLog.AutoMarginDb, st.ThresholdDb);
        }

        [Fact]
        public void Tick_LogsTheLevelsOncePerMinute()
        {
            var o = Options(threshold: null);
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            engine.Tick();                                  // the minute is measured from here
            var sound = _recorders[0].Sound!;
            for (int s = 0; s < 40; s++)
                sound.Observe(SoundSource.Mic, t0.AddSeconds(s), AudioLevel.FromDb(-70, -60, 48000));
            Speak(sound, t0.AddSeconds(40));                // 4 loud seconds, sustained
            for (int s = 45; s < 59; s++)
                sound.Observe(SoundSource.Mic, t0.AddSeconds(s), AudioLevel.FromDb(-70, -60, 48000));

            _now = t0.AddSeconds(30);
            engine.Tick();
            Assert.Null(engine.LastLevelsLine);             // not a minute yet

            _now = t0.AddSeconds(58);
            engine.Tick();
            Assert.Null(engine.LastLevelsLine);             // 58 s is not a minute

            // A timer tick a few milliseconds short of the minute still logs (seen live: a strict
            // compare skipped to the next tick and logged "last 75s").
            _now = t0.AddSeconds(60).AddMilliseconds(-10);
            engine.Tick();
            string? line = engine.LastLevelsLine;
            Assert.NotNull(line);
            Assert.Equal("mic floor=-70.0dBFS line=-50.0dBFS (auto); last 60s: loud=4 sustained=yes (4s)", line);
            Assert.Contains("[AlwaysOnEngine] levels: " + line, TestRunIsolation.ReadLog());

            _now = t0.AddSeconds(105);
            engine.Tick();
            Assert.Equal(line, engine.LastLevelsLine);      // 45 s later: still the same line

            _now = t0.AddSeconds(120).AddMilliseconds(-10);
            engine.Tick();
            Assert.Equal("mic floor=-70.0dBFS line=-50.0dBFS (auto); last 60s: loud=0 sustained=no (0s)", engine.LastLevelsLine);
        }

        [Fact]
        public void Engine_SilentRun_LeavesNoFiles()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int m = 0; m <= 6; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                engine.Tick();
            }
            engine.Stop();

            Assert.Empty(Directory.GetFiles(o.ClipsFolder));
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Equal(0, engine.Status().ClipsToday);
        }

        [Fact]
        public void Start_JoinsHoldingFoldersAndDeletesLoosePiecesFromAnEarlierRun()
        {
            var o = Options();
            string held = Path.Combine(o.PendingFolder, "clip_20260923-080000");
            WritePiece(held, _now.AddHours(-1));
            WritePiece(held, _now.AddHours(-1).AddMinutes(1));
            WritePiece(o.PieceFolder, _now.AddMinutes(-3));

            using var engine = Engine();
            engine.Start(o);

            var clips = Directory.GetFiles(o.ClipsFolder, "*.mp4");
            Assert.Single(clips);
            Assert.Equal(4, MediaProbe.DurationSeconds(clips[0]), 0);
            Assert.False(Directory.Exists(held));
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
        }

        [Fact]
        public void Start_OverTheCap_DeletesOldestClipsAndNothingItDidNotWrite()
        {
            var o = Options(capBytes: 2500);
            Directory.CreateDirectory(o.ClipsFolder);
            string Make(string name, int minutesAgo)
            {
                string p = Path.Combine(o.ClipsFolder, name);
                File.WriteAllBytes(p, new byte[1000]);
                File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-minutesAgo));
                return p;
            }
            string oldest = Make("2026-09-20_10-00-00.mp4", 300);
            string older = Make("2026-09-21_10-00-00.mp4", 200);
            string newest = Make("2026-09-22_10-00-00.mp4", 100);
            string foreign = Make("holiday.mp4", 1000);
            // Named exactly like a clip, older than all of them - but not one this engine wrote.
            string lookalike = Make("2026-09-19_10-00-00.mp4", 2000);
            Directory.CreateDirectory(o.WorkFolder);
            File.WriteAllLines(o.ClipLedger, new[] { oldest, older, newest }.Select(p => AlwaysOnEngine.LedgerLine(new FileInfo(p))));

            using var engine = Engine();
            engine.Start(o);

            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(older));
            Assert.True(File.Exists(newest));
            Assert.True(File.Exists(foreign));
            Assert.True(File.Exists(lookalike));
            // The evicted clip is no longer deletion authority.
            Assert.DoesNotContain(File.ReadAllLines(o.ClipLedger), l => l.StartsWith("2026-09-20_10-00-00.mp4"));
            Assert.Equal(2, File.ReadAllLines(o.ClipLedger).Length);
        }

        [Fact]
        public void EnforceCap_AnotherFileSavedUnderAnEvictedClipsName_IsNeverDeleted()
        {
            var o = Options(capBytes: 1500);
            Directory.CreateDirectory(o.ClipsFolder);
            Directory.CreateDirectory(o.WorkFolder);
            string clip = Path.Combine(o.ClipsFolder, "2026-09-20_10-00-00.mp4");
            File.WriteAllBytes(clip, new byte[1000]);
            File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddDays(-3));
            File.WriteAllLines(o.ClipLedger, new[] { AlwaysOnEngine.LedgerLine(new FileInfo(clip)) });
            // The owner's own video, saved later under the same name: same path, not the same file.
            File.WriteAllBytes(clip, new byte[5000]);

            using var engine = Engine();
            engine.Start(o);

            Assert.True(File.Exists(clip));
            Assert.Equal(5000, new FileInfo(clip).Length);
        }

        [Fact]
        public void EvictIfUnchanged_TheProvenFile_IsDeleted()
        {
            string clip = Path.Combine(_root, "2026-09-20_10-00-00.mp4");
            File.WriteAllBytes(clip, new byte[1000]);
            var fi = new FileInfo(clip);

            Assert.True(AlwaysOnEngine.EvictIfUnchanged(clip, fi.Length, fi.LastWriteTimeUtc.Ticks));

            Assert.Empty(Directory.GetFiles(_root));
        }

        [Fact]
        public void EvictIfUnchanged_FileReplacedAfterTheLedgerCheck_IsKeptUnderItsName()
        {
            // The ledger check saw the clip; another program then saved its own file at the same path.
            string clip = Path.Combine(_root, "2026-09-20_10-00-00.mp4");
            File.WriteAllBytes(clip, new byte[1000]);
            var proven = new FileInfo(clip);
            long bytes = proven.Length, ticks = proven.LastWriteTimeUtc.Ticks;
            File.WriteAllBytes(clip, new byte[5000]);
            File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(1));

            Assert.False(AlwaysOnEngine.EvictIfUnchanged(clip, bytes, ticks));

            Assert.Equal(5000, new FileInfo(clip).Length);
            Assert.Single(Directory.GetFiles(_root));
        }

        [Fact]
        public void Start_ClipLeftMidEvictionByACrash_IsRestoredUnderItsName()
        {
            var o = Options();
            Directory.CreateDirectory(o.ClipsFolder);
            string held = Path.Combine(o.ClipsFolder, ".alwayson-evict-" + Guid.NewGuid().ToString("N") + ".2026-09-20_10-00-00.mp4");
            File.WriteAllBytes(held, new byte[1000]);

            using var engine = Engine();
            engine.Start(o);

            Assert.False(File.Exists(held));
            Assert.Equal(1000, new FileInfo(Path.Combine(o.ClipsFolder, "2026-09-20_10-00-00.mp4")).Length);
        }

        [Fact]
        public void EnforceCap_LedgerLost_DeletesNothing()
        {
            var o = Options(capBytes: 1000);
            Directory.CreateDirectory(o.ClipsFolder);
            string a = Path.Combine(o.ClipsFolder, "2026-09-20_10-00-00.mp4");
            string b = Path.Combine(o.ClipsFolder, "2026-09-21_10-00-00.mp4");
            File.WriteAllBytes(a, new byte[1000]);
            File.WriteAllBytes(b, new byte[1000]);

            using var engine = Engine();
            engine.Start(o);

            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
        }

        [Fact]
        public void Tick_KeeperFailsDuringARestart_TheCaptureStillRestarts()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            WritePiece(o.PieceFolder, t0);
            _now = t0.AddMinutes(10);
            _recorders[0].Exited = true;

            // A silent finished piece held open with no sharing: the restart's pass cannot delete it.
            // It was last written a minute after it opened, on the test's clock (issue #81: the pass
            // after a restart is an ordinary one, and it reads a piece's end from its write time).
            string held = Directory.GetFiles(o.PieceFolder)[0];
            File.SetLastWriteTimeUtc(held, t0.AddMinutes(1));
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => engine.Tick());
            }

            Assert.Equal(2, _recorders.Count);
            Assert.Equal(AlwaysOnState.Listening, engine.State);

            // The new capture writes its first piece; the next pass decides the one the failed pass could not.
            WritePiece(o.PieceFolder, _now);
            _now = _now.AddMinutes(3);
            WritePiece(o.PieceFolder, _now);
            engine.Tick();
            Assert.False(File.Exists(held));
        }

        [Fact]
        public void Pause_KeeperFails_IsStillPaused()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            WritePiece(o.PieceFolder, t0);
            _now = t0.AddMinutes(10);

            string held = Directory.GetFiles(o.PieceFolder)[0];
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => engine.Pause("test"));
            }

            Assert.Equal(AlwaysOnState.Paused, engine.State);
            Assert.True(_recorders[0].Stopped);
        }

        [Fact]
        public void JoinClip_EveryKeptPieceOutsideTheSpan_TheHoldingFolderIsSetAsideNeverDeleted()
        {
            // Review fix pass, finding 4. Keep-before and keep-after of 0 s; talking at 4:30-4:34 makes the
            // span 4:30-4:34. The keeper keeps the piece from 4:00 (it holds the speech); but every test
            // piece is a 2-second file, so on disk that piece is 4:00-4:02 - wholly outside the span, as a
            // piece a stall truncated would look. Nothing is written, and the holding folder with its piece
            // is MOVED to the unreadable folder, not deleted.
            var o = new AlwaysOnOptions
            {
                SetupName = "test", Counts = SoundSource.Mic, ThresholdDb = -40,
                KeepBefore = TimeSpan.Zero, KeepAfter = TimeSpan.Zero, SilenceGap = TimeSpan.FromMinutes(5),
                CapBytes = 5L * 1024 * 1024 * 1024,
                ClipsFolder = Path.Combine(_root, "clips"), WorkFolder = Path.Combine(_root, "work"),
                PieceSeconds = 60,
            };
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int m = 0; m <= 10; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                if (m == 5) Speak(_recorders[0].Sound!, t0.AddMinutes(4.5));
                engine.Tick();
            }
            engine.Stop();

            Assert.Empty(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.Empty(Directory.GetDirectories(o.PendingFolder));
            var setAside = Assert.Single(Directory.GetDirectories(o.UnreadableFolder));
            Assert.StartsWith("clip_", Path.GetFileName(setAside));
            string kept = Assert.Single(Directory.GetFiles(setAside, "piece_*.mp4"));
            Assert.Equal("piece_" + t0.AddMinutes(4).ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4", Path.GetFileName(kept));
            Assert.Contains("set aside", engine.Status().LastError);
            Assert.Equal(0, engine.Status().ClipsToday);
            string log = TestRunIsolation.ReadLog();
            Assert.Contains("no recorded video inside the clip's span", log);
            Assert.Contains("set aside, not deleted", log);
        }

        [Fact]
        public void JoinClip_UnreadablePiece_IsSetAsideNeverDeleted()
        {
            var o = Options();
            string held = Path.Combine(o.PendingFolder, "clip_20260923-080000");
            WritePiece(held, _now.AddHours(-1));
            string broken = Path.Combine(held, "piece_" + _now.AddHours(-1).AddMinutes(1).ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4");
            File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4 });

            using var engine = Engine();
            engine.Start(o);

            Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.True(File.Exists(Path.Combine(o.UnreadableFolder, Path.GetFileName(broken))));
            Assert.Contains("set aside", engine.Status().LastError);
        }

        [Fact]
        public void Tick_CaptureAliveButWritingNothing_IsRestarted()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            WritePiece(o.PieceFolder, _now);

            _now = _now + AlwaysOnEngine.HungAfter(o.PieceSeconds) + TimeSpan.FromSeconds(1);
            engine.Tick();

            Assert.Equal(2, _recorders.Count);
            Assert.True(_recorders[0].Stopped);
            Assert.Contains("stopped writing", engine.Status().LastError);
        }

        [Fact]
        public void Tick_PiecesKeepComing_IsNotRestarted()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);
            for (int m = 0; m <= 4; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                engine.Tick();
            }

            Assert.Single(_recorders);
        }

        [Fact]
        public void Tick_RecorderDied_IsRestarted()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            _recorders[0].Exited = true;

            engine.Tick();

            Assert.Equal(2, _recorders.Count);
            Assert.Equal(AlwaysOnState.Listening, engine.State);
            Assert.Contains("stopped unexpectedly", engine.Status().LastError);
        }

        [Fact]
        public void Tick_RestartFails_RetriesWithBackoffAndSaysWhy()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            _recorders[0].Exited = true;
            _make = () => new FakeRecorder { FailStart = "the monitor is gone" };

            engine.Tick();
            Assert.Equal(AlwaysOnState.Retrying, engine.State);
            Assert.Equal("the monitor is gone", engine.Status().LastError);

            engine.Tick();   // inside the backoff: no new attempt
            Assert.Equal(2, _recorders.Count);

            _make = () => new FakeRecorder();
            _now = _now + AlwaysOnEngine.RestartBackoff[0] + TimeSpan.FromSeconds(1);
            engine.Tick();
            Assert.Equal(3, _recorders.Count);
            Assert.Equal(AlwaysOnState.Listening, engine.State);
        }

        [Fact]
        public void PauseAndResume_StopsTheCaptureThenStartsANewOne()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);

            engine.Pause("a normal recording started");
            Assert.Equal(AlwaysOnState.Paused, engine.State);
            Assert.True(_recorders[0].Stopped);
            Assert.Equal("a normal recording started", engine.Status().PausedReason);
            Assert.True(engine.IsOn);

            engine.Resume();
            Assert.Equal(AlwaysOnState.Listening, engine.State);
            Assert.Equal(2, _recorders.Count);
            Assert.Null(engine.Status().PausedReason);
        }

        [Fact]
        public void Start_RecorderFails_LeavesAlwaysOnOff()
        {
            _make = () => new FakeRecorder { FailStart = "no such monitor" };
            using var engine = Engine();

            var ex = Assert.Throws<UsageException>(() => engine.Start(Options()));
            Assert.Equal("no such monitor", ex.Message);
            Assert.Equal(AlwaysOnState.Off, engine.State);
            Assert.False(engine.IsOn);
        }

        [Fact]
        public void Start_Twice_Throws()
        {
            using var engine = Engine();
            engine.Start(Options());

            Assert.Throws<UsageException>(() => engine.Start(Options()));
        }

        [Fact]
        public void ClipsFolderInsideTheRecordingsRoot_IsNotARecordingToAnyScan()
        {
            // The clips live in Videos\AgentEyes\AlwaysOn - inside the root every repair and library
            // pass walks. A folder of MP4s with no manifest must be invisible to all of them.
            string clips = Path.Combine(_root, "AlwaysOn");
            WritePiece(clips, _now);
            File.Move(Directory.GetFiles(clips)[0], Path.Combine(clips, "2026-09-23_09-00-00.mp4"));

            Assert.False(Thumbnails.NeedsThumb(clips));
            Assert.False(TranscriptionBacklog.NeedsTranscription(clips));
            Assert.False(TranscriptionBacklog.NeedsTitle(clips));
            Assert.Empty(PostRecordingPlan.Outstanding(clips));
            Assert.Equal(0, RecordingLibrary.List(100, 0, _root).Total);
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
