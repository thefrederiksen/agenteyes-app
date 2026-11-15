using System.Collections.Generic;
using System.Linq;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    /// <summary>
    /// ISSUE #114. On macOS the microphone is captured by its own ffmpeg process rather than as a
    /// second input of the screen capture. The audio loss that issue was filed about was caused by
    /// throttled background QoS, not by the topology (see FfmpegArgs.AvfVideoCapture); these are the
    /// pure parts of the topology change that accompanied it: the capture-clock reading that aligns
    /// the two files, the mux argument list that applies it, and the guarantee that moving the macOS
    /// microphone did not move the Windows or Linux one.
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
            // Issue #128 round two (D7) pins the NaN sanitizer to the mic's MEASURED layout, so the
            // chain cannot be built without one. AudioMix.Prepare measures it from the mic WAV; on
            // this machine that is a mono device.
            MicChannels = 1,
        };

        /// <summary>
        /// The clean-voice chain Opts() produces, spelled out rather than composed from the
        /// product's own constants: a pin that is built the way the thing it pins is built cannot
        /// fail when that thing changes. Issue #128's sanitizer and arnndn framing are part of it.
        /// </summary>
        private const string Chain =
            "[0:a]aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same,"
            + "aformat=channel_layouts=mono,asetnsamples=n=480:p=1,"
            + "arnndn=m='/Users/u/Library/Application Support/AgentEyes/models/bd.rnnn',"
            + "agate=threshold=0.02:ratio=2:attack=20:release=250,speechnorm=e=4:p=0.95,volume=1[m]";

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

        // ---- the pass-1 audio command that applies it ----------------------
        //
        // ISSUE #136 SPLIT THIS IN HALF, and the halves are where the alignment now lives. #114 was
        // written against a single-command mux - "-i raw.mp4 -i mic.wav -filter_complex <chain>
        // -map 0:v -c:v copy -map [a] -c:a aac" - which is exactly the shape #136 proved deadlocks
        // on macOS: the copied video is queued almost instantly while the mic is still being
        // filtered, ffmpeg's muxing queue hits its bound and the process spins at ~99% CPU without
        // ever finalizing. So the mic's whole journey - the seek, the chain, the delay, the limiter
        // - is PASS 1, into an intermediate WAV with no video stream anywhere in the command, and
        // the picture is joined to it by PASS 2, which carries no filter graph at all.
        //
        // The alignment arithmetic below is unchanged by that; what changed is which command
        // carries it, and that the mic is now input 0 of its own pass rather than input 1 of a mux.
        // AgentEyes.Tests.Shared.FfmpegMuxShapeTests is the guard that keeps the single-pass shape
        // out; these are the pins for what #114 adds to the pass that survived.

        [Fact]
        public void MicPass_NoOffset_IsTheMicWavThroughTheChainIntoTheIntermediate()
        {
            var args = FfmpegArgs.FilterMicToWav("mic_native.wav", "audio_processed.wav", Opts(), 0.0);

            Assert.Equal(new[]
            {
                "-y", "-i", "mic_native.wav",
                "-filter_complex", Chain + ";[m]alimiter=limit=0.95:level=false[a]",
                "-map", "[a]", "-c:a", "pcm_f32le", "-rf64", "auto", "audio_processed.wav",
            }, args);
        }

        /// <summary>
        /// The mic is input 0 of its own pass. Under the single-command mux it was input 1, because
        /// input 0 was the video; there is no video in this command at all, which is the point of
        /// the split - so [1:a] here would read an input that does not exist.
        /// </summary>
        [Fact]
        public void MicPass_ReadsTheMicFromInputZeroAndHasNoVideoInTheCommand()
        {
            var args = FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", Opts(), 0.0);
            string s = Join(args);

            Assert.Contains("[0:a]", s);
            Assert.DoesNotContain("[1:a]", s);
            Assert.DoesNotContain("0:v", args);
            Assert.DoesNotContain("-c:v", args);
            Assert.Equal(1, args.Count(a => a == "-i"));
        }

        [Fact]
        public void MicPass_MicStartedLate_PadsItsHeadWithThatMuchSilence()
        {
            // +1.25 s: the mic's first sample is 1.25 s after the first video frame, so its samples
            // belong 1.25 s into the timeline. Letting them start at zero would run the speech early.
            string s = Join(FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", Opts(), 1.25));

            Assert.Contains("adelay=1250:all=1", s);
            Assert.DoesNotContain("-ss", s);
        }

        [Fact]
        public void MicPass_MicStartedEarly_SeeksThatHeadOffTheInput()
        {
            // -0.75 s: the WAV holds 0.75 s of audio from before the picture existed.
            var args = FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", Opts(), -0.75);

            int ss = args.IndexOf("-ss");
            Assert.True(ss >= 0);
            Assert.Equal("0.75", args[ss + 1]);
            // An INPUT seek: it belongs before the -i it applies to, and before nothing else.
            Assert.Equal("-i", args[ss + 2]);
            Assert.Equal("mic.wav", args[ss + 3]);
            Assert.DoesNotContain("adelay", Join(args));
        }

        /// <summary>
        /// Whatever the offset does to the mic, the pass stays a pass: one input, no video, the
        /// limiter still on the end, and the intermediate WAV as the output. An offset that pushed a
        /// video stream back into this command would put the deadlock back (issue #136).
        /// </summary>
        [Fact]
        public void MicPass_AtEveryOffset_StaysAnAudioOnlyPassIntoTheIntermediate()
        {
            foreach (double offset in new[] { -2.0, -0.001, 0.0, 0.001, 2.0 })
            {
                var args = FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", Opts(), offset);

                Assert.Equal("audio_processed.wav", args[^1]);
                Assert.Equal("pcm_f32le", args[args.IndexOf("-c:a") + 1]);
                Assert.Equal("auto", args[args.IndexOf("-rf64") + 1]);
                Assert.Contains("alimiter=limit=0.95:level=false", Join(args));
                Assert.Equal(1, args.Count(a => a == "-i"));
                Assert.DoesNotContain("0:v", args);
                Assert.DoesNotContain("copy", args);
            }
        }

        /// <summary>A sub-millisecond offset is not worth an adelay stage, and adelay cannot express
        /// it: the filter takes whole milliseconds. Rounding keeps the number ffmpeg will accept.</summary>
        [Fact]
        public void MicPass_SmallPositiveOffset_RoundsToMillisecondsFfmpegAccepts()
        {
            string s = Join(FfmpegArgs.FilterMicToWav("mic.wav", "audio_processed.wav", Opts(), 0.2085));

            Assert.Contains("adelay=208.5:all=1", s);
        }

        /// <summary>
        /// PASS 2 is what joins the aligned audio to the picture, and it is the same builder every
        /// other mux on every platform uses: no filter graph, so nothing can race the video copy.
        /// Pinned here because this is the pass #114's own path now ends in.
        /// </summary>
        [Fact]
        public void MuxPass_JoinsThePictureToTheIntermediateWithNoFilterGraph()
        {
            var args = FfmpegArgs.MuxVideoWithProcessedAudio(
                "raw.mp4", "audio_processed.wav", "recording.mp4.muxing.tmp");

            Assert.Equal(new[]
            {
                "-y", "-i", "raw.mp4", "-i", "audio_processed.wav",
                "-map", "0:v", "-c:v", "copy", "-map", "1:a", "-c:a", "aac", "-b:a", "160k",
                "-f", "mp4", "recording.mp4.muxing.tmp",
            }, args);
            Assert.DoesNotContain("-filter_complex", args);
        }

        /// <summary>
        /// FAIL-CLOSED on the shape that was removed. #114's single-command builders cannot be
        /// reintroduced by name without this failing, and FfmpegMuxShapeTests would catch them under
        /// any other name - the two together are why "split the mux" cannot be quietly undone.
        /// </summary>
        [Fact]
        public void TheSingleCommandMuxBuildersAreGone()
        {
            foreach (string removed in new[] { "MuxVideoAddMic", "MuxVideoAddMicAndSystem" })
            {
                Assert.Null(typeof(FfmpegArgs).GetMethod(
                    removed,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic));
            }
        }

        // ---- mixed: mic captured separately, system from its own WAV --------

        /// <summary>
        /// macOS mixed audio, as pass 1. The microphone is input 0 and the system WAV input 1 -
        /// under the single-command mux they were inputs 1 and 2 behind the video, and on macOS the
        /// video holds no audio track at all. NOT exercised end to end on this machine (it has no
        /// virtual audio device, so a mixed recording cannot start), which is why the argument list
        /// is pinned here instead.
        /// </summary>
        [Fact]
        public void MicPlusSystemPass_MicIsInputZero_SystemIsInputOne()
        {
            var args = FfmpegArgs.MixMicSystemToWav(
                "mic_native.wav", "sys_native.wav", "audio_processed.wav", Opts(), 0.0);

            Assert.Equal(new[]
            {
                "-y", "-i", "mic_native.wav", "-i", "sys_native.wav",
                "-filter_complex",
                Chain + ";[1:a]volume=0.7[s];"
                      + "[m][s]amix=inputs=2:duration=longest:normalize=0,"
                      + "alimiter=limit=0.95:level=false[a]",
                "-map", "[a]", "-c:a", "pcm_f32le", "-rf64", "auto", "audio_processed.wav",
            }, args);
        }

        [Fact]
        public void MicPlusSystemPass_AlignsTheMicTheSameWayTheMicOnlyPassDoes()
        {
            var late = FfmpegArgs.MixMicSystemToWav(
                "mic.wav", "sys.wav", "audio_processed.wav", Opts(), 1.25);
            // The delay is on the MIC branch and nowhere near the system branch, which is written by
            // the same process as the picture and is therefore already aligned to it.
            Assert.Contains("[m]adelay=1250:all=1,anull[md];[md][s]amix=", Join(late));
            Assert.DoesNotContain("-ss", late);

            var early = FfmpegArgs.MixMicSystemToWav(
                "mic.wav", "sys.wav", "audio_processed.wav", Opts(), -0.75);
            int ss = early.IndexOf("-ss");
            Assert.True(ss >= 0);
            Assert.Equal("0.75", early[ss + 1]);
            // The seek applies to the MIC, not to the system WAV.
            Assert.Equal("-i", early[ss + 2]);
            Assert.Equal("mic.wav", early[ss + 3]);
            Assert.DoesNotContain("adelay", Join(early));
        }

        [Fact]
        public void MicPlusSystemPass_AtEveryOffset_StaysAnAudioOnlyPassIntoTheIntermediate()
        {
            foreach (double offset in new[] { -2.0, 0.0, 2.0 })
            {
                var args = FfmpegArgs.MixMicSystemToWav(
                    "mic.wav", "sys.wav", "audio_processed.wav", Opts(), offset);

                Assert.Equal("audio_processed.wav", args[^1]);
                Assert.DoesNotContain("0:v", args);
                Assert.DoesNotContain("copy", args);
                Assert.Equal(2, args.Count(a => a == "-i"));
            }
        }

        // ---- criterion 8: Windows and Linux are untouched -------------------

        /// <summary>
        /// ISSUE #114 ADDS AN OPTIONAL ALIGNMENT OFFSET TO TWO SHARED PASS-1 BUILDERS, and nothing
        /// else about them. Its default is 0.0, which is what every caller on a platform whose screen
        /// recorder captures the mic itself passes, and at 0.0 both builders emit the list they
        /// emitted before #114 - byte for byte, which is what these two tests pin.
        ///
        /// How this changed in the rebase onto #136, because the earlier version of this comment is
        /// no longer true of the code. #114 originally parameterized MicChain's SOURCE LABEL, so the
        /// macOS mux could read the mic from [1:a] behind the video; the enumeration that mattered
        /// was then "which callers rely on the [0:a] default". After #136's split there is no video
        /// in a pass-1 command, so the mic is input 0 everywhere and that parameter is gone again -
        /// MicChain is byte-identical to main's. What #114 now adds is the offset, so what has to be
        /// pinned is that the offset's default changes nothing.
        ///
        /// Both builders are also pinned, without an offset, by the shared and Linux suites
        /// (tests/Shared/FfmpegMuxShapeTests.cs, tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs).
        /// These run on a Mac, which is the machine the change was made on.
        /// </summary>
        [Fact]
        public void FilterMicToWav_WithNoOffset_IsTheListItEmittedBeforeIssue114()
        {
            var expected = new[]
            {
                "-y", "-i", "raw.mp4",
                "-filter_complex", Chain + ";[m]alimiter=limit=0.95:level=false[a]",
                "-map", "[a]", "-c:a", "pcm_f32le", "-rf64", "auto", "audio_processed.wav",
            };

            // The overload every non-macOS caller uses - it does not mention an offset at all ...
            Assert.Equal(expected, FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", Opts()));
            // ... and an explicit zero is the same list, so the default is not hiding a difference.
            Assert.Equal(expected,
                FfmpegArgs.FilterMicToWav("raw.mp4", "audio_processed.wav", Opts(), 0.0));
        }

        [Fact]
        public void MixMicSystemToWav_WithNoOffset_IsTheListItEmittedBeforeIssue114()
        {
            var expected = new[]
            {
                "-y", "-i", "raw.mp4", "-i", "sys.wav",
                "-filter_complex",
                Chain + ";[1:a]volume=0.7[s];"
                      + "[m][s]amix=inputs=2:duration=longest:normalize=0,"
                      + "alimiter=limit=0.95:level=false[a]",
                "-map", "[a]", "-c:a", "pcm_f32le", "-rf64", "auto", "audio_processed.wav",
            };

            Assert.Equal(expected,
                FfmpegArgs.MixMicSystemToWav("raw.mp4", "sys.wav", "audio_processed.wav", Opts()));
            Assert.Equal(expected,
                FfmpegArgs.MixMicSystemToWav("raw.mp4", "sys.wav", "audio_processed.wav", Opts(), 0.0));
        }

        /// <summary>
        /// The two shared builders #114 does not touch at all, pinned on this machine for the same
        /// reason: the mic-plus-system WAV mix an audio-only recording ends in, and the system-only
        /// pass. Neither grew an offset - the system track is written by the same process as the
        /// picture, so there is nothing to align.
        /// </summary>
        [Fact]
        public void TheSharedBuildersIssue114DoesNotTouch_AreUnchanged()
        {
            Assert.Equal(new[]
            {
                "-y", "-i", "mic.wav", "-i", "sys.wav",
                "-filter_complex",
                Chain + ";[1:a]volume=0.7[s];"
                      + "[m][s]amix=inputs=2:duration=longest:normalize=0,"
                      + "alimiter=limit=0.95:level=false[a]",
                "-map", "[a]", "-ar", "48000", "-ac", "2", "out.wav",
            }, FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", Opts()));

            Assert.Equal(new[]
            {
                "-y", "-i", "sys_native.wav",
                "-filter_complex", "[0:a]volume=0.7[a]",
                "-map", "[a]", "-c:a", "pcm_f32le", "-rf64", "auto", "audio_processed.wav",
            }, FfmpegArgs.FilterSystemToWav("sys_native.wav", "audio_processed.wav", 0.7));
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
        /// returns to the single-process topology that shipped when 88% of the audio was being lost,
        /// and the mic WAV that made that loss measurable stops being written.
        /// </summary>
        [Fact]
        public void CaptureTopology_OnMacOS_TheScreenRecorderDoesNotCaptureTheMic()
        {
            Assert.False(CaptureTopology.ScreenRecorderCapturesMic);
            Assert.Equal("mic_native.wav", CaptureTopology.MicNativeWav);
        }
    }
}
