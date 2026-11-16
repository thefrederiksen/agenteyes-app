using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AgentEyes.Video;
using Xunit;
using Xunit.Abstractions;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #113, pass 5 (the Review Gate's second smaller finding): the fragmented capture must not
    /// move the video against the audio.
    ///
    /// libx264's B-frames make the first video timestamp negative by two frames; plain MP4 cancels that
    /// with an edit list, and a fragmented file whose index is written before any sample cannot carry
    /// one, so the video started late: 0.067 s at 30 fps on this synthetic source (the gate's
    /// measurement), 0.73-1.00 s on a real Windows capture, where gdigrab's second frame arrives about
    /// 0.7 s after its first. <see cref="FfmpegArgs.FragmentedOutput"/> now delays the index to the
    /// first fragment (+delay_moov) so the edit list is written.
    ///
    /// This runs a REAL ffmpeg with the product's own output options and the product's encoder
    /// settings (libx264 veryfast, B-frames on - the default), two short lavfi encodes that end by
    /// themselves. MUTATION: removing +delay_moov from FragmentedOutput makes the start-time test fail
    /// with the video at 0.067 s.
    /// </summary>
    public sealed class FragmentedOutputTimelineTests
    {
        private readonly ITestOutputHelper _out;
        public FragmentedOutputTimelineTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void FragmentedOutput_WithBFrames_StartsVideoAndAudioTogetherAtZero()
        {
            string dir = Directory.CreateTempSubdirectory("agenteyes-fragtimeline-").FullName;
            try
            {
                string path = Encode(dir, "frag.mp4");
                double video = StartTime(path, "v:0");
                double audio = StartTime(path, "a:0");
                _out.WriteLine($"fragmented: video start {video:F6}s, audio start {audio:F6}s");

                // Expected 0 for both (plain MP4 on the same input: 0/0). Bad, without delay_moov: the
                // video at two frames, 0.067 s. One millisecond is far under one frame (33 ms).
                Assert.True(Math.Abs(video) < 0.001, $"the video starts at {video:F6}s, not at zero - "
                    + "the fragmented file has no edit list for the B-frame delay");
                Assert.True(Math.Abs(audio) < 0.001, $"the audio starts at {audio:F6}s, not at zero");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void FragmentedOutput_StillUsesBFrames_SoTheFixIsTheEditListNotAWorseEncode()
        {
            // The other fix that measured clean was -bf 0, which spends B-frames. This pins that the
            // fix lives in the container: through FragmentedOutput the first video packet still has a
            // negative decode time (a B-frame reorder delay) and an edit list cancels it. It fails if
            // FragmentedOutput turns B-frames off, and ALSO without +delay_moov (no edit list, so the
            // muxer shifts the decode time to 0) - so it is not a substitute for the start-time test.
            // The encoder settings here are written out, not the product's: a -bf 0 added to the
            // capture's ENCODER arguments would not show here, only one added to FragmentedOutput.
            string dir = Directory.CreateTempSubdirectory("agenteyes-fragtimeline-").FullName;
            try
            {
                string path = Encode(dir, "frag.mp4");
                string firstDts = Probe(path, "-select_streams", "v:0", "-read_intervals", "%+#1",
                    "-show_entries", "packet=dts_time", "-of", "default=nw=1:nk=1").Trim();
                _out.WriteLine($"first video packet dts {firstDts}");
                double dts = double.Parse(firstDts, CultureInfo.InvariantCulture);
                Assert.True(dts < 0, $"the first video packet decodes at {dts:F6}s - no B-frame reorder delay, so no B-frames");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>Two seconds of 30 fps video and 48 kHz audio through the product's encoder settings
        /// and <see cref="FfmpegArgs.FragmentedOutput"/>, exactly as the capture builders place it.</summary>
        private static string Encode(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            var args = new System.Collections.Generic.List<string>
            {
                "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "2",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
                "-c:a", "aac", "-b:a", "128k",
            };
            args.AddRange(FfmpegArgs.FragmentedOutput());
            args.Add(path);
            Ffmpeg.Run(args, "test: fragmented timeline");
            Assert.True(File.Exists(path), $"ffmpeg wrote no {name}");
            return path;
        }

        private static double StartTime(string path, string stream)
        {
            string s = Probe(path, "-select_streams", stream, "-show_entries", "stream=start_time",
                "-of", "default=nw=1:nk=1").Trim();
            return double.Parse(s, CultureInfo.InvariantCulture);
        }

        private static string Probe(string path, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffprobe(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "ffprobe did not finish in 30 s");
            Assert.True(p.ExitCode == 0, $"ffprobe exit {p.ExitCode}: {stderr}");
            return stdout;
        }
    }
}
