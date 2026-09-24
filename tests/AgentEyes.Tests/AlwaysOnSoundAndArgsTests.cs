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
    /// <summary>The always-on ffmpeg command and the cap (issue #66). The sound log is in AlwaysOnSoundRuleTests.</summary>
    public class AlwaysOnSoundAndArgsTests
    {
        // The sound log itself is proven in AlwaysOnSoundRuleTests (issue #72).

        // ---- the capture command ------------------------------------------------

        private static readonly Drawing.Rectangle Screen = new(0, 0, 1920, 1080);

        private static List<string> Capture(string? mic, bool system, string encoder = "h264_qsv",
            Drawing.Rectangle? region = null, int piece = 60) =>
            AlwaysOnArgs.Capture(region ?? Screen, Screen, 10, encoder, mic, 1.0,
                system ? @"\\.\pipe\x" : null, system ? new PipeAudioFormat(48000, 2) : null, 0.7,
                piece, @"C:\work\pieces");

        private static string Joined(List<string> a) => string.Join(" ", a);

        [Fact]
        public void Capture_ForcesAKeyframeEveryTwoSeconds_AndCutsSixtySecondPieces()
        {
            // Issue #79: a keyframe every 2 s (it was one per 60 s piece), so a clip can start 10 s
            // before the speech with a lossless cut; the pieces stay 60 s, a whole number of keyframes.
            var a = Capture("Mic", false);

            int i = a.IndexOf("-force_key_frames");
            Assert.True(i >= 0);
            Assert.Equal("expr:gte(t,n_forced*2)", a[i + 1]);
            Assert.Equal(2, AlwaysOnArgs.DefaultKeyframeSeconds);
            Assert.Equal(1, a.Count(x => x == "-force_key_frames"));
            Assert.Equal("60", a[a.IndexOf("-segment_time") + 1]);
            Assert.Equal("1", a[a.IndexOf("-reset_timestamps") + 1]);
            Assert.Equal("1", a[a.IndexOf("-strftime") + 1]);
            Assert.EndsWith(AlwaysOnArgs.PiecePattern, a[^1]);
        }

        [Fact]
        public void Capture_PieceLengthFollowsTheOption()
        {
            var a = Capture("Mic", false, piece: 10);

            Assert.Equal("expr:gte(t,n_forced*2)", a[a.IndexOf("-force_key_frames") + 1]);
            Assert.Equal("10", a[a.IndexOf("-segment_time") + 1]);
        }

        [Fact]
        public void Capture_KeyframeIntervalFollowsTheOption()
        {
            var a = AlwaysOnArgs.Capture(Screen, Screen, 10, "h264_qsv", "Mic", 1.0, null, null, 0.7, 60, @"C:\work\pieces",
                keyframeSeconds: 5);

            Assert.Equal("expr:gte(t,n_forced*5)", a[a.IndexOf("-force_key_frames") + 1]);
        }

        [Fact]
        public void Capture_PieceNotAWholeNumberOfKeyframes_Throws()
        {
            // A 61 s piece cannot be cut on a 2 s keyframe grid: the pieces would drift off their minute.
            Assert.Throws<ArgumentException>(() => Capture("Mic", false, piece: 61));
            Assert.Throws<ArgumentOutOfRangeException>(() => AlwaysOnArgs.Capture(Screen, Screen, 10, "libx264", null, 1,
                null, null, 1, 60, @"C:\w", keyframeSeconds: 0));
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
        public void Join_NeverNamesAnEncoder()
        {
            var a = AlwaysOnArgs.Join("list.txt", "out.mp4");

            Assert.Equal(new[] { "-y", "-f", "concat", "-safe", "0", "-i", "list.txt", "-c", "copy", "-movflags", "+faststart", "out.mp4" }, a);
            Assert.DoesNotContain(a, x => x.StartsWith("-c:") || x.StartsWith("-crf") || x.Contains("264") || x == "-vf" || x == "-af");
        }

        // ---- the trim (issue #79) ------------------------------------------------

        [Fact]
        public void Trim_StartAndEnd_IsAStreamCopyFromTheKeyframeForTheKeptLength()
        {
            var a = AlwaysOnArgs.Trim(@"C:\h\piece_1.mp4", @"C:\h\cut_piece_1.mp4", 54, 58.5);

            Assert.Equal(new[]
            {
                "-y", "-ss", "54", "-i", @"C:\h\piece_1.mp4", "-t", "4.5",
                "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", @"C:\h\cut_piece_1.mp4",
            }, a);
        }

        [Fact]
        public void Trim_StartOnly_SeeksOnTheInputAndKeepsToTheEnd()
        {
            var a = AlwaysOnArgs.Trim("in.mp4", "out.mp4", 54, null);

            Assert.Equal(new[] { "-y", "-ss", "54", "-i", "in.mp4", "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "out.mp4" }, a);
            // Input seeking: -ss comes before -i, so a stream copy starts on the keyframe at or before it.
            Assert.True(a.IndexOf("-ss") < a.IndexOf("-i"));
        }

        [Fact]
        public void Trim_EndOnly_KeepsFromTheStartForTheGivenLength()
        {
            var a = AlwaysOnArgs.Trim("in.mp4", "out.mp4", null, 10);

            Assert.Equal(new[] { "-y", "-i", "in.mp4", "-t", "10", "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "out.mp4" }, a);
        }

        [Fact]
        public void Trim_NeverReencodes()
        {
            foreach (var a in new[] { AlwaysOnArgs.Trim("i", "o", 2, null), AlwaysOnArgs.Trim("i", "o", null, 3), AlwaysOnArgs.Trim("i", "o", 2, 30) })
            {
                Assert.Equal("copy", a[a.IndexOf("-c") + 1]);
                Assert.DoesNotContain(a, x => x.StartsWith("-c:") || x.StartsWith("-crf") || x.Contains("264") || x == "-vf" || x == "-af"
                                              || x == "-filter_complex" || x.StartsWith("-b:"));
            }
        }

        [Fact]
        public void Trim_BadCut_Throws()
        {
            Assert.Throws<ArgumentException>(() => AlwaysOnArgs.Trim("i", "o", null, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => AlwaysOnArgs.Trim("i", "o", -2, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => AlwaysOnArgs.Trim("i", "o", 10, 10));
            Assert.Throws<ArgumentException>(() => AlwaysOnArgs.Trim("", "o", 2, null));
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

        [Fact]
        public void PipeFeeder_Complete_SlowReader_EveryQueuedByteArrivesBeforeItReturns()
        {
            // The production stop order: Complete while the pipe is still open, then close it.
            var sink = new SlowStream(delayMs: 5);
            var feeder = new PipeFeeder(sink, maxQueuedBytes: 1 << 20);
            for (int i = 0; i < 100; i++) feeder.Write(new byte[1000], 0, 1000);

            bool drained = feeder.Complete(TimeSpan.FromSeconds(10));
            long got = sink.Written;
            sink.Dispose();
            feeder.Dispose();

            Assert.True(drained);
            Assert.Equal(100_000, got);
        }

        [Fact]
        public void PipeFeeder_Complete_StuckReader_ReturnsFalseWithinTheTimeout()
        {
            using var stuck = new StuckStream();
            using var feeder = new PipeFeeder(stuck, maxQueuedBytes: 1 << 20);
            feeder.Write(new byte[10], 0, 10);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(feeder.Complete(TimeSpan.FromMilliseconds(300)));
            Assert.True(sw.ElapsedMilliseconds < 3000);
            stuck.Release();
        }

        /// <summary>A pipe whose reader takes a while over every write.</summary>
        private sealed class SlowStream : System.IO.Stream
        {
            private readonly int _delayMs;
            private long _written;
            public SlowStream(int delayMs) => _delayMs = delayMs;
            public long Written => System.Threading.Interlocked.Read(ref _written);
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_disposed) throw new System.ObjectDisposedException(nameof(SlowStream));
                System.Threading.Thread.Sleep(_delayMs);
                System.Threading.Interlocked.Add(ref _written, count);
            }
            private volatile bool _disposed;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, System.IO.SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
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
