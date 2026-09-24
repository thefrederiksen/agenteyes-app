using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AgentEyes.AlwaysOn;
using AgentEyes.Audio;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// What counts as SOUND for the always-on keeper (issue #72): per-second RMS, a line of floor +
    /// 15 dB but never below -50 dBFS, and only sustained sound (3 loud seconds within 10) counts.
    /// The level series are replayed straight into the real <see cref="SoundLog"/>, and the keeper's
    /// decisions come from the real <see cref="KeeperRule"/>.
    /// </summary>
    public class AlwaysOnSoundRuleTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
        private const int SamplesPerSecond = 48000;

        /// <summary>Feed one whole second at <paramref name="rmsDb"/> dBFS RMS as a single buffer.</summary>
        private static void Second(SoundLog log, int offset, double rmsDb, double? peakDb = null,
            SoundSource source = SoundSource.Mic) =>
            log.Observe(source, T0.AddSeconds(offset), AudioLevel.FromDb(rmsDb, peakDb ?? rmsDb + 10, SamplesPerSecond));

        /// <summary>Feed a per-second RMS series starting at offset 0, then one buffer of the next
        /// second so the last second of the series is closed and judged.</summary>
        private static void Replay(SoundLog log, IReadOnlyList<double> rmsDb)
        {
            for (int i = 0; i < rmsDb.Count; i++) Second(log, i, rmsDb[i]);
            Second(log, rmsDb.Count, -90);
        }

        private static DateTime At(int offset) => T0.AddSeconds(offset);

        // ---- the replay of the measured quiet room (criterion 1) ---------------------------------

        private sealed record LevelRow(int Second, double RmsDb, double PeakDb);

        private const string FixturePath = @"tests\AgentEyes.Tests\fixtures\issue72-quiet-room-levels.csv";

        /// <summary>The committed fixture: minutes 282-325 of the 5 h 51 min clip the owner found
        /// silent. 2580 per-second rows. See the header of the file for how it was measured.</summary>
        private static List<LevelRow> QuietRoomFixture()
        {
            string path = Path.Combine(RepoSource.Root, FixturePath);
            Assert.True(File.Exists(path), $"the replay fixture is missing: {path}");
            var rows = File.ReadAllLines(path)
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Select(l => l.Split(','))
                .Select(p => new LevelRow(
                    int.Parse(p[0], CultureInfo.InvariantCulture),
                    double.Parse(p[1], CultureInfo.InvariantCulture),
                    double.Parse(p[2], CultureInfo.InvariantCulture)))
                .ToList();
            // An empty or truncated fixture would make every "no sound" assertion below pass by
            // hearing nothing - a broken instrument, not a clean run.
            Assert.Equal(2580, rows.Count);
            Assert.Equal(Enumerable.Range(0, 2580), rows.Select(r => r.Second));
            return rows;
        }

        [Fact]
        public void Replay_QuietRoomFixture_OldPeakRule_CountedTheRoomNoiseAsSound()
        {
            // The KNOWN-BAD arm: the fixture really is the data that broke the old rule. Replaying the
            // issue #66 rule (per-second PEAK over a 20th-percentile peak floor + 10 dB, floor clamped
            // at -70) over it marks hundreds of seconds loud - 956 on the committed fixture (the issue
            // measured 1108 of 2580 with a slightly different extraction). Every one of them was sound.
            var rows = QuietRoomFixture();
            var history = new List<double>();
            int loud = 0;
            foreach (var r in rows)
            {
                var window = history.Skip(Math.Max(0, history.Count - 600)).ToList();
                if (window.Count >= 5)
                {
                    var sorted = window.OrderBy(d => d).ToArray();
                    double floor = Math.Max(sorted[(int)Math.Floor(0.20 * (sorted.Length - 1))], -70.0);
                    if (r.PeakDb > floor + 10.0) loud++;
                }
                history.Add(r.PeakDb);
            }

            Assert.True(loud > 500, $"old peak rule marked {loud} of {rows.Count} seconds loud");
        }

        [Fact]
        public void Replay_QuietRoomFixture_Auto_NoSoundAndTheClipClosesAfterTheAfterWindow()
        {
            var rows = QuietRoomFixture();
            var log = new SoundLog(SoundSource.Mic, null);
            Replay(log, rows.Select(r => r.RmsDb).ToList());
            int end = rows.Count;

            // The line the room produced: its RMS floor is near -76 dBFS, so Auto sits at its -50 minimum.
            double floor = log.CurrentFloorDb(SoundSource.Mic)!.Value;
            Assert.InRange(floor, -80.0, -70.0);
            Assert.Equal(SoundLog.AutoMinLineDb, log.CurrentThresholdDb(SoundSource.Mic));

            // NO sound anywhere in the 43 minutes.
            Assert.False(log.AnySound(At(0), At(end)));
            Assert.Null(log.LastSoundUtc);
            var sum = log.Summarize(At(0), At(end));
            Assert.Equal(0, sum.SoundSeconds);
            // The instrument is live: the room did produce loud seconds (2 on the committed fixture:
            // offsets 33 and 39), and it is the sustained rule that refuses them.
            Assert.True(sum.LoudSeconds > 0, "the replay produced no loud second at all - the instrument heard nothing");
            Assert.True(sum.LoudSeconds < SoundLog.SustainMinLoudSeconds * 2,
                $"{sum.LoudSeconds} loud seconds in a quiet room - the line is in the noise");

            // The keeper over the same stretch, one-minute pieces, keep 5 before / 5 after (the owner's
            // settings). A clip is open from talking that ended one second before the stretch.
            var before = TimeSpan.FromMinutes(5);
            var after = TimeSpan.FromMinutes(5);
            DateTime lastSpeech = At(-1);
            Func<DateTime, DateTime, bool> anySound = (a, b) => (a <= lastSpeech && lastSpeech <= b) || log.AnySound(a, b);
            var pieces = Enumerable.Range(0, end / 60)
                .Select(m => new Piece($"piece_{m}.mp4", At(m * 60), At(m * 60 + 60), 1))
                .ToList();
            Assert.Equal(43, pieces.Count);

            var plan = KeeperRule.Decide(pieces, anySound, At(end) + before + TimeSpan.FromSeconds(1),
                before, after, openClip: 1, openClipEndUtc: At(0), nextClip: 2, final: false);

            // Kept: only the "after" tail of the earlier talking - the pieces starting at minutes 0..4.
            Assert.Equal(Enumerable.Range(0, 5).Select(m => At(m * 60)), plan.Keep.Select(k => k.Piece.StartUtc));
            Assert.All(plan.Keep, k => Assert.Equal(1, k.Clip));
            // The clip closes once 5 minutes pass with no sound; nothing else is kept.
            Assert.Equal(new[] { 1 }, plan.Close);
            Assert.Null(plan.OpenClip);
            Assert.Equal(38, plan.Delete.Count);
            Assert.Equal(At(5 * 60), plan.Delete[0].StartUtc);
        }

        // ---- synthetic series (criteria 2 and 3) ------------------------------------------------

        [Fact]
        public void Replay_SpeechLikeSeriesInsideNoise_IsSound()
        {
            // 60 s of room noise at -65, then 20 s of talking at -35 with 1-2 s pauses, then 60 s of noise.
            var series = new List<double>();
            series.AddRange(Enumerable.Repeat(-65.0, 60));
            bool[] talk =
            {
                true, true, true, true, false, true, true, true, false, false,
                true, true, true, true, true, false, true, true, false, true,
            };
            series.AddRange(talk.Select(t => t ? -35.0 : -65.0));
            series.AddRange(Enumerable.Repeat(-65.0, 60));
            var log = new SoundLog(SoundSource.Mic, null);
            Replay(log, series);

            Assert.False(log.AnySound(At(0), At(59)));
            Assert.True(log.AnySound(At(60), At(79)));
            Assert.False(log.AnySound(At(80), At(140)));
            Assert.Equal(At(79), log.LastSoundUtc);
            Assert.Equal(new SoundSummary(talk.Count(t => t), talk.Count(t => t)), log.Summarize(At(60), At(79)));
        }

        [Fact]
        public void Replay_IsolatedSingleSecondSpikesInsideNoise_AreNotSound()
        {
            // 30 minutes of noise at -65 with one second at -20 every 60 s.
            var series = Enumerable.Range(0, 30 * 60).Select(i => i % 60 == 30 ? -20.0 : -65.0).ToList();
            var log = new SoundLog(SoundSource.Mic, null);
            Replay(log, series);

            Assert.False(log.AnySound(At(0), At(series.Count)));
            Assert.Null(log.LastSoundUtc);
            // Every spike was over the line - it is only the sustained rule that keeps them out.
            Assert.Equal(new SoundSummary(30, 0), log.Summarize(At(0), At(series.Count)));
        }

        [Fact]
        public void Sustained_TwoLoudSecondsInTen_IsNotSound_ThreeIs()
        {
            var two = new SoundLog(SoundSource.Mic, -40);
            Second(two, 0, -30);
            Second(two, 9, -30);
            Second(two, 10, -90);
            Assert.False(two.AnySound(At(0), At(10)));

            var spread = new SoundLog(SoundSource.Mic, -40);
            Second(spread, 0, -30);
            Second(spread, 10, -30);   // 0 and 10 are 11 seconds apart - never in one 10-second span
            Second(spread, 19, -30);
            Second(spread, 20, -90);
            Assert.False(spread.AnySound(At(0), At(20)));
            Assert.Equal(new SoundSummary(3, 0), spread.Summarize(At(0), At(20)));

            var three = new SoundLog(SoundSource.Mic, -40);
            Second(three, 0, -30);
            Second(three, 5, -30);
            Second(three, 9, -30);
            Second(three, 10, -90);
            Assert.True(three.AnySound(At(0), At(0)));
            Assert.True(three.AnySound(At(5), At(5)));
            Assert.True(three.AnySound(At(9), At(9)));
            Assert.False(three.AnySound(At(1), At(4)));   // the quiet seconds between are not sound
            Assert.Equal(At(9), three.LastSoundUtc);
        }

        [Fact]
        public void Observe_SecondIsJudgedOnlyOnceItIsOver()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            Second(log, 0, -20);
            Second(log, 1, -20);
            Second(log, 2, -20);
            Assert.False(log.AnySound(At(0), At(2)));   // second 2 is still open
            Second(log, 3, -90);
            Assert.True(log.AnySound(At(0), At(2)));
        }

        // ---- RMS, not peak ----------------------------------------------------------------------

        [Fact]
        public void Observe_FixedLine_UsesRmsNotPeak()
        {
            // A click: a -5 dBFS peak in a second whose RMS is -55. Loud by peak, quiet by RMS.
            var log = new SoundLog(SoundSource.Mic, -40);
            for (int s = 0; s < 5; s++) Second(log, s, -55, peakDb: -5);
            Second(log, 5, -90);

            Assert.Equal(new SoundSummary(0, 0), log.Summarize(At(0), At(5)));
        }

        [Fact]
        public void Observe_ManyBuffersInOneSecond_AreSummedIntoOneRms()
        {
            // Twenty 50 ms buffers: ten at -30 dBFS RMS and ten of digital silence make a second of
            // -33 dBFS RMS (half the energy) - loud against a -40 line.
            var log = new SoundLog(SoundSource.Mic, -40);
            for (int s = 0; s < 3; s++)
                for (int b = 0; b < 20; b++)
                {
                    var t = At(s).AddMilliseconds(b * 50);
                    var level = b % 2 == 0 ? AudioLevel.FromDb(-30, -20, 2400) : new AudioLevel(0f, 0.0, 2400);
                    log.Observe(SoundSource.Mic, t, level);
                }
            Second(log, 3, -90);

            Assert.Equal(new SoundSummary(3, 3), log.Summarize(At(0), At(2)));
        }

        [Fact]
        public void RmsDb_DigitalSilence_ReadsAsTheSilenceLevel()
        {
            Assert.Equal(SoundLog.SilenceDb, SoundLog.RmsDb(0.0));
            Assert.Equal(-20.0, SoundLog.RmsDb(0.01), 6);
            Assert.Equal(-33.0103, SoundLog.RmsDb(new AudioLevel(0f, 0.001 * 2400 / 2, 2400).MeanSquare), 3);
        }

        // ---- the Auto line ----------------------------------------------------------------------

        [Fact]
        public void AutoThreshold_IsFloorPlusFifteen_NeverBelowMinusFifty()
        {
            Assert.Equal(-25.0, SoundLog.AutoThreshold(-40));
            Assert.Equal(-50.0, SoundLog.AutoThreshold(-65));
            Assert.Equal(-50.0, SoundLog.AutoThreshold(-76.7));
            Assert.Equal(-50.0, SoundLog.AutoThreshold(SoundLog.SilenceDb));
        }

        [Fact]
        public void Observe_Auto_NothingCountsUntilTheFloorIsMeasured()
        {
            var log = new SoundLog(SoundSource.Mic, null);
            // Loud first seconds: there is no floor yet, so there is no line to be above.
            for (int s = 0; s < SoundLog.MinSecondsForAuto; s++) Second(log, s, -10);
            Assert.Null(log.CurrentThresholdDb(SoundSource.Mic));
            Assert.Null(log.CurrentFloorDb(SoundSource.Mic));
            Second(log, SoundLog.MinSecondsForAuto, -10);

            Assert.False(log.AnySound(At(0), At(SoundLog.MinSecondsForAuto)));
            Assert.NotNull(log.CurrentThresholdDb(SoundSource.Mic));
        }

        [Fact]
        public void Observe_Auto_LineFollowsTheFloor()
        {
            var log = new SoundLog(SoundSource.Mic, null);
            // A loud room: floor -40, line -25. -30 is above the floor but below the line.
            for (int s = 0; s <= 30; s++) Second(log, s, -40);
            Assert.Equal(-40.0, log.CurrentFloorDb(SoundSource.Mic)!.Value, 6);
            Assert.Equal(-25.0, log.CurrentThresholdDb(SoundSource.Mic)!.Value, 6);

            for (int s = 31; s < 36; s++) Second(log, s, -30);
            for (int s = 36; s < 41; s++) Second(log, s, -15);
            Second(log, 41, -40);
            Assert.False(log.AnySound(At(31), At(35)));
            Assert.True(log.AnySound(At(36), At(40)));
        }

        [Fact]
        public void Observe_Auto_FloorIgnoresTheLoudFewSeconds()
        {
            var log = new SoundLog(SoundSource.Mic, null);
            // 60 seconds of room noise with speech in a third of them: the floor stays the room.
            for (int s = 0; s < 61; s++) Second(log, s, s % 3 == 0 ? -20 : -60);

            Assert.Equal(-60.0, log.CurrentFloorDb(SoundSource.Mic)!.Value, 6);
            Assert.Equal(-45.0, log.CurrentThresholdDb(SoundSource.Mic)!.Value, 6);
        }

        // ---- sources, pruning, reporting --------------------------------------------------------

        [Fact]
        public void Observe_SourceThatDoesNotCount_IsIgnored()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            for (int s = 0; s < 5; s++) Second(log, s, -3, source: SoundSource.System);

            Assert.False(log.AnySound(At(-60), At(60)));
            Assert.False(log.Listens(SoundSource.System));
            Assert.Null(log.CurrentThresholdDb(SoundSource.System));
        }

        [Fact]
        public void Observe_Both_LoudSecondsFromEitherSourceSustainTogether()
        {
            var log = new SoundLog(SoundSource.Both, -40);
            Second(log, 0, -20, source: SoundSource.Mic);
            Second(log, 1, -20, source: SoundSource.System);
            Second(log, 2, -20, source: SoundSource.Mic);
            Second(log, 3, -90, source: SoundSource.Mic);
            Second(log, 3, -90, source: SoundSource.System);

            Assert.True(log.AnySound(At(0), At(0)));
            Assert.True(log.AnySound(At(1), At(1)));
            Assert.True(log.AnySound(At(2), At(2)));
        }

        [Fact]
        public void Prune_ForgetsOldSecondsButNotTheLastSoundTime()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            Replay(log, new[] { -10.0, -10.0, -10.0 });
            Assert.True(log.AnySound(At(0), At(2)));
            log.Prune(At(60));

            Assert.False(log.AnySound(At(0), At(2)));
            Assert.Equal(new SoundSummary(0, 0), log.Summarize(At(0), At(2)));
            Assert.Equal(At(2), log.LastSoundUtc);
        }

        [Fact]
        public void AnySound_EmptyOrBackwardsRange_IsFalse()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            Replay(log, new[] { -10.0, -10.0, -10.0 });

            Assert.False(log.AnySound(At(1), At(0)));
            Assert.Equal(new SoundSummary(0, 0), log.Summarize(At(1), At(0)));
        }

        [Fact]
        public void Describe_ReportsFloorLineLoudSecondsAndSustained()
        {
            var auto = new SoundLog(SoundSource.Mic, null);
            Replay(auto, Enumerable.Repeat(-70.0, 30).Concat(new[] { -20.0, -90.0 }).ToList());
            Assert.Equal("mic floor=-70.0dBFS line=-50.0dBFS (auto); last 32s: loud=1 sustained=no (0s)",
                auto.Describe(At(0), At(31)));

            var both = new SoundLog(SoundSource.Both, -40);
            Assert.Equal("mic floor=none line=-40.0dBFS (fixed); system floor=none line=-40.0dBFS (fixed); last 60s: loud=0 sustained=no (0s)",
                both.Describe(At(0), At(59)));
        }
    }
}
