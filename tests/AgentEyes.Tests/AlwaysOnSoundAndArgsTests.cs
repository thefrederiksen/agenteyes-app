using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes.AlwaysOn;
using AgentEyes.Audio;
using AgentEyes.Housekeeping;
using Xunit;
using Drawing = System.Drawing;

namespace AgentEyes.Tests
{
    /// <summary>The always-on sound log, the ffmpeg command it runs, and the cap (issue #66).</summary>
    public class AlwaysOnSoundAndArgsTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>A linear peak for a dBFS value.</summary>
        private static float Peak(double db) => (float)Math.Pow(10, db / 20.0);

        // ---- the sound log ------------------------------------------------------

        [Fact]
        public void Observe_FixedThreshold_LogsOnlyLouderSeconds()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            log.Observe(SoundSource.Mic, T0, Peak(-50));
            log.Observe(SoundSource.Mic, T0.AddSeconds(10), Peak(-30));

            Assert.False(log.AnySound(T0, T0.AddSeconds(9)));
            Assert.True(log.AnySound(T0.AddSeconds(10), T0.AddSeconds(10)));
            Assert.Equal(T0.AddSeconds(10), log.LastSoundUtc);
        }

        [Fact]
        public void Observe_SourceThatDoesNotCount_IsIgnored()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            log.Observe(SoundSource.System, T0, Peak(-3));

            Assert.False(log.AnySound(T0.AddMinutes(-1), T0.AddMinutes(1)));
            Assert.False(log.Listens(SoundSource.System));
        }

        [Fact]
        public void Observe_Both_CountsEitherSource()
        {
            var log = new SoundLog(SoundSource.Both, -40);
            log.Observe(SoundSource.System, T0, Peak(-20));
            log.Observe(SoundSource.Mic, T0.AddSeconds(5), Peak(-20));

            Assert.True(log.AnySound(T0, T0));
            Assert.True(log.AnySound(T0.AddSeconds(5), T0.AddSeconds(5)));
        }

        [Fact]
        public void Observe_Auto_NothingCountsUntilTheFloorIsMeasured()
        {
            var log = new SoundLog(SoundSource.Mic, null);
            // A loud first second: there is no floor yet, so there is no line to be above.
            log.Observe(SoundSource.Mic, T0, Peak(-10));

            Assert.False(log.AnySound(T0, T0));
            Assert.Null(log.CurrentThresholdDb(SoundSource.Mic));
        }

        [Fact]
        public void Observe_Auto_LineIsFloorPlusTheGateMargin()
        {
            var log = new SoundLog(SoundSource.Mic, null);
            for (int s = 0; s < 30; s++) log.Observe(SoundSource.Mic, T0.AddSeconds(s), Peak(-60));
            // One more second so the last -60 second is closed into the history.
            log.Observe(SoundSource.Mic, T0.AddSeconds(30), Peak(-60));

            double? line = log.CurrentThresholdDb(SoundSource.Mic);
            Assert.NotNull(line);
            Assert.Equal(-60 + GateCalibration.FloorMarginDb, line!.Value, 1);

            log.Observe(SoundSource.Mic, T0.AddSeconds(31), Peak(-55));   // above the floor, below the line
            log.Observe(SoundSource.Mic, T0.AddSeconds(32), Peak(-30));   // speech
            Assert.False(log.AnySound(T0.AddSeconds(31), T0.AddSeconds(31)));
            Assert.True(log.AnySound(T0.AddSeconds(32), T0.AddSeconds(32)));
        }

        [Fact]
        public void Observe_Auto_DigitalSilenceFloorIsClamped()
        {
            Assert.Equal(SoundLog.FloorClampDb + GateCalibration.FloorMarginDb, SoundLog.AutoThreshold(double.NegativeInfinity));
            Assert.Equal(-40 + GateCalibration.FloorMarginDb, SoundLog.AutoThreshold(-40));
        }

        [Fact]
        public void Observe_Auto_FloorIgnoresTheLoudFewSeconds()
        {
            var log = new SoundLog(SoundSource.Mic, null);
            // 60 seconds of room noise with speech in a third of them: the floor stays the room.
            for (int s = 0; s < 61; s++)
                log.Observe(SoundSource.Mic, T0.AddSeconds(s), Peak(s % 3 == 0 ? -20 : -65));

            Assert.Equal(-65 + GateCalibration.FloorMarginDb, log.CurrentThresholdDb(SoundSource.Mic)!.Value, 1);
        }

        [Fact]
        public void Prune_ForgetsOldSecondsButNotTheLastSoundTime()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            log.Observe(SoundSource.Mic, T0, Peak(-10));
            log.Prune(T0.AddMinutes(1));

            Assert.False(log.AnySound(T0, T0));
            Assert.Equal(T0, log.LastSoundUtc);
        }

        [Fact]
        public void AnySound_EmptyOrBackwardsRange_IsFalse()
        {
            var log = new SoundLog(SoundSource.Mic, -40);
            log.Observe(SoundSource.Mic, T0, Peak(-10));

            Assert.False(log.AnySound(T0.AddSeconds(1), T0));
        }

        // ---- the capture command ------------------------------------------------

        private static readonly Drawing.Rectangle Screen = new(0, 0, 1920, 1080);

        private static List<string> Capture(string? mic, bool system, string encoder = "h264_qsv",
            Drawing.Rectangle? region = null, int piece = 60) =>
            AlwaysOnArgs.Capture(region ?? Screen, Screen, 10, encoder, mic, 1.0,
                system ? @"\\.\pipe\x" : null, system ? new PipeAudioFormat(48000, 2) : null, 0.7,
                piece, @"C:\work\pieces");

        private static string Joined(List<string> a) => string.Join(" ", a);

        [Fact]
        public void Capture_ForcesAKeyframeAtEveryPieceBoundary()
        {
            var a = Capture("Mic", false);

            int i = a.IndexOf("-force_key_frames");
            Assert.True(i >= 0);
            Assert.Equal("expr:gte(t,n_forced*60)", a[i + 1]);
            Assert.Equal("60", a[a.IndexOf("-segment_time") + 1]);
            Assert.Equal("1", a[a.IndexOf("-reset_timestamps") + 1]);
            Assert.Equal("1", a[a.IndexOf("-strftime") + 1]);
            Assert.EndsWith(AlwaysOnArgs.PiecePattern, a[^1]);
        }

        [Fact]
        public void Capture_PieceLengthFollowsTheOption()
        {
            var a = Capture("Mic", false, piece: 10);

            Assert.Equal("expr:gte(t,n_forced*10)", a[a.IndexOf("-force_key_frames") + 1]);
            Assert.Equal("10", a[a.IndexOf("-segment_time") + 1]);
        }

        [Fact]
        public void Capture_MicOnly_MapsTheMicThroughItsGain()
        {
            var a = Capture("Yeti", false);

            Assert.Contains("audio=Yeti", a);
            Assert.Contains("1:a", a);
            Assert.Equal("volume=1", a[a.IndexOf("-af") + 1]);
            Assert.DoesNotContain("-filter_complex", a);
            Assert.DoesNotContain("f32le", a);
        }

        [Fact]
        public void Capture_SystemOnly_ReadsThePipeAsFloatPcm()
        {
            var a = Capture(null, true);

            Assert.DoesNotContain("dshow", a);
            int f = a.IndexOf("f32le");
            Assert.True(f > 0);
            Assert.Equal("48000", a[a.IndexOf("-ar") + 1]);
            Assert.Equal("2", a[a.IndexOf("-ac") + 1]);
            Assert.Contains(@"\\.\pipe\x", a);
            Assert.Equal("volume=0.7", a[a.IndexOf("-af") + 1]);
        }

        [Fact]
        public void Capture_Mixed_MixesBothWithoutHalvingThem()
        {
            var a = Capture("Yeti", true);

            string fc = a[a.IndexOf("-filter_complex") + 1];
            Assert.Contains("[1:a]volume=1[m]", fc);
            Assert.Contains("[2:a]volume=0.7[s]", fc);
            Assert.Contains("normalize=0", fc);
            Assert.Equal("[aout]", a[a.LastIndexOf("-map") + 1]);
        }

        [Fact]
        public void Capture_NoAudio_HasNoAudioCodec()
        {
            var a = Capture(null, false);

            Assert.DoesNotContain("-c:a", a);
        }

        [Fact]
        public void Capture_Encoder_SetsItsPixelFormat()
        {
            Assert.Equal("format=nv12", Capture(null, false, "h264_qsv")[Capture(null, false, "h264_qsv").IndexOf("-vf") + 1]);
            var x = Capture(null, false, "libx264");
            Assert.Equal("format=yuv420p", x[x.IndexOf("-vf") + 1]);
            Assert.Contains("-crf", x);
        }

        [Fact]
        public void Capture_OversizedRegion_IsPaddedBackToItsSize()
        {
            var a = Capture(null, false, region: new Drawing.Rectangle(1000, 0, 1080, 1920));

            Assert.StartsWith("pad=1080:1920", a[a.IndexOf("-vf") + 1]);
            Assert.Equal("920x1080", a[a.IndexOf("-video_size") + 1]);
        }

        [Fact]
        public void Capture_SystemPipeWithoutFormat_Throws()
        {
            Assert.Throws<ArgumentException>(() => AlwaysOnArgs.Capture(Screen, Screen, 10, "libx264", null, 1,
                @"\\.\pipe\x", null, 1, 60, @"C:\w"));
        }

        [Fact]
        public void EncoderArgs_Unknown_Throws()
        {
            Assert.Throws<ArgumentException>(() => AlwaysOnArgs.EncoderArgs("h265_magic"));
        }

        [Fact]
        public void EncoderPreference_EndsWithTheSoftwareEncoderEveryBuildHas()
        {
            Assert.Equal("libx264", AlwaysOnArgs.EncoderPreference[^1]);
            Assert.All(AlwaysOnArgs.EncoderPreference, e => Assert.NotEmpty(AlwaysOnArgs.EncoderArgs(e)));
        }

        [Fact]
        public void Join_CopiesStreamsWithoutReencoding()
        {
            var a = AlwaysOnArgs.Join("list.txt", "out.mp4");

            Assert.Equal("copy", a[a.IndexOf("-c") + 1]);
            Assert.Equal("concat", a[a.IndexOf("-f") + 1]);
            Assert.Equal("out.mp4", a[^1]);
        }

        [Fact]
        public void JoinList_QuotesPathsForTheConcatDemuxer()
        {
            string list = AlwaysOnArgs.JoinList(new[] { @"C:\a\piece_1.mp4", @"C:\it's\piece_2.mp4" });

            Assert.Equal("file 'C:/a/piece_1.mp4'\nfile 'C:/it'\\''s/piece_2.mp4'\n", list);
        }

        [Fact]
        public void PieceStartUtc_ParsesTheNameFfmpegWroteAsUtc()
        {
            var t = AlwaysOnArgs.PieceStartUtc(@"C:\x\piece_20260923-080613.mp4");
            Assert.Equal(new DateTime(2026, 9, 23, 8, 6, 13, DateTimeKind.Utc), t);
            Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
            Assert.Null(AlwaysOnArgs.PieceStartUtc("recording.mp4"));
            Assert.Null(AlwaysOnArgs.PieceStartUtc("piece_garbage.mp4"));
        }

        [Fact]
        public void PieceNames_InUtc_NeverRepeatAcrossTheAutumnClockChange()
        {
            // 2026-11-01 in Toronto: 01:30 local happens twice, at 05:30Z and 06:30Z. The names are
            // written in UTC, so the two pieces cannot share a name (review finding 2).
            var first = new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc);
            var second = first.AddHours(1);
            string a = "piece_" + first.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4";
            string b = "piece_" + second.ToString(AlwaysOnArgs.PieceStampFormat) + ".mp4";

            Assert.NotEqual(a, b);
            Assert.Equal(first, AlwaysOnArgs.PieceStartUtc(a));
            Assert.Equal(second, AlwaysOnArgs.PieceStartUtc(b));
        }

        // ---- the pipe feeder (review finding 4) ------------------------------------

        [Fact]
        public void PipeFeeder_ReaderThatStopsReading_NeverBlocksTheWriterAndIsFlaggedStalled()
        {
            using var stuck = new StuckStream();
            using var feeder = new PipeFeeder(stuck, maxQueuedBytes: 64 * 1024);
            var chunk = new byte[4096];

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) feeder.Write(chunk, 0, chunk.Length);   // 800 KB into a 64 KB queue
            sw.Stop();

            Assert.True(feeder.Stalled);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"the audio callback was held {sw.ElapsedMilliseconds}ms");
            stuck.Release();
        }

        [Fact]
        public void PipeFeeder_HealthyReader_GetsEveryByteInOrder()
        {
            var sink = new System.IO.MemoryStream();
            using (var feeder = new PipeFeeder(sink, maxQueuedBytes: 1 << 20))
            {
                for (byte i = 0; i < 100; i++) feeder.Write(new[] { i, i }, 0, 2);
            }
            Assert.False(false);
            var got = sink.ToArray();
            Assert.Equal(200, got.Length);
            Assert.Equal(99, got[^1]);
        }

        /// <summary>A pipe whose reader is alive and never reads: every write blocks.</summary>
        private sealed class StuckStream : System.IO.Stream
        {
            private readonly System.Threading.ManualResetEventSlim _gate = new(false);
            public void Release() => _gate.Set();
            public override void Write(byte[] buffer, int offset, int count) => _gate.Wait();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, System.IO.SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            protected override void Dispose(bool disposing) { _gate.Set(); base.Dispose(disposing); }
        }

        // ---- the cap --------------------------------------------------------------

        private static HousekeepingCandidate C(string name, long bytes, bool pinned = false) =>
            new() { Recording = name, Bytes = bytes, Pinned = pinned };

        [Fact]
        public void EvictOldest_UnderTheCap_TakesNothing()
        {
            Assert.Empty(HousekeepingCeiling.EvictOldest(new[] { C("a", 10), C("b", 10) }, 100, 0));
        }

        [Fact]
        public void EvictOldest_OverTheCap_TakesOldestFirstUntilUnder()
        {
            var evict = HousekeepingCeiling.EvictOldest(new[] { C("a", 40), C("b", 40), C("c", 40) }, 90, 0);

            Assert.Equal(new[] { "a" }, evict.Select(c => c.Recording));
        }

        [Fact]
        public void EvictOldest_CountsFixedBytesButNeverTakesPinned()
        {
            var evict = HousekeepingCeiling.EvictOldest(new[] { C("a", 40, pinned: true), C("b", 40), C("c", 40) }, 90, 50);

            Assert.Equal(new[] { "b", "c" }, evict.Select(c => c.Recording));
        }

        [Fact]
        public void EvictOldest_NoCap_TakesNothing()
        {
            Assert.Empty(HousekeepingCeiling.EvictOldest(new[] { C("a", 1000) }, 0, 0));
        }

        // ---- the CLI's parsing ------------------------------------------------------

        [Fact]
        public void ParseThreshold_AutoOrNegativeDb()
        {
            Assert.Null(AlwaysOnCli.ParseThreshold("auto"));
            Assert.Null(AlwaysOnCli.ParseThreshold(null));
            Assert.Equal(-42.5, AlwaysOnCli.ParseThreshold("-42.5"));
            Assert.Throws<UsageException>(() => AlwaysOnCli.ParseThreshold("12"));
        }

        [Fact]
        public void ParseCounts_KnownNamesOnly()
        {
            Assert.Equal(SoundSource.Both, AlwaysOnCli.ParseCounts("both"));
            Assert.Equal(SoundSource.Mic, AlwaysOnCli.ParseCounts("Microphone"));
            Assert.Throws<UsageException>(() => AlwaysOnCli.ParseCounts("speakers"));
        }
    }
}
