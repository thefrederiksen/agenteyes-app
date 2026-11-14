using System.Collections.Generic;
using System.Linq;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    /// <summary>
    /// ISSUE #114. On macOS the microphone is captured by its own ffmpeg process, because a second
    /// AVFoundation input beside a display loses about 88% of its samples. These are the pure parts
    /// of that fix: the capture-clock reading that aligns the two files, the mux argument list that
    /// applies it, and the guarantee that moving the macOS microphone did not move the Windows or
    /// Linux one.
    /// </summary>
    public sealed class MacMicAlignmentTests
    {
        private static AgentEyes.AudioMixOptions Opts() => new()
        {
            MicGain = 1.0,
            SystemGain = 0.7,
            NoiseGate = true,
            GateThresholdLinear = 0.02,
            GateCalibrated = true,
            RnnoiseModelPath = "/Users/u/Library/Application Support/AgentEyes/models/bd.rnnn",
        };

        private static string Join(IEnumerable<string> args) => string.Join(" ", args);

        // ---- the capture clock reading -------------------------------------

        [Fact]
        public void ParseInputStartSeconds_TheLineFfmpegActuallyPrints_ForTheSCREEN()
        {
            // Copied from a real raw.mp4.ffmpeg.log on devthrottle-mac-mini.
            double? s = FfmpegRecorder.ParseInputStartSeconds(
                "  Duration: N/A, start: 152597.730833, bitrate: N/A");

            Assert.Equal(152597.730833, s!.Value, 6);
        }

        [Fact]
        public void ParseInputStartSeconds_TheLineFfmpegActuallyPrints_ForTheMICROPHONE()
        {
            double? s = FfmpegRecorder.ParseInputStartSeconds(
                "  Duration: N/A, start: 150421.190500, bitrate: 1024 kb/s");

            Assert.Equal(150421.190500, s!.Value, 6);
        }

        /// <summary>
        /// The two readings in one real recording's log, subtracted. The screen and the microphone
        /// were opened by the SAME ffmpeg there, which is why the gap is small; the point is the
        /// arithmetic and the sign, which is what the mux applies.
        /// </summary>
        [Fact]
        public void ParseInputStartSeconds_TwoRealReadings_SubtractToTheOffset()
        {
            double video = FfmpegRecorder.ParseInputStartSeconds(
                "  Duration: N/A, start: 150420.982000, bitrate: N/A")!.Value;
            double mic = FfmpegRecorder.ParseInputStartSeconds(
                "  Duration: N/A, start: 150421.190500, bitrate: 1024 kb/s")!.Value;

            Assert.Equal(0.2085, mic - video, 4);
        }

        /// <summary>
        /// The PER-STREAM line carries "start 152597.730833" with no colon - a different field, on a
        /// line that also appears in every log. Reading it would be reading the wrong number.
        /// </summary>
        [Fact]
        public void ParseInputStartSeconds_IgnoresThePerStreamStartField()
        {
            Assert.Null(FfmpegRecorder.ParseInputStartSeconds(
                "  Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12, 1920x1080, 1000k tbr, "
                + "1000k tbn, start 152597.730833"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("frame=  458 fps= 13 q=-1.0 Lsize=   13176KiB time=00:00:35.10")]
        [InlineData("  Duration: N/A, bitrate: N/A")]
        [InlineData("  Duration: N/A, start: , bitrate: N/A")]
        [InlineData("[AVFoundation indev] Configuration of video device failed, falling back to default.")]
        public void ParseInputStartSeconds_LinesThatAreNotIt_AreNull(string line)
        {
            Assert.Null(FfmpegRecorder.ParseInputStartSeconds(line));
        }

        [Fact]
        public void ParseInputStartSeconds_NullInput_IsNull()
        {
            Assert.Null(FfmpegRecorder.ParseInputStartSeconds(null!));
        }

        // ---- the mux that applies it ---------------------------------------

        [Fact]
        public void MuxVideoAddMic_NoOffset_IsTwoInputsVideoCopiedMicFiltered()
        {
            var args = FfmpegArgs.MuxVideoAddMic("raw.mp4", "mic_native.wav", "recording.mp4", Opts(), 0.0);

            Assert.Equal(new[]
            {
                "-y", "-i", "raw.mp4", "-i", "mic_native.wav",
                "-filter_complex",
                "[1:a]arnndn=m='/Users/u/Library/Application Support/AgentEyes/models/bd.rnnn',"
                + "agate=threshold=0.02:ratio=2:attack=20:release=250,speechnorm=e=4:p=0.95,"
                + "volume=1[m];[m]alimiter=limit=0.95:level=false[a]",
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k",
                "recording.mp4",
            }, args);
        }

        /// <summary>
        /// The mic is input 1 here, not the audio track inside input 0. Reading [0:a] would filter the
        /// VIDEO file's audio - which on macOS does not exist at all, so ffmpeg would fail the mux.
        /// </summary>
        [Fact]
        public void MuxVideoAddMic_ReadsTheMicFromInputOne()
        {
            string s = Join(FfmpegArgs.MuxVideoAddMic("raw.mp4", "mic.wav", "out.mp4", Opts(), 0.0));

            Assert.Contains("[1:a]", s);
            Assert.DoesNotContain("[0:a]", s);
        }

        [Fact]
        public void MuxVideoAddMic_MicStartedLate_PadsItsHeadWithThatMuchSilence()
        {
            // +1.25 s: the mic's first sample is 1.25 s after the first video frame, so its samples
            // belong 1.25 s into the timeline. Letting them start at zero would run the speech early.
            string s = Join(FfmpegArgs.MuxVideoAddMic("raw.mp4", "mic.wav", "out.mp4", Opts(), 1.25));

            Assert.Contains("adelay=1250:all=1", s);
            Assert.DoesNotContain("-ss", s);
        }

        [Fact]
        public void MuxVideoAddMic_MicStartedEarly_SeeksThatHeadOffTheInput()
        {
            // -0.75 s: the WAV holds 0.75 s of audio from before the picture existed.
            var args = FfmpegArgs.MuxVideoAddMic("raw.mp4", "mic.wav", "out.mp4", Opts(), -0.75);

            int ss = args.IndexOf("-ss");
            Assert.True(ss >= 0);
            Assert.Equal("0.75", args[ss + 1]);
            // An INPUT seek: it belongs before the -i it applies to, and before nothing else.
            Assert.Equal("-i", args[ss + 2]);
            Assert.Equal("mic.wav", args[ss + 3]);
            Assert.DoesNotContain("adelay", Join(args));
        }

        [Fact]
        public void MuxVideoAddMic_AlwaysCopiesTheVideoAndEndsAtTheOutput()
        {
            foreach (double offset in new[] { -2.0, -0.001, 0.0, 0.001, 2.0 })
            {
                var args = FfmpegArgs.MuxVideoAddMic("raw.mp4", "mic.wav", "out.mp4", Opts(), offset);

                Assert.Equal("out.mp4", args[^1]);
                int v = args.IndexOf("-c:v");
                Assert.Equal("copy", args[v + 1]);
                Assert.Contains("alimiter=limit=0.95:level=false", Join(args));
                Assert.Equal(2, args.Count(a => a == "-i"));
            }
        }

        /// <summary>A sub-millisecond offset is not worth an adelay stage, and adelay cannot express
        /// it: the filter takes whole milliseconds. Rounding keeps the number ffmpeg will accept.</summary>
        [Fact]
        public void MuxVideoAddMic_SmallPositiveOffset_RoundsToMillisecondsFfmpegAccepts()
        {
            string s = Join(FfmpegArgs.MuxVideoAddMic("raw.mp4", "mic.wav", "out.mp4", Opts(), 0.2085));

            Assert.Contains("adelay=208.5:all=1", s);
        }

        // ---- mixed: mic captured separately, system from its own WAV --------

        /// <summary>
        /// macOS mixed audio. The microphone is input 1 and the system WAV input 2, because on macOS
        /// the video holds no audio track at all - the two-input MuxVideoAddMixed would look for a
        /// [0:a] that does not exist and fail the mux. NOT exercised end to end on this machine (it
        /// has no virtual audio device, so a mixed recording cannot start), which is why the argument
        /// list is pinned here instead.
        /// </summary>
        [Fact]
        public void MuxVideoAddMicAndSystem_MicIsInputOne_SystemIsInputTwo()
        {
            var args = FfmpegArgs.MuxVideoAddMicAndSystem(
                "raw.mp4", "mic_native.wav", "sys_native.wav", "recording.mp4", Opts(), 0.0);

            Assert.Equal(new[]
            {
                "-y", "-i", "raw.mp4", "-i", "mic_native.wav", "-i", "sys_native.wav",
                "-filter_complex",
                "[1:a]arnndn=m='/Users/u/Library/Application Support/AgentEyes/models/bd.rnnn',"
                + "agate=threshold=0.02:ratio=2:attack=20:release=250,speechnorm=e=4:p=0.95,"
                + "volume=1[m];[2:a]volume=0.7[s];"
                + "[m]anull[md];[md][s]amix=inputs=2:duration=longest:normalize=0,"
                + "alimiter=limit=0.95:level=false[a]",
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k",
                "recording.mp4",
            }, args);
        }

        [Fact]
        public void MuxVideoAddMicAndSystem_AlignsTheMicTheSameWayTheMicOnlyMuxDoes()
        {
            string late = Join(FfmpegArgs.MuxVideoAddMicAndSystem(
                "raw.mp4", "mic.wav", "sys.wav", "out.mp4", Opts(), 1.25));
            Assert.Contains("adelay=1250:all=1", late);

            var early = FfmpegArgs.MuxVideoAddMicAndSystem(
                "raw.mp4", "mic.wav", "sys.wav", "out.mp4", Opts(), -0.75);
            int ss = early.IndexOf("-ss");
            Assert.True(ss >= 0);
            Assert.Equal("0.75", early[ss + 1]);
            // The seek applies to the MIC, not to the system WAV.
            Assert.Equal("-i", early[ss + 2]);
            Assert.Equal("mic.wav", early[ss + 3]);
            Assert.DoesNotContain("adelay", Join(early));
        }

        // ---- criterion 8: Windows and Linux are untouched -------------------

        /// <summary>
        /// MicChain gained a source-label parameter so the macOS mux could read [1:a]. Its DEFAULT is
        /// [0:a], and these two shared builders are every other caller of it - so this pins that the
        /// lists Windows and Linux emit are the lists they emitted before issue #114. (Their own
        /// suites pin them too: tests/AgentEyes.Tests/FfmpegArgsTests.cs and
        /// tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs. This one runs on a Mac, which is the
        /// machine the change was made on.)
        /// </summary>
        [Fact]
        public void FilterVideoMic_StillReadsTheMicFromInsideInputZero()
        {
            var args = FfmpegArgs.FilterVideoMic("raw.mp4", "final.mp4", Opts());

            Assert.Equal(new[]
            {
                "-y", "-i", "raw.mp4",
                "-filter_complex",
                "[0:a]arnndn=m='/Users/u/Library/Application Support/AgentEyes/models/bd.rnnn',"
                + "agate=threshold=0.02:ratio=2:attack=20:release=250,speechnorm=e=4:p=0.95,"
                + "volume=1[m];[m]alimiter=limit=0.95:level=false[a]",
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k",
                "final.mp4",
            }, args);
        }

        [Fact]
        public void MuxVideoAddSystem_IsUnchanged()
        {
            var args = FfmpegArgs.MuxVideoAddSystem("raw.mp4", "sys_native.wav", "final.mp4", 0.7);

            Assert.Equal(new[]
            {
                "-y", "-i", "raw.mp4", "-i", "sys_native.wav",
                "-filter_complex", "[1:a]volume=0.7[a]",
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k",
                "final.mp4",
            }, args);
        }

        // ---- asking the screen recorder for a mic is refused, not ignored ----

        /// <summary>
        /// A caller that asks the macOS screen recorder to capture a microphone gets an error, not a
        /// silent file. This is the fail-closed half of the fix: ignoring the request is what would
        /// hand back a recording its caller believes has sound.
        ///
        /// It cannot be asserted through FfmpegRecorder.Start without launching ffmpeg, so what is
        /// pinned here is the guarantee underneath it - the builder has no way to express a
        /// microphone at all - plus the words of the message, which is the only thing a developer
        /// who hits this will have to go on.
        /// </summary>
        [Fact]
        public void TheScreenCaptureCannotExpressAMicrophoneAtAll()
        {
            var method = typeof(FfmpegArgs).GetMethod(
                nameof(FfmpegArgs.AvfVideoCapture),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            // Its only string is the output path, and there is no optional device index - the
            // int? parameter that used to carry the microphone's AVFoundation index.
            var ps = method!.GetParameters();
            Assert.Equal(new[] { "outPath" }, ps.Where(x => x.ParameterType == typeof(string))
                                                .Select(x => x.Name!).ToArray());
            Assert.DoesNotContain(typeof(int?), ps.Select(x => x.ParameterType).ToArray());

            // And FfmpegArgs.AvfAudioInput, the builder that used to append one, is gone.
            Assert.Null(typeof(FfmpegArgs).GetMethod(
                "AvfAudioInput",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic));
        }

        // ---- the topology constant the shared pipeline reads -----------------

        /// <summary>
        /// This is what makes RecordingService start a mic WRITER rather than a mic METER, and makes
        /// the mux mandatory rather than optional. If it ever reads true on a Mac, screen+mic silently
        /// returns to the topology that lost 88% of the audio.
        /// </summary>
        [Fact]
        public void CaptureTopology_OnMacOS_TheScreenRecorderDoesNotCaptureTheMic()
        {
            Assert.False(CaptureTopology.ScreenRecorderCapturesMic);
            Assert.Equal("mic_native.wav", CaptureTopology.MicNativeWav);
        }
    }
}
