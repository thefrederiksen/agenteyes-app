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
    /// Issue #81: the always-on capture stalled for ~2.5 min and the restart split the open clip.
    /// These pin the four fixes - full stderr in the log, a stall caught within 75 s of the piece's
    /// expected end, a restart that keeps the clip open, and a restart's short leftovers that are
    /// joined instead of closing the clip - without launching ffmpeg for the capture itself.
    /// </summary>
    public class AlwaysOnStallTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);
        private static readonly Func<DateTime, DateTime, bool> Silence = (_, _) => false;

        private readonly string _root;
        private DateTime _now = T0;
        private readonly List<FakeRecorder> _recorders = new();
        private Func<FakeRecorder> _make = () => new FakeRecorder();

        public AlwaysOnStallTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-stall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        // ---- full stderr capture ------------------------------------------------

        [Fact]
        public void Tail_MoreLinesThanTheTail_ReturnsTheLast20EachInFull()
        {
            var err = new FfmpegStderr();
            string longLine = "[segment @ 000001a9d7153f00] Opening '" + new string('x', 700) + ".mp4' for writing";
            for (int i = 1; i <= 25; i++) err.Add(i == 25 ? longLine : $"line {i}");

            string tail = err.Tail();

            var lines = tail.Split(Environment.NewLine);
            Assert.Equal(FfmpegStderr.TailLines, lines.Length);
            Assert.Equal("line 6", lines[0]);
            Assert.Equal(longLine, lines[^1]);            // the whole line, not its last 800 characters
            Assert.DoesNotContain("...", tail);
        }

        [Fact]
        public void Tail_NothingWritten_SaysSo()
        {
            Assert.Equal("(ffmpeg said nothing)", new FfmpegStderr().Tail());
        }

        [Fact]
        public void Add_AllDayOfLines_KeepsOnlyTheCapacity()
        {
            var err = new FfmpegStderr();
            for (int i = 0; i < FfmpegStderr.Capacity * 3; i++) err.Add($"line {i}");

            var all = err.Tail(FfmpegStderr.Capacity + 50).Split(Environment.NewLine);

            Assert.Equal(FfmpegStderr.Capacity, all.Length);
            Assert.Equal($"line {FfmpegStderr.Capacity * 3 - 1}", all[^1]);
        }

        [Fact]
        public void Add_TheScreenGrabDies_IsSeenAsADeadInput()
        {
            // The exact lines of stall 1 on 2026-09-24 (the owner's log, 09:04:16).
            var err = new FfmpegStderr();
            err.Add(@"[segment @ 000001a9d7153f00] Opening 'C:\pieces\piece_20260924-130145.mp4' for writing");
            Assert.Null(err.FatalInputLine);

            err.Add("[in#0/gdigrab @ 000001a9d70db000] Failed to capture image (error 5)");
            err.Add("[in#0/gdigrab @ 000001a9d70d6c00] Error during demuxing: I/O error");

            Assert.Equal("[in#0/gdigrab @ 000001a9d70d6c00] Error during demuxing: I/O error", err.FatalInputLine);
        }

        [Fact]
        public void IsFatalInputError_OrdinaryLines_AreNot()
        {
            Assert.False(FfmpegStderr.IsFatalInputError("[segment @ 0] Opening 'x.mp4' for writing"));
            Assert.False(FfmpegStderr.IsFatalInputError("[dshow @ 0] real-time buffer [mic] too full or near too full"));
            Assert.True(FfmpegStderr.IsFatalInputError("[in#1/dshow @ 0] Error during demuxing: I/O error"));
        }

        [Fact]
        public void Tick_CaptureFails_LogsTheFullTailAndTheProcessStateAndRecordsTheRestart()
        {
            var tail = string.Join(Environment.NewLine, Enumerable.Range(1, 20).Select(i => $"ffmpeg line {i} " + new string('y', 100)));
            _make = () => new FakeRecorder { StderrTail = tail, ProcessState = "still running (pid 42) - an input died: Error during demuxing: I/O error" };
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            _make = () => new FakeRecorder();
            _recorders[0].Exited = true;
            _now = T0.AddSeconds(30);

            engine.Tick();

            Assert.Contains(tail, engine.LastRestartReport);
            Assert.Contains("still running (pid 42) - an input died", engine.LastRestartReport);
            var s = engine.Status();
            Assert.Equal(1, s.RestartsToday);
            var r = Assert.Single(s.Restarts);
            Assert.Equal(T0.AddSeconds(30), r.AtUtc);
            Assert.Contains("stopped unexpectedly", r.Reason);
            Assert.Equal(T0.AddSeconds(30), r.RecoveredUtc);
        }

        // ---- stall detection timing ---------------------------------------------

        [Fact]
        public void HungAfter_OneMinutePieces_IsCaughtWithin75SecondsOfTheExpectedEnd()
        {
            // Worst case: the check that sees the stall runs one tick after the threshold.
            var latest = AlwaysOnEngine.HungAfter(60) + AlwaysOnEngine.TickInterval - TimeSpan.FromSeconds(60);

            Assert.True(latest <= TimeSpan.FromSeconds(75), $"a stall could go unseen for {latest.TotalSeconds}s past the piece's end");
        }

        [Fact]
        public void Tick_NoNewPiece_IsRestartedWithin75SecondsOfTheExpectedEnd()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            DateTime pieceStart = T0.AddSeconds(2);
            WritePiece(o.PieceFolder, pieceStart, pieceStart.AddSeconds(1));

            // The engine's timer: one pass every 15 s; the next piece never comes.
            DateTime? caught = null;
            for (_now = T0; _now < T0.AddMinutes(5) && caught == null; _now += AlwaysOnEngine.TickInterval)
            {
                engine.Tick();
                if (_recorders.Count > 1) caught = _now;
            }

            Assert.NotNull(caught);
            var pastExpectedEnd = caught!.Value - pieceStart.AddSeconds(o.PieceSeconds);
            Assert.True(pastExpectedEnd <= TimeSpan.FromSeconds(75), $"caught {pastExpectedEnd.TotalSeconds}s after the piece's expected end");
            Assert.Contains("stopped writing", engine.Status().LastError);
        }

        [Fact]
        public void Tick_NextPieceLateButInsideTheMargin_IsNotRestarted()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            WritePiece(o.PieceFolder, T0.AddSeconds(2), T0.AddSeconds(3));

            _now = T0.AddSeconds(2) + AlwaysOnEngine.HungAfter(o.PieceSeconds) - TimeSpan.FromSeconds(1);
            engine.Tick();

            Assert.Single(_recorders);
            Assert.Equal(0, engine.Status().RestartsToday);
        }

        [Fact]
        public void Tick_CapturesKeepDyingAtOnce_TheSecondRestartWaitsOnTheBackoff()
        {
            // A locked session: every new capture's screen input dies straight away.
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            _recorders[0].Exited = true;
            _make = () => new FakeRecorder { Exited = true };

            _now = T0.AddSeconds(15);
            engine.Tick();                                   // first quick failure: restarted at once
            Assert.Equal(2, _recorders.Count);
            Assert.Equal(AlwaysOnState.Listening, engine.State);

            _now = T0.AddSeconds(30);
            engine.Tick();                                   // second in a row: waits
            Assert.Equal(2, _recorders.Count);
            Assert.Equal(AlwaysOnState.Retrying, engine.State);

            _make = () => new FakeRecorder();
            _now = _now + AlwaysOnEngine.RestartBackoff[0] + TimeSpan.FromSeconds(1);
            engine.Tick();
            Assert.Equal(3, _recorders.Count);
            Assert.Equal(AlwaysOnState.Listening, engine.State);
            Assert.Equal(2, engine.Status().RestartsToday);
        }

        // ---- restart keeps the clip ----------------------------------------------

        [Fact]
        public void Decide_KeptPieceAfterARestartHoleWithinTheBridge_ContinuesTheClipAndReportsTheHole()
        {
            // The clip ended at 2:00; the restarted capture's first piece opens at 2:40.
            var piece = new Piece("p", T0 + 2 * Min + TimeSpan.FromSeconds(40), T0 + 3 * Min + TimeSpan.FromSeconds(40), 1000);

            var plan = KeeperRule.Decide(new[] { piece }, SoundAt(90), T0 + 4 * Min, Five, Five, 7, T0 + 2 * Min, 8,
                final: false, restartBridge: Five);

            Assert.Equal(7, plan.Keep.Single().Clip);
            Assert.Empty(plan.Close);
            Assert.Equal(7, plan.OpenClip);
            Assert.Equal((7, T0 + 2 * Min, piece.StartUtc), plan.Bridged.Single());
        }

        [Fact]
        public void Decide_HoleLongerThanTheBridge_StartsANewClip()
        {
            var piece = new Piece("p", T0 + 8 * Min, T0 + 9 * Min, 1000);

            var plan = KeeperRule.Decide(new[] { piece }, SoundAt(500), T0 + 10 * Min, Five, Five, 7, T0 + 2 * Min, 8,
                final: false, restartBridge: Five);

            Assert.Equal(new[] { 7 }, plan.Close);
            Assert.Equal(8, plan.Keep.Single().Clip);
            Assert.Empty(plan.Bridged);
        }

        [Fact]
        public void Decide_NegativeRestartBridge_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                KeeperRule.Decide(new List<Piece>(), Silence, T0, Five, Five, null, null, 1, final: false,
                    restartBridge: -Min));
        }

        // ---- short pieces -------------------------------------------------------

        [Fact]
        public void Decide_ShortSilentPieceAfterTheOpenClip_IsJoinedAndTheClipStaysOpen()
        {
            // Stall 2: a dying ffmpeg flushed a 2 s and a 1 s piece; both were deleted and the clip closed.
            var shortA = new Piece("a", T0 + 2 * Min, T0 + 2 * Min + TimeSpan.FromSeconds(2), 100);
            var shortB = new Piece("b", shortA.EndUtc, shortA.EndUtc + TimeSpan.FromSeconds(1), 100);

            var plan = KeeperRule.Decide(new[] { shortA, shortB }, Silence, T0 + 30 * Min, Five, Five, 7, T0 + 2 * Min, 8,
                final: false, restartBridge: Five);

            Assert.Equal(new[] { (shortA, 7), (shortB, 7) }, plan.Keep.Select(k => (k.Piece, k.Clip)).ToArray());
            Assert.Empty(plan.Delete);
            Assert.Empty(plan.Close);
            Assert.Equal(7, plan.OpenClip);
            Assert.Equal(shortB.EndUtc, plan.OpenClipEndUtc);
        }

        [Fact]
        public void Decide_ShortPieceWithNoOpenClip_IsDecidedByTheNormalRule()
        {
            var shortPiece = new Piece("a", T0, T0 + TimeSpan.FromSeconds(2), 100);

            var plan = KeeperRule.Decide(new[] { shortPiece }, Silence, T0 + 30 * Min, Five, Five, null, null, 1, final: false);

            Assert.Equal(shortPiece, plan.Delete.Single());
            Assert.Empty(plan.Keep);
        }

        [Fact]
        public void Engine_StallDuringAClip_TheRestartContinuesTheSameClipWithItsLeadIn()
        {
            // Stall 2 replayed (2-minute windows): sound at 2:30, the capture hangs in the piece that
            // opened at 3:00, the restart flushes two short pieces, the new capture starts at 4:47 and
            // the owner speaks at 5:30. Before issue #81 that was two clips and the lead-in was lost.
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            for (int m = 0; m <= 3; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now, null);
                if (m == 3) Speak(_recorders[0].Sound!, T0.AddSeconds(150));
                engine.Tick();
            }

            // The hang: no piece after 3:00; it is caught at 4:30. The dying ffmpeg leaves its last
            // piece written at 4:30 and flushes two short ones.
            DateTime stalledPiece = T0.AddMinutes(3);
            _recorders[0].OnStop = () =>
            {
                File.SetLastWriteTimeUtc(PiecePath(o, stalledPiece), T0.AddSeconds(270));
                WritePiece(o.PieceFolder, T0.AddSeconds(270), T0.AddSeconds(272));
                WritePiece(o.PieceFolder, T0.AddSeconds(272), T0.AddSeconds(273));
            };
            while (_recorders.Count == 1)
            {
                _now += AlwaysOnEngine.TickInterval;
                engine.Tick();
                Assert.True(_now < T0.AddMinutes(6), "the stall was never detected");
            }
            Assert.Equal(AlwaysOnState.Keeping, engine.State);   // the clip is still open after the restart

            // The new capture: pieces from 4:47; the owner speaks at 5:30.
            var sound = _recorders[1].Sound!;
            for (int m = 0; m <= 4; m++)
            {
                DateTime start = T0.AddSeconds(287).AddMinutes(m);
                if (_now < start) _now = start;
                WritePiece(o.PieceFolder, start, null);
                if (m == 1) Speak(sound, T0.AddSeconds(330));
                engine.Tick();
            }
            _now = _now.AddMinutes(1);
            engine.Stop();

            var clip = Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.Equal(T0.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clip));
            // Every piece is a 2-second test file: 4 before the stall, 2 short leftovers, and the 3 new
            // pieces with the 5:30 speech in their window (the piece from 7:47 is past it).
            Assert.Equal(9 * 2, MediaProbe.DurationSeconds(clip), 0);
            Assert.Equal(1, engine.Status().RestartsToday);
        }

        [Fact]
        public void Tick_CaptureDownLongerThanTheAfterWindow_WritesTheOpenClip()
        {
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            for (int m = 0; m <= 2; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now, null);
                if (m == 1) Speak(_recorders[0].Sound!, T0.AddSeconds(30));
                engine.Tick();
            }
            _recorders[0].OnStop = () => File.SetLastWriteTimeUtc(PiecePath(o, T0.AddMinutes(2)), T0.AddMinutes(3));
            _recorders[0].Exited = true;
            _make = () => new FakeRecorder { FailStart = "the monitor is gone" };
            _now = T0.AddMinutes(3);
            engine.Tick();
            Assert.Equal(AlwaysOnState.Retrying, engine.State);
            Assert.Empty(Directory.GetFiles(o.ClipsFolder, "*.mp4"));   // still open: the capture may come back

            _now = T0.AddMinutes(3) + o.KeepAfter + TimeSpan.FromSeconds(30);
            engine.Tick();

            Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
        }

        // ---- helpers --------------------------------------------------------------

        private AlwaysOnOptions Options() => new()
        {
            SetupName = "test",
            Counts = SoundSource.Mic,
            ThresholdDb = -40,
            KeepBefore = TimeSpan.FromMinutes(2),
            KeepAfter = TimeSpan.FromMinutes(2),
            CapBytes = 5L * 1024 * 1024 * 1024,
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

        private static Func<DateTime, DateTime, bool> SoundAt(params int[] seconds) =>
            (a, b) => seconds.Any(s => T0.AddSeconds(s) >= a && T0.AddSeconds(s) <= b);

        private static string PiecePath(AlwaysOnOptions o, DateTime startUtc) =>
            Path.Combine(o.PieceFolder, "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4");

        /// <summary>A real two-second MP4 named as ffmpeg names a piece that opened at <paramref name="startUtc"/>,
        /// last written at <paramref name="lastWriteUtc"/> on the test's clock (null: left at the real time,
        /// which is after every test time, so the next piece's start is its end).</summary>
        private static void WritePiece(string folder, DateTime startUtc, DateTime? lastWriteUtc)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4");
            // One encode per test run, copied for every piece: these tests make many pieces, and an
            // encode per piece loads the machine enough to starve timing-sensitive tests running beside them.
            File.Copy(TemplatePiece.Value, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, lastWriteUtc ?? DateTime.UtcNow);
        }

        /// <summary>A real two-second MP4 (picture and sound), encoded once per test run.</summary>
        private static readonly Lazy<string> TemplatePiece = new(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "agenteyes-stall-template-" + Guid.NewGuid().ToString("N") + ".mp4");
            Ffmpeg.Run(new[]
            {
                "-y", "-f", "lavfi", "-i", "color=c=gray:s=160x90:r=10:d=2",
                "-f", "lavfi", "-i", "sine=f=300:d=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
                path,
            }, "test piece template");
            return path;
        });

        /// <summary>Four seconds of talking at -20 dBFS from <paramref name="atUtc"/> (issue #72's sustained rule).</summary>
        private static void Speak(SoundLog log, DateTime atUtc)
        {
            for (int s = 0; s < 4; s++)
                log.Observe(SoundSource.Mic, atUtc.AddSeconds(s), AudioLevel.FromDb(-20, -10, 48000));
            log.Observe(SoundSource.Mic, atUtc.AddSeconds(4), AudioLevel.FromDb(-80, -70, 48000));
        }

        private sealed class FakeRecorder : IPieceRecorder
        {
            public SoundLog? Sound;
            public bool Stopped;
            public bool Exited;
            public string? FailStart;
            public Action? OnStop;

            public void Start(AlwaysOnOptions options, SoundLog sound)
            {
                if (FailStart != null) throw new UsageException(FailStart);
                Sound = sound;
            }

            public void Stop()
            {
                Stopped = true;
                OnStop?.Invoke();
            }

            public bool HasExited => Exited;
            public string Encoder => "fake";
            public string StderrTail { get; set; } = "fake ffmpeg: device lost";
            public string ProcessState { get; set; } = "exited with code 1";
            public void Dispose() { }
        }
    }
}
