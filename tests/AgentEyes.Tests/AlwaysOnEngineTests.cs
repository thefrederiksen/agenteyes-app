using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes.AlwaysOn;
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

        private AlwaysOnOptions Options(long capBytes = 5L * 1024 * 1024 * 1024) => new()
        {
            SetupName = "test",
            Counts = SoundSource.Mic,
            ThresholdDb = -40,
            KeepBefore = TimeSpan.FromMinutes(2),
            KeepAfter = TimeSpan.FromMinutes(2),
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
                if (m == 5) _recorders[0].Sound!.Observe(SoundSource.Mic, t0.AddMinutes(4.5), 0.5f);
                engine.Tick();
            }
            engine.Stop();

            // Windows of 2 min: pieces 2..6 have the 4:30 sound within [start - 2, end + 2].
            var clips = Directory.GetFiles(o.ClipsFolder, "*.mp4");
            Assert.Single(clips);
            Assert.Equal(t0.AddMinutes(2).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clips[0]));
            Assert.Equal(5 * 2, MediaProbe.DurationSeconds(clips[0]), 0);
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Empty(Directory.GetDirectories(o.PendingFolder));

            var s = engine.Status();
            Assert.Equal(AlwaysOnState.Off, s.State);
            Assert.Equal(1, s.ClipsToday);
            Assert.True(s.DiscardedSecondsToday > 0);
            Assert.True(_recorders[0].Stopped);
        }

        [Fact]
        public void Engine_ClipIsWrittenOnceAfterWindowOfSilencePasses_WhileStillRecording()
        {
            var o = Options();
            using var engine = Engine();
            var t0 = _now;
            engine.Start(o);

            for (int m = 0; m <= 8; m++)
            {
                _now = t0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now);
                if (m == 1) _recorders[0].Sound!.Observe(SoundSource.Mic, t0.AddSeconds(30), 0.5f);
                engine.Tick();
                if (m == 3) Assert.Equal(AlwaysOnState.Keeping, engine.State);
            }

            // Sound at 0:30 with a 2 min after window: the clip closed while always-on is still on.
            Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.Equal(AlwaysOnState.Listening, engine.State);
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

            // A silent finished piece held open with no sharing: the final pass cannot delete it.
            string held = Directory.GetFiles(o.PieceFolder)[0];
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
            public string StderrTail => "fake ffmpeg: device lost";
            public void Dispose() { }
        }
    }
}
