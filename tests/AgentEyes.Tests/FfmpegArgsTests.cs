using System.Collections.Generic;
using System.Drawing;
using Xunit;
using AgentEyes.Video;

namespace AgentEyes.Tests
{
    public class FfmpegArgsTests
    {
        private static string Join(IReadOnlyList<string> a) => string.Join(" ", a);

        [Fact]
        public void VideoCapture_includes_gdigrab_offsets_and_size()
        {
            var args = FfmpegArgs.VideoCapture(new Rectangle(2560, 0, 1920, 1080), null, 30, 23, "out.mp4");
            string s = Join(args);
            Assert.Contains("-f gdigrab", s);
            Assert.Contains("-offset_x 2560", s);
            Assert.Contains("-offset_y 0", s);
            Assert.Contains("-video_size 1920x1080", s);
            Assert.Contains("-i desktop", s);
            Assert.EndsWith("out.mp4", s);
        }

        [Fact]
        public void VideoCapture_without_mic_has_no_audio_input_or_codec()
        {
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 1280, 720), null, 30, 23, "o.mp4");
            string s = Join(args);
            Assert.DoesNotContain("dshow", s);
            Assert.DoesNotContain("-c:a", s);
            Assert.Contains("libx264", s);
        }

        [Fact]
        public void VideoCapture_with_mic_adds_dshow_and_aac()
        {
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 1280, 720), "Microphone (Yeti)", 30, 23, "o.mp4");
            Assert.Contains("dshow", args);
            Assert.Contains("audio=Microphone (Yeti)", args);
            Assert.Contains("aac", args);
        }

        [Fact]
        public void VideoCapture_with_mic_sets_small_dshow_buffer_so_stop_does_not_drop_the_tail()
        {
            // Issue #125: without a small dshow audio buffer the final ~2.4s of mic audio sat
            // un-read in the device buffer and was discarded when the capture stopped.
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 1280, 720), "Microphone (Yeti)", 30, 23, "o.mp4");
            string s = Join(args);
            Assert.Contains("-audio_buffer_size 80", s);
            Assert.Contains("-thread_queue_size 1024", s);
        }

        [Fact]
        public void VideoCapture_without_mic_has_no_audio_buffer_option()
        {
            // audio_buffer_size is a dshow input option; it must not appear on a video-only capture.
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 1280, 720), null, 30, 23, "o.mp4");
            Assert.DoesNotContain("-audio_buffer_size", Join(args));
        }

        [Fact]
        public void VideoCapture_evenizes_odd_dimensions()
        {
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 1921, 1081), null, 30, 23, "o.mp4");
            Assert.Contains("1920x1080", Join(args));
        }

        [Fact]
        public void VideoCapture_uses_given_fps_and_crf()
        {
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 640, 480), null, 60, 18, "o.mp4");
            string s = Join(args);
            Assert.Contains("-framerate 60", s);
            Assert.Contains("-crf 18", s);
        }

        // ---- region clamp + pad to exact size (issue #69, AC4) ------------

        [Fact]
        public void VideoCapture_region_that_fits_desktop_grabs_it_whole_without_padding()
        {
            // A region fully inside the desktop must produce today's exact args: grab == request, no pad.
            var desktop = new Rectangle(0, 0, 3840, 2160);
            var args = FfmpegArgs.VideoCapture(new Rectangle(0, 0, 1080, 1080), null, 30, 23, "o.mp4", desktop);
            string s = Join(args);
            Assert.Contains("-video_size 1080x1080", s);
            Assert.Contains("-offset_x 0", s);
            Assert.DoesNotContain("-vf", s);
            Assert.DoesNotContain("pad=", s);
        }

        [Fact]
        public void VideoCapture_vertical_taller_than_desktop_grabs_the_fit_and_pads_to_exact_size()
        {
            // AC4 core: 1080x1920 vertical on a desktop only ~1848 tall. gdigrab grabs the 1848-tall
            // slice that fits; ffmpeg pads it back to EXACTLY 1080x1920 (black fill for the shortfall).
            var desktop = new Rectangle(0, -5, 3840, 1853); // bottom = 1848 (this machine's layout)
            var args = FfmpegArgs.VideoCapture(new Rectangle(420, 0, 1080, 1920), null, 30, 23, "o.mp4", desktop);
            string s = Join(args);
            // Grab is clamped to what fits (height 1848, not 1920) so gdigrab can open the input.
            Assert.Contains("-video_size 1080x1848", s);
            Assert.Contains("-offset_x 420", s);
            Assert.Contains("-offset_y 0", s);
            // Output is padded to the exact requested 1080x1920, content anchored top-left of the region.
            Assert.Contains("-vf pad=1080:1920:0:0:black", s);
        }

        [Fact]
        public void VideoCapture_region_off_the_left_edge_pads_with_a_positive_x_offset()
        {
            // Region starts left of the desktop: grab the on-screen part, place it at the matching
            // offset inside the exact-size canvas so the framing stays true.
            var desktop = new Rectangle(0, 0, 1920, 1080);
            var args = FfmpegArgs.VideoCapture(new Rectangle(-100, 0, 1080, 1080), null, 30, 23, "o.mp4", desktop);
            string s = Join(args);
            Assert.Contains("-video_size 980x1080", s); // 1080 - 100 off-screen = 980 grabbed
            Assert.Contains("-offset_x 0", s);
            Assert.Contains("-vf pad=1080:1080:100:0:black", s);
        }

        [Fact]
        public void VideoCapture_region_not_overlapping_desktop_throws()
        {
            var desktop = new Rectangle(0, 0, 1920, 1080);
            Assert.Throws<UsageException>(() =>
                FfmpegArgs.VideoCapture(new Rectangle(5000, 0, 640, 480), null, 30, 23, "o.mp4", desktop));
        }

        [Fact]
        public void VideoCapture_null_desktop_grabs_region_as_is()
        {
            // No bounds constraint (tests / guaranteed-fit callers): grab the region unchanged, no pad.
            var args = FfmpegArgs.VideoCapture(new Rectangle(420, 0, 1080, 1920), null, 30, 23, "o.mp4", null);
            string s = Join(args);
            Assert.Contains("-video_size 1080x1920", s);
            Assert.DoesNotContain("pad=", s);
        }

        [Fact]
        public void ExtractWav_targets_16k_mono()
        {
            var args = FfmpegArgs.ExtractWav("in.mp4", "out.wav");
            string s = Join(args);
            Assert.Contains("-ar 16000", s);
            Assert.Contains("-ac 1", s);
            Assert.Contains("-vn", s);
            Assert.EndsWith("out.wav", s);
        }

        [Fact]
        public void SceneExtract_uses_threshold_and_pattern()
        {
            var args = FfmpegArgs.SceneExtract("in.mp4", 0.4, "shots/frame_%03d.png");
            string s = Join(args);
            Assert.Contains("select='gt(scene,0.4)'", s);
            Assert.Contains("shots/frame_%03d.png", s);
        }

        [Fact]
        public void IntervalExtract_converts_seconds_to_fps()
        {
            // 1 frame every 5s -> fps=0.2
            var args = FfmpegArgs.IntervalExtract("in.mp4", 5, "shots/frame_%03d.png");
            string s = Join(args);
            Assert.Contains("fps=0.2", s);
            Assert.Contains("shots/frame_%03d.png", s);
        }

        [Fact]
        public void IntervalExtract_handles_one_second()
        {
            var args = FfmpegArgs.IntervalExtract("in.mp4", 1, "f_%03d.png");
            Assert.Contains("fps=1", Join(args));
        }

        // Regression, issue #136. ffmpeg REMOVED -vsync in 9.0 and the bundled build is 9.0, so
        // passing it aborts the process before any work with "Unrecognized option 'vsync'". That
        // killed key-frame extraction and took the whole transcription pass with it - a 114-minute
        // recording produced no transcript at all. -fps_mode is the replacement.

        [Fact]
        public void IntervalExtract_uses_fps_mode_not_the_removed_vsync()
        {
            string s = Join(FfmpegArgs.IntervalExtract("in.mp4", 5, "shots/frame_%03d.png"));
            Assert.Contains("-fps_mode vfr", s);
            Assert.DoesNotContain("-vsync", s);
        }

        [Fact]
        public void SceneExtract_uses_fps_mode_not_the_removed_vsync()
        {
            string s = Join(FfmpegArgs.SceneExtract("in.mp4", 0.4, "shots/frame_%03d.png"));
            Assert.Contains("-fps_mode vfr", s);
            Assert.DoesNotContain("-vsync", s);
        }

        [Fact]
        public void GenerateTone_builds_lavfi_sine()
        {
            var s = Join(FfmpegArgs.GenerateTone(440, 5, 0.5, "tone.wav"));
            Assert.Contains("lavfi", s);
            Assert.Contains("sine=frequency=440", s);
            Assert.Contains("volume=0.5", s);
            Assert.EndsWith("tone.wav", s);
        }

        // All mic processing on, with a model path so the arnndn stage can be built.
        private static AgentEyes.AudioMixOptions FullOpts() => new()
        {
            MicGain = 1.0,
            SystemGain = 0.7,
            NoiseGate = true,
            GateThreshold = 0.02,
            RnnoiseModelPath = @"C:\Users\u\AppData\Local\AgentEyes\models\bd.rnnn",
        };

        [Fact]
        public void MixTwoWav_has_full_clean_voice_chain_and_amix()
        {
            var s = Join(FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", FullOpts()));
            Assert.Contains("arnndn=m='C\\:/Users/u/AppData/Local/AgentEyes/models/bd.rnnn'", s);
            Assert.Contains("agate=threshold=0.02", s);
            Assert.Contains("speechnorm=", s);
            Assert.Contains("volume=1", s);          // mic gain
            Assert.Contains("volume=0.7", s);        // system gain
            Assert.Contains("amix=inputs=2", s);
            Assert.Contains("normalize=0", s);
            Assert.Contains("alimiter=", s);         // safety limiter after the mix
            Assert.EndsWith("out.wav", s);
        }

        [Fact]
        public void MixTwoWav_chain_is_in_obs_order_denoise_gate_level_volume()
        {
            var s = Join(FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", FullOpts()));
            int denoise = s.IndexOf("arnndn"), gate = s.IndexOf("agate"),
                level = s.IndexOf("speechnorm"), vol = s.IndexOf("volume=1");
            Assert.True(denoise < gate && gate < level && level < vol);
        }

        [Fact]
        public void MixTwoWav_without_gate_omits_agate()
        {
            var o = FullOpts(); o.NoiseGate = false;
            var s = Join(FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", o));
            Assert.DoesNotContain("agate", s);
            Assert.Contains("amix=inputs=2", s);
        }

        [Fact]
        public void MixTwoWav_without_denoise_and_level_omits_those_stages()
        {
            var o = FullOpts(); o.NoiseSuppression = false; o.VoiceLeveling = false;
            var s = Join(FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", o));
            Assert.DoesNotContain("arnndn", s);
            Assert.DoesNotContain("speechnorm", s);
            Assert.Contains("agate", s);
        }

        [Fact]
        public void MixTwoWav_suppression_without_model_path_throws()
        {
            var o = FullOpts(); o.RnnoiseModelPath = null;
            Assert.Throws<AgentEyes.UsageException>(
                () => FfmpegArgs.MixTwoWav("mic.wav", "sys.wav", "out.wav", o));
        }

        [Fact]
        public void MuxVideoMixMicSystem_copies_video_and_mixes_audio()
        {
            var args = FfmpegArgs.MuxVideoMixMicSystem("raw.mp4", "sys.wav", "final.mp4", FullOpts());
            var s = Join(args);
            Assert.Contains("-map 0:v", s);
            Assert.Contains("-c:v copy", s);
            Assert.Contains("amix=inputs=2", s);
            Assert.Contains("aac", s);
            Assert.EndsWith("final.mp4", s);
        }

        [Fact]
        public void FilterVideoMic_copies_video_and_filters_mic_without_amix()
        {
            var s = Join(FfmpegArgs.FilterVideoMic("raw.mp4", "final.mp4", FullOpts()));
            Assert.Contains("-map 0:v", s);
            Assert.Contains("-c:v copy", s);
            Assert.Contains("arnndn", s);
            Assert.Contains("speechnorm", s);
            Assert.Contains("alimiter=", s);
            Assert.DoesNotContain("amix", s);        // single source, nothing to mix
            Assert.EndsWith("final.mp4", s);
        }

        [Fact]
        public void MuxVideoAddSystem_maps_system_as_only_audio()
        {
            var s = Join(FfmpegArgs.MuxVideoAddSystem("raw.mp4", "sys.wav", "final.mp4", 0.8));
            Assert.Contains("-map 0:v", s);
            Assert.Contains("-c:v copy", s);
            Assert.Contains("volume=0.8", s);
            Assert.DoesNotContain("amix", s);     // no mic to mix
        }

        // ---- subtitle burn-in (issue #102) --------------------------------

        [Fact]
        public void BurnSubtitles_applies_subtitles_filter_with_escaped_path_and_default_style()
        {
            var args = FfmpegArgs.BurnSubtitles(
                @"C:\rec\recording.mp4", @"C:\rec\transcript.tr.vtt", @"C:\rec\recording.tr.subtitled.mp4");
            string s = Join(args);
            // libass subtitles filter references the VTT with a filtergraph-escaped path
            // (forward slashes, ':' escaped), single-quoted.
            Assert.Contains("subtitles='C\\:/rec/transcript.tr.vtt'", s);
            // documented default style (issue #102 A1): white Arial with black outline, bottom-centered.
            Assert.Contains("force_style='FontName=Arial,FontSize=24", s);
            Assert.Contains("PrimaryColour=&H00FFFFFF", s);
            Assert.Contains("OutlineColour=&H00000000", s);
            Assert.Contains("Alignment=2", s);
            Assert.Contains("MarginV=30", s);
            Assert.EndsWith("recording.tr.subtitled.mp4", s);
        }

        [Fact]
        public void BurnSubtitles_reencodes_video_and_copies_audio()
        {
            var args = FfmpegArgs.BurnSubtitles("in.mp4", "sub.vtt", "out.mp4", crf: 20);
            string s = Join(args);
            Assert.Contains("-c:v libx264", s);   // video must be re-encoded to burn the overlay
            Assert.Contains("-crf 20", s);        // honors the given quality
            Assert.Contains("-c:a copy", s);      // audio track preserved unchanged
            Assert.Contains("-i in.mp4", s);
        }

        [Fact]
        public void ToCommandLine_quotes_args_with_spaces()
        {
            var cmd = FfmpegArgs.ToCommandLine("ffmpeg.exe", new[] { "-i", "audio=My Mic" });
            Assert.Contains("\"audio=My Mic\"", cmd);
        }
    }
}
