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
    /// Issue #86 (tester's finding): a restart of the app split the open always-on clip and dropped the
    /// last piece - the next start saw a crash, joined the clip whole, deleted the loose piece "because
    /// its sound log went with that run", and the speech after the restart became a new clip. Once an
    /// update restarts the app on purpose, every update would do that.
    ///
    /// A PLANNED stop (<see cref="AlwaysOnEngine.StopForRestart"/>) now hands the open clip and the
    /// sound log over to the next start, which continues the clip across the restart when sound resumes
    /// within the silence gap - exactly as a capture restart does (issue #81). Same instruments as the
    /// stall tests: a fake recorder, real short MP4 pieces named as ffmpeg names them, a fake clock, and
    /// the real keeper and joiner. A second ENGINE INSTANCE stands in for the restarted process.
    /// </summary>
    public class AlwaysOnRestartTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly AlwaysOnHistory _history;
        private DateTime _now = T0;
        private readonly List<FakeRecorder> _recorders = new();

        public AlwaysOnRestartTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-restart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _history = new AlwaysOnHistory(Path.Combine(_root, AlwaysOnHistory.FileName), () => _now);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        // ---- the criterion: one clip across the restart, no piece deleted ---------------------------

        [Fact]
        public void StopForRestart_ThenStart_SoundResumesWithinTheGap_OneClipAcrossTheRestart_AndNoPieceIsDeleted()
        {
            var o = Options();
            string clipDir = RunUntilThePlannedStop(o);

            // Left for the next start: the handover, the open clip's four kept pieces (the last one -
            // finished by the stop - included), no loose piece, and NO clip written yet.
            Assert.True(File.Exists(o.HandoverFile), "the planned stop wrote no handover");
            Assert.Equal(4, Directory.GetFiles(clipDir, "piece_*.mp4").Length);
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Empty(Directory.GetFiles(o.ClipsFolder, "*.mp4"));

            // The restarted process: 30 s later (an update's stop + relaunch), a new engine instance.
            _now = T0.AddMinutes(4);
            using var again = Engine();
            again.Start(o, why: "restored at app start");
            Assert.False(File.Exists(o.HandoverFile), "the handover was not consumed");
            Assert.True(Directory.Exists(clipDir), "the open clip's holding folder was joined at start instead of carried");
            Assert.Empty(Directory.GetFiles(o.ClipsFolder, "*.mp4"));

            // The owner speaks again at 4:30 - within the 5 min gap of the 1:33 last sound.
            for (int m = 4; m <= 6; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now, null);
                if (m == 5) Speak(_recorders[1].Sound!, T0.AddSeconds(270));
                again.Tick();
            }
            Assert.Equal(AlwaysOnState.Keeping, again.State);
            _now = T0.AddMinutes(6).AddSeconds(30);
            _recorders[1].OnStop = () => File.SetLastWriteTimeUtc(PiecePath(o, T0.AddMinutes(6)), _now);
            again.Stop();

            // ONE clip, from the first piece before the restart to the last one after it: seven 2-second
            // pieces (0:00, 1:00, 2:00, 3:00 | 4:00, 5:00, 6:00), none deleted, none left behind.
            var clip = Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.Equal(T0.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clip));
            Assert.Equal(7 * 2, MediaProbe.DurationSeconds(clip), 0);
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Empty(Directory.GetDirectories(o.PendingFolder));
            Assert.Equal(1, again.Status().ClipsToday);
            Assert.Equal(0, again.Status().RestartsToday);              // a planned stop is not a capture failure

            var events = _history.Events(null, HistoryFilter.All);
            Assert.DoesNotContain(events, e => e.Text.StartsWith("DELETE", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Text.Contains("stopped for a restart") && e.Text.Contains("stays open"));
            Assert.Contains(events, e => e.Text.Contains("continues after the planned stop"));
            Assert.Contains(events, e => e.Text.Contains("continues across a capture restart"));   // the #81 bridge carried it
        }

        [Fact]
        public void StopForRestart_ThenStartAfterLongerThanTheGap_TheCarriedClipIsWrittenWithItsSpan_AndNewSpeechStartsANewClip()
        {
            var o = Options();
            string clipDir = RunUntilThePlannedStop(o);

            // The app comes back 6.5 min later - longer than the 5 min gap: the carried clip cannot be
            // continued. It is closed by the keeper WITH its span (not joined whole as a crash's clip
            // would be), and the speech at 10:30 starts a second clip with its own lead-in.
            _now = T0.AddMinutes(10);
            using var again = Engine();
            again.Start(o);
            Assert.True(Directory.Exists(clipDir));
            for (int m = 10; m <= 12; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now, null);
                if (m == 11) Speak(_recorders[1].Sound!, T0.AddSeconds(630));
                again.Tick();
            }
            _now = T0.AddMinutes(12).AddSeconds(30);
            _recorders[1].OnStop = () => File.SetLastWriteTimeUtc(PiecePath(o, T0.AddMinutes(12)), _now);
            again.Stop();

            var clips = Directory.GetFiles(o.ClipsFolder, "*.mp4").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, clips.Length);
            Assert.Equal(T0.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clips[0]));
            Assert.Equal(4 * 2, MediaProbe.DurationSeconds(clips[0]), 0);               // 0:00, 1:00, 2:00, 3:00
            Assert.Equal(T0.AddMinutes(10).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4", Path.GetFileName(clips[1]));
            Assert.Equal(3 * 2, MediaProbe.DurationSeconds(clips[1]), 0);               // 10:00, 11:00, 12:00
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Empty(Directory.GetDirectories(o.PendingFolder));
            Assert.DoesNotContain(_history.Events(null, HistoryFilter.All), e => e.Text.StartsWith("DELETE", StringComparison.Ordinal));
        }

        // ---- the handover itself -----------------------------------------------------------------------

        [Fact]
        public void StopForRestart_WritesTheOpenClipAndTheSoundHeard()
        {
            var o = Options();
            string clipDir = RunUntilThePlannedStop(o);

            var h = AlwaysOnHandover.Load(o.HandoverFile);
            Assert.NotNull(h);
            Assert.Equal("test restart", h!.Why);
            Assert.Equal(T0.AddMinutes(3).AddSeconds(30), h.StoppedUtc);
            Assert.NotNull(h.Open);
            Assert.Equal(clipDir, h.Open!.Dir);
            Assert.Equal(T0, h.Open.StartUtc);                                    // the clip began with the 0:00 piece
            Assert.Equal(T0.AddSeconds(90), h.Open.FirstSoundUtc);
            Assert.Equal(T0.AddSeconds(93), h.Open.LastSoundUtc);
            Assert.Equal(T0.AddMinutes(3).AddSeconds(30), h.Open.LastPieceEndUtc); // the piece the stop finished
            Assert.Equal(2, h.NextClip);
            // The four seconds of talking, as unix seconds - what the keeper needs to judge what is left.
            long first = new DateTimeOffset(T0.AddSeconds(90)).ToUnixTimeSeconds();
            Assert.Equal(new[] { first, first + 1, first + 2, first + 3 }, h.Sound.SoundSeconds.OrderBy(s => s).ToArray());
            Assert.Equal(first + 3, h.Sound.LastSound);
        }

        [Fact]
        public void StopForRestart_NoClipOpen_LoosePiecesWaitForTheKeeper_AndAreJudgedByTheRuleNotByRecover()
        {
            // Two silent pieces, no clip: the planned stop leaves them waiting (their keep-before window
            // has not passed). The restart must not delete them at start; the keeper deletes them later
            // by the ordinary rule - with the sound log that says they had no sound.
            var o = Options();
            using (var engine = Engine())
            {
                engine.Start(o);
                for (int m = 0; m <= 1; m++)
                {
                    _now = T0.AddMinutes(m);
                    WritePiece(o.PieceFolder, _now, null);
                    engine.Tick();
                }
                _now = T0.AddSeconds(90);
                _recorders[0].OnStop = () => File.SetLastWriteTimeUtc(PiecePath(o, T0.AddMinutes(1)), _now);
                engine.StopForRestart("test restart");
            }
            Assert.Equal(2, Directory.GetFiles(o.PieceFolder, "piece_*.mp4").Length);
            var h = AlwaysOnHandover.Load(o.HandoverFile);
            Assert.NotNull(h);
            Assert.Null(h!.Open);

            _now = T0.AddMinutes(2);
            using var again = Engine();
            again.Start(o);
            Assert.Equal(2, Directory.GetFiles(o.PieceFolder, "piece_*.mp4").Length);     // still there
            Assert.DoesNotContain(_history.Events(null, HistoryFilter.All), e => e.Text.StartsWith("DELETE", StringComparison.Ordinal));

            // Once keep-before (2 min) and the settle time have passed since each piece with no sound,
            // the rule deletes them - "no sound within ... before it or ... after it", not "left by an
            // earlier run".
            _now = T0.AddMinutes(6);
            WritePiece(o.PieceFolder, _now, null);
            again.Tick();
            Assert.Single(Directory.GetFiles(o.PieceFolder, "piece_*.mp4"));               // only the piece being written
            var deletes = _history.Events(null, HistoryFilter.All).Where(e => e.Text.StartsWith("DELETE", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, deletes.Count);
            Assert.All(deletes, e => Assert.Contains("no sound within", e.Text));
            Assert.All(deletes, e => Assert.DoesNotContain("earlier run", e.Text));
        }

        [Fact]
        public void Start_NoHandover_RecoversAsFromACrash()
        {
            // The pre-#86 behaviour is untouched when there is no handover: a holding folder is joined
            // whole and a loose piece - with no sound log anywhere - is deleted and said so. (The same
            // case is also covered by Start_JoinsHoldingFoldersAndDeletesLoosePiecesFromAnEarlierRun.)
            var o = Options();
            string held = Path.Combine(o.PendingFolder, "clip_20260923-080000");
            WritePiece(held, T0.AddHours(-1), null);
            WritePiece(o.PieceFolder, T0.AddMinutes(-3), null);

            using var engine = Engine();
            engine.Start(o);

            Assert.Single(Directory.GetFiles(o.ClipsFolder, "*.mp4"));
            Assert.False(Directory.Exists(held));
            Assert.Empty(Directory.GetFiles(o.PieceFolder));
            Assert.Contains(_history.Events(null, HistoryFilter.All), e => e.Text.StartsWith("DELETE", StringComparison.Ordinal) && e.Text.Contains("earlier run"));
        }

        [Fact]
        public void Start_HandoverNamesAMissingClipFolder_TheSoundIsRestored_TheClipIsNotContinued_AndTheHandoverIsConsumed()
        {
            var o = Options();
            Directory.CreateDirectory(o.WorkFolder);
            long second = new DateTimeOffset(T0.AddSeconds(30)).ToUnixTimeSeconds();
            new AlwaysOnHandover
            {
                StoppedUtc = T0.AddMinutes(1),
                Why = "test",
                Open = new HandoverClip { Id = 3, Dir = Path.Combine(o.PendingFolder, "clip_gone"), FirstSoundUtc = T0, LastSoundUtc = T0, LastPieceEndUtc = T0, StartUtc = T0 },
                NextClip = 4,
                Sound = new SoundLogState(new[] { second, second + 1, second + 2 }, new[] { second, second + 1, second + 2 }, second + 2),
            }.Save(o.HandoverFile);

            _now = T0.AddMinutes(2);
            using var engine = Engine();
            engine.Start(o);

            Assert.False(File.Exists(o.HandoverFile));
            Assert.Equal(AlwaysOnState.Listening, engine.State);
            Assert.Contains(_history.Events(null, HistoryFilter.All), e => e.Text.Contains("clip_gone") && e.Text.Contains("is not there"));
            Assert.Equal(T0.AddSeconds(32), _recorders[0].Sound!.LastSoundUtc);       // the sound log came through
        }

        [Fact]
        public void Start_CorruptHandover_IsSetAsideAndTheStartRecoversAsFromACrash()
        {
            var o = Options();
            Directory.CreateDirectory(o.WorkFolder);
            File.WriteAllText(o.HandoverFile, "{ this is not a handover");
            WritePiece(o.PieceFolder, T0.AddMinutes(-3), null);

            using var engine = Engine();
            engine.Start(o);

            Assert.False(File.Exists(o.HandoverFile));
            Assert.True(File.Exists(o.HandoverFile + ".bad"), "the unreadable handover was not set aside for a look");
            Assert.Empty(Directory.GetFiles(o.PieceFolder));                          // crash behaviour: no sound log, deleted and said
            Assert.Contains(_history.Events(null, HistoryFilter.All), e => e.Text.StartsWith("DELETE", StringComparison.Ordinal) && e.Text.Contains("earlier run"));
        }

        [Fact]
        public void StopForRestart_WhenOff_IsANoOp()
        {
            var o = Options();
            using var engine = Engine();
            engine.StopForRestart("nothing to stop");
            Assert.False(File.Exists(o.HandoverFile));
            Assert.Equal(AlwaysOnState.Off, engine.State);
        }

        [Fact]
        public void StopForRestart_KeeperPassFails_TheHandoverIsStillWritten_AndTheFailureIsOnRecord()
        {
            // The last piece is held open exclusively while the planned stop runs, so the keeper's move
            // of it into the clip folder fails. The handover must be written regardless - it is what
            // lets the next start decide that very piece - and the failure is logged and in the history.
            var o = Options();
            using var engine = Engine();
            engine.Start(o);
            for (int m = 0; m <= 3; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now, null);
                if (m == 2) Speak(_recorders[0].Sound!, T0.AddSeconds(90));
                engine.Tick();
            }
            _now = T0.AddMinutes(3).AddSeconds(30);
            using (new FileStream(PiecePath(o, T0.AddMinutes(3)), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                engine.StopForRestart("test restart");
            }

            Assert.Equal(AlwaysOnState.Off, engine.State);
            var h = AlwaysOnHandover.Load(o.HandoverFile);
            Assert.NotNull(h);
            Assert.NotNull(h!.Open);
            Assert.Single(Directory.GetFiles(o.PieceFolder, "piece_*.mp4"));           // the held piece waits for the next start
            Assert.Contains(_history.Events(null, HistoryFilter.All), e => e.Text.Contains("keeper pass at the planned stop failed"));
            Assert.Contains("keeper failed at the planned stop", engine.Status().LastError);
        }

        // ---- the sound log's part ---------------------------------------------------------------------

        [Fact]
        public void SoundLog_ExportThenImport_TheNewLogAnswersAsTheOldOneDid()
        {
            var old = new SoundLog(SoundSource.Mic, -40);
            Speak(old, T0.AddSeconds(90));
            var state = old.Export(T0);

            var fresh = new SoundLog(SoundSource.Mic, -40);
            Assert.False(fresh.AnySound(T0, T0.AddMinutes(5)));
            fresh.Import(state);

            Assert.True(fresh.AnySound(T0.AddSeconds(90), T0.AddSeconds(93)));
            Assert.Equal(T0.AddSeconds(90), fresh.FirstSound(T0, T0.AddMinutes(5)));
            Assert.Equal(T0.AddSeconds(93), fresh.LastSound(T0, T0.AddMinutes(5)));
            Assert.Equal(T0.AddSeconds(93), fresh.LastSoundUtc);
            Assert.Equal(4, fresh.Summarize(T0, T0.AddMinutes(5)).SoundSeconds);
        }

        [Fact]
        public void SoundLog_Export_KeepsOnlySecondsFromTheHorizonOn()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            Speak(log, T0.AddSeconds(10));
            Speak(log, T0.AddMinutes(10));

            var state = log.Export(T0.AddMinutes(5));

            long cut = new DateTimeOffset(T0.AddMinutes(5)).ToUnixTimeSeconds();
            Assert.Equal(4, state.SoundSeconds.Length);
            Assert.All(state.SoundSeconds, s => Assert.True(s >= cut));
            Assert.All(state.LoudSeconds, s => Assert.True(s >= cut));
            Assert.Equal(new DateTimeOffset(T0.AddMinutes(10).AddSeconds(3)).ToUnixTimeSeconds(), state.LastSound);
        }

        [Fact]
        public void SoundLog_Import_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new SoundLog(SoundSource.Mic, -40).Import(null!));
        }

        [Fact]
        public void Handover_Load_MissingFile_IsNull()
        {
            Assert.Null(AlwaysOnHandover.Load(Path.Combine(_root, "none.json")));
        }

        // ---- helpers ----------------------------------------------------------------------------------

        /// <summary>
        /// The first run: pieces at 0:00, 1:00, 2:00 and 3:00, talking at 1:30, then the planned stop at
        /// 3:30 while the 3:00 piece is being written. Returns the open clip's holding folder.
        /// </summary>
        private string RunUntilThePlannedStop(AlwaysOnOptions o)
        {
            using var engine = Engine();
            engine.Start(o);
            for (int m = 0; m <= 3; m++)
            {
                _now = T0.AddMinutes(m);
                WritePiece(o.PieceFolder, _now, null);
                if (m == 2) Speak(_recorders[0].Sound!, T0.AddSeconds(90));
                engine.Tick();
            }
            Assert.Equal(AlwaysOnState.Keeping, engine.State);
            string clipDir = Assert.Single(Directory.GetDirectories(o.PendingFolder, "clip_*"));

            // The stop: ffmpeg finishes the 3:00 piece at 3:30.
            _now = T0.AddMinutes(3).AddSeconds(30);
            _recorders[0].OnStop = () => File.SetLastWriteTimeUtc(PiecePath(o, T0.AddMinutes(3)), _now);
            engine.StopForRestart("test restart");
            Assert.Equal(AlwaysOnState.Off, engine.State);
            Assert.True(_recorders[0].Stopped);
            return clipDir;
        }

        private AlwaysOnOptions Options() => new()
        {
            SetupName = "test",
            Counts = SoundSource.Mic,
            ThresholdDb = -40,
            // Two-minute margins either side of the speech (2 min is the most keep-before allows) and the
            // 5 min silence gap that closes a clip - the restart bridge is the gap (issues #79, #81).
            KeepBefore = TimeSpan.FromMinutes(2),
            KeepAfter = TimeSpan.FromMinutes(2),
            SilenceGap = TimeSpan.FromMinutes(5),
            CapBytes = 5L * 1024 * 1024 * 1024,
            ClipsFolder = Path.Combine(_root, "clips"),
            WorkFolder = Path.Combine(_root, "work"),
            PieceSeconds = 60,
        };

        private AlwaysOnEngine Engine() => new(() =>
        {
            var r = new FakeRecorder();
            _recorders.Add(r);
            return r;
        }, () => _now, ownTimer: false, history: _history);

        private static string PiecePath(AlwaysOnOptions o, DateTime startUtc) =>
            Path.Combine(o.PieceFolder, "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4");

        /// <summary>A real two-second MP4 named as ffmpeg names a piece that opened at <paramref name="startUtc"/>,
        /// last written at <paramref name="lastWriteUtc"/> on the test's clock (null: left at the real time,
        /// which is after every test time, so the next piece's start is its end).</summary>
        private static void WritePiece(string folder, DateTime startUtc, DateTime? lastWriteUtc)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "piece_" + startUtc.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4");
            File.Copy(TemplatePiece.Value, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, lastWriteUtc ?? DateTime.UtcNow);
        }

        /// <summary>A real two-second MP4 (picture and sound), encoded once per test run.</summary>
        private static readonly Lazy<string> TemplatePiece = new(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "agenteyes-restart-template-" + Guid.NewGuid().ToString("N") + ".mp4");
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
            public Action? OnStop;

            public void Start(AlwaysOnOptions options, SoundLog sound) => Sound = sound;

            public void Stop()
            {
                Stopped = true;
                OnStop?.Invoke();
            }

            public bool HasExited => false;
            public string Encoder => "fake";
            public string StderrTail => "fake ffmpeg";
            public string ProcessState => "still running (pid 1)";
            public void Dispose() { }
        }
    }
}
