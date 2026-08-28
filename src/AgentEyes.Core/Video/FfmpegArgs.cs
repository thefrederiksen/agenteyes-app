using System;
using System.Collections.Generic;
using Drawing = System.Drawing;

namespace AgentEyes.Video
{
    /// <summary>
    /// Pure ffmpeg argument builders. Kept side-effect free so they can be unit tested
    /// without launching ffmpeg.
    /// </summary>
    internal static class FfmpegArgs
    {
        /// <summary>
        /// Capture a screen region (gdigrab) plus a microphone (dshow), muxed to MP4 (H.264 + AAC).
        ///
        /// gdigrab refuses to open its input when the requested rectangle extends past the virtual
        /// desktop, so a social-format region larger than the desktop (e.g. a 1080x1920 vertical on a
        /// 1080-tall monitor) cannot be grabbed as-is. To keep such presets at their EXACT requested
        /// dimensions (issue #69, AC4), the requested region is split into a grab rectangle (the part
        /// that actually fits the desktop) and a pad step that composes the grabbed frames back to the
        /// exact requested WxH, filling the off-desktop area with black. When the region fits fully the
        /// grab equals the request and no pad is emitted - identical to a plain full-fit capture.
        /// </summary>
        /// <param name="capture">Region in virtual-desktop device pixels (full monitor = its bounds).</param>
        /// <param name="dshowMicName">Exact DirectShow device name, or null for video-only.</param>
        /// <param name="fps">Capture frame rate.</param>
        /// <param name="crf">x264 quality (lower = better; 23 is a good default).</param>
        /// <param name="desktop">The capturable virtual-desktop bounds (device px) used to clamp+pad an
        /// oversized region. Null (or empty) means "no bounds constraint" - grab the region as-is (used by
        /// callers/tests that guarantee the region fits). Production callers pass Monitors.VirtualBounds().</param>
        /// <param name="previewStream">Issue #33: also emit the small MJPEG monitoring stream on stdout
        /// (see <see cref="PreviewOutput"/>). False - the default - produces byte-for-byte the command
        /// line this built before the HUD preview existed (AC11).</param>
        public static List<string> VideoCapture(
            Drawing.Rectangle capture, string? dshowMicName, int fps, int crf, string outPath,
            Drawing.Rectangle? desktop = null, bool previewStream = false)
        {
            var target = RegionMath.Evenize(capture);

            // Decide what gdigrab actually grabs, and whether we must pad it back to the exact size.
            var grab = target;
            string? padFilter = null;
            if (desktop is Drawing.Rectangle d && !d.IsEmpty)
            {
                var raw = Drawing.Rectangle.Intersect(target, d);
                if (raw.Width < 2 || raw.Height < 2)
                    throw new UsageException(
                        $"the capture region {target.Width}x{target.Height} at ({target.X},{target.Y}) " +
                        $"does not overlap the desktop ({d.Width}x{d.Height} at ({d.X},{d.Y})) - nothing to capture.");

                var fit = RegionMath.Evenize(raw);
                if (fit != target)
                {
                    grab = fit;
                    int ox = grab.X - target.X;
                    int oy = grab.Y - target.Y;
                    // yuv420p pad offsets must be even; round down (shifts content <=1px, chroma-aligned).
                    ox -= ox % 2;
                    oy -= oy % 2;
                    if (ox < 0) ox = 0;
                    if (oy < 0) oy = 0;
                    if (ox + grab.Width > target.Width) ox = target.Width - grab.Width;
                    if (oy + grab.Height > target.Height) oy = target.Height - grab.Height;
                    padFilter = $"pad={target.Width}:{target.Height}:{ox}:{oy}:black";
                }
            }

            var a = new List<string>
            {
                "-y",
                "-f", "gdigrab",
                // Buffer input packets so a busy CPU during warmup does not drop frames (the logs
                // showed drop=5 without this). Applies to the input it precedes (gdigrab).
                "-thread_queue_size", "1024",
                "-framerate", fps.ToString(),
                "-offset_x", grab.X.ToString(),
                "-offset_y", grab.Y.ToString(),
                "-video_size", $"{grab.Width}x{grab.Height}",
                "-i", "desktop",
            };

            if (!string.IsNullOrWhiteSpace(dshowMicName))
            {
                a.Add("-f");
                a.Add("dshow");
                // Issue #125: keep the dshow capture buffer small so that when the user hits stop,
                // at most ~80ms of mic audio is still un-read in the device buffer. The default
                // buffering left ~2.4s of the final words undelivered, and stopping the capture
                // discarded them. thread_queue_size guards against packet drops on the audio input.
                a.Add("-thread_queue_size");
                a.Add("1024");
                a.Add("-audio_buffer_size");
                a.Add("80");
                a.Add("-i");
                a.Add($"audio={dshowMicName}");
            }

            if (padFilter != null)
            {
                a.Add("-vf");
                a.Add(padFilter);
            }

            a.AddRange(new[]
            {
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(),
            });

            if (!string.IsNullOrWhiteSpace(dshowMicName))
            {
                a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
            }

            a.Add(outPath);
            if (previewStream) a.AddRange(PreviewOutput());
            return a;
        }

        // ---- the HUD preview tap (issue #33) --------------------------------

        /// <summary>Preview frame height in pixels. The width follows the source aspect ratio
        /// (scale=-2 rounds it to an even number), so a 16:9 screen previews at 480x270 - the size
        /// assumption C2 names - and a 4:3 camera at 360x270.</summary>
        public const int PreviewHeight = 270;

        /// <summary>Preview frame rate. A monitor, not a viewfinder (assumption C2): enough to see
        /// motion, low enough that AC9's cost bound is not in question.</summary>
        public const int PreviewFps = 10;

        /// <summary>MJPEG quality for the preview (2 = best, 31 = worst). Tens of kilobytes a frame.</summary>
        public const int PreviewQuality = 8;

        /// <summary>
        /// The preview's filter chain, and every part of it was MEASURED rather than chosen
        /// (issue #33, AC9 - a preview must cost the recording no dropped frames).
        ///
        /// On a 1920x1080 30fps capture, 30-second runs on 2026-08-28:
        ///   control (no preview)                             drops 4, 1, 5
        ///   scale then -r 10, 4:4:4                           drops 19, 27, 37   <- REJECTED
        ///   fps=10 then scale, 4:2:0, neighbor sampling       drops 1, 0, 0      <- this
        ///
        /// The two-thirds difference is the ORDER: <c>fps=10</c> comes FIRST, so ten frames a second
        /// are scaled instead of thirty being scaled and twenty thrown away at the encoder.
        /// <c>flags=neighbor</c> is point sampling rather than a filtered resample - the right trade
        /// for a monitor at a quarter size, and the rest of the difference. <c>yuvj420p</c> halves
        /// the chroma the JPEG encoder has to touch.
        /// </summary>
        public static string PreviewFilter => $"fps={PreviewFps},scale=-2:{PreviewHeight}:flags=neighbor";

        /// <summary>
        /// The SECOND OUTPUT that feeds the recording HUD's live preview (issue #33): the captured
        /// video, scaled down and sent as an MJPEG stream on ffmpeg's STDOUT.
        ///
        /// STDOUT AND NOT A FILE, and that is the whole design decision. Handing ffmpeg a file for
        /// this output was measured on 2026-08-28: when the preview path failed mid-run, ffmpeg's
        /// muxer error terminated the WHOLE process and truncated a 15-second recording to 5.1
        /// seconds. A pipe moves that failure out of ffmpeg entirely - AgentEyes drains the pipe
        /// unconditionally (<see cref="Preview.PreviewTap"/>) and any failure downstream of the drain
        /// costs a picture, never the recording (AC10).
        ///
        /// It maps input 0's video explicitly because a recording with a microphone has a second
        /// input, and it never carries audio: the preview is a picture.
        ///
        /// <c>-flush_packets 1</c> is required rather than tidy. Without it the raw MJPEG muxer fills
        /// its 32KB AVIO buffer before anything reaches the pipe, which at these frame sizes delays
        /// every frame by two or three of them - a monitor that lags is a monitor that lies about
        /// what is being recorded right now.
        /// </summary>
        public static List<string> PreviewOutput() => new()
        {
            "-map", "0:v",
            "-vf", PreviewFilter,
            "-q:v", PreviewQuality.ToString(),
            "-pix_fmt", "yuvj420p",
            "-an",
            "-f", "mjpeg",
            "-flush_packets", "1",
            "pipe:1",
        };

        /// <summary>
        /// Capture a DirectShow camera to its OWN MP4 (issue #28) - a second, independent ffmpeg
        /// process running alongside <see cref="VideoCapture"/>, so the screen and the presenter stay
        /// two files an editor can compose later instead of one baked-in layout chosen at record time.
        ///
        /// VIDEO ONLY, by decision: no dshow audio input is opened and <c>-an</c> is passed, so
        /// camera.mp4 carries exactly one stream. All audio stays on recording.mp4.
        ///
        /// The camera is captured at the DEVICE'S OWN default resolution (issue #28, assumption A2):
        /// only the framerate is requested. Pinning an explicit <c>-video_size</c> makes ffmpeg's
        /// dshow input fail outright on a camera that does not offer that exact mode, which would turn
        /// a working camera into a failed start - and a failed start is loud here (decision 3).
        ///
        /// Encoding matches the screen video (assumption A3): libx264 / veryfast / yuv420p / CRF.
        /// </summary>
        /// <param name="dshowCameraName">Exact DirectShow device name (from FfmpegDevices.ListVideo).</param>
        /// <param name="fps">Requested camera frame rate.</param>
        /// <param name="crf">x264 quality (lower = better; 23 is the screen recorder's default).</param>
        /// <param name="outPath">Where camera.mp4 is written (its FINAL path - no deferred mux, A4).</param>
        /// <param name="previewStream">Issue #33: also emit the small MJPEG monitoring stream on stdout
        /// (see <see cref="PreviewOutput"/>). This is the ONLY way a camera preview can exist while a
        /// recording runs - ffmpeg holds the DirectShow device exclusively, so the preview cannot open
        /// it a second time (assumption C1). False - the default - produces byte-for-byte the command
        /// line this built before the HUD preview existed (AC11).</param>
        public static List<string> CameraCapture(
            string dshowCameraName, int fps, int crf, string outPath, bool previewStream = false)
        {
            if (string.IsNullOrWhiteSpace(dshowCameraName))
                throw new UsageException("a camera capture needs an exact DirectShow device name.");

            var a = new List<string>
            {
                "-y",
                "-f", "dshow",
                // Buffer input packets so a busy CPU during warmup does not drop camera frames -
                // the same guard the gdigrab input carries.
                "-thread_queue_size", "1024",
                "-framerate", fps.ToString(),
                "-i", $"video={dshowCameraName}",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(),
                // Explicitly no audio track: the input has none, and this says so in the output too
                // so camera.mp4 cannot acquire one by accident.
                "-an",
                outPath,
            };

            if (previewStream) a.AddRange(PreviewOutput());
            return a;
        }

        /// <summary>Extract a 16 kHz mono WAV (what Whisper wants) from any media file.</summary>
        public static List<string> ExtractWav(string inputPath, string wavPath) => new()
        {
            "-y", "-i", inputPath, "-vn", "-ac", "1", "-ar", "16000", "-f", "wav", wavPath,
        };

        /// <summary>
        /// Extract content-change key frames from a video via scene-change detection.
        /// Catches hard cuts (UI transitions); continuous motion scores low. outPattern e.g.
        /// shots/frame_%03d.png.
        /// </summary>
        public static List<string> SceneExtract(string inputPath, double sceneThreshold, string outPattern) => new()
        {
            "-y", "-i", inputPath,
            "-vf", $"select='gt(scene,{sceneThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)})'",
            // "-fps_mode", not the old "-vsync": ffmpeg REMOVED -vsync in 9.0, and the bundled
            // build is 9.0. Passing it aborts before any work with "Unrecognized option 'vsync'",
            // which killed key-frame extraction and therefore the whole transcription pass.
            "-fps_mode", "vfr",
            outPattern,
        };

        /// <summary>
        /// Extract one representative frame every N seconds (reliable for any video, including
        /// continuous footage). outPattern e.g. shots/frame_%03d.png.
        /// </summary>
        public static List<string> IntervalExtract(string inputPath, double everySeconds, string outPattern)
        {
            double fps = everySeconds <= 0 ? 1.0 : 1.0 / everySeconds;
            return new()
            {
                "-y", "-i", inputPath,
                "-vf", $"fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                // See SceneExtract: -vsync was removed in ffmpeg 9.0; -fps_mode is its replacement.
                "-fps_mode", "vfr",
                outPattern,
            };
        }

        /// <summary>Generate a test sine-tone WAV (48 kHz stereo) at a given amplitude. For self-tests.</summary>
        public static List<string> GenerateTone(int freq, double seconds, double amplitude, string outWav) => new()
        {
            "-y", "-f", "lavfi", "-i",
            $"sine=frequency={freq}:duration={Inv(seconds)}",
            "-filter:a", $"volume={Inv(amplitude)}",
            "-ar", "48000", "-ac", "2", outWav,
        };

        // ---- audio mixing (mic + system) ----------------------------------

        private static string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Mic chain prefix producing label [m], in OBS order: RNNoise suppression (arnndn),
        /// noise gate, voice leveling (speechnorm), then manual volume. Each stage optional.
        /// </summary>
        private static string MicChain(AudioMixOptions o)
        {
            string denoise = "";
            if (o.NoiseSuppression)
            {
                if (string.IsNullOrWhiteSpace(o.RnnoiseModelPath))
                    throw new UsageException(
                        "noise suppression is on but the RNNoise model path is not set - " +
                        "call RnnoiseModel.Ensure() before building mix args");
                denoise = $"arnndn=m='{FilterPath(o.RnnoiseModelPath!)}',";
            }
            string gate = o.NoiseGate
                ? $"agate=threshold={Inv(o.GateThreshold)}:ratio=2:attack=20:release=250,"
                : "";
            // e=4: boost quiet speech up to 4x (+12 dB); p=0.95: normalize peaks to 95%.
            string level = o.VoiceLeveling ? "speechnorm=e=4:p=0.95," : "";
            return $"[0:a]{denoise}{gate}{level}volume={Inv(o.MicGain)}[m]";
        }

        /// <summary>Safety limiter appended after the last mix stage: stops clipping when the
        /// leveled mic and the system audio sum above full scale. level=false = no auto makeup gain.</summary>
        private const string Limiter = "alimiter=limit=0.95:level=false";

        /// <summary>Escape a Windows path for use inside an ffmpeg filtergraph option value
        /// (forward slashes; ':' is the filter option separator so it needs escaping).</summary>
        private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:");

        /// <summary>
        /// Mix a mic WAV and a system WAV into one WAV (48 kHz stereo): gate+volume the mic,
        /// volume the system, amix. normalize=0 keeps levels predictable.
        /// </summary>
        public static List<string> MixTwoWav(string micWav, string sysWav, string outWav, AudioMixOptions o)
        {
            string fc = $"{MicChain(o)};[1:a]volume={Inv(o.SystemGain)}[s];"
                      + $"[m][s]amix=inputs=2:duration=longest:normalize=0,{Limiter}[a]";
            return new()
            {
                "-y", "-i", micWav, "-i", sysWav,
                "-filter_complex", fc, "-map", "[a]", "-ar", "48000", "-ac", "2", outWav,
            };
        }

        /// <summary>
        /// Mux a video that already contains a mic track (input 0) with a separate system WAV
        /// (input 1): copy the video, mix mic+system into the audio track.
        /// </summary>
        public static List<string> MuxVideoMixMicSystem(string rawMp4, string sysWav, string outMp4, AudioMixOptions o)
        {
            string fc = $"{MicChain(o)};[1:a]volume={Inv(o.SystemGain)}[s];"
                      + $"[m][s]amix=inputs=2:duration=longest:normalize=0,{Limiter}[a]";
            return new()
            {
                "-y", "-i", rawMp4, "-i", sysWav,
                "-filter_complex", fc,
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k", outMp4,
            };
        }

        /// <summary>
        /// Re-process the mic track already inside a video (input 0) through the full mic chain
        /// plus the safety limiter: copy the video, re-encode only the audio. Used for mic-only
        /// recordings so they get the same clean-voice treatment as mixed ones.
        /// </summary>
        public static List<string> FilterVideoMic(string rawMp4, string outMp4, AudioMixOptions o)
        {
            string fc = $"{MicChain(o)};[m]{Limiter}[a]";
            return new()
            {
                "-y", "-i", rawMp4,
                "-filter_complex", fc,
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k", outMp4,
            };
        }

        /// <summary>
        /// Mux a video-only file (input 0) with a system WAV (input 1) as its sole audio track.
        /// Used when capturing system audio for video without a microphone.
        /// </summary>
        public static List<string> MuxVideoAddSystem(string rawMp4, string sysWav, string outMp4, double sysGain)
        {
            string fc = $"[1:a]volume={Inv(sysGain)}[a]";
            return new()
            {
                "-y", "-i", rawMp4, "-i", sysWav,
                "-filter_complex", fc,
                "-map", "0:v", "-c:v", "copy", "-map", "[a]", "-c:a", "aac", "-b:a", "160k", outMp4,
            };
        }

        // ---- subtitle burn-in (issue #102) --------------------------------

        /// <summary>
        /// The single default caption style (issue #102, assumption A1) applied through the libass
        /// <c>subtitles</c> filter's <c>force_style</c>. ASS style fields:
        /// Arial 24pt white text (<c>PrimaryColour=&amp;H00FFFFFF</c>, AABBGGRR = opaque white) with a
        /// solid black outline (<c>OutlineColour=&amp;H00000000</c>, <c>BorderStyle=1</c>,
        /// <c>Outline=2</c>) plus a light drop shadow (<c>Shadow=1</c>) for readability on any
        /// background, bottom-centered (<c>Alignment=2</c>) with a 30px bottom margin
        /// (<c>MarginV=30</c>). A styling UI is out of scope for this slice.
        /// </summary>
        public const string DefaultSubtitleStyle =
            "FontName=Arial,FontSize=24,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000," +
            "BorderStyle=1,Outline=2,Shadow=1,Alignment=2,MarginV=30";

        /// <summary>
        /// Burn the cues from a WebVTT subtitle file into a NEW MP4 (issue #102): re-encode the input
        /// video with the libass <c>subtitles</c> filter overlaying <paramref name="subtitlePath"/> in
        /// the documented default style (<see cref="DefaultSubtitleStyle"/>), copying the audio track
        /// unchanged. libass reads WebVTT natively (it converts to ASS internally), so the .vtt is
        /// referenced directly - no separate ASS conversion. The subtitle path is escaped for use inside
        /// a filtergraph option value (forward slashes; ':' escaped) exactly like the RNNoise model path.
        /// </summary>
        public static List<string> BurnSubtitles(string inputVideo, string subtitlePath, string outPath, int crf = 23)
        {
            string vf = $"subtitles='{FilterPath(subtitlePath)}':force_style='{DefaultSubtitleStyle}'";
            return new()
            {
                "-y", "-i", inputVideo,
                "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", crf.ToString(),
                "-c:a", "copy",
                outPath,
            };
        }

        /// <summary>Render the args list back to a copy-pasteable command line (for logs/manifests).</summary>
        public static string ToCommandLine(string exe, IReadOnlyList<string> args)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('"').Append(exe).Append('"');
            foreach (var arg in args)
            {
                sb.Append(' ');
                sb.Append(arg.Contains(' ') || arg.Contains('=') ? $"\"{arg}\"" : arg);
            }
            return sb.ToString();
        }
    }
}
